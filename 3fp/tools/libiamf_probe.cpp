// 阶段二可行性验证（2a 前置）：确认 libiamf 能被 3FP 侧代码正常调用。
//
// 本探针**不接入播放链路**，只回答一个工程问题：
//   「用 iamf.lib 的公开 API，能否把 IAMF 位流解成多声道 PCM？」
// 若这一步通了，阶段二就是"接线"而不是"研究"。
//
// 关键 API（code/include/IAMF_decoder.h）：
//   IAMF_decoder_open / close
//   IAMF_decoder_configure(handle, data, size, &rsize)   ← descriptor OBUs
//   IAMF_decoder_decode(handle, data, size, &rsize, pcm) ← audio frame OBUs
//   IAMF_decoder_output_layout_set_sound_system(handle, ss)
//
// ★ 数据泵**照搬参考实现** code/test/tools/iamfdec/src/test_iamfdec.c:396-486。
//   我先后写错两版，教训记在这里：
//     1) 把 IAMF_ERR_BUFFER_TOO_SMALL 当致命错误 —— 它是**正常中间态**；
//     2) 一次性把整个文件塞给 configure —— 描述符解析完就该停，
//        剩余字节属于 decode；必须用**滑动缓冲 + rsize** 流式推进。
//
// 用法：libiamf_probe <文件.iamf> [--sound-system N]
//
// ⚠ 本文件是独立探针，不进 3FP 产物。它给阶段二留一个可回归的基准。

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

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
        read == out.size();
    CloseHandle(file);
    return ok;
}

const char* SoundSystemName(const int ss) {
    switch (ss) {
        case 0: return "A (0+2+0)";
        case 1: return "B (0+5+0)";
        case 2: return "C (2+5+0)";
        case 3: return "D (4+5+0)";
        case 4: return "E (4+5+1)";
        case 5: return "F (3+7+0)";
        case 6: return "G (4+9+0)";
        case 7: return "H (9+10+3)";
        case 8: return "I (0+7+0)";
        case 9: return "J (4+7+0)";
        case 10: return "ext 712 (2+7+0)";
        case 11: return "ext 312 (2+3+0)";
        case 12: return "mono (0+1+0)";
        case 13: return "ext 916 (6+9+0)";
        case 14: return "ext 7154 (5+7+4)";
        default: return "?";
    }
}

/// 各 sound system 的输出声道数 —— 由 iamfdec.exe 实测得到（见 docs/44），
/// 不是按 "Upper+Middle+Bottom" 相加猜的：H(9+10+3) 实测 24 而非 22。
std::uint32_t OutputChannelsForSoundSystem(const int ss) {
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

/// 命令行 0..14 -> 枚举（显式映射，避免上游改序时静默错位）。
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
    if (argc < 2) { wprintf(L"用法: libiamf_probe <文件.iamf> [--sound-system N]\n"); return 2; }
    int soundSystem = 5;   // F (3+7+0) = 12 声道，与 7.1.4 最接近
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--sound-system") soundSystem = _wtoi(argv[i + 1]);
    if (soundSystem < 0 || soundSystem > 14) soundSystem = 5;

    std::vector<std::uint8_t> data;
    if (!ReadAll(argv[1], data)) { printf("无法读取文件\n"); return 3; }

    printf("文件: %ls\n", argv[1]);
    printf("大小: %zu 字节\n", data.size());
    printf("目标 sound system: %d = %s\n\n", soundSystem, SoundSystemName(soundSystem));

    const auto handle = IAMF_decoder_open();
    if (handle == nullptr) { printf("IAMF_decoder_open 失败\n"); return 4; }
    printf("① IAMF_decoder_open          OK\n");

    // 输出布局必须在解码前设定：它决定混音目标，而不是由文件单方面决定。
    const auto layoutResult = IAMF_decoder_output_layout_set_sound_system(handle,
        SoundSystemEnum(soundSystem));
    printf("② output_layout_set_ss(%d)    %s (ret=%d)\n", soundSystem,
        layoutResult == IAMF_OK ? "OK" : "失败", layoutResult);

    const auto channels = OutputChannelsForSoundSystem(soundSystem);
    constexpr std::size_t defBlockSize = 960 * 6 * 2 * 16;   // 与参考实现一致

    // ── 数据泵：滑动缓冲 + rsize 推进（照搬参考实现）──────────────────────
    std::vector<std::uint8_t> block((std::max)(defBlockSize, data.size()));
    std::size_t used = 0;        // 缓冲中尚未被消费的字节数
    std::size_t readPos = 0;     // 文件读取位置
    bool end = false;
    int state = 0;
    int configResult = IAMF_ERR_BUFFER_TOO_SMALL;
    long long frames = 0;
    long long totalSamples = 0;
    std::uint32_t maxFrame = 0;
    std::vector<float> pcm;
    bool 有信号 = false;

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
                if (const auto* cfgInfo = IAMF_decoder_get_stream_info(handle);
                    cfgInfo != nullptr) {
                    const auto& stream = cfgInfo->iamf_stream_info;
                    printf("④ stream_info: 采样率=%d 每帧=%u 元素=%u 混音=%u max_frame=%u\n",
                        static_cast<int>(stream.sampling_rate),
                        stream.samples_per_channel_in_frame,
                        stream.audio_element_count,
                        stream.mix_presentation_count,
                        cfgInfo->max_frame_size);
                    maxFrame = cfgInfo->max_frame_size;
                }
                if (maxFrame > 0 && channels > 0)
                    pcm.assign(static_cast<std::size_t>(maxFrame) * channels, 0.0f);
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
                    ++frames;
                    totalSamples += ret;
                    if (!有信号) {
                        for (std::size_t i = 0; i < pcm.size(); ++i)
                            if (pcm[i] != 0.0f) { 有信号 = true; break; }
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

    printf("③ IAMF_decoder_configure     %s (ret=%d)\n",
        configResult == IAMF_OK ? "OK" : "失败", configResult);
    if (configResult != IAMF_OK) {
        IAMF_decoder_close(handle);
        return 5;
    }

    printf("\n结果: 请求 %s -> %u 声道, %lld 帧, %lld 采样, 非零=%s\n",
        SoundSystemName(soundSystem), channels, frames, totalSamples,
        有信号 ? "是" : "否");

    const bool ok = frames > 0 && channels > 0 && 有信号;
    printf("判定: %s\n", ok ? "PASS（libiamf 可用于 3FP 集成）"
                           : "FAIL（未取得有效多声道输出）");

    IAMF_decoder_close(handle);
    return ok ? 0 : 1;
}
