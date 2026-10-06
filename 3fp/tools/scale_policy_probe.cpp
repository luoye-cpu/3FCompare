// 验证图片模式的缩放策略：确认"放大走像素块、缩小走平滑核"确实生效。
//
// 背景：3FP 对静态图片默认 limitToNativeSize=true，即**小图不放大**（保持 1:1）。
// 用户可通过 FFF3FP_SetFitLimitToNative(false) 允许放大。
// 本探针分别测两种设置，并读取内核选核诊断（stderr 的 [scale] 行）。
//
// 用法：scale_policy_probe <媒体> [--seconds N]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

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
    if (argc < 2) { wprintf(L"用法: scale_policy_probe <媒体> [--seconds N]\n"); return 2; }
    int seconds = 2;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFScalePolicyWnd";
    RegisterClassW(&wc);
    // 出一个较大窗口，让小图有明显放大倍率
    const auto window = CreateWindowExW(0, L"FFFScalePolicyWnd", L"scale", WS_OVERLAPPEDWINDOW,
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

    // 两次测量：默认（限制原生尺寸）与允许放大
    for (int pass = 0; pass < 2; ++pass) {
        const bool limitToNative = pass == 0;
        FFF3FP_SetFitLimitToNative(player, limitToNative ? 1 : 0);
        printf("\n--- %s ---\n", limitToNative ? "默认：限制原生尺寸（小图不放大）"
                                               : "允许放大（SetFitLimitToNative(false)）");
        FFF3FP_Play(player);
        Pump(seconds * 1000);
        FFF3FPSnapshot snapshot{};
        snapshot.size = sizeof(snapshot);
        snapshot.version = 8;
        FFF3FP_GetSnapshot(player, &snapshot);
        printf("  源 %ux%u  呈现帧 %llu\n",
            snapshot.videoWidth, snapshot.videoHeight,
            static_cast<unsigned long long>(snapshot.presentedVideoFrames));
        FFF3FP_Redraw(player);
        Pump(900);
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
