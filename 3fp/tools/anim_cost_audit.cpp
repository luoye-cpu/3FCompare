// ============================================================================
// anim_cost_audit.cpp —— 动图每帧开销归因探针（2026-10-09）
//
// 三个疑问，都不改内核，只用 API 已有的累计计数器 + 进程外读数：
//   ① 整进程 CPU 70.8ms/帧（4K APNG）对 decode_profile 可归因 5.97ms/帧 的 ~65ms 缺口在哪
//      ⇒ 逐秒 ΔsoftwareConvert100ns / ΔpresentWait100ns / ΔdeviceLockWait100ns /
//        ΔhardwareTransfer100ns + **存活线程数**（解码并行度 ⇒ CPU 时间 ≠ 墙钟）
//   ② 回绕接缝 +81ms 是不是"队列重灌" ⇒ position 回退后打一段 queued 轨迹
//   ③ 4K 私有内存 670MB 的成分 ⇒ 逐秒私有 WS，与 RGBA/YUV 两种像素对照
//
// 用法：anim_cost_audit <媒体路径> [秒数，默认 10]
//   ACA_POLL_MS：轮询间隔 ms（默认 20，合法 1..500）——判决的量化带 = ±2×此值，
//                把它调到几百时「长停」这条只能出「不可辨」，不能出绿
//   ACA_QUIET=1：不打接缝轨迹
//   ACA_DECODE=gpu：请求硬解（快照 decodeMode 报最终落在哪一路）
//   ACA_DOWNSCALE_PCT=N：打开"上传前自适应降采样"，传感器为 30 帧窗口丢帧率 ≥N%
//   ACA_DEC_THREADS=N：把软解线程数钉死在 N（adaptiveDecoderThreads=1 + min=max=N）
//   ⚠ 本探针**不接管 stderr**：内核的 `[animprime]` 逐帧轨迹照旧从 stderr 出，
//     由驱动层把 stderr 并入子进程 stdout ⇒ 不要在这里加 freopen / 重定向
// 编译：py 3fp/tools/build_probe.py anim_cost_audit
// 退出码（2026-10-10 补牙）：
//   0 = 判为无长停（绿；前提是真的量到可测呈现）
//   1 = 判为有长停（真红）
//   2 = 用法 / 建窗 / Create / Open 失败 ⇒ 进程或对象根本没起来，一条判据都没跑
//   3 = NOT-JUDGED（不可判，**不是通过**）：OpenCompleted 未到达 / 0 次呈现 /
//       瞬态后可测呈现间隔 < 3 / 最大间隔与阈值之差落在量化带 ±2×轮询 之内（测不准）
// 长停阈值（2026-10-10）：改为**相对**源节奏 max(50ms, 1.5×稳态中位)，与 loop_stutter_probe 同规则。
//   原先的固定 150ms 会把 5fps 源（200ms 节奏）判成帧帧长停（实测 58/58 假红）。
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

volatile bool g_openCompleted = false;
std::vector<std::string> g_events;

void __cdecl EventCallback(void*, FFF3FPEvent e, const char* json) {
    const char* name = "?";
    switch (e) {
        case FFF3FPEvent::OpenCompleted: name = "OpenCompleted"; g_openCompleted = true; break;
        case FFF3FPEvent::PlaybackEnded: name = "PlaybackEnded"; break;
        case FFF3FPEvent::Error: name = "Error"; break;
        case FFF3FPEvent::StateChanged: name = "StateChanged"; break;
        case FFF3FPEvent::ColorModeChanged: name = "ColorModeChanged"; break;
        default: return;
    }
    if (g_events.size() < 40) g_events.emplace_back(std::string(name) + " " + (json ? json : ""));
}

LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

int g_pollMs = 20;

double NowMs() {
    static const auto origin = std::chrono::steady_clock::now();
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - origin).count();
}

void PumpMessages() {
    MSG msg{};
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg); DispatchMessageW(&msg);
    }
}

void PumpFor(int milliseconds) {
    const auto deadline = NowMs() + milliseconds;
    while (NowMs() < deadline) { PumpMessages(); Sleep(1); }
}

FFF3FPSnapshot Read(FFF3FPHandle p) {
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = 20;
    FFF3FP_GetSnapshot(p, &s);
    return s;
}

std::size_t CountThreads() {
    const auto snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 te{};
    te.dwSize = sizeof(te);
    std::size_t count = 0;
    const auto pid = GetCurrentProcessId();
    if (Thread32First(snap, &te)) {
        do { if (te.th32OwnerProcessID == pid) ++count; } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
    return count;
}

double PrivateMB() {
    PROCESS_MEMORY_COUNTERS_EX pmc{};
    pmc.cb = sizeof(pmc);
    if (!K32GetProcessMemoryInfo(GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc), sizeof(pmc)))
        return 0.0;
    return pmc.PrivateUsage / 1048576.0;
}

std::uint64_t Cpu100ns() {
    FILETIME c{}, e{}, k{}, u{};
    if (!GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u)) return 0;
    const auto toU64 = [](const FILETIME& f) {
        return (static_cast<std::uint64_t>(f.dwHighDateTime) << 32) | f.dwLowDateTime;
    };
    return toU64(k) + toU64(u);
}

constexpr double MsPer100ns = 1.0 / 10000.0;
std::uint64_t SafeDelta(std::uint64_t now, std::uint64_t before) {
    return now >= before ? now - before : 0;
}

// 分位数（副本内排序，p=0.5 即中位，p=0.9 即 p90）；空集回 0，调用方负责标注样本数
double Percentile(std::vector<double> v, double p) {
    if (v.empty()) return 0.0;
    std::sort(v.begin(), v.end());
    const auto idx = static_cast<std::size_t>(p * static_cast<double>(v.size() - 1));
    return v[std::min(idx, v.size() - 1)];
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) { std::printf("usage: anim_cost_audit <media path> [seconds]\n"); return 2; }
    const char* path = argv[1];
    const double seconds = argc >= 3 ? std::atof(argv[2]) : 10.0;
    if (const char* env = std::getenv("ACA_POLL_MS")) {
        const int v = std::atoi(env);
        if (v >= 1 && v <= 500) g_pollMs = v;
    }
    const bool quiet = std::getenv("ACA_QUIET") != nullptr;

    std::printf("=== 动图每帧开销归因 ===\n");
    std::printf("file: %s  观察 %.1fs  轮询 %dms  API=%u\n", path, seconds, g_pollMs,
                FFF3FP_GetApiVersion());

    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"animCostAuditWindow";
    RegisterClassW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"anim-cost-audit", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
    if (hwnd == nullptr) { std::printf("创建窗口失败\n"); return 2; }

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
    config.preferredAdapterIndex = -1;
    config.eventCallback = &EventCallback;
    // ACA_DECODE=gpu：验"动图能不能走硬解"（快照 decodeMode 会报最终落在哪一路）
    if (const char* env = std::getenv("ACA_DECODE"))
        config.decodeMode = std::strcmp(env, "gpu") == 0 ? FFF3FPDecodeMode::Gpu : FFF3FPDecodeMode::Cpu;
    // ACA_DOWNSCALE_PCT=N：打开"上传前自适应降采样"（内核默认按配置字段关着）。
    // 它的传感器是"30 帧窗口内丢帧率 ≥N%"，所以只影响真的在丢帧的档位。
    if (const char* env = std::getenv("ACA_DOWNSCALE_PCT")) {
        const int n = std::atoi(env);
        if (n >= 1 && n <= 99) {
            config.adaptiveDownscaleBeforeUpload = 1;
            config.adaptiveDownscaleDropPercent = static_cast<std::uint32_t>(n);
            std::printf("自适应降采样=开 触发丢帧率=%d%%\n", n);
        }
    }
    // ACA_DEC_THREADS=N 把软解线程数钉死在 N（adaptiveDecoderThreads=1 + min=max=N）。
    // 这是"只用配置"的旋钮，用来验证接缝是否来自解码帧线程管线重灌。
    if (const char* env = std::getenv("ACA_DEC_THREADS")) {
        const int n = std::atoi(env);
        if (n >= 1 && n <= 64) {
            config.adaptiveDecoderThreads = 1;
            config.minSoftwareDecoderThreads = static_cast<std::uint32_t>(n);
            config.maxSoftwareDecoderThreads = static_cast<std::uint32_t>(n);
            std::printf("钉死软解线程数=%d\n", n);
        }
    }

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&config, &player) != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败\n");
        return 2;
    }
    if (FFF3FP_Open(player, path) != FFFResult::Success) { std::printf("Open 失败\n"); return 2; }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) PumpFor(50);
    PumpFor(400);

    const auto boot = Read(player);
    const auto bootCpu = Cpu100ns();
    const auto bootMB = PrivateMB();
    std::printf("尺寸=%ux%u duration100ns=%lld 状态=%d **解码路=%d** queued=%u decoded=%llu 线程=%zu 私有=%.1fMB\n",
                boot.videoWidth, boot.videoHeight, (long long)boot.duration100ns,
                static_cast<int>(boot.state), static_cast<int>(boot.decodeMode),
                boot.queuedVideoFrames,
                (unsigned long long)boot.decodedVideoFrames, CountThreads(), bootMB);

    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);

    struct TraceRow { double tMs; std::uint32_t queued; };
    std::vector<TraceRow> trace;
    bool tracing = false;

    const auto t0 = NowMs();
    const auto deadline = t0 + seconds * 1000.0;
    auto prev = Read(player);
    auto secPrev = prev;
    auto cpuPrev = Cpu100ns();
    std::uint64_t lastPresented = prev.presentedVideoFrames;
    double lastPresentMs = t0;

    std::uint64_t presents = 0, wraps = 0, longStops = 0, queuedMax = 0, secQueuedMax = 0;
    std::uint64_t seamCount = 0;
    bool wrapPending = false;
    double gapSum = 0.0, gapMax = 0.0, seamSum = 0.0, seamMax = 0.0;
    int secondMark = 0;
    // 逐次呈现间隔按"是否跨回绕"分组存下来：长停阈值只能由**稳态**（位置递增）间隔的中位算，
    // 否则接缝那一次会把阈值抬起来把真长停洗白。
    std::vector<double> steadyGaps, wrapGaps, allGaps;
    // 逐秒 CPU ms/s 样本 (第几秒, ms/s)：直方图要暴露双峰，不能只留均值
    std::vector<std::pair<int, double>> cpuPerSec;

    std::printf("\n  秒 呈现 解码 队列峰 线程 私有MB  CPU ms/s  转换ms/s  PresentWait  设备锁  HW传输\n");
    while (NowMs() < deadline) {
        PumpMessages();
        const auto snap = Read(player);
        const auto now = NowMs();

        if (snap.position100ns + 1'000'000 < prev.position100ns) {   // 回绕（位置倒退 >0.1s）
            ++wraps;
            wrapPending = true;
            if (!quiet) { trace.clear(); tracing = true;
                std::printf("  ★WRAP @ %.2fs  距上次呈现=%.2fms\n", (now - t0) / 1000.0,
                            now - lastPresentMs); }
        }
        if (snap.presentedVideoFrames != lastPresented) {
            const auto gap = now - lastPresentMs;
            if (now - t0 > 400.0) {
                ++presents;
                gapSum += gap;
                if (gap > gapMax) gapMax = gap;
                allGaps.push_back(gap);        // 长停只在这批**可测间隔**上判（阈值见末尾）
                if (wrapPending) {             // 跨过回绕的那一次呈现间隔 = 接缝成本
                    seamSum += gap;
                    ++seamCount;
                    if (gap > seamMax) seamMax = gap;
                    wrapGaps.push_back(gap);
                    wrapPending = false;
                } else {
                    steadyGaps.push_back(gap);
                }
            } else {
                wrapPending = false;
            }
            lastPresented = snap.presentedVideoFrames;
            lastPresentMs = now;
        }
        if (snap.queuedVideoFrames > queuedMax) queuedMax = snap.queuedVideoFrames;
        if (snap.queuedVideoFrames > secQueuedMax) secQueuedMax = snap.queuedVideoFrames;
        if (tracing && trace.size() < 12) trace.push_back({ now - t0, snap.queuedVideoFrames });
        if (tracing && trace.size() >= 12) {
            tracing = false;
            std::printf("      回绕后 queued 轨迹：");
            for (const auto& r : trace) std::printf("[%.0fms q=%u] ", r.tMs, r.queued);
            std::printf("\n");
        }
        prev = snap;

        const int mark = static_cast<int>((now - t0) / 1000.0);
        if (mark > secondMark) {
            secondMark = mark;
            const auto cpuNow = Cpu100ns();
            const double secCpuMs = static_cast<double>(cpuNow - cpuPrev) * MsPer100ns;
            cpuPerSec.emplace_back(mark, secCpuMs);   // 供末尾的 ms/s 直方图（双峰就藏在这里）
            std::printf("%4d %5llu %5llu %6llu %5zu %7.1f %8.1f %9.2f %11.2f %8.2f %8.2f\n",
                mark,
                (unsigned long long)SafeDelta(snap.presentedVideoFrames, secPrev.presentedVideoFrames),
                (unsigned long long)SafeDelta(snap.decodedVideoFrames, secPrev.decodedVideoFrames),
                secQueuedMax, CountThreads(), PrivateMB(),
                secCpuMs,
                SafeDelta(snap.softwareConvert100ns, secPrev.softwareConvert100ns) * MsPer100ns,
                SafeDelta(snap.presentWait100ns, secPrev.presentWait100ns) * MsPer100ns,
                SafeDelta(snap.deviceLockWait100ns, secPrev.deviceLockWait100ns) * MsPer100ns,
                SafeDelta(snap.hardwareTransfer100ns, secPrev.hardwareTransfer100ns) * MsPer100ns);
            cpuPrev = cpuNow;
            secQueuedMax = 0;
            secPrev = snap;
        }
        Sleep(g_pollMs);
    }

    const auto end = Read(player);
    const auto wall = (NowMs() - t0) / 1000.0;
    const auto cpuTotalMs = (Cpu100ns() - bootCpu) * MsPer100ns;
    const auto presentedDelta = SafeDelta(end.presentedVideoFrames, boot.presentedVideoFrames);
    const auto n = presentedDelta ? static_cast<double>(presentedDelta) : 1.0;
    const auto perFrame = [&](std::uint64_t delta100ns) {
        return delta100ns * MsPer100ns / n;
    };

    std::printf("\n--- 整窗汇总（%.2fs，呈现帧=%llu，回绕=%llu）---\n", wall,
        (unsigned long long)presentedDelta, (unsigned long long)wraps);
    if (presentedDelta == 0) {
        std::printf("  ⚠ 整窗 0 次呈现 ⇒ 下面所有「ms/呈现帧」是合计÷1 的占位除法，**不作判据**\n");
    }
    std::printf("  帧间隔（跳过前 0.4s 瞬态）：均值=%.2fms 最大=%.2fms ⇒ 等效 %.2f fps\n",
        presents ? gapSum / presents : 0.0, gapMax,
        (presents && gapSum > 0) ? presents * 1000.0 / gapSum : 0.0);
    std::printf("  接缝（跨回绕的那次呈现间隔）：n=%llu 均值=%.2fms 最大=%.2fms\n",
        (unsigned long long)seamCount, seamCount ? seamSum / static_cast<double>(seamCount) : 0.0,
        seamMax);

    // ── 长停阈值：相对源节奏 max(50, 1.5×稳态中位)，与 loop_stutter_probe:237-248 同规则。
    //    固定 150ms 是缺陷：5fps 源（200ms 节奏）会被判成帧帧长停（实测 58/58 假红）。
    //    中位只取**非回绕**间隔，免得接缝那一次把阈值抬起来、反过来洗白真长停。
    std::vector<double> medianPool = steadyGaps;
    if (medianPool.empty()) medianPool = allGaps;   // 循环比观察窗还短 ⇒ 全是回绕间隔
    std::sort(medianPool.begin(), medianPool.end());
    const double steadyMedian = medianPool.empty() ? 0.0 : medianPool[medianPool.size() / 2];
    const double longStopThreshold = std::max(50.0, steadyMedian * 1.5);
    std::uint64_t wrapLongStops = 0;
    for (double g : allGaps) { if (g > longStopThreshold) ++longStops; }
    for (double g : wrapGaps) { if (g > longStopThreshold) ++wrapLongStops; }
    std::printf("  长停阈值（相对判据）：稳态中位=%.2fms（非回绕间隔 %zu 个）⇒ 阈值=max(50, 1.5×中位)=%.2fms\n",
        steadyMedian, steadyGaps.size(), longStopThreshold);
    std::printf("  长停(>阈值)=%llu / 可测间隔=%zu（其中跨回绕 %llu）  最大间隔=%.2fms\n",
        (unsigned long long)longStops, allGaps.size(),
        (unsigned long long)wrapLongStops, gapMax);

    std::printf("  Δdecoded=%llu Δpresented=%llu Δdropped=%llu Δcoalesced=%llu swapChainPresents=%llu\n",
        (unsigned long long)SafeDelta(end.decodedVideoFrames, boot.decodedVideoFrames),
        (unsigned long long)presentedDelta,
        (unsigned long long)SafeDelta(end.droppedVideoFrames, boot.droppedVideoFrames),
        (unsigned long long)SafeDelta(end.coalescedVideoFrames, boot.coalescedVideoFrames),
        (unsigned long long)SafeDelta(end.swapChainPresents, boot.swapChainPresents));
    std::printf("  内核已计墙钟段（ms/帧）：转换=%.2f PresentWait=%.2f 设备锁=%.2f HW传输=%.2f  小计=%.2f\n",
        perFrame(SafeDelta(end.softwareConvert100ns, boot.softwareConvert100ns)),
        perFrame(SafeDelta(end.presentWait100ns, boot.presentWait100ns)),
        perFrame(SafeDelta(end.deviceLockWait100ns, boot.deviceLockWait100ns)),
        perFrame(SafeDelta(end.hardwareTransfer100ns, boot.hardwareTransfer100ns)),
        perFrame(SafeDelta(end.softwareConvert100ns, boot.softwareConvert100ns) +
                 SafeDelta(end.presentWait100ns, boot.presentWait100ns) +
                 SafeDelta(end.deviceLockWait100ns, boot.deviceLockWait100ns) +
                 SafeDelta(end.hardwareTransfer100ns, boot.hardwareTransfer100ns)));
    std::printf("  整进程 CPU=%.1fms / %.2fs ⇒ **%.2f ms/呈现帧**（=%.1f%% 单核，%u 核机）\n",
        cpuTotalMs, wall, cpuTotalMs / n, 100.0 * cpuTotalMs / (wall * 1000.0),
        std::thread::hardware_concurrency());

    // ── 逐秒 CPU ms/s 直方图：4K APNG 实测 1656/828/1437/1281 的双峰会被整窗均值抹平
    //    ⇒ 摊开成 min/p50/p90/max 并保留时间序，双峰要看得见。稳态窗=第 2 秒起（第 1 秒含起播瞬态）。
    {
        std::vector<double> hist;
        int minMark = 2;                       // 稳态窗下界：第 2 秒起
        std::size_t windowed = 0;
        for (const auto& s : cpuPerSec) { if (s.first >= minMark) { hist.push_back(s.second); ++windowed; } }
        const char* windowName = "稳态窗=第2秒起";
        if (hist.size() < 2) {   // 观察窗太短 ⇒ 退回整窗，并在行首**标注**是退回的
            minMark = 1;
            hist.clear();
            for (const auto& s : cpuPerSec) hist.push_back(s.second);
            windowed = hist.size();
            windowName = "稳态样本<2 退回整窗";
        }
        if (hist.empty()) {
            std::printf("  CPU ms/s 直方图：无逐秒样本（观察窗不足 1s？）⇒ 这条未判\n");
        } else {
            std::printf("  CPU ms/s 直方图（%s，n=%zu）：min=%.1f p50=%.1f p90=%.1f max=%.1f  极差=%.1f\n",
                windowName, hist.size(), Percentile(hist, 0.0), Percentile(hist, 0.5),
                Percentile(hist, 0.9), Percentile(hist, 1.0),
                Percentile(hist, 1.0) - Percentile(hist, 0.0));
            std::printf("    逐秒序列(时间序)：");
            std::size_t shown = 0;
            for (std::size_t i = 0; i < cpuPerSec.size(); ++i) {
                if (cpuPerSec[i].first < minMark) continue;
                if (shown >= 40) {
                    std::printf("…（共 %zu 个秒样本，只打前 40）", windowed);
                    break;
                }
                std::printf("[%zus %.0f] ", static_cast<std::size_t>(cpuPerSec[i].first), cpuPerSec[i].second);
                ++shown;
            }
            std::printf("\n");
        }
    }

    std::printf("  队列峰值=%llu  结束线程=%zu  私有=%.1fMB（开完流基线 %.1fMB）⇒ Δ=%.1fMB\n",
        queuedMax, CountThreads(), PrivateMB(), bootMB, PrivateMB() - bootMB);
    std::printf("  **结束时解码路=%d**（请求值见首行；GPU 请求落到 CPU 即动图无硬件解码器）\n",
                static_cast<int>(end.decodeMode));
    if (!g_events.empty()) {
        std::printf("  events：\n");
        for (const auto& e : g_events) std::printf("    [event] %s\n", e.c_str());
    }
    // ── 判决（2026-10-10 补牙）：① 量不到呈现就**拒绝判**，不许把"没量到"洗成 PASS；
    //    ② 阈值改成相对量后，阈差落在量化带（±2×轮询）内 ⇒ 测不准，记「不可辨」而不是猜方向。
    //    这不是放宽断言：绿只允许在**带外**成立。退出码 3=不可判，与 2（对象没起来）、1（真红）互斥。
    std::string notJudged;
    if (!g_openCompleted) notJudged = "OpenCompleted 未到达（内核没认这条流）";
    else if (presentedDelta == 0) notJudged = "0 次呈现（Open 成功但从未 present，一个间隔都没量到）";
    else if (allGaps.size() < 3) notJudged = "瞬态后可测呈现间隔仅 " +
        std::to_string(allGaps.size()) + " 个（<3 ⇒ 稳态中位无从算起）";
    else if (steadyMedian <= 0.0) notJudged = "稳态中位<=0ms（间隔样本退化）";

    std::printf("\n--- 判决 ---\n");
    if (!notJudged.empty()) {
        std::printf("  [未判] 长停：%s ⇒ 不打分，也不算过\n", notJudged.c_str());
        std::printf("AUDIT RESULT: NOT-JUDGED(%s)\n", notJudged.c_str());
        FFF3FP_Destroy(player);
        DestroyWindow(hwnd);
        return 3;
    }
    const double quant = 2.0 * static_cast<double>(g_pollMs);   // 一次间隔最多被轮询拉宽约 2 个轮询周期
    const double over = gapMax - longStopThreshold;             // >0 ⇒ 朝"更坏"方向越界
    std::printf("  长停判据：阈值=max(50, 1.5×稳态中位 %.2fms)=%.2fms；量化带=±%.2fms（2×轮询 %dms）"
                " ⇒ 只有最大间隔落在 [%.2f, %.2f]ms 之外才判方向；观测最大间隔=%.2fms\n",
        steadyMedian, longStopThreshold, quant, g_pollMs,
        longStopThreshold - quant, longStopThreshold + quant, gapMax);
    if (std::fabs(over) <= quant) {
        const char* boundBit = over >= 0.0
            ? "带上界一侧（最大间隔 ≥ 阈值，差一点算红）"
            : "带下界一侧（最大间隔 < 阈值，差一点算绿）";
        std::printf("  [不可辨] 长停：|%.2f - %.2f|=%.2fms ≤ 带 ±%.2fms ⇒ 轮询分辨率不够，位置在%s\n",
            gapMax, longStopThreshold, std::fabs(over), quant, boundBit);
        std::printf("AUDIT RESULT: NOT-JUDGED(长停阈差 %.2fms 落在量化带 ±%.2fms 内，%s；把 ACA_POLL_MS 调到 1 重跑才判得动)\n",
            std::fabs(over), quant, boundBit);
        FFF3FP_Destroy(player);
        DestroyWindow(hwnd);
        return 3;
    }
    const bool failed = over > 0.0;
    std::printf("  [%s] 长停：最大间隔在带**%s** ⇒ %s；长停(>阈值)=%llu / 可测间隔=%zu\n",
        failed ? "红" : "绿", failed ? "上界之外" : "下界之外",
        failed ? "有长停" : "无长停",
        (unsigned long long)longStops, allGaps.size());
    std::printf("AUDIT RESULT: %s\n", failed ? "FAIL(有长停)" : "PASS(无长停)");
    FFF3FP_Destroy(player);
    DestroyWindow(hwnd);
    return failed ? 1 : 0;
}
