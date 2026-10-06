// 验证：把 libiamf 的 12/24 声道交错 PCM 包成 AVFrame 后，
// 3FP 现有的 swr 下混路径能否正常把它变成设备声道数。
//
// 这一步是阶段二的关键假设：libiamf 出多声道 → 包 AVFrame → swr 下混到设备。
// 若 swr 对 >8 声道（尤其 24 声道）布局不支持，整条链路就不成立。
//
// 做法：完全照 IamfAudioDecoder::Deliver 的方式建帧（av_channel_layout_default
// + AV_SAMPLE_FMT_S16 packed），然后用与 WasapiRenderer::EnsureResampler 相同的
// 参数建 swr，做一次真实转换并检查输出非零。
//
// 用法：swr_multichannel_probe <文件.iamf> [--sound-system N] [--device-channels 2]

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

extern "C" {
#include <libavutil/channel_layout.h>
#include <libavutil/frame.h>
#include <libavutil/opt.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
}

#include "IAMF_decoder.h"

namespace {
bool ReadAll(const std::wstring& path, std::vector<std::uint8_t>& out) {
    const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0) { CloseHandle(file); return false; }
    out.resize(static_cast<std::size_t>(size.QuadPart));
    DWORD read = 0;
    const bool ok = ReadFile(file, out.data(), static_cast<DWORD>(out.size()), &read, nullptr) &&
        read == static_cast<DWORD>(out.size());
    CloseHandle(file);
    return ok;
}

int ChannelsForSoundSystem(const int ss) {
    switch (ss) {
        case 0: return 2;
        case 1: return 6;
        case 2: return 8;
        case 3: return 10;
        case 4: return 11;
        case 5: return 12;
        case 6: return 14;
        case 7: return 24;
        case 8: return 8;
        case 9: return 12;
        case 10: return 10;
        case 11: return 6;
        case 12: return 1;
        case 13: return 16;
        case 14: return 16;
        default: return 0;
    }
}

IAMF_SoundSystem SoundSystemEnum(const int ss) {
    switch (ss) {
        case 0: return SOUND_SYSTEM_A;
        case 1: return SOUND_SYSTEM_B;
        case 2: return SOUND_SYSTEM_C;
        case 3: return SOUND_SYSTEM_D;
        case 4: return SOUND_SYSTEM_E;
        case 5: return SOUND_SYSTEM_F;
        case 6: return SOUND_SYSTEM_G;
        case 7: return SOUND_SYSTEM_H;
        case 8: return SOUND_SYSTEM_I;
        case 9: return SOUND_SYSTEM_J;
        case 10: return SOUND_SYSTEM_EXT_712;
        case 11: return SOUND_SYSTEM_EXT_312;
        case 12: return SOUND_SYSTEM_MONO;
        case 13: return SOUND_SYSTEM_EXT_916;
        case 14: return SOUND_SYSTEM_EXT_7154;
        default: return SOUND_SYSTEM_INVALID;
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: swr_multichannel_probe <文件.iamf> [--sound-system N] [--device-channels N]\n");
        return 2;
    }
    int soundSystem = 5;
    int deviceChannels = 2;
    for (int i = 2; i + 1 < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--sound-system") soundSystem = _wtoi(argv[i + 1]);
        else if (key == L"--device-channels") deviceChannels = _wtoi(argv[i + 1]);
    }
    if (soundSystem < 0 || soundSystem > 14) soundSystem = 5;
    if (deviceChannels < 1 || deviceChannels > 8) deviceChannels = 2;

    std::vector<std::uint8_t> data;
    if (!ReadAll(argv[1], data)) { printf("无法读取文件\n"); return 3; }

    const auto handle = IAMF_decoder_open();
    if (handle == nullptr) { printf("open 失败\n"); return 4; }
    if (IAMF_decoder_output_layout_set_sound_system(handle, SoundSystemEnum(soundSystem)) != IAMF_OK) {
        printf("布局设置失败\n"); IAMF_decoder_close(handle); return 4;
    }

    // —— 与 IamfAudioDecoder::Open 相同的配置泵 ——
    constexpr std::size_t defBlockSize = 960 * 6 * 2 * 16;
    std::vector<std::uint8_t> block((std::max)(defBlockSize, data.size()));
    std::size_t used = 0, readPos = 0;
    bool end = false;
    int state = 0;
    int configResult = IAMF_ERR_BUFFER_TOO_SMALL;
    int channels = ChannelsForSoundSystem(soundSystem);
    int sampleRate = 0;
    std::uint32_t maxFrame = 0;
    std::vector<std::uint8_t> pcm;
    int converted = 0;
    bool outputNonZero = false;

    auto* resampler = static_cast<SwrContext*>(nullptr);
    AVFrame* frame = av_frame_alloc();
    std::vector<std::uint8_t> out;
    int outCapacitySamples = 0;

    while (true) {
        std::size_t got = 0;
        if (used != defBlockSize && readPos < data.size()) {
            got = (std::min)(defBlockSize - used, data.size() - readPos);
            std::memcpy(block.data() + used, data.data() + readPos, got);
            readPos += got;
        }
        if (got == 0 && readPos >= data.size()) end = true;
        const std::size_t size = used + got;
        used = 0;

        if (state <= 0) {
            if (end) break;
            std::uint32_t rsize = 0;
            const auto ret = IAMF_decoder_configure(handle, block.data(),
                static_cast<std::uint32_t>(size), &rsize);
            if (ret == IAMF_OK) {
                state = 1;
                configResult = IAMF_OK;
                if (const auto* info = IAMF_decoder_get_stream_info(handle); info != nullptr) {
                    sampleRate = static_cast<int>(info->iamf_stream_info.sampling_rate);
                    maxFrame = info->max_frame_size;
                }
                pcm.assign(static_cast<std::size_t>(maxFrame) * channels * 2u, 0);
                printf("配置成功: %d 声道, %d Hz, max_frame=%u\n", channels, sampleRate, maxFrame);
            } else if (ret != IAMF_ERR_BUFFER_TOO_SMALL || rsize == 0) {
                configResult = ret;
                break;
            }
            used += rsize;
        }

        if (state > 0 && !pcm.empty()) {
            while (true) {
                std::uint32_t rsize = 0;
                const auto ret = end
                    ? IAMF_decoder_decode(handle, nullptr, 0, &rsize, pcm.data())
                    : IAMF_decoder_decode(handle, block.data() + used,
                        static_cast<std::int32_t>(size - used), &rsize, pcm.data());
                if (ret > 0) {
                    // ★ 关键：按 IamfAudioDecoder::Deliver 的方式建帧
                    av_frame_unref(frame);
                    frame->format = AV_SAMPLE_FMT_S16;
                    frame->sample_rate = sampleRate;
                    frame->nb_samples = ret;
                    av_channel_layout_default(&frame->ch_layout, channels);
                    if (av_frame_get_buffer(frame, 0) < 0) {
                        printf("av_frame_get_buffer 失败（%d 声道）\n", channels);
                        return 6;
                    }
                    const auto rowBytes = static_cast<std::size_t>(ret) *
                        static_cast<std::size_t>(channels) * 2u;
                    std::memcpy(frame->data[0], pcm.data(), rowBytes);
                    if (converted == 0) {
                        char desc[256]{};
                        av_channel_layout_describe(&frame->ch_layout, desc, sizeof(desc));
                        printf("帧布局: %s (%d 声道)\n", desc, frame->ch_layout.nb_channels);
                    }

                    // ★ 按 WasapiRenderer::EnsureResampler 的方式建 swr
                    if (resampler == nullptr) {
                        AVChannelLayout outLayout{};
                        av_channel_layout_default(&outLayout, deviceChannels);
                        const auto result = swr_alloc_set_opts2(&resampler, &outLayout,
                            AV_SAMPLE_FMT_FLT, sampleRate, &frame->ch_layout,
                            AV_SAMPLE_FMT_S16, sampleRate, 0, nullptr);
                        av_channel_layout_uninit(&outLayout);
                        if (result < 0 || resampler == nullptr || swr_init(resampler) < 0) {
                            printf("swr 初始化失败：%d 声道 -> %d 声道\n", channels, deviceChannels);
                            return 7;
                        }
                        outCapacitySamples = av_rescale_rnd(
                            swr_get_out_samples(resampler, maxFrame), 1, 1, AV_ROUND_UP);
                        out.resize(static_cast<std::size_t>(outCapacitySamples) *
                            deviceChannels * 4u);
                        printf("swr 就绪: %d 声道 -> %d 声道\n", channels, deviceChannels);
                    }

                    std::uint8_t* outPlanes[1] = {out.data()};
                    const auto producedOut = swr_convert(resampler, outPlanes,
                        outCapacitySamples, const_cast<const std::uint8_t**>(frame->extended_data),
                        ret);
                    if (producedOut > 0) {
                        converted += producedOut;
                        if (!outputNonZero) {
                            const auto* f = reinterpret_cast<const float*>(out.data());
                            for (int i = 0; i < producedOut * deviceChannels; ++i)
                                if (f[i] != 0.0f) { outputNonZero = true; break; }
                        }
                    }
                }
                if (end) break;
                used += rsize;
                if (ret < 0 || used >= size || rsize == 0) break;
            }
        }

        if (end) break;
        if (used > 0 && used < size)
            std::memmove(block.data(), block.data() + used, size - used);
        used = size - used;
    }

    printf("\n结果: %d 声道 -> %d 声道设备, 转换出 %d 采样, 非零=%s\n",
        channels, deviceChannels, converted, outputNonZero ? "是" : "否");
    const bool ok = configResult == IAMF_OK && converted > 0 && outputNonZero;
    printf("判定: %s\n", ok ? "PASS（swr 能承接多声道 IAMF 帧）"
                           : "FAIL（链路不通）");

    if (resampler != nullptr) swr_free(&resampler);
    av_frame_free(&frame);
    IAMF_decoder_close(handle);
    return ok ? 0 : 1;
}
