// 图片浏览的真实模式：**同一个播放器连续打开多张图**。
//
// 这是关键区别：image_load_probe 每张图都 Create/Destroy 播放器，
// 于是每次都要重建 D3D11 设备（~240ms），把"设备创建"算进了每张图的成本。
// 而真实的图片浏览（上一张/下一张）复用同一个会话，设备只建一次。
//
// 本探针量化两种模式的差别，从而判断"启动慢"到底慢在设备还是慢在图片。
//
// 用法：image_reopen_probe <图片...>

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
volatile bool g_openCompleted = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
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
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: image_reopen_probe <图片...>\n"); return 2; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFImageReopenWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFImageReopenWnd", L"reopen",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }

    printf("同一播放器连续打开（真实图片浏览模式）\n\n");
    printf("%-20s %12s\n", "图片", "到Open事件(ms)");

    for (int round = 0; round < 2; ++round) {
        for (int i = 1; i < argc; ++i) {
            const auto path = Utf8(argv[i]);
            g_openCompleted = false;
            FFF3FP_Open(player, path.c_str());
            const auto start = std::chrono::steady_clock::now();
            for (int spin = 0; spin < 30000 && !g_openCompleted; ++spin) Pump(1);
            const auto ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - start).count();
            printf("轮%d %-17s %12.1f\n", round + 1,
                path.substr(path.find_last_of("/\\") + 1).c_str(), ms);
        }
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
