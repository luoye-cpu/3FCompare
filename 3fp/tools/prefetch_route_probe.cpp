// E1 probe for P1-a: hardware trial-decode refusal memory (route replay).
//
// Build (from 3fp/, NOT the repo root):
//   python tools/build_probe.py prefetch_route_probe
//
// What it measures
//   The kernel remembers "this file's hardware route was refused" as a small
//   POD keyed by (path, routeKey, file fingerprint). PlayerSession drops its
//   in-session copy in ConfigurePlaybackPerformance (state must be Idle), so a
//   host that reconfigures performance between opens pays a fresh trial decode
//   unless the memory is replayed before OpenDecoder.
//
//   Each round is:  open -> close -> ConfigurePlaybackPerformance (clears the
//   in-session list) -> open again. The SECOND open is the measured one: with
//   the replay arm it must take the same route without re-trying hardware.
//
//   The arm is chosen OUTSIDE the process by FFF3FP_PREFETCH_ROUTE=0|1 (the
//   kernel caches the switch once at first use). --arm only labels output.
//
// A video whose hardware trial fails is required, e.g. H.264 High 4:4:4
// Predictive (no D3D11VA surface). Build one with:
//   ffmpeg -f lavfi -i testsrc2=size=640x480:rate=25 -frames:v 50 \
//          -c:v libx264 -pix_fmt yuv444p -qp 18 hwfail_h264_444.mp4

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"
#include "3FP/Api/FFF.Player.Prefetch.h"

namespace {

constexpr std::uint32_t kSnapshotVersion = 23u;

struct Events {
    bool openCompleted = false;
    bool error = false;
    char detail[512] = {};
};

Events g_events;

// FFF3FP_KernelLogImpl forwards ONLY to a host-installed sink (PlayerApi.cpp:84),
// so a probe that never registers one sees no trace at all. The route memory's
// only observable besides wall time is these two lines.
std::FILE* g_trace = nullptr;

void __cdecl OnLog(void*, const char* line) noexcept {
    if (line == nullptr || g_trace == nullptr) return;
    if (std::strstr(line, "route-note") == nullptr &&
        std::strstr(line, "route-hit") == nullptr &&
        std::strstr(line, "action=plan") == nullptr) return;
    std::fprintf(g_trace, "%s\n", line);
    std::fflush(g_trace);
}

void __cdecl OnEvent(void*, FFF3FPEvent type, const char* detail) {
    if (type == FFF3FPEvent::OpenCompleted) g_events.openCompleted = true;
    if (type == FFF3FPEvent::Error) {
        g_events.error = true;
        if (detail != nullptr) std::strncpy(g_events.detail, detail, sizeof(g_events.detail) - 1);
    }
}

LRESULT CALLBACK WndProc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    return DefWindowProcW(w, m, wp, lp);
}

void PumpMessages(int milliseconds) {
    const auto start = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start < std::chrono::milliseconds(milliseconds)) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Sleep(1);
    }
}

HWND MakeHiddenWindow() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = &WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFF3FP_PrefetchRouteProbe";
    if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    return CreateWindowExW(0, wc.lpszClassName, L"", WS_POPUP, 0, 0, 64, 64,
        nullptr, nullptr, wc.hInstance, nullptr);
}

// Opens once and returns the wall time until the OpenCompleted event, or -1.
double OpenOnce(FFF3FPHandle player, const char* path, FFF3FPSnapshot* snap) {
    g_events = Events{};
    const auto started = std::chrono::steady_clock::now();
    if (FFF3FP_Open(player, path) != FFFResult::Success) return -1.0;
    for (int k = 0; k < 4000 && !g_events.openCompleted && !g_events.error; ++k) PumpMessages(2);
    const double ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count();
    std::memset(snap, 0, sizeof(*snap));
    snap->size = static_cast<std::uint32_t>(sizeof(*snap));
    snap->version = kSnapshotVersion;
    if (FFF3FP_GetSnapshot(player, snap) != FFFResult::Success) return -1.0;
    return ms;
}

FFF3FPState ReadState(FFF3FPHandle player) {
    FFF3FPSnapshot snap{};
    snap.size = static_cast<std::uint32_t>(sizeof(snap));
    snap.version = kSnapshotVersion;
    if (FFF3FP_GetSnapshot(player, &snap) != FFFResult::Success) return FFF3FPState::Idle;
    return snap.state;
}

// ConfigurePlaybackPerformance is gated on Idle (PlayerSession.cpp:1372) and it
// is the only place that drops the in-session refusal list (:1378). Close()
// leaves the session in Closed, so it can never clear the list -- the reachable
// sequence is Stop() (-> DoClose(Idle), PlayerSession.cpp:1482) followed by the
// configure call. That is exactly what a host does when it re-applies the
// playback performance profile, so this probe reproduces the real path.
bool StopAndConfigure(FFF3FPHandle player) {
    const auto state = ReadState(player);
    if (state == FFF3FPState::Ready || state == FFF3FPState::Playing ||
        state == FFF3FPState::Paused || state == FFF3FPState::Ended) {
        FFF3FP_Stop(player);
    }
    for (int k = 0; k < 500; ++k) {
        if (ReadState(player) == FFF3FPState::Idle) break;
        PumpMessages(2);
    }
    return FFF3FP_ConfigurePlaybackPerformance(player, -1, 1u, 1u) == FFFResult::Success;
}

// Plays briefly so a real frame is presented: without this presentedVideoFrames
// stays 0 and the pixel hash is meaningless, which would make the picture-parity
// check vacuous instead of a real comparison.
void PlayBriefly(FFF3FPHandle player) {
    FFF3FP_Play(player);
    PumpMessages(260);
    FFF3FP_Pause(player);
    PumpMessages(20);
}

std::uint64_t PixelHash(FFF3FPHandle player) {
    FFF3FPRenderTargetInfo target{};
    target.size = static_cast<std::uint32_t>(sizeof(target));
    target.version = FFF3FP_GetApiVersion();
    if (FFF3FP_GetRenderTargetInfo(player, &target) != FFFResult::Success) return 0;
    const std::uint32_t side = 16u;
    if (target.destWidth < side || target.destHeight < side) return 0;
    const std::uint32_t x = target.destX + (target.destWidth - side) / 2u;
    const std::uint32_t y = target.destY + (target.destHeight - side) / 2u;
    std::vector<float> pixels(static_cast<std::size_t>(side) * side * 4u, 0.0f);
    std::uint32_t bitDepth = 0;
    if (FFF3FP_ReadVideoPixelRegion(player, x, y, side, side, pixels.data(),
            static_cast<std::uint32_t>(pixels.size()), &bitDepth) != FFFResult::Success) return 0;
    std::uint64_t hash = 14695981039346656037ull;
    const auto* bytes = reinterpret_cast<const unsigned char*>(pixels.data());
    for (std::size_t i = 0; i < pixels.size() * sizeof(float); ++i) {
        hash ^= bytes[i];
        hash *= 1099511628211ull;
    }
    return hash;
}

} // namespace

int main(int argc, char** argv) {
    int rounds = 14;
    std::string outDir;
    std::string arm;
    std::string path;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--rounds" && i + 1 < argc) rounds = std::atoi(argv[++i]);
        else if (arg == "--out" && i + 1 < argc) outDir = argv[++i];
        else if (arg == "--arm" && i + 1 < argc) arm = argv[++i];
        else if (arg.rfind("--", 0) != 0) path = arg;
    }
    if (arm.empty()) {
        char value[8]{};
        const DWORD n = GetEnvironmentVariableA("FFF3FP_PREFETCH_ROUTE", value, 8);
        arm = (n > 0 && value[0] == '0') ? "route_off" : "route_on";
    }
    if (outDir.empty() || path.empty() || rounds < 1) {
        std::fprintf(stderr, "usage: prefetch_route_probe --rounds N --out DIR [--arm LABEL] <video>\n");
        return 2;
    }
    CreateDirectoryA(outDir.c_str(), nullptr);
    const std::string csvPath = outDir + "\\rows_" + arm + ".csv";
    std::FILE* csv = std::fopen(csvPath.c_str(), "w");
    if (csv == nullptr) return 2;
    std::fprintf(csv,
        "arm,round,coldMs,coldDecodeMode,warmMs,warmDecodeMode,coldPresented,"
        "warmPresented,warmHash,hashEq,deltaMs,openResult,configured,cachedProbe,tier\n");

    const std::string tracePath = outDir + "\\trace_" + arm + ".log";
    g_trace = std::fopen(tracePath.c_str(), "w");
    FFF3FP_SetLogCallback(&OnLog, nullptr);

    const HWND window = MakeHiddenWindow();
    FFF3FPConfiguration config{};
    config.size = static_cast<std::uint32_t>(sizeof(config));
    config.version = FFF3FP_GetApiVersion();
    config.outputWindow = window;
    // D3D11 on purpose: this probe exists to observe a hardware refusal.
    config.decodeMode = FFF3FPDecodeMode::D3D11;
    config.colorMode = FFF3FPColorMode::MapToSdr;
    config.sdrPeakNits = 100.0f;
    config.hdrPeakNits = 0.0f;
    config.sdrPaperWhiteNits = 203.0f;
    config.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    config.preferredAdapterIndex = -1;
    config.eventCallback = &OnEvent;
    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&config, &player) != FFFResult::Success || player == nullptr) {
        std::fprintf(stderr, "FFF3FP_Create failed\n");
        std::fclose(csv);
        return 3;
    }

    // A plan naming the file as "current media" is not required for the route
    // memory (it keys on the opened path), but it keeps the service awake and
    // makes cachedProbe observable.
    FFF3FPPrefetchPlan plan{};
    plan.size = static_cast<std::uint32_t>(sizeof(plan));
    plan.version = FFF3FP_PREFETCH_PLAN_VERSION;
    plan.generation = 1u;
    plan.flags = 0u;   // nothing is browsing: the route memory keys on the open path
    plan.pathsUtf8 = path.c_str();
    FFF3FP_SetPrefetchPlan(&plan);
    PumpMessages(50);

    int result = 0;
    for (int round = 0; round < rounds; ++round) {
        FFF3FPSnapshot cold{};
        const double coldMs = OpenOnce(player, path.c_str(), &cold);
        PlayBriefly(player);
        const std::uint64_t coldHash = PixelHash(player);

        // Drop the in-session route list the way the host does when it
        // re-applies the playback performance profile.
        const bool configured = StopAndConfigure(player);

        FFF3FPSnapshot warm{};
        const double warmMs = OpenOnce(player, path.c_str(), &warm);
        PlayBriefly(player);
        const std::uint64_t warmHash = PixelHash(player);
        FFF3FP_Stop(player);
        PumpMessages(10);

        FFF3FPPrefetchStatus status{};
        status.size = static_cast<std::uint32_t>(sizeof(status));
        status.version = FFF3FP_PREFETCH_STATUS_VERSION;
        FFF3FP_GetPrefetchStatus(&status);

        const bool same = coldHash != 0 && coldHash == warmHash;
        if (coldMs < 0 || warmMs < 0 || !same || !configured) result = 1;
        std::fprintf(csv, "%s,%d,%.3f,%d,%.3f,%d,%llu,%llu,%llu,%d,%.3f,%d,%d,%u,%u\n",
            arm.c_str(), round, coldMs, static_cast<int>(cold.decodeMode),
            warmMs, static_cast<int>(warm.decodeMode),
            static_cast<unsigned long long>(cold.presentedVideoFrames),
            static_cast<unsigned long long>(warm.presentedVideoFrames),
            static_cast<unsigned long long>(warmHash),
            same ? 1 : 0, warmMs - coldMs,
            coldMs < 0 || warmMs < 0 ? -1 : 0, configured ? 1 : 0,
            status.cachedProbeEntries, status.tier);
        std::fflush(csv);
        std::fprintf(stderr, "[%s] round %d/%d cold=%.1f warm=%.1f mode=%d cfg=%d hashEq=%d\n",
            arm.c_str(), round + 1, rounds, coldMs, warmMs,
            static_cast<int>(warm.decodeMode), configured ? 1 : 0, same ? 1 : 0);
    }

    std::fclose(csv);
    FFF3FP_SetLogCallback(nullptr, nullptr);
    if (g_trace != nullptr) std::fclose(g_trace);
    FFF3FP_Destroy(player);
    std::fprintf(stdout, "DONE arm=%s rounds=%d csv=%s ok=%d\n",
        arm.c_str(), rounds, csvPath.c_str(), result == 0 ? 1 : 0);
    return 0;
}
