// Headless video-playback performance probe, built against the SHIPPING kernel
// tree (third_party/fff_project). It exists because 3fp/tools/upload_bench.cpp
// (a) pins colorMode to MapToSdr, so it never exercises the HDR presentation
// path, and (b) links the 3fp/kernel tree, which contains no ST 2094-40 code at
// all — measuring HDR10+ overhead with it would read an unconnected gauge.
//
// Headless (outputWindow = nullptr) keeps the measurement free of the vsync-bound
// Present wait, which the repo already proved unusable as an A/B metric
// (in-group swing 2.1~5.9 ms, larger than between-group differences).
//
// Every sample runs in its own process: repeated Create/Destroy of the D3D11
// decoder inside one process fails from the second round onwards.
//
// Usage:
//   play_back_perf_bench <media> [--decode cpu|gpu] [--color sdr|hdr]
//                        [--forcehdr 0|1] [--sdrscrgb 0|1] [--window WxH]
//                        [--seconds N] [--runs R] [--adaptive 0|1] [--one]
// Without --window the session is headless: only decode and per-frame HDR
// metadata processing are on the clock. The HDR presentation path requires a
// window (see CreateProbeWindow).
//
// --adaptive 1 mirrors the shipping host's config (adaptive decoder-thread ladder:
// 1 / min 4 / max 32 / grow 10 / shrink 2). Leave it off for the historical
// kernel-ABI default. This is what makes the placement policy's three-tier path
// participate the same way it does in the product (see 播放器会话.vb 原生配置).
//
// 2026-11-12 additions for the CPU-placement A/B (docs/151): the probe now also
// prints, per sample:
//   ENV       the FFF_* variables the child actually saw (env-injection self-proof)
//   PLACEMENT one FFF3FP_GetCpuPlacementSnapshot read mid-playback (tier/domain/
//             flags/set counts/worker+presenter selection/decoder count)
//   PRESENT   swap-chain flip cadence (p50/p99/max gap in ms) from
//             FFF3FP_GetPresentationCounter polled during the playback window
//   KLOG      the kernel's own PLACEMENT_* diagnostics captured through
//             FFF3FP_SetLogCallback (the policy's own account of what it applied)
// The SAMPLE line is unchanged, so existing parsers keep working.
//
// The config block on each sample is part of the instrument, not decoration: if
// the colour mode, decode mode or output bit depth is not what the arm claims to
// measure, the numbers are meaningless and the summary says so with a non-zero exit.

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"
#include "3FP/Api/FFF.Player.CpuPlacement.h"

namespace {

bool g_openCompleted = false;

void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

// ---- kernel diagnostic log capture (placement self-proof, 2026-11-12) ----
// The policy already emits PLACEMENT_* lines through the kernel log sink; the probe
// is the only consumer in these runs because the GUI host does not register the sink.
// Capturing them here means "the arm's env never reached the kernel" can never be
// misread as "the policy made no difference".
std::mutex g_logMutex;
std::vector<std::string> g_logLines;

void LogSink(void*, const char* line) noexcept {
    if (line == nullptr) return;
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logLines.size() < 4096) g_logLines.emplace_back(line);
}

std::vector<std::string> TakeLogLines() {
    std::lock_guard<std::mutex> lock(g_logMutex);
    return g_logLines;
}

double NowMs() {
    static const double frequency = [] {
        LARGE_INTEGER f{};
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart > 0 ? f.QuadPart : 1);
    }();
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    return static_cast<double>(counter.QuadPart) * 1000.0 / frequency;
}

const char* EnvironmentOrUnset(const char* name) {
    const char* value = std::getenv(name);
    return (value != nullptr && *value != '\0') ? value : "(unset)";
}

void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }
        Sleep(1);
    }
}

struct Options {
    std::wstring path;
    bool gpu = false;
    bool hdr = false;
    unsigned forceHdr = 0;
    unsigned sdrScRgb = 0;
    int seconds = 6;
    int runs = 5;
    bool windowed = false;
    int width = 1280;
    int height = 720;
    // Product host default (播放器会话.vb 原生配置): the adaptive decoder ladder is
    // what makes PlacementPolicy's three-tier path behave as it does in the product.
    bool adaptive = false;
};

// The 29 counters the SAMPLE line carries. Print and parse are lock-step:
// kFieldCount below is what sscanf must report, so a reordered or extended line
// fails loudly instead of shifting every later field by one.
constexpr int kFieldCount = 29;

struct Sample {
    bool ok = false;
    double frames = 0, decoded = 0, dropped = 0, coalesced = 0, presents = 0;
    double swsMs = 0, hwTransferMs = 0, deviceLockMs = 0, presentMs = 0;
    double cpuMs = 0, wallMs = 0;
    unsigned width = 0, height = 0, isHdrSource = 0, hdrFormat = 0, hdrPath = 0;
    unsigned outBits = 0, reqColorMode = 0, actColorMode = 0, actualDecodeMode = 0;
    unsigned dynamicActive = 0, windows = 0, degrade = 0;
    unsigned long long heldFrames = 0, serialDelta = 0;
    unsigned targetedNits = 0, effectivePeakNits = 0, sourcePeakNits = 0;
    unsigned long long videoBitRate = 0;
};

// A real HWND is not optional for the HDR question: VideoRenderer::Render returns
// at the `if (window_ == nullptr)` early exit, before EnsureSwapChain, the texture
// uploads, BuildDynamicToneSettings and the pixel shader. Headless therefore
// measures decode + frame metadata only, and swapOutputBits_ stays at 8.
//
// With a window the chain is created and Present(1,0) paces playback to vblank, so
// fps no longer separates arms; that is why cpuMs below is the sensitive readout -
// vsync waiting costs wall time, not CPU time.
HWND CreateProbeWindow(const int width, const int height) {
    static bool registered = false;
    if (!registered) {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"FFFPlayPerfProbe";
        if (!RegisterClassExW(&wc)) return nullptr;
        registered = true;
    }
    return CreateWindowExW(0, L"FFFPlayPerfProbe", L"FFF playback perf probe",
        WS_POPUP | WS_CLIPCHILDREN, 0, 0, width, height, nullptr, nullptr,
        GetModuleHandleW(nullptr), nullptr);
}

// ---- 2026-11-12 CPU-placement A/B diagnostics (docs/151) ---------------------
// All of these are additive print lines; SAMPLE keeps its 29-field format.

void PrintEnvLine(const Options& o) {
    std::printf("ENV placement=%s trace=%s startup=%s prefetch=%s adaptive=%d surface=%s\n",
        EnvironmentOrUnset("FFF_PLACEMENT"), EnvironmentOrUnset("FFF_PLACEMENT_TRACE"),
        EnvironmentOrUnset("FFF_STARTUP_PLACEMENT"), EnvironmentOrUnset("FFF3FP_PREFETCH"),
        o.adaptive ? 1 : 0, o.windowed ? "window" : "headless");
}

void PrintPlacementSnapshot(FFF3FPHandle player) {
    FFF3FPCpuPlacementSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = FFF3FP_CPUPLACEMENT_SNAPSHOT_VERSION;
    const FFFResult result = FFF3FP_GetCpuPlacementSnapshot(player, &snapshot);
    std::printf("PLACEMENT rc=%d domain=%u tier=%u flags=0x%X procclass=%d baseline=%d "
        "tier0=%u tier1=%u procdefault=%u preferred=%u groups=%u worker=%u workerprio=%d "
        "workersets=%u present=%u presentsets=%u decoders=%u\n",
        static_cast<int>(result), snapshot.domain, snapshot.tier,
        static_cast<unsigned>(snapshot.flags),
        static_cast<int>(snapshot.processPriorityClass),
        static_cast<int>(snapshot.baselinePriorityClass),
        snapshot.tier0SetCount, snapshot.tier1SetCount, snapshot.processDefaultSetCount,
        snapshot.preferredDomainThreads, snapshot.processorGroupCount,
        snapshot.workerThreadId, static_cast<int>(snapshot.workerThreadPriority),
        snapshot.workerSelectedSetCount, snapshot.presentThreadId,
        snapshot.presentSelectedSetCount, snapshot.decoderThreadCount);
}

double Percentile(const std::vector<double>& sorted, const double fraction) {
    if (sorted.empty()) return 0.0;
    const double index = fraction * static_cast<double>(sorted.size() - 1);
    const auto lower = static_cast<std::size_t>(index);
    const auto upper = (lower + 1 < sorted.size()) ? lower + 1 : sorted.size() - 1;
    const double weight = index - static_cast<double>(lower);
    return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
}

void PrintPresentCadence(const std::uint64_t counterDelta, std::vector<double> gapsMs) {
    std::sort(gapsMs.begin(), gapsMs.end());
    std::printf("PRESENT counter_delta=%llu gaps=%zu p50=%.2f p90=%.2f p99=%.2f max=%.2f\n",
        static_cast<unsigned long long>(counterDelta), gapsMs.size(),
        Percentile(gapsMs, 0.50), Percentile(gapsMs, 0.90), Percentile(gapsMs, 0.99),
        gapsMs.empty() ? 0.0 : gapsMs.back());
}

void PrintKernelLog() {
    for (const auto& line : TakeLogLines()) {
        if (line.find("PLACEMENT") != std::string::npos) {
            std::printf("KLOG %s\n", line.c_str());
        }
    }
}

Sample RunOnce(const Options& o) {
    Sample s;
    HWND window = nullptr;
    if (o.windowed) {
        window = CreateProbeWindow(o.width, o.height);
        if (window == nullptr) return s;
        ShowWindow(window, SW_SHOWNOACTIVATE);
        UpdateWindow(window);
    }
    PrintEnvLine(o);
    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;                        // nullptr = headless
    configuration.decodeMode = o.gpu ? FFF3FPDecodeMode::Gpu : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = o.hdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.forceHdrOutput = o.forceHdr;
    configuration.sdrScRgbMode = o.sdrScRgb;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;                   // 0 is a valid index, not "unset"
    if (o.adaptive) {
        // Same numbers the shipping host sends (播放器会话.vb 原生配置). Spelled out
        // rather than relying on struct defaults so the A/B config cannot drift silently.
        configuration.adaptiveDecoderThreads = 1;
        configuration.minSoftwareDecoderThreads = 4;
        configuration.maxSoftwareDecoderThreads = 32;
        configuration.decoderGrowDropPercent = 10;
        configuration.decoderShrinkDropPercent = 2;
    }

    // Install the kernel diagnostic sink before Create: PlacementPolicy decides at
    // construction whether FFF_PLACEMENT_TRACE is on, and the sink must be live before
    // the playback-start trace lines can be emitted.
    FFF3FP_SetLogCallback(&LogSink, nullptr);

    FFF3FPHandle player = nullptr;
    const auto cleanup = [&](const bool closePlayer) {
        if (player != nullptr) {
            if (closePlayer) FFF3FP_Close(player);
            FFF3FP_Destroy(player);
        }
        if (window != nullptr) DestroyWindow(window);
        return s;
    };

    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        player = nullptr;
        return cleanup(false);
    }

    auto readSnapshot = [&](FFF3FPSnapshot& snap) {
        snap.size = sizeof(snap);
        // PlayerSession::GetSnapshot gates on an exact version and returns
        // InvalidArgument for anything else, so a stale constant here fails every
        // arm with "SAMPLE failed" and no other symptom. Keep it in lock-step with
        // the shipping kernel: ... -> 22 (audio tap) -> 23 (audio passthrough
        // diagnostics tail, current). Stale 20 was the reason this probe printed
        // SAMPLE failed on the 2026-11-12 kernel.
        snap.version = 23;
        return FFF3FP_GetSnapshot(player, &snap) == FFFResult::Success;
    };

    std::string utf8Path;
    {
        const int size = WideCharToMultiByte(CP_UTF8, 0, o.path.c_str(), -1,
            nullptr, 0, nullptr, nullptr);
        if (size <= 1) return cleanup(false);
        utf8Path.resize(static_cast<std::size_t>(size) - 1);
        WideCharToMultiByte(CP_UTF8, 0, o.path.c_str(), -1, utf8Path.data(), size, nullptr, nullptr);
    }

    if (FFF3FP_Open(player, utf8Path.c_str()) != FFFResult::Success) return cleanup(true);
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (!g_openCompleted) return cleanup(true);

    FFF3FPSnapshot before{};
    FFF3FPSnapshot after{};
    if (!readSnapshot(before)) return cleanup(true);

    FILETIME create{}, exit{}, kernelStart{}, userStart{};
    GetProcessTimes(GetCurrentProcess(), &create, &exit, &kernelStart, &userStart);
    const LARGE_INTEGER wallStart{ [] { LARGE_INTEGER v; QueryPerformanceCounter(&v); return v; }() };

    FFF3FP_Play(player);
    // Playback window: pump messages (the window has to be composited) while polling
    // the live swap-chain flip counter, so the present cadence is a measured
    // distribution and not just a frame count. One placement snapshot is taken
    // mid-playback, after the policy has had time to escalate.
    const double playbackStart = NowMs();
    const double playbackMs = static_cast<double>(o.seconds) * 1000.0;
    const double placementReadAt =
        playbackStart + (playbackMs * 0.5 < 2000.0 ? playbackMs * 0.5 : 2000.0);
    bool placementRead = false;
    bool haveFlip = false;
    std::uint64_t flipsSeen = 0;
    std::uint64_t flipsStart = 0;
    (void)FFF3FP_GetPresentationCounter(player, &flipsStart);
    std::uint64_t lastFlips = flipsStart;
    double lastFlipMs = playbackStart;
    std::vector<double> flipGapsMs;
    flipGapsMs.reserve(4096);
    while (true) {
        const double now = NowMs();
        if (now - playbackStart >= playbackMs) break;
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::uint64_t flips = 0;
        if (FFF3FP_GetPresentationCounter(player, &flips) == FFFResult::Success) {
            if (!haveFlip) {
                haveFlip = true;
                lastFlips = flips;
                lastFlipMs = now;
            } else if (flips > lastFlips) {
                flipGapsMs.push_back(now - lastFlipMs);
                lastFlips = flips;
                lastFlipMs = now;
            }
            flipsSeen = flips;
        }
        if (!placementRead && now >= placementReadAt) {
            PrintPlacementSnapshot(player);
            placementRead = true;
        }
        Sleep(1);
    }
    PrintPresentCadence(flipsSeen > flipsStart ? flipsSeen - flipsStart : 0, flipGapsMs);

    FILETIME kernelEnd{}, userEnd{};
    GetProcessTimes(GetCurrentProcess(), &create, &exit, &kernelEnd, &userEnd);
    LARGE_INTEGER wallEnd{};
    QueryPerformanceCounter(&wallEnd);
    LARGE_INTEGER qpc{};
    QueryPerformanceFrequency(&qpc);
    const auto filetimeMs = [](const FILETIME& lo, const FILETIME& hi) {
        ULARGE_INTEGER a{ lo.dwLowDateTime, lo.dwHighDateTime };
        ULARGE_INTEGER b{ hi.dwLowDateTime, hi.dwHighDateTime };
        return static_cast<double>(b.QuadPart - a.QuadPart) / 10000.0;
    };
    s.cpuMs = filetimeMs(kernelStart, kernelEnd) + filetimeMs(userStart, userEnd);
    s.wallMs = static_cast<double>(wallEnd.QuadPart - wallStart.QuadPart) * 1000.0 /
        (qpc.QuadPart > 0 ? qpc.QuadPart : 1);

    if (!readSnapshot(after)) return cleanup(true);

    const auto toMs = [](const std::uint64_t hundredNs) {
        return static_cast<double>(hundredNs) / 10000.0;
    };
    s.frames = static_cast<double>(after.presentedVideoFrames - before.presentedVideoFrames);
    s.decoded = static_cast<double>(after.decodedVideoFrames - before.decodedVideoFrames);
    s.dropped = static_cast<double>(after.droppedVideoFrames - before.droppedVideoFrames);
    s.coalesced = static_cast<double>(after.coalescedVideoFrames - before.coalescedVideoFrames);
    s.presents = static_cast<double>(after.swapChainPresents - before.swapChainPresents);
    s.swsMs = toMs(after.softwareConvert100ns - before.softwareConvert100ns);
    s.hwTransferMs = toMs(after.hardwareTransfer100ns - before.hardwareTransfer100ns);
    s.deviceLockMs = toMs(after.deviceLockWait100ns - before.deviceLockWait100ns);
    s.presentMs = toMs(after.presentWait100ns - before.presentWait100ns);

    s.width = after.videoWidth;
    s.height = after.videoHeight;
    s.isHdrSource = after.isHdrSource;
    s.hdrFormat = static_cast<unsigned>(after.hdrFormat);
    s.hdrPath = static_cast<unsigned>(after.hdrProcessingPath);
    s.outBits = after.videoOutputBitDepth;
    s.reqColorMode = static_cast<unsigned>(after.requestedColorMode);
    s.actColorMode = static_cast<unsigned>(after.actualColorMode);
    s.actualDecodeMode = static_cast<unsigned>(after.decodeMode);
    s.dynamicActive = after.dynamicHdrMetadataActive;
    s.windows = after.dynamicMetadataWindows;
    s.degrade = after.dynamicMetadataDegrade;
    s.heldFrames = after.dynamicMetadataHeldFrames;
    s.targetedNits = after.dynamicMetadataTargetedNits;
    s.effectivePeakNits = after.effectiveTargetPeakNits;
    s.sourcePeakNits = after.sourcePeakNits;
    s.serialDelta = after.dynamicMetadataSerial - before.dynamicMetadataSerial;
    s.videoBitRate = after.videoBitRate;

    s.ok = s.decoded > 0;
    // Teardown before printing the kernel log: Release()/Close() lines belong to this
    // sample's account too. (The probe is one sample per process, so no cross-talk.)
    if (player != nullptr) {
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        player = nullptr;
    }
    if (window != nullptr) {
        DestroyWindow(window);
        window = nullptr;
    }
    PrintKernelLog();
    return s;
}

constexpr const char* kSampleFormat =
    "SAMPLE frames=%lf decoded=%lf dropped=%lf coalesced=%lf presents=%lf "
    "sws_ms=%lf hwtr_ms=%lf lock_ms=%lf present_ms=%lf cpu_ms=%lf wall_ms=%lf "
    "w=%u h=%u hdrsrc=%u fmt=%u "
    "path=%u outbits=%u reqcm=%u actcm=%u dcm=%u dyn=%u wins=%u degr=%u held=%llu "
    "serial=%llu targ=%u effpeak=%u srcpeak=%u br=%llu";

void PrintSample(const Sample& s) {
    std::printf("SAMPLE frames=%.0f decoded=%.0f dropped=%.0f coalesced=%.0f presents=%.0f "
        "sws_ms=%.3f hwtr_ms=%.3f lock_ms=%.3f present_ms=%.3f cpu_ms=%.3f wall_ms=%.3f "
        "w=%u h=%u hdrsrc=%u fmt=%u path=%u outbits=%u reqcm=%u actcm=%u dcm=%u "
        "dyn=%u wins=%u degr=%u held=%llu serial=%llu targ=%u effpeak=%u srcpeak=%u br=%llu\n",
        s.frames, s.decoded, s.dropped, s.coalesced, s.presents,
        s.swsMs, s.hwTransferMs, s.deviceLockMs, s.presentMs, s.cpuMs, s.wallMs,
        s.width, s.height, s.isHdrSource, s.hdrFormat, s.hdrPath, s.outBits,
        s.reqColorMode, s.actColorMode, s.actualDecodeMode, s.dynamicActive,
        s.windows, s.degrade,
        static_cast<unsigned long long>(s.heldFrames),
        static_cast<unsigned long long>(s.serialDelta),
        s.targetedNits, s.effectivePeakNits, s.sourcePeakNits,
        static_cast<unsigned long long>(s.videoBitRate));
}

bool ParseSample(const char* line, Sample& s) {
    return std::sscanf(line, kSampleFormat,
        &s.frames, &s.decoded, &s.dropped, &s.coalesced, &s.presents,
        &s.swsMs, &s.hwTransferMs, &s.deviceLockMs, &s.presentMs, &s.cpuMs, &s.wallMs,
        &s.width, &s.height, &s.isHdrSource, &s.hdrFormat, &s.hdrPath, &s.outBits,
        &s.reqColorMode, &s.actColorMode, &s.actualDecodeMode, &s.dynamicActive,
        &s.windows, &s.degrade, &s.heldFrames, &s.serialDelta,
        &s.targetedNits, &s.effectivePeakNits, &s.sourcePeakNits,
        &s.videoBitRate) == kFieldCount;
}

double Median(std::vector<double> values) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const auto mid = values.size() / 2;
    return values.size() % 2 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
}

std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) {
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    }
    return out;
}

Options Parse(int argc, wchar_t** argv) {
    Options o;
    o.path = argv[1];
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        const auto next = [&]() -> std::wstring { return i + 1 < argc ? argv[++i] : L""; };
        if (key == L"--decode") o.gpu = next() == L"gpu";
        else if (key == L"--color") o.hdr = next() == L"hdr";
        else if (key == L"--forcehdr") o.forceHdr = static_cast<unsigned>(_wtoi(next().c_str()));
        else if (key == L"--sdrscrgb") o.sdrScRgb = static_cast<unsigned>(_wtoi(next().c_str()));
        else if (key == L"--seconds") o.seconds = _wtoi(next().c_str());
        else if (key == L"--runs") o.runs = _wtoi(next().c_str());
        else if (key == L"--adaptive") o.adaptive = _wtoi(next().c_str()) != 0;
        else if (key == L"--window") {
            const std::wstring spec = next();
            const auto x = spec.find(L'x');
            if (x == std::wstring::npos) { std::printf("--window wants WxH\n"); std::exit(2); }
            o.width = _wtoi(spec.substr(0, x).c_str());
            o.height = _wtoi(spec.substr(x + 1).c_str());
            o.windowed = o.width > 0 && o.height > 0;
        }
    }
    if (o.seconds < 1) o.seconds = 6;
    if (o.runs < 1) o.runs = 1;
    return o;
}

std::wstring ChildCommand(const std::wstring& self, const Options& o) {
    std::wstring cmd = L"\"" + self + L"\" \"" + o.path + L"\" --one";
    cmd += o.gpu ? L" --decode gpu" : L" --decode cpu";
    cmd += o.hdr ? L" --color hdr" : L" --color sdr";
    cmd += L" --forcehdr " + std::to_wstring(o.forceHdr);
    cmd += L" --sdrscrgb " + std::to_wstring(o.sdrScRgb);
    cmd += L" --seconds " + std::to_wstring(o.seconds);
    if (o.adaptive) cmd += L" --adaptive 1";
    if (o.windowed) {
        cmd += L" --window " + std::to_wstring(o.width) + L"x" + std::to_wstring(o.height);
    }
    return cmd;
}

Sample SpawnChild(const std::wstring& self, const Options& o) {
    Sample s;
    const std::wstring cmd = ChildCommand(self, o);
    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.bInheritHandle = TRUE;
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    if (!CreatePipe(&readEnd, &writeEnd, &attributes, 0)) return s;
    SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdOutput = writeEnd;
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION process{};
    std::vector<wchar_t> mutableCommand(cmd.begin(), cmd.end());
    mutableCommand.push_back(L'\0');
    const auto started = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr,
        TRUE, 0, nullptr, nullptr, &startup, &process);
    CloseHandle(writeEnd);
    std::string output;
    if (started) {
        char buffer[512]{};
        DWORD read = 0;
        while (ReadFile(readEnd, buffer, sizeof(buffer) - 1, &read, nullptr) && read > 0) {
            buffer[read] = '\0';
            output += buffer;
        }
        WaitForSingleObject(process.hProcess, 180000);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    CloseHandle(readEnd);

    const auto at = output.find("SAMPLE ");
    if (at != std::string::npos && ParseSample(output.c_str() + at, s)) s.ok = s.decoded > 0;
    return s;
}

int RunParent(const Options& o, const std::wstring& self) {
    std::vector<Sample> samples;
    for (int i = 0; i < o.runs; ++i) {
        auto s = SpawnChild(self, o);
        if (!s.ok) { std::printf("  sample %d: FAILED (no decodable sample line)\n", i + 1); continue; }
        std::printf("  sample %d: fps=%.2f decoded=%.0f cpu_per_frame_ms=%.4f "
            "sws_per_frame_ms=%.4f outbits=%u path=%u windows=%u\n",
            i + 1, s.frames / o.seconds, s.decoded,
            s.decoded > 0 ? s.cpuMs / s.decoded : 0.0,
            s.decoded > 0 ? s.swsMs / s.decoded : 0.0, s.outBits, s.hdrPath, s.windows);
        samples.push_back(s);
    }
    if (samples.empty()) { std::printf("ALL SAMPLES FAILED\n"); return 1; }

    std::vector<double> fps, decoded, sws, hwtr, lock, dropped, holds, serials, cpu, wall, present;
    for (const auto& s : samples) {
        fps.push_back(s.frames / o.seconds);
        decoded.push_back(s.decoded);
        sws.push_back(s.swsMs);
        hwtr.push_back(s.hwTransferMs);
        lock.push_back(s.deviceLockMs);
        present.push_back(s.presentMs);
        dropped.push_back(s.dropped);
        cpu.push_back(s.cpuMs);
        wall.push_back(s.wallMs);
        holds.push_back(static_cast<double>(s.heldFrames));
        serials.push_back(static_cast<double>(s.serialDelta));
    }
    const double medianDecoded = Median(decoded);
    const auto perFrame = [&](const std::vector<double>& ms) {
        return medianDecoded > 0 ? Median(ms) / medianDecoded : 0.0;
    };

    const Sample& first = samples.front();
    unsigned splits = 0;
    for (const auto& s : samples) {
        if (s.outBits != first.outBits || s.actColorMode != first.actColorMode ||
            s.actualDecodeMode != first.actualDecodeMode || s.hdrPath != first.hdrPath ||
            s.hdrFormat != first.hdrFormat || s.windows != first.windows) {
            ++splits;
        }
    }

    const std::string surface = o.windowed
        ? (std::to_string(o.width) + "x" + std::to_string(o.height) + " window")
        : std::string("headless");
    std::printf("RESULT api=%u media=%s decode=%s color=%s forcehdr=%u sdrscrgb=%u "
        "surface=%s seconds=%d samples=%zu\n",
        FFF3FP_GetApiVersion(), Utf8(o.path.c_str()).c_str(),
        o.gpu ? "gpu" : "cpu", o.hdr ? "hdr" : "sdr", o.forceHdr, o.sdrScRgb,
        surface.c_str(), o.seconds, samples.size());
    std::printf("FPS_median=%.2f FPS_min=%.2f FPS_max=%.2f decoded_median=%.0f\n",
        Median(fps), *std::min_element(fps.begin(), fps.end()),
        *std::max_element(fps.begin(), fps.end()), medianDecoded);
    std::printf("cpu_ms_per_frame=%.4f wall_ms_per_frame=%.4f sws_per_frame_ms=%.4f "
        "hwtransfer_per_frame_ms=%.4f devicelock_per_frame_ms=%.4f present_per_frame_ms=%.4f\n",
        medianDecoded > 0 ? Median(cpu) / medianDecoded : 0.0,
        medianDecoded > 0 ? Median(wall) / medianDecoded : 0.0,
        perFrame(sws), perFrame(hwtr), perFrame(lock), perFrame(present));
    std::printf("dropped_median=%.0f fresh_metadata_per_frame=%.4f held_frames_median=%.0f\n",
        Median(dropped), medianDecoded > 0 ? Median(serials) / medianDecoded : 0.0,
        Median(holds));
    std::printf("CONFIG res=%ux%u hdrsrc=%u hdrformat=%u path=%u outbits=%u reqcm=%u actcm=%u "
        "dcm=%u dyn=%u windows=%u degrade=%u targ=%u effpeak=%u bitrate=%llu\n",
        first.width, first.height, first.isHdrSource, first.hdrFormat, first.hdrPath,
        first.outBits, first.reqColorMode, first.actColorMode, first.actualDecodeMode,
        first.dynamicActive, first.windows, first.degrade, first.targetedNits,
        first.effectivePeakNits, first.videoBitRate);

    if (splits > 0) {
        std::printf("GUARD: %zu sample(s) disagreed on config state - the arm is not stable\n",
            static_cast<std::size_t>(splits));
        return 3;
    }
    if ((o.hdr || o.forceHdr != 0) && !o.windowed) {
        std::printf("GUARD: HDR arm requested headless. Render() returns at the "
            "window_==nullptr early exit, before EnsureSwapChain / uploads / "
            "BuildDynamicToneSettings / the pixel shader - add --window WxH\n");
        return 6;
    }
    if ((o.hdr || o.forceHdr != 0) && first.outBits != 16) {
        std::printf("GUARD: HDR arm requested but output bit depth is %u, not 16 - "
            "this arm is NOT the 16-bit path\n", first.outBits);
        return 4;
    }
    if (o.gpu && first.actualDecodeMode != 2) {
        std::printf("GUARD: gpu decode requested but kernel reports decodeMode=%u "
            "(silent software fallback)\n", first.actualDecodeMode);
        return 5;
    }
    return 0;
}

}  // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        std::wprintf(L"用法: play_back_perf_bench <媒体> [--decode cpu|gpu] [--color sdr|hdr]"
                    L" [--forcehdr 0|1] [--sdrscrgb 0|1] [--seconds N] [--runs R]"
                    L" [--adaptive 0|1] [--one]\n");
        return 2;
    }
    const Options o = Parse(argc, argv);
    bool oneShot = false;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--one") oneShot = true;
    }
    if (oneShot || o.runs == 1) {
        const auto s = RunOnce(o);
        if (!s.ok) { std::printf("SAMPLE failed\n"); return 1; }
        PrintSample(s);
        return 0;
    }
    return RunParent(o, std::wstring(argv[0]));
}
