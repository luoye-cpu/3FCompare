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
 * 5) 全程 RAII（winrt::com_ptr / 自定义 guard），异常安全。**唯一例外**：目标窗口已消失的
 *    会话会被**故意**挂进 Graveyard 而不释放 —— 释放它会死锁（见 7)）。
 * 6) 超时保护：单次抓帧最多等 2 秒（已有可兜底帧时约 0.4 秒），绝不死等。
 * 7) **teardown 单出口**（docs/41 #6）：session/pool/item 的释放只有 RetireRefs 一个出口，
 *    且「Close 还是入 Graveyard」由「窗口对象身份标记」**一次**判定（见 kWindowTagProp）——
 *    判定不再依赖会被系统回收复用的 HWND 整数值；目标已死一律入表，连 CreateForWindow
 *    中途失败留下的半成品/局部对象也不例外。Worker::InvokeWithDeadline 再兜一层**有界等待**，
 *    保证「不会死等」在死锁真的发生时仍然成立。
 * 8) Graveyard 的**上限只是观测点，不是回收机制**（kGraveyardMax，见 Graveyard::
 *    NoteOverflowLocked）：超限时唯一安全的处置就是**继续持有** —— 任何 Close()/Release()
 *    都可能永久阻塞（见 7)），包括交给「独立回收线程」去做也一样（那只会多泄漏一条永不
 *    退出的线程）。因此该上限**不能**减少显存占用，它只负责在 WGC_TRACE 下暴露「滞留量已
 *    越限、且在增长」，并给出一次明确诊断。根治方向在宿主时序：销毁视频子窗口之前先关闭
 *    WGC 会话（非本 DLL 能解决）。
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
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <functional>
#include <iterator>
#include <memory>
#include <mutex>
#include <new>
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

// 有界等待的上限（docs/41 #6 的看门狗）。worker 线程上的任务正常耗时：抓帧 ≤ 2s 预算 +
// 建会话；teardown 实测 0~16ms。因此这两个值都远大于正常耗时，只在真踩中死锁时才会命中 ——
// 命中后调用方**返回错误**，而不是永久挂死（见 Worker::InvokeWithDeadline）。
constexpr DWORD kCaptureDeadlineMs = 6000;   // 抓帧任务
constexpr DWORD kTeardownDeadlineMs = 3000;  // 销毁 / 关会话
constexpr DWORD kProbeDeadlineMs = 3000;     // 能力探测（Wgc_IsSupported）

// 「待回收表」每类引用的上限（docs/41 #7）。**仅用于观测/告警**，不做任何淘汰或回收 ——
// 见 Graveyard::NoteOverflowLocked。
constexpr size_t kGraveyardMax = 32;

// 能力探测**连续超时**的容忍次数。超时一次 ⇒ 放弃那个 Worker（必须泄漏，见
// Wgc_IsSupported 的注释），代价是一条不会再退出的线程；若每次调用都超时就会单调累积，
// 所以到达本上限后改为**缓存"不支持"**并停止重试。
// 语义上是安全的降级：返回 0 本就等于"不支持 WGC"，托管侧会走 GDI 兜底抓屏（双线设计）。
constexpr int kProbeTimeoutMax = 3;

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
/* 窗口身份标记 + 已销毁窗口的会话：挂起而不 teardown                     */
/* ------------------------------------------------------------------ */

// 【窗口身份标记】判定「会话的目标窗口是否还活着」**不能**只用 IsWindow（docs/41 #6）：
// Windows 会**回收 HWND 值** —— 宿主反复开关媒体、不断重建视频子窗口时，新窗口很可能拿到
// 刚被销毁那个窗口的 HWND 值。此时 IsWindow() 返回 TRUE，但被捕获的**旧窗口对象**其实早已
// 不存在，对它做 Close()/Release() 仍会踩中「目标窗口已消失」的死锁（这才是生产里真正
// 高频的触发路径，远不止自测 [7] 那条窄竞争）。
// 因此改为在**被捕获的窗口对象**上打一个标记（SetProp），拆除时读回（GetProp）：标记随窗口
// 对象销毁而消失；HWND 值即便被复用，新窗口上也没有该标记 ⇒ 判定为「目标已死」。
// 这样「存活判定」依赖的是窗口对象的身份，而不是一个会被复用的整数值；判定与释放之间只剩
// 「取到标记之后、窗口才被销毁」这一瞬时竞争，由 Worker::InvokeWithDeadline 看门狗兜底。
// SetProp 失败（跨进程窗口等极少数情况）时**保守判死**，见 TargetStillAlive —— 绝不退回
// IsWindow：那条判据正是会被 HWND 复用骗过的旧判据，而两种误判的代价不对称（详见该函数）。
constexpr wchar_t kWindowTagProp[] = L"3FC.WgcCapture.TaggedWindow";

// 标记值：只要非 NULL 即可（SetProp 传 NULL 等于**删除**属性，不能当标记用）。
HANDLE WindowTagValue() { return reinterpret_cast<HANDLE>(static_cast<uintptr_t>(1)); }

// 每个 handle 一份的属性名。必须**按 handle 唯一**：多个 handle 捕获同一个顶层窗口时
// （3FCompare 的 4 路画面就是同一个主窗口下的 4 个子窗口，root 相同），共用一个属性名会
// 互相覆盖标记 —— 于是「别的 handle 给一个被复用的 HWND 打上标记」会让本 handle 误判
// 自己的死目标还活着，又回到死锁。带上进程内唯一后缀即互不干扰。
std::wstring MakeTagPropName() {
    static std::atomic<uint64_t> next{1};
    return std::wstring(kWindowTagProp) + L"." +
           std::to_wstring(next.fetch_add(1, std::memory_order_relaxed));
}

// 进程级「待回收表」：存放**目标窗口已被销毁**的捕获会话/帧池/item，之后不再触碰。
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
//
// 关于「上限」（docs/41 #7）：滞留量在反复开关媒体的生产路径上确实会单调增长（每份含帧池的
// 两张全尺寸纹理，1080p 约 16MB），因此曾设过 kGraveyardMax 并「超限时把最旧的条目交给一条
// **独立回收线程**去做真正的 teardown」。该机制已**整体删除**，因为它的前提不成立：
//   ① 回收线程做的正是 s.Close() / p.Close() / clear() —— 也就是上面那段**会永久阻塞**的
//      操作，所以显存一份都回收不掉；
//   ② 回收线程永不返回 ⇒ 表长被压回 kGraveyardMax 之后**下一次入表立刻又越限**，于是每多一次
//      「窗口在会话存活期被销毁」就多泄漏一条永不退出的线程（1MB 栈 + 线程对象 + 它攥着的
//      纹理），比不回收更难收拾；
//   ③ 「同时只允许一条回收线程」「失败后停用」这类变体同样会留下永久挂住的线程，不能采纳。
// 结论：**唯一安全的处置是继续持有**，所以上限机制无法减少显存占用，它只是观测点
// （见 NoteOverflowLocked）。根治方向在宿主时序：销毁视频子窗口之前先关闭 WGC 会话。
struct Graveyard {
    std::mutex m;
    std::vector<winrt::Windows::Graphics::Capture::GraphicsCaptureSession> sessions;
    std::vector<winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool> pools;
    std::vector<winrt::Windows::Graphics::Capture::GraphicsCaptureItem> items;

    static Graveyard& Get() {
        static Graveyard* g = new Graveyard();  // 故意泄漏，见上
        return *g;
    }

    // 越限观测（调用方须持有 m）。**只统计 + 告警，绝不淘汰/回收**（见上方长注释）。
    // 首次越限时无条件往 stderr 打一行明确诊断（此后每次越限只在 WGC_TRACE 下打）。
    void NoteOverflowLocked() {
        size_t worst = sessions.size();
        if (pools.size() > worst) worst = pools.size();
        if (items.size() > worst) worst = items.size();
        if (worst <= kGraveyardMax) return;

        static std::atomic<bool> warned{false};
        if (!warned.exchange(true, std::memory_order_relaxed)) {
            std::fprintf(
                stderr,
                "[wgc] Graveyard 滞留量已超过上限 %zu（session=%zu pool=%zu item=%zu）："
                "显存将持续增长。本 DLL 无法回收这些会话 —— 对目标窗口已消失的捕获会话做 "
                "Close()/Release() 会永久阻塞在系统捕获服务里，唯一安全的处置是继续持有。"
                "根治方向：宿主在销毁视频子窗口之前先关闭 WGC 会话（时序问题）。\n",
                kGraveyardMax, sessions.size(), pools.size(), items.size());
            std::fflush(stderr);
        }
        Trace("[wgc] Graveyard 越限：session=%zu pool=%zu item=%zu（上限 %zu，只统计不回收）\n",
              sessions.size(), pools.size(), items.size(), kGraveyardMax);
    }
};

/* ------------------------------------------------------------------ */
/* MTA 工作线程                                                        */
/* ------------------------------------------------------------------ */

// 一个 handle 一个：所有任务在同一个 MTA 线程上串行执行，同步返回。
class Worker {
public:
    // ⚠ thread_ **必须是最后一个成员**（见成员声明处）：成员按声明顺序初始化，构造函数体开始
    //   前线程就已启动，而 Loop() 会访问 m_/cv_/job_/hasJob_/quit_ —— 若 thread_ 声明在前，
    //   这些成员尚未构造 ⇒ UB（hasJob_ 的未初始化内存恰好非 0 时，worker 会去拷贝尚未构造的
    //   std::function job_）。
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
    // 并发安全（B4）：job_/hasJob_/doneSeq_ 是**单槽**握手机制，只支持「一个在飞任务」。
    // 若两个线程同时进来，后者的 job_ 会覆盖前者、前者永远等不到自己的完成信号
    // （任务被丢弃却返回 INTERNAL），因此这里用 invokeMutex_ 把调用方**串行化**：
    // 第二个调用方阻塞到第一个任务执行完毕，与头文件承诺一致。
    // ⚠ 串行锁的获取本身也必须**有界**（🟠5）：前一个任务永不返回时，后来者若用 lock_guard
    //   就会无限期卡在锁上，与头文件「不会死等」矛盾。改用 timed_mutex + try_lock_for；
    //   取不到就**不投递**并直接返回（本函数无返回值，调用方按「fn 未执行」的既有语义处理）。
    // 注意：串行化只保护 Worker 内部状态；调用方仍不得与 Wgc_Destroy 并发
    // （handle 失效后不得再使用，见头文件）。
    void Invoke(std::function<void()> fn) {
        std::unique_lock<std::timed_mutex> serialize(invokeMutex_, std::defer_lock);
        if (!serialize.try_lock_for(std::chrono::milliseconds(kCaptureDeadlineMs))) {
            Trace("[wgc] Invoke: 串行锁 %lums 内未取到（前一个任务未返回），放弃本次投递\n",
                  kCaptureDeadlineMs);
            return;
        }
        std::unique_lock<std::mutex> lk(m_);
        const uint64_t my = ++seq_;  // 本任务的序号（见 doneSeq_ 注释）
        job_ = std::move(fn);
        hasJob_ = true;
        cv_.notify_all();
        cv_.wait(lk, [this, my] { return doneSeq_ >= my || quit_; });
        job_ = nullptr;
    }

    // 与 Invoke 相同的握手，但**有界等待**：最多等 timeoutMs（docs/41 #6 的看门狗）。
    // 返回 true  = fn 已执行完毕，本 Worker 可安全析构（析构里的 join 会立即返回）；
    // 返回 false = 未能在预算内确认 fn 执行完毕：可能是 fn 仍在跑或已卡死（最可能是命中
    //   「目标窗口已消失」的 teardown 死锁），也可能是**串行锁**没抢到（见下）。
    //   此时调用方**必须放弃本 Worker**：不析构、不 join、不再投递任务，否则又变回死等。
    // ⚠ fn 可能在本函数返回**之后**才写完它捕获的状态，所以调用方绝不能把结果直接写在
    //   「本函数返回后就要离开作用域」的对象上（见 Wgc_CaptureFrame 里的 shared_ptr）。
    bool InvokeWithDeadline(std::function<void()> fn, DWORD timeoutMs) {
        // 串行锁的获取也纳入看门狗（🟠5）：否则第二个调用方会卡在 invokeMutex_ 上无限期等待
        // （只有前一个调用方自己超时返回后才释放）。简单起见用满额预算 try_lock_for；
        // 拿不到即返回 false —— 不投递任务，调用方拿到既有错误语义。
        std::unique_lock<std::timed_mutex> serialize(invokeMutex_, std::defer_lock);
        if (!serialize.try_lock_for(std::chrono::milliseconds(timeoutMs))) {
            Trace("[wgc] InvokeWithDeadline: 串行锁 %lums 内未取到（前一个任务未返回），不投递\n",
                  timeoutMs);
            return false;
        }
        std::unique_lock<std::mutex> lk(m_);
        const uint64_t my = ++seq_;  // 本任务的序号（见 doneSeq_ 注释）
        job_ = std::move(fn);
        hasJob_ = true;
        cv_.notify_all();
        const bool finished = cv_.wait_for(lk, std::chrono::milliseconds(timeoutMs),
                                           [this, my] { return doneSeq_ >= my || quit_; });
        // **无条件**清槽（超时路径也清）：否则 worker 可能在超时之后才取到这一槽，把「调用方
        // 已经放弃、结果无人读取」的任务再执行一遍（对抓帧任务而言可能长时间占用 worker）。
        job_ = nullptr;
        return finished;
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
            uint64_t mySeq = 0;
            {
                std::unique_lock<std::mutex> lk(m_);
                cv_.wait(lk, [this] { return hasJob_ || quit_; });
                if (quit_) break;
                job = job_;
                mySeq = seq_;  // 本次取走的是「第几号任务」⇒ 完成时回报同一号（见 doneSeq_）
                hasJob_ = false;
            }

            // 任务本身已捕获所有异常；这里再兜一层，避免异常逃逸导致 std::terminate。
            try { job(); } catch (...) {}

            {
                std::lock_guard<std::mutex> lk(m_);
                doneSeq_ = mySeq;
            }
            cv_.notify_all();
        }

        try { winrt::uninit_apartment(); } catch (...) {}
    }

    std::timed_mutex invokeMutex_;  // 串行化 Invoke 调用方（见 Invoke 注释）；获取必须有界
    std::mutex m_;
    std::condition_variable cv_;
    std::function<void()> job_;
    bool hasJob_ = false;
    // 任务序号（🟠4）：若完成信号只是一个全局 bool（原 done_），会出现「A 超时后 B 的任务被
    // A 的完成信号误判为已完成」—— B 读到 out->rc 的初值就返回，B 的任务从未执行；随后
    // worker 取到已被置空的 job_ 并对空 std::function 调用，抛 bad_function_call 被
    // `catch (...) {}` 吃掉 ⇒ 任务彻底丢失。改为「每投递一个任务 ++seq_，worker 完成时把
    // 该任务的序号写进 doneSeq_」，等待谓词用 `doneSeq_ >= my` ⇒ 每个调用方只认自己那一号。
    // seq_/doneSeq_ 均受 m_ 保护。
    uint64_t seq_ = 0;
    uint64_t doneSeq_ = 0;
    bool quit_ = false;
    // ⚠ 必须是最后一个成员：线程在构造函数体之前就启动，Loop() 会访问上面的全部成员（🟠3）。
    std::thread thread_;
};

/* ------------------------------------------------------------------ */
/* 捕获器                                                              */
/* ------------------------------------------------------------------ */

struct WgcCapture {
    Worker worker;

    // 错误信息：worker 线程写、调用方线程读 ⇒ 单独加锁。
    std::mutex errMutex;
    std::string lastError;

    // 已放弃标记：调用方线程写、调用方线程读（worker 线程卡死时它不会再读任何东西）。
    std::atomic<bool> poisoned_{false};

    void SetError(std::string s) {
        std::lock_guard<std::mutex> lk(errMutex);
        lastError = std::move(s);
    }
    std::string GetError() {
        std::lock_guard<std::mutex> lk(errMutex);
        return lastError;
    }

    // 该 handle 是否已因 worker 线程有界等待超时而被放弃（见 Wgc_CaptureFrame / Wgc_Destroy）。
    // 置位后所有抓帧调用快速失败：死掉的 worker 线程不会再执行任何任务，继续投递只会白等。
    void Poison() { poisoned_.store(true, std::memory_order_relaxed); }
    bool Poisoned() const { return poisoned_.load(std::memory_order_relaxed); }

    // ---- 以下成员仅在 worker 线程上访问 ----
    HWND hwnd = nullptr;        // 当前会话对应的调用方 hwnd（会话关闭即置空）
    HWND cacheHwnd = nullptr;   // 跨会话缓存归属的 hwnd；变了才丢弃缓存（见 CloseSession）
    HWND captureHwnd = nullptr; // 实际被 WGC 捕获的窗口（子窗口时 = 顶层祖先）
    bool cropping = false;      // true = 捕获的是顶层祖先，需要裁剪到子窗口客户区
    bool windowTagged = false;  // 建会话时是否成功给 captureHwnd 打了身份标记（见 kWindowTagProp）
    std::wstring tagProp = MakeTagPropName();  // 本 handle 专属的标记属性名（见 MakeTagPropName）

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
        RetireRefs(TargetStillAlive());
        if (!keepCache) {
            hasCache = false;  // 缓存归属该 hwnd；换了 hwnd 即失效
            cache.clear();
            cacheW = cacheH = cacheStride = 0;
            cacheHwnd = nullptr;
        }
    }

    // 目标窗口对象是否**仍是建会话时的那个**（见 kWindowTagProp 注释）。
    // 返回 false ⇒ 绝不能对 session/pool/item 做 Close()/Release()（会死锁，见 Graveyard）。
    bool TargetStillAlive() const {
        if (!session && !pool && !item) return true;  // 没有引用要释放：判定无意义
        if (!captureHwnd && !hwnd) return false;      // 有引用却不知道目标：保守当已死
        if (!windowTagged) {
            // SetProp 失败（跨进程窗口等极少数情况）⇒ 无法确认窗口对象身份。
            // 这里**保守判死**，不退回 IsWindow：判据的代价是不对称的 ——
            //   「误判为已死」的代价只是把对象挂进 Graveyard（泄漏一份已无产出的会话）；
            //   「误判为还活着」的代价是对一个已消失的窗口做 teardown ⇒ 宿主界面冻死。
            // IsWindow 又恰好会被 HWND 值复用骗过（docs/41 #6），所以不能退回去。
            return false;
        }
        // 标记随窗口对象销毁而消失；HWND 值被复用时新窗口上没有**本 handle 的**标记
        // ⇒ 目标已死。（用本 handle 专属的属性名，别人的标记不会造成假阳性。）
        const wchar_t* prop = tagProp.c_str();
        if (captureHwnd && ::GetPropW(captureHwnd, prop) == WindowTagValue()) return true;
        if (hwnd && ::GetPropW(hwnd, prop) == WindowTagValue()) return true;
        return false;
    }

    // 「已有会话可否直接复用」判据（EnsureSession 用）。与 TargetStillAlive 的**唯一**差别是
    // 标记不可用时的取向，取向相反是因为这里的代价结构相反：
    //   「误判为可复用」的代价 = 继续用一个可能已死的会话 ⇒ 抓帧超时/报错，**有界**，不冻死；
    //   「误判为不可复用」的代价 = 每次抓帧都重建会话、并把旧会话挂进 Graveyard（RetireRefs
    //   判定为「目标已死」⇒ 不 Close 也不 Release）⇒ **每帧泄漏一份帧池**，远大于前者。
    // 因此在标记不可用时退化为 IsWindow（= W2 之前的原有行为），其余情况一律走身份标记。
    bool SessionReusable(HWND target) const {
        if (!session || hwnd != target) return false;
        if (windowTagged) return TargetStillAlive();
        return (target && ::IsWindow(target)) || (captureHwnd && ::IsWindow(captureHwnd));
    }

    // 释放会话相关引用的**唯一出口**（docs/41 #6）：所有 session/pool/item 引用都从这里出去，
    // 且「Close 还是入 Graveyard」只由调用方传入的**一次**判定决定 —— 不再出现「各分支各自
    // 用 IsWindow 判一次、判完到释放之间目标窗口又被销毁」的 TOCTOU 分叉。
    // targetAlive=false（目标窗口对象已消失）时绝不 Close()/Release()，一律挂进 Graveyard。
    // ⚠ EnsureSession 里**半成品**创建的 item/pool/session 也走这里：局部对象不受保护时，
    //   它的析构就是「最后一次 Release」，会直接踩中同一段死锁。
    void RetireRefs(bool targetAlive) {
        if ((session || pool || item) && !targetAlive) {
            // 先把身份字段清掉：万一下面入表失败（极端内存不足），再次进来仍走这条安全分支，
            // 而不会去 Close() 一个目标已死的会话。
            hwnd = nullptr;
            captureHwnd = nullptr;
            windowTagged = false;
            try {
                auto& g = Graveyard::Get();
                std::lock_guard<std::mutex> lk(g.m);
                if (session) g.sessions.push_back(session);
                if (pool) g.pools.push_back(pool);
                if (item) g.items.push_back(item);
                session = nullptr;  // 入表已持有一份引用 ⇒ 这里不是最后一次 Release
                pool = nullptr;
                item = nullptr;
                g.NoteOverflowLocked();  // 越限只观测告警，不回收（docs/41 #7）
            } catch (...) {
                // 入表失败：宁可把引用留在 handle 上（不释放、不 teardown），也绝不冒死锁风险。
            }
        } else {
            if (session) { try { session.Close(); } catch (...) {} session = nullptr; }
            if (pool) { try { pool.Close(); } catch (...) {} pool = nullptr; }
            item = nullptr;
        }

        hwnd = nullptr;
        captureHwnd = nullptr;
        windowTagged = false;
        cropping = false;
        sessionAnchor100ns = 0;
        clockUsable = false;
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
    // 复用判定除「hwnd 值相同」外还要确认**窗口对象**还是同一个（docs/41 #6）：HWND 值会被
    // 系统回收，宿主反复开关媒体时新子窗口可能拿到刚销毁那个窗口的 HWND 值，此时按值复用
    // 就会继续用一个绑在已销毁窗口上的死会话。
    // ⚠ 这里用 SessionReusable 而非 TargetStillAlive：后者的取向是「宁可不释放」，直接拿来做
    //   复用判定会在「窗口标记不可用」时**每帧重建会话 + 每帧往 Graveyard 挂一份帧池**。
    if (h.SessionReusable(hwnd)) return WGC_OK;

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

    // ⚠ 从这里开始所有 session/pool/item 引用**直接落在 h 的成员上**，不再用局部变量
    //   （docs/41 #6）：局部对象不受 Graveyard 保护，一旦在 CreateForWindow /
    //   CreateCaptureSession / StartCapture 期间目标窗口消失（或随后 WinRT 调用抛异常），
    //   局部 pool/session 的析构就是「最后一次 Release」，会直接踩中同一段死锁。
    //   同时**先把身份（hwnd/captureHwnd/窗口标记）写进 h**，这样下面任何失败分支经
    //   CloseSession 退出时都能正确判定「目标窗口对象是否还在」，从而决定 Close 还是入表。
    h.hwnd = hwnd;
    h.captureHwnd = root;
    h.cropping = cropping;
    h.sessionAnchor100ns = 0;
    h.clockUsable = false;
    h.windowTagged = (::SetPropW(root, h.tagProp.c_str(), WindowTagValue()) != FALSE);
    if (!h.windowTagged) {
        Trace("[wgc] SetProp 窗口标记失败（err=%lu），退化为 IsWindow 存活判定\n",
              ::GetLastError());
    }

    // 单一出口：任何失败（含异常）都在这里退出 —— 先关会话 / 视目标存活决定是否入
    // Graveyard，再返回错误码。不再有「某条分支直接 return、把引用留给局部变量析构」的路径。
    auto fail = [&](int code, std::string message) {
        err = std::move(message);
        h.CloseSession(sameHwnd);  // 半成品会话不保留；目标已死则入 Graveyard
        return code;
    };

    // 阶段 2：创建捕获器 / 帧池 / 会话。任一失败都归入 WGC_ERR_CREATE_FAILED(2)，
    // 与头文件的错误码表一致（此前这些 check_hresult 会一路冒泡成 INTERNAL(7)）。
    try {
        // C++/WinRT 没有 GraphicsCaptureItem::CreateFromWindow 投影，必须走 interop。
        auto interop =
            winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(
            root, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(h.item)));
        if (!h.item) {
            return fail(WGC_ERR_CREATE_FAILED, "CreateForWindow 返回空 item");
        }
        const DWORD tItem = ::GetTickCount();

        const auto size = h.item.Size();
        if (size.Width <= 0 || size.Height <= 0) {
            return fail(WGC_ERR_NOT_CAPTURABLE,
                        "窗口客户区尺寸为 0（" + std::to_string(size.Width) + "x" +
                            std::to_string(size.Height) + "）");
        }

        h.pool = Direct3D11CaptureFramePool::Create(
            h.rtDevice,
            winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, size);
        h.session = h.pool.CreateCaptureSession(h.item);

        h.session.IsCursorCaptureEnabled(false);
        // 关掉系统黄框；旧系统（< Win11 / build 20348）没有该属性会抛异常，忽略即可。
        try {
            h.session.IsBorderRequired(false);
        } catch (winrt::hresult_error const&) {
            // 无法关闭黄框：不影响抓帧正确性。
        }

        h.session.StartCapture();
        const DWORD tStarted = ::GetTickCount();

        // 会话起点锚：紧跟在 StartCapture 之后取。早于此刻合成的帧一律视为「会话前合成」，
        // 不作为可信帧返回（见 GrabFrame）。
        h.sessionAnchor100ns = QpcNow100ns();
        h.clockUsable = false;
        h.cacheHwnd = hwnd;  // 缓存（若有）归属这个 hwnd
        Trace("[wgc] EnsureSession hwnd=%p 关旧会话=%lums CreateForWindow=%lums 建池建会话=%lums "
              "StartCapture=%lums 合计=%lums\n",
              hwnd, tClosed - tBegin, tItem - tClosed, tStarted - tItem,
              ::GetTickCount() - tStarted, ::GetTickCount() - tBegin);
        return WGC_OK;
    } catch (winrt::hresult_error const& e) {
        return fail(WGC_ERR_CREATE_FAILED, "创建捕获会话失败: " + FromHresult(e));
    } catch (std::exception const& e) {
        return fail(WGC_ERR_CREATE_FAILED,
                    std::string("创建捕获会话失败（C++ 异常）: ") + e.what());
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
    //
    // ⚠ 只用「真正得到答案」的结果写缓存：Worker::InvokeWithDeadline 在**串行锁没抢到**
    // 或**等待超时**时返回 false（fn 根本没投递/没跑完），探测本身也可能抛异常 —— 这两种
    // 情况下 result 都不是"不支持"，而是"未知"。若把未知当成 0 写进 static 缓存，一次偶发的
    // 锁超时就会把整个进程的能力探测**永久钉死**为 false（此后所有调用直接命中缓存返回
    // false，永不重试）。因此这里用 -1 表示未知：只有探测确实返回了（无论 true/false）
    // 才缓存；未知则本次返回 0 但不缓存，让下次调用还能重试。
    //
    // ⚠ 为什么必须用 InvokeWithDeadline 而不是无界的 Invoke（docs/41 §13.6 backlog）：
    // Invoke 内是 cv_.wait 无超时，一旦 GraphicsCaptureSession::IsSupported() 卡住
    // （同族调用实测有 ≥150s 不返回的先例，见 GrabFrame 的注释），本函数就会把调用方
    // 永久挂死。改用有界版本后，最坏情况是"超时 + 滞留一条线程"，本函数必定在
    // kProbeDeadlineMs 内返回。
    // ⚠ probe 必须在**堆上**：InvokeWithDeadline 超时返回后，fn 可能到此刻之后才被执行完
    // 并写入 probe（见它的契约注释）。栈上对象会在那时已成悬空引用 ⇒ 堆分配 + shared_ptr
    // 让「调用方已放弃」与「fn 仍在跑」两种时序下都安全。
    auto probe = std::make_shared<std::atomic<int>>(-1);  // -1=未知；0=不支持；1=支持
    // 连续超时计数：见 kProbeTimeoutMax。
    static std::atomic<int> probeTimeouts{0};
    if (probeTimeouts.load(std::memory_order_relaxed) >= kProbeTimeoutMax) {
        // 已连续超时到上限 ⇒ 停止重试，降级缓存"不支持"（托管侧走 GDI 兜底）。
        cached.store(0, std::memory_order_relaxed);
        return 0;
    }

    Worker* w = nullptr;
    bool finished = false;
    try {
        // 故意用 new 而不是栈对象：超时路径下**绝不能**析构（析构里的 join 会等一条可能
        // 永不返回的线程，又变回死等）—— 此时唯一安全的处置是放弃它（泄漏一条线程），
        // 与 Graveyard「宁可滞留也不回收」同源。
        w = new Worker();
        finished = w->InvokeWithDeadline([probe] {
            try {
                probe->store(
                    winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported() ? 1 : 0,
                    std::memory_order_release);
            } catch (...) {
                // 查询本身失败（公寓/激活异常等）⇒ 保持「未知」，不写缓存。
            }
        }, kProbeDeadlineMs);
    } catch (...) {
        // Worker 构造失败等 ⇒ 保持「未知」，不写缓存。
    }

    if (finished) {
        delete w;  // fn 已执行完毕 ⇒ 析构里的 join 立即返回
    } else {
        // 超时 / 串行锁没抢到 / 构造失败 ⇒ 放弃这个 Worker：不析构、不 join。
        // 代价是一条滞留线程，换来的是本函数**有界返回**（不再永久挂死）。
        probeTimeouts.fetch_add(1, std::memory_order_relaxed);
        Trace("[wgc] IsSupported: 探测未在 %lums 内返回（第 %d 次），放弃本次 Worker（滞留一条线程）\n",
              kProbeDeadlineMs, probeTimeouts.load(std::memory_order_relaxed));
    }

    const int result = probe->load(std::memory_order_acquire);
    if (result >= 0) {
        probeTimeouts.store(0, std::memory_order_relaxed);  // 拿到答案 ⇒ 重置连续超时
        cached.store(result, std::memory_order_relaxed);
    }
    return result < 0 ? 0 : result;
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
    if (h->Poisoned()) {
        // worker 线程已卡死：delete h 会 join 它 ⇒ 又变回死等。宁可泄漏一个 handle
        // （进程退出即回收），也绝不让宿主的关闭流程挂死（docs/41 #6）。
        Trace("[wgc] Wgc_Destroy: handle 已被放弃，跳过 join 以免死等\n");
        return;
    }
    // 有界等待 teardown（docs/41 #6）：正常实测 0~16ms；命中「目标窗口已消失」的死锁时
    // 返回 false，而不是永久阻塞。
    if (!h->worker.InvokeWithDeadline([h] { h->CloseSession(); }, kTeardownDeadlineMs)) {
        Trace("[wgc] Wgc_Destroy: teardown 超时，放弃该 handle（不 join、不 delete）\n");
        h->Poison();
        return;
    }
    delete h;  // 析构里 join 工作线程
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

    // 已被放弃的 handle 快速失败：死掉的 worker 线程不会再执行任何任务，继续投递只会白等。
    // ⚠ 必须用**专用错误码** WGC_ERR_HANDLE_DEAD(8) 而不是 WGC_ERR_INTERNAL(7)：
    // 托管侧需要据此判断"该重建 handle 了"。若混在 7 里，托管侧要么只能去匹配诊断文本
    // （脆弱），要么一遇到瞬时错误就重建 handle（代价高且会掩盖真实故障）。
    if (h->Poisoned()) {
        h->SetError("该 handle 的内部工作线程此前已超时被放弃，handle 已永久失效："
                    "请 Wgc_Destroy 后重新 Wgc_Create");
        return WGC_ERR_HANDLE_DEAD;
    }

    const HWND wnd = reinterpret_cast<HWND>(hwnd);

    // 结果**不直接写调用方的栈变量**：一旦有界等待超时，本函数会立刻返回，而任务可能仍在
    // worker 线程上跑；若它随后写 outBits/outWidth… 就是 use-after-free。因此结果先落在
    // shared_ptr 里，只有确认任务已结束才读出来（docs/41 #6）。
    struct Outcome {
        int rc = WGC_ERR_INTERNAL;
        std::string err;
        unsigned char* bits = nullptr;
        int width = 0, height = 0, stride = 0;
    };
    auto out = std::make_shared<Outcome>();

    // 所有 WGC 操作都放到该 handle 的 MTA 工作线程上执行，与调用方公寓无关。
    // 有界等待（看门狗）：worker 卡死（最可能是踩中「目标窗口已消失」的 teardown 死锁）时
    // 返回错误，而不是让宿主界面永久冻死 —— 兑现头文件「不会死等」的承诺。
    const bool finished = h->worker.InvokeWithDeadline(
        [h, wnd, out] {
            try {
                out->rc = CaptureImpl(*h, wnd, &out->bits, &out->width, &out->height,
                                      &out->stride, out->err);
            } catch (winrt::hresult_error const& e) {
                out->err = FromHresult(e);
                out->rc = WGC_ERR_INTERNAL;
            } catch (std::exception const& e) {
                out->err = std::string("C++ 异常: ") + e.what();
                out->rc = WGC_ERR_INTERNAL;
            } catch (...) {
                out->err = "未知异常";
                out->rc = WGC_ERR_INTERNAL;
            }
        },
        kCaptureDeadlineMs);

    if (!finished) {
        // worker 线程已卡死且可能永不返回：本 handle 不可再用，标记放弃以免后续调用次次白等。
        h->Poison();
        h->SetError("抓帧超时：内部工作线程未在 " + std::to_string(kCaptureDeadlineMs) +
                    " ms 内返回，已放弃该 handle（请重新创建捕获器）");
        // ⚠ 必须返回 WGC_ERR_HANDLE_DEAD(8) 而不是 WGC_ERR_INTERNAL(7)：上面刚 Poison，
        // 本 handle 已**永久失效**，正是 8 的定义（7 是"本次失败、handle 仍可用"）。
        // 托管侧只对 8 触发重建（WgcFrameCapture.Capture 里 `rc == ErrHandleDead`
        // ⇒ ResetHandleLocked）；若这里返回 7，托管侧不会重建，得等下一次调用拿到 8 才重建
        // ⇒ 白白多废一轮（多一帧旧/黑帧）。返回 8 = "本次即重建"，与 Poison 的语义一致。
        return WGC_ERR_HANDLE_DEAD;
    }

    const int rc = out->rc;
    if (rc == WGC_OK) {
        *outBits = out->bits;
        *outWidth = out->width;
        *outHeight = out->height;
        *outStride = out->stride;
    }
    h->SetError(rc == WGC_OK ? std::string() : out->err);
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
