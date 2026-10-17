// rd2_geom_probe.cpp -- lane L1 discriminator for RD-2 (float-RGB upload ignoring the
// adaptive-downscale planes). Written by L1; lives outside the repository.
//
// Two-phase design, because the adaptive policy can only be armed by sustained software
// decode drops, and a JPEG XR still produces exactly one frame:
//   phase 1: open a heavy CPU-decoded video  -> the drop window trips [adaptive] engaged
//            (adaptiveDownscaleActive_ lives on the renderer, not on the media)
//   phase 2: close it, open the JXR          -> layout 3 frame now hits the float upload
//            arm while the policy is engaged and the source is far bigger than the dest.
//
// Readouts that separate "correct resample" from "full-size upload into a small texture":
//   * a dense pixel row across the presented frame (geometry), and
//   * the videoUpload100ns delta over phase 2 (metric coverage).
//
// usage:
//   rd2_geom_probe --measure <media> [--warm <media>] [--warm-seconds N]
//                  [--adaptive on|off] [--threshold N] [--client WxH]
//                  [--points N] [--row-y F] [--hdr]
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
std::atomic<bool> g_openCompleted{false};

void __cdecl OnEvent(void*, FFF3FPEvent type, const char*) {
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message{};
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

bool GetOpen(FFF3FPHandle player, const std::string& pathUtf8) {
    g_openCompleted = false;
    if (FFF3FP_Open(player, pathUtf8.c_str()) != FFFResult::Success) {
        std::printf("[error] Open failed for %s\n", pathUtf8.c_str());
        return false;
    }
    for (int i = 0; i < 400 && !g_openCompleted; ++i) Pump(50);
    if (!g_openCompleted) { std::printf("[error] Open timed out for %s\n", pathUtf8.c_str()); return false; }
    return true;
}

void TakeSnapshot(FFF3FPSnapshot& snapshot, FFF3FPHandle player) {
    snapshot = {};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
}

void DumpTarget(FFF3FPHandle player, const char* tag) {
    FFF3FPRenderTargetInfo info{};
    info.size = sizeof(info);
    info.version = 20;
    const auto result = FFF3FP_GetRenderTargetInfo(player, &info);
    std::printf("[target]%s rc=%d swap=%ux%u client=%ux%u dest=(%d,%d,%ux%u) outBits=%u hdr=%u\n",
        tag, static_cast<int>(result), info.swapWidth, info.swapHeight, info.clientWidth,
        info.clientHeight, info.destX, info.destY, info.destWidth, info.destHeight,
        info.outputBitDepth, info.hdr);
}
}  // namespace

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    // Match physical pixels to the client rect so the probe's coordinates are the
    // swapchain's coordinates, not a DPI-scaled fraction of them.
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    std::string measure, warm, warmAudio;
    bool adaptive = true, hdr = false;
    int threshold = 1, warmSeconds = 8, points = 41, reopens = 1;
    int clientW = 800, clientH = 450;
    double rowFraction = 0.5, colFraction = 0.5;
    for (int i = 1; i < argc; ++i) {
        const std::string key = argv[i];
        const std::string next = i + 1 < argc ? argv[i + 1] : std::string();
        if (key == "--measure") { measure = next; ++i; }
        else if (key == "--warm") { warm = next; ++i; }
        else if (key == "--warm-audio") { warmAudio = next; ++i; }
        else if (key == "--adaptive") { adaptive = next != "off"; ++i; }
        else if (key == "--threshold") { threshold = std::atoi(next.c_str()); ++i; }
        else if (key == "--warm-seconds") { warmSeconds = std::atoi(next.c_str()); ++i; }
        else if (key == "--client") { std::sscanf(next.c_str(), "%dx%d", &clientW, &clientH); ++i; }
        else if (key == "--points") { points = std::atoi(next.c_str()); ++i; }
        else if (key == "--reopens") { reopens = std::atoi(next.c_str()); ++i; }
        else if (key == "--row-y") { rowFraction = std::atof(next.c_str()); ++i; }
        else if (key == "--col-x") { colFraction = std::atof(next.c_str()); ++i; }
        else if (key == "--hdr") hdr = true;
        else std::printf("[warn] ignoring argument %s\n", key.c_str());
    }
    if (measure.empty()) {
        std::printf("usage: rd2_geom_probe --measure <media> [--warm <media>] ...\n");
        return 2;
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpRd2GeomWindow";
    if (RegisterClassW(&windowClass) == 0) { std::printf("RegisterClassW failed\n"); return 1; }
    RECT desired{0, 0, clientW, clientH};
    const auto style = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
    AdjustWindowRect(&desired, style, FALSE);
    HWND window = CreateWindowExW(0, L"3fpRd2GeomWindow", L"3fp rd2 geometry probe", style,
        40, 40, desired.right - desired.left, desired.bottom - desired.top, nullptr, nullptr,
        GetModuleHandleW(nullptr), nullptr);
    if (window == nullptr) { std::printf("CreateWindowExW failed\n"); return 1; }
    Pump(250);
    RECT client{};
    GetClientRect(window, &client);
    std::printf("[cfg] adaptive=%d threshold=%d hdr=%d client=%ldx%ld measure=%s warm=%s\n",
        adaptive ? 1 : 0, threshold, hdr ? 1 : 0, client.right, client.bottom,
        measure.c_str(), warm.empty() ? "(none)" : warm.c_str());

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = hdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;
    configuration.adaptiveDownscaleBeforeUpload = adaptive ? 1u : 0u;
    configuration.adaptiveDownscaleDropPercent = static_cast<std::uint32_t>(threshold);

    FFF3FPHandle player = nullptr;
    const auto created = FFF3FP_Create(&configuration, &player);
    if (created != FFFResult::Success || player == nullptr) {
        std::printf("Create failed: %d\n", static_cast<int>(created));
        return 1;
    }

    // ---- phase 1: arm the policy with real software-decode drops -------------------
    if (!warm.empty()) {
        if (!GetOpen(player, warm)) { FFF3FP_Destroy(player); return 1; }
        if (!warmAudio.empty()) {
            const auto loaded = FFF3FP_LoadExternalAudio(player, warmAudio.c_str(), 0, 0);
            std::printf("[phase1] LoadExternalAudio=%d\n", static_cast<int>(loaded));
        }
        FFF3FP_Play(player);
        FFF3FPSnapshot tick{};
        for (int i = 0; i < warmSeconds * 2; ++i) {
            Pump(500);
            TakeSnapshot(tick, player);
            std::printf("[warmtick] t=%.1fs presented=%llu dropped=%llu upload100ns=%llu\n",
                0.5 * (i + 1), static_cast<unsigned long long>(tick.presentedVideoFrames),
                static_cast<unsigned long long>(tick.droppedVideoFrames),
                static_cast<unsigned long long>(tick.videoUpload100ns));
        }
        FFF3FPSnapshot warmSnap{};
        TakeSnapshot(warmSnap, player);
        std::printf("[phase1] warm presented=%llu dropped=%llu upload_ms=%.2f audioStream=%d decodedAudio=%llu"
            " audioPos100ns=%lld pos100ns=%lld\n",
            static_cast<unsigned long long>(warmSnap.presentedVideoFrames),
            static_cast<unsigned long long>(warmSnap.droppedVideoFrames),
            static_cast<double>(warmSnap.videoUpload100ns) / 10000.0,
            static_cast<int>(warmSnap.selectedAudioStream),
            static_cast<unsigned long long>(warmSnap.decodedAudioFrames),
            static_cast<long long>(warmSnap.audioPosition100ns),
            static_cast<long long>(warmSnap.position100ns));
        DumpTarget(player, " afterWarm");
        FFF3FP_Close(player);
        Pump(300);
        DumpTarget(player, " afterWarmClose");
    }

    // Prime the recorded destination rect by opening the same media once more when asked
    // to (lastDest* is only written by a draw, so the very first render of a session can
    // never see a destination to downscale towards).
    for (int r = 1; r < reopens; ++r) {
        if (!GetOpen(player, measure)) { FFF3FP_Destroy(player); return 1; }
        FFF3FP_Play(player);
        Pump(900);
        DumpTarget(player, " afterPrimeOpen");
        FFF3FP_Close(player);
        Pump(300);
    }

    // ---- phase 2: measure the layout-3 frame under the (hopefully) engaged policy ----
    FFF3FPSnapshot before{};
    TakeSnapshot(before, player);
    std::printf("[before] presented=%llu dropped=%llu upload100ns=%llu\n",
        static_cast<unsigned long long>(before.presentedVideoFrames),
        static_cast<unsigned long long>(before.droppedVideoFrames),
        static_cast<unsigned long long>(before.videoUpload100ns));
    if (!GetOpen(player, measure)) { FFF3FP_Destroy(player); return 1; }
    FFF3FP_Play(player);
    Pump(1800);
    DumpTarget(player, " afterMeasureOpen");
    FFF3FPSnapshot after{};
    TakeSnapshot(after, player);

    const auto presented = after.presentedVideoFrames >= before.presentedVideoFrames
        ? after.presentedVideoFrames - before.presentedVideoFrames : after.presentedVideoFrames;
    const auto dropped = after.droppedVideoFrames >= before.droppedVideoFrames
        ? after.droppedVideoFrames - before.droppedVideoFrames : after.droppedVideoFrames;
    const auto uploadDelta = after.videoUpload100ns >= before.videoUpload100ns
        ? after.videoUpload100ns - before.videoUpload100ns : after.videoUpload100ns;
    const auto uploadMs = static_cast<double>(uploadDelta) / 10000.0;
    // lane M addition: layout 0 has never recorded videoUpload100ns, and its CPU cost
    // lives in softwareConvert100ns, so both are printed to separate "full-size convert +
    // cropped upload" from "convert already at the uploaded size".
    const auto convertDelta = after.softwareConvert100ns >= before.softwareConvert100ns
        ? after.softwareConvert100ns - before.softwareConvert100ns : after.softwareConvert100ns;
    std::printf("[phase2] source=%ux%u outBits=%u presented=%llu dropped=%llu upload100ns_delta=%llu upload_ms=%.4f",
        after.videoWidth, after.videoHeight, after.videoOutputBitDepth,
        static_cast<unsigned long long>(presented), static_cast<unsigned long long>(dropped),
        static_cast<unsigned long long>(uploadDelta), uploadMs);
    std::printf(" convert100ns_delta=%llu convert_ms=%.4f",
        static_cast<unsigned long long>(convertDelta),
        static_cast<double>(convertDelta) / 10000.0);
    std::printf(" upload_ms_per_frame=%.4f upload_ms_per_frame_abs=%.4f\n",
        presented > 0 ? uploadMs / static_cast<double>(presented) : 0.0,
        after.presentedVideoFrames > 0
            ? static_cast<double>(after.videoUpload100ns) / 10000.0 /
              static_cast<double>(after.presentedVideoFrames) : 0.0);
    std::printf("[phase2] after_abs presented=%llu dropped=%llu upload100ns=%llu\n",
        static_cast<unsigned long long>(after.presentedVideoFrames),
        static_cast<unsigned long long>(after.droppedVideoFrames),
        static_cast<unsigned long long>(after.videoUpload100ns));

    auto readPoint = [&](std::uint32_t x, std::uint32_t y, const char* tag) {
        FFF3FPVideoPixelProbe probe{};
        probe.size = sizeof(probe);
        probe.version = 20;
        probe.x = x;
        probe.y = y;
        const auto result = FFF3FP_ReadVideoPixel(player, &probe);
        if (result != FFFResult::Success) {
            std::printf("%s %u %u FAILED %d\n", tag, x, y, static_cast<int>(result));
            return;
        }
        std::printf("%s %u %u %.5f %.5f %.5f %.5f %u\n", tag, x, y,
            probe.red, probe.green, probe.blue, probe.alpha, probe.outputBitDepth);
    };
    const auto rowY = static_cast<std::uint32_t>(static_cast<double>(client.bottom) * rowFraction);
    const auto colX = static_cast<std::uint32_t>(static_cast<double>(client.right) * colFraction);
    for (int i = 0; i < points; ++i) {
        const auto x = static_cast<std::uint32_t>(1u + static_cast<unsigned long>(
            static_cast<double>(client.right - 2u) * static_cast<double>(i) /
            static_cast<double>(points - 1)));
        readPoint(x, rowY, "[row]");
    }
    for (int i = 0; i < points; ++i) {
        const auto y = static_cast<std::uint32_t>(1u + static_cast<unsigned long>(
            static_cast<double>(client.bottom - 2u) * static_cast<double>(i) /
            static_cast<double>(points - 1)));
        readPoint(colX, y, "[col]");
    }

    Pump(200);
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    std::printf("[done]\n");
    return 0;
}
