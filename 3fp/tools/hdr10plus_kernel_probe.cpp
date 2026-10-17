// hdr10plus_kernel_probe.cpp — read the ST 2094 diagnostics back out of the
// shipping kernel.
//
// Why a dedicated probe
// ---------------------
// 3fp/tools/build_probe.py resolves `FFF.Player.Api.h` from 3fp/kernel, which
// tracks a *different* upstream branch (API 19) than the shipping tree this
// change builds against (API 17). Reusing read_pixel_probe would therefore
// measure a struct layout the DLL does not produce. This probe is compiled
// directly against third_party/fff_project/FFF.Native so the struct it declares
// is the one the DLL was built with.
//
// Usage: hdr10plus_kernel_probe <media path> [--hdr]

#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include "3FP/Api/FFF.Player.Api.h"

namespace {
FFF3FPHandle g_player = nullptr;
std::atomic<bool> g_openCompleted{false};

void __cdecl OnEvent(void* context, FFF3FPEvent type, const char* detail) {
    (void)context; (void)detail;
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::milliseconds(milliseconds);
    MSG message;
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

void Dump(const char* label, FFF3FPSnapshot& snapshot) {
    std::printf("%s: fmt=%u path=%u compat=%u dyn=%u fallback=%u\n", label,
        static_cast<unsigned>(snapshot.hdrFormat),
        static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.compatibleHdrFormats,
        snapshot.dynamicHdrMetadataActive,
        snapshot.hdrFallbackActive);
    std::printf("      peak=%u targetPeak=%u outBits=%u\n",
        snapshot.sourcePeakNits, snapshot.effectiveTargetPeakNits,
        snapshot.videoOutputBitDepth);
    std::printf("      windows=%u degrade=%u serial=%llu held=%llu targeted=%u\n",
        snapshot.dynamicMetadataWindows,
        snapshot.dynamicMetadataDegrade,
        static_cast<unsigned long long>(snapshot.dynamicMetadataSerial),
        static_cast<unsigned long long>(snapshot.dynamicMetadataHeldFrames),
        snapshot.dynamicMetadataTargetedNits);
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: hdr10plus_kernel_probe <media path> [--hdr]\n");
        return 2;
    }
    const char* path = argv[1];
    bool requestHdr = false;
    for (int i = 2; i < argc; ++i)
        if (std::strcmp(argv[i], "--hdr") == 0) requestHdr = true;

    std::printf("dll api version = %u (probe expects 17)\n", FFF3FP_GetApiVersion());

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpHdr10PlusProbe";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, L"3fpHdr10PlusProbe", L"hdr10plus probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540,
        nullptr, nullptr, windowClass.hInstance, nullptr);
    if (window == nullptr) { std::printf("CreateWindowExW failed\n"); return 1; }
    Pump(200);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = requestHdr ? FFF3FPColorMode::MapToHdr
                                         : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.forceHdrOutput = 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    if (FFF3FP_Create(&configuration, &g_player) != FFFResult::Success ||
        g_player == nullptr) {
        std::printf("Create failed\n");
        return 6;
    }
    if (FFF3FP_Open(g_player, path) != FFFResult::Success) {
        char error[512]{}; FFF3FP_GetLastError(g_player, error, sizeof(error), nullptr);
        std::printf("Open failed: %s\n", error);
        FFF3FP_Destroy(g_player);
        return 7;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    Pump(300);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(g_player, &snapshot) != FFFResult::Success) {
        std::printf("GetSnapshot failed at open\n");
        FFF3FP_Destroy(g_player);
        return 8;
    }
    Dump("open ", snapshot);

    FFF3FP_Play(g_player);
    // Sample repeatedly: the point of the feature is that the mapping follows
    // the metadata frame by frame, so a single reading cannot show that.
    for (int step = 0; step < 8; ++step) {
        Pump(400);
        std::memset(&snapshot, 0, sizeof(snapshot));
        snapshot.size = sizeof(snapshot);
        snapshot.version = 20;
        if (FFF3FP_GetSnapshot(g_player, &snapshot) != FFFResult::Success) continue;
        char label[32];
        std::snprintf(label, sizeof(label), "t=%4dms", (step + 1) * 400);
        Dump(label, snapshot);
    }

    std::printf("callback api version = %u\n", FFF3FP_GetApiVersion());
    Pump(200);
    FFF3FP_Close(g_player);
    FFF3FP_Destroy(g_player);
    return 0;
}
