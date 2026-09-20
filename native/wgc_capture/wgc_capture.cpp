/*
 * wgc_capture.cpp — Windows Graphics Capture 抓屏 DLL 实现（纯 C ABI，见 wgc_capture.h）。
 *
 * 设计要点
 * --------
 * 1) 每个 handle 一个**专属 MTA 工作线程**：所有 WinRT/WGC 对象都只在该线程创建与使用。
 *    这样无论 C# 宿主从哪个线程调用（线程池 MTA、Avalonia UI 线程 STA、任意线程），
 *    公寓（apartment）状态都是确定且正确的，也天然把同一 handle 的并发调用串行化。
 * 2) 会话复用：hwnd 不变就复用捕获会话；hwnd 变了才重建。
 * 3) **陈旧帧判定用帧时间戳**，不再「盲丢前 2 帧」：Direct3D11CaptureFrame::SystemRelativeTime()
 *    与本地 QPC 同基准，因此「时间戳 < 会话起点（StartCapture 之后取的 QPC 锚）」即该帧在
 *    会话开始前就已合成。这样的帧不会作为可信帧返回（B1）。盲丢的害处是：会话刚建立而窗口
 *    静止（只出 1 帧）时，唯一一帧会被丢掉、再被当成成功帧返回——返回自己判定为陈旧的帧。
 * 4) **静止内容友好**：内容静止时 WGC 不再产出新帧，此时用「跨会话缓存」（上一次成功返回给
 *    调用方的已验证帧）兜底，而不是报 TIMEOUT。缓存只在 hwnd 变化时失效；同一个 hwnd
 *    重建会话（最小化还原 / NOT_CAPTURABLE 重试）时保留。
 * 5) 全程 RAII（winrt::com_ptr / 自定义 guard），异常安全，无泄漏。
 * 6) 超时保护：单次抓帧最多等 2 秒（已有可兜底帧时约 0.4 秒），绝不死等。
 *
 * 参考实现：.review_pr/wgc_probe.cpp（已实测通过）。
 */

// 必须在包含公开头文件之前定义：头文件据此把 WGC_API 展开为 __declspec(dllexport)。
#ifndef WGC_CAPTURE_BUILD
#define WGC_CAPTURE_BUILD
#endif

#include "wgc_capture.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwmapi.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <Windows.Graphics.Capture.Interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

namespace {

/* ------------------------------------------------------------------ */
/* 常量                                                                */
/* ------------------------------------------------------------------ */

// 单次 Wgc_CaptureFrame 的总时间预算（毫秒）。超时即返回 WGC_ERR_TIMEOUT，不死等。
constexpr DWORD kWgcTimeoutMs = 2000;

// 轮询间隔：帧池为空时小睡，避免空转烧 CPU。
constexpr DWORD kPollSleepMs = 4;

// 手上已有可兜底的帧（跨会话缓存）时，单次抓帧只为「等到更新的帧」等这么久。
// 内容静止（暂停态）时 WGC 不再产出新帧，用短等待换响应速度；内容在动时一帧 16ms 内就到。
constexpr DWORD kCachedProbeMs = 400;

/* ------------------------------------------------------------------ */
/* 小工具                                                              */
/* ------------------------------------------------------------------ */

std::string Hex(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof(b), "0x%08X", static_cast<unsigned>(v));
    return b;
}

/* 诊断开关（默认关闭）：设 WGC_TRACE=1 时，把「建会话各阶段耗时」与「取帧等待/来源」打到
 * stderr。用途只有一个——回答「自测里那些墙钟断言，量到的到底是本 DLL 的开销，还是 WGC/DWM
 * 出帧的环境抖动」。不设该环境变量时零开销、零输出。 */
bool TraceOn() {
    static const bool on = (::GetEnvironmentVariableA("WGC_TRACE", nullptr, 0) > 0);
    return on;
}
void Trace(const char* fmt, ...) {
    if (!TraceOn()) return;
    va_list ap;
    va_start(ap, fmt);
    std::vfprintf(stderr, fmt, ap);
    va_end(ap);
    std::fflush(stderr);
}

// 当前时刻，换算成 100ns 单位——与 Direct3D11CaptureFrame::SystemRelativeTime() 同一时间基准
// （实测：会话起点读数与首帧时间戳之差恒为正的十几毫秒，即帧时间戳 >= 会话起点）。
// 拿不到 QPC 频率时返回 0：此时任何时间戳都 >= 0，等价于「不判定陈旧」的安全退化。
int64_t QpcNow100ns() {
    LARGE_INTEGER freq{}, now{};
    if (!::QueryPerformanceFrequency(&freq) || freq.QuadPart <= 0) return 0;
    if (!::QueryPerformanceCounter(&now)) return 0;
    return now.QuadPart * 10000000LL / freq.QuadPart;
}

std::string FromHresult(winrt::hresult_error const& e) {
    return "WinRT 异常 " + Hex(static_cast<uint32_t>(e.code())) + ": " +
           winrt::to_string(e.message());
}

// 作用域退出时执行一次动作（用于保证 Unmap / Close 一定发生）。
struct ScopeExit {
    std::function<void()> fn;
    explicit ScopeExit(std::function<void()> f) : fn(std::move(f)) {}
    ~ScopeExit() { if (fn) fn(); }
    ScopeExit(const ScopeExit&) = delete;
    ScopeExit& operator=(const ScopeExit&) = delete;
};

/* ------------------------------------------------------------------ */
/* 已销毁窗口的会话：挂起而不 teardown（见 CloseSession 注释）           */
/* ------------------------------------------------------------------ */

// 进程级「待回收表」：存放**目标窗口已被销毁**的捕获会话/帧池，之后不再触碰。
//
// 为什么需要它（实测，自测 [7] 约 2% 概率复现）：
//   会话的目标窗口被宿主 DestroyWindow 之后，GraphicsCaptureSession 的 Close() 与
//   「最后一次 Release()」都会进入捕获服务对「窗口已消失」的清理路径，**永久阻塞**
//   （实测 ≥150s 不返回）。而 Worker::Invoke 是同步等待 ⇒ Wgc_CaptureFrame 永不返回、
//   宿主界面直接冻死，违反头文件「不会死等」的承诺。
//   因此这条路径必须**不被触发**：既不 Close() 也不 Release()，把对象挂在这里。
//
// 表本身用 new 分配、永不 delete：否则 DLL 卸载时的静态析构又会去跑同一段 teardown。
// 代价：每次「窗口在会话存活期间被销毁」会滞留一份会话/帧池代理对象（窗口已不存在，
// 它不再产出任何内容，也不会再被访问）。
struct Graveyard {
    std::mutex m;
    std::vector<winrt::Windows::Graphics::Capture::GraphicsCaptureSession> sessions;
    std::vector<winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool> pools;

    static Graveyard& Get() {
        static Graveyard* g = new Graveyard();  // 故意泄漏，见上
        return *g;
    }
};

/* ------------------------------------------------------------------ */
/* MTA 工作线程                                                        */
/* ------------------------------------------------------------------ */

// 一个 handle 一个：所有任务在同一个 MTA 线程上串行执行，同步返回。
class Worker {
public:
    Worker() : thread_([this] { Loop(); }) {}

    ~Worker() {
        {
            std::lock_guard<std::mutex> lk(m_);
            quit_ = true;
        }
        cv_.notify_all();
        if (thread_.joinable()) thread_.join();
    }

    Worker(const Worker&) = delete;
    Worker& operator=(const Worker&) = delete;

    // 在 worker 线程上同步执行 fn（本调用会阻塞直到 fn 执行完毕）。
    //
    // 并发安全（B4）：job_/done_ 是**单槽**握手机制，只支持「一个在飞任务」。
    // 若两个线程同时进来，后者的 job_ 会覆盖前者、前者永远等不到自己的完成信号
    // （任务被丢弃却返回 INTERNAL），因此这里用 invokeMutex_ 把调用方**串行化**：
    // 第二个调用方阻塞到第一个任务执行完毕，与头文件承诺一致。
    // 注意：串行化只保护 Worker 内部状态；调用方仍不得与 Wgc_Destroy 并发
    // （handle 失效后不得再使用，见头文件）。
    void Invoke(std::function<void()> fn) {
        std::lock_guard<std::mutex> serialize(invokeMutex_);
        std::unique_lock<std::mutex> lk(m_);
        job_ = std::move(fn);
        hasJob_ = true;
        done_ = false;
        cv_.notify_all();
        cv_.wait(lk, [this] { return done_ || quit_; });
        job_ = nullptr;
    }

private:
    void Loop() {
        // MTA 即可：WGC 不需要 STA / UI 线程 / 消息泵（见 docs/26 §八）。
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
        } catch (...) {
            // 线程已被初始化成别的公寓模式：继续用当前公寓，不致命。
        }

        for (;;) {
            std::function<void()> job;
            {
                std::unique_lock<std::mutex> lk(m_);
                cv_.wait(lk, [this] { return hasJob_ || quit_; });
                if (quit_) break;
                job = job_;
                hasJob_ = false;
            }

            // 任务本身已捕获所有异常；这里再兜一层，避免异常逃逸导致 std::terminate。
            try { job(); } catch (...) {}

            {
                std::lock_guard<std::mutex> lk(m_);
                done_ = true;
            }
            cv_.notify_all();
        }

        try { winrt::uninit_apartment(); } catch (...) {}
    }

    std::thread thread_;
    std::mutex invokeMutex_;  // 串行化 Invoke 调用方（见 Invoke 注释）
    std::mutex m_;
    std::condition_variable cv_;
    std::function<void()> job_;
    bool hasJob_ = false;
    bool done_ = false;
    bool quit_ = false;
};

/* ------------------------------------------------------------------ */
/* 捕获器                                                              */
/* ------------------------------------------------------------------ */

struct WgcCapture {
    Worker worker;

    // 错误信息：worker 线程写、调用方线程读 ⇒ 单独加锁。
    std::mutex errMutex;
    std::string lastError;

    void SetError(std::string s) {
        std::lock_guard<std::mutex> lk(errMutex);
        lastError = std::move(s);
    }
    std::string GetError() {
        std::lock_guard<std::mutex> lk(errMutex);
        return lastError;
    }

    // ---- 以下成员仅在 worker 线程上访问 ----
    HWND hwnd = nullptr;        // 当前会话对应的调用方 hwnd（会话关闭即置空）
    HWND cacheHwnd = nullptr;   // 跨会话缓存归属的 hwnd；变了才丢弃缓存（见 CloseSession）
    HWND captureHwnd = nullptr; // 实际被 WGC 捕获的窗口（子窗口时 = 顶层祖先）
    bool cropping = false;      // true = 捕获的是顶层祖先，需要裁剪到子窗口客户区

    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool{nullptr};
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session{nullptr};

    int64_t sessionAnchor100ns = 0;  // 会话起点（QPC 换算 100ns），用于判定帧是否「会话前合成」
    bool clockUsable = false;        // 是否已确认帧时间戳与本地 QPC 同基准（见 FrameIsFresh）

    // 最近一次**成功返回给调用方**的帧（跨调用缓存）。窗口内容静止时 WGC 不再产出新帧，
    // 超时后用它兜底——内容本来就没变，这一帧就是正确内容。
    // 只在会话**换到另一个 hwnd** 时失效；同一个 hwnd 重建会话时保留（见 EnsureSession）。
    bool hasCache = false;
    std::vector<unsigned char> cache;
    int cacheW = 0, cacheH = 0, cacheStride = 0;

    winrt::com_ptr<ID3D11Device> d3dDevice;         // 每个 handle 只建一次，跨会话复用
    winrt::com_ptr<ID3D11DeviceContext> d3dContext;
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice rtDevice{nullptr};

    winrt::com_ptr<ID3D11Texture2D> staging;  // CPU 读回用的 staging 纹理
    UINT stagingW = 0, stagingH = 0;
    DXGI_FORMAT stagingFmt = DXGI_FORMAT_UNKNOWN;

    // 关闭捕获会话（幂等）。keepCache=true 时保留跨会话缓存（同一个 hwnd 重建会话的情形）。
    void CloseSession(bool keepCache = false) {
        // 会话的目标窗口可能已被宿主销毁。hwnd / captureHwnd 是**本会话**建立时写入的，
        // 此处仍指向该会话，因此可以据此判定目标是否还活着。
        const bool targetAlive =
            (hwnd && ::IsWindow(hwnd)) || (captureHwnd && ::IsWindow(captureHwnd));

        if (!targetAlive) {
            // ⚠ 目标窗口已消失 ⇒ 不做任何 teardown（Close()/Release() 都会死锁，见 Graveyard）。
            // 先把目标字段清掉：万一下面入表失败（极端内存不足），再次进来仍走这条安全分支，
            // 而不会去 Close() 一个目标已死的会话。
            hwnd = nullptr;
            captureHwnd = nullptr;
            try {
                auto& g = Graveyard::Get();
                std::lock_guard<std::mutex> lk(g.m);
                if (session) g.sessions.push_back(session);
                if (pool) g.pools.push_back(pool);
                session = nullptr;  // 入表已持有一份引用 ⇒ 这里不是最后一次 Release
                pool = nullptr;
            } catch (...) {
                // 入表失败：宁可把引用留在 handle 上（不释放、不 teardown），也绝不冒死锁风险。
            }
        } else {
            if (session) { try { session.Close(); } catch (...) {} session = nullptr; }
            if (pool) { try { pool.Close(); } catch (...) {} pool = nullptr; }
        }

        item = nullptr;
        hwnd = nullptr;
        captureHwnd = nullptr;
        cropping = false;
        sessionAnchor100ns = 0;
        clockUsable = false;
        if (!keepCache) {
            hasCache = false;  // 缓存归属该 hwnd；换了 hwnd 即失效
            cache.clear();
            cacheW = cacheH = cacheStride = 0;
            cacheHwnd = nullptr;
        }
        staging = nullptr;
        stagingW = stagingH = 0;
        stagingFmt = DXGI_FORMAT_UNKNOWN;
    }
};

/* ------------------------------------------------------------------ */
/* 会话建立                                                            */
/* ------------------------------------------------------------------ */

// 惰性创建 D3D11 设备 + WinRT 包装设备（每个 handle 一次，跨会话复用）。
int EnsureDevice(WgcCapture& h, std::string& err) {
    if (h.rtDevice && h.d3dDevice) return WGC_OK;

    if (!h.d3dDevice) {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        // 与探针保持一致：不请求 11_1（无 11.1 运行时的老系统会 E_INVALIDARG）。
        D3D_FEATURE_LEVEL levels[] = {
            D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        D3D_FEATURE_LEVEL got{};
        const HRESULT hr = ::D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            h.d3dDevice.put(), &got, h.d3dContext.put());
        if (FAILED(hr)) {
            err = "D3D11CreateDevice 失败 " + Hex(static_cast<uint32_t>(hr));
            return WGC_ERR_CREATE_FAILED;
        }
    }

    if (!h.rtDevice) {
        try {
            winrt::com_ptr<IDXGIDevice> dxgiDevice;
            winrt::check_hresult(h.d3dDevice->QueryInterface(IID_PPV_ARGS(dxgiDevice.put())));

            // 诊断：把实际选中的适配器打出来。多 GPU（含虚拟显示适配器）机器上，
            // D3D_DRIVER_TYPE_HARDWARE 选的是「默认适配器」，未必与目标窗口所在显示器同卡，
            // 跨适配器捕获会引入额外延迟/抖动 —— 排查环境相关 flaky 时这是关键信息。
            try {
                winrt::com_ptr<IDXGIAdapter> ad;
                if (SUCCEEDED(dxgiDevice->GetAdapter(ad.put())) && ad) {
                    DXGI_ADAPTER_DESC d{};
                    if (SUCCEEDED(ad->GetDesc(&d))) {
                        Trace("[wgc] 设备适配器: %ls (LUID %08X:%08X, 显存 %llu MB)\n",
                              d.Description, static_cast<unsigned>(d.AdapterLuid.HighPart),
                              static_cast<unsigned>(d.AdapterLuid.LowPart),
                              static_cast<unsigned long long>(d.DedicatedVideoMemory / (1024 * 1024)));
                    }
                }
            } catch (...) {
            }

            winrt::com_ptr<::IInspectable> inspectable;
            winrt::check_hresult(
                ::CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()));
            h.rtDevice =
                inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
        } catch (winrt::hresult_error const& e) {
            err = "包装 WinRT D3D 设备失败: " + FromHresult(e);
            return WGC_ERR_CREATE_FAILED;  // 仍属「创建捕获器」阶段
        }
    }
    return WGC_OK;
}

// 保证存在一个与 hwnd 匹配的捕获会话；hwnd 未变则直接复用。
int EnsureSession(WgcCapture& h, HWND hwnd, std::string& err) {
    if (h.session && h.hwnd == hwnd) return WGC_OK;

    // 同一个 hwnd 重建会话（最小化还原 / 上次 NOT_CAPTURABLE 后重试）时**保留跨会话缓存**：
    // 它是上一次真正返回给调用方的、已验证的帧。丢了它，静止窗口就只能靠「本次调用内
    // 尚未验证的帧」兜底（B1 的成因），或者干脆 TIMEOUT。
    const bool sameHwnd = (h.cacheHwnd == hwnd);
    const DWORD tBegin = ::GetTickCount();
    h.CloseSession(sameHwnd);
    const DWORD tClosed = ::GetTickCount();

    if (hwnd == nullptr || !::IsWindow(hwnd)) {
        err = "hwnd 不是有效窗口";
        return WGC_ERR_NOT_CAPTURABLE;
    }

    // ⚠ 实测：WGC 的 IGraphicsCaptureItemInterop::CreateForWindow **不接受子窗口**
    //   （WS_CHILD 会直接失败：E_INVALIDARG "Could not capture the given window"）。
    //   而 3FCompare 的视频画面正是父窗口下的子 HWND ⇒ 这里自动退化为
    //   「捕获顶层祖先 + 裁剪到子窗口客户区」。顶层窗口则保持原样（裁剪=全幅）。
    HWND root = ::GetAncestor(hwnd, GA_ROOT);
    if (root == nullptr) root = hwnd;
    const bool cropping = (root != hwnd);

    // 最小化时 WGC 只会给黑帧，直接明确报错（IsIconic 对子窗口恒为 FALSE，安全）。
    if (::IsIconic(root)) {
        err = "窗口已最小化，WGC 无法取到有效内容";
        return WGC_ERR_NOT_CAPTURABLE;
    }

    {
        int rc = EnsureDevice(h, err);
        if (rc != WGC_OK) return rc;
    }

    using winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool;
    using winrt::Windows::Graphics::Capture::GraphicsCaptureItem;

    // 阶段 2：创建捕获器 / 帧池 / 会话。任一失败都归入 WGC_ERR_CREATE_FAILED(2)，
    // 与头文件的错误码表一致（此前这些 check_hresult 会一路冒泡成 INTERNAL(7)）。
    try {
        // C++/WinRT 没有 GraphicsCaptureItem::CreateFromWindow 投影，必须走 interop。
        auto interop =
            winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{nullptr};
        winrt::check_hresult(interop->CreateForWindow(
            root, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)));
        if (!item) {
            err = "CreateForWindow 返回空 item";
            return WGC_ERR_CREATE_FAILED;
        }
        const DWORD tItem = ::GetTickCount();

        const auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0) {
            err = "窗口客户区尺寸为 0（" + std::to_string(size.Width) + "x" +
                  std::to_string(size.Height) + "）";
            return WGC_ERR_NOT_CAPTURABLE;
        }

        auto pool = Direct3D11CaptureFramePool::Create(
            h.rtDevice,
            winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, size);
        auto session = pool.CreateCaptureSession(item);

        session.IsCursorCaptureEnabled(false);
        // 关掉系统黄框；旧系统（< Win11 / build 20348）没有该属性会抛异常，忽略即可。
        try {
            session.IsBorderRequired(false);
        } catch (winrt::hresult_error const&) {
            // 无法关闭黄框：不影响抓帧正确性。
        }

        session.StartCapture();
        const DWORD tStarted = ::GetTickCount();

        // 会话起点锚：紧跟在 StartCapture 之后取。早于此刻合成的帧一律视为「会话前合成」，
        // 不作为可信帧返回（见 GrabFrame）。
        h.sessionAnchor100ns = QpcNow100ns();
        h.clockUsable = false;

        h.item = item;
        h.pool = pool;
        h.session = session;
        h.hwnd = hwnd;
        h.cacheHwnd = hwnd;  // 缓存（若有）归属这个 hwnd
        h.captureHwnd = root;
        h.cropping = cropping;
        Trace("[wgc] EnsureSession hwnd=%p 关旧会话=%lums CreateForWindow=%lums 建池建会话=%lums "
              "StartCapture=%lums 合计=%lums\n",
              hwnd, tClosed - tBegin, tItem - tClosed, tStarted - tItem,
              ::GetTickCount() - tStarted, ::GetTickCount() - tBegin);
        return WGC_OK;
    } catch (winrt::hresult_error const& e) {
        err = "创建捕获会话失败: " + FromHresult(e);
        h.CloseSession(sameHwnd);  // 半成品会话不保留
        return WGC_ERR_CREATE_FAILED;
    } catch (std::exception const& e) {
        err = std::string("创建捕获会话失败（C++ 异常）: ") + e.what();
        h.CloseSession(sameHwnd);
        return WGC_ERR_CREATE_FAILED;
    }
}

// 计算在「捕获内容坐标系」中的裁剪矩形。
// ⚠ 实测结论：WGC 窗口捕获的内容区域 = DWM 的**扩展帧边界**（DWMWA_EXTENDED_FRAME_BOUNDS），
//   不是客户区、也不是 GetWindowRect 的窗口矩形。
//   实测数据：父窗口 窗口矩形 700x520 / 客户区 678x464 / WGC 抓回 **682x511**
//   ⇒ 682x511 正是扩展帧边界（标准窗口左右各内缩 9px 不可见缩放边框、底边内缩 9px，顶边不缩）。
//   因此这里用扩展帧边界的左上角作为内容原点，把子窗口客户区原点映射到屏幕后相减即可。
bool ComputeCropRect(const WgcCapture& h, UINT texW, UINT texH, RECT& out) {
    out = RECT{0, 0, static_cast<LONG>(texW), static_cast<LONG>(texH)};
    if (!h.cropping) return true;

    POINT tl{0, 0};  // 子窗口客户区原点 → 屏幕坐标
    if (!::ClientToScreen(h.hwnd, &tl)) return false;

    // 内容原点 = 扩展帧边界左上角；取不到时退回窗口矩形。
    RECT frame{};
    if (FAILED(::DwmGetWindowAttribute(h.captureHwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame,
                                       sizeof(frame))) ||
        (frame.right <= frame.left) || (frame.bottom <= frame.top)) {
        if (!::GetWindowRect(h.captureHwnd, &frame)) return false;
    }

    RECT cr{};
    if (!::GetClientRect(h.hwnd, &cr)) return false;

    LONG x0 = tl.x - frame.left;
    LONG y0 = tl.y - frame.top;
    LONG x1 = x0 + (cr.right - cr.left);
    LONG y1 = y0 + (cr.bottom - cr.top);

    // 钳制到捕获面范围内（子窗口可能被父窗口裁掉一部分）。
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > static_cast<LONG>(texW)) x1 = static_cast<LONG>(texW);
    if (y1 > static_cast<LONG>(texH)) y1 = static_cast<LONG>(texH);

    out = RECT{x0, y0, x1, y1};
    return (x1 > x0) && (y1 > y0);
}

/* ------------------------------------------------------------------ */
/* 抓一帧                                                              */
/* ------------------------------------------------------------------ */

// 把一帧捕获成「紧凑 top-down BGRA」输出缓冲，并更新跨调用兜底缓存。
// updateCache=false 时不刷新缓存（用于「会话前合成」的 best-effort 兜底帧：它未经证实，
// 不能污染后续调用要复用的已验证帧）。
// 成功返回 WGC_OK 并填好 out*；失败返回错误码（不分配输出缓冲）。
int EmitFrame(WgcCapture& h,
              winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame const& frame,
              unsigned char** outBits, int* outWidth, int* outHeight, int* outStride,
              std::string& err, bool updateCache = true) {
    // 注意：IDirect3DDxgiInterfaceAccess 是 interop 头里的**全局命名空间**类型
    // （::Windows::Graphics::DirectX::Direct3D11::...），不在 winrt:: 下。
    using ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

    // 用 guard 保证任何退出路径都 Close 掉 frame（把缓冲还给帧池）。
    bool frameClosed = false;
    ScopeExit frameGuard([&] {
        if (!frameClosed) { try { frame.Close(); } catch (...) {} }
    });

    // 阶段 3：从捕获帧取 D3D11 纹理。失败归入 WGC_ERR_TEXTURE_READ(5)。
    winrt::com_ptr<ID3D11Texture2D> tex;
    D3D11_TEXTURE2D_DESC desc{};
    try {
        auto access = frame.Surface().as<IDirect3DDxgiInterfaceAccess>();
        winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), tex.put_void()));
        tex->GetDesc(&desc);
    } catch (winrt::hresult_error const& e) {
        err = "取捕获纹理失败: " + FromHresult(e);
        return WGC_ERR_TEXTURE_READ;
    }
    if (desc.Width == 0 || desc.Height == 0) {
        err = "捕获纹理尺寸为 0";
        return WGC_ERR_NOT_CAPTURABLE;
    }

    // staging 纹理（尺寸/格式变化时重建）+ CopyResource + Map：同属 CPU 读回阶段。
    D3D11_MAPPED_SUBRESOURCE ms{};
    try {
        if (!h.staging || h.stagingW != desc.Width || h.stagingH != desc.Height ||
            h.stagingFmt != desc.Format) {
            h.staging = nullptr;
            D3D11_TEXTURE2D_DESC s = desc;
            s.ArraySize = 1;
            s.MipLevels = 1;
            s.Usage = D3D11_USAGE_STAGING;
            s.BindFlags = 0;
            s.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            s.MiscFlags = 0;
            winrt::check_hresult(h.d3dDevice->CreateTexture2D(&s, nullptr, h.staging.put()));
            h.stagingW = desc.Width;
            h.stagingH = desc.Height;
            h.stagingFmt = desc.Format;
        }

        h.d3dContext->CopyResource(h.staging.get(), tex.get());
        winrt::check_hresult(h.d3dContext->Map(h.staging.get(), 0, D3D11_MAP_READ, 0, &ms));
    } catch (winrt::hresult_error const& e) {
        err = "CPU 读回捕获纹理失败: " + FromHresult(e);
        h.staging = nullptr;
        h.stagingW = h.stagingH = 0;
        h.stagingFmt = DXGI_FORMAT_UNKNOWN;
        return WGC_ERR_TEXTURE_READ;
    }
    bool mapped = true;
    ScopeExit mapGuard([&] {
        if (mapped) { try { h.d3dContext->Unmap(h.staging.get(), 0); } catch (...) {} }
    });

    // 子窗口场景：裁剪到子窗口客户区（坐标已在捕获面坐标系里）。顶层窗口时裁剪=全幅。
    RECT crop{};
    if (!ComputeCropRect(h, desc.Width, desc.Height, crop)) {
        err = "子窗口客户区不在顶层窗口可见范围内（裁剪结果为空）";
        return WGC_ERR_NOT_CAPTURABLE;
    }
    const int cw = static_cast<int>(crop.right - crop.left);
    const int ch = static_cast<int>(crop.bottom - crop.top);
    const size_t bytes = static_cast<size_t>(cw) * 4 * static_cast<size_t>(ch);

    // 输出缓冲：调用方用 Wgc_FreeFrame 释放（底层 CoTaskMemFree），跨 ABI 分配器最安全。
    auto* buf = static_cast<unsigned char*>(::CoTaskMemAlloc(bytes));
    if (!buf) {
        err = "CoTaskMemAlloc 分配 " + std::to_string(bytes) + " 字节失败";
        return WGC_ERR_INTERNAL;
    }

    // 逐行拷贝成紧凑 top-down 布局（源行距 RowPitch 可能大于 w*4）。
    for (int y = 0; y < ch; ++y) {
        std::memcpy(buf + static_cast<size_t>(y) * cw * 4,
                    static_cast<const unsigned char*>(ms.pData) +
                        static_cast<size_t>(crop.top + y) * ms.RowPitch +
                        static_cast<size_t>(crop.left) * 4,
                    static_cast<size_t>(cw) * 4);
    }

    h.d3dContext->Unmap(h.staging.get(), 0);
    mapped = false;
    frame.Close();
    frameClosed = true;

    // 更新兜底缓存：下次若 WGC 不再产出新帧（内容静止），直接把这一帧再给一份。
    // 只有「会话起点之后合成」的可信帧才允许写入缓存。
    if (updateCache) {
        try {
            h.cache.assign(buf, buf + bytes);
            h.cacheW = cw;
            h.cacheH = ch;
            h.cacheStride = cw * 4;
            h.cacheHwnd = h.hwnd;
            h.hasCache = true;
        } catch (...) {
            h.hasCache = false;  // 缓存失败不影响本次结果
        }
    }

    *outBits = buf;
    *outWidth = cw;
    *outHeight = ch;
    *outStride = cw * 4;
    return WGC_OK;
}

// 超时降级：把上一次成功抓到的帧原样再给一份（内容静止时它就是当前内容）。
int EmitCached(WgcCapture& h, unsigned char** outBits, int* outWidth, int* outHeight,
               int* outStride, std::string& err) {
    const size_t bytes = h.cache.size();
    auto* buf = static_cast<unsigned char*>(::CoTaskMemAlloc(bytes));
    if (!buf) {
        err = "CoTaskMemAlloc 分配 " + std::to_string(bytes) + " 字节失败（兜底帧）";
        return WGC_ERR_INTERNAL;
    }
    std::memcpy(buf, h.cache.data(), bytes);
    *outBits = buf;
    *outWidth = h.cacheW;
    *outHeight = h.cacheH;
    *outStride = h.cacheStride;
    return WGC_OK;
}

// 判定一帧是否为「会话起点之后合成」的可信帧。
//
// 为什么不用「盲丢前 2 帧」：那样会在会话刚建立、窗口静止（只出 1 帧）时把唯一一帧
// 当成可疑帧丢弃，再把它当成功帧返回并写进缓存 —— 即 B1：返回自己判定为陈旧的帧。
// 改为用帧自带的时间戳判定：Direct3D11CaptureFrame::SystemRelativeTime() 与本地 QPC
// 同一时间基准（实测两者之差恒为正值），因此「时间戳 < 会话起点」= 该帧在 StartCapture
// 之前就已合成，才是真正的陈旧帧。
//
// 安全网：只有在确认过「帧时间戳 >= 会话起点」之后才启用该判定（clockUsable）。
// 万一某系统的基准不同（恒小于起点），clockUsable 永不置位，所有帧都按可信处理，
// 退化为「不判定陈旧」，绝不会因为判定失效而全部拒绝、把静止窗口逼成 TIMEOUT。
bool FrameIsFresh(WgcCapture& h, int64_t frameTime100ns) {
    if (!h.clockUsable) {
        if (frameTime100ns >= h.sessionAnchor100ns) h.clockUsable = true;
        return true;  // 尚未确认时间基准：不判定陈旧
    }
    return frameTime100ns >= h.sessionAnchor100ns;
}

int GrabFrame(WgcCapture& h, unsigned char** outBits, int* outWidth, int* outHeight,
              int* outStride, std::string& err) {
    using winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame;

    const DWORD t0 = ::GetTickCount();
    // 已有可兜底的缓存帧 ⇒ 只为「等到更新的帧」短暂等待（暂停态下连续抓帧要快）；
    // 否则用完整预算（会话刚建立，WGC 需要时间出帧）。
    const DWORD budget = h.hasCache ? kCachedProbeMs : kWgcTimeoutMs;

    int polled = 0;
    int staleSkipped = 0;
    // 「会话起点之前合成」的帧：保留未关闭，仅作最后兜底（best-effort），绝不写进缓存。
    Direct3D11CaptureFrame staleFrame{nullptr};
    ScopeExit staleGuard([&] {
        if (staleFrame) { try { staleFrame.Close(); } catch (...) {} }
    });

    while (::GetTickCount() - t0 < budget) {
        if (::IsIconic(h.captureHwnd)) {
            err = "抓帧期间窗口被最小化";
            return WGC_ERR_NOT_CAPTURABLE;
        }

        auto frame = h.pool.TryGetNextFrame();  // 轮询即可，不必用 FrameArrived 事件
        if (!frame) {
            ::Sleep(kPollSleepMs);
            continue;
        }
        ++polled;

        // 诊断：本会话**首个**帧距会话起点的时间差。负数 = 该帧在 StartCapture 之前就已合成
        // （即「会话前合成帧」）。此时 clockUsable 仍为 false，FrameIsFresh 会无条件放行 ——
        // 这条埋点就是用来量「首帧绕过陈旧判定」的实际发生频率与幅度。
        if (!h.clockUsable) {
            Trace("[wgc] 首帧 hwnd=%p ts-anchor=%+lld(100ns) polled=%d\n", h.hwnd,
                  static_cast<long long>(frame.SystemRelativeTime().count() -
                                         h.sessionAnchor100ns),
                  polled);
        }

        // 只拒绝「会话起点之前合成」的帧；会话起点之后的帧当场返回（静止窗口无需空等）。
        if (!FrameIsFresh(h, frame.SystemRelativeTime().count())) {
            ++staleSkipped;
            if (staleFrame) { try { staleFrame.Close(); } catch (...) {} }
            staleFrame = frame;  // 留作兜底候选，先不关闭
            continue;
        }

        Trace("[wgc] GrabFrame hwnd=%p 池内新帧 waited=%lums polled=%d stale=%d\n", h.hwnd,
              ::GetTickCount() - t0, polled, staleSkipped);
        return EmitFrame(h, frame, outBits, outWidth, outHeight, outStride, err);
    }
    if (::IsIconic(h.captureHwnd)) {
        err = "抓帧期间窗口被最小化";
        return WGC_ERR_NOT_CAPTURABLE;
    }

    // 降级 1：跨会话缓存 —— 上一次**成功返回给调用方**的已验证帧（同 hwnd 重建会话时保留）。
    // 内容静止时它就是当前内容；优先于未经验证的 staleFrame。
    if (h.hasCache) {
        Trace("[wgc] GrabFrame hwnd=%p 兜底=缓存 waited=%lums polled=%d stale=%d\n", h.hwnd,
              ::GetTickCount() - t0, polled, staleSkipped);
        return EmitCached(h, outBits, outWidth, outHeight, outStride, err);
    }
    // 降级 2：本次只拿到「会话起点之前合成」的帧，且没有任何已验证帧 ⇒ 它仍是最佳可得证据，
    // 返回但不写缓存（避免把未经证实的像素固化成后续调用的可信兜底）。
    if (staleFrame) {
        Trace("[wgc] GrabFrame hwnd=%p 兜底=会话前帧 waited=%lums polled=%d stale=%d\n", h.hwnd,
              ::GetTickCount() - t0, polled, staleSkipped);
        const int rc =
            EmitFrame(h, staleFrame, outBits, outWidth, outHeight, outStride, err, false);
        staleFrame = nullptr;  // EmitFrame 内已 Close
        return rc;
    }

    Trace("[wgc] GrabFrame hwnd=%p 超时 waited=%lums polled=%d stale=%d\n", h.hwnd,
          ::GetTickCount() - t0, polled, staleSkipped);
    err = "等待帧超时（" + std::to_string(kWgcTimeoutMs) + " ms）：已轮询 " +
          std::to_string(polled) + " 帧（其中会话前合成 " + std::to_string(staleSkipped) +
          " 帧），且此前从未成功抓到过帧。窗口内容长时间无变化（如播放暂停）时 WGC 可能不产出新帧。";
    return WGC_ERR_TIMEOUT;
}

int CaptureImpl(WgcCapture& h, HWND hwnd, unsigned char** outBits, int* outWidth,
                int* outHeight, int* outStride, std::string& err) {
    int rc = EnsureSession(h, hwnd, err);
    if (rc != WGC_OK) {
        if (rc == WGC_ERR_NOT_CAPTURABLE) h.CloseSession(/*keepCache=*/true);
        return rc;
    }

    rc = GrabFrame(h, outBits, outWidth, outHeight, outStride, err);
    // 不可捕获（最小化等）时关会话，但**保留同一个 hwnd 的缓存**：还原后静止窗口直接可用。
    if (rc == WGC_ERR_NOT_CAPTURABLE) h.CloseSession(/*keepCache=*/true);
    // 超时不销毁会话：内容可能只是暂时静止，重建反而要重新热身。
    return rc;
}

}  // namespace

/* ------------------------------------------------------------------ */
/* 导出实现                                                            */
/* ------------------------------------------------------------------ */

extern "C" {

WGC_API int WGC_CALL Wgc_IsSupported(void) {
    // 缓存：支持与否在进程生命周期内不会变，避免重复起线程。
    static std::atomic<int> cached{-1};
    const int hit = cached.load(std::memory_order_relaxed);
    if (hit >= 0) return hit;

    // 在一个**临时 MTA 工作线程**上查询，而不是在调用方线程上做 CoInitializeEx/CoUninitialize。
    // 原因：C++/WinRT 的激活工厂缓存是线程内（thread_local）的，调用方线程上
    // CoUninitialize 会让该缓存失效，之后再触发 WinRT 激活就会崩。
    // 用临时线程既不动调用方的公寓状态，也保证 MTA 语义一致。
    int result = 0;
    try {
        Worker w;
        w.Invoke([&result] {
            try {
                result =
                    winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported() ? 1 : 0;
            } catch (...) {
                result = 0;
            }
        });
    } catch (...) {
        result = 0;
    }
    cached.store(result, std::memory_order_relaxed);
    return result;
}

WGC_API void* WGC_CALL Wgc_Create(void) {
    try {
        return new WgcCapture();  // 构造时会拉起 MTA 工作线程
    } catch (...) {
        return nullptr;
    }
}

WGC_API void WGC_CALL Wgc_Destroy(void* handle) {
    if (!handle) return;
    auto* h = static_cast<WgcCapture*>(handle);
    h->worker.Invoke([h] { h->CloseSession(); });  // 在 worker 线程上释放 WinRT 对象
    delete h;                                      // 析构里 join 工作线程
}

WGC_API int WGC_CALL Wgc_CaptureFrame(void* handle, void* hwnd, unsigned char** outBits,
                                      int* outWidth, int* outHeight, int* outStride) {
    if (outBits) *outBits = nullptr;
    if (outWidth) *outWidth = 0;
    if (outHeight) *outHeight = 0;
    if (outStride) *outStride = 0;

    if (!handle || !outBits || !outWidth || !outHeight || !outStride) return WGC_ERR_INVALID_ARG;
    auto* h = static_cast<WgcCapture*>(handle);
    if (!hwnd) {
        h->SetError("hwnd 为 NULL");
        return WGC_ERR_INVALID_ARG;
    }

    int rc = WGC_ERR_INTERNAL;
    std::string err;
    const HWND wnd = reinterpret_cast<HWND>(hwnd);

    // 所有 WGC 操作都放到该 handle 的 MTA 工作线程上执行，与调用方公寓无关。
    h->worker.Invoke([&] {
        try {
            rc = CaptureImpl(*h, wnd, outBits, outWidth, outHeight, outStride, err);
        } catch (winrt::hresult_error const& e) {
            err = FromHresult(e);
            rc = WGC_ERR_INTERNAL;
        } catch (std::exception const& e) {
            err = std::string("C++ 异常: ") + e.what();
            rc = WGC_ERR_INTERNAL;
        } catch (...) {
            err = "未知异常";
            rc = WGC_ERR_INTERNAL;
        }
    });

    h->SetError(rc == WGC_OK ? std::string() : err);
    return rc;
}

WGC_API void WGC_CALL Wgc_FreeFrame(void* /*handle*/, unsigned char* bits) {
    if (!bits) return;
    ::CoTaskMemFree(bits);
}

WGC_API void WGC_CALL Wgc_LastError(void* handle, char* buffer, int bufferSize) {
    if (!buffer || bufferSize <= 0) return;
    buffer[0] = '\0';
    if (!handle) return;

    const std::string s = static_cast<WgcCapture*>(handle)->GetError();
    if (s.empty()) return;

    const size_t n = (s.size() < static_cast<size_t>(bufferSize) - 1)
                         ? s.size()
                         : static_cast<size_t>(bufferSize) - 1;
    std::memcpy(buffer, s.data(), n);
    buffer[n] = '\0';
}

}  // extern "C"
