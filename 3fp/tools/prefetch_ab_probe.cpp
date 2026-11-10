// E1 paired A/B probe for runtime prefetch (P0-a still hit).
//
// Build (from 3fp/, NOT the repo root):
//   python tools/build_probe.py prefetch_ab_probe
// Run (FFF.Native.dll + FFmpeg DLLs + FFF.PrefetchHost.exe must sit beside the exe):
//   prefetch_ab_probe.exe --rounds 14 --out <dir> --arm baseline <current> <n1> <n2> ...
//
// The arm itself is selected OUTSIDE the process by FFF3FP_PREFETCH_HIT=0|1,
// because the kernel caches that switch once at first use. --arm only labels
// the output; it never changes behaviour. Both arms run the SAME binary.
//
// Each round pushes a real plan (current + neighbours, STILL_BROWSING), waits
// for the worker to fill the still cache, then opens the three nearest
// neighbours and dumps ImageInfo / Snapshot / media-info JSON / a pixel hash.
// Everything lands in CSV + one JSON per open so a later pass can diff the two
// arms field by field instead of eyeballing a screenshot.

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

// GetSnapshot is gated on the INTERNAL SnapshotVersion (23), not on
// FFF3FP_GetApiVersion() (20). Hardcoding 20 silently returns InvalidArgument
// and leaves the struct zeroed -- a known false-green trap in this repo.
constexpr std::uint32_t kSnapshotVersion = 23u;

struct Events {
    bool openCompleted = false;
    char error[512] = {};
};
Events g_events;

void __cdecl OnEvent(void*, FFF3FPEvent type, const char* detail) {
    if (type == FFF3FPEvent::OpenCompleted) g_events.openCompleted = true;
    if (type == FFF3FPEvent::Error && detail != nullptr)
        std::strncpy(g_events.error, detail, sizeof(g_events.error) - 1);
}

// The kernel only hands log lines to a host-installed sink, and without one a
// correctly refused neighbour is indistinguishable from a read error: all you
// see is "that file never got cached". Install a sink so a scope rejection can
// be read back as evidence instead of inferred.
std::FILE* g_kernelLog = nullptr;

void __cdecl OnKernelLog(void*, const char* utf8Line) noexcept {
    if (g_kernelLog == nullptr || utf8Line == nullptr) return;
    std::fprintf(g_kernelLog, "%s\n", utf8Line);
    std::fflush(g_kernelLog);
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
    wc.lpszClassName = L"FFF3FP_PrefetchABProbe";
    if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    return CreateWindowExW(0, wc.lpszClassName, L"", WS_POPUP, 0, 0, 64, 64,
        nullptr, nullptr, wc.hInstance, nullptr);
}

std::uint64_t Fnv64(const float* values, std::size_t count) {
    std::uint64_t hash = 14695981039346656037ull;
    const auto* bytes = reinterpret_cast<const unsigned char*>(values);
    for (std::size_t i = 0; i < count * sizeof(float); ++i) {
        hash ^= bytes[i];
        hash *= 1099511628211ull;
    }
    return hash;
}

std::string CsvEscape(const std::string& text) {
    std::string out;
    for (const char c : text) {
        if (c == '"') out += "\"\"";
        else if (c == ',' || c == '\n' || c == '\r') out += ' ';
        else out += c;
    }
    return out;
}

bool ReadStatus(FFF3FPPrefetchStatus* status) {
    std::memset(status, 0, sizeof(*status));
    status->size = static_cast<std::uint32_t>(sizeof(*status));
    status->version = FFF3FP_PREFETCH_STATUS_VERSION;
    return FFF3FP_GetPrefetchStatus(status) == FFFResult::Success;
}

// --- cost accounting -------------------------------------------------------
// Wall time alone cannot say whether an Open is compute-bound or waiting. The
// playe is multi-threaded (worker, presenter, decoder), so total process CPU is
// the right denominator: a 1 s wall clock backed by 20 ms of CPU is a wait, not
// work -- and no prefetch change can shorten a wait that is not decoding.
struct Cost {
    double userMs = 0.0;
    double kernelMs = 0.0;
    std::uint64_t readBytes = 0;
    std::uint64_t readOps = 0;
    double cpuMs() const { return userMs + kernelMs; }
};

double FileTimeToMs(const FILETIME& value) {
    ULARGE_INTEGER v{};
    v.LowPart = value.dwLowDateTime;
    v.HighPart = value.dwHighDateTime;
    return static_cast<double>(v.QuadPart) / 10000.0;   // 100 ns ticks -> ms
}

Cost ReadCost() {
    Cost c;
    FILETIME creation{}, exitTime{}, kernel{}, user{};
    if (GetProcessTimes(GetCurrentProcess(), &creation, &exitTime, &kernel, &user)) {
        c.kernelMs = FileTimeToMs(kernel);
        c.userMs = FileTimeToMs(user);
    }
    IO_COUNTERS io{};
    if (GetProcessIoCounters(GetCurrentProcess(), &io)) {
        c.readBytes = io.ReadTransferCount;
        c.readOps = io.ReadOperationCount;
    }
    return c;
}

Cost CostDelta(const Cost& after, const Cost& before) {
    Cost d;
    d.userMs = after.userMs - before.userMs;
    d.kernelMs = after.kernelMs - before.kernelMs;
    d.readBytes = after.readBytes - before.readBytes;
    d.readOps = after.readOps - before.readOps;
    return d;
}

std::uint64_t PixelHash(FFF3FPHandle player) {
    FFF3FPRenderTargetInfo target{};
    target.size = static_cast<std::uint32_t>(sizeof(target));
    target.version = FFF3FP_GetApiVersion();
    if (FFF3FP_GetRenderTargetInfo(player, &target) != FFFResult::Success) return 0;
    const std::uint32_t side = 32u;
    if (target.destWidth < side || target.destHeight < side) return 0;
    const std::uint32_t x = target.destX + (target.destWidth - side) / 2u;
    const std::uint32_t y = target.destY + (target.destHeight - side) / 2u;
    std::vector<float> pixels(static_cast<std::size_t>(side) * side * 4u, 0.0f);
    std::uint32_t bitDepth = 0;
    if (FFF3FP_ReadVideoPixelRegion(player, x, y, side, side, pixels.data(),
            static_cast<std::uint32_t>(pixels.size()), &bitDepth) != FFFResult::Success) return 0;
    return Fnv64(pixels.data(), pixels.size());
}

int ReadState(FFF3FPHandle player) {
    FFF3FPSnapshot snap{};
    snap.size = static_cast<std::uint32_t>(sizeof(snap));
    snap.version = kSnapshotVersion;
    if (FFF3FP_GetSnapshot(player, &snap) != FFFResult::Success) return -1;
    return static_cast<int>(snap.state);
}

// FFF3FP_Close only enqueues DoClose (PlayerSession.cpp:1483), so the next
// FFF3FP_Open runs DoClose first on the same worker queue. Without waiting, every
// measured "open" also pays the previous media's teardown -- which is why a first
// open looks 7x cheaper than the ones that follow it. Both numbers matter:
//   settle   -> the cost of opening this media (teardown excluded)
//   no-wait  -> what a host actually waits for when it switches file to file
// Returns the wall time spent closing, so teardown is never silently folded in.
double CloseAndSettle(FFF3FPHandle player, bool settle) {
    const auto started = std::chrono::steady_clock::now();
    FFF3FP_Close(player);
    if (settle) {
        for (int k = 0; k < 3000; ++k) {
            if (ReadState(player) == static_cast<int>(FFF3FPState::Closed)) break;
            PumpMessages(2);
        }
    } else {
        PumpMessages(20);
    }
    return std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count();
}

} // namespace

int main(int argc, char** argv) {
    int rounds = 14;
    int waitMs = 6000;
    bool settle = true;
    // Chain mode: never close between opens. This is what a host actually does
    // when the user steps to the next picture -- DoOpen's leading
    // DoClose(Opening, preserveVideoOutput=true) keeps the renderer and only
    // resets per-media state. Closing instead calls videoRenderer_.Close()
    // (DoClose default), which tears the D3D device down and makes the next open
    // pay a full rebuild -- a cost no prefetch policy can remove and no host hits.
    bool chain = false;
    std::string logPath;
    std::string outDir;
    std::string arm;
    std::vector<std::string> paths;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--rounds" && i + 1 < argc) rounds = std::atoi(argv[++i]);
        else if (arg == "--wait-ms" && i + 1 < argc) waitMs = std::atoi(argv[++i]);
        else if (arg == "--no-settle") settle = false;
        else if (arg == "--chain") chain = true;
        else if (arg == "--out" && i + 1 < argc) outDir = argv[++i];
        else if (arg == "--arm" && i + 1 < argc) arm = argv[++i];
        else if (arg == "--log" && i + 1 < argc) logPath = argv[++i];
        else if (arg.rfind("--", 0) != 0) paths.push_back(arg);
    }
    if (arm.empty()) {
        char value[8]{};
        const DWORD n = GetEnvironmentVariableA("FFF3FP_PREFETCH_HIT", value, 8);
        arm = (n > 0 && value[0] == '0') ? "baseline" : "hit";
    }
    if (outDir.empty() || paths.size() < 2 || rounds < 1) {
        std::fprintf(stderr,
            "usage: prefetch_ab_probe --rounds N --wait-ms M --out DIR [--arm LABEL] <current> <neighbour...>\n");
        return 2;
    }
    CreateDirectoryA(outDir.c_str(), nullptr);

    const std::string csvPath = outDir + "\\rows_" + arm + ".csv";
    std::FILE* csv = std::fopen(csvPath.c_str(), "w");
    if (csv == nullptr) {
        std::fprintf(stderr, "cannot open %s\n", csvPath.c_str());
        return 2;
    }
    std::fprintf(csv,
        "arm,round,idx,path,planResult,openResult,openMs,completed,error,"
        "snapOk,presented,frameIndex,framePts,position100ns,decodeMode,"
        "imgOk,frameCount,loopCount,imgFlags,rotation,sourcePixelFormat,sourceBitDepth,"
        "iccSize,primaries,colorSpace,transfer,pixelHash,"
        "hits,misses,cachedStill,cachedProbe,cachedBytes,tier,cacheReadyMs,mediaJson,"
        "cpuMs,cpuUserMs,cpuKernelMs,ioReadBytes,ioReadOps,closeMs\n");

    if (!logPath.empty()) {
        g_kernelLog = std::fopen(logPath.c_str(), "w");
        if (g_kernelLog != nullptr) FFF3FP_SetLogCallback(&OnKernelLog, nullptr);
    }

    const HWND window = MakeHiddenWindow();
    FFF3FPConfiguration config{};
    config.size = static_cast<std::uint32_t>(sizeof(config));
    config.version = FFF3FP_GetApiVersion();
    config.outputWindow = window;
    // CPU route on both arms: the P0-a hit branch deliberately refuses a
    // hardware decoder, so a GPU arm would be a guaranteed miss by design.
    config.decodeMode = FFF3FPDecodeMode::Cpu;
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

    bool statusOk = false;
    // Idle baseline: the process owns a worker thread, a presenter and a D3D
    // device, so "CPU spent during an Open" is only meaningful net of what the
    // process burns doing nothing. Without this control a constant per-Open CPU
    // figure cannot be attributed to the Open at all.
    {
        const auto idleBefore = ReadCost();
        PumpMessages(600);
        const auto idle = CostDelta(ReadCost(), idleBefore);
        std::fprintf(stderr, "[idle] 600 ms of pump -> cpu=%.1f ms user=%.1f kernel=%.1f "
                             "readOps=%llu readBytes=%llu\n",
            idle.cpuMs(), idle.userMs, idle.kernelMs,
            static_cast<unsigned long long>(idle.readOps),
            static_cast<unsigned long long>(idle.readBytes));
    }
    const std::size_t wantFrames = paths.size() - 1;
    for (int round = 0; round < rounds; ++round) {
        std::string blob;
        for (std::size_t i = 0; i < paths.size(); ++i) {
            if (i != 0) blob += '\n';
            blob += paths[i];
        }
        FFF3FPPrefetchPlan plan{};
        plan.size = static_cast<std::uint32_t>(sizeof(plan));
        plan.version = FFF3FP_PREFETCH_PLAN_VERSION;
        plan.generation = static_cast<std::uint32_t>(round + 1);
        plan.flags = FFF3FP_PREFETCH_FLAG_STILL_BROWSING;
        plan.pathsUtf8 = blob.c_str();
        const auto planResult = FFF3FP_SetPrefetchPlan(&plan);

        const auto waitStart = std::chrono::steady_clock::now();
        FFF3FPPrefetchStatus cached{};
        statusOk = ReadStatus(&cached);
        double cacheReadyMs = -1.0;
        for (int tick = 0; tick < waitMs / 50; ++tick) {
            PumpMessages(50);
            FFF3FPPrefetchStatus now{};
            if (ReadStatus(&now) && now.cachedStillFrames >= wantFrames) {
                cached = now;
                cacheReadyMs = std::chrono::duration<double, std::milli>(
                    std::chrono::steady_clock::now() - waitStart).count();
                break;
            }
            cached = now;
        }

        const std::size_t last = paths.size() > 4 ? 4 : paths.size();
        for (std::size_t idx = 1; idx < last; ++idx) {
            g_events = Events{};
            double closeMs = -1.0;
            FFF3FPPrefetchStatus before{};
            ReadStatus(&before);
            const auto costBefore = ReadCost();
            const auto started = std::chrono::steady_clock::now();
            const auto openResult = FFF3FP_Open(player, paths[idx].c_str());
            // 2 ms granularity, not 25: the whole point of P0-a is a few tens of
            // milliseconds of first-frame decode, which a 25 ms poll cannot
            // resolve. The cap (3000 ticks) keeps the 6 s ceiling of the old
            // 240 x 25 ms loop so a 108 MP still can still finish.
            for (int k = 0; k < 3000 && !g_events.openCompleted; ++k) PumpMessages(2);
            const double openMs = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count();
            const auto cost = CostDelta(ReadCost(), costBefore);

            FFF3FPSnapshot snap{};
            std::memset(&snap, 0, sizeof(snap));
            snap.size = static_cast<std::uint32_t>(sizeof(snap));
            snap.version = kSnapshotVersion;
            const bool snapOk = FFF3FP_GetSnapshot(player, &snap) == FFFResult::Success;

            FFF3FPImageInfo info{};
            std::memset(&info, 0, sizeof(info));
            info.size = static_cast<std::uint32_t>(sizeof(info));
            info.version = FFF3FP_GetApiVersion();
            const bool imgOk = FFF3FP_GetImageInfo(player, &info) == FFFResult::Success;

            const std::uint64_t pixelHash = PixelHash(player);

            std::vector<char> media(65536, 0);
            std::uint32_t needed = 0;
            FFF3FP_GetMediaInfo(player, media.data(),
                static_cast<std::uint32_t>(media.size()), &needed);
            char jsonName[128]{};
            std::snprintf(jsonName, sizeof(jsonName), "%s_r%02d_i%02d.json",
                arm.c_str(), round, static_cast<int>(idx));
            const std::string jsonPath = outDir + "\\" + jsonName;
            if (std::FILE* jf = std::fopen(jsonPath.c_str(), "wb")) {
                const std::size_t len = std::strlen(media.data());
                if (len > 0) std::fwrite(media.data(), 1, len, jf);
                std::fclose(jf);
            }

            FFF3FPPrefetchStatus after{};
            ReadStatus(&after);

            std::fprintf(csv,
                "%s,%d,%zu,\"%s\",%d,%d,%.3f,%d,\"%s\","
                "%d,%llu,%lld,%lld,%lld,%d,"
                "%d,%d,%d,%u,%u,%d,%u,%u,%d,%d,%d,"
                "%llu,%llu,%llu,%u,%u,%llu,%u,%.3f,%s,"
                "%.3f,%.3f,%.3f,%llu,%llu,%.3f\n",
                arm.c_str(), round, idx, CsvEscape(paths[idx]).c_str(),
                static_cast<int>(planResult), static_cast<int>(openResult), openMs,
                g_events.openCompleted ? 1 : 0, CsvEscape(g_events.error).c_str(),
                snapOk ? 1 : 0,
                static_cast<unsigned long long>(snap.presentedVideoFrames),
                static_cast<long long>(snap.frameIndex),
                static_cast<long long>(snap.framePts),
                static_cast<long long>(snap.position100ns),
                static_cast<int>(snap.decodeMode),
                imgOk ? 1 : 0, info.frameCount, info.loopCount, info.flags,
                info.rotationQuarterTurns, info.sourcePixelFormat, info.sourceBitDepth,
                info.iccProfileSizeBytes, info.colorPrimaries, info.colorSpace,
                info.colorTransfer,
                static_cast<unsigned long long>(pixelHash),
                static_cast<unsigned long long>(after.hits),
                static_cast<unsigned long long>(after.misses),
                after.cachedStillFrames, after.cachedProbeEntries,
                static_cast<unsigned long long>(after.cachedBytes), after.tier,
                cacheReadyMs, jsonName,
                cost.cpuMs(), cost.userMs, cost.kernelMs,
                static_cast<unsigned long long>(cost.readBytes),
                static_cast<unsigned long long>(cost.readOps), closeMs);
            std::fflush(csv);

            if (!chain) closeMs = CloseAndSettle(player, settle);
        }
        std::fprintf(stderr, "[%s] round %d/%d cachedStill=%u hits=%llu misses=%llu\n",
            arm.c_str(), round + 1, rounds, cached.cachedStillFrames,
            static_cast<unsigned long long>(cached.hits),
            static_cast<unsigned long long>(cached.misses));
    }

    std::fclose(csv);
    if (chain) { FFF3FP_Close(player); PumpMessages(300); }
    if (g_kernelLog != nullptr) {
        FFF3FP_SetLogCallback(nullptr, nullptr);
        std::fclose(g_kernelLog);
        g_kernelLog = nullptr;
    }
    FFF3FP_Destroy(player);
    std::fprintf(stdout, "DONE arm=%s rows=%d csv=%s statusOk=%d\n",
        arm.c_str(), rounds * static_cast<int>(paths.size() > 4 ? 3 : paths.size() - 1),
        csvPath.c_str(), statusOk ? 1 : 0);
    return 0;
}
