// 验证 pan 方向语义：panX = +1 时图片往哪边移动？
//
// 背景：lakeUI 的 PixelPictureBox 用 _scrollX = start - dx，即**图片跟随光标**
// （鼠标右移 -> 图片右移）。3FP 的注释写 pan=+1 把画面推向"as far left/up"，
// 若属实，UI 若把"拖拽右移"映射成 panX=+1，画面就会**反向**，不符合直觉。
//
// 本探针不去猜：改变 panX，测量画面内容的实际位移方向。
//
// 用法：pan_direction_probe <媒体> [--zoom Z]

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

/// 用一行的亮度剖面做模板匹配，求出当前画面相对参考帧的水平位移（像素）。
double EstimateShiftX(const std::vector<float>& reference,
    const std::vector<float>& current, const std::uint32_t width, const std::uint32_t height) {
    if (reference.size() != current.size() || width < 8 || height < 4) return 0.0;
    const auto row = height / 2;
    const auto at = [&](const std::vector<float>& pixels, std::uint32_t x) {
        return pixels[(static_cast<std::size_t>(row) * width + x) * 4];
    };
    // 在有限范围内找使差异最小的偏移
    int bestShift = 0;
    double bestCost = 1e18;
    const int maxShift = static_cast<int>(width / 4);
    for (int shift = -maxShift; shift <= maxShift; ++shift) {
        double cost = 0;
        int compared = 0;
        for (std::uint32_t x = 0; x < width; ++x) {
            const auto other = static_cast<long long>(x) + shift;
            if (other < 0 || other >= static_cast<long long>(width)) continue;
            cost += std::fabs(at(reference, x) - at(current, static_cast<std::uint32_t>(other)));
            ++compared;
        }
        if (compared < 8) continue;
        cost /= compared;
        if (cost < bestCost) { bestCost = cost; bestShift = shift; }
    }
    return static_cast<double>(bestShift);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: pan_direction_probe <媒体> [--zoom Z]\n"); return 2; }
    float zoom = 3.0f;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--zoom" && i + 1 < argc) zoom = static_cast<float>(_wtof(argv[++i]));
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFPanDirectionWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFPanDirectionWnd", L"pan", WS_OVERLAPPEDWINDOW,
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

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
    // ReadVideoPixelRegion reads the back buffer, so the region must fit the swap
    // chain, not the source. Use the window client size.
    RECT client{};
    GetClientRect(window, &client);
    const auto width = static_cast<std::uint32_t>(client.right - client.left);
    const auto height = static_cast<std::uint32_t>(client.bottom - client.top);
    if (width < 16 || height < 8) { printf("窗口过小 %ux%u\n", width, height); FFF3FP_Destroy(player); return 1; }
    printf("源 %ux%u  zoom=%.2f\n\n", width, height, zoom);

    const auto grab = [&](std::vector<float>& out) {
        out.assign(static_cast<std::size_t>(width) * height * 4, 0.0f);
        std::uint32_t bitDepth = 0;
        return FFF3FP_ReadVideoPixelRegion(player, 0, 0, width, height, out.data(),
            static_cast<std::uint32_t>(out.size()), &bitDepth) == FFFResult::Success;
    };

    // 参考帧：pan = 0
    FFF3FP_SetViewTransform(player, zoom, 0.0f, 0.0f);
    FFF3FP_Redraw(player);
    Pump(900);
    std::vector<float> center;
    if (!grab(center)) { printf("回读失败\n"); FFF3FP_Destroy(player); return 1; }

    printf("%10s %14s %14s\n", "panX", "画面位移(px)", "含义");
    for (const float pan : {1.0f, -1.0f, 0.5f, -0.5f}) {
        FFF3FP_SetViewTransform(player, zoom, pan, 0.0f);
        FFF3FP_Redraw(player);
        Pump(900);
        std::vector<float> current;
        if (!grab(current)) { printf("%10.1f %14s\n", pan, "回读失败"); continue; }
        const auto shift = EstimateShiftX(center, current, width, height);
        // 参考帧内容左移 => 画面左移。用"内容在参考中的位置"描述更直观：
        // shift>0 表示当前帧 x 处对应参考帧 x+shift 处 => 内容**左移**
        const char* meaning = shift > 1.0 ? "画面左移" : (shift < -1.0 ? "画面右移" : "基本未动");
        printf("%10.1f %14.1f %14s\n", pan, shift, meaning);
    }

    printf("\n期望（lakeUI 约定：拖拽右移 => 画面右移）: panX=+1 应使画面**右移**\n");
    printf("注：3FP 注释称 pan=+1 把画面推向 left/up。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
