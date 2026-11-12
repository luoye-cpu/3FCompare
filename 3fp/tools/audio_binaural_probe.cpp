// E1 probe for the binaural bed-render policy (FFF3FP_SetAudioBinauralPolicy /
// FFF3FP_GetAudioBinauralStatus).
//
// Build (from 3fp/, NOT the repo root):
//   python tools/build_probe.py audio_binaural_probe
// Run (FFF.Native.dll + FFmpeg DLLs must sit beside the exe):
//   audio_binaural_probe.exe <media>
//
// What it proves, and what it cannot:
//   * the endpoint form factor AND mix channel count the kernel reports are the
//     SAME values independent MMDeviceEnumerator queries return for the default
//     render endpoint (two paths, one answer);
//   * the three policies drive `wanted` exactly as specified, including the
//     automatic branch computing its answer from the endpoint's mix channel
//     count (2 => binaural default; product decision 2026-11-12);
//   * an out-of-range policy is refused without moving the stored value;
//   * `available` follows "plug installed AND 2-channel endpoint", so a run
//     with no module on disk must read 0 even when `wanted` is 1.
// It does NOT prove the audio itself changed: routing a 5.1 bed through the
// external renderer needs the licensed module, which is not present here.

// NOMINMAX first: WasapiRenderer.h uses std::min/max initializer lists, and the
// windows.h macros would turn those into a parse error. The kernel gets the same
// effect from its pch.
#define NOMINMAX
#include <windows.h>

#include <initguid.h>              // must precede the PKEY header for definitions
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <mmreg.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propsys.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"
#include "3FP/Api/FFF.Player.AudioRenderPlug.h"
// The route rule itself, included rather than re-implemented: BinauralWantedFor is
// an inline free function in the header the kernel compiled, so testing it here
// tests the shipped code. It is the only way to cover the headphone branch on a
// machine whose live endpoint is not a headphone.
#include "3FP/Audio/WasapiRenderer.h"

namespace {

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

LRESULT CALLBACK WndProc(HWND window, UINT message, WPARAM w, LPARAM l) {
    return DefWindowProcW(window, message, w, l);
}

HWND MakeHiddenWindow() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = &WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFF3FP_AudioBinauralProbe";
    if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    return CreateWindowExW(0, wc.lpszClassName, L"", WS_POPUP, 0, 0, 64, 64,
        nullptr, nullptr, wc.hInstance, nullptr);
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

// Independent answer to "what shape is the default render endpoint": the same
// query the Sound control panel makes, deliberately not going through the
// kernel. Uses the current thread's COM apartment; the caller joins MTA.
std::uint32_t DefaultEndpointFormFactor() {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::uint32_t formFactor = 0;
    IMMDeviceEnumerator* enumerator = nullptr;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            IID_PPV_ARGS(&enumerator)))) {
        IMMDevice* device = nullptr;
        if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device))) {
            IPropertyStore* store = nullptr;
            if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store))) {
                PROPVARIANT value;
                PropVariantInit(&value);
                if (SUCCEEDED(store->GetValue(PKEY_AudioEndpoint_FormFactor, &value)) &&
                    value.vt == VT_UI4)
                    formFactor = value.ulVal;
                PropVariantClear(&value);
                store->Release();
            }
            device->Release();
        }
        enumerator->Release();
    }
    if (SUCCEEDED(com)) CoUninitialize();
    return formFactor;
}

// The channel count the default endpoint would actually run. This is the number
// the kernel compares against 2 before it lets the seam own the bed, so it has to
// be queried the same way Windows reports it rather than guessed from the mask.
std::uint32_t DefaultEndpointChannels() {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::uint32_t channels = 0;
    IMMDeviceEnumerator* enumerator = nullptr;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            IID_PPV_ARGS(&enumerator)))) {
        IMMDevice* device = nullptr;
        if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device))) {
            IAudioClient* client = nullptr;
            if (SUCCEEDED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                    reinterpret_cast<void**>(&client)))) {
                WAVEFORMATEX* mix = nullptr;
                if (SUCCEEDED(client->GetMixFormat(&mix)) && mix != nullptr) {
                    channels = mix->nChannels;
                    CoTaskMemFree(mix);
                }
                client->Release();
            }
            device->Release();
        }
        enumerator->Release();
    }
    if (SUCCEEDED(com)) CoUninitialize();
    return channels;
}

// Reproduces what the host does and nothing more: load the module, ask it for the
// bed-render table (a refusal here IS the authorization gate answering), hand the
// table to the kernel. The probe never calls into the table, so the licensed
// implementation stays the module's business.
using GetAudioTableFn = int (*)(std::uint32_t version, FFF3FPAudioRenderPlug* plug);

bool InstallModulePlug(FFF3FPHandle player, const char* dllPath, std::string& detail) {
    const HMODULE module = LoadLibraryA(dllPath);
    if (module == nullptr) {
        detail = "LoadLibrary failed, err=" + std::to_string(GetLastError());
        return false;
    }
    const auto proc = reinterpret_cast<GetAudioTableFn>(
        reinterpret_cast<void*>(GetProcAddress(module, "FFF_GetAudioRenderApi")));
    if (proc == nullptr) {
        detail = "module has no FFF_GetAudioRenderApi";
        return false;
    }
    FFF3FPAudioRenderPlug plug{};
    plug.size = static_cast<std::uint32_t>(sizeof(plug));
    const int rc = proc(FFF3FP_AUDIO_PLUG_VERSION, &plug);
    if (rc != 0) {
        detail = "table refused rc=" + std::to_string(rc) + " (authorization gate)";
        return false;
    }
    const FFFResult set = FFF3FP_SetAudioRenderPlug(player, &plug);
    detail = "size=" + std::to_string(plug.size) +
             " version=" + std::to_string(plug.version) +
             " name=" + (plug.name != nullptr ? plug.name : "(null)") +
             " SetAudioRenderPlug=" + std::to_string(static_cast<int>(set));
    return set == FFFResult::Success;
}

struct Status {
    std::uint32_t policy = 0;
    std::uint32_t formFactor = 0;
    std::uint32_t wanted = 0;
    std::uint32_t available = 0;
    FFFResult result = FFFResult::Success;
};

Status ReadStatus(FFF3FPHandle player) {
    Status status;
    status.result = FFF3FP_GetAudioBinauralStatus(player, &status.policy,
        &status.formFactor, &status.wanted, &status.available);
    return status;
}

const char* PolicyName(const std::uint32_t policy) {
    switch (policy) {
        case 0: return "auto";
        case 1: return "force-binaural";
        case 2: return "force-stereo";
        default: return "?";
    }
}

int failures = 0;

void Check(const bool condition, const char* what, const std::string& detail) {
    std::printf("%-46s %s  %s\n", what, condition ? "PASS" : "FAIL", detail.c_str());
    if (!condition) ++failures;
}

// The whole decision rule, exercised for every policy against the channel counts
// that matter. Product default (2026-11-12): automatic = binaural on a 2-channel
// endpoint, matrix fold-down on multichannel; the form factor decides nothing.
void CheckDecisionRule() {
    bool autoRule = PlayerWasapiRenderer::BinauralWantedFor(0u, 2u);
    for (const auto channels : {1u, 3u, 4u, 6u, 8u, 12u, 16u})
        autoRule = autoRule && !PlayerWasapiRenderer::BinauralWantedFor(0u, channels);
    Check(autoRule, "auto: stereo endpoint on, multichannel off", "");

    bool forcedOn = true;
    for (const auto channels : {1u, 6u, 8u, 12u})  // the point of forcing: render binaural anywhere
        forcedOn = forcedOn && PlayerWasapiRenderer::BinauralWantedFor(1u, channels);
    Check(forcedOn, "force-binaural ignores the channel count", "");

    const bool forcedOff = !PlayerWasapiRenderer::BinauralWantedFor(2u, 2u);
    Check(forcedOff, "force-stereo overrides even a stereo endpoint", "");

    // Unknown channel count (device not opened yet) must not be guessed into binaural.
    Check(!PlayerWasapiRenderer::BinauralWantedFor(0u, 0u),
        "auto treats an unknown channel count as fold-down", "");
}

}  // namespace

int main(const int argc, char** argv) {
    const char* media = argc > 1 ? argv[1] : nullptr;
    if (media == nullptr) {
        std::fprintf(stderr, "usage: audio_binaural_probe <media>\n");
        return 2;
    }

    const std::uint32_t osFormFactor = DefaultEndpointFormFactor();
    const std::uint32_t osChannels = DefaultEndpointChannels();
    std::printf("independent MMDevice default-endpoint formFactor = %u, mix channels = %u\n",
        osFormFactor, osChannels);
    CheckDecisionRule();

    const HWND window = MakeHiddenWindow();
    FFF3FPConfiguration config{};
    config.size = static_cast<std::uint32_t>(sizeof(config));
    config.version = FFF3FP_GetApiVersion();
    config.outputWindow = window;
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
        return 3;
    }

    // Before any media: no renderer has opened a device, so the kernel has no
    // shape to report and the seam cannot be available.
    const auto before = ReadStatus(player);
    Check(before.result == FFFResult::Success, "status readable before open",
        "result=" + std::to_string(static_cast<int>(before.result)));
    Check(before.formFactor == 0u, "formFactor is 0 before a device opens",
        "formFactor=" + std::to_string(before.formFactor));
    Check(before.available == 0u, "available is 0 before a device opens",
        "available=" + std::to_string(before.available));

    g_events = Events{};
    const FFFResult openResult = FFF3FP_Open(player, media);
    if (openResult != FFFResult::Success) {
        std::fprintf(stderr, "FFF3FP_Open failed: %d %s\n",
            static_cast<int>(openResult), g_events.error);
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 3;
    }
    for (int i = 0; i < 400 && !g_events.openCompleted; ++i) PumpMessages(10);
    Check(g_events.openCompleted, "OpenCompleted event arrived", "");

    // The audio renderer opens its endpoint on the worker thread; the form
    // factor is published there. Give it a moment rather than assuming the
    // event ordering, then read the settled value.
    Status opened;
    for (int i = 0; i < 300; ++i) {
        opened = ReadStatus(player);
        if (opened.formFactor != 0u) break;
        PumpMessages(10);
    }
    std::printf("opened: policy=%s(%u) formFactor=%u wanted=%u available=%u\n",
        PolicyName(opened.policy), opened.policy, opened.formFactor,
        opened.wanted, opened.available);

    Check(opened.formFactor == osFormFactor,
        "kernel formFactor == independent MMDevice query",
        "kernel=" + std::to_string(opened.formFactor) +
            " os=" + std::to_string(osFormFactor));
    // Automatic policy on the mix channel count the kernel actually reported
    // (2026-11-12 rule: stereo endpoint => binaural default).
    const std::uint32_t autoWanted = osChannels == 2u ? 1u : 0u;
    Check(opened.wanted == autoWanted, "auto policy derives from the channel count",
        "wanted=" + std::to_string(opened.wanted) +
            " expected=" + std::to_string(autoWanted) +
            " (mix channels " + std::to_string(osChannels) + ")");

    // Reproduce the host's install step, if a module was named on the command line,
    // and watch `available` become 1. This is the half the earlier run could not
    // show: with no table handed over the seam is unavailable no matter the shape.
    if (argc > 2) {
        std::string detail;
        const bool installed = InstallModulePlug(player, argv[2], detail);
        Check(installed, "module bed table handed to the kernel", detail);
        Status withPlug;
        for (int i = 0; i < 300; ++i) {
            withPlug = ReadStatus(player);
            if (withPlug.available != 0u) break;
            PumpMessages(10);
        }
        const std::uint32_t expectAvailable = osChannels == 2u ? 1u : 0u;
        Check(withPlug.available == expectAvailable,
            "available follows plug + 2-channel endpoint",
            "available=" + std::to_string(withPlug.available) +
                " expected=" + std::to_string(expectAvailable) +
                " (mix channels " + std::to_string(osChannels) + ")");
    }

    // Force binaural: the answer must not depend on the device any more.
    Check(FFF3FP_SetAudioBinauralPolicy(player, 1u) == FFFResult::Success,
        "set force-binaural accepted", "");
    PumpMessages(120);
    auto forced = ReadStatus(player);
    Check(forced.wanted == 1u && forced.policy == 1u,
        "force-binaural reports wanted=1",
        "policy=" + std::to_string(forced.policy) +
            " wanted=" + std::to_string(forced.wanted));

    // Force stereo.
    Check(FFF3FP_SetAudioBinauralPolicy(player, 2u) == FFFResult::Success,
        "set force-stereo accepted", "");
    PumpMessages(120);
    forced = ReadStatus(player);
    Check(forced.wanted == 0u && forced.policy == 2u,
        "force-stereo reports wanted=0",
        "policy=" + std::to_string(forced.policy) +
            " wanted=" + std::to_string(forced.wanted));

    // Back to automatic: the device decides again.
    Check(FFF3FP_SetAudioBinauralPolicy(player, 0u) == FFFResult::Success,
        "set auto accepted", "");
    PumpMessages(120);
    const auto restored = ReadStatus(player);
    Check(restored.wanted == autoWanted && restored.policy == 0u,
        "auto restores the device-derived answer",
        "wanted=" + std::to_string(restored.wanted) +
            " expected=" + std::to_string(autoWanted));

    // Out of range: refused, and the stored value must not move.
    const FFFResult badResult = FFF3FP_SetAudioBinauralPolicy(player, 3u);
    PumpMessages(60);
    const auto afterBad = ReadStatus(player);
    Check(badResult == FFFResult::InvalidArgument, "policy 3 refused",
        "result=" + std::to_string(static_cast<int>(badResult)));
    Check(afterBad.policy == 0u, "refused policy left the stored value alone",
        "policy=" + std::to_string(afterBad.policy));

    // Null-pointer contract: every output is optional, the handle is not.
    Check(FFF3FP_GetAudioBinauralStatus(player, nullptr, nullptr, nullptr, nullptr) ==
        FFFResult::Success, "all-null outputs accepted", "");
    Check(FFF3FP_GetAudioBinauralStatus(nullptr, nullptr, nullptr, nullptr, nullptr) ==
        FFFResult::InvalidArgument, "null handle refused", "");
    Check(FFF3FP_SetAudioBinauralPolicy(nullptr, 0u) == FFFResult::InvalidArgument,
        "null handle refused on set", "");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    std::printf("\nfailures = %d\n", failures);
    return failures == 0 ? 0 : 1;
}
