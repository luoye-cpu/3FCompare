// 直接测试 IamfAudioDecoder::PumpFile：确认它不会挂死、且能稳定产出帧。
//
// 背景：接进 PlayerSession 后表现为「PumpPlayback 只进一次就卡住」，
// 需要把嫌疑范围从"播放循环"缩小到"PumpFile 本身是否收敛"。
// 这个探针不碰播放链路，只反复调 PumpFile 直到 atEnd，并打印每轮产出。
//
// 用法：iamf_pump_probe <文件.iamf> [--sound-system N] [--rounds N]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Audio/IamfAudioDecoder.h"

extern "C" {
#include <libavutil/frame.h>
}

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_pump_probe <文件.iamf> [--sound-system N] [--rounds N]\n"); return 2; }
    int soundSystem = -1;
    int maxRounds = 2000;
    for (int i = 2; i + 1 < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--sound-system") soundSystem = _wtoi(argv[i + 1]);
        else if (key == L"--rounds") maxRounds = _wtoi(argv[i + 1]);
    }

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    IamfAudioDecoder decoder;
    std::string error;
    if (!decoder.OpenFile(path, error)) {
        printf("OpenFile 失败: %s\n", error.c_str());
        return 4;
    }
    printf("OpenFile OK: %d 声道, %d Hz, soundSystem=%d\n",
        decoder.Channels(), decoder.SampleRate(), decoder.SoundSystem());

    long long totalFrames = 0;
    long long totalSamples = 0;
    int rounds = 0;
    bool atEnd = false;
    while (!atEnd && rounds < maxRounds) {
        std::vector<std::pair<AVFrame*, std::int64_t>> frames;
        std::string pumpError;
        const auto started = GetTickCount64();
        if (!decoder.PumpFile(16, frames, atEnd, pumpError)) {
            printf("第 %d 轮 PumpFile 失败: %s\n", rounds, pumpError.c_str());
            for (auto& e : frames) av_frame_free(&e.first);
            return 5;
        }
        const auto elapsed = GetTickCount64() - started;
        for (auto& e : frames) {
            ++totalFrames;
            totalSamples += e.first->nb_samples;
            av_frame_free(&e.first);
        }
        ++rounds;
        // 每轮都打印，便于看出是否"零产出空转"（那正是怀疑的挂死形态）
        if (rounds <= 5 || frames.empty() || elapsed > 200)
            printf("  轮 %-4d 产出 %-3zu 帧  atEnd=%d  耗时 %llu ms\n",
                rounds, frames.size(), atEnd ? 1 : 0,
                static_cast<unsigned long long>(elapsed));
        if (frames.empty() && !atEnd) {
            printf("⚠ 空产出且未到末尾 —— 这就是不死不活的空转形态\n");
            break;
        }
    }

    printf("\n结果: %d 轮, %lld 帧, %lld 采样, atEnd=%d\n",
        rounds, totalFrames, totalSamples, atEnd ? 1 : 0);
    const bool ok = totalFrames > 0 && atEnd;
    printf("判定: %s\n", ok ? "PASS（PumpFile 收敛且有产出）" : "FAIL（未收敛或无产出）");
    return ok ? 0 : 1;
}
