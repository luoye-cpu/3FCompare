// 3FP 图片模式 / 广色域 —— 实机验证程序
//
// 目的：在真实窗口 + 真实 FFmpeg DLL 下跑内核，检查那些只靠读代码无法确认的行为：
//   · 静态图打开后是否还跳 Ended (K1)、Seek 是否还报错 (K2)
//   · 源是否报告为 HDR、解码帧的 primaries / transfer 到底是什么
//   · 请求 MapToHdr 后，交换链最终用了几位（16 = scRGB 生效，8/10 = 已回落 SDR）
//   · 广色域 SDR 源能不能进 scRGB（门禁是否真的放开）
//
// 用法：
//   verify_image_mode <媒体路径> [--hdr] [--sdr-scrgb]
//     --hdr         初始即请求 MapToHdr（默认 MapToSdr）
//     --sdr-scrgb   打开"SDR 源经 scRGB 呈现"策略（10bit / 广色域 SDR 源走 16bit 链）
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

// FFF3FP_GetImageInfo 是本分支新增的导出，**基线内核（上游 2acd96d）没有**。
// 若静态导入它，exe 在基线上会因"找不到入口点"直接加载失败 ⇒ 无法做 A/B 对照。
// 因此改为运行时解析：同一份 exe 既能跑改动内核，也能跑基线内核。
using GetImageInfoFn = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPImageInfo*);

GetImageInfoFn ResolveGetImageInfo() {
    HMODULE module = GetModuleHandleW(L"FFF.Native.dll");
    if (module == nullptr) module = LoadLibraryW(L"FFF.Native.dll");
    if (module == nullptr) return nullptr;
    return reinterpret_cast<GetImageInfoFn>(
        reinterpret_cast<void*>(GetProcAddress(module, "FFF3FP_GetImageInfo")));
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
    std::printf("  [%s] hdrFormat=%u hdrPath=%u dynamicMeta=%u\n",
        label, static_cast<unsigned>(snapshot.hdrFormat),
        static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: verify_image_mode <media path> [--hdr]\n");
        return 2;
    }
    const char* path = argv[1];
    bool requestHdr = false;
    // --sdr-scrgb: 打开"SDR 源经 scRGB 呈现"策略（AppSettings.SdrScRgbMode=1），
    // 用来验证 10bit/广色域 SDR 源是否真的被判进 16bit scRGB 链。
    bool sdrScRgb = false;
    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--hdr") == 0) requestHdr = true;
        if (std::strcmp(argv[i], "--sdr-scrgb") == 0) sdrScRgb = true;
    }

    std::printf("=== 3FP image mode verification ===\n");
    std::printf("file: %s\n", path);
    std::printf("PlayerApiVersion: %u\n", FFF3FP_GetApiVersion());
    std::printf("options: requestHdr=%d sdrScRgb=%d\n", requestHdr ? 1 : 0, sdrScRgb ? 1 : 0);

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
    configuration.sdrScRgbMode = sdrScRgb ? 1u : 0u;
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
    const auto getImageInfo = ResolveGetImageInfo();
    if (getImageInfo == nullptr) {
        std::printf("  GetImageInfo: 该内核未导出此函数（基线内核的预期结果）\n");
    } else {
    const auto infoResult = getImageInfo(player, &info);
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
    }

    // staticImage 判定：K3 改的就是它。基线按 demuxer 名判定 ⇒ AVIF/HEIC（走 mov）会被判 false。
    // 基线内核也导出 GetMediaInfo，故这一项**两版都可读**，可作 K3 的直接对照。
    std::printf("--- media info: staticImage 判定 ---\n");
    {
        char mediaInfo[65536] = {};
        std::uint32_t requiredSize = 0;
        const auto mediaResult = FFF3FP_GetMediaInfo(player, mediaInfo, sizeof(mediaInfo), &requiredSize);
        if (mediaResult == FFFResult::Success) {
            const char* key = "\"staticImage\"";
            const char* found = std::strstr(mediaInfo, key);
            if (found != nullptr) {
                const char* end = std::strchr(found, ',');
                std::printf("  %.*s\n", end != nullptr ? static_cast<int>(end - found) : 24, found);
            } else {
                std::printf("  staticImage 字段: (未找到)\n");
            }
        } else {
            std::printf("  GetMediaInfo: %d\n", static_cast<int>(mediaResult));
        }
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

    // ⚠ Seek(0) 对静态图**两版都会成功**（av_seek_frame 到起点天然可满足）⇒ 它**不能**证明 K2。
    //    K2 的真实触发条件是非零 Seek：单图解复用器不可 seek，基线会走 av_seek_frame 失败 →
    //    字节回退也失败 → ReportError。故必须补这一条，判据才具特异性。
    const std::size_t eventLogBefore = std::strlen(g_eventLog);
    const auto seekedNonZero = FFF3FP_Seek(player, 50000000);   // 5 秒（100ns 单位）
    std::printf("  Seek(5s): %d\n", static_cast<int>(seekedNonZero));
    Pump(400);
    if (std::strlen(g_eventLog) > eventLogBefore) {
        std::printf("  ↑ 本次 Seek 新产生的事件:\n");
        std::printf("%s", g_eventLog + eventLogBefore);
    } else {
        std::printf("  本次 Seek 未产生新事件\n");
    }
    snapshot = ReadSnapshot(player);
    ReportSnapshot("after-seek-nonzero", snapshot);

    // ════════════════════════════════════════════════════════════════════════
    // 循环播放验证（动图应一直循环，不进入 Ended —— 用户硬要求）
    //   · 重置 ended 标志，从头 Play
    //   · 跨过一遍时长（B 类动画 HEIC 约 1.6s）后采样多帧
    //   · 判据：presented 持续递增 且 全程不出现 PlaybackEnded
    //     —— 修复前：一遍放完内核 SetState(Ended)+Emit(PlaybackEnded)，presented 冻结；
    //        修复后：FlushAtEnd 对 animatedImage_ 走 DoSeek(0) 回绕，持续循环。
    // ════════════════════════════════════════════════════════════════════════
    std::printf("=== loop playback test ===\n");
    g_playbackEnded = false;
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    unsigned long long lp0 = 0, lp1 = 0, lp2 = 0, lp3 = 0;
    Pump(1000); lp0 = ReadSnapshot(player).presentedVideoFrames;
    Pump(1500); lp1 = ReadSnapshot(player).presentedVideoFrames;
    Pump(1500); lp2 = ReadSnapshot(player).presentedVideoFrames;
    Pump(1500); lp3 = ReadSnapshot(player).presentedVideoFrames;
    std::printf("  presented@1.0s=%llu @2.5s=%llu @4.0s=%llu @5.5s=%llu\n",
        static_cast<unsigned long long>(lp0), static_cast<unsigned long long>(lp1),
        static_cast<unsigned long long>(lp2), static_cast<unsigned long long>(lp3));
    std::printf("  PlaybackEnded during loop: %s\n", g_playbackEnded ? "yes (BAD)" : "no (good)");
    const bool loopOk = !g_playbackEnded && (lp3 > lp1) && (lp1 > lp0);
    std::printf("  LOOP RESULT: %s\n", loopOk ? "PASS" : "FAIL");

    FFF3FP_Destroy(player);
    std::printf("=== done ===\n");
    return 0;
}
