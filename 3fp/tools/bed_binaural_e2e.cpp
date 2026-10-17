// 通用声道床双耳渲染端到端验证（2026-10-11）。
//
// 验证要点（内核真实路径）：
//   1. 多声道 PCM/WAV 床（HdrDp/DTS/TrueHD 解码后同样是多声道帧，路径一致）
//      在立体声设备上 audioBinaural=1，音频持续解码、输出非静音；
//   2. IAMF 文件上 iamfBinaural=1 而 audioBinaural=0（两条双耳路径互斥，
//      IAMF 帧不再二过 HRTF）；
//   3. 输出峰值（渲染后的 WASAPI 侧电平）非零——证明音频真的出去。
//
// 用法：bed_binaural_e2e <文件> [--seconds N]
// 素材建议（仓外语料库）：
//   ...\atmos2iamf\ffprobe_matrix_out\DTS_芯_7_1.wav    （8ch 7.1）
//   ...\atmos2iamf\ffprobe_matrix_out\Apple_ALAC_6ch.wav （6ch 5.1）
//   ...\atmos2iamf\bed_order_out\live_7_1_4.iamf        （IAMF 对照）

#include <windows.h>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#undef max
#undef min

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_openCompleted = false;
bool g_iamfFellBack = false;
bool g_expectStereo = false;
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
    if (argc < 2) { wprintf(L"用法: bed_binaural_e2e <文件> [--seconds N]\n"); return 2; }
    int seconds = 3;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[i + 1]);
        if (std::wstring(argv[i]) == L"--stereo") g_expectStereo = true;   // 素材本身是立体声，期望直通
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFBedBinWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFBedBinWnd", L"bed binaural",
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
    configuration.sdrPeakNits = 100.0f;      // Create 对峰值是硬校验（<=0 即 InvalidArgument）
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = &EventCallback;
    FFF3FPHandle player = nullptr;
    const auto createResult = FFF3FP_Create(&configuration, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        std::printf("FFF3FP_Create 失败：result=%d\n", static_cast<int>(createResult));
        return 3;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        std::printf("FFF3FP_Open 失败\n");
        return 4;
    }
    for (int i = 0; i < 500 && !g_openCompleted; ++i) Pump(20);
    Pump(300);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) {
        std::printf("GetSnapshot 失败（版本门?）\n");
        return 5;
    }
    std::printf("iamfActive=%u iamfBinaural=%u audioBinaural=%u\n",
        snapshot.iamfActive, snapshot.iamfBinaural, snapshot.audioBinaural);

    // 内核实际看到的音频布局（MediaInfo JSON）：宽表/背表的分辨以它为准。
    {
        std::uint32_t required = 0;
        if (FFF3FP_GetMediaInfo(player, nullptr, 0, &required) == FFFResult::BufferTooSmall &&
            required > 0 && required < 1024 * 1024) {
            std::vector<char> json(required + 1, 0);
            if (FFF3FP_GetMediaInfo(player, json.data(), required, &required) == FFFResult::Success) {
                std::printf("MediaInfo: %.600s\n", json.data());
            }
        }
    }

    FFF3FP_Play(player);
    // 两段式：先泵一半，在播放中段采样电平（缓冲排空时电平表会被清零，
    // 短素材若等到底就只剩 0）；再泵完，取最终快照与模式状态。
    FFF3FPAudioPeakLevels levels{};
    levels.size = sizeof(levels);
    levels.version = 20;
    double outputPeak = 0.0;
    double peakL = 0.0;
    double peakR = 0.0;
    unsigned levelChannels = 0;
    unsigned long long maxDecoded = 0;
    const auto totalPumpMs = std::max(400, seconds * 1000);
    const auto sampleAt = [&](const int ms) {
        Pump(ms);
        FFF3FPSnapshot mid{};
        mid.size = sizeof(mid);
        mid.version = 20;
        if (FFF3FP_GetSnapshot(player, &mid) == FFFResult::Success)
            maxDecoded = std::max(maxDecoded,
                static_cast<unsigned long long>(mid.decodedAudioFrames));
        if (FFF3FP_GetAudioPeakLevels(player, &levels) == FFFResult::Success) {
            levelChannels = levels.channelCount;
            for (unsigned c = 0; c < levels.channelCount && c < 8; ++c)
                outputPeak = std::max(outputPeak, static_cast<double>(levels.values[c]));
            if (levels.channelCount >= 2) {
                peakL = std::max(peakL, static_cast<double>(levels.values[0]));
                peakR = std::max(peakR, static_cast<double>(levels.values[1]));
            }
        }
    };
    // 两点采样（40%/60%）：短素材（如 1.02s 的 IAMF 夹具）在文件播完后电平表
    // 会被清零，必须在中段取到；取 max 兼顾各种时长。
    sampleAt(totalPumpMs * 4 / 10);
    sampleAt(totalPumpMs * 2 / 10);
    Pump(totalPumpMs - totalPumpMs * 6 / 10);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetSnapshot(player, &after);
    std::printf("已解码音频帧=%llu audioPosition=%lldns bufferedAudio=%lldns 欠载=%llu\n",
        (unsigned long long)after.decodedAudioFrames,
        (long long)after.audioPosition100ns,
        (long long)after.bufferedAudio100ns,
        (unsigned long long)after.audioUnderruns);
    std::printf("（播放后）audioBinaural=%u\n", after.audioBinaural);
    std::printf("输出峰值电平=%.4f（播放中段采样，channelCount=%u） peakL=%.4f peakR=%.4f\n",
        outputPeak, levelChannels, peakL, peakR);
    if (peakL > 0 && peakR > 0) {
        // ILD（R 相对 L 的能量差）。P0 黄金值参考（7.1 床，单声道送音）：
        // FL=-11.7dB、SiL=-6.9dB、BL=-12.5dB、FR=+9.4dB。
        const auto ild = 20.0 * std::log10(peakR / peakL);
        std::printf("L=%.4f R=%.4f  ILD=%+.1fdB（FLC 经 0.5/0.5 分入 FL+SiL，期望落在 -6.9~-11.7 之间）\n",
            peakL, peakR, ild);
    }

    FFF3FP_Pause(player);

    const bool isIamf = snapshot.iamfActive == 1;
    const bool playing = std::max(maxDecoded,
        static_cast<unsigned long long>(after.decodedAudioFrames)) > 0;
    // 期望模式：IAMF 文件 => iamf 路径；立体声素材（--stereo）=> 直通；
    // 其余多声道素材 => 通用双耳。三者互斥且都必须可闻。
    bool modeCorrect;
    if (isIamf)
        modeCorrect = snapshot.iamfBinaural == 1 && snapshot.audioBinaural == 0;
    else if (g_expectStereo)
        modeCorrect = after.audioBinaural == 0;      // 立体声素材期望直通
    else
        modeCorrect = after.audioBinaural == 1;      // 通用双耳首帧后才生效，取播放后
    const bool audible = outputPeak > 0.01;
    std::printf("\n判词：%s => 模式=%s 播放=%s 有声=%s : %s\n",
        isIamf ? "IAMF" : (g_expectStereo ? "立体声素材" : "多声道床"),
        modeCorrect ? "正确" : "错误",
        playing ? "是" : "否",
        audible ? "是" : "否",
        (modeCorrect && playing && audible) ? "PASS" : "FAIL");
    FFF3FP_Destroy(player);
    return (modeCorrect && playing && audible) ? 0 : 10;
}
