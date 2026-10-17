// 量 FFF3FP_Create 的耗时，分「有窗口」与「无窗口」两种情形。
//
// 背景：审计指出 PlayerSession 构造函数**无条件**调用 videoRenderer_.WarmDevice()，
// 而 WarmDevice 会创建 D3D11 设备。EnsureDevice 只把 window_ 用于**适配器选择**，
// 并不因无窗口而跳过设备创建。
//
// ⇒ 若成立，则**音频-only / 无窗口**会话也要付约 150 ms 的设备创建代价，
//    而这类会话可能永远用不到 D3D 设备 —— 属于把首图优化外溢成通用回退。
//
// 用法：create_cost_probe [--runs N]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
double NowMs() {
    LARGE_INTEGER f{}, c{};
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) * 1000.0 / static_cast<double>(f.QuadPart);
}
// 无窗口创建：outputWindow 传 nullptr。
double MeasureCreate(const HWND window, const int runs, int& failures) {
    double total = 0.0;
    failures = 0;
    for (int i = 0; i < runs; ++i) {
        FFF3FPConfiguration c{};
        c.size = sizeof(c);
        c.version = FFF3FP_GetApiVersion();
        c.outputWindow = window;
        c.decodeMode = FFF3FPDecodeMode::Cpu;
        c.colorMode = FFF3FPColorMode::MapToSdr;
        c.sdrPeakNits = 100.0f;
        c.sdrPaperWhiteNits = 203.0f;
        c.preferredAdapterIndex = -1;
        FFF3FPHandle p = nullptr;
        const auto t0 = NowMs();
        const auto r = FFF3FP_Create(&c, &p);
        const auto t1 = NowMs();
        if (r != FFFResult::Success || p == nullptr) { ++failures; continue; }
        total += (t1 - t0);
        FFF3FP_Destroy(p);
    }
    return failures == runs ? -1.0 : total / (runs - failures);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    int runs = 5;
    for (int i = 1; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--runs" && i + 1 < argc) runs = _wtoi(argv[++i]);

    printf("内核 API 版本: %u\n", FFF3FP_GetApiVersion());

    int fail = 0;
    const auto noWindow = MeasureCreate(nullptr, runs, fail);
    printf("\n无窗口 Create : %s\n", noWindow < 0 ? "**全部失败**" :
        (std::to_string(noWindow) + " ms").c_str());
    if (fail > 0) printf("  （失败 %d 次）\n", fail);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFCreateCostWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFCreateCostWnd", L"cost",
        WS_OVERLAPPEDWINDOW, 20, 20, 640, 480, nullptr, nullptr, wc.hInstance, nullptr);
    const auto withWindow = MeasureCreate(window, runs, fail);
    printf("有窗口 Create : %s\n", withWindow < 0 ? "**全部失败**" :
        (std::to_string(withWindow) + " ms").c_str());
    if (fail > 0) printf("  （失败 %d 次）\n", fail);

    printf("\n判读：\n");
    printf("  若两者都约 150-250 ms => WarmDevice 无条件执行，\n");
    printf("  无窗口（音频-only / headless）会话付了用不到的 GPU 设备代价。\n");
    printf("  若无窗口明显更快 => 该会话类型不受影响。\n");

    if (window != nullptr) DestroyWindow(window);
    return 0;
}
