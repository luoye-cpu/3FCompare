// 验证图片放大的像素块采样：通过视图缩放把图片放大，读回像素判断是否成块。
//
// 关键背景：3FP 对静态图片默认 limitToNativeSize=true => **不放大**（1:1）。
// 因此"图片放大"只在**用户缩放**（viewZoom > 1）时发生。
// 本探针用 FFF3FP_SetViewZoom 放大图片，再比较相邻输出像素是否成块。
//
// 判据：像素块放大下，同一源像素覆盖的输出像素**完全相同**（比例高）；
//       Lanczos 插值下相邻像素几乎总不同（比例接近 0）。
//
// 用法：image_zoom_probe <媒体> [--zoom Z]

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
    if (argc < 2) { wprintf(L"用法: image_zoom_probe <媒体> [--zoom Z]\n"); return 2; }
    float zoom = 4.0f;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--zoom" && i + 1 < argc) zoom = static_cast<float>(_wtof(argv[++i]));
    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFImageZoomWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFImageZoomWnd", L"zoom", WS_OVERLAPPEDWINDOW,
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

    FFF3FP_Play(player);
    Pump(800);
    const auto zoomResult = FFF3FP_SetViewTransform(player, zoom, 0.0f, 0.0f);
    printf("SetViewTransform(%.2f) -> %d\n", zoom, static_cast<int>(zoomResult));
    FFF3FP_Redraw(player);
    Pump(1200);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
    printf("源 %ux%u\n", snapshot.videoWidth, snapshot.videoHeight);

    // 扫描非黑区域
    std::uint32_t maxX = 0, minX = 0xFFFFFFFFu, maxY = 0, minY = 0xFFFFFFFFu;
    for (std::uint32_t yy = 0; yy < snapshot.videoHeight; yy += 3) {
        for (std::uint32_t xx = 0; xx < snapshot.videoWidth; xx += 3) {
            FFF3FPVideoPixelProbe point{};
            point.size = sizeof(point);
            point.version = 20;
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
    printf("非黑区域: x %u..%u  y %u..%u\n", minX, maxX, minY, maxY);
    if (minX > maxX || minY > maxY) { printf("未找到内容\n"); }
    else {
        const auto y = (minY + maxY) / 2;
        const auto rowWidth = (std::min)(maxX - minX + 1, 400u);
        std::vector<float> pixels(static_cast<std::size_t>(rowWidth) * 4, 0.0f);
        std::uint32_t bitDepth = 0;
        if (FFF3FP_ReadVideoPixelRegion(player, minX, y, rowWidth, 1, pixels.data(),
                static_cast<std::uint32_t>(pixels.size()), &bitDepth) == FFFResult::Success) {
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
            printf("相邻像素相同比例 = %.1f%%  （块状应偏高，插值接近 0）\n",
                compared > 0 ? 100.0 * identical / compared : 0.0);
        }
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
