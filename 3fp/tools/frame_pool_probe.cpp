// 帧缓冲池观测：统计池大小、队列深度、以及是否发生逐帧分配。
//
// 关注的三个问题：
//   1. 池是否无界增长（曾经并发的最大帧数决定其大小）
//   2. 稳态下是否仍有 av_frame_alloc（即池是否真的复用了）
//   3. 队列深度与已配置上限的关系
//
// 本探针通过快照的 queuedVideoFrames / decodedVideoFrames 推断队列行为，
// 并长时间运行观察内存是否稳定。
//
// 用法：frame_pool_probe <媒体> [--seconds N] [--decode cpu|gpu] [--interval MS]

#include <windows.h>
#include <psapi.h>

#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

#pragma comment(lib, "psapi.lib")

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
/// 当前进程的私有提交字节数（比工作集更能反映真实分配）
std::uint64_t PrivateBytes() {
    PROCESS_MEMORY_COUNTERS_EX counters{};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)))
        return 0;
    return static_cast<std::uint64_t>(counters.PrivateUsage);
}
std::uint64_t PeakQueue = 0;
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: frame_pool_probe <媒体> [--seconds N] [--decode cpu|gpu] [--interval MS]\n");
        return 2;
    }
    bool gpu = false;
    int seconds = 20, interval = 1000;
    for (int i = 2; i < argc; ++i) {
        const std::wstring k = argv[i];
        if (k == L"--decode" && i + 1 < argc) gpu = std::wstring(argv[++i]) == L"gpu";
        else if (k == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (k == L"--interval" && i + 1 < argc) interval = _wtoi(argv[++i]);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFFramePoolWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFFramePoolWnd", L"pool",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = gpu ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    const auto baselineMemory = PrivateBytes();
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
    before.version = 20;
    FFF3FP_GetSnapshot(player, &before);

    printf("媒体: %s\n解码: %s\n", path.c_str(), gpu ? "硬解" : "软解");
    printf("打开后私有内存: %.1f MB\n\n", PrivateBytes() / 1048576.0);
    printf("%8s %10s %10s %12s %12s %10s\n",
        "秒", "已解码", "已呈现", "队列深度", "私有内存MB", "丢帧");

    FFF3FP_Play(player);
    const auto start = GetTickCount64();
    std::uint64_t maxQueue = 0;
    std::uint64_t memoryAtHalf = 0;
    while (GetTickCount64() - start < static_cast<DWORD>(seconds) * 1000) {
        Pump(interval);
        FFF3FPSnapshot s{};
        s.size = sizeof(s);
        s.version = 20;
        if (FFF3FP_GetSnapshot(player, &s) != FFFResult::Success) continue;
        const auto elapsed = (GetTickCount64() - start) / 1000;
        if (s.queuedVideoFrames > maxQueue) maxQueue = s.queuedVideoFrames;
        if (memoryAtHalf == 0 && elapsed >= static_cast<std::uint64_t>(seconds) / 2)
            memoryAtHalf = PrivateBytes();
        printf("%8llu %10llu %10llu %12u %12.1f %10llu\n",
            static_cast<unsigned long long>(elapsed),
            static_cast<unsigned long long>(s.decodedVideoFrames - before.decodedVideoFrames),
            static_cast<unsigned long long>(s.presentedVideoFrames - before.presentedVideoFrames),
            s.queuedVideoFrames, PrivateBytes() / 1048576.0,
            static_cast<unsigned long long>(s.droppedVideoFrames - before.droppedVideoFrames));
    }
    const auto endMemory = PrivateBytes();
    printf("\n峰值队列深度: %llu\n", static_cast<unsigned long long>(maxQueue));
    printf("基线 %.1f MB -> 中段 %.1f MB -> 结束 %.1f MB\n",
        baselineMemory / 1048576.0, memoryAtHalf / 1048576.0, endMemory / 1048576.0);
    if (memoryAtHalf > 0)
        printf("后半段增长: %+.1f MB（接近 0 说明池已稳定复用）\n",
            (static_cast<double>(endMemory) - static_cast<double>(memoryAtHalf)) / 1048576.0);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
