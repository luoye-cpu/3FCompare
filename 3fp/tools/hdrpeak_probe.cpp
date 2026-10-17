// HDR 峰值行为探针：验证上游 SetExtensionProcessing 收紧后，
// 普通 HDR10（非 HDRdp、无外部扩展）的 sourcePeak 是否仍正确取自 MaxCLL。
//
// 背景：上游把 `if (std::isfinite(sourcePeakNits) && sourcePeakNits > 0)` 收紧为
// `if (active && ...)`。这条只在 SetExtensionProcessing 内部，而它只在
// "存在颜色扩展 DLL" 的分支里被调用（见 VideoRenderer.cpp:4723/4728/4756）。
// 因此需要回答：**没有扩展 DLL 时**，HDR 峰值走的是哪条路？
//
// 用法：hdrpeak_probe <HDR视频>
// 判据：GetMediaInfo 的 maxCLL、以及快照的 sourcePeakNits。

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
        Sleep(10);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) { wprintf(L"用法: hdrpeak_probe <HDR视频>\n"); return 2; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFPeakProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFPeakProbeWnd", L"peak probe",
        WS_OVERLAPPEDWINDOW, 30, 30, 800, 450, nullptr, nullptr, wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;   // 走色调映射，最能看出峰值
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(player);
    Pump(1200);

    // 1) 媒体信息里的 maxCLL / 母版亮度 —— 上游未改这里的解析
    static char mediaInfo[65536]{};
    std::uint32_t required = 0;
    if (FFF3FP_GetMediaInfo(player, mediaInfo, sizeof(mediaInfo), &required) == FFFResult::Success) {
        for (const char* key : { "maxCLL", "maxFALL", "maxContentLightLevel",
                                 "masteringDisplay", "maxLuminance" }) {
            const char* at = std::strstr(mediaInfo, key);
            if (at != nullptr) printf("MediaInfo %-22s: %.*s\n", key, 60, at);
        }
    }

    // 2) 快照里的源峰值与 HDR 判定
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(player, &snapshot) == FFFResult::Success) {
        printf("快照: sourcePeakNits=%u  isHdrSource=%u  hdrFormat=%d  hdrPath=%d  "
               "actualColorMode=%d  outBits=%u\n",
            snapshot.sourcePeakNits, snapshot.isHdrSource,
            static_cast<int>(snapshot.hdrFormat), static_cast<int>(snapshot.hdrProcessingPath),
            static_cast<int>(snapshot.actualColorMode), snapshot.videoOutputBitDepth);
    }

    // 3) 颜色变换探针：直接问内核"这个源峰值会怎么映射"
    FFF3FPColorTransform transform{};
    transform.size = sizeof(transform);
    transform.version = 20;
    transform.colorMode = FFF3FPColorMode::MapToSdr;
    transform.transfer = FFF3FPColorTransfer::Pq;
    transform.sdrPeakNits = 100.0f;
    transform.sourcePeakNits = 1242.0f;   // 与失败断言同一量级
    transform.paperWhiteNits = 203.0f;
    transform.inputRed = transform.inputGreen = transform.inputBlue = 0.5f;
    if (FFF3FP_EvaluateColorTransform(&transform) == FFFResult::Success) {
        printf("色彩变换(源峰值1242): out=(%.4f,%.4f,%.4f)\n",
            transform.outputRed, transform.outputGreen, transform.outputBlue);
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
