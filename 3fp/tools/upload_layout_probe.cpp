// 测量视频上传路径的布局分布与耗时：判断 PR #15 的 WRITE_DISCARD 优化命中率。
//
// 背景：PR #15 只对 inputLayout==1（planar YUV420P）启用 DYNAMIC + WRITE_DISCARD
// 上传；layout 2（semi-planar NV12，硬件解码器与多数软件解码输出）仍走
// UpdateSubresource。若实际内容以 layout 2 为主，则该优化命中率低。
//
// 做法：解码若干真实素材，读快照里的渲染统计（上传次数/耗时/布局），
// 输出每种 layout 的占比。**只读，不改内核行为。**
//
// 用法：upload_layout_probe <媒体> [--seconds N]

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
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
        Sleep(5);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: upload_layout_probe <媒体> [--seconds N]\n"); return 2; }
    int seconds = 4;
    bool hw = false;
    for (int i = 2; i + 1 < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--seconds") seconds = _wtoi(argv[i + 1]);
        else if (key == L"--hw") hw = true;
    }
    for (int i = 2; i < argc; ++i) if (std::wstring(argv[i]) == L"--hw") hw = true;

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFUploadProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFUploadProbeWnd", L"upload probe",
        WS_OVERLAPPEDWINDOW, 30, 30, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    // 软解得到 planar/system 内存帧；硬解得到 semi-planar 共享纹理 —— 两条路布局不同。
    configuration.decodeMode = hw ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); DestroyWindow(window); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);

    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 8;
    FFF3FP_GetSnapshot(player, &before);

    FFF3FP_Play(player);
    Pump(seconds * 1000);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 8;
    FFF3FP_GetSnapshot(player, &after);

    printf("媒体: %s\n", path.c_str());
    printf("解码模式: %s\n", hw ? "D3D11(硬解)" : "CPU(软解)");
    printf("源: %ux%u  呈现帧: %llu -> %llu\n",
        after.videoWidth, after.videoHeight,
        static_cast<unsigned long long>(before.presentedVideoFrames),
        static_cast<unsigned long long>(after.presentedVideoFrames));

    // 软件转换耗时：CPU 上传路径的直接成本指标
    const auto swDelta = after.softwareConvert100ns - before.softwareConvert100ns;
    const auto presentDelta = after.presentWait100ns - before.presentWait100ns;
    const auto lockDelta = after.deviceLockWait100ns - before.deviceLockWait100ns;
    printf("软件转换累计: %.2f ms -> %.2f ms (增量 %.2f ms)\n",
        before.softwareConvert100ns / 10000.0, after.softwareConvert100ns / 10000.0, swDelta / 10000.0);
    printf("Present 等待:  %.2f ms -> %.2f ms (增量 %.2f ms)\n",
        before.presentWait100ns / 10000.0, after.presentWait100ns / 10000.0, presentDelta / 10000.0);
    printf("设备锁等待:    %.2f ms -> %.2f ms (增量 %.2f ms)\n",
        before.deviceLockWait100ns / 10000.0, after.deviceLockWait100ns / 10000.0, lockDelta / 10000.0);
    printf("合并丢帧=%llu  呈现次数=%llu\n",
        static_cast<unsigned long long>(after.coalescedVideoFrames),
        static_cast<unsigned long long>(after.swapChainPresents));

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
