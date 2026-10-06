// ============================================================================
// loop_stutter_probe.cpp —— 动图循环播放「卡顿」诊断探针（2026-09-26）
//
// 目的：量化动图在**反复循环**时的卡顿。核心量测：
//   ① 每帧到达的**墙钟间隔**（4ms 精度轮询 presentedVideoFrames 的增量）
//   ② **回绕点**（position100ns 从尾部跳回 ~0）前后的帧间隔 vs 稳态帧间隔
//   ③ 整个观察窗口的间隔分布（min/中位/p90/max）与「长停」次数
//
// 判据（卡顿）：
//   · 若回绕点的间隔显著大于稳态 ⇒ 循环接缝处掉帧（真卡顿）
//   · 若整体间隔都远大于源帧时长 ⇒ 帧率整体偏低（不是循环特有）
//
// 用法：loop_stutter_probe <媒体路径> [观察秒数，默认 6]
//   环境变量 LSP_POLL_MS：轮询间隔 ms（默认 4，可设 1 提高分辨率）
//   ⚠⚠ 传 Windows 路径必须用**单引号**或 `cygpath -w`：
//       ./loop_stutter_probe.exe 'C:\PLAN\...\x.gif' 12      ← ✅
//       ./loop_stutter_probe.exe "C:\\PLAN\\...\\x.gif" 12    ← ❌ Git Bash 双引号内
//         `\\` 被折叠成单个反斜杠，后续 `\P`/`\3` 被路径解析吞掉 ⇒ frameCount=0（打开失败）
// 依赖：同目录 FFF.Native.dll（与 verify_image_mode.exe 相同）
// 编译：python tools/build_probe.py loop_stutter_probe
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>
#include <algorithm>
#include <string>

#include "FFF.Player.Api.h"

namespace {

char g_eventLog[8192] = {};
volatile bool g_openCompleted = false;

void __cdecl EventCallback(void* /*context*/, FFF3FPEvent e, const char* json) {
    if (json == nullptr) json = "";
    const char* name = "?";
    switch (e) {
        case FFF3FPEvent::OpenCompleted: name = "OpenCompleted"; g_openCompleted = true; break;
        case FFF3FPEvent::PlaybackEnded: name = "PlaybackEnded"; break;
        case FFF3FPEvent::ColorModeChanged: name = "ColorModeChanged"; break;
        case FFF3FPEvent::Error: name = "Error"; break;
        case FFF3FPEvent::StateChanged: name = "StateChanged"; break;
        default: break;
    }
    char line[512];
    std::snprintf(line, sizeof(line), "    [event] %s %s\n", name, json);
    const auto used = std::strlen(g_eventLog);
    if (used + std::strlen(line) < sizeof(g_eventLog))
        std::memcpy(g_eventLog + used, line, std::strlen(line) + 1);
}

LRESULT CALLBACK WindowProcedure(HWND h, UINT m, WPARAM w, LPARAM l) {
    return DefWindowProcW(h, m, w, l);
}

// 轮询间隔（ms）：越小量化误差越低，但 CPU 占用越高。可用环境变量 LSP_POLL_MS 覆盖。
static int g_pollMs = 4;

void PumpFor(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message{};
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(g_pollMs));
    }
}

FFF3FPSnapshot ReadSnapshot(FFF3FPHandle player) {
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = 8;
    FFF3FP_GetSnapshot(player, &s);
    return s;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) { std::printf("usage: loop_stutter_probe <media path> [seconds]\n"); return 2; }
    const char* path = argv[1];
    const double seconds = argc >= 3 ? std::atof(argv[2]) : 6.0;
    if (const char* env = std::getenv("LSP_POLL_MS")) {
        const int v = std::atoi(env);
        if (v >= 1 && v <= 100) g_pollMs = v;   // 上限 100ms 防呆
    }

    std::printf("=== 动图循环卡顿诊断 ===\n");
    std::printf("file: %s  观察 %.1fs  轮询 %dms  API=%u\n", path, seconds, g_pollMs, FFF3FP_GetApiVersion());

    WNDCLASSW wc{};
    wc.lpfnWndProc = WindowProcedure;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"loopStutterWindow";
    RegisterClassW(&wc);

    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"loop-stutter", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
    if (hwnd == nullptr) { std::printf("创建窗口失败\n"); return 1; }

    FFF3FPConfiguration config{};
    config.size = sizeof(config);
    config.version = FFF3FP_GetApiVersion();   // ⚠ 必须是 API 版本（内核严格相等校验）
    config.outputWindow = hwnd;
    config.decodeMode = FFF3FPDecodeMode::Cpu;
    config.colorMode = FFF3FPColorMode::MapToSdr;
    config.sdrPeakNits = 100.0f;
    config.hdrPeakNits = 0.0f;
    config.sdrPaperWhiteNits = 203.0f;
    config.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    config.forceHdrOutput = 0u;
    config.preferredAdapterIndex = -1;   // ⚠ 必须显式 -1（0 是合法索引）
    config.eventCallback = &EventCallback;
    config.eventCallbackContext = nullptr;

    FFF3FPHandle player = nullptr;
    const auto createResult = FFF3FP_Create(&config, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败: %d\n", static_cast<int>(createResult));
        return 1;
    }

    const auto openResult = FFF3FP_Open(player, path);
    std::printf("Open: %d\n", static_cast<int>(openResult));
    for (int i = 0; i < 300 && !g_openCompleted; ++i) PumpFor(50);
    std::printf("OpenCompleted: %s\n", g_openCompleted ? "yes" : "no");
    PumpFor(300);   // 让首帧落地

    FFF3FPImageInfo info{};
    info.size = sizeof(info);
    info.version = 1;
    HMODULE mod = GetModuleHandleW(L"FFF.Native.dll");
    if (mod == nullptr) mod = LoadLibraryW(L"FFF.Native.dll");
    using GetInfoFn = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPImageInfo*);
    auto getInfo = mod ? reinterpret_cast<GetInfoFn>(
        reinterpret_cast<void*>(GetProcAddress(mod, "FFF3FP_GetImageInfo"))) : nullptr;
    if (getInfo) {
        getInfo(player, &info);
        std::printf("frameCount=%d flags=0x%X ANIMATED=%d STATIC=%d\n",
            info.frameCount, info.flags,
            (info.flags & FFF3FP_IMAGE_FLAG_ANIMATED) ? 1 : 0,
            (info.flags & FFF3FP_IMAGE_FLAG_STATIC) ? 1 : 0);
    }

    // ── 循环观察：4ms 轮询，记录每次 presentedVideoFrames 变化的时刻 ──
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);

    struct Sample { double ms; std::uint64_t presented; std::int64_t posUs; std::uint64_t dropped; std::uint64_t decoded;
                    std::uint64_t coalesced; std::uint32_t queued; std::int64_t frameIndex; };
    std::vector<Sample> samples;
    const auto t0 = std::chrono::steady_clock::now();
    const auto deadline = t0 + std::chrono::milliseconds(static_cast<long long>(seconds * 1000));
    std::uint64_t lastPresented = ReadSnapshot(player).presentedVideoFrames;

    while (std::chrono::steady_clock::now() < deadline) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        const auto snap = ReadSnapshot(player);
        if (snap.presentedVideoFrames != lastPresented) {
            const auto ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - t0).count();
            samples.push_back({ ms,
                static_cast<std::uint64_t>(snap.presentedVideoFrames),
                static_cast<std::int64_t>(snap.position100ns / 10),
                static_cast<std::uint64_t>(snap.droppedVideoFrames),
                static_cast<std::uint64_t>(snap.decodedVideoFrames),
                static_cast<std::uint64_t>(snap.coalescedVideoFrames),
                static_cast<std::uint32_t>(snap.queuedVideoFrames),
                static_cast<std::int64_t>(snap.frameIndex) });
            lastPresented = snap.presentedVideoFrames;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(g_pollMs));
    }

    std::printf("\n--- 逐帧记录（共 %zu 次呈现）---\n", samples.size());
    std::printf("  #   墙钟(s)   间隔(ms)   位置(s)  fIdx  queued  coalesced  回绕?\n");
    std::vector<double> gaps;
    int wraps = 0, longStops = 0;
    // 「长停」阈值：相对源帧间隔而言，>1.5 倍源间隔才算异常停顿。
    // 固定 200ms 对 5fps 源（间隔 200ms）会每帧误判 ⇒ 必须先算出稳态中位再定阈值。
    double steadyMedian = 100.0;
    {
        std::vector<double> tmp;
        for (std::size_t i = 1; i < samples.size(); ++i) {
            const auto d = samples[i].posUs - samples[i - 1].posUs;
            if (d >= -200'000) tmp.push_back(samples[i].ms - samples[i - 1].ms);
        }
        if (!tmp.empty()) { std::sort(tmp.begin(), tmp.end()); steadyMedian = tmp[tmp.size() / 2]; }
    }
    const double longStopThreshold = std::max(50.0, steadyMedian * 1.5);
    for (std::size_t i = 0; i < samples.size(); ++i) {
        double gap = 0; bool wrap = false;
        if (i > 0) {
            gap = samples[i].ms - samples[i - 1].ms;
            gaps.push_back(gap);
            // 回绕判据：位置比上一帧小了 > 200ms（正常帧间隔不可能这么大）
            const auto deltaUs = samples[i].posUs - samples[i - 1].posUs;
            if (deltaUs < -200'000) { wrap = true; ++wraps; }
        }
        const bool longStop = gap > longStopThreshold;   // >1.5× 稳态中位 ⇒ 异常停顿
        if (longStop) ++longStops;
        const bool droppedNow = i > 0 && samples[i].dropped > samples[i - 1].dropped;
        const bool coalescedNow = i > 0 && samples[i].coalesced > samples[i - 1].coalesced;
        // ⚠ 不要把 decoded 与 presented 相减：presented 由 GetSnapshot 实时从 renderer 取，
        //    而 decoded 是 marshal 到 snapshot_ 的旧值 ⇒ 新鲜度不同，相减会下溢。
        std::printf("  %3zu  %8.3f  %9.2f  %8.3f  %5lld  %6u  %9llu  %s%s%s\n",
            i, samples[i].ms / 1000.0, gap, samples[i].posUs / 1e6,
            static_cast<long long>(samples[i].frameIndex),
            samples[i].queued,
            static_cast<unsigned long long>(samples[i].coalesced),
            droppedNow ? "★DROP " : "", coalescedNow ? "★COAL " : "", wrap ? "★WRAP" : "");
    }

    // ── 统计 ──
    if (!gaps.empty()) {
        auto sorted = gaps;
        std::sort(sorted.begin(), sorted.end());
        const auto pct = [&](double p) { return sorted[std::min(sorted.size() - 1,
            static_cast<std::size_t>(p * (sorted.size() - 1)))]; };
        double sum = 0; for (double g : gaps) sum += g;
        std::printf("\n--- 帧间隔统计（全部 %zu 个）---\n", gaps.size());
        std::printf("  均值=%.2fms  最小=%.2fms  中位=%.2fms  p90=%.2fms  最大=%.2fms\n",
            sum / gaps.size(), sorted.front(), pct(0.5), pct(0.9), sorted.back());
        std::printf("  等效帧率：均值 %.1f fps / 中位 %.1f fps\n",
            1000.0 / (sum / gaps.size()), 1000.0 / pct(0.5));

        // ★ 分组：把「回绕帧」（位置回退）与「稳态帧」分开 —— 循环接缝是否掉帧只有这里能看出来
        std::vector<double> wrapGaps, steadyGaps;
        for (std::size_t i = 1; i < samples.size(); ++i) {
            const double g = samples[i].ms - samples[i - 1].ms;
            const auto d = samples[i].posUs - samples[i - 1].posUs;
            if (d < -200'000) wrapGaps.push_back(g); else steadyGaps.push_back(g);
        }
        const auto stat = [](std::vector<double>& v) {
            if (v.empty()) return std::string("(无)");
            auto s = v; std::sort(s.begin(), s.end());
            double t = 0; for (double x : s) t += x;
            char buf[160];
            std::snprintf(buf, sizeof(buf), "n=%zu 均值=%.2fms 中位=%.2fms 最大=%.2fms",
                v.size(), t / v.size(), s[s.size() / 2], s.back());
            return std::string(buf);
        };
        std::printf("\n--- ★ 分组统计（循环接缝是否掉帧的关键判据）---\n");
        std::printf("  稳态帧(位置递增): %s\n", stat(steadyGaps).c_str());
        std::printf("  回绕帧(位置回退): %s\n", stat(wrapGaps).c_str());
        if (!wrapGaps.empty() && !steadyGaps.empty()) {
            auto ws = wrapGaps; std::sort(ws.begin(), ws.end());
            auto ss = steadyGaps; std::sort(ss.begin(), ss.end());
            const double wMed = ws[ws.size() / 2], sMed = ss[ss.size() / 2];
            std::printf("  ⇒ 回绕帧比稳态帧 %+.2fms（%.0f%%）",
                wMed - sMed, (wMed / sMed - 1.0) * 100.0);
            std::printf("%s\n", (wMed - sMed) > 25.0 ? "  ⚠ 接缝处明显掉帧" : "  （接缝处未明显掉帧）");
        }
    }
    std::printf("  回绕次数=%d   长停(>%.0fms)次数=%d\n", wraps, longStopThreshold, longStops);
    if (!samples.empty()) {
        std::printf("  droppedVideoFrames: %llu → %llu（净增 %llu）\n",
            static_cast<unsigned long long>(samples.front().dropped),
            static_cast<unsigned long long>(samples.back().dropped),
            static_cast<unsigned long long>(samples.back().dropped - samples.front().dropped));
        std::printf("  decodedVideoFrames: %llu → %llu（净增 %llu）\n",
            static_cast<unsigned long long>(samples.front().decoded),
            static_cast<unsigned long long>(samples.back().decoded),
            static_cast<unsigned long long>(samples.back().decoded - samples.front().decoded));
        std::printf("  coalescedVideoFrames: %llu → %llu（净增 %llu）★渲染侧跳帧\n",
            static_cast<unsigned long long>(samples.front().coalesced),
            static_cast<unsigned long long>(samples.back().coalesced),
            static_cast<unsigned long long>(samples.back().coalesced - samples.front().coalesced));
    }
    std::printf("  最终 PlaybackEnded=%s\n",
        std::strstr(g_eventLog, "PlaybackEnded") ? "yes (BAD)" : "no (good)");
    if (std::strlen(g_eventLog) > 0) std::printf("--- events ---\n%s", g_eventLog);

    FFF3FP_Destroy(player);
    DestroyWindow(hwnd);
    std::printf("=== done ===\n");
    return 0;
}
