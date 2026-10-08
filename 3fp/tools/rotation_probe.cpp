// 3FP 图片旋转 —— 实机验证程序
//
// 目的：确认那些"只读代码无法确认"的行为：
//   · EXIF Orientation=6 的样张是否被 GetImageInfo 检出（K5）
//   · SetViewRotation 是否真的改变了呈现几何（K6）
//   · 旋转后画幅是否按旋转后的宽高比适配（而不是被压成细条）
//   · 越界入参是否被拒绝、读回是否一致
//   · 关闭旋转（回到 0）是否恢复原始几何
//
// 用法：
//   rotation_probe <图片路径> [--seconds N]
//
// 判据（不依赖肉眼）：
//   用 FFF3FP_GetRenderTargetInfo 读 Dest* 矩形。
//   1920x1080 的样张旋转 90° 后，适配盒的宽高比应从 16:9 变成 9:16，
//   即 Dest 高度应显著增大（在同一个窗口里）。
//
// ⚠ 新 API 一律 GetProcAddress 动态解析，否则旧内核会加载失败。
//
// 构建（VS x64 环境）：
//   cl /EHsc /std:c++20 /I <内核根> rotation_probe.cpp <路径>\FFF.Native.lib user32.lib

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

using SetViewRotationFn = FFFResult(__cdecl*)(FFF3FPHandle, std::uint32_t);
using GetViewRotationFn = FFFResult(__cdecl*)(FFF3FPHandle, std::uint32_t*);
using GetImageInfoFn = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPImageInfo*);

bool g_openCompleted = false;

void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(const int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::milliseconds(milliseconds);
    while (std::chrono::steady_clock::now() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(15);
    }
}

/// Reports the fitted destination rect plus its aspect ratio. The whole point of
/// rotation is visible here: the box must trade width for height.
void ReportGeometry(const FFF3FPHandle player, const char* tag) {
    FFF3FPRenderTargetInfo info{};
    info.size = sizeof(info);
    // v2, not v1: GetRenderTargetInfo versions independently of PlayerApiVersion
    // (v2 made destX/destY signed) and rejects anything else with InvalidArgument.
    info.version = 2;
    const auto result = FFF3FP_GetRenderTargetInfo(player, &info);
    if (result != FFFResult::Success) {
        printf("  [%s] GetRenderTargetInfo=%d\n", tag, static_cast<int>(result));
        return;
    }
    const double aspect = info.destHeight != 0
        ? static_cast<double>(info.destWidth) / static_cast<double>(info.destHeight) : 0.0;
    printf("  [%s] swap=%ux%u dest=(%u,%u) %ux%u  aspect=%.4f outBits=%u hdr=%u\n",
        tag, info.swapWidth, info.swapHeight, info.destX, info.destY,
        info.destWidth, info.destHeight, aspect, info.outputBitDepth, info.hdr);
}

const char* FlagsText(const std::uint32_t flags) {
    static char buffer[128];
    std::snprintf(buffer, sizeof(buffer),
        "STATIC=%u ANIMATED=%u MULTI=%u ALPHA=%u ICC=%u ROTATION=%u WIDEGAMUT=%u",
        (flags & FFF3FP_IMAGE_FLAG_STATIC) != 0, (flags & FFF3FP_IMAGE_FLAG_ANIMATED) != 0,
        (flags & FFF3FP_IMAGE_FLAG_MULTI_FRAME) != 0, (flags & FFF3FP_IMAGE_FLAG_HAS_ALPHA) != 0,
        (flags & FFF3FP_IMAGE_FLAG_HAS_ICC) != 0, (flags & FFF3FP_IMAGE_FLAG_HAS_ROTATION) != 0,
        (flags & FFF3FP_IMAGE_FLAG_WIDE_GAMUT) != 0);
    return buffer;
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) {
        wprintf(L"用法: rotation_probe <图片路径> [--seconds N]\n");
        return 2;
    }
    const std::wstring media = argv[1];
    int seconds = 2;
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds") seconds = _wtoi(argv[i + 1]);

    const HMODULE kernel = GetModuleHandleW(L"FFF.Native.dll");
    if (kernel == nullptr) { printf("找不到 FFF.Native.dll\n"); return 3; }
    const auto setViewRotation = reinterpret_cast<SetViewRotationFn>(
        GetProcAddress(kernel, "FFF3FP_SetViewRotation"));
    const auto getViewRotation = reinterpret_cast<GetViewRotationFn>(
        GetProcAddress(kernel, "FFF3FP_GetViewRotation"));
    const auto getImageInfo = reinterpret_cast<GetImageInfoFn>(
        GetProcAddress(kernel, "FFF3FP_GetImageInfo"));
    printf("内核: PlayerApiVersion=%u  Set/GetViewRotation=%s  GetImageInfo=%s\n",
        FFF3FP_GetApiVersion(), setViewRotation ? "有" : "无（旧内核）",
        getImageInfo ? "有" : "无");
    if (setViewRotation == nullptr || getViewRotation == nullptr) return 4;

    const HINSTANCE instance = GetModuleHandleW(nullptr);
    constexpr wchar_t kClassName[] = L"FFF3FPRotationProbeWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kClassName;
    RegisterClassW(&windowClass);
    const HWND window = CreateWindowExW(0, kClassName, L"3FP rotation probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 80, 80, 1280, 720, nullptr, nullptr,
        instance, nullptr);
    if (window == nullptr) return 5;
    Pump(300);

    const int size = WideCharToMultiByte(CP_UTF8, 0, media.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string mediaUtf8(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1)
        WideCharToMultiByte(CP_UTF8, 0, media.c_str(), -1, mediaUtf8.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n");
        return 6;
    }
    if (FFF3FP_Open(player, mediaUtf8.c_str()) != FFFResult::Success) {
        char error[512]{};
        FFF3FP_GetLastError(player, error, sizeof(error), nullptr);
        printf("Open 失败: %s\n", error);
        FFF3FP_Destroy(player);
        return 7;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    printf("OpenCompleted=%s\n", g_openCompleted ? "yes" : "no");
    // A still image only reaches the swap chain once playback starts (the image
    // mode presents the decode-initial frame), so Play before reading geometry.
    printf("Play result=%d\n", static_cast<int>(FFF3FP_Play(player)));
    Pump(500);

    // --- K5: what does the kernel report about the source? ---
    if (getImageInfo != nullptr) {
        FFF3FPImageInfo info{};
        info.size = sizeof(info);
        info.version = 1;
        if (getImageInfo(player, &info) == FFFResult::Success) {
            printf("GetImageInfo: rotationQuarterTurns=%u (0xFFFFFFFF=unknown)  %s\n",
                info.rotationQuarterTurns, FlagsText(info.flags));
        } else {
            printf("GetImageInfo 调用失败\n");
        }
    }

    printf("--- geometry per rotation (the fit box must trade width for height) ---\n");
    std::uint32_t readBack = 999;
    ReportGeometry(player, "rot=0");
    for (std::uint32_t quarter = 1; quarter <= 3; ++quarter) {
        const auto result = setViewRotation(player, quarter);
        Pump(350);
        char tag[32]{};
        std::snprintf(tag, sizeof(tag), "rot=%u", quarter);
        printf("SetViewRotation(%u) result=%d\n", quarter, static_cast<int>(result));
        ReportGeometry(player, tag);
        if (getViewRotation(player, &readBack) == FFFResult::Success)
            printf("  readback=%u (expect %u)\n", readBack, quarter);
    }

    // --- rotate back to 0: geometry must return to the unrotated aspect ---
    setViewRotation(player, 0);
    Pump(350);
    ReportGeometry(player, "rot=0 again");

    // --- out-of-range must be rejected, and must not disturb the current value ---
    const auto invalid = setViewRotation(player, 4);
    std::uint32_t afterInvalid = 999;
    getViewRotation(player, &afterInvalid);
    printf("SetViewRotation(4) result=%d (expect InvalidArgument=?) readback=%u (expect 0)\n",
        static_cast<int>(invalid), afterInvalid);

    // --- pixel proof: the image must actually turn, not just the fit box ---
    // Sample ONE FIXED screen coordinate across rotations. If the shader really
    // rotates the lookup, the colour at that pixel changes; if only the box moved,
    // the picture would be identical wherever the boxes overlap.
    printf("--- fixed-coordinate pixel readback (proves the lookup rotates) ---\n");
    {
        FFF3FPRenderTargetInfo info{};
        info.size = sizeof(info);
        info.version = 2;
        setViewRotation(player, 0);
        Pump(400);
        if (FFF3FP_GetRenderTargetInfo(player, &info) != FFFResult::Success) {
            printf("  (no swap chain; skipping pixel proof)\n");
        } else {
            const std::uint32_t fixedX = info.destX + info.destWidth / 2;
            const std::uint32_t fixedY = info.destY + info.destHeight / 8;
            for (const std::uint32_t quarter : { 0u, 1u, 2u, 3u }) {
                setViewRotation(player, quarter);
                Pump(400);
                FFF3FPVideoPixelProbe probe{};
                probe.size = sizeof(probe);
                probe.version = 1;
                probe.x = fixedX;
                probe.y = fixedY;
                const auto read = FFF3FP_ReadVideoPixel(player, &probe);
                printf("  fixed(%u,%u) rot=%u result=%d rgb=(%.3f,%.3f,%.3f)\n",
                    fixedX, fixedY, quarter, static_cast<int>(read),
                    probe.red, probe.green, probe.blue);
            }
        }
    }

    (void)seconds;
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
