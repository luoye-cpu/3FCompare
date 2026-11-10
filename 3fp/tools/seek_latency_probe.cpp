// ============================================================================
// seek_latency_probe.cpp —— 端到端量「快进/快退」的响应时间与排队行为
//
// 背景：用户报「来回快进/快退时卡顿」。纯 FFmpeg 侧（seek_floor_probe.c）已量出
//   单次精确跳转的下限：av_seek_frame 本身 ~0.02 ms，但 DoSeek(exact) 之后要把
//   目标之前的关键帧整段解到目标，1080p60 / 4.17s GOP 实测 247 ms / 151 帧。
//   本探针测内核真实路径，把一次跳转拆成四段：
//     t0 提交（FFF3FP_Seek 返回）
//     t1 位置发布（DoSeek 末尾 PublishSnapshot：此时 demux 已定位，但画面还是旧的）
//     t2 画面呈现（presentedVideoFrames 首次增长：用户真正看到新画面）
//     t3 操作完成事件（OperationCompleted:seek：worker 把这次命令干完了）
//   另跑「连点」脚本，照宿主的写法读快照位置再 +step 提交，检验：
//     · 队列是否合并被超越的跳转（不合并 ⇒ N 次点击 = N × 单次耗时）
//     · 宿主读到的位置是否已经过期（未过期才累积；过期则连点等于原地跳）
//
// 用法：
//   seek_latency_probe <媒体> [--step 秒] [--rounds N] [--hw] [--playing] [--press-ms 40]
//   默认 step=2 秒、rounds=6、暂停态、CPU 解码。
//   --hw      用 D3D11 硬件解码（宿主的默认路线）
//   --playing 播放态连点（默认先在暂停态量纯跳转成本）
//   --press-ms N  连点时两次提交的间隔（默认 40 ms，模拟快速连按）
// ============================================================================
#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

constexpr double kTicksPerSecond = 1e7;

struct SeekEvent {
    std::int64_t atNs;
    std::int64_t position100ns;
};

HANDLE g_eventFile = INVALID_HANDLE_VALUE;   // 不由本探针使用，保留给外部重定向
std::vector<SeekEvent> g_seekEvents;
int g_openCount = 0;

std::int64_t NowNs() {
    static LARGE_INTEGER freq{};
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER t{};
    QueryPerformanceCounter(&t);
    return static_cast<std::int64_t>(static_cast<double>(t.QuadPart) * 1e9 / static_cast<double>(freq.QuadPart));
}

double Ms(std::int64_t ns) { return static_cast<double>(ns) / 1e6; }

void Pump(const int milliseconds) {
    const auto deadline = GetTickCount64() + static_cast<DWORD>(milliseconds);
    while (GetTickCount64() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(1);
    }
}

std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    return out;
}

void EventCallback(void*, const FFF3FPEvent eventType, const char* payload) {
    if (eventType == FFF3FPEvent::OpenCompleted) { ++g_openCount; return; }
    if (eventType != FFF3FPEvent::OperationCompleted || payload == nullptr) return;
    if (std::string(payload).find("\"seek\"") == std::string::npos) return;
    // payload 形如 {"operation":"seek","position100ns":123}
    std::int64_t position = 0;
    const auto key = std::string("\"position100ns\":");
    const auto at = std::string(payload).find(key);
    if (at != std::string::npos) position = std::atoll(payload + at + key.size());
    g_seekEvents.push_back(SeekEvent{NowNs(), position});
}

bool Snapshot(FFF3FPHandle player, FFF3FPSnapshot& snapshot) {
    snapshot = {};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 23;
    return FFF3FP_GetSnapshot(player, &snapshot) == FFFResult::Success;
}

const char* StateName(const FFF3FPState state) {
    switch (state) {
    case FFF3FPState::Idle: return "Idle";
    case FFF3FPState::Opening: return "Opening";
    case FFF3FPState::Ready: return "Ready";
    case FFF3FPState::Playing: return "Playing";
    case FFF3FPState::Paused: return "Paused";
    case FFF3FPState::Ended: return "Ended";
    default: return "?";
    }
}

struct Measurement {
    double positionMs = -1;
    // 目标到位前屏幕上出现的任何新画面（seek 预览帧）落在这里。presentMs 只认
    // 「位置≈目标且 frameIndex>=0」，所以它看不见预览；而按键有没有反应，看的
    // 正是这一列。
    double firstPresentMs = -1;
    double presentMs = -1;
    double completeMs = -1;
    std::uint64_t decodedDelta = 0;
    std::uint64_t droppedDelta = 0;
    // 墙钟拆分的两个操作数：解码帧数决定了「要解多少」，videoDecode100ns 决定了
    // 「每帧多少钱」。二者相乘若远小于墙钟，说明时间花在解码之外（等待/呈现/IO）。
    std::uint64_t decodedFrames = 0;
    double decodeMs = -1;
};

// 判据要能在「暂停」和「播放」两种状态下都成立，所以分两段：
//   ① 命令真的跑了：DoSeek 末尾把 position 直接设成 target（此时 frameIndex 被置 -1，
//      画面还是旧的那张——这是「位置已发布但画面没换」的窗口，本探针要抓的就是它）
//   ② 目标画面真的上屏了：之后的 PresentVideoFrame 会把 frameIndex 从 -1 写成 >=0，
//      并把 position 更新成那一帧的位置（PlayerSession.cpp:7425）
// 播放态下 ① 之后画面本来就会继续变，所以必须用 frameIndex 的 -1→>=0 这个跃迁，
// 不能用「presentedVideoFrames 有没有增长」（播放时它一直都在涨）。
Measurement WaitForSeek(FFF3FPHandle player, const std::int64_t target100ns,
    const std::uint64_t presentedBefore, const std::uint64_t droppedBefore,
    const std::size_t eventsBefore, const int timeoutMs,
    const std::uint64_t decodedBaseline = 0, const std::uint64_t decodeNsBaseline = 0) {
    const auto start = NowNs();
    Measurement out{};
    const auto tolerance = static_cast<std::int64_t>(0.10 * kTicksPerSecond);
    bool commandRan = false;
    for (;;) {
        FFF3FPSnapshot snapshot{};
        if (!Snapshot(player, snapshot)) break;
        const auto elapsed = Ms(NowNs() - start);
        const auto nearTarget = std::llabs(snapshot.position100ns - target100ns) <= tolerance;
        if (!commandRan && nearTarget && out.positionMs < 0) {
            out.positionMs = elapsed;
            commandRan = true;
        }
        if (commandRan && out.firstPresentMs < 0 &&
            snapshot.presentedVideoFrames > presentedBefore)
            out.firstPresentMs = elapsed;
        if (commandRan && out.presentMs < 0 && nearTarget && snapshot.frameIndex >= 0) {
            out.presentMs = elapsed;
            out.decodedDelta = snapshot.decodedVideoFrames - decodedBaseline;
            out.droppedDelta = snapshot.droppedVideoFrames - droppedBefore;
            out.decodedFrames = snapshot.decodedVideoFrames - decodedBaseline;
            if (snapshot.videoDecode100ns >= decodeNsBaseline)
                out.decodeMs = static_cast<double>(snapshot.videoDecode100ns - decodeNsBaseline) / 10000.0;
        }
        if (out.completeMs < 0 && g_seekEvents.size() > eventsBefore) out.completeMs = elapsed;
        if (out.presentMs >= 0 && out.completeMs >= 0) break;
        if (elapsed > timeoutMs) break;
        // 必须抽窗口消息：DXGI 的 Present 在有窗口时会与窗口消息/遮挡状态交互，
        // 光 Sleep 会让呈现线程卡在 Present 上，量出来的是假延迟（实测能差一个数量级）。
        Pump(1);
    }
    return out;
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: seek_latency_probe <media> [--step s] [--rounds n] [--hw] [--playing] [--press-ms ms]\n");
        return 2;
    }
    double stepSeconds = 2.0;
    int rounds = 6;
    int pressMs = 40;
    bool hardware = false;
    bool playing = false;
    bool keyframeSeek = false;
    bool compare = false;
    for (int i = 2; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--step" && i + 1 < argc) stepSeconds = _wtof(argv[++i]);
        else if (arg == L"--rounds" && i + 1 < argc) rounds = _wtoi(argv[++i]);
        else if (arg == L"--press-ms" && i + 1 < argc) pressMs = _wtoi(argv[++i]);
        else if (arg == L"--hw") hardware = true;
        else if (arg == L"--playing") playing = true;
        else if (arg == L"--keyframe") keyframeSeek = true;
        else if (arg == L"--compare") compare = true;
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"FFFSeekLatencyWnd";
    RegisterClassW(&windowClass);
    const auto window = CreateWindowExW(0, L"FFFSeekLatencyWnd", L"seek-latency", WS_OVERLAPPEDWINDOW,
        40, 40, 960, 540, nullptr, nullptr, windowClass.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = hardware ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 203.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建会话失败\n");
        return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n");
        FFF3FP_Destroy(player);
        return 1;
    }
    for (int i = 0; i < 400 && g_openCount == 0; ++i) Pump(25);
    if (g_openCount == 0) { printf("打开超时\n"); FFF3FP_Destroy(player); return 1; }

    FFF3FPSnapshot snapshot{};
    Snapshot(player, snapshot);
    const double duration = static_cast<double>(snapshot.duration100ns) / kTicksPerSecond;
    printf("媒体: %s\n", path.c_str());
    // 打印的是**实际生效**的解码模式，不是配置请求值：请求 D3D11 却在打开时回退到
    // CPU 是这条路径上真实发生过的事，而配置值会让报告显示成硬解、把结论指向错误方向。
    printf("时长 %.2fs  解码 请求=%s/实际=%s  状态 %s  跳转 %s  视频 %ux%u\n", duration,
        hardware ? "D3D11" : "CPU",
        snapshot.decodeMode == FFF3FPDecodeMode::Cpu ? "CPU" : "GPU",
        StateName(snapshot.state),
        keyframeSeek ? "关键帧" : "精确",
        snapshot.videoWidth, snapshot.videoHeight);

    // 起点放在片长 35% 处，向左向右都有余量做「来回」
    const auto base = duration > 0 ? duration * 0.35 : 5.0;
    const auto doSeek = [player, keyframeSeek](const std::int64_t target) {
        return keyframeSeek ? FFF3FP_SeekKeyframe(player, target) : FFF3FP_Seek(player, target);
    };
    doSeek(static_cast<std::int64_t>(base * kTicksPerSecond));
    Pump(1500);
    if (!playing) { FFF3FP_Pause(player); Pump(300); }
    else { FFF3FP_Play(player); Pump(500); }

    if (compare) {
        // 同一会话内交替做「精确」与「关键帧」跳转，两个目标互不相同、次序相邻，
        // 负载对两臂同等生效，所以比值比绝对值可信（本机负载 100% 时绝对值会成倍放大）。
        // 判据用「画面确实换了」：position 变化 > 50 ms 且 frameIndex >= 0。
        // 不能用「position 落在目标附近」——精确跳转已经把位置设成目标了，关键帧那臂会
        // 立刻判成功，量出 0 ms（这是初版对照的错误，已废弃）。
        const auto waitChange = [&](const std::int64_t prePosition, const int timeoutMs) {
            const auto start = NowNs();
            for (;;) {
                FFF3FPSnapshot now{};
                if (!Snapshot(player, now)) return -1.0;
                const auto elapsed = Ms(NowNs() - start);
                if (now.frameIndex >= 0 && std::llabs(now.position100ns - prePosition) > 500'000)
                    return elapsed;
                if (elapsed > timeoutMs) return elapsed;
                Pump(1);
            }
        };
        printf("\n=== 对照：精确 / 关键帧 交替（目标互不相同）===\n");
        printf("%-4s %-10s %-11s %-12s %-11s\n", "#", "精确目标", "精确ms", "关键帧目标", "关键帧ms");
        double sumExact = 0, sumKey = 0;
        int n = 0;
        for (int i = 0; i < rounds; ++i) {
            Snapshot(player, snapshot);
            const auto current = static_cast<double>(snapshot.position100ns) / kTicksPerSecond;
            const double exactTarget = ((i / 3) % 2 == 0) ? current + stepSeconds : current - stepSeconds;
            const auto exact100ns = std::max<std::int64_t>(0, static_cast<std::int64_t>(exactTarget * kTicksPerSecond));

            const auto preExact = snapshot.position100ns;
            FFF3FP_Seek(player, exact100ns);
            const auto exactMs = waitChange(preExact, 15000);

            Snapshot(player, snapshot);
            const auto preKey = snapshot.position100ns;
            const auto keyTarget = static_cast<std::int64_t>(exactTarget * kTicksPerSecond) +
                static_cast<std::int64_t>(stepSeconds * kTicksPerSecond) * (((i / 3) % 2 == 0) ? 1 : -1);
            const auto key100ns = std::clamp<std::int64_t>(keyTarget, 0, snapshot.duration100ns);
            FFF3FP_SeekKeyframe(player, key100ns);
            const auto keyMs = waitChange(preKey, 15000);

            printf("%-4d %8.3fs %10.1f %10.3fs %10.1f\n", i,
                static_cast<double>(exact100ns) / kTicksPerSecond, exactMs,
                static_cast<double>(key100ns) / kTicksPerSecond, keyMs);
            if (exactMs >= 0 && keyMs >= 0) { sumExact += exactMs; sumKey += keyMs; ++n; }
            Pump(150);
        }
        if (n > 0) printf("平均：精确 %.1f ms，关键帧 %.1f ms，比值 %.1f\n",
            sumExact / n, sumKey / n, sumKey > 0 ? sumExact / sumKey : 0.0);
        FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 0;
    }

    // ---- A 段：逐次跳转（每次等画面真的换了再发下一次）----
    printf("\n=== A 段：逐次跳转（等画面呈现后再发下一次）===\n");
    printf("%-4s %-10s %-11s %-11s %-11s %-10s %-8s %-9s %-9s %-10s %-6s\n",
        "#", "目标", "位置发布ms", "首画面ms", "画面呈现ms", "其中解码ms", "解码帧", "帧均ms", "事件完成ms",
        "落点帧pts", "帧号");
    double sumPresent = 0;
    double sumPosition = 0;
    double sumFirstPresent = 0;
    int samples = 0;
    int firstSamples = 0;
    for (int i = 0; i < rounds; ++i) {
        Snapshot(player, snapshot);
        const auto current = static_cast<double>(snapshot.position100ns) / kTicksPerSecond;
        const int phase = (i / 3) % 2;
        const double target = (phase == 0 ? current + stepSeconds : current - stepSeconds);
        const auto target100ns = std::max<std::int64_t>(0, static_cast<std::int64_t>(target * kTicksPerSecond));
        const auto presentedBefore = snapshot.presentedVideoFrames;
        const auto droppedBefore = snapshot.droppedVideoFrames;
        const auto eventsBefore = g_seekEvents.size();
        const auto decodedBaseline = snapshot.decodedVideoFrames;
        const auto decodeNsBaseline = snapshot.videoDecode100ns;

        doSeek(target100ns);
        const auto measured = WaitForSeek(player, target100ns, presentedBefore, droppedBefore, eventsBefore, 8000,
            decodedBaseline, decodeNsBaseline);
        const auto perFrame = measured.decodedFrames > 0 ? measured.decodeMs / static_cast<double>(measured.decodedFrames) : 0.0;
        // 落点必须逐帧可验：两臂（全解 / 跳参考帧）若落在同一帧，framePts 应逐位相同。
        // 位置快照只到 100ns 且由目标值写入，不能用来比较落点。
        FFF3FPSnapshot landed{};
        Snapshot(player, landed);
        const auto landedSec = landed.frameTimeBaseDenominator > 0
            ? static_cast<double>(landed.framePts) * landed.frameTimeBaseNumerator /
                static_cast<double>(landed.frameTimeBaseDenominator) : -1.0;
        printf("%-4d %8.3fs %10.1f %10.1f %10.1f %10.1f %8llu %8.2f %10.1f %9.3fs %6lld\n", i,
            static_cast<double>(target100ns) / kTicksPerSecond,
            measured.positionMs, measured.firstPresentMs, measured.presentMs, measured.decodeMs,
            static_cast<unsigned long long>(measured.decodedFrames), perFrame, measured.completeMs,
            landedSec, static_cast<long long>(landed.frameIndex));
        if (measured.firstPresentMs >= 0) { sumFirstPresent += measured.firstPresentMs; ++firstSamples; }
        if (measured.presentMs >= 0) { sumPresent += measured.presentMs; ++samples; }
        if (measured.positionMs >= 0) sumPosition += measured.positionMs;
        // 400 ms 静默：这一段代表"用户单次按方向键"，而不是连击。内核用 400 ms 区分
        // 两者（见 DoSeek 的 PreviewCooldownMs），这里少等就会被判成连击而失去预览。
        Pump(400);
    }
    if (samples > 0)
        printf("A 段平均：定位 %.1f ms 后还要 %.1f ms 才换画面\n",
            sumPosition / samples, sumPresent / samples);
    if (firstSamples > 0)
        printf("A 段首画面平均 %.1f ms（seek 预览帧，n=%d）\n",
            sumFirstPresent / firstSamples, firstSamples);

    // ---- B 段：连点（照宿主相对跳转的写法：读快照位置 + step，不等完成）----
    printf("\n=== B 段：连点 %d 次，间隔 %d ms（照宿主「读快照+step」写法）===\n", rounds, pressMs);
    Snapshot(player, snapshot);
    const auto burstStart = static_cast<double>(snapshot.position100ns) / kTicksPerSecond;
    const auto burstPresentedBefore = snapshot.presentedVideoFrames;
    const auto burstEventsBefore = g_seekEvents.size();
    const auto burstStartNs = NowNs();
    printf("%-4s %-10s %-12s %s\n", "#", "快照位置", "提交目标", "差值");
    std::vector<std::int64_t> submitted;
    for (int i = 0; i < rounds; ++i) {
        Snapshot(player, snapshot);
        const auto readPosition = static_cast<double>(snapshot.position100ns) / kTicksPerSecond;
        const int phase = (i / 3) % 2;
        const double target = (phase == 0 ? readPosition + stepSeconds : readPosition - stepSeconds);
        const auto target100ns = std::max<std::int64_t>(0, static_cast<std::int64_t>(target * kTicksPerSecond));
        submitted.push_back(target100ns);
        printf("%-4d %8.3fs %10.3fs %+11.3fs\n", i, readPosition,
            static_cast<double>(target100ns) / kTicksPerSecond,
            static_cast<double>(target100ns) / kTicksPerSecond - readPosition);
        doSeek(target100ns);
        // 连点间隔：这段时间里宿主只做界面刷新，不阻塞
        const auto nextPress = NowNs() + static_cast<std::int64_t>(pressMs) * 1'000'000;
        while (NowNs() < nextPress) Pump(1);
    }
    const auto lastTarget = submitted.back();
    // 连点的完成判据不能复用 WaitForSeek（连点期间画面一直在变，presentedVideoFrames
    // 基线早就被越过）。这里分开量两件事：
    //   ① 最后一条 seek 完成事件到达（worker 把队列抽干）
    //   ② presentedVideoFrames 停在最终值 200 ms 不动（屏幕上已经是最终那张）
    double drainMs = -1;
    while (Ms(NowNs() - burstStartNs) < 20000) {
        if (g_seekEvents.size() - burstEventsBefore >= static_cast<std::size_t>(rounds)) {
            drainMs = Ms(NowNs() - burstStartNs);
            break;
        }
        Pump(1);
    }
    std::uint64_t lastPresented = 0;
    double stableMs = -1;
    {
        const auto stableStart = NowNs();
        std::uint64_t seen = 0;
        std::int64_t lastChange = stableStart;
        while (Ms(NowNs() - stableStart) < 20000) {
            FFF3FPSnapshot snapshotNow{};
            Snapshot(player, snapshotNow);
            if (snapshotNow.presentedVideoFrames != seen) { seen = snapshotNow.presentedVideoFrames; lastChange = NowNs(); }
            if (Ms(NowNs() - lastChange) > 200.0) { stableMs = Ms(lastChange - burstStartNs); break; }
            Pump(1);
        }
        lastPresented = seen;
    }
    const auto totalMs = Ms(NowNs() - burstStartNs);
    Snapshot(player, snapshot);
    auto distinct = submitted;
    std::sort(distinct.begin(), distinct.end());
    distinct.erase(std::unique(distinct.begin(), distinct.end()), distinct.end());
    printf("连点结束：队列抽干 %.1f ms  画面稳定 %.1f ms  墙钟(含 200ms 判稳) %.1f ms\n",
        drainMs, stableMs, totalMs);
    printf("最终位置 %.3fs  呈现帧 %llu（提交 %d 次，实际执行 %zu 条 seek 事件）\n",
        static_cast<double>(snapshot.position100ns) / kTicksPerSecond,
        static_cast<unsigned long long>(lastPresented), rounds,
        g_seekEvents.size() - burstEventsBefore);
    printf("提交目标去重后 %zu 个（少于点击数 ⇒ 宿主读到过期位置，连点等于原地跳）\n", distinct.size());
    printf("净位移 %.3fs\n",
        static_cast<double>(snapshot.position100ns) / kTicksPerSecond - burstStart);

    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
