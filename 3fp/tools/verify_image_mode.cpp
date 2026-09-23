// 3FP 图片模式 / 广色域 —— 实机验证程序
//
// 目的：在真实窗口 + 真实 FFmpeg DLL 下跑内核，检查那些只靠读代码无法确认的行为：
//   · 静态图打开后是否还跳 Ended (K1)、Seek 是否还报错 (K2)
//   · 源是否报告为 HDR、解码帧的 primaries / transfer 到底是什么
//   · 请求 MapToHdr 后，交换链最终用了几位（16 = scRGB 生效，8/10 = 已回落 SDR）
//   · 广色域 SDR 源能不能进 scRGB（门禁是否真的放开）
//
// 用法：
//   verify_image_mode <媒体路径> [--hdr]
//     --hdr  初始即请求 MapToHdr（默认 MapToSdr）
//
// 构建（VS 的 x64 环境）：
//   vcvars64.bat
//   cl /EHsc /std:c++20 /I <内核根> verify_image_mode.cpp <路径>\FFF.Native.lib
// 运行前把 Shared FFmpeg 的 DLL 放到 exe 同目录（内核是延迟加载的）。

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

bool g_openCompleted = false;
bool g_playbackEnded = false;
bool g_colorModeChanged = false;
char g_eventLog[4096] = {};

const char* StateName(std::uint32_t state) {
    switch (static_cast<FFF3FPState>(state)) {
    case FFF3FPState::Idle: return "Idle";
    case FFF3FPState::Opening: return "Opening";
    case FFF3FPState::Ready: return "Ready";
    case FFF3FPState::Playing: return "Playing";
    case FFF3FPState::Paused: return "Paused";
    case FFF3FPState::Ended: return "Ended";
    case FFF3FPState::Failed: return "Failed";
    case FFF3FPState::Closed: return "Closed";
    default: return "?";
    }
}

const char* ColorModeName(std::uint32_t mode) {
    switch (static_cast<FFF3FPColorMode>(mode)) {
    case FFF3FPColorMode::MapToSdr: return "MapToSdr";
    case FFF3FPColorMode::RawHdrAsSdr: return "RawHdrAsSdr";
    case FFF3FPColorMode::MapToHdr: return "MapToHdr";
    default: return "?";
    }
}

const char* PrimariesName(std::int32_t primaries) {
    switch (primaries) {
    case 1: return "BT.709";
    case 2: return "Unspecified(2)";
    case 4: return "BT.470M";
    case 5: return "BT.470BG";
    case 6: return "SMPTE170M";
    case 7: return "SMPTE240M";
    case 8: return "FILM";
    case 9: return "BT.2020";
    case 10: return "SMPTE428";
    case 11: return "DCI-P3 (SMPTE431)";
    case 12: return "Display P3 (SMPTE432)";
    case 22: return "EBU3213";
    default: return "unknown";
    }
}

const char* TransferName(std::int32_t transfer) {
    switch (transfer) {
    case 1: return "BT.709";
    case 2: return "Unspecified(2)";
    case 4: return "Gamma22";
    case 5: return "Gamma28";
    case 6: return "SMPTE170M";
    case 7: return "SMPTE240M";
    case 8: return "Linear";
    case 9: return "Log";
    case 10: return "LogSqrt";
    case 11: return "IEC61966-2-4";
    case 12: return "BT.1361";
    case 13: return "IEC61966-2-1 (sRGB)";
    case 16: return "PQ (SMPTE2084)";
    case 18: return "HLG (ARIB STD-B67)";
    default: return "unknown";
    }
}

void __cdecl OnEvent(void*, FFF3FPEvent type, const char* detail) {
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
    if (type == FFF3FPEvent::PlaybackEnded) g_playbackEnded = true;
    if (type == FFF3FPEvent::ColorModeChanged) g_colorModeChanged = true;
    const char* name = "?";
    switch (type) {
    case FFF3FPEvent::StateChanged: name = "StateChanged"; break;
    case FFF3FPEvent::OpenCompleted: name = "OpenCompleted"; break;
    case FFF3FPEvent::OperationCompleted: name = "OperationCompleted"; break;
    case FFF3FPEvent::PlaybackEnded: name = "PlaybackEnded"; break;
    case FFF3FPEvent::Error: name = "Error"; break;
    case FFF3FPEvent::ColorModeChanged: name = "ColorModeChanged"; break;
    case FFF3FPEvent::DeviceChanged: name = "DeviceChanged"; break;
    default: break;
    }
    char line[512];
    std::snprintf(line, sizeof(line), "  event=%s %s\n", name, detail != nullptr ? detail : "");
    std::strncat(g_eventLog, line, sizeof(g_eventLog) - std::strlen(g_eventLog) - 1);
}

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM w, LPARAM l) {
    return DefWindowProcW(window, message, w, l);
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

FFF3FPSnapshot ReadSnapshot(FFF3FPHandle player) {
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    const auto result = FFF3FP_GetSnapshot(player, &snapshot);
    if (result != FFFResult::Success) std::printf("  (GetSnapshot failed: %d)\n", static_cast<int>(result));
    return snapshot;
}

void ReportSnapshot(const char* label, const FFF3FPSnapshot& snapshot) {
    std::printf("  [%s] state=%s requested=%s actual=%s isHdrSource=%u\n",
        label, StateName(static_cast<std::uint32_t>(snapshot.state)),
        ColorModeName(static_cast<std::uint32_t>(snapshot.requestedColorMode)),
        ColorModeName(static_cast<std::uint32_t>(snapshot.actualColorMode)),
        snapshot.isHdrSource);
    std::printf("  [%s] %ux%u outBits=%u sourcePeak=%u presented=%llu swapPresents=%llu\n",
        label, snapshot.videoWidth, snapshot.videoHeight, snapshot.videoOutputBitDepth,
        snapshot.sourcePeakNits,
        static_cast<unsigned long long>(snapshot.presentedVideoFrames),
        static_cast<unsigned long long>(snapshot.swapChainPresents));
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: verify_image_mode <media path> [--hdr]\n");
        return 2;
    }
    const char* path = argv[1];
    bool requestHdr = false;
    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--hdr") == 0) requestHdr = true;
    }

    std::printf("=== 3FP image mode verification ===\n");
    std::printf("file: %s\n", path);
    std::printf("PlayerApiVersion: %u\n", FFF3FP_GetApiVersion());

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpVerifyWindow";
    if (RegisterClassW(&windowClass) == 0) {
        std::printf("RegisterClassW failed\n");
        return 1;
    }
    HWND window = CreateWindowExW(0, L"3fpVerifyWindow", L"3fp verify",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, 960, 540, nullptr, nullptr,
        GetModuleHandleW(nullptr), nullptr);
    if (window == nullptr) {
        std::printf("CreateWindowExW failed\n");
        return 1;
    }
    Pump(200);

    FFF3FPHandle player = nullptr;
    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = requestHdr ?
        FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.forceHdrOutput = 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    const auto created = FFF3FP_Create(&configuration, &player);
    std::printf("Create: %d\n", static_cast<int>(created));
    if (created != FFFResult::Success || player == nullptr) return 1;

    const auto opened = FFF3FP_Open(player, path);
    std::printf("Open: %d\n", static_cast<int>(opened));
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    std::printf("OpenCompleted: %s\n", g_openCompleted ? "yes" : "no");
    Pump(300);

    std::printf("--- events ---\n%s", g_eventLog);

    std::printf("--- snapshot after open ---\n");
    auto snapshot = ReadSnapshot(player);
    ReportSnapshot("open", snapshot);

    std::printf("--- image info ---\n");
    FFF3FPImageInfo info{};
    info.size = sizeof(info);
    info.version = 1;
    const auto infoResult = FFF3FP_GetImageInfo(player, &info);
    if (infoResult == FFFResult::Success) {
        std::printf("  frameCount=%d loopCount=%d flags=0x%X\n", info.frameCount, info.loopCount, info.flags);
        std::printf("  primaries=%s(%d) transfer=%s(%d) space=%d\n",
            PrimariesName(info.colorPrimaries), info.colorPrimaries,
            TransferName(info.colorTransfer), info.colorTransfer, info.colorSpace);
        std::printf("  pixelFormat=%d bitDepth=%u icc=%u rotation=%u\n",
            info.sourcePixelFormat, info.sourceBitDepth, info.iccProfileSizeBytes,
            info.rotationQuarterTurns);
        std::printf("  STATIC=%u ANIMATED=%u MULTI_FRAME=%u ALPHA=%u ICC=%u ROTATION=%u WIDE_GAMUT=%u\n",
            (info.flags & FFF3FP_IMAGE_FLAG_STATIC) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_ANIMATED) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_MULTI_FRAME) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_HAS_ALPHA) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_HAS_ICC) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_HAS_ROTATION) != 0,
            (info.flags & FFF3FP_IMAGE_FLAG_WIDE_GAMUT) != 0);
    } else {
        std::printf("  GetImageInfo: %d\n", static_cast<int>(infoResult));
    }

    std::printf("--- request MapToHdr ---\n");
    const auto colorResult = FFF3FP_SetColorMode(player, FFF3FPColorMode::MapToHdr,
        100.0f, 0.0f, 203.0f, 0u);
    std::printf("  SetColorMode: %d\n", static_cast<int>(colorResult));
    Pump(400);
    snapshot = ReadSnapshot(player);
    ReportSnapshot("hdr", snapshot);
    std::printf("  ColorModeChanged event: %s\n", g_colorModeChanged ? "yes" : "no");

    std::printf("--- play / seek (still-image behaviour) ---\n");
    const auto played = FFF3FP_Play(player);
    std::printf("  Play: %d\n", static_cast<int>(played));
    Pump(1200);
    snapshot = ReadSnapshot(player);
    ReportSnapshot("after-play", snapshot);
    std::printf("  PlaybackEnded: %s\n", g_playbackEnded ? "yes" : "no");

    const auto seeked = FFF3FP_Seek(player, 0);
    std::printf("  Seek(0): %d\n", static_cast<int>(seeked));
    Pump(200);
    snapshot = ReadSnapshot(player);
    ReportSnapshot("after-seek", snapshot);

    FFF3FP_Destroy(player);
    std::printf("=== done ===\n");
    return 0;
}
