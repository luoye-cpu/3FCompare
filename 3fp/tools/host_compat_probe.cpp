// 宿主兼容性核查：宿主当前写死 API 16、配置结构体止于 sdrScRgbMode（84 字节），
// 而内核已是 API 18 且结构体多了 10 个 uint32（10 个自适应/线程字段）。
//
// 本探针直接复现"老宿主配置"调用 FFF3FP_Create，确认是否被拒 —— 不靠推断。
//
// 用法：host_compat_probe

#include <windows.h>

#include <cstdint>
#include <cstdio>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
/// 宿主（FFF.Player 的 原生播放器配置）目前声明的字段，止于 sdrScRgbMode。
struct LegacyHostConfiguration {
    std::uint32_t size;
    std::uint32_t version;
    void* outputWindow;
    FFF3FPDecodeMode decodeMode;
    FFF3FPColorMode colorMode;
    float sdrPeakNits;
    float hdrPeakNits;
    float sdrPaperWhiteNits;
    const char* audioEndpointUtf8;
    FFF3FPEventCallback eventCallback;
    void* eventCallbackContext;
    FFF3FPVideoScalingQuality videoScalingQuality;
    std::uint32_t forceHdrOutput;
    std::int32_t preferredAdapterIndex;
    std::uint32_t sdrScRgbMode;
    // API 17/18 追加的 7 个字段（宿主修复后也必须有）
    std::uint32_t adaptiveDownscaleBeforeUpload;
    std::uint32_t adaptiveDownscaleDropPercent;
    std::uint32_t adaptiveDecoderThreads;
    std::uint32_t minSoftwareDecoderThreads;
    std::uint32_t maxSoftwareDecoderThreads;
    std::uint32_t decoderGrowDropPercent;
    std::uint32_t decoderShrinkDropPercent;
};
// 修复前的结构体（止于 sdrScRgbMode），用于对照
struct PinnedHostConfiguration {
    std::uint32_t size;
    std::uint32_t version;
    void* outputWindow;
    FFF3FPDecodeMode decodeMode;
    FFF3FPColorMode colorMode;
    float sdrPeakNits;
    float hdrPeakNits;
    float sdrPaperWhiteNits;
    const char* audioEndpointUtf8;
    FFF3FPEventCallback eventCallback;
    void* eventCallbackContext;
    FFF3FPVideoScalingQuality videoScalingQuality;
    std::uint32_t forceHdrOutput;
    std::int32_t preferredAdapterIndex;
    std::uint32_t sdrScRgbMode;
};

void EventCallback(void*, FFF3FPEvent, const char*) {}
} // namespace

int wmain() {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    printf("内核 API 版本 = %u\n", FFF3FP_GetApiVersion());
    printf("内核 sizeof(FFF3FPConfiguration) = %zu 字节\n", sizeof(FFF3FPConfiguration));
    printf("修复后宿主结构体 = %zu 字节\n", sizeof(LegacyHostConfiguration));
    printf("修复前宿主结构体 = %zu 字节\n\n", sizeof(PinnedHostConfiguration));

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFHostCompatWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFHostCompatWnd", L"compat",
        WS_OVERLAPPEDWINDOW, 20, 20, 320, 240, nullptr, nullptr, wc.hInstance, nullptr);

    // ① 完全复现宿主当前的调用方式（版本 16、结构体 84 字节）
    LegacyHostConfiguration legacy{};
    legacy.size = sizeof(legacy);
    legacy.version = 16;
    legacy.outputWindow = window;
    legacy.decodeMode = FFF3FPDecodeMode::D3D11;
    legacy.colorMode = FFF3FPColorMode::MapToSdr;
    legacy.sdrPeakNits = 100.0f;
    legacy.sdrPaperWhiteNits = 203.0f;
    legacy.eventCallback = EventCallback;
    legacy.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    const auto legacyResult = FFF3FP_Create(reinterpret_cast<const FFF3FPConfiguration*>(&legacy), &player);
    printf("① 老宿主方式（version=16, size=%zu）: result=%d %s\n",
        sizeof(legacy), static_cast<int>(legacyResult),
        legacyResult == FFFResult::Success ? "（可用）" : "**被拒**");
    if (player != nullptr) { FFF3FP_Destroy(player); player = nullptr; }

    // ② 只改版本号为 18，结构体仍短
    legacy.version = FFF3FP_GetApiVersion();
    const auto versionOnly = FFF3FP_Create(reinterpret_cast<const FFF3FPConfiguration*>(&legacy), &player);
    printf("② 仅版本改为 %u（size 仍 %zu）: result=%d %s\n",
        FFF3FP_GetApiVersion(), sizeof(legacy), static_cast<int>(versionOnly),
        versionOnly == FFFResult::Success ? "（可用）" : "**被拒（size 不足）**");
    if (player != nullptr) { FFF3FP_Destroy(player); player = nullptr; }

    // ③ 完整配置（新宿主应有）
    FFF3FPConfiguration full{};
    full.size = sizeof(full);
    full.version = FFF3FP_GetApiVersion();
    full.outputWindow = window;
    full.decodeMode = FFF3FPDecodeMode::D3D11;
    full.colorMode = FFF3FPColorMode::MapToSdr;
    full.sdrPeakNits = 100.0f;
    full.sdrPaperWhiteNits = 203.0f;
    full.eventCallback = EventCallback;
    full.preferredAdapterIndex = -1;
    const auto fullResult = FFF3FP_Create(&full, &player);
    printf("③ 完整配置（size=%zu）: result=%d %s\n",
        sizeof(full), static_cast<int>(fullResult),
        fullResult == FFFResult::Success ? "（可用）" : "**被拒**");
    if (player != nullptr) { FFF3FP_Destroy(player); }

    printf("\n结论：%s\n",
        legacyResult != FFFResult::Success
            ? "**宿主当前实现无法创建会话，必须补齐结构体字段与版本号**"
            : "宿主可创建会话");

    if (window != nullptr) DestroyWindow(window);
    return 0;
}
