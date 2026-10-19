// TEMPORARY verification harness for the external bed render plug seam.
// Lives under obj/ (gitignored) and is NOT part of FFF.Native.vcxproj: it exists to
// answer one question with numbers -- does the kernel's seam actually run, and does
// the fold-down still run when it must.
//
// It loads a kernel DLL by path, so the same binary drives the before/after arms.
// Everything it needs from the kernel is resolved by name through GetProcAddress;
// an older DLL simply reports the entry point as missing instead of failing to load.
//
//   plug_seam_probe <dll> <media> [--seconds N] [--mode none|decline|accept|bail|badsize]
//
// The fake plug here is deliberately not a binaural renderer: it writes two
// differently scaled copies of bed channel 0, which makes "the plug's samples
// reached the endpoint" visible in the kernel's own per-channel peak levels.

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>

#include <algorithm>
#include <atomic>
#include <clocale>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"
#include "3FP/Api/FFF.Player.AudioRenderPlug.h"

namespace {
using CreateFn = FFFResult(*)(const FFF3FPConfiguration*, FFF3FPHandle*) noexcept;
using OpenFn = FFFResult(*)(FFF3FPHandle, const char*) noexcept;
using PlayFn = FFFResult(*)(FFF3FPHandle) noexcept;
using CloseFn = FFFResult(*)(FFF3FPHandle) noexcept;
using DestroyFn = void(*)(FFF3FPHandle) noexcept;
using SnapshotFn = FFFResult(*)(FFF3FPHandle, FFF3FPSnapshot*) noexcept;
using PeaksFn = FFFResult(*)(FFF3FPHandle, FFF3FPAudioPeakLevels*) noexcept;
using SetPlugFn = FFFResult(*)(FFF3FPHandle, const FFF3FPAudioRenderPlug*) noexcept;
using GetPlugStateFn = FFFResult(*)(FFF3FPHandle, std::uint32_t*, char*, std::uint32_t,
    std::uint64_t*, std::uint64_t*) noexcept;
using ApiVersionFn = std::uint32_t(*)() noexcept;
using LastErrorFn = FFFResult(*)(FFF3FPHandle, char*, std::uint32_t, std::uint32_t*) noexcept;

bool g_open = false;
void EventCallback(void*, const FFF3FPEvent event, const char*) {
    if (event == FFF3FPEvent::OpenCompleted) g_open = true;
}

void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
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

// The fake plug's whole state. Every counter here is an independent piece of
// evidence about which entry points the kernel actually reached -- the kernel's own
// counters alone cannot tell "the seam never fired" from "the plug said no".
struct FakePlug final {
    bool accepts = false;
    bool bails = false;
    std::uint32_t supportsCalls = 0;
    std::uint32_t renderCalls = 0;
    std::uint32_t resetCalls = 0;
    std::uint32_t destroyCalls = 0;
    std::uint32_t badDescriptions = 0;
    std::uint32_t nonFiniteSamples = 0;
    std::uint32_t bedChannelCount = 0;
    std::uint32_t bedSampleRate = 0;
    std::uint32_t bedLfeIndex = 0xFFFFFFFFu;
    std::int64_t lastPosition100ns = -1;
    std::uint64_t framesWritten = 0;
    std::int32_t positions[FFF3FP_AUDIO_PLUG_MAX_CHANNELS] = { 0 };
};

int32_t FakeSupports(void* context, const FFF3FPAudioBedDescription* bed) noexcept {
    auto* plug = static_cast<FakePlug*>(context);
    ++plug->supportsCalls;
    if (bed == nullptr) { ++plug->badDescriptions; return 0; }
    if (bed->size != sizeof(FFF3FPAudioBedDescription) ||
        bed->version != FFF3FP_AUDIO_PLUG_VERSION) { ++plug->badDescriptions; return 0; }
    plug->bedChannelCount = bed->channelCount;
    plug->bedSampleRate = bed->sampleRate;
    plug->bedLfeIndex = bed->lfeIndex;
    for (std::uint32_t index = 0; index < FFF3FP_AUDIO_PLUG_MAX_CHANNELS; ++index)
        plug->positions[index] = bed->positions[index];
    return plug->accepts ? 1 : 0;
}

int32_t FakeRender(void* context, const FFF3FPAudioBedFrame* frame, float* outLeft,
    float* outRight, const std::uint32_t outCapacity) noexcept {
    auto* plug = static_cast<FakePlug*>(context);
    ++plug->renderCalls;
    if (plug->bails) return 0;
    if (frame == nullptr || outLeft == nullptr || outRight == nullptr ||
        frame->channels == nullptr || frame->channelCount == 0 ||
        frame->size != sizeof(FFF3FPAudioBedFrame)) return -1;
    const auto count = std::min<std::uint32_t>(frame->frameCount, outCapacity);
    plug->lastPosition100ns = frame->position100ns;
    const auto* source = frame->channels[0];
    for (std::uint32_t index = 0; index < count; ++index) {
        const auto sample = source[index];
        // Two deliberately different gains: the kernel's own per-channel peak
        // levels then show whether these samples or the fold-down's reached the
        // endpoint (0.50 vs 0.05 is not a ratio a matrix downmix produces).
        outLeft[index] = sample * 0.50f;
        outRight[index] = sample * 0.05f;
        if (!std::isfinite(outLeft[index]) || !std::isfinite(outRight[index]))
            ++plug->nonFiniteSamples;
    }
    plug->framesWritten += count;
    return static_cast<int32_t>(count);
}

void FakeReset(void* context) noexcept { ++static_cast<FakePlug*>(context)->resetCalls; }
void FakeDestroy(void* context) noexcept { ++static_cast<FakePlug*>(context)->destroyCalls; }

template <typename T>
T Resolve(const HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

// ---- loopback capture of the render endpoint --------------------------------
// The only way to compare what the two kernel builds actually put on the device,
// rather than what the kernel was willing to report about it. Passive
// AUDCLNT_STREAMFLAGS_LOOPBACK on the default endpoint: it writes the raw packets
// to a file and the driver aligns them afterwards.
struct LoopbackCapture final {
    HANDLE file = INVALID_HANDLE_VALUE;
    std::atomic<bool> stop_{false};
    std::atomic<bool> ready_{false};
    std::atomic<unsigned long long> bytes{0};
    std::thread thread;
    unsigned int rate = 0;
    unsigned short channels = 0;
    unsigned short bits = 0;
    bool floating = false;
    std::string error;

    void Run(const std::wstring& path) {
        file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) { error = "capture file"; ready_ = true; return; }
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        IMMDeviceEnumerator* enumerator = nullptr;
        IMMDevice* device = nullptr;
        IAudioClient* client = nullptr;
        IAudioCaptureClient* capture = nullptr;
        WAVEFORMATEX* format = nullptr;
        auto fail = [this](const char* stage) { error = stage; ready_.store(true); };
        if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            IID_PPV_ARGS(&enumerator))) ||
            FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device)) ||
            FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client))) ||
            FAILED(client->GetMixFormat(&format))) { fail("endpoint"); goto done; }
        rate = format->nSamplesPerSec;
        channels = format->nChannels;
        bits = format->wBitsPerSample;
        floating = format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
            (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22 &&
                IsEqualGUID(reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format)->SubFormat,
                    KSDATAFORMAT_SUBTYPE_IEEE_FLOAT));
        if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK, 10'000'000, 0, format, nullptr)) ||
            FAILED(client->GetService(IID_PPV_ARGS(&capture)))) { fail("initialize"); goto done; }
        ready_.store(true);
        for (;;) {
            if (stop_.load()) break;
            UINT32 available = 0;
            if (FAILED(capture->GetNextPacketSize(&available))) { error = "packet size"; break; }
            while (available != 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                if (FAILED(capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) {
                    error = "get buffer"; goto done;
                }
                const auto size = static_cast<DWORD>(frames) * format->nBlockAlign;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && size > 0) {
                    DWORD written = 0;
                    if (!WriteFile(file, data, size, &written, nullptr) || written != size) {
                        capture->ReleaseBuffer(frames); error = "write"; goto done;
                    }
                    bytes.fetch_add(written);
                }
                capture->ReleaseBuffer(frames);
                if (FAILED(capture->GetNextPacketSize(&available))) { error = "packet size"; goto done; }
            }
            Sleep(5);
        }
    done:
        if (format != nullptr) CoTaskMemFree(format);
        if (capture != nullptr) capture->Release();
        if (client != nullptr) client->Release();
        if (device != nullptr) device->Release();
        if (enumerator != nullptr) enumerator->Release();
        if (file != INVALID_HANDLE_VALUE) { CloseHandle(file); file = INVALID_HANDLE_VALUE; }
        ready_.store(true);
        CoUninitialize();
    }
};
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 3) {
        wprintf(L"用法: plug_seam_probe <dll> <媒体> [--seconds N] [--mode ...]\n");
        return 2;
    }
    std::string dllPath = Utf8(argv[1]);
    std::string media = Utf8(argv[2]);
    int seconds = 3;
    std::string mode = "none";
    double installAt = -1.0;
    double clearAt = -1.0;
    double badAt = -1.0;
    std::wstring capturePath;
    for (int i = 3; i < argc; ++i) {
        const std::wstring a = argv[i];
        if (a == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (a == L"--mode" && i + 1 < argc) mode = Utf8(argv[++i]);
        else if (a == L"--install-at" && i + 1 < argc) installAt = _wtof(argv[++i]);
        else if (a == L"--clear-at" && i + 1 < argc) clearAt = _wtof(argv[++i]);
        else if (a == L"--bad-at" && i + 1 < argc) badAt = _wtof(argv[++i]);
        else if (a == L"--capture" && i + 1 < argc) capturePath = argv[++i];
    }
    FakePlug plug{};
    plug.accepts = (mode == "accept" || mode == "bail");
    plug.bails = (mode == "bail");

    HMODULE loaded = nullptr;
    const auto search = Utf8(argv[1]);
    // LOAD_WITH_ALTERED_SEARCH_PATH so the FFmpeg dlls that sit next to the kernel
    // are the ones it binds to, whichever copy of the kernel the caller named.
    loaded = LoadLibraryExW(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (loaded == nullptr) {
        printf("PROBE 加载内核失败 hr=%lu dll=%s\n", GetLastError(), search.c_str());
        return 1;
    }
    const auto create = Resolve<CreateFn>(loaded, "FFF3FP_Create");
    const auto open = Resolve<OpenFn>(loaded, "FFF3FP_Open");
    const auto play = Resolve<PlayFn>(loaded, "FFF3FP_Play");
    const auto close = Resolve<CloseFn>(loaded, "FFF3FP_Close");
    const auto destroy = Resolve<DestroyFn>(loaded, "FFF3FP_Destroy");
    const auto snapshot = Resolve<SnapshotFn>(loaded, "FFF3FP_GetSnapshot");
    const auto peaks = Resolve<PeaksFn>(loaded, "FFF3FP_GetAudioPeakLevels");
    const auto setPlug = Resolve<SetPlugFn>(loaded, "FFF3FP_SetAudioRenderPlug");
    const auto plugState = Resolve<GetPlugStateFn>(loaded, "FFF3FP_GetAudioRenderPlugState");
    const auto apiVersion = Resolve<ApiVersionFn>(loaded, "FFF3FP_GetApiVersion");
    const auto lastErrorFn = Resolve<LastErrorFn>(loaded, "FFF3FP_GetLastError");
    printf("PROBE dll=%s media=%s mode=%s exports: setPlug=%d getPlugState=%d\n",
        search.c_str(), media.c_str(), mode.c_str(), setPlug != nullptr,
        plugState != nullptr);
    if (apiVersion != nullptr) printf("PROBE PlayerApiVersion=%u\n", apiVersion());
    if (create == nullptr || open == nullptr || play == nullptr || snapshot == nullptr) {
        printf("PROBE 缺少基础导出\n"); return 1;
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFPlugSeamProbeWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFPlugSeamProbeWnd", L"plugseam",
        WS_OVERLAPPEDWINDOW, 20, 20, 320, 200, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = apiVersion != nullptr ? apiVersion() : 20u;
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;
    FFF3FPHandle player = nullptr;
    if (create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("PROBE Create 失败\n"); return 1;
    }
    if (open(player, media.c_str()) != FFFResult::Success) {
        printf("PROBE Open 失败\n"); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = configuration.version;
    snapshot(player, &s);
    printf("OPEN 音频流=%d 时长=%.2fs\n", s.selectedAudioStream,
        static_cast<double>(s.duration100ns) / 1e7);

    if (mode == "badsize") {
        FFF3FPAudioRenderPlug bad{};
        bad.size = sizeof(FFF3FPAudioRenderPlug) + 4;   // not this ABI's layout
        bad.version = FFF3FP_AUDIO_PLUG_VERSION;
        bad.context = &plug;
        bad.name = "bad-size-plug";
        bad.supports = FakeSupports;
        bad.render = FakeRender;
        bad.reset = FakeReset;
        bad.destroy = FakeDestroy;
        const auto rejected = setPlug != nullptr ? setPlug(player, &bad)
            : FFFResult::NotSupported;
        printf("BADSIZE 拒绝结果=%d active=%u\n", static_cast<int>(rejected),
            [&] { std::uint32_t a = 999; char n[64]{}; std::uint64_t r = 0, d = 0;
                  if (plugState) plugState(player, &a, n, sizeof(n), &r, &d); return a; }());
        if (setPlug) setPlug(player, nullptr);   // leave no plug installed
        close(player); destroy(player);
        printf("BADSIZE supportsCalls=%u destroyCalls=%u\n", plug.supportsCalls,
            plug.destroyCalls);
        return 0;
    }

    auto installTable = [&](const char* label, const bool accept, const bool bail) -> int {
        if (setPlug == nullptr) { printf("PROBE 该 DLL 没有 SetAudioRenderPlug\n"); return -1; }
        plug.accepts = accept;
        plug.bails = bail;
        FFF3FPAudioRenderPlug table{};
        table.size = sizeof(FFF3FPAudioRenderPlug);
        table.version = FFF3FP_AUDIO_PLUG_VERSION;
        table.context = &plug;
        table.name = label;
        table.supports = FakeSupports;
        table.render = FakeRender;
        table.reset = FakeReset;
        table.destroy = FakeDestroy;
        const auto installed = setPlug(player, &table);
        std::uint32_t state = 999;
        char readName[64]{};
        std::uint64_t rendered = 0, declined = 0;
        if (plugState)
            plugState(player, &state, readName, sizeof(readName), &rendered, &declined);
        printf("INSTALL label=%s rc=%d active=%u name=%s rendered=%llu declined=%llu\n",
            label, static_cast<int>(installed), state, readName,
            static_cast<unsigned long long>(rendered),
            static_cast<unsigned long long>(declined));
        return static_cast<int>(installed);
    };

    if (mode != "none" && installAt < 0) {
        installTable(mode.c_str(), mode == "accept" || mode == "bail" || mode == "badafter",
            mode == "bail");
    }

    LoopbackCapture capture;
    if (!capturePath.empty()) {
        capture.thread = std::thread([&capture, capturePath] { capture.Run(capturePath); });
        for (int i = 0; i < 100 && !capture.ready_.load(); ++i) Sleep(20);
        printf("CAPTURE fmt rate=%u ch=%u bits=%u float=%d error=%s\n", capture.rate,
            capture.channels, capture.bits, capture.floating ? 1 : 0,
            capture.error.c_str());
    }
    play(player);
    std::uint32_t active = 0;
    char name[64]{};
    std::uint64_t rendered = 0, declined = 0;
    // Aggregates over the polls, for the before/after equivalence arms: which buffer
    // the kernel had just submitted when the host asked is a timing artifact, its
    // maximum over a whole run is not. A plug that declines per frame makes the
    // per-poll deltas non-deterministic, so these are only ever compared between two
    // runs of the same media with no plug installed.
    float maxPeak0 = 0.0f, maxPeak1 = 0.0f;
    int polls = 0;
    for (int tick = 0; tick < seconds * 10; ++tick) {
        Pump(100);
        const auto now = tick / 10.0;
        if (installAt >= 0.0 && now >= installAt) {
            installAt = -1.0;
            installTable(mode.c_str(), mode == "accept" || mode == "bail", mode == "bail");
        }
        if (badAt >= 0.0 && now >= badAt) {
            badAt = -1.0;
            FFF3FPAudioRenderPlug bad{};
            bad.size = sizeof(FFF3FPAudioRenderPlug) + 4;
            bad.version = FFF3FP_AUDIO_PLUG_VERSION;
            bad.context = &plug;
            bad.name = "bad-size-plug";
            bad.supports = FakeSupports;
            bad.render = FakeRender;
            bad.reset = FakeReset;
            bad.destroy = FakeDestroy;
            const auto rejected = setPlug ? setPlug(player, &bad) : FFFResult::NotSupported;
            std::uint32_t state = 999;
            char readName[64]{};
            std::uint64_t stillRendered = 0, stillDeclined = 0;
            if (plugState) plugState(player, &state, readName, sizeof(readName),
                &stillRendered, &stillDeclined);
            printf("BADSIZE t=%.1f rc=%d 原 plug 仍在: active=%u name=%s rendered=%llu "
                "declined=%llu\n", now, static_cast<int>(rejected), state, readName,
                static_cast<unsigned long long>(stillRendered),
                static_cast<unsigned long long>(stillDeclined));
        }
        if (clearAt >= 0.0 && now >= clearAt) {
            clearAt = -1.0;
            const auto cleared = setPlug ? setPlug(player, nullptr) : FFFResult::NotSupported;
            std::uint32_t state = 999;
            char readName[64]{};
            std::uint64_t clearedRendered = 0, clearedDeclined = 0;
            if (plugState) plugState(player, &state, readName, sizeof(readName),
                &clearedRendered, &clearedDeclined);
            printf("CLEAR t=%.1f rc=%d active=%u name='%s' rendered=%llu declined=%llu "
                "destroyCalls=%u\n", now, static_cast<int>(cleared), state, readName,
                static_cast<unsigned long long>(clearedRendered),
                static_cast<unsigned long long>(clearedDeclined), plug.destroyCalls);
        }
        if (tick % 5 != 0) continue;   // one line every 500 ms
        snapshot(player, &s);
        FFF3FPAudioPeakLevels level{};
        level.size = sizeof(level);
        level.version = configuration.version;
        if (peaks) peaks(player, &level);
        active = 0; name[0] = '\0'; rendered = 0; declined = 0;
        if (plugState) plugState(player, &active, name, sizeof(name), &rendered, &declined);
        ++polls;
        if (level.values[0] > maxPeak0) maxPeak0 = level.values[0];
        if (level.values[1] > maxPeak1) maxPeak1 = level.values[1];
        printf("TICK t=%.1f decoded=%llu bin=%u act=%u rend=%llu decl=%llu "
            "peak0=%.4f peak1=%.4f peakCh=%u underrun=%llu disc=%llu\n",
            tick / 10.0, static_cast<unsigned long long>(s.decodedAudioFrames),
            s.audioBinaural, active,
            static_cast<unsigned long long>(rendered),
            static_cast<unsigned long long>(declined),
            level.values[0], level.values[1], level.channelCount,
            static_cast<unsigned long long>(s.audioUnderruns),
            static_cast<unsigned long long>(s.audioDiscontinuities));
    }
    snapshot(player, &s);
    if (!capturePath.empty()) {
        capture.stop_.store(true);
        if (capture.thread.joinable()) capture.thread.join();
        printf("CAPTURE bytes=%llu error=%s\n",
            static_cast<unsigned long long>(capture.bytes.load()), capture.error.c_str());
    }
    char lastError[512]{};
    if (lastErrorFn) lastErrorFn(player, lastError, sizeof(lastError), nullptr);
    printf("PEAKSTAT polls=%d max0=%.6f max1=%.6f\n", polls, maxPeak0, maxPeak1);
    printf("FINAL state=%d decoded=%llu underrun=%llu jitter=%llu disc=%llu silence=%llu "
        "overlap=%llu rejected=%llu bin=%u act=%u rend=%llu decl=%llu lastError='%s'\n",
        static_cast<int>(s.state),
        static_cast<unsigned long long>(s.decodedAudioFrames),
        static_cast<unsigned long long>(s.audioUnderruns),
        static_cast<unsigned long long>(s.audioTimestampJitterFrames),
        static_cast<unsigned long long>(s.audioDiscontinuities),
        static_cast<unsigned long long>(s.audioInsertedSilenceFrames),
        static_cast<unsigned long long>(s.audioDroppedOverlapFrames),
        static_cast<unsigned long long>(s.audioRejectedFrames),
        s.audioBinaural, active,
        static_cast<unsigned long long>(rendered),
        static_cast<unsigned long long>(declined), lastError);
    printf("PLUG supportsCalls=%u renderCalls=%u resetCalls=%u bedChannels=%u "
        "bedRate=%u bedLfe=%u pos100ns=%lld framesWritten=%llu badDescriptions=%u\n",
        plug.supportsCalls, plug.renderCalls, plug.resetCalls, plug.bedChannelCount,
        plug.bedSampleRate, plug.bedLfeIndex,
        static_cast<long long>(plug.lastPosition100ns),
        static_cast<unsigned long long>(plug.framesWritten), plug.badDescriptions);
    printf("PLUG position0..3=%d,%d,%d,%d\n", plug.positions[0], plug.positions[1],
        plug.positions[2], plug.positions[3]);
    close(player);
    if (setPlug) setPlug(player, nullptr);   // clear before teardown: exercises release
    destroy(player);
    printf("PLUG destroyCalls=%u (context released by the kernel)\n", plug.destroyCalls);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
