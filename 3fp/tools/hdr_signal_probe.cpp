// Measures the HDR presentation diagnostics on a real stream.
//
// Closes the one gap the arm-effect gate could not reach: whether the per-frame display
// signalling that docs/58 task T3 promises actually fires, and how often the 2%-movement
// change gate suppresses it. Also reports the curve gate as frame counts, so "the dynamic
// path is active" stops being a claim about a shader constant.
//
// Two polls are taken N seconds apart; the delta is the measurement. Polling twice also
// disambiguates a mixed read of the signalled block (fields are atomic individually): if
// the two polls agree, the block was stable during the poll.
//
// usage: hdr_signal_probe <media> [--color sdr|hdr] [--forcehdr 0|1]
//                         [--seconds 6] [--headless]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

bool g_openCompleted = false;

void OnEvent(void*, const FFF3FPEvent event, const char*) {
    if (event == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
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

void Read(FFF3FPHandle player, FFF3FPHdrSignalDiagnostics& out) {
    out = FFF3FPHdrSignalDiagnostics{};
    out.size = sizeof(out);
    out.version = 20;
    const auto result = FFF3FP_GetHdrSignalDiagnostics(player, &out);
    if (result != FFFResult::Success) {
        std::printf("GetHdrSignalDiagnostics failed (%d)\n", static_cast<int>(result));
        out.size = 0;
    }
}

void Dump(const char* label, const FFF3FPHdrSignalDiagnostics& d) {
    std::printf("%s curve applied=%llu refused=%llu | SetHDRMetaData calls=%llu suppressed=%llu\n",
        label,
        static_cast<unsigned long long>(d.dynamicCurveFramesApplied),
        static_cast<unsigned long long>(d.dynamicCurveFramesRefused),
        static_cast<unsigned long long>(d.setHdrMetaDataCalls),
        static_cast<unsigned long long>(d.setHdrMetaDataSuppressed));
    // DXGI_HDR_METADATA_HDR10 stores the mastering luminances in units of 1/10000 cd/m^2
    // and both light levels in cd/m^2.
    std::printf("%s signalled maxMastering=%.1f nits minMastering=%.4f nits maxCLL=%u maxFALL=%u\n",
        label, d.lastSignaledMaxMasteringLuminance / 10000.0,
        d.lastSignaledMinMasteringLuminance / 10000.0,
        d.lastSignaledMaxContentLightLevel, d.lastSignaledMaxFrameAverageLightLevel);
}

}  // namespace

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) {
        std::printf("usage: hdr_signal_probe <media> [--color sdr|hdr] [--forcehdr 0|1] "
                    "[--seconds N] [--headless]\n");
        return 2;
    }
    bool hdrArm = false;
    bool headless = false;
    unsigned forceHdr = 0;
    int seconds = 6;
    for (int i = 2; i < argc; ++i) {
        const std::string key = argv[i];
        const auto next = [&]() -> const char* { return i + 1 < argc ? argv[++i] : ""; };
        if (key == "--color") hdrArm = std::strcmp(next(), "hdr") == 0;
        else if (key == "--forcehdr") forceHdr = static_cast<unsigned>(atoi(next()));
        else if (key == "--headless") headless = true;
        else if (key == "--seconds") seconds = atoi(next());
    }

    HWND window = nullptr;
    if (!headless) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"hdrSignalProbe";
        RegisterClassW(&wc);
        window = CreateWindowExW(0, wc.lpszClassName, L"HDR signalling",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540, nullptr, nullptr,
            wc.hInstance, nullptr);
        if (window == nullptr) return 1;
        Pump(300);
    }

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = hdrArm ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.forceHdrOutput = forceHdr;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) return 4;
    if (FFF3FP_Open(player, argv[1]) != FFFResult::Success) return 5;
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (FFF3FP_Play(player) != FFFResult::Success) return 7;

    Pump(1500);
    FFF3FPHdrSignalDiagnostics first{};
    Read(player, first);
    if (first.size == 0) return 8;
    Pump(seconds * 1000);
    FFF3FPHdrSignalDiagnostics second{};
    Read(player, second);
    if (second.size == 0) return 8;

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);

    std::printf("media=%s arm=%s seconds=%d hdrFormat=%u path=%u dynActive=%u outbits=%u\n",
        argv[1], hdrArm ? "MapToHdr" : "MapToSdr", seconds,
        static_cast<unsigned>(snapshot.hdrFormat),
        static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive, snapshot.videoOutputBitDepth);
    Dump("  t1", first);
    Dump("  t2", second);
    std::printf("DELTA curve applied=%llu refused=%llu | calls=%llu suppressed=%llu\n",
        static_cast<unsigned long long>(second.dynamicCurveFramesApplied - first.dynamicCurveFramesApplied),
        static_cast<unsigned long long>(second.dynamicCurveFramesRefused - first.dynamicCurveFramesRefused),
        static_cast<unsigned long long>(second.setHdrMetaDataCalls - first.setHdrMetaDataCalls),
        static_cast<unsigned long long>(second.setHdrMetaDataSuppressed - first.setHdrMetaDataSuppressed));
    const bool blockStable =
        first.lastSignaledMaxContentLightLevel == second.lastSignaledMaxContentLightLevel &&
        first.lastSignaledMaxFrameAverageLightLevel == second.lastSignaledMaxFrameAverageLightLevel &&
        first.lastSignaledMaxMasteringLuminance == second.lastSignaledMaxMasteringLuminance;
    // Name the field that moved; printing maxCLL while maxFALL is what changed reads
    // like a contradiction (observed: "CHANGED (maxCLL 294 -> 294)").
    std::printf("SIGNALLED BLOCK %s over the window (mastering %u -> %u, maxCLL %u -> %u, maxFALL %u -> %u)\n",
        blockStable ? "UNCHANGED" : "CHANGED",
        first.lastSignaledMaxMasteringLuminance, second.lastSignaledMaxMasteringLuminance,
        first.lastSignaledMaxContentLightLevel, second.lastSignaledMaxContentLightLevel,
        first.lastSignaledMaxFrameAverageLightLevel, second.lastSignaledMaxFrameAverageLightLevel);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
