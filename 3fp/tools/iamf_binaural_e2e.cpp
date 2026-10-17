// IAMF 双耳（binaural）渲染端到端验证（2026-10-11）。
//
// 验证要点（内核真实路径，非库级）：
//   1. IAMF 文件被 libiamf 接管（iamfActive=1）；
//   2. 渲染模式按设备声道数自动选择：立体声设备 => iamfBinaural=1 且
//      iamfChannels=2（libiamf HRTF 直接产立体声）；多声道设备 =>
//      iamfBinaural=0 且 iamfChannels=12（扬声器布局，WASAPI 折叠）；
//   3. 音频真的在解码（已解码音频帧数增长）；
//   4. 没有发生 IAMF 回退（回退即说明双耳 configure 失败被兜底，实现 bug）。
//
// 用法：iamf_binaural_e2e <文件.iamf> [--seconds N]

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_openCompleted = false;
bool g_iamfFellBack = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char* detail) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
    if (eventType == FFF3FPEvent::DeviceChanged && detail != nullptr &&
        std::string(detail).find("\"iamf\":true") != std::string::npos)
        g_iamfFellBack = true;
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
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_binaural_e2e <文件.iamf> [--seconds N]\n"); return 2; }
    int seconds = 3;
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds") seconds = _wtoi(argv[i + 1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfBinauralWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFIamfBinauralWnd", L"iamf binaural",
        WS_OVERLAPPEDWINDOW, 40, 40, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    std::printf("内核 API 版本 = %u\n", FFF3FP_GetApiVersion());

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    // Create 对峰值字段是硬校验（<=0 / 非有限即 InvalidArgument），零初始化不够。
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = &EventCallback;
    FFF3FPHandle player = nullptr;
    const auto createResult = FFF3FP_Create(&configuration, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        std::printf("FFF3FP_Create 失败：result=%d player=%p apiVersion=%u\n",
            static_cast<int>(createResult), static_cast<void*>(player),
            FFF3FP_GetApiVersion());
        return 3;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        std::printf("FFF3FP_Open 失败\n");
        return 4;
    }
    // 等 OpenCompleted 事件再读快照：DoOpen 是异步的，读早了 IAMF 解码器还没装。
    for (int i = 0; i < 500 && !g_openCompleted; ++i) Pump(20);
    Pump(300);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) {
        std::printf("GetSnapshot 失败（版本门?）\n");
        return 5;
    }
    std::printf("iamfActive=%u iamfChannels=%u iamfContentChannels=%u iamfSoundSystem=%d "
                "iamfBinaural=%u\n",
        snapshot.iamfActive, snapshot.iamfChannels, snapshot.iamfContentChannels,
        snapshot.iamfSoundSystem, snapshot.iamfBinaural);

    // 设备混音格式声道数：GetAudioPeakLevels 的 channelCount 是 WASAPI 输出侧
    // 的真实声道数（渲染器按 mix format 建 swr），双耳判据的最终来源。
    FFF3FPAudioPeakLevels levels{};
    levels.size = sizeof(levels);
    levels.version = 20;
    std::uint32_t deviceChannels = 0;
    if (FFF3FP_GetAudioPeakLevels(player, &levels) == FFFResult::Success)
        deviceChannels = levels.channelCount;
    std::printf("outputChannels=%u（设备混音格式声道数，双耳判据来源）\n", deviceChannels);

    FFF3FP_Play(player);
    Pump(seconds * 1000);
    FFF3FP_Pause(player);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetSnapshot(player, &after);
    std::printf("已解码音频帧=%llu 已呈现=%llu 欠载=%llu\n",
        (unsigned long long)after.decodedAudioFrames,
        (unsigned long long)after.presentedVideoFrames,
        (unsigned long long)after.audioUnderruns);
    std::printf("IAMF 回退发生：%s\n", g_iamfFellBack ? "是（bug：双耳应兜底在解码器内，不应回退 FFmpeg）" : "否");

    const bool stereoDevice = deviceChannels == 2;
    const bool modeAsExpected = stereoDevice
        ? (snapshot.iamfBinaural == 1 && snapshot.iamfChannels == 2)
        : (snapshot.iamfBinaural == 0 && snapshot.iamfChannels > 2);
    std::printf("\n判词：设备=%s(%uch) => 期望模式=%s，实际 binaural=%u/channels=%u : %s\n",
        stereoDevice ? "立体声" : "多声道", deviceChannels,
        stereoDevice ? "双耳(2ch)" : "扬声器布局(>2ch)",
        snapshot.iamfBinaural, snapshot.iamfChannels,
        modeAsExpected ? "符合" : "不符合");

    FFF3FP_Destroy(player);
    const bool pass = snapshot.iamfActive == 1 && modeAsExpected &&
        after.decodedAudioFrames > 0 && !g_iamfFellBack;
    std::printf("总体：%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 10;
}
