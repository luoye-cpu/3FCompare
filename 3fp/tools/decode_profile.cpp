// 解码路径分解剖析：把每帧的耗时拆成 解码 / 转换 / 上传 / 合成+Present。
//
// 为什么要拆：端到端 FPS 在多数素材上是**源帧率或刷新率受限**的，看不出内部构成。
// 而"该优化哪里"取决于时间花在哪一段。本探针用内核已有的计时统计
// （softwareConvert / videoUpload / presentWait / deviceLockWait）加上
// **自测的解码吞吐**（无渲染头的纯解码），给出可信的分解。
//
// 两种模式：
//   --render   带窗口，走完整链路，报告各阶段累计耗时
//   --decode   无窗口（headless），只做解码，测纯解码吞吐上限
//
// 用法：decode_profile <媒体> [--decode cpu|gpu] [--seconds N] [--runs R] [--render|--decode-only]

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

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
        Sleep(1);
    }
}

struct Totals {
    double frames = 0;
    double decoded = 0;
    double convertMs = 0;
    double uploadMs = 0;
    double presentMs = 0;
    double lockMs = 0;
    double dropped = 0;
    double wallMs = 0;
    bool ok = false;
};

Totals RunOnce(const std::string& path, const bool gpu, const int seconds, const bool render) {
    Totals t;
    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = nullptr;
    configuration.decodeMode = gpu ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    HWND window = nullptr;
    if (render) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"FFFDecodeProfileWnd";
        static bool registered = false;
        if (!registered) { RegisterClassW(&wc); registered = true; }
        window = CreateWindowExW(0, L"FFFDecodeProfileWnd", L"profile",
            WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
        configuration.outputWindow = window;
    }

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) return t;
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) { FFF3FP_Destroy(player); return t; }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);

    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 20;
    FFF3FP_GetSnapshot(player, &before);

    const auto wallStart = GetTickCount64();
    FFF3FP_Play(player);
    Pump(seconds * 1000);
    t.wallMs = static_cast<double>(GetTickCount64() - wallStart);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetSnapshot(player, &after);

    t.frames = static_cast<double>(after.presentedVideoFrames - before.presentedVideoFrames);
    t.decoded = static_cast<double>(after.decodedVideoFrames - before.decodedVideoFrames);
    t.convertMs = static_cast<double>(after.softwareConvert100ns - before.softwareConvert100ns) / 10000.0;
    t.uploadMs = static_cast<double>(after.videoUpload100ns - before.videoUpload100ns) / 10000.0;
    t.presentMs = static_cast<double>(after.presentWait100ns - before.presentWait100ns) / 10000.0;
    t.lockMs = static_cast<double>(after.deviceLockWait100ns - before.deviceLockWait100ns) / 10000.0;
    t.dropped = static_cast<double>(after.droppedVideoFrames - before.droppedVideoFrames);
    t.ok = t.frames > 0 || t.decoded > 0;

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return t;
}

void Print(const char* label, const Totals& t) {
    if (!t.ok) { printf("%-26s 失败\n", label); return; }
    const auto fps = t.wallMs > 0 ? t.frames / (t.wallMs / 1000.0) : 0;
    printf("%-26s 帧=%-5.0f 解码=%-5.0f  %.1f fps\n", label, t.frames, t.decoded, fps);
    if (t.frames > 0) {
        printf("%-26s   每帧: 转换 %.2f ms  上传 %.2f ms  锁 %.3f ms  Present %.2f ms  丢帧 %.0f\n",
            "", t.convertMs / t.frames, t.uploadMs / t.frames, t.lockMs / t.frames,
            t.presentMs / t.frames, t.dropped);
        const auto accounted = t.convertMs + t.uploadMs + t.lockMs;
        printf("%-26s   可归因 CPU 合计 %.1f ms (%.2f ms/帧)\n", "", accounted, accounted / t.frames);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: decode_profile <媒体> [--decode cpu|gpu] [--seconds N] [--runs R] [--render]\n");
        return 2;
    }
    bool gpu = false, render = false;
    int seconds = 3, runs = 1;
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--decode" && i + 1 < argc) gpu = std::wstring(argv[++i]) == L"gpu";
        else if (key == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (key == L"--runs" && i + 1 < argc) runs = _wtoi(argv[++i]);
        else if (key == L"--render") render = true;
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    printf("媒体: %s\n解码: %s   模式: %s\n\n", path.c_str(), gpu ? "D3D11(硬解)" : "CPU(软解)",
        render ? "带窗口(完整链路)" : "无窗口(仅解码)");
    for (int i = 0; i < runs; ++i) {
        char label[64]{};
        sprintf_s(label, "run %d", i + 1);
        Print(label, RunOnce(path, gpu, seconds, render));
    }
    return 0;
}
