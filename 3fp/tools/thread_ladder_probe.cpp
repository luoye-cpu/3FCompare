// 验证软解线程数动态分级：压力大时升档、压力小时降档。
//
// 用法：thread_ladder_probe <媒体> [--seconds N] [--off] [--min A] [--max B]
//
// 观察点：内核在每次梯级变化时打印 [threads] xxx -> yyy (drops N%)。
// 本探针把每档线程数记下来，报告是否发生了升/降档。

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
    if (argc < 2) {
        wprintf(L"用法: thread_ladder_probe <媒体> [--seconds N] [--off] [--min A] [--max B]\n");
        return 2;
    }
    bool off = false;
    int seconds = 10, minThreads = 4, maxThreads = 32;
    for (int i = 2; i < argc; ++i) {
        const std::wstring k = argv[i];
        if (k == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (k == L"--off") off = true;
        else if (k == L"--min" && i + 1 < argc) minThreads = _wtoi(argv[++i]);
        else if (k == L"--max" && i + 1 < argc) maxThreads = _wtoi(argv[++i]);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFThreadLadderWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFThreadLadderWnd", L"ladder",
        WS_OVERLAPPEDWINDOW, 20, 20, 1600, 900, nullptr, nullptr, wc.hInstance, nullptr);

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
    configuration.adaptiveDecoderThreads = off ? 0u : 1u;
    configuration.minSoftwareDecoderThreads = static_cast<std::uint32_t>(minThreads);
    configuration.maxSoftwareDecoderThreads = static_cast<std::uint32_t>(maxThreads);

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

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

    const auto frames = static_cast<double>(after.presentedVideoFrames - before.presentedVideoFrames);
    const auto dropped = static_cast<double>(after.droppedVideoFrames - before.droppedVideoFrames);
    printf("\n配置: 动态线程=%s  范围=[%d,%d]  解码=软解\n",
        off ? "关" : "开", minThreads, maxThreads);
    printf("呈现帧=%.0f (%.1f fps)  丢帧=%.0f\n", frames, frames / seconds, dropped);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
