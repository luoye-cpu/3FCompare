// 验证光标锚定缩放：缩放后，光标下的图像点是否保持不动。
//
// 对照 lakeUI PixelPictureBox 的滚轮逻辑：
//   Dim imgPt = 客户坐标转图片像素(e.Location)
//   _zoomFactor *= factor
//   _scrollX = imgPt.X * _zoomFactor - (e.Location.X - bw)   ' 保持 imgPt 不动
// 3FP 的 FFF3FP_ZoomViewAt 应达到同样效果。
//
// 判据：以某一锚点连续放大两次，锚点处的**图像内容**应基本不变。
//
// 用法：zoom_anchor_probe <媒体> [--zoom Z] [--anchor X,Y]

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
std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    return out;
}

/// 取窗口内一个正方形小块的平均色，用于判断锚点内容是否稳定。
bool SamplePatch(FFF3FPHandle player, const std::uint32_t cx, const std::uint32_t cy,
    const std::uint32_t size, float& r, float& g, float& b) {
    std::vector<float> pixels(static_cast<std::size_t>(size) * size * 4, 0.0f);
    std::uint32_t bitDepth = 0;
    if (FFF3FP_ReadVideoPixelRegion(player, cx, cy, size, size, pixels.data(),
            static_cast<std::uint32_t>(pixels.size()), &bitDepth) != FFFResult::Success)
        return false;
    double sr = 0, sg = 0, sb = 0;
    const auto count = static_cast<std::size_t>(size) * size;
    for (std::size_t i = 0; i < count; ++i) {
        sr += pixels[i * 4]; sg += pixels[i * 4 + 1]; sb += pixels[i * 4 + 2];
    }
    r = static_cast<float>(sr / count);
    g = static_cast<float>(sg / count);
    b = static_cast<float>(sb / count);
    return true;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: zoom_anchor_probe <媒体> [--anchor X,Y]\n"); return 2; }
    float anchorX = 0.75f, anchorY = 0.30f;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--anchor" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%f,%f", &anchorX, &anchorY);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFZoomAnchorWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFZoomAnchorWnd", L"zoom", WS_OVERLAPPEDWINDOW,
        20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

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
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(600);

    RECT client{};
    GetClientRect(window, &client);
    const std::uint32_t width = static_cast<std::uint32_t>(client.right - client.left);
    const std::uint32_t height = static_cast<std::uint32_t>(client.bottom - client.top);
    const std::uint32_t patch = 12;
    const auto cx = static_cast<std::uint32_t>(anchorX * width) - patch / 2;
    const auto cy = static_cast<std::uint32_t>(anchorY * height) - patch / 2;
    printf("窗口 %ux%u  锚点 (%.2f, %.2f) => 像素 (%u, %u)\n\n", width, height, anchorX, anchorY, cx, cy);

    // 放大到 3x，再以光标为锚连续步进，观察锚点处内容
    FFF3FP_SetViewTransform(player, 3.0f, 0.0f, 0.0f);
    FFF3FP_Redraw(player);
    Pump(800);

    printf("%6s %10s %10s  %s\n", "步", "锚点R", "锚点G", "说明");
    float r0 = 0, g0 = 0, b0 = 0;
    if (!SamplePatch(player, cx, cy, patch, r0, g0, b0)) { printf("采样失败\n"); FFF3FP_Destroy(player); return 1; }
    printf("%6d %10.4f %10.4f  起始\n", 0, r0, g0);

    for (int step = 1; step <= 3; ++step) {
        float resulting = 0.0f;
        const auto result = FFF3FP_ZoomViewAt(player, 1.25f, anchorX, anchorY, &resulting);
        FFF3FP_Redraw(player);
        Pump(800);
        float r = 0, g = 0, b = 0;
        if (!SamplePatch(player, cx, cy, patch, r, g, b)) { printf("%6d %10s\n", step, "失败"); break; }
        const auto drift = std::fabs(r - r0) + std::fabs(g - g0) + std::fabs(b - b0);
        printf("%6d %10.4f %10.4f  result=%d zoom=%.2f 锚点漂移=%.4f %s\n",
            step, r, g, static_cast<int>(result), resulting, drift,
            drift < 0.08f ? "锚定良好" : "**锚点漂移明显**");
    }

    printf("\n注：漂移应远小于整体画面变化；若接近 0 说明光标下的内容被正确固定。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
