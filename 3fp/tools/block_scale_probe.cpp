// 验证"图片放大走像素块"是否真的生效。
//
// 判据：像素块放大（nearest）下，输出中相邻像素会**成块完全相同**；
// 而平滑插值（Lanczos）下相邻像素**几乎总是不同**。
// 本探针读回一行像素，统计"相邻相同的比例"，块状应显著高于插值。
//
// 用法：block_scale_probe <媒体> [--width W] [--height H]

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(1);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: block_scale_probe <媒体> [--limitnative 0|1]\n"); return 2; }
    int limitNative = 1;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--limitnative" && i + 1 < argc) limitNative = _wtoi(argv[++i]);
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFBlockScaleWnd";
    RegisterClassW(&wc);
    // 大窗口 -> 小图有明显放大倍率
    const auto window = CreateWindowExW(0, L"FFFBlockScaleWnd", L"block", WS_OVERLAPPEDWINDOW,
        20, 20, 1600, 900, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FP_SetFitLimitToNative(player, limitNative ? 1 : 0);
    FFF3FP_Play(player);
    Pump(1500);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    FFF3FP_GetSnapshot(player, &snapshot);
    printf("源 %ux%u  limitToNative=%d\n", snapshot.videoWidth, snapshot.videoHeight, limitNative);

    // 读回中间一行，统计相邻像素"几乎相同"的比例
    const auto rowWidth = 32u;
    std::vector<float> pixels(static_cast<std::size_t>(rowWidth) * 4);
    std::uint32_t bitDepth = 0;
    // Scan for the non-black region first. Reading at assumed coordinates hit the
    // letterbox and produced a false "all zero, 100% identical" reading.
    std::uint32_t maxX = 0, minX = 0xFFFFFFFFu, maxY = 0, minY = 0xFFFFFFFFu;
    for (std::uint32_t yy = 0; yy < snapshot.videoHeight; yy += 4) {
        for (std::uint32_t xx = 0; xx < snapshot.videoWidth; xx += 4) {
            FFF3FPVideoPixelProbe point{};
            point.size = sizeof(point);
            point.version = 1;
            point.x = xx;
            point.y = yy;
            if (FFF3FP_ReadVideoPixel(player, &point) != FFFResult::Success) continue;
            if (point.red + point.green + point.blue > 0.02f) {
                if (xx < minX) minX = xx;
                if (xx > maxX) maxX = xx;
                if (yy < minY) minY = yy;
                if (yy > maxY) maxY = yy;
            }
        }
    }
    printf("  非黑区域: x %u..%u  y %u..%u\n", minX, maxX, minY, maxY);
    if (minX > maxX || minY > maxY) {
        printf("  未找到内容\n");
    } else {
        const auto y = (minY + maxY) / 2;
        const auto rowWidth = maxX - minX + 1;
        pixels.assign(static_cast<std::size_t>(rowWidth) * 4, 0.0f);
        const auto result = FFF3FP_ReadVideoPixelRegion(player, minX, y, rowWidth, 1,
            pixels.data(), static_cast<std::uint32_t>(pixels.size()), &bitDepth);
        if (result != FFFResult::Success) {
            printf("读回失败 (%d)\n", static_cast<int>(result));
        } else {
            std::size_t identical = 0, compared = 0;
            for (std::uint32_t x = 1; x < rowWidth; ++x) {
                const auto i0 = static_cast<std::size_t>(x - 1) * 4;
                const auto i1 = static_cast<std::size_t>(x) * 4;
                const auto diff = std::fabs(pixels[i0] - pixels[i1]) +
                    std::fabs(pixels[i0 + 1] - pixels[i1 + 1]) +
                    std::fabs(pixels[i0 + 2] - pixels[i1 + 2]);
                ++compared;
                if (diff < 0.0005f) ++identical;
            }
            const auto ratio = compared > 0 ? 100.0 * identical / compared : 0.0;
            printf("  行 y=%u 宽=%u  相邻像素相同比例 = %.1f%%\n", y, rowWidth, ratio);
            printf("  （块状放大应显著偏高；Lanczos 插值应接近 0）\n");
        }
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
