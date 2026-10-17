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
//                        [--seconds N] [--runs R] [--one]
// Without --window the session is headless: only decode and per-frame HDR
// metadata processing are on the clock. The HDR presentation path requires a
// window (see CreateProbeWindow).
//
// The config block on each sample is part of the instrument, not decoration: if
// the colour mode, decode mode or output bit depth is not what the arm claims to
// measure, the numbers are meaningless and the summary says so with a non-zero exit.

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

bool g_openCompleted = false;

void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
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

Sample RunOnce(const Options& o) {
    Sample s;
    HWND window = nullptr;
    if (o.windowed) {
        window = CreateProbeWindow(o.width, o.height);
        if (window == nullptr) return s;
        ShowWindow(window, SW_SHOWNOACTIVATE);
        UpdateWindow(window);
    }
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
        // the shipping kernel: 8 -> 9 (ST 2094 diagnostics tail) -> 11 (current).
        snap.version = 20;
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
    Pump(o.seconds * 1000);

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
    return cleanup(true);
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
                    L" [--forcehdr 0|1] [--sdrscrgb 0|1] [--seconds N] [--runs R] [--one]\n");
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
