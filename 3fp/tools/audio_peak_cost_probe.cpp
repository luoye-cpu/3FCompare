// 音频管线热点测量：UpdateInputAudioPeakLevels 的逐样本扫描成本。
//
// 背景：该函数在 QueueAudioFrame 里**无条件**执行，逐声道逐样本扫描以提高峰值电平，
// 但对外只暴露 8 个声道（FFF3FPAudioPeakLevels::inputValues[8]）。
// IAMF 7.1.4 是 12 声道 => 有 4 个声道的扫描结果被丢弃。
//
// 本探针直接复现同样的扫描逻辑并计时，给出"每帧/每秒"的真实成本量级，
// 而不是靠推断。
//
// 用法：audio_peak_cost_probe [--seconds N]

#include <windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <vector>

namespace {
constexpr std::uint32_t MaximumChannels = 8;

// 与 PlayerSession::UpdateInputAudioPeakLevels 相同的逻辑
void ScanPeaks(const std::int16_t* data, const int channels, const int samples,
    std::array<float, MaximumChannels>& peaks) {
    for (std::uint32_t channel = 0; channel < static_cast<std::uint32_t>(channels); ++channel) {
        for (int sample = 0; sample < samples; ++sample) {
            const auto index = sample * channels + channel;
            auto value = data[index] / 32768.0f;
            if (std::isfinite(value)) peaks[channel] = (std::max)(peaks[channel], std::abs(value));
        }
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    int seconds = 5;
    for (int i = 1; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);

    printf("音频管线热点：逐样本峰值扫描成本\n");
    printf("（复现 QueueAudioFrame -> UpdateInputAudioPeakLevels 的循环）\n\n");

    constexpr int SampleRates[] = {48000};
    constexpr int FrameSizes[] = {1024, 4096};

    for (const int rate : SampleRates) {
        for (const int frame : FrameSizes) {
            for (const int channels : {2, 6, 8, 12}) {
                std::vector<std::int16_t> pcm(static_cast<std::size_t>(frame) * channels);
                for (std::size_t i = 0; i < pcm.size(); ++i)
                    pcm[i] = static_cast<std::int16_t>((i * 37) % 65536 - 32768);

                const auto packetsPerSecond = rate / frame;
                // Amplify so the measured span is well above timer resolution.
                const auto iterations = (std::max)(1, packetsPerSecond * seconds) * 200;

                std::array<float, MaximumChannels> peaks{};
                // 预热
                for (int i = 0; i < 100; ++i) ScanPeaks(pcm.data(), channels, frame, peaks);

                const auto start = std::chrono::steady_clock::now();
                for (int i = 0; i < iterations; ++i) {
                    peaks.fill(0.0f);
                    ScanPeaks(pcm.data(), channels, frame, peaks);
                }
                const auto end = std::chrono::steady_clock::now();
                const auto totalUs = std::chrono::duration_cast<std::chrono::microseconds>(end - start).count();
                const auto perPacketUs = static_cast<double>(totalUs) / iterations;
                const auto perSecondMs = static_cast<double>(totalUs) / 1000.0 / seconds;
                const auto scanned = static_cast<double>(frame) * channels / 1e6;

                printf("%2d ch / %4d 帧 / %d Hz: %6.2f µs/包  %6.3f ms/秒  (%.2f M 样本/秒)\n",
                    channels, frame, rate, perPacketUs, perSecondMs, scanned * packetsPerSecond);
            }
        }
    }

    printf("\n注：这是**纯扫描**成本，不含 swr/拷贝。用于判断是否值得优化。\n");
    return 0;
}
