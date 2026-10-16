// ============================================================================
// present_pacing_probe.cpp —— 呈现节律锁的验收探针（2026-10-15）
//
// 内核新增了三件事，每件都要能判红，否则等于没做：
//   ① DetectDisplayRefreshRate 不再把面板 clamp 到 120（144Hz 曾被报成 120）
//   ② 呈现器用交换链自己的帧统计标定刷新网格，并把每次翻帧钉到目标格
//      （VideoRenderer.cpp: AdvancePacingDeadline / UpdatePresentGrid）
//   ③ 窗口翻帧上限自动取 min(面板率, 内容率)（没有 setter），以及 G-SYNC/FreeSync
//      （enableTearing=1）时
//      主动让位——可变刷新下面板自己配速，再钉网格只会加延迟
//
// 为什么必须有这条探针：FFF3FP_SetPresentConfig 的返回值早就被证明不能当判据
//   （docs/03 A8：它只说明交换链"接受了请求"）。节律锁同样看不见——它生效的时候
//   画面是平的，它失效的时候画面也是平的，差别只在每一帧停留了几次刷新。
//
// 采样前提（2026-10-15 的教训）：窗口**必须真的在被合成**。上一版网格夹具因为
//   窗口被遮挡，读到 421 次零差分 + 2 次几百刷新的计数器跳变——遮挡时 DWM 根本不动
//   这条链的计数。所以这里显式置顶窗口，并把"网格标定出来了没有"本身当成前置判据：
//   measured 一直是 0 ⇒ 本臂没在合成，显示侧判据全部记未判，而不是记绿。
//
// 用法：present_pacing_probe '<绝对媒体路径>' [秒数]
//   PCP_TEARING=1      走 VRR/撕裂路（判 gridLocked 必须为 0 且 bypass=1）
//   PCP_CONTENT_FPS=N  内容标称帧率，用来预测"自动上限 = min(面板率, 内容率)"该是多少
//                      （不设则该条判据记未判；60fps 片配 144Hz 面板应得 60，
//                       240fps 片配同一面板应得 144 —— 两个方向都必须是活的）
//   PCP_POLL_MS=1      轮询间隔
//   PCP_VFR_EARLY_HZ=N / PCP_VFR_LATE_HZ=N
//                      可变帧率臂：声明这条媒体**前后两段的各自速率**（配
//                      samples/media/vfr/12a_vfr_25then60.* ⇒ 25 与 60）。开了这两个
//                      变量就改判 ⑥ 那组：自动上限必须逐段变高/变低，并且
//                      **不得停在整片平均**（那是 avg_frame_rate 那条路的特征签名）。
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

// ---- 可变帧率臂用的平台段 ----
// 上限是一个逐帧重算的标量（min(面板率, 内容率)），所以"内容率"换了档，读数会在一段
// 连续时间里整体搬过去。按段判而不是按整片平均判是必须的：整片平均把 25/60 两段糊成
// 42.5，而 42.5 恰好就是"走容器 avg_frame_rate 那条路"应当产生的数——用平均数当尺子，
// 错实现和正实现会一起通过。
struct BoundSample { double tS; double hz; };
struct Plateau { double startS; double durationS; double meanHz; std::size_t count; };

std::vector<Plateau> FindPlateaus(const std::vector<BoundSample>& samples) {
    std::vector<Plateau> out;
    for (const auto& s : samples) {
        if (!out.empty()) {
            auto& last = out.back();
            if (std::fabs(s.hz - last.meanHz) <= last.meanHz * 0.05) {
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

const Plateau* FindPlateau(const std::vector<Plateau>& plateaus, double wantHz, double minS) {
    if (wantHz <= 0.0) return nullptr;
    for (const auto& p : plateaus)
        if (p.durationS >= minS && std::fabs(p.meanHz - wantHz) <= wantHz * 0.05) return &p;
    return nullptr;
}

}   // namespace

int main(int argc, char** argv) {
    // 无缓冲：这台机器上这个臂实测会间歇性地在一条 stderr（"can't open file for write !"）
    // 之后一声不响地结束，而 stdout 是块缓冲的 —— 那样连"=== 呈现节律锁验收 ==="都看不到，
    // 无法区分"没建起来"和"建起来后死了"。留这条是为了下次能判。
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { std::printf("用法：present_pacing_probe '<绝对媒体路径>' [秒数]\n"); return 2; }
    const std::string path = argv[1];
    const double seconds = argc > 2 ? std::atof(argv[2]) : 8.0;
    if (const char* env = std::getenv("PCP_POLL_MS")) g_pollMs = std::clamp(std::atoi(env), 1, 100);
    const auto tearing = std::atoi(std::getenv("PCP_TEARING") ? std::getenv("PCP_TEARING") : "0");
    // 内容标称帧率：用来预测内核应当自动算出的上限 min(面板率, 内容率)。
    // 不设就没有可预测的上限，那条判据记未判而不是猜。
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
    // 上限只走运行时 setter，不走配置字段：见 FFF.Player.Api.h 里 FFF3FPConfiguration
    // 尾部那条注释——配置面加字段会把 sizeof 加宽，而 FFF3FP_Create 对 size 偏小的宿主
    // 直接判 InvalidArgument，那会让没同步重编的宿主连会话都建不起来。

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&config, &player) != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败\n"); return 2;
    }
    std::printf("=== 呈现节律锁验收 ===\n");
    // 一栏一名：这装的是"内容率预期"（用来预测自动上限），不是任何上限设置——
    // 上限在内核里恒取 min(面板率, 内容率)，没有可写入口。
    std::printf("file=%s 观察=%.1fs 轮询=%dms 臂: tearing=%d 内容率预期=%d 可变帧率段=%d/%d\n",
        path.c_str(), seconds, g_pollMs, tearing, contentFps, vfrEarly, vfrLate);
    std::printf("探针独立量到的面板声明值=%d Hz（判 ① 用的就是它）\n", probeDeclaredHz);
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) { std::printf("Open 失败\n"); return 2; }
    for (int i = 0; i < 400 && !g_openCompleted.load(); ++i) PumpFor(hwnd, 25);
    if (tearing) FFF3FP_SetPresentConfig(player, 1);
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    PumpFor(hwnd, 600);   // 让首帧落地并给出两次标定采样

    const auto baseline = ReadSnapshot(player);
    const auto baselinePacing = ReadPacing(player);
    const auto baselineDisplayDiscards = baselinePacing.displayRateDiscardedFrames;
    const auto start100ns = Now100ns();
    double maxMeasuredHz = 0.0;
    double maxBoundHz = 0.0;
    std::uint32_t sawLocked = 0, sawUnlocked = 0;
    std::uint32_t lastBypass = 0;
    std::vector<double> slotErrorPeriods;
    std::vector<double> slotSignedPeriods;
    std::vector<BoundSample> boundSeries;   // 仅可变帧率臂填充，别的臂一个额外字节都不加

    while (Now100ns() - start100ns < static_cast<std::int64_t>(seconds * 10'000'000)) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        const auto pacing = ReadPacing(player);
        maxMeasuredHz = std::max(maxMeasuredHz, static_cast<double>(pacing.measuredRefreshHz));
        maxBoundHz = std::max(maxBoundHz, static_cast<double>(pacing.frameRateBoundHz));
        if (vfrArm) boundSeries.push_back(BoundSample{
            (Now100ns() - start100ns) / 10'000'000.0,
            static_cast<double>(pacing.frameRateBoundHz)});
        if (pacing.gridLocked) ++sawLocked; else ++sawUnlocked;
        lastBypass = pacing.pacingBypass;
        if (pacing.measuredRefreshHz > 0.0f && pacing.gridLocked) {
            // 只在锁真的在岗的那些采样上算精度。否则一个残留的旧读数就能把"从未锁上"的
            // 那一臂判成满分——30fps 上限臂实测就是这样假绿的。
            const auto period100ns = 10'000'000.0 / pacing.measuredRefreshHz;
            const auto signedPeriods = static_cast<double>(pacing.lastSlotError100ns) / period100ns;
            slotErrorPeriods.push_back(std::fabs(signedPeriods));
            slotSignedPeriods.push_back(signedPeriods);
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

    std::printf("\n--- 读数 ---\n");
    std::printf("  内核报的声明刷新率=%.2f Hz｜探针量的=%d Hz\n",
        finalPacing.declaredRefreshHz, probeDeclaredHz);
    std::printf("  网格标定（整窗峰值）=%.2f Hz｜末次 gridLocked=%u bypass=%u（轮询里 锁=%u 次/未锁=%u 次）\n",
        maxMeasuredHz, finalPacing.gridLocked, finalPacing.pacingBypass, sawLocked, sawUnlocked);
    std::printf("  呈现帧=%.0f（%.1f fps）翻帧=%.0f｜翻帧/呈现帧=%.3f\n",
        presented, presentedFps, flips, presented > 0 ? flips / presented : 0.0);
    // 合并帧这一栏是为"屏幕停留带内比例"那条判据准备的：一次呈现跳过了 k 个更新过的
    // 画面，被显示的那一帧就要多停 k 个源帧的时长，于是 ⌊r⌋/⌈r⌉ 带外必然出现——
    // 这是管线合并，不是节律锁失守。两个数不并排列，就没法把这两种成因分开。
    std::printf("  合并帧（呈现时跳过的更新次数）=%.0f｜解码帧=%.0f\n",
        static_cast<double>(after.coalescedVideoFrames - baseline.coalescedVideoFrames),
        static_cast<double>(after.decodedVideoFrames - baseline.decodedVideoFrames));
    // 两类丢弃并排列出，各自写明原因——它们含义不同，混在一个数里就会让人去查一个
    // 根本不存在的性能问题。原因那句只在条件真的成立时才印，否则就是在写一句假话
    // （上一版无条件印"面板 144 Hz < 片源 60 fps"，那个不等式本身是反的）。
    const bool panelIsTheLimit = static_cast<double>(contentFps) > maxMeasuredHz * 1.005;
    std::printf("  丢弃：管线跟不上媒体时钟（传统口径）=%.0f｜因显示器帧率 < 视频帧率而丢弃=%.0f｜合计=%.0f\n",
        drops, displayDrops, drops + displayDrops);
    if (panelIsTheLimit)
        std::printf("    ↑ 显示速率那一栏的成因：片源 %d fps 快过面板 %.2f Hz，"
            "面板就是上限，缩画面或加线程都改变不了\n", contentFps, maxMeasuredHz);
    else if (contentFps > 0)
        std::printf("    （片源 %d fps 不高于面板 %.2f Hz，本臂不该有显示速率丢弃；"
            "两栏都是 0 才对）\n", contentFps, maxMeasuredHz);
    // 眼睛看到的节律在这一行：内核自己数的"这一帧在屏上真停了几个刷新周期"，
    // 对内容率/面板率算出的 ⌊r⌋~⌈r⌉ 带。提交精度只算半边，这条才是结果。
    const double holdSampled = static_cast<double>(finalPacing.holdSampled);
    std::printf("  屏幕停留对账：可测=%.0f 落在 ⌊r⌋/⌈r⌉ 带内=%.0f（%.1f%%）带外最差偏=%llu 格｜"
        "从未上屏翻帧=%llu（停留判带按累计 ratio 结算，这个数就是被并掉的那些翻帧）\n",
        holdSampled, static_cast<double>(finalPacing.holdWithinRounding),
        holdSampled > 0 ? finalPacing.holdWithinRounding * 100.0 / holdSampled : 0.0,
        static_cast<unsigned long long>(finalPacing.holdWorstDeviationRefreshes),
        static_cast<unsigned long long>(finalPacing.neverCompositedPresents));
    if (!slotErrorPeriods.empty()) {
        std::size_t withinOne = 0;
        double worst = 0.0, sumSigned = 0.0;
        std::size_t signedCount = 0;
        for (const auto e : slotErrorPeriods) { if (e <= 1.0) ++withinOne; worst = std::max(worst, e); }
        for (const auto e : slotSignedPeriods) { sumSigned += e; ++signedCount; }
        const auto meanSigned = signedCount ? sumSigned / signedCount : 0.0;
        // lead 是这个臂自己应当提前提交的量（内核取 max(0.35×面板周期, 1ms)）。
        // 带符号均值若≈ −lead，说明这个读数就是设计偏移本身，**不构成精度证据**——
        // 这一点必须打在旁边，否则读者会把"最差 0.35 格"当成锁很准。
        std::printf("  瞄格误差（单位=一个刷新周期）：样本=%zu 落在一格内=%.1f%% 最差=%.2f｜"
            "带符号均值=%.2f｜内核 lead≈0.35 ⇒ 若两者同量级，这条读数只是 lead，不是精度\n",
            slotErrorPeriods.size(), withinOne * 100.0 / slotErrorPeriods.size(), worst, meanSigned);
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
    // 前置：标定不出来就意味着窗口没被合成，后面的显示侧判据全部不作数。
    Judge(true, calibrated, "合成前置：网格标定出来了（否则本臂显示侧全是假话）");
    if (!calibrated) {
        std::printf("\nPACING RESULT: NOT-JUDGED（判 %d / 红 %d / 未判 %d）——标定未达成\n",
            judged, red, undecided);
        FFF3FP_Destroy(player);
        return red > 0 ? 1 : 3;
    }
    // ② 标定值与声明值一致性（±0.5%）。144.00 与 143.99 之差是面板自身振荡，
    //    超过 0.5% 说明我把别的东西当成了刷新周期。
    Judge(probeDeclaredHz > 0,
        std::fabs(maxMeasuredHz - probeDeclaredHz) / probeDeclaredHz < 0.005,
        "② 网格标定值与声明刷新率相差 <0.5%");
    // ②/VRR 分叉：撕裂路下必须从没锁过且给出 bypass=1，否则"配合 G-SYNC"这句话没有牙。
    if (tearing) {
        Judge(true, sawLocked == 0 && lastBypass == 1,
            "③ VRR 路让位：gridLocked 全程为 0 且 bypass=1");
    } else {
        Judge(true, sawLocked > 0, "② 锁上岗过：轮询里出现过 gridLocked=1");
        std::size_t withinOne = 0;
        for (const auto e : slotErrorPeriods) if (e <= 1.0) ++withinOne;
        const auto fraction = slotErrorPeriods.empty() ? 0.0 :
            withinOne * 1.0 / slotErrorPeriods.size();
        // 这两条都要"管线跟得上"才有资格判。实测同一件 60fps 文件三趟：呈现 42.1／38.5／57.7
        // fps 时瞄格一格内 81.5／67.3／99.0%、停留带内 66.5／48.7／91.3%——两个数跟的都是
        // 吞吐而不是节律锁。跟不上还记红，就是让进程调度替内核背锅。
        const bool keptUp = maxBoundHz > 0.0 && presentedFps >= 0.95 * maxBoundHz && presented > 200;
        std::printf("  计时判据前置：呈现 %.1f fps 对上限 %.1f Hz 的 95%% ⇒ %s\n",
            presentedFps, maxBoundHz, keptUp ? "管线跟得上，两条计时判据生效"
                                             : "管线跟不上，两条计时判据记未判（这是吞吐的账）");
        Judge(keptUp && slotErrorPeriods.size() >= 40, fraction >= 0.95,
            "② 瞄格误差：≥95% 的采样落在一个刷新周期内（对的是「该提交的时刻」，不是槽时刻）");
        // ③ 没有多次复制：翻帧数不该明显超过呈现帧数。144Hz 面板上若按"UI 跟面板"
        //    实现，这个比值会接近 2.4，正是要避免的性能浪费。
        Judge(presented > 20, flips / presented <= 1.15,
            "③ 无多余复制：翻帧/呈现帧 ≤ 1.15");
        // 结果那半边，取"与对齐无关"的口径：停留格数落在 ⌊r⌋ 或 ⌈r⌉ 之内就算稳。
        // 判"等于我瞄的那一格"试过两种对齐，都是 30~47%，而面板平均恰好 2.4 —— 那说
        // 明问题在两条计数序列的归属上，不在节律本身。带内判据不依赖归属。
        Judge(keptUp && finalPacing.holdSampled >= 100,
            finalPacing.holdWithinRounding * 1.0 / finalPacing.holdSampled >= 0.95 &&
            finalPacing.holdWorstDeviationRefreshes <= 1,
            "② 屏幕停留落在 ⌊r⌋/⌈r⌉ 带内：≥95% 且带外偏差 ≤1 格");
    }
    // ③ 上限自动取 min(面板率, 内容率)：内核自己算，探针只预测不设置。
    //    两个方向都得是活的——60fps 片配 144Hz 面板该给 60，240fps 片配同一面板该给 144。
    if (contentFps > 0 && maxMeasuredHz > 0.0) {
        const auto expected = std::min(maxMeasuredHz, static_cast<double>(contentFps));
        Judge(true, std::fabs(maxBoundHz - expected) / expected < 0.02,
            "③ 自动上限 == min(面板率, 内容率)");
        std::printf("  上限：内核报 %.2f Hz｜预测 min(%.2f, %d) = %.2f Hz｜呈现 %.1f fps\n",
            maxBoundHz, maxMeasuredHz, contentFps, expected, presentedFps);
        Judge(presented > 20, presentedFps <= expected * 1.08,
            "③ 呈现速率没越过自动上限");
        // 分账必须真的分得开：内容快于面板时，丢弃要落在"显示速率"那一栏，传统口径得近乎 0。
        // 少了这条，换个名字记同一笔数也能"看起来分开了"。
        if (static_cast<double>(contentFps) > maxMeasuredHz * 1.005) {
            Judge(true, drops <= 2 && displayDrops > 0,
                "④ 分账真的分叉：快于面板的丢弃全记在显示速率栏，传统迟到口径近乎 0");
            const auto expectedDiscards = (static_cast<double>(contentFps) - maxMeasuredHz) *
                (wall100ns / 10'000'000.0);
            Judge(true, std::fabs(displayDrops - expectedDiscards) / expectedDiscards < 0.25,
                "④ 显示速率丢弃数 ≈ (片源率 − 面板率) × 观察秒数");
            std::printf("  预期量级：(内容 %d − 面板 %.2f) × %.1fs ≈ %.0f 帧\n",
                contentFps, maxMeasuredHz, wall100ns / 10'000'000.0, expectedDiscards);
        }
    } else {
        ++undecided;
        std::printf("  [未判] 自动上限：没给 PCP_CONTENT_FPS，无法预测 min(面板率, 内容率)\n");
    }

    // ⑥ 可变帧率臂：自动上限必须**逐段换档**，而且不得停在整片平均上。
    //
    // 为什么用平台段而不是"上下半场各取均值"：采样起点与媒体位置之间有 600ms 预热偏移
    // （Play 之后才 PumpFor(600) 再取 start100ns），拿墙上时间切两半会把变速边界算歪，
    // 边界那 0.6s 会把两个数互相污染。平台段判"读数有没有在一段连续时间里稳在某个档位"，
    // 对边界位置不敏感。0.5s 的门槛来自这条夹具每档各占约 2s。
    //
    // 三条判据各挡一种错法：只看早段 ⇒ 用第一个数套整片；看平均 ⇒ 走容器 avg_frame_rate；
    // 因此第三条（"不许落在整片平均"）是前两条之外独立的一根牙，缺了它，"取整片平均"
    // 这种实现能同时通过前两条的一半。
    if (vfrArm && starved) {
        std::printf("\n--- 可变帧率臂（声明段率 %d / %d）---\n", vfrEarly, vfrLate);
        std::printf("  [未判] 会话被饿住：媒体时钟这扇窗只推进 %.2fs（观察窗 %.2fs），"
            "晚段根本没到，⑥ 三条没有操作数\n", mediaAdvancedS, seconds);
        undecided += 3;
    } else if (vfrArm) {
        const auto plateaus = FindPlateaus(boundSeries);
        std::printf("\n--- 可变帧率臂（声明段率 %d / %d）---\n", vfrEarly, vfrLate);
        std::printf("  采样 %zu 个｜平台段 %zu 条：", boundSeries.size(), plateaus.size());
        for (const auto& p : plateaus)
            std::printf("[起 %.2fs 长 %.2fs @ %.1fHz] ", p.startS, p.durationS, p.meanHz);
        std::printf("\n");
        const auto panel = static_cast<double>(maxMeasuredHz);
        const auto wantEarly = std::min(panel, static_cast<double>(vfrEarly));
        const auto wantLate = std::min(panel, static_cast<double>(vfrLate));
        const auto wantMean = std::min(panel, (vfrEarly + vfrLate) / 2.0);
        // 两档差不到 1.5 倍就分不出平台段；晚段撞上面板时，"60"既可能是内容率也可能是
        // 面板率，那条判据就没有操作数了 —— 这两种情况一律记未判，不记绿。
        const bool separable = static_cast<double>(vfrLate) >= vfrEarly * 1.5;
        const bool lateIsPanel = panel <= static_cast<double>(vfrLate);
        std::printf("  预测：早段 %.2f Hz｜晚段 %.2f Hz｜整片平均 %.2f Hz｜面板 %.2f Hz"
            "（分档可辨=%s，晚段撞面板=%s）\n", wantEarly, wantLate, wantMean, panel,
            separable ? "是" : "否", lateIsPanel ? "是" : "否");
        Judge(separable, FindPlateau(plateaus, wantEarly, 0.5) != nullptr,
            "⑥ 早段有 ≥0.5s 平台段落在 min(面板, 早段率)");
        Judge(separable && !lateIsPanel, FindPlateau(plateaus, wantLate, 0.5) != nullptr,
            "⑥ 晚段有 ≥0.5s 平台段落在 min(面板, 晚段率)");
        // 顺序这一条不是装饰：实测 .ts 臂在片尾出现过第三条 [0.59s @ 25.0Hz]（EOF 后
        // 走的是队列空 ⇒ 退回容器那一档），若只问"有没有 25 的平台段"，那条尾巴就能
        // 替早段作证，晚段判据红与否就都和早段无关了。要求早段真的**先于**晚段开始，
        // 才把"逐段换档"这件事钉在时间方向上。
        const auto* earlyPlateau = FindPlateau(plateaus, wantEarly, 0.5);
        const auto* latePlateau = FindPlateau(plateaus, wantLate, 0.5);
        Judge(earlyPlateau != nullptr && latePlateau != nullptr,
            earlyPlateau != nullptr && latePlateau != nullptr &&
            earlyPlateau->startS < latePlateau->startS,
            "⑥ 早段平台段先于晚段平台段出现（换档方向不能反）");
        Judge(separable && !lateIsPanel &&
              wantMean < wantLate * 0.95 && wantMean > wantEarly * 1.05,
            FindPlateau(plateaus, wantMean, 0.5) == nullptr,
            "⑥ 上限没停在整片平均速率上（容器 avg_frame_rate 那条路的签名）");
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
