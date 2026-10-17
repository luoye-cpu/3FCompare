// 视频旋转探测 —— 判断 3FP 是否"看得见"视频的 display matrix
//
// 背景：3FP 目前**只在 GetImageInfo（图片专用）里**读 AV_FRAME_DATA_DISPLAYMATRIX。
// 视频路径完全不读，所以竖拍/旋转过的视频会横躺着播。
//
// 本探针回答两个问题：
//   1) ffmpeg 是否把容器的 rotate 元数据**下推到解码帧**（AV_FRAME_DATA_DISPLAYMATRIX）？
//      —— 若下推，内核可以直接读帧侧数据修复；若不下推（只在 stream/coded_side_data 上），
//         则必须改读 packet/stream 级。
//   2) GetMediaInfo 的 JSON 里有没有可用的旋转信息？
//
// 用法： vprobe <视频路径>
//
// 构建：cl /EHsc /std:c++20 /I <内核根> vprobe.cpp <路径>\FFF.Native.lib
//       并把内核的 FFmpeg DLL 放在 exe 同目录。

#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>

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
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(10);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) { wprintf(L"用法: vprobe <视频路径>\n"); return 2; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFVProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFVProbeWnd", L"vprobe",
        WS_OVERLAPPEDWINDOW, 50, 50, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;   // 软件解码，排开 D3D11 侧干扰
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    printf("OpenCompleted=%s\n", g_openCompleted ? "yes" : "no");
    printf("Play=%d\n", static_cast<int>(FFF3FP_Play(player)));
    Pump(800);

    // 1) GetImageInfo 对视频应当失败（图片专用 API）——确认"没有渠道读视频旋转"
    FFF3FPImageInfo info{};
    info.size = sizeof(info);
    info.version = 20;
    const auto imageResult = FFF3FP_GetImageInfo(player, &info);
    printf("GetImageInfo(视频)=%d %s\n", static_cast<int>(imageResult),
        imageResult == FFFResult::Success ? "(意外：视频也能读)" : "(预期失败=图片专用)");
    if (imageResult == FFFResult::Success) {
        printf("  rotationQuarterTurns=%u flags=0x%X\n",
            info.rotationQuarterTurns, info.flags);
    }

    // 2) 播放中再查一次快照，确认视频尺寸（旋转视频的解码尺寸不会变）
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(player, &snapshot) == FFFResult::Success)
        printf("snapshot: %ux%u scalingMode=%d outBits=%u\n",
            snapshot.videoWidth, snapshot.videoHeight,
            static_cast<int>(snapshot.videoScalingMode), snapshot.videoOutputBitDepth);

    // 3) Read back the applied view rotation. This is the decisive check: the
    //    snapshot reports DECODED dimensions (rotation is a view property and
    //    deliberately does not change them), so only this getter proves the
    //    container's rotation metadata reached the renderer.
    using GetViewRotationFn = FFFResult(__cdecl*)(FFF3FPHandle, std::uint32_t*);
    const auto getViewRotation = reinterpret_cast<GetViewRotationFn>(
        GetProcAddress(GetModuleHandleW(L"FFF.Native.dll"), "FFF3FP_GetViewRotation"));
    if (getViewRotation != nullptr) {
        std::uint32_t turns = 999;
        if (getViewRotation(player, &turns) == FFFResult::Success)
            printf(">>> applied view rotation = %u quarter turns clockwise "
                   "(0=none, 1=90cw, 2=180, 3=270cw)\n", turns);
    } else {
        printf("（内核无 GetViewRotation）\n");
    }

    // 4) MediaInfo JSON 里找旋转线索
    static char mediaInfo[65536]{};
    std::uint32_t required = 0;
    if (FFF3FP_GetMediaInfo(player, mediaInfo, sizeof(mediaInfo), &required) == FFFResult::Success) {
        const char* keys[] = { "rotate", "rotation", "displayMatrix", "sideData", "side_data" };
        bool found = false;
        for (const auto* key : keys) {
            const char* at = std::strstr(mediaInfo, key);
            if (at != nullptr) {
                printf("MediaInfo 含 '%s': %.*s\n", key, 80, at);
                found = true;
            }
        }
        if (!found) printf("MediaInfo 中无任何旋转相关字段\n");
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
