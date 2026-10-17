// Does HDR10+ dynamic metadata actually reach the picture, in a given presentation arm?
//
// Prints a digest of one scanline of read-back pixels for one media file, plus what the
// snapshot claims. Pairing two runs and deciding SAME/DIFFER is done by
// hdr10plus_arm_effect.ps1 -- one process per measurement on purpose: reopening the
// decoder in the same process is known to fail from the second round onwards.
//
// The A/B pair is the same clip with and without the ST 2094-40 SEI (make_true_control.py
// builds the control with filter_units=remove_types=39 and proves by full-stream decoded
// md5 that the pixels are identical), so ANY difference in what is drawn is attributable
// to the dynamic metadata and nothing else.
//
// usage: hdr10plus_arm_effect_probe <media> [--color sdr|hdr] [--forcehdr 0|1]
//                                   [--frame N] [--rows N] [--width W]

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

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

}  // namespace

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) {
        std::printf("usage: hdr10plus_arm_effect_probe <media> [--color sdr|hdr] "
                    "[--forcehdr 0|1] [--frame N] [--rows N] [--width W]\n");
        return 2;
    }
    bool hdrArm = false;
    unsigned forceHdr = 0;
    int frame = 120, rows = 7, width = 256;
    for (int i = 2; i < argc; ++i) {
        const std::string key = argv[i];
        const auto next = [&]() -> const char* { return i + 1 < argc ? argv[++i] : ""; };
        if (key == "--color") hdrArm = std::strcmp(next(), "hdr") == 0;
        else if (key == "--forcehdr") forceHdr = static_cast<unsigned>(atoi(next()));
        else if (key == "--frame") frame = atoi(next());
        else if (key == "--rows") rows = atoi(next());
        else if (key == "--width") width = atoi(next());
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"hdr10plusArmEffect";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, wc.lpszClassName, L"HDR10+ arm effect",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540, nullptr, nullptr, wc.hInstance, nullptr);
    if (window == nullptr) return 1;
    Pump(300);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = hdrArm ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.forceHdrOutput = forceHdr;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) return 4;
    if (FFF3FP_Open(player, argv[1]) != FFFResult::Success) return 5;
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (!g_openCompleted) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 6; }

    if (FFF3FP_Play(player) != FFFResult::Success) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 7; }
    Pump(1200);
    // Land on an exact frame, then stop: two runs of the same frame are comparable,
    // two free-running playbacks are not.
    if (FFF3FP_SeekFrame(player, frame) != FFFResult::Success) {
        std::printf("SEEKFRAME failed\n");
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 8;
    }
    Pump(700);
    FFF3FP_Pause(player);
    Pump(500);

    FFF3FPSnapshot snapshot{};
    // A seek that has not settled makes the two runs compare different frames, which
    // reads as "the metadata changed the picture". Wait for the frame that was asked for
    // and refuse to measure otherwise.
    bool landed = false;
    for (int i = 0; i < 60; ++i) {
        snapshot.size = sizeof(snapshot);
        snapshot.version = 20;
        if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) break;
        if (static_cast<int>(snapshot.frameIndex) == frame) { landed = true; break; }
        Pump(50);
    }
    if (!landed) {
        std::printf("WRONGLANDED requested=%d landed=%d -> comparison invalid\n",
            frame, static_cast<int>(snapshot.frameIndex));
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 12;
    }
    if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) {
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 9;
    }

    // Sample several rows spread across the picture. One hand-picked row can land in a
    // letterbox/black band, where every arm hashes the same zeros and the comparison
    // proves nothing either way -- observed with row 170 of a 4K frame.
    // Read-back coordinates are the output window's CLIENT space, not the source frame
    // (asking for y=648 of a 960x540 client returns InvalidArgument). Sample by fractions
    // of the client rect instead of fractions of the video height.
    RECT client{};
    if (!GetClientRect(window, &client) || client.bottom <= 0 || client.right <= 0) {
        std::printf("NOCLIENTRECT\n");
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 14;
    }
    if (width > client.right) width = client.right;
    const unsigned sourceHeight = static_cast<unsigned>(client.bottom);
    std::vector<float> pixels(static_cast<std::size_t>(width) * rows * 4, -12345.0f);
    std::uint32_t readbackBits = 0;
    for (int r = 0; r < rows; ++r) {
        const double fraction = rows == 1 ? 0.5 : 0.30 + 0.40 * r / (rows - 1);
        const auto y = static_cast<std::uint32_t>(sourceHeight * fraction);
        auto* const slot = pixels.data() + static_cast<std::size_t>(r) * width * 4;
        const auto result = FFF3FP_ReadVideoPixelRegion(player, 0u, y,
            static_cast<std::uint32_t>(width), 1, slot,
            static_cast<std::uint32_t>(static_cast<std::size_t>(width) * 4), &readbackBits);
        if (result != FFFResult::Success) {
            std::printf("READBACK failed (%d) at y=%u\n", static_cast<int>(result), y);
            FFF3FP_Close(player);
            FFF3FP_Destroy(player);
            return 10;
        }
    }

    // Quantise before hashing so FP16 rounding cannot manufacture a difference; a real
    // curve change moves luminance by far more than 1/4096 of full scale.
    unsigned long long digest = 0xcbf29ce484222325ULL;
    std::size_t nonZero = 0, samples = 0;
    float maxValue = 0.0f;
    for (std::size_t i = 0; i < pixels.size(); i += 4) {
        for (int c = 0; c < 3; ++c) {
            const float value = pixels[i + static_cast<std::size_t>(c)];
            ++samples;
            if (std::fabs(value) > 1e-4f) ++nonZero;
            if (value > maxValue) maxValue = value;
            digest ^= static_cast<unsigned long long>(std::lround(value * 4096.0f) + 0x100000000LL);
            digest *= 0x1000193ULL;
        }
    }
    if (nonZero == 0) {
        std::printf("ALLBLACK %llu samples (rows=%d) -> nothing to compare, refusing to judge\n",
            static_cast<unsigned long long>(samples), rows);
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 13;
    }

    std::printf("PROBE media=%s arm=%s frame=%d rows=%d width=%d\n", argv[1],
        hdrArm ? "MapToHdr" : "MapToSdr", frame, rows, width);
    std::printf("  digest=%016llx nonZero=%llu/%llu maxValue=%.5f\n", digest,
        static_cast<unsigned long long>(nonZero), static_cast<unsigned long long>(samples),
        maxValue);
    // The peaks below are the evidence for the display-referred arm: on MapToHdr the
    // renderer does not reshape pixels (docs/58 T3), so what HDR10+ must change there is
    // the per-frame signalling values themselves. If they equal the static control's,
    // "metadata guides the display mapping" would still be an unmeasured claim.
    std::printf("  peaks sourceNits=%u effectiveTargetNits=%u displayPeakNits=%u\n",
        snapshot.sourcePeakNits, snapshot.effectiveTargetPeakNits, snapshot.displayPeakNits);
    std::printf("  claims path=%u fmt=%u outbits=%u wins=%u dyn=%u readbackBits=%u landedFrame=%d\n",
        static_cast<unsigned>(snapshot.hdrProcessingPath),
        static_cast<unsigned>(snapshot.hdrFormat), snapshot.videoOutputBitDepth,
        snapshot.dynamicMetadataWindows, snapshot.dynamicHdrMetadataActive,
        readbackBits,
        static_cast<int>(snapshot.frameIndex));
    if (forceHdr != 0 && snapshot.videoOutputBitDepth != 16) {
        std::printf("  GUARD: HDR arm requested but output bit depth is %u -> not the 16-bit chain\n",
            snapshot.videoOutputBitDepth);
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 11;
    }
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    return 0;
}
