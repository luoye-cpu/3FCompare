// ============================================================================
// display_grid_probe.cpp —— 呈现网格探针（2026-10-15）
//
// 要回答的问题：60fps 内容在 144Hz 面板上，**每一帧在屏幕上到底停留了几个
// 刷新周期**。产品侧计数器只能看 CPU 提交节律；实测 Present 平均只等 0.60ms
// ⇒ Present(1,0) 在"内容比面板慢"时不阻塞，所以"锁没锁住"必须换成 DXGI
// 自己的帧统计（IDXGISwapChain::GetFrameStatistics）来量。
//
// 字段语义（权威：dxgi.h 的 DXGI_FRAME_STATISTICS + "DXGI flip model" 文档）：
//   PresentCount        自启动以来"图像被显示到监视器"的累计次数（≠ Present 调用数）
//   PresentRefreshCount 最后一次图像被显示到监视器时所处 v 空白的累计计数
//   SyncRefreshCount    调度器上次用 QueryPerformanceCounter 采样时的 v 空白累计计数
//   SyncQPCTime         那次采样的 QPC 值（与 QueryPerformanceCounter 同刻度）
//   ⇒ 一帧在屏上的停留 = 相邻两次 Present 后读到的 PresentRefreshCount 之差；
//     面板真实周期 = ΔSyncQPCTime / ΔSyncRefreshCount（不依赖 EnumDisplaySettings）。
//   翻转链在窗口模式也提供统计；bitblt 链全零 ⇒ 全零要显式登记，不能当读数。
//   首次调用与模式切换返回 DXGI_ERROR_FRAME_STATISTICS_DISJOINT ⇒ 只重置基线。
//
// 夹具不加载内核：自建设备 + 交换链，逐字段照抄产品创建路径
//   （VideoRenderer.cpp:2892-2901 的 DESC1、:2955 的 SetMaximumFrameLatency(2)、
//    :2944 记录"链上没有 FRAME_LATENCY_WAITABLE_OBJECT"），
//   并用与产品发布规则同形的节律喂 60fps
//   （PlayerSession.cpp:3374 的 `position <= now + 20'000` 提前容差、
//    :3403-3404 的 clamp(…,1,20) 等待）。
//
// 自检（夹具有效性）：臂 A 的 CPU 提交间隔必须落在产品实测带内
//   （均值 16.76–16.90ms、中位 15.69–15.82ms、最大 32–39ms），否则夹具不代表产品。
//
// 三个臂：
//   A 现行节律   产品同形：媒体时钟 + 提前容差 + Present(1,0)，无网格知识
//   B 网格锁     同一条链，先用 (SyncRefreshCount, SyncQPCTime) 标定面板网格，
//                再把每帧的提交时刻钉到"该帧目标 v 空白前 lead 毫秒"
//   C 立即提交   每次到期就 Present(0)，用来给出"完全无锁"的下界参照
//
// 判据（judder 取"可预测性"而不是方差）：
//   理想停留序列 ideal(n) = round((n+1)·r) − round(n·r)，r = 面板Hz/内容Hz（144/60=2.4）。
//   先在 5 个相位里取偏差最小的常数相位偏移（恒定相位观众看不出来），
//   再判：偏离帧占比 > DGP_JUDDER_MAX_FRAC（默认 5%）⇒ 红。
//   稳态样本 <120 帧 ⇒ 该臂不可判。PresentRefreshCount 是整数，无量化带。
//
// 用法：display_grid_probe        （环境变量 DGP_SECONDS=6 DGP_ARMS=ABC
//                                  DGP_MEDIA_FPS=60 DGP_LEAD_MS=2.0）
// 编译：py 3fp/tools/build_probe.py display_grid_probe
// 退出码：0=判且绿 · 1=有红 · 2=建窗/建链失败 · 3=不可判
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi1_6.h>
#include <d3d11_4.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr std::int64_t kTicks = 10'000'000;   // 100ns 单位

double EnvDouble(const char* name, double fallback) {
    const auto raw = std::getenv(name);
    if (raw == nullptr) return fallback;
    const auto value = std::atof(raw);
    return value > 0 ? value : fallback;
}
std::string EnvString(const char* name, const char* fallback) {
    const auto raw = std::getenv(name);
    return (raw != nullptr && *raw != '\0') ? std::string(raw) : std::string(fallback);
}

std::int64_t QpcFrequency() {
    static std::int64_t frequency = [] { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return f.QuadPart; }();
    return frequency;
}
std::int64_t QpcNow() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }

struct Stats {
    std::vector<double> values;
    double Mean() const { double s = 0; for (const auto v : values) s += v; return values.empty() ? 0 : s / values.size(); }
    double Quantile(double q) const {
        if (values.empty()) return 0; auto c = values; std::sort(c.begin(), c.end());
        return c[static_cast<std::size_t>(q * (c.size() - 1) + 0.5)];
    }
};

LRESULT CALLBACK WndProc(HWND w, UINT m, WPARAM a, LPARAM b) {
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(w, m, a, b);
}

struct ArmResult {
    char arm = '?';
    bool built = false;
    bool judged = false;
    bool red = false;
    std::size_t samples = 0;
    std::size_t deviations = 0;
    double deviationFraction = 0;
    int bestPhase = 0;
    double cpuMeanMs = 0, cpuMedianMs = 0, cpuMaxMs = 0;
    double presentWaitMeanMs = 0;
    double calibratedHz = 0;
    std::size_t sameVblank = 0;       // 差分=0 ⇒ 这两次 Present 落在同一次 v 空白，前一帧从没上过屏
    std::size_t discontinuities = 0;  // 差分>30 ⇒ 计数器不连续，不能当停留读数
    std::int64_t totalAdvance = 0;
    std::int64_t expectedAdvance = 0;
    std::vector<int> holds;
    std::string note;
};

// 产品创建路径的逐字段复制。
IDXGISwapChain4* CreateChain(ID3D11Device* device, HWND window, bool allowTearingCapability,
    std::uint32_t& flagsOut) {
    IDXGIDevice* dxgiDevice = nullptr;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))) return nullptr;
    IDXGIAdapter* adapter = nullptr;
    if (FAILED(dxgiDevice->GetAdapter(&adapter))) { dxgiDevice->Release(); return nullptr; }
    IDXGIFactory2* factory = nullptr;
    const bool ok = SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory)));
    dxgiDevice->Release(); adapter->Release();
    if (!ok) return nullptr;

    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = 1280; description.Height = 720;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;          // 产品 8bit SDR 分支
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 3;                               // VideoRenderer.cpp:2898
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;    // :2898
    description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;            // :2899
    description.Scaling = DXGI_SCALING_NONE;                   // :2899
    description.Flags = allowTearingCapability ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;  // :2900
    flagsOut = description.Flags;
    IDXGISwapChain1* chain1 = nullptr;
    const HRESULT created = factory->CreateSwapChainForHwnd(device, window, &description,
        nullptr, nullptr, &chain1);                            // output 传 nullptr，同产品
    factory->Release();
    if (FAILED(created) || chain1 == nullptr) return nullptr;
    IDXGISwapChain4* chain = nullptr;
    if (FAILED(chain1->QueryInterface(IID_PPV_ARGS(&chain)))) { chain1->Release(); return nullptr; }
    chain1->Release();
    IDXGIDevice1* latencyDevice = nullptr;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&latencyDevice)))) {
        latencyDevice->SetMaximumFrameLatency(2);              // VideoRenderer.cpp:2955
        latencyDevice->Release();
    }
    return chain;
}

// 一臂：arm='A' 产品同形；'B' 网格锁；'C' 完全无锁。
ArmResult RunArm(const char name, ID3D11Device* device, ID3D11DeviceContext* context,
    HWND window, double seconds, double mediaFps, double declaredHz, double leadMs) {
    ArmResult result; result.arm = name;
    std::uint32_t flags = 0;
    IDXGISwapChain4* chain = CreateChain(device, window, true, flags);
    if (chain == nullptr) { result.note = "建链失败"; return result; }
    result.built = true;

    ID3D11Texture2D* backBuffer = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) ||
        FAILED(device->CreateRenderTargetView(backBuffer, nullptr, &target))) {
        if (backBuffer != nullptr) backBuffer->Release();
        chain->Release(); result.note = "RTV 失败"; return result;
    }
    backBuffer->Release();

    const double mediaPeriodMs = 1000.0 / mediaFps;
    const std::int64_t mediaPeriodTicks = static_cast<std::int64_t>(mediaPeriodMs * 10'000.0);
    const double ratio = declaredHz / mediaFps;

    // 面板网格标定状态：由 (SyncRefreshCount, SyncQPCTime) 对来反解，不信任声明值。
    std::int64_t calibrationCount = 0;
    std::int64_t calibrationQpc = 0;
    double refreshPeriodQpc = QpcFrequency() / declaredHz;   // 一个刷新周期的 QPC 数；先按声明值起种
    bool gridKnown = false;
    std::int64_t anchorRefresh = 0;
    std::int64_t anchorQpc = 0;
    std::int64_t anchorFrame = -1;

    Stats cpuIntervals, presentWaits;
    std::vector<std::int64_t> presentRefreshes;
    std::vector<std::int64_t> presentCounts;
    bool disjointSeen = false, allZeroStats = false;

    const auto startQpc = QpcNow();
    const auto endTicks = static_cast<std::int64_t>(seconds * kTicks);
    std::int64_t lastSubmitQpc = 0;
    std::uint64_t frameIndex = 0;
    std::int64_t nextDueTicks = mediaPeriodTicks;
    const std::int64_t qpcFrequency = QpcFrequency();

    for (;;) {
        const auto nowQpc = QpcNow();
        const auto nowTicks = (nowQpc - startQpc) * kTicks / qpcFrequency;
        if (nowTicks > endTicks) break;

        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }

        std::int64_t dueTicks = nextDueTicks;
        if (name == 'B' && gridKnown && anchorFrame >= 0) {
            // 第 n 帧应在 anchorRefresh + round((n+1-anchorFrame)·r) 那次 v 空白显示，
            // 提交时刻 = 该 v 空白的 QPC 时刻 - lead。
            const double cumulative = static_cast<double>(frameIndex + 1 - anchorFrame) * ratio;
            const std::int64_t targetRefresh = anchorRefresh +
                static_cast<std::int64_t>(std::llround(cumulative));
            const auto targetQpc = anchorQpc +
                static_cast<std::int64_t>(static_cast<double>(targetRefresh - anchorRefresh) * refreshPeriodQpc);
            const auto submitQpc = targetQpc - static_cast<std::int64_t>(leadMs * 10'000.0 * qpcFrequency / kTicks);
            dueTicks = (submitQpc - startQpc) * kTicks / qpcFrequency;
        }
        if (dueTicks > nowTicks + 20'000) {   // 同产品的 2ms 提前容差
            const auto waitTicks = dueTicks - nowTicks - 20'000;
            const auto waitMs = std::clamp<std::int64_t>((waitTicks + 9'999) / 10'000, 1, 20);
            std::this_thread::sleep_for(std::chrono::milliseconds(waitMs));
            continue;
        }

        const float hue = static_cast<float>(frameIndex % 60u) / 60.0f;
        const float color[4] = { hue, 1.0f - hue, 0.5f, 1.0f };
        context->OMSetRenderTargets(1, &target, nullptr);
        context->ClearRenderTargetView(target, color);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->Flush();

        const auto presentStart = QpcNow();
        const HRESULT present = (name == 'C')
            ? chain->Present(0, 0)
            : chain->Present(1, 0);
        presentWaits.values.push_back(static_cast<double>(QpcNow() - presentStart) * 1000.0 / qpcFrequency);
        if (present == DXGI_ERROR_INVALID_CALL) {
            result.note = "Present(1,0) 判 DXGI_ERROR_INVALID_CALL（链上没有 ALLOW_TEARING？不可能：已带）";
            break;
        }
        if (present == DXGI_ERROR_WAS_STILL_DRAWING) { /* 合法：不记录统计 */ }
        else if (FAILED(present)) { result.note = "Present 失败"; break; }

        DXGI_FRAME_STATISTICS stats{};
        const HRESULT statsResult = chain->GetFrameStatistics(&stats);
        if (statsResult == DXGI_ERROR_FRAME_STATISTICS_DISJOINT) {
            disjointSeen = true;                    // 序列起点/模式切换：只重置基线
        } else if (SUCCEEDED(statsResult)) {
            const auto syncQpc = stats.SyncQPCTime.QuadPart;
            if (stats.PresentRefreshCount == 0 && stats.SyncRefreshCount == 0 && syncQpc == 0)
                allZeroStats = true;
            presentRefreshes.push_back(static_cast<std::int64_t>(stats.PresentRefreshCount));
            presentCounts.push_back(static_cast<std::int64_t>(stats.PresentCount));
            if (presentRefreshes.size() <= 4)
                std::printf("    [原始] PresentCount=%u PresentRefreshCount=%u SyncRefreshCount=%u\n",
                    stats.PresentCount, stats.PresentRefreshCount, stats.SyncRefreshCount);
            // 网格标定：ΔSyncQPCTime / ΔSyncRefreshCount = 一个刷新周期的 QPC 数
            if (stats.SyncRefreshCount != 0 && syncQpc != 0) {
                if (calibrationCount != 0 &&
                    static_cast<std::int64_t>(stats.SyncRefreshCount) > calibrationCount) {
                    const auto period = static_cast<double>(syncQpc - calibrationQpc) /
                        (static_cast<std::int64_t>(stats.SyncRefreshCount) - calibrationCount);
                    if (period > 0) {
                        refreshPeriodQpc = period;
                        gridKnown = true;
                        // 只锚一次：目标刷新序号必须挂在固定基准上，否则每帧重锚会让
                        // 序号不断前跳，B 臂的"等目标 v 空白"永远等不到。
                        if (anchorFrame < 0) {
                            anchorRefresh = static_cast<std::int64_t>(stats.SyncRefreshCount);
                            anchorQpc = syncQpc;
                            anchorFrame = static_cast<std::int64_t>(frameIndex);
                        }
                    }
                }
                calibrationCount = static_cast<std::int64_t>(stats.SyncRefreshCount);
                calibrationQpc = syncQpc;
            }
        }
        const auto submitQpcNow = QpcNow();
        if (lastSubmitQpc != 0)
            cpuIntervals.values.push_back(static_cast<double>(submitQpcNow - lastSubmitQpc) * 1000.0 / qpcFrequency);
        lastSubmitQpc = submitQpcNow;
        ++frameIndex;
        nextDueTicks += mediaPeriodTicks;
    }

    result.cpuMeanMs = cpuIntervals.Mean();
    result.cpuMedianMs = cpuIntervals.Quantile(0.5);
    result.cpuMaxMs = cpuIntervals.Quantile(1.0);
    result.presentWaitMeanMs = presentWaits.Mean();
    if (gridKnown && refreshPeriodQpc > 0)
        result.calibratedHz = static_cast<double>(qpcFrequency) / refreshPeriodQpc;
    if (allZeroStats) result.note += " 统计全零⇒该链不是翻转链，本臂读数作废";
    if (disjointSeen && presentRefreshes.empty()) result.note += " 只拿到 DISJOINT，没有统计";

    // 采样是时间序，逐对相减即"上一帧在屏上占了几次刷新"。
    // 但先剔两类不是停留读数的样本：首个非零计数之前的 0（还没锁存过），
    // 以及计数器不连续（差分 >30，相当于整段序列被重置）。
    std::size_t firstValid = 0;
    while (firstValid < presentRefreshes.size() && presentRefreshes[firstValid] == 0) ++firstValid;
    for (std::size_t i = firstValid + 1; i < presentRefreshes.size(); ++i) {
        const auto delta = presentRefreshes[i] - presentRefreshes[i - 1];
        if (delta > 30) { ++result.discontinuities; continue; }
        if (delta == 0) { ++result.sameVblank; continue; }
        result.holds.push_back(static_cast<int>(delta));
    }
    if (presentRefreshes.size() >= 2) {
        result.totalAdvance = presentRefreshes.back() - presentRefreshes[firstValid];
        result.expectedAdvance = static_cast<std::int64_t>(declaredHz * seconds + 0.5);
    }

    const std::size_t skip = std::min<std::size_t>(10, result.holds.size());
    std::vector<int> steady(result.holds.begin() + static_cast<std::ptrdiff_t>(skip), result.holds.end());
    // 前置判据：零差分占大头就意味着这条链没在屏上，后面的"停留几格"全是假话。
    const auto zeroDominated = result.sameVblank > result.holds.size();
    if (zeroDominated)
        result.note += " 零差分反超有效停留样本 ⇒ 窗口未被合成，本臂显示侧不作数";
    if (steady.size() >= 120 && !zeroDominated) {
        result.judged = true;
        result.samples = steady.size();
        const int period = 5;
        std::size_t best = steady.size() + 1;
        for (int phase = 0; phase < period; ++phase) {
            std::size_t deviations = 0;
            for (std::size_t i = 0; i < steady.size(); ++i) {
                const double index = static_cast<double>(i + static_cast<std::size_t>(phase));
                const int ideal = static_cast<int>(std::llround((index + 1) * ratio) - std::llround(index * ratio));
                if (steady[i] != ideal) ++deviations;
            }
            if (deviations < best) { best = deviations; result.bestPhase = phase; }
        }
        result.deviations = best;
        result.deviationFraction = static_cast<double>(best) / steady.size();
        result.red = result.deviationFraction > EnvDouble("DGP_JUDDER_MAX_FRAC", 0.05);
    }
    target->Release(); chain->Release();
    return result;
}

}   // namespace

int main() {
    const auto seconds = EnvDouble("DGP_SECONDS", 6.0);
    const auto arms = EnvString("DGP_ARMS", "ABC");
    const auto mediaFps = EnvDouble("DGP_MEDIA_FPS", 60.0);
    const auto leadMs = EnvDouble("DGP_LEAD_MS", 2.0);

    DEVMODEW mode{}; mode.dmSize = sizeof(mode);
    int declaredHz = 60;
    if (EnumDisplaySettingsW(nullptr, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 0)
        declaredHz = mode.dmDisplayFrequency;

    std::printf("=== 呈现网格探针 ===\n");
    std::printf("条件：EnumDisplaySettings=%d Hz · 内容=%.1f fps(周期 %.4fms) · r=%.4f 刷新/帧 · "
        "链=FLIP_DISCARD/3缓冲/BGRA8/MaxFrameLatency=2/带 ALLOW_TEARING 能力(同产品:2892-2901,2955)\n",
        declaredHz, mediaFps, 1000.0 / mediaFps, static_cast<double>(declaredHz) / mediaFps);
    std::printf("对照：产品 DetectDisplayRefreshRate 把 144 先 clamp 到 120（VideoRenderer.cpp:62），"
        "而该值只用于字幕层节奏，视频路径不用它\n");

    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc; wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"display-grid"; wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    RegisterClassW(&wc);
    // 合成前置（2026-10-15，第一版就栽在这里）：窗口必须**真在被 DWM 合成**。
    // 被遮挡时这条翻转链的帧统计根本不推进 —— 实测留下 421 次零差分加 2 次几百
    // 刷新的跳变，看着像"帧从没上过屏"，其实是窗口没在屏上。所以这里置顶，
    // 并且下面把"零差分占比"当成前置判据：占比过高 ⇒ 整臂记未判，不记绿。
    HWND window = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, L"display-grid",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720,
        nullptr, nullptr, wc.hInstance, nullptr);
    if (window == nullptr) { std::printf("建窗失败\n"); return 2; }
    ShowWindow(window, SW_SHOWNORMAL);
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    BringWindowToTop(window);
    SetForegroundWindow(window);

    ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr; D3D_FEATURE_LEVEL level{};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context))) {
        std::printf("D3D11CreateDevice 失败\n"); return 2;
    }

    std::vector<ArmResult> results;
    for (char name : arms) {
        auto result = RunArm(name, device, context, window, seconds, mediaFps, declaredHz, leadMs);
        std::printf("\n--- 臂 %c (%s) ---\n", name,
            name == 'A' ? "产品同形：媒体时钟+2ms提前容差+Present(1,0)" :
            name == 'B' ? "网格锁：v空白标定后把提交钉到目标v空白前" : "完全无锁：Present(0)");
        if (!result.built) { std::printf("  未建成：%s\n", result.note.c_str()); results.push_back(result); continue; }
        std::printf("  CPU 提交间隔：均值=%.2f 中位=%.2f 最大=%.2f ms｜Present 等待均值=%.2fms\n",
            result.cpuMeanMs, result.cpuMedianMs, result.cpuMaxMs, result.presentWaitMeanMs);
        std::printf("  面板周期标定：%.3f Hz%s 样本=%zu\n", result.calibratedHz,
            result.calibratedHz > 0 ? "" : "（未标定）", result.holds.size());
        std::printf("  计数器对账：PresentRefreshCount 实际推进=%lld，按声明 %.0fHz×%.1fs 应有≈%lld"
            "（差 %.1f%%）｜同 v 空白内被覆盖的帧=%zu｜计数器不连续样本=%zu\n",
            static_cast<long long>(result.totalAdvance), static_cast<double>(declaredHz), seconds,
            static_cast<long long>(result.expectedAdvance),
            result.expectedAdvance > 0 ? std::fabs(result.totalAdvance - result.expectedAdvance) * 100.0 /
                static_cast<double>(result.expectedAdvance) : 0.0,
            result.sameVblank, result.discontinuities);
        if (!result.holds.empty()) {
            std::printf("  每帧停留刷新数 前 20 帧：");
            for (std::size_t i = 0; i < std::min<std::size_t>(20, result.holds.size()); ++i) std::printf("%d ", result.holds[i]);
            std::printf("\n  停留直方图：");
            std::vector<int> counts(20, 0);
            for (const auto h : result.holds) if (h >= 0 && h < 20) ++counts[h];
            for (std::size_t h = 0; h < counts.size(); ++h) if (counts[h]) std::printf("%zu刷新=%d ", h, counts[h]);
            std::printf("\n");
        }
        if (result.judged)
            std::printf("  理想{2,2,2,3,3}偏离：偏差 %zu/%zu = %.1f%%（最优相位=%d）\n",
                result.deviations, result.samples ? result.samples : result.holds.size(),
                result.deviationFraction * 100.0, result.bestPhase);
        else
            std::printf("  判据：不可判（稳态样本不足 120）%s\n", result.note.empty() ? "" : ("·" + result.note).c_str());
        results.push_back(result);
    }

    int red = 0, judged = 0, notJudged = 0;
    for (const auto& result : results) {
        if (!result.built) { std::printf("[红] 臂 %c 未建成 ⇒ 该臂不可信\n", result.arm); ++red; continue; }
        if (!result.judged) { ++notJudged; continue; }
        ++judged;
        if (result.red) { std::printf("[红] 臂 %c：停留刷新数偏离理想序列 %.1f%% ⇒ 节律未被锁住\n",
            result.arm, result.deviationFraction * 100.0); ++red; }
        else { std::printf("[绿] 臂 %c：偏离 %.1f%% ⇒ 与理想模式一致\n", result.arm, result.deviationFraction * 100.0); }
    }
    std::printf("\nGRID RESULT: %s（判 %d / 红 %d / 未判 %d）\n",
        red > 0 ? "FAIL" : (judged > 0 ? "PASS" : "NOT-JUDGED"), judged, red, notJudged);
    context->Release(); device->Release();
    return red > 0 ? 1 : (judged > 0 ? 0 : 3);
}
