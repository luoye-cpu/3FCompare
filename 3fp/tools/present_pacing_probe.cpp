// ============================================================================
// present_pacing_probe.cpp —— 呈现帧率上限层的验收探针（2026-10-16 语义改版）
//
// 这一层现在只做一件事：**产出一个上限**，并且**只在有新画面时翻一帧**。
//   上限 = min(Windows 声明的显示器刷新率, 该媒体在 open 时算出的帧率上界)
//   媒体上界取 FFmpeg 的 AVStream::r_frame_rate（只会偏松、不会偏低的上界，不追求精确峰值），
//   每次 open 算一次，整个观察窗内是一个常数。
//
// 探针要能判红的三件事，每件都要有牙：
//   ① DetectDisplayRefreshRate 不再把面板 clamp 到 120（144Hz 曾被报成 120）
//   ② 上限在岗：min(声明刷新率, 媒体帧率上界) 算得对、整个窗口恒定、提交不早于 1/上限
//   ③ G-SYNC/FreeSync（enableTearing=1）时主动让位——可变刷新下面板自己配速，
//      再钉上限只会加延迟；但**内容那一半的上限仍然生效**
//
// 与内核同步改版：ABI 不变（字段名与结构体布局都不动），但三处字段的语义变了，本探针的
// 判据文案一律按新语义写。旧版那套"刷新网格"的瞄准/提前提交机制已经不存在，
// 这里不判它的任何环节，输出里也不描述它。语义映射：
//   · lastSlotError100ns：原「对目标槽位的带符号偏差」→ 现「对上限节奏的偏差」
//     = 本次提交时刻 −（上一次提交时刻 + 上限周期），仍按一个刷新周期归一。负 = 早提交
//     （比上限允许的更快），正 = 晚提交。
//   · gridLocked：原「翻帧被钉在网格上」→ 现「上限在岗」。
//   · pacingBypass 的 2：原「网格未标定」→ 现「上限不可用（无输出窗口或取不到声明刷新率）」；
//     1 仍是「可变刷新路请求」，3 仍是「原生窗口移动循环持有本次节律」。
//   · measuredRefreshHz 仍在（交换链帧统计反解出的实测面板周期），但它**不再是上限的操作数**：
//     上限只能用声明值，因为 VRR 一开实测周期就等于内容周期，min(面板,内容) 会自指。
//
// 关键判据口径的变化：内容的快慢段**不再从 frameRateBoundHz 观测**（它现在是常数），
// 只从「实测呈现速率」观测——轮询 snapshot 的 presentedVideoFrames 对墙上时间做滑窗差分，
// 再做平台段检测。见下面的 WindowedRates()。
//
// 为什么必须有这条探针：FFF3FP_SetPresentConfig 的返回值早就被证明不能当判据
//   （docs/03 A8：它只说明交换链"接受了请求"）。上限层同样看不见——它生效的时候
//   画面是平的，它失效的时候画面也是平的，差别只在每一帧停留了几次刷新、
//   以及每一秒真换了几帧。
//
// 采样前提（2026-10-15 的教训）：窗口**必须真的在被合成**。早先的夹具因为
//   窗口被遮挡，读到 421 次零差分 + 2 次几百刷新的计数器跳变——遮挡时 DWM 根本不动
//   这条链的计数。所以这里显式置顶窗口，并把"实测面板周期标出来了没有"本身当成前置判据：
//   measured 一直是 0 ⇒ 本臂没在合成，显示侧判据全部记未判，而不是记绿。
//
// 用法：present_pacing_probe '<绝对媒体路径>' [秒数]
//   PCP_TEARING=1      走 VRR/撕裂路（判 gridLocked 必须为 0 且 bypass=1，另加上限仍生效）
//   PCP_CONTENT_FPS=N  内容标称帧率，用来预测"上限 = min(声明刷新率, 媒体帧率上界)"该是多少
//                      （不设则该条判据记未判；60fps 片配 144Hz 面板应得 60，
//                       240fps 片配同一面板应得 144 —— 两个方向都必须是活的）
//   PCP_POLL_MS=1      轮询间隔
//   PCP_VFR_EARLY_HZ=N / PCP_VFR_LATE_HZ=N
//                      可变帧率臂：声明这条媒体**前后两段的各自速率**（配
//                      samples/media/vfr/12a_vfr_25then60.* ⇒ 25 与 60）。开了这两个
//                      变量就改判 ⑥ 那组：**实测呈现速率**必须逐段跟上该段内容率，
//                      并且不得越过 open 时产出的上限；同时不得停在整片平均
//                      （那是 avg_frame_rate 那条路的特征签名）。
//                      注意本臂以前判的是「bound 逐段变」，现在判的是「bound 必须恒定、
//                      速率逐段变」——这是有意的语义翻转，不是回归，见 ⑥ 的注释。
//                      用平台段（连续 ≥0.5s 读数一致）而不是"上下半场均值"来判，
//                      因为采样时刻与媒体位置之间有 0.6s 预热偏移，拿墙上时间切两半
//                      会把边界算错——平台段判据对边界不敏感。
//   ⚠ Windows 路径必须单引号；相对路径内核会拒。
// 编译：py 3fp/tools/build_probe.py present_pacing_probe
// 退出码：0=判且绿 · 1=有红 · 2=建窗/Create/Open 失败 · 3=不可判
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

int g_pollMs = 1;
std::atomic<bool> g_openCompleted{ false };

int EnvInt(const char* name, int fallback) {
    const auto raw = std::getenv(name);
    if (raw == nullptr) return fallback;
    const auto value = std::atoi(raw);
    return value > 0 ? value : fallback;
}

void __cdecl OnEvent(void*, FFF3FPEvent event, const char*) {
    if (event == FFF3FPEvent::OpenCompleted) g_openCompleted.store(true);
}

std::int64_t Now100ns() {
    LARGE_INTEGER v{}; QueryPerformanceCounter(&v);
    static const LARGE_INTEGER f = [] { LARGE_INTEGER x{}; QueryPerformanceFrequency(&x); return x; }();
    return v.QuadPart * 10'000'000 / f.QuadPart;
}

void PumpFor(HWND hwnd, int ms) {
    const auto end = Now100ns() + ms * 10'000;
    while (Now100ns() < end) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    (void)hwnd;
}

FFF3FPSnapshot ReadSnapshot(FFF3FPHandle player) {
    FFF3FPSnapshot s{}; s.size = sizeof(s); s.version = 20;
    FFF3FP_GetSnapshot(player, &s);
    return s;
}

FFF3FPPresentPacingStatus ReadPacing(FFF3FPHandle player) {
    FFF3FPPresentPacingStatus p{}; p.size = sizeof(p); p.version = 20;
    FFF3FP_GetPresentPacingStatus(player, &p);
    return p;
}

// ---- 平台段：内容段的观测都改走这条路 ----
// frameRateBoundHz 现在是一个 per-媒体的常数（open 时算一次），所以"内容率换了档"这件事
// 在它身上**读不出来**，只能从实际翻帧速率上读。下面这两套工具同时服务两个序列：
//   · boundSeries：上限自身，用来判"窗口内恒定"（用 min/max，不用平台段）；
//   · rateSeries：实测呈现速率，用来判"快慢段被跟随"（用平台段）。
// 按段判而不是按整片平均判这一点仍然成立且更必要了：整片平均把 25/60 两段糊成 42.5，
// 而 42.5 恰好就是"走容器 avg_frame_rate 那条路"应当产生的数——用平均数当尺子，
// 错实现和正实现会一起通过。
struct RateSample { double tS; double hz; };
struct Plateau { double startS; double durationS; double meanHz; std::size_t count; };

// tolHz 是量化步长的兜底容差（差分窗口 W 给出的最小非零增量是 1/W）。窗口取 0.5s 时
// 步长 2Hz，对 20/25 这种低段率已经大于 5% 相对容差，不给兜底平台段永远聚不出来。
std::vector<Plateau> FindPlateaus(const std::vector<RateSample>& samples, double tolHz) {
    std::vector<Plateau> out;
    for (const auto& s : samples) {
        if (!out.empty()) {
            auto& last = out.back();
            if (std::fabs(s.hz - last.meanHz) <= std::fmax(last.meanHz * 0.05, tolHz)) {
                last.meanHz = (last.meanHz * last.count + s.hz) / (last.count + 1);
                ++last.count;
                last.durationS = s.tS - last.startS;
                continue;
            }
        }
        out.push_back(Plateau{s.tS, 0.0, s.hz, 1});
    }
    return out;
}

const Plateau* FindPlateau(const std::vector<Plateau>& plateaus, double wantHz, double minS,
                           double tolHz) {
    if (wantHz <= 0.0) return nullptr;
    const auto tol = std::fmax(wantHz * 0.05, tolHz * 1.5);
    for (const auto& p : plateaus)
        if (p.durationS >= minS && std::fabs(p.meanHz - wantHz) <= tol) return &p;
    return nullptr;
}

// ---- 实测呈现速率：presentedVideoFrames 对墙上时间的滑窗差分 ----
// 采样起点与媒体位置之间有 ~0.6s 预热偏移（Play 之后先 PumpFor(600) 才取起点），
// 所以只能用平台段判"这一段稳在哪个速率"，不能拿墙上时间切上下半场取均值。
// 步长 0.1s（≈每 100ms 出一个读数），窗宽 0.5s：窗宽不跟步长取 0.1s 是因为 0.1s 窗在
// 25Hz 上的量化步长就是 10Hz，比 25/60 段的判据容差还粗，平台段永远聚不出来；
// 0.5s 窗把步长压到 2Hz，仍远小于段间差。窗口重叠（0.5s 窗、0.1s 步）顺带把差分噪声抹平。
struct FrameStamp { double tS; double frames; };

std::vector<RateSample> WindowedRates(const std::vector<FrameStamp>& raw,
                                       double windowS, double strideS) {
    std::vector<RateSample> out;
    std::size_t j = 0;
    for (std::size_t i = 0; i < raw.size(); ++i) {
        const auto t = raw[i].tS;
        if (t < windowS) continue;
        if (!out.empty() && t - out.back().tS < strideS) continue;
        while (j + 1 < raw.size() && raw[j + 1].tS <= t - windowS) ++j;
        const auto dt = t - raw[j].tS;
        if (dt < windowS * 0.5) continue;
        out.push_back(RateSample{t, (raw[i].frames - raw[j].frames) / dt});
    }
    return out;
}

// 中位数：上限现在应当只有一个值，取中位数而不是取峰值，是为了让"窗口内取 max"这种
// 悄悄把抖动放大成读数的写法没有藏身处（峰值只在一个采样里出现过也算峰值）。
double Median(std::vector<double> v) {
    if (v.empty()) return 0.0;
    std::nth_element(v.begin(), v.begin() + v.size() / 2, v.end());
    return v[v.size() / 2];
}

}   // namespace

int main(int argc, char** argv) {
    // 无缓冲：这台机器上这个臂实测会间歇性地在一条 stderr（"can't open file for write !"）
    // 之后一声不响地结束，而 stdout 是块缓冲的 —— 那样连"=== 呈现帧率上限验收 ==="都看不到，
    // 无法区分"没建起来"和"建起来后死了"。留这条是为了下次能判。
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { std::printf("用法：present_pacing_probe '<绝对媒体路径>' [秒数]\n"); return 2; }
    const std::string path = argv[1];
    const double seconds = argc > 2 ? std::atof(argv[2]) : 8.0;
    if (const char* env = std::getenv("PCP_POLL_MS")) g_pollMs = std::clamp(std::atoi(env), 1, 100);
    const auto tearing = std::atoi(std::getenv("PCP_TEARING") ? std::getenv("PCP_TEARING") : "0");
    // 内容标称帧率：用来预测内核应当自动算出的上限 min(声明刷新率, 媒体帧率上界)。
    //   注意媒体帧率上界取 r_frame_rate，只会偏松不会偏低，所以这条预测在"上界报得比
    //   标称率还高"的件上会偏——那种红先查件的 r_frame_rate，别直接算给上限层。
    //   不设就没有可预测的上限，那条判据记未判而不是猜。
    const auto contentFps = EnvInt("PCP_CONTENT_FPS", 0);
    // 可变帧率臂：两段的各自速率。两个都给了才开，只给一个等于没声明分段 ⇒ ⑥ 记未判。
    const auto vfrEarly = EnvInt("PCP_VFR_EARLY_HZ", 0);
    const auto vfrLate = EnvInt("PCP_VFR_LATE_HZ", 0);
    const bool vfrArm = vfrEarly > 0 && vfrLate > 0;

    WNDCLASSW wc{};
    wc.lpfnWndProc = [](HWND h, UINT m, WPARAM a, LPARAM b) -> LRESULT {
        if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(h, m, a, b);
    };
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"presentPacingWindow";
    RegisterClassW(&wc);
    HWND hwnd = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, L"present-pacing",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720,
        nullptr, nullptr, wc.hInstance, nullptr);
    if (hwnd == nullptr) { std::printf("建窗失败\n"); return 2; }
    // 合成前置条件：可见、置顶、不在最小化状态。遮挡会让链的帧统计冻结，
    // 那会让所有显示侧读数变成假话，所以这一步不是美化，是判据成立的前提。
    ShowWindow(hwnd, SW_SHOWNORMAL);
    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    BringWindowToTop(hwnd);
    SetForegroundWindow(hwnd);
    if (!IsWindowVisible(hwnd)) { std::printf("窗口不可见，本臂不能判显示侧\n"); return 2; }

    // 面板声明值由探针独立量一次，用来判 ① clamp 有没有真的去掉。
    // 必须走和内核同一个算法（MonitorFromWindow + EnumDisplaySettingsExW），
    // 否则两边量的不是同一块屏。
    int probeDeclaredHz = 0;
    if (const auto monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)) {
        MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);
        if (GetMonitorInfoW(monitor, &mi)) {
            DEVMODEW mode{}; mode.dmSize = sizeof(mode);
            if (EnumDisplaySettingsExW(mi.szDevice, ENUM_CURRENT_SETTINGS, &mode, 0))
                probeDeclaredHz = mode.dmDisplayFrequency;
        }
    }

    FFF3FPConfiguration config{};
    config.size = sizeof(config);
    config.version = FFF3FP_GetApiVersion();
    config.outputWindow = hwnd;
    config.decodeMode = FFF3FPDecodeMode::Cpu;
    config.colorMode = FFF3FPColorMode::MapToSdr;
    config.sdrPeakNits = 100.0f;
    config.hdrPeakNits = 0.0f;
    config.sdrPaperWhiteNits = 203.0f;
    config.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    config.forceHdrOutput = 0u;
    config.preferredAdapterIndex = -1;
    config.eventCallback = &OnEvent;
    // 上限没有配置面入口，探针也不在这里加字段：见 FFF.Player.Api.h 里 FFF3FPConfiguration
    // 尾部那条注释——配置面加字段会把 sizeof 加宽，而 FFF3FP_Create 对 size 偏小的宿主
    // 直接判 InvalidArgument，那会让没同步重编的宿主连会话都建不起来。

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&config, &player) != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败\n"); return 2;
    }
    std::printf("=== 呈现帧率上限验收 ===\n");
    // 一栏一名：这装的是"内容率预期"（用来预测上限的媒体那一半），不是任何上限设置——
    // 上限在内核里恒取 min(声明刷新率, 媒体帧率上界)，没有可写入口。
    std::printf("file=%s 观察=%.1fs 轮询=%dms 臂: tearing=%d 内容率预期=%d 可变帧率段=%d/%d\n",
        path.c_str(), seconds, g_pollMs, tearing, contentFps, vfrEarly, vfrLate);
    std::printf("探针独立量到的面板声明值=%d Hz（判 ① 与所有上限判据用的都是它）\n", probeDeclaredHz);
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) { std::printf("Open 失败\n"); return 2; }
    for (int i = 0; i < 400 && !g_openCompleted.load(); ++i) PumpFor(hwnd, 25);
    if (tearing) FFF3FP_SetPresentConfig(player, 1);
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    PumpFor(hwnd, 600);   // 让首帧落地，并给出两次交换链帧统计（实测面板周期要两个采样才反解得出）

    const auto baseline = ReadSnapshot(player);
    const auto baselinePacing = ReadPacing(player);
    const auto baselineDisplayDiscards = baselinePacing.displayRateDiscardedFrames;
    const auto start100ns = Now100ns();
    double maxMeasuredHz = 0.0;          // 实测面板周期（整窗峰值）——只是观测量，不是上限操作数
    std::vector<double> measuredSeries;   // 逐轮实测周期；② 判一致性用中位，不用峰值
    double minBoundHz = 0.0, maxBoundHz = 0.0;   // 上限在窗口里的取值范围；恒定 ⇒ 两者相等
    std::uint32_t zeroBoundSamples = 0;          // 上限中途塌回 0 的次数（=上限掉岗）
    std::uint32_t sawLocked = 0, sawUnlocked = 0;
    std::uint32_t lastBypass = 0;
    std::size_t bypassTwoSamples = 0;            // bypass==2：上限不可用（新语义）
    std::vector<double> boundPeriodError;        // |对上限节奏的偏差|，单位=一个刷新周期
    std::vector<double> boundPeriodSigned;       // 同上，带符号：负=早提交（快过上限）
    std::vector<RateSample> boundSeries;         // 每轮采一条上限读数（全臂，不再只给 VFR）
    std::vector<FrameStamp> presentTimeline;     // 每轮采一条呈现帧计数，供滑窗差分出实测速率

    while (Now100ns() - start100ns < static_cast<std::int64_t>(seconds * 10'000'000)) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        const auto pacing = ReadPacing(player);
        const auto snap = ReadSnapshot(player);
        const auto tS = (Now100ns() - start100ns) / 10'000'000.0;
        maxMeasuredHz = std::max(maxMeasuredHz, static_cast<double>(pacing.measuredRefreshHz));
        if (pacing.measuredRefreshHz > 0.0f)
            measuredSeries.push_back(static_cast<double>(pacing.measuredRefreshHz));
        const auto bound = static_cast<double>(pacing.frameRateBoundHz);
        if (bound > 0.0) {
            if (minBoundHz == 0.0) minBoundHz = bound;
            maxBoundHz = std::max(maxBoundHz, bound);
            minBoundHz = std::min(minBoundHz, bound);
            boundSeries.push_back(RateSample{tS, bound});
        } else {
            ++zeroBoundSamples;   // 上限读成 0：这一层没产出（无输出窗口/取不到声明刷新率时走 bypass=2）
        }
        presentTimeline.push_back(FrameStamp{tS, static_cast<double>(snap.presentedVideoFrames)});
        if (pacing.gridLocked) ++sawLocked; else ++sawUnlocked;
        lastBypass = pacing.pacingBypass;
        if (pacing.pacingBypass == 2u) ++bypassTwoSamples;
        if (pacing.measuredRefreshHz > 0.0f && pacing.gridLocked) {
            // 只在上限真的在岗的那些采样上算精度，否则残留读数能把"上限从没在岗"那一臂
            // 判成满分——30fps 上限臂实测就是这样假绿的。
            // 分母是**一个刷新周期**（不是上限周期）：lastSlotError100ns 按定义
            // = 本次提交 −（上次提交 + 上限周期），归一到刷新周期才对得上"早/晚几格"。
            const auto period100ns = 10'000'000.0 / pacing.measuredRefreshHz;
            const auto signedPeriods = static_cast<double>(pacing.lastSlotError100ns) / period100ns;
            boundPeriodError.push_back(std::fabs(signedPeriods));
            boundPeriodSigned.push_back(signedPeriods);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(g_pollMs));
    }

    const auto after = ReadSnapshot(player);
    const auto finalPacing = ReadPacing(player);
    const auto wall100ns = Now100ns() - start100ns;
    const double presented = static_cast<double>(after.presentedVideoFrames - baseline.presentedVideoFrames);
    const double flips = static_cast<double>(after.swapChainPresents - baseline.swapChainPresents);
    const double presentedFps = presented * 10'000'000.0 / wall100ns;
    const double drops = static_cast<double>(after.droppedVideoFrames - baseline.droppedVideoFrames);
    const double displayDrops = static_cast<double>(finalPacing.displayRateDiscardedFrames -
        baselineDisplayDiscards);
    // 显示侧真正到达率。`presentedFps` 数的是"带着新画面交给 Present 的次数"，而其中
    // 被后来者取代、从未合成的那些翻帧根本不改变屏幕——4K60 臂就是被这个区别骗的：
    // 呈现 59.4 fps、提交误差 100% 落在一个周期内，可 475 次翻帧里 193 次没上屏，
    // 屏幕实际只换了 ~35 次/秒，于是"管线跟得上"这句前置把一条真红判成了判据的错。
    const auto neverComposited = static_cast<double>(finalPacing.neverCompositedPresents -
        baselinePacing.neverCompositedPresents);
    const double displayed = flips - neverComposited;
    const double displayedFps = displayed * 10'000'000.0 / wall100ns;

    // ---- 两条新操作数 ----
    // 第一件：上限在窗口里的唯一值。恒定就取中位数（等于 max），不恒定时中位数也仍然是一个
    //    真实出现过的读数，而"整窗取 max"会把一次抖动当成上限本身。
    std::vector<double> boundValues;
    boundValues.reserve(boundSeries.size());
    for (const auto& b : boundSeries) boundValues.push_back(b.hz);
    const double boundHz = Median(boundValues);
    const double boundSpread = (boundHz > 0.0) ? (maxBoundHz - minBoundHz) : 0.0;
    // 阈值：float 回读的舍入 + 内核内部按 100ns 累计的抖动，给到 1% 且不低于 0.5Hz。
    // 方向才是这条设计要判的东西，"恒定"不是：上限是 per-媒体 的初值，而初值来自
    // `r_frame_rate`，那个字段在 MPEG-TS 上会**偏低**（实测 12b_vfr_20then40.ts 报 20/1，
    // 后半段真是 40fps），所以会话看见更短的逐帧间距时只允许把它**上抬**。
    // 因此判"单调不减"：下降 = 上限把自己掐小（真缺陷，也正是这条修复要防的形状）；
    // 上抬台阶 = 看到更快的一段以后纠正（对的，12b.ts 应当恰好抬一次 20→40）。
    const double boundTol = std::fmax(0.5, boundHz * 0.01);
    std::size_t boundDownSteps = 0, boundUpSteps = 0;
    for (std::size_t i = 1; i < boundSeries.size(); ++i) {
        if (boundSeries[i].hz + boundTol < boundSeries[i - 1].hz) ++boundDownSteps;
        else if (boundSeries[i].hz > boundSeries[i - 1].hz + boundTol) ++boundUpSteps;
    }
    const bool boundMonotone = boundHz > 0.0 && zeroBoundSamples == 0 &&
        boundSeries.size() >= 40 && boundDownSteps == 0;
    // 第二件：实测呈现速率序列（presentedVideoFrames 差分 ÷ 墙上时间，0.5s 窗 / 0.1s 步）。
    //    内容的快慢段今后只能从这一条上看。
    const double rateWindowS = 0.5;
    const double rateStrideS = 0.1;
    const double rateQuantHz = 1.0 / rateWindowS;   // 差分窗给出的最小非零增量
    const auto rateSeries = WindowedRates(presentTimeline, rateWindowS, rateStrideS);
    const auto ratePlateaus = FindPlateaus(rateSeries, rateQuantHz);
    double peakRateHz = 0.0;
    for (const auto& r : rateSeries) peakRateHz = std::max(peakRateHz, r.hz);
    // 面板参考值一律用**声明**刷新率：上限的新操作数就是声明值，measured 只剩观测量。
    // 探针独立量的那个整数（probeDeclaredHz）优先，避免拿被测读数给自己出题；
    // 内核报的 declaredRefreshHz 与它相等这件事由 ① 那条判据负责。
    const double panelHz = probeDeclaredHz > 0 ? static_cast<double>(probeDeclaredHz)
        : static_cast<double>(finalPacing.declaredRefreshHz);

    // ② 用的统计量：中位而不是整窗峰值（峰值把一次坏样本当结论，理由写在判据旁边）。
    const double medianMeasuredHz = measuredSeries.empty() ? 0.0 : Median(measuredSeries);

    std::printf("\n--- 读数 ---\n");
    std::printf("  内核报的声明刷新率=%.2f Hz｜探针量的=%d Hz\n",
        finalPacing.declaredRefreshHz, probeDeclaredHz);
    std::printf("  实测面板周期（峰值=%.2f｜中位=%.2f，只是观测量、不进上限）｜"
        "末次 gridLocked=%u（新语义：上限在岗）bypass=%u（轮询里 在岗=%u 次/不在岗=%u 次）\n",
        maxMeasuredHz, medianMeasuredHz, finalPacing.gridLocked, finalPacing.pacingBypass,
        sawLocked, sawUnlocked);
    std::printf("  上限读数：中位=%.2f Hz｜窗口内范围=[%.2f, %.2f] 跨度=%.2f Hz｜"
        "上抬台阶=%zu 次 下降=%zu 次（阈值 %.2f，判的是单调不减）｜"
        "塌回 0 的采样=%u 次｜bypass=2（上限不可用：无输出窗口或取不到声明刷新率）采样=%zu 次\n",
        boundHz, minBoundHz, maxBoundHz, boundSpread, boundUpSteps, boundDownSteps, boundTol,
        zeroBoundSamples, bypassTwoSamples);
    std::printf("  呈现帧=%.0f（%.1f fps）翻帧=%.0f｜翻帧/呈现帧=%.3f｜"
        "显示侧到达=%.0f 帧（%.1f fps，从未上屏=%.0f＝%.1f%%）\n",
        presented, presentedFps, flips, presented > 0 ? flips / presented : 0.0,
        displayed, displayedFps, neverComposited,
        flips > 0 ? neverComposited * 100.0 / flips : 0.0);
    std::printf("  实测呈现速率（%.2fs 窗/%.2fs 步差分）：样本=%zu 峰值=%.1f Hz｜平台段 %zu 条：",
        rateWindowS, rateStrideS, rateSeries.size(), peakRateHz, ratePlateaus.size());
    for (const auto& p : ratePlateaus)
        std::printf("[起 %.2fs 长 %.2fs @ %.1fHz] ", p.startS, p.durationS, p.meanHz);
    std::printf("\n");
    // 合并帧这一栏是为"屏幕停留带内比例"那条判据准备的：一次呈现跳过了 k 个更新过的
    // 画面，被显示的那一帧就要多停 k 个源帧的时长，于是 ⌊r⌋/⌈r⌉ 带外必然出现——
    // 这是管线合并，不是上限失守。两个数不并排列，就没法把这两种成因分开。
    std::printf("  合并帧（呈现时跳过的更新次数）=%.0f｜解码帧=%.0f\n",
        static_cast<double>(after.coalescedVideoFrames - baseline.coalescedVideoFrames),
        static_cast<double>(after.decodedVideoFrames - baseline.decodedVideoFrames));
    // 两类丢弃并排列出，各自写明原因——它们含义不同，混在一个数里就会让人去查一个
    // 根本不存在的性能问题。原因那句只在条件真的成立时才印，否则就是在写一句假话
    // （上一版无条件印"面板 144 Hz < 片源 60 fps"，那个不等式本身是反的）。
    const bool panelIsTheLimit = panelHz > 0.0 && static_cast<double>(contentFps) > panelHz * 1.005;
    std::printf("  丢弃：管线跟不上媒体时钟（传统口径）=%.0f｜因显示器帧率 < 视频帧率而丢弃=%.0f｜合计=%.0f\n",
        drops, displayDrops, drops + displayDrops);
    if (panelIsTheLimit)
        std::printf("    ↑ 显示速率那一栏的成因：片源 %d fps 快过声明刷新率 %.2f Hz，"
            "面板就是上限，缩画面或加线程都改变不了\n", contentFps, panelHz);
    else if (contentFps > 0)
        std::printf("    （片源 %d fps 不高于声明刷新率 %.2f Hz，本臂不该有显示速率丢弃；"
            "两栏都是 0 才对）\n", contentFps, panelHz);
    // 眼睛看到的节律在这一行：内核自己数的"这一帧在屏上真停了几个刷新周期"。
    // 带由「每帧内容周期 ÷ 声明面板周期」算出（⌊r⌋~⌈r⌉），提交精度只算半边，这条才是结果。
    const double holdSampled = static_cast<double>(finalPacing.holdSampled);
    std::printf("  屏幕停留对账：可测=%.0f 落在 ⌊r⌋/⌈r⌉ 带内=%.0f（%.1f%%）带外最差偏=%llu 格｜"
        "从未上屏翻帧=%llu（停留判带按累计 ratio 结算，这个数就是被并掉的那些翻帧；"
        "预期带 = 每帧内容周期 ÷ 声明面板周期）\n",
        holdSampled, static_cast<double>(finalPacing.holdWithinRounding),
        holdSampled > 0 ? finalPacing.holdWithinRounding * 100.0 / holdSampled : 0.0,
        static_cast<unsigned long long>(finalPacing.holdWorstDeviationRefreshes),
        static_cast<unsigned long long>(finalPacing.neverCompositedPresents));
    if (!boundPeriodError.empty()) {
        std::size_t withinOne = 0, markedlyEarly = 0;
        double worst = 0.0, sumSigned = 0.0;
        for (const auto e : boundPeriodError) { if (e <= 1.0) ++withinOne; worst = std::max(worst, e); }
        for (const auto e : boundPeriodSigned) {
            sumSigned += e;
            if (e <= -0.5) ++markedlyEarly;   // 比上限允许的还快半个周期以上
        }
        const auto meanSigned = sumSigned / static_cast<double>(boundPeriodSigned.size());
        // 读法（新语义）：这一栏量的是「本次提交 −（上次提交 + 上限周期）」÷一个刷新周期。
        // 均值≈0 且早提交占比≈0 ⇒ 提交节奏贴在上限上；均值明显为负 ⇒ 提交快过 1/上限，
        // 也就是这一层没把上限当上限；均值明显为正 ⇒ 上限没在岗，是吞吐的账。
        std::printf("  上限节奏误差（单位=一个刷新周期）：样本=%zu 落在一格内=%.1f%% 最差=%.2f｜"
            "带符号均值=%.2f｜明显早提交（≤ −0.5 格）=%.1f%%\n",
            boundPeriodError.size(), withinOne * 100.0 / boundPeriodError.size(), worst, meanSigned,
            markedlyEarly * 100.0 / boundPeriodError.size());
    }

    std::printf("  媒体时钟在这扇窗里走了 %.2fs（观察窗 %.2fs）｜呈现帧=%.0f\n",
        std::max(0.0, static_cast<double>(after.position100ns - baseline.position100ns)
            / 10'000'000.0), seconds, presented);
    int judged = 0, red = 0, undecided = 0;
    auto Judge = [&](bool evaluable, bool ok, const char* label) {
        if (!evaluable) { ++undecided; std::printf("  [未判] %s\n", label); return; }
        ++judged;
        if (ok) std::printf("  [绿] %s\n", label);
        else { ++red; std::printf("  [红] %s\n", label); }
    };

    // 饥饿前置：实测有一趟整轮只推进了 0.76s 媒体时间、只呈现 19 帧 —— 会话根本没跑到
    // 晚段，那"晚段没换档"这条红就不是内核的账（窗口被遮挡、DWM 不采样都造得出这个形状）。
    // 所以它必须挡住 ⑥，而不是和 ⑥ 一起报红。
    const double mediaAdvancedS = std::max(0.0,
        static_cast<double>(after.position100ns - baseline.position100ns) / 10'000'000.0);
    const bool starved = mediaAdvancedS < seconds * 0.5;
    Judge(true, !starved,
        "前置：会话没被饿住（媒体时钟推进 ≥ 观察窗的一半，否则 ⑥ 没有操作数）");

    const bool calibrated = maxMeasuredHz > 0.0;
    // ① clamp 修掉了没有：内核报的声明值必须等于探针独立量到的那个数。144Hz 面板
    //    上旧实现会报 120，这条就是给它当牙的。
    Judge(probeDeclaredHz > 0,
        std::fabs(finalPacing.declaredRefreshHz - probeDeclaredHz) < 0.5,
        "① 声明刷新率不再被 clamp（内核读数 == 探针独立读数）");
    // 前置：实测面板周期标不出来就意味着窗口没被合成，后面的显示侧判据全部不作数。
    // （measuredRefreshHz 在这里只当"有没有在合成"的存在性探针用，不进任何上限判据。）
    Judge(true, calibrated, "合成前置：交换链帧统计标出了实测面板周期（否则本臂显示侧全是假话）");
    if (!calibrated) {
        std::printf("\nPACING RESULT: NOT-JUDGED（判 %d / 红 %d / 未判 %d）——实测面板周期未标出\n",
            judged, red, undecided);
        FFF3FP_Destroy(player);
        return red > 0 ? 1 : 3;
    }
    // ② 实测面板周期与声明值一致性（±0.5%）。144.00 与 143.99 之差是面板自身振荡，
    //    超过 0.5% 说明我把别的东西当成了刷新周期。注意这条只证"观测量还是面板周期"，
    //    上限不使用它（VRR 一开实测周期就等于内容周期，min(面板,内容) 会自指）。
    //    两处操作数修正是本轮实测逼出来的：
    //      · 用**中位**而不是峰值。同一批里 24fps 臂峰值 144.78、VRR 臂 145.12，而
    //        60/120/240 臂是 144.02~144.15 —— 那条红量的是统计量的尾巴，不是标定坏了。
    //      · 撕裂／VRR 臂不判这条：该臂反解出来的是 DWM 的合成间隔（145.12 对 60fps 内容），
    //        它本来就不是面板率，跟声明值比是问错问题。
    Judge(probeDeclaredHz > 0 && !tearing && measuredSeries.size() >= 40,
        std::fabs(medianMeasuredHz - probeDeclaredHz) / probeDeclaredHz < 0.005,
        "② 实测面板周期（整窗中位）与声明刷新率相差 <0.5%；VRR 臂不判——那里量的是合成间隔");

    // ⑦ 上限是 **per-媒体** 的产物：open 时算一次，整个观察窗内必须只有一个值。
    //    这一条在 VFR 臂上也判，而且判的内容和旧版正好相反——旧版要求 frameRateBoundHz
    //    逐段变（20 然后 40），新版把它换成"初值 per-媒体 + 只许上抬"，"内容的快慢"改由
    //    实测呈现速率那条序列承担。这不是回归，是这一层的语义翻转：上限只是上限。
    //    操作数：boundSeries（逐轮上限读数，内核回读）+ 探针独立量的声明刷新率 +
    //    探针声明的 PCP_CONTENT_FPS。内核那个媒体帧率上界探针读不到（它不是 API 面），
    //    所以判"单调不减 + 不高于两个已知上界 + 不低于最快段"，等号右半边由 ③ 那条判。
    Judge(boundSeries.size() >= 40 && boundHz > 0.0, boundMonotone,
        "⑦ 上限在观察窗内单调不减（per-媒体 初值＋看到更快一段就上抬，下降步数必须为 0；中途不塌回 0）");
    // 越界方向用窗口内**最大值**（该媒体最终稳定下来的上限）而不是中位：上限允许在窗内抬
    // 一次，中位会把两个时期混成一个数，既能藏住越界也会把正确实现读成红。
    Judge(maxBoundHz > 0.0 && panelHz > 0.0, maxBoundHz <= panelHz * 1.005 + 0.5,
        "⑦ 上限不高于 Windows 声明的显示器刷新率（上限只能取声明值，不取实测周期；取窗口内最大值）");
    Judge(maxBoundHz > 0.0 && contentFps > 0, maxBoundHz <= contentFps * 1.02 + 0.5,
        "⑦ 上限不高于探针声明的内容标称帧率（媒体上界那一半的兜底；取窗口内最大值）");

    // ②/VRR 分叉：撕裂路下上限层必须从没在岗且给出 bypass=1，否则"配合 G-SYNC"这句话没有牙。
    if (tearing) {
        Judge(true, sawLocked == 0 && lastBypass == 1,
            "③ VRR 路让位：gridLocked（上限在岗标志）全程为 0 且 bypass=1");
        // 让位让的是"面板那一半"，内容那一半的上限不许跟着一起没了。上限在岗标志为 0
        // 时 lastSlotError 没有操作数，所以这条只能用实测呈现速率判：
        // 逐段速率与整窗速率都不许越过 min(声明刷新率, 内容率)。
        if (contentFps > 0 && panelHz > 0.0) {
            const auto ceiling = std::min(panelHz, static_cast<double>(contentFps));
            Judge(presented > 20, presentedFps <= ceiling * 1.08,
                "③ 无同步路上限仍然生效：整窗实测呈现速率 ≤ min(声明刷新率, 内容率)");
            Judge(rateSeries.size() >= 5, peakRateHz <= ceiling * 1.15,
                "③ 无同步路上限仍然生效：逐段实测呈现速率峰值 ≤ min(声明刷新率, 内容率)×1.15");
            std::printf("  无同步路上限：整窗 %.1f fps、逐段峰值 %.1f Hz 对 min(%.2f, %d) = %.2f Hz\n",
                presentedFps, peakRateHz, panelHz, contentFps, ceiling);
        } else {
            ++undecided;
            std::printf("  [未判] 无同步路上限：没给 PCP_CONTENT_FPS 或声明刷新率，没有上界可预测\n");
        }
        std::printf("  （本臂 gridLocked 恒为 0 ⇒ lastSlotError 没有操作数，上限节奏的两条计时判据"
            "只在同步步上判；这一臂的账由上面那条让位判据加两条无同步路上限判据记）\n");
    } else {
        Judge(true, sawLocked > 0,
            "② 上限在岗过：轮询里出现过 gridLocked=1（gridLocked 的新语义＝上限在岗）");
        std::size_t withinOne = 0, markedlyEarly = 0;
        for (const auto e : boundPeriodError) if (e <= 1.0) ++withinOne;
        for (const auto e : boundPeriodSigned) if (e <= -0.5) ++markedlyEarly;
        const auto fraction = boundPeriodError.empty() ? 0.0 :
            withinOne * 1.0 / boundPeriodError.size();
        const auto earlyFraction = boundPeriodSigned.empty() ? 1.0 :
            markedlyEarly * 1.0 / boundPeriodSigned.size();
        // 这两条都要"管线跟得上"才有资格判。实测同一件 60fps 文件三趟：呈现 42.1／38.5／57.7
        // fps 时提交误差一格内 81.5／67.3／99.0%、停留带内 66.5／48.7／91.3%——两个数跟的都是
        // 吞吐而不是上限层。跟不上还记红，就是让进程调度替内核背锅。
        // 操作数下限原来写死 `presented > 200`：那对 24fps 臂永远不成立（8s 窗口只可能有
        // 192 帧），于是这支明明呈现 24.0fps 对上限 24.0Hz（一分不差）被记成"跟不上"。
        // 改成按窗口算：至少交付"上限在岗时该有的帧数"的一半，既留下 60fps 臂的 discrimination
        // （那三趟 337/462/460 帧对下限 240 仍然靠 fps 那一半判），又不让低内容率臂天生没有操作数。
        const double operandFloor = 0.5 * boundHz * seconds;
        // 两半分开判，因为它们是两笔不同的账：
        //   deliveringFlips —— 翻帧**生产**跟得上上限（跟不上就是解码/调度的账，整机忙也走这支）；
        //   keptUp         —— **屏幕真到达**的速率跟得上，停留判带只有在这之下才有意义。
        // 4K60 臂实测是"生产跟得上、到达跟不上"（475 次翻帧里 193 次没上屏，屏幕只换了
        // ~35 次/秒），所以以前 keptUp 用呈现速率把它误放行，把该由到达率记的红包在停留判据上。
        // 拆开之后那支的停留判据记未判，到达率自己记红——就是下面这条新判据。
        const bool deliveringFlips = boundHz > 0.0 && presentedFps >= 0.95 * boundHz &&
            presented >= operandFloor;
        const bool keptUp = boundHz > 0.0 && displayedFps >= 0.95 * boundHz &&
            displayed >= operandFloor;
        std::printf("  计时判据前置：翻帧生产 %.1f fps、显示到达 %.1f fps 对上限 %.1f Hz 的 95%%"
            "（操作数下限 %.0f 帧）⇒ %s\n",
            presentedFps, displayedFps, boundHz, operandFloor,
            !deliveringFlips ? "生产就没跟上，计时判据记未判（这是吞吐的账）"
                : (keptUp ? "显示侧也跟得上，计时判据生效"
                          : "生产跟得上而显示侧没跟上：停留判据记未判，账由下面那条到达率判据记"));
        Judge(deliveringFlips, keptUp,
            "② 显示侧到达率（翻帧−从未上屏）≥ 95% 上限：生产跟得上时，没上屏的那些就不是调度的账");
        // 上限节奏误差量的是"这一帧的提交有没有落在 1/上限 这个间隔上"，那是生产侧的事，
        // 只要翻帧真的按上限在产就有资格判——显示侧到没到屏不改变这句。4K60 臂正是如此：
        // 提交误差 100% 落在一个周期内（该判绿），而到达率 36.4/60（该由上面那条记红）。
        Judge(deliveringFlips && boundPeriodError.size() >= 40, fraction >= 0.95,
            "② 上限节奏误差：≥95% 的采样落在一个刷新周期内（量的是提交有没有早于 1/上限）");
        // 方向性判据：这一层只锁上限，不逼着慢提交。所以"比上限间隔还快"的那些采样
        // （带符号误差 ≤ −0.5 个刷新周期）应当几乎为 0——这是"上限真的当上限用"最直接的证据，
        // 上一句的两边对称判据放行不了一路抢跑的错实现，这条能。
        Judge(deliveringFlips && boundPeriodSigned.size() >= 40, earlyFraction <= 0.01,
            "② 方向：明显早于上限节奏的提交（≤ −0.5 格）占比 ≤1%——这层只锁上限的直接证据");
        // ③ 没有多次复制：翻帧数不该明显超过呈现帧数。144Hz 面板上若按"UI 跟面板"
        //    实现，这个比值会接近 2.4，正是要避免的性能浪费。
        Judge(presented > 20, flips / presented <= 1.15,
            "③ 无多余复制：翻帧/呈现帧 ≤ 1.15");
        // 结果那半边，取"与对齐无关"的口径：停留格数落在 ⌊r⌋ 或 ⌈r⌉ 之内就算稳，
        // 预期带由「每帧内容周期 ÷ 声明面板周期」给出（60fps 内容配 144Hz ⇒ 2~3 格）。
        // 判"等于某个目标格"试过两种对齐，都是 30~47%，而面板平均恰好 2.4 —— 那说
        // 明问题在两条计数序列的归属上，不在节律本身。带内判据不依赖归属。
        Judge(keptUp && finalPacing.holdSampled >= 100,
            finalPacing.holdWithinRounding * 1.0 / finalPacing.holdSampled >= 0.95 &&
            finalPacing.holdWorstDeviationRefreshes <= 1,
            "② 屏幕停留落在 ⌊r⌋/⌈r⌉ 带内（带=内容周期÷声明面板周期）：≥95% 且带外偏差 ≤1 格");
    }
    // ③ 上限自动取 min(声明刷新率, 媒体帧率上界)：内核自己算，探针只预测不设置。
    //    两个方向都得是活的——60fps 片配 144Hz 面板该给 60，240fps 片配同一面板该给 144。
    if (contentFps > 0 && panelHz > 0.0) {
        const auto expected = std::min(panelHz, static_cast<double>(contentFps));
        Judge(true, std::fabs(boundHz - expected) / expected < 0.02,
            "③ 自动上限 == min(声明刷新率, 媒体帧率上界)");
        std::printf("  上限：内核报（窗口中位）%.2f Hz｜预测 min(%.2f, %d) = %.2f Hz｜"
            "整窗呈现 %.1f fps｜逐段速率峰值 %.1f Hz\n",
            boundHz, panelHz, contentFps, expected, presentedFps, peakRateHz);
        Judge(presented > 20, presentedFps <= expected * 1.08,
            "③ 整窗呈现速率没越过自动上限");
        Judge(rateSeries.size() >= 5, peakRateHz <= expected * 1.15,
            "③ 逐段实测呈现速率峰值也没越过自动上限（段级抢跑不会被整窗平均藏住）");
        // 分账必须真的分得开：内容快于面板时，丢弃要落在"显示速率"那一栏，传统口径得近乎 0。
        // 少了这条，换个名字记同一笔数也能"看起来分开了"。
        if (static_cast<double>(contentFps) > panelHz * 1.005) {
            Judge(true, drops <= 2 && displayDrops > 0,
                "④ 分账真的分叉：快于面板的丢弃全记在显示速率栏，传统迟到口径近乎 0");
            const auto expectedDiscards = (static_cast<double>(contentFps) - panelHz) *
                (wall100ns / 10'000'000.0);
            Judge(true, std::fabs(displayDrops - expectedDiscards) / expectedDiscards < 0.25,
                "④ 显示速率丢弃数 ≈ (片源率 − 声明刷新率) × 观察秒数");
            std::printf("  预期量级：(内容 %d − 声明刷新率 %.2f) × %.1fs ≈ %.0f 帧\n",
                contentFps, panelHz, wall100ns / 10'000'000.0, expectedDiscards);
        }
    } else {
        ++undecided;
        std::printf("  [未判] 自动上限：没给 PCP_CONTENT_FPS 或取不到声明刷新率，无法预测 min(声明刷新率, 媒体帧率上界)\n");
    }

    // ⑥ 可变帧率臂：**实测呈现速率**必须逐段跟上内容率，而且不得停在整片平均上。
    //
    // ⚠ 语义翻转（有意的，不是回归）：这一臂以前判的是 frameRateBoundHz 逐段换档
    //   （25 然后 60）。上限层改版后 bound 是 open 时算出的 per-媒体常数，
    //   在 VFR 文件上它**必须恒定**——那件事由上面 ⑦ 那组判据负责（⑦ 在本臂同样判，
    //   而且正是靠本臂才有意义：只有 VFR 文件能让"逐段重算上限"这种旧实现露出跨度）。
    //   "内容的快慢段被跟随"这件事改由 presentedVideoFrames 的滑窗差分速率来判。
    //
    // 为什么用平台段而不是"上下半场各取均值"：采样起点与媒体位置之间有 600ms 预热偏移
    // （Play 之后才 PumpFor(600) 再取 start100ns），拿墙上时间切两半会把变速边界算歪，
    // 边界那 0.6s 会把两个数互相污染。平台段判"速率有没有在一段连续时间里稳在某个档位"，
    // 对边界位置不敏感。0.5s 的门槛来自这条夹具每档各占约 2s，而差分窗本身又是 0.5s
    // （窗宽决定量化步长 1/W = 2Hz，见 rateQuantHz）。
    //
    // 各条判据挡的错法：只看早段 ⇒ 用第一个数套整片；看平均 ⇒ 走容器 avg_frame_rate；
    // 因此"不许落在整片平均"那条是独立的一根牙，缺了它，"取整片平均"这种实现
    // 能同时通过前两条的一半。
    if (vfrArm) {
        std::printf("\n--- 可变帧率臂（声明段率 %d / %d，操作数=实测呈现速率）---\n",
            vfrEarly, vfrLate);
        if (starved)
            std::printf("  会话被饿住：媒体时钟这扇窗只推进 %.2fs（观察窗 %.2fs），"
                "晚段根本没到 ⇒ 本臂各条判据记未判，不记红\n", mediaAdvancedS, seconds);
        if (rateSeries.empty())
            std::printf("  实测呈现速率没有样本（呈现帧差分全程为零，窗宽 %.2fs）⇒ 本臂没有操作数\n",
                rateWindowS);
        const bool operand = !starved && rateSeries.size() >= 5;
        const auto panel = panelHz;
        // 段期望速率 = 该段内容率，天花板是 open 时产出的那个常数上限（上限本身由 ⑦ 判）；
        // 声明刷新率作为上限的另一半仍然要参与：内容快过它时，该段就只能读到声明刷新率。
        const auto wantEarly = std::min(panel, static_cast<double>(vfrEarly));
        const auto wantLate = std::min(panel, static_cast<double>(vfrLate));
        const auto wantMean = std::min(panel, (vfrEarly + vfrLate) / 2.0);
        // 两档差不到 1.5 倍就分不出平台段；晚段撞上声明刷新率时，"60"既可能是内容率也可能是
        // 刷新率，那条判据就没有操作数了 —— 这两种情况一律记未判，不记绿。
        const bool separable = static_cast<double>(vfrLate) >= vfrEarly * 1.5;
        const bool lateIsPanel = panel <= static_cast<double>(vfrLate);
        std::printf("  预测：早段 %.2f Hz｜晚段 %.2f Hz｜整片平均 %.2f Hz｜声明刷新率 %.2f Hz"
            "｜本媒体上限 %.2f Hz（分档可辨=%s，晚段撞上刷新率=%s，操作数=%s）\n",
            wantEarly, wantLate, wantMean, panel, boundHz,
            separable ? "是" : "否", lateIsPanel ? "是" : "否", operand ? "有" : "无");
        const auto* earlyPlateau = operand ? FindPlateau(ratePlateaus, wantEarly, 0.5, rateQuantHz)
                                           : nullptr;
        const auto* latePlateau = operand ? FindPlateau(ratePlateaus, wantLate, 0.5, rateQuantHz)
                                          : nullptr;
        Judge(operand && separable, earlyPlateau != nullptr,
            "⑥ 早段实测呈现速率有 ≥0.5s 平台段落在该段内容率（且该率 ≤ open 时产出的上限）");
        Judge(operand && separable && !lateIsPanel, latePlateau != nullptr,
            "⑥ 晚段实测呈现速率有 ≥0.5s 平台段落在该段内容率（且该率 ≤ open 时产出的上限）");
        // 顺序这一条不是装饰：实测 .ts 臂在片尾出现过一条回到早段率的尾巴（EOF 后
        // 走的是队列空 ⇒ 退回容器那一档），若只问"有没有 25 的平台段"，那条尾巴就能
        // 替早段作证，晚段判据红与否就都和早段无关了。要求早段真的**先于**晚段开始，
        // 才把"逐段跟上"这件事钉在时间方向上。
        Judge(operand && earlyPlateau != nullptr && latePlateau != nullptr,
            earlyPlateau != nullptr && latePlateau != nullptr &&
            earlyPlateau->startS < latePlateau->startS,
            "⑥ 早段速率平台段先于晚段速率平台段出现（换档方向不能反）");
        Judge(operand && separable && !lateIsPanel &&
              wantMean < wantLate * 0.95 && wantMean > wantEarly * 1.05,
            FindPlateau(ratePlateaus, wantMean, 0.5, rateQuantHz) == nullptr,
            "⑥ 实测呈现速率没停在整片平均上（容器 avg_frame_rate 那条路的签名）");
        // 偏松的上界不许偏低：r_frame_rate 只会高估、不会低估，所以 per-媒体上限必须
        // 不低于最快那一段的内容率；低了就会把快段掐成掉帧（后果看"传统口径丢弃"那一栏，
        // 以及实测速率平台段读不到快段率这件事）。
        Judge(operand && boundHz > 0.0,
            maxBoundHz >= std::min(panel, std::max(static_cast<double>(vfrEarly),
                                                static_cast<double>(vfrLate))) * 0.95,
            "⑥ 上限不低于最快段的内容率（不抬上去就报出一个低于真实内容的假上限）");
    }

    // 换媒体臂：上限在换一条媒体之后要跟**新媒自身**的内容率走，不能停在上一条。
    // 设计要点（本轮踩过两次才做对）：两条媒体的率必须差得够远，否则这条判据没有操作数。
    // 实测：24fps→25fps 静图 分不开（24/25 同量级，等于没判）；24fps 动图 PNG 自带 10fps、
    // 静图自带 25fps，所以"退回面板率"这个预期本身就是错的 —— 真正会穿的是"停在上一条的值"。
    // 用 240fps 视频(上限 144) → 静图(25)：泄漏则读 144，正常读 25。
    if (const char* leakImage = std::getenv("PCP_LEAK_IMAGE")) {
        const auto nextFps = EnvInt("PCP_LEAK_IMAGE_FPS", 0);
        const auto videoBound = ReadPacing(player).frameRateBoundHz;
        g_openCompleted.store(false);
        FFF3FP_Stop(player);
        const auto reopen = FFF3FP_Open(player, leakImage);
        for (int i = 0; i < 400 && !g_openCompleted.load(); ++i) PumpFor(hwnd, 25);
        PumpFor(hwnd, 1200);
        const auto imagePacing = ReadPacing(player);
        std::printf("\n--- 换媒体臂 ---\n");
        std::printf("  Open 结果=%d｜上一媒上限=%.2f Hz → 新媒上限=%.2f Hz（新媒标称 %d fps）\n",
            static_cast<int>(reopen), videoBound, imagePacing.frameRateBoundHz, nextFps);
        Judge(reopen == FFFResult::Success && nextFps > 0 && videoBound > nextFps * 2.0,
            std::fabs(imagePacing.frameRateBoundHz - nextFps) / nextFps < 0.10,
            "⑤ 换媒体后上限跟随新媒自身内容率（不停在上一条的值）");
    }

    std::printf("\nPACING RESULT: %s（判 %d / 红 %d / 未判 %d）\n",
        red > 0 ? "FAIL" : (judged > 0 ? "PASS" : "NOT-JUDGED"), judged, red, undecided);
    FFF3FP_Destroy(player);
    return red > 0 ? 1 : (judged > 0 ? 0 : 3);
}
