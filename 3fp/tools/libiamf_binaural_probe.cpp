// libiamf 双耳渲染实测探针（2026-10-11）。
//
// 目的：验证"立体声设备走 libiamf binaural 渲染"这条设计在真实素材上成立，
// 再决定内核怎么接线。对同一个 IAMF 文件分别以两种输出布局解码：
//   A) sound system F (7.1.4 = 12ch) —— 当前内核行为，之后由 swr 折叠到立体声；
//   B) binaural (HRTF 双耳)          —— 本案要补的路径，libiamf 直接出 2ch。
// 对每种报告：输出声道数、非静音采样数、L/R 差异（双耳渲染应当 L≠R 且差异
// 显著——矩阵折叠的立体声床也可能 L≠R，故判据是"非静音 + 声道数符合预期"，
// 双耳是否正确另看 L−R 能量占比量级）。
//
// 另测：binaural 设置失败/不支持时的降级是否可预期（本探针只报告返回码）。
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <cmath>
#include <string>
#include <vector>

#include "IAMF_decoder.h"

namespace {

struct Stats {
    int channels = 0;
    std::uint64_t samples = 0;
    std::uint64_t nonSilent = 0;
    double peak = 0.0;
    double lrDifferenceRms = 0.0;   // RMS(L-R)，双耳渲染应显著大于 0
    bool ok = false;
};

std::vector<std::uint8_t> ReadFile(const char* path) {
    std::vector<std::uint8_t> data;
    FILE* f = nullptr;
    if (fopen_s(&f, path, "rb") != 0 || f == nullptr) return data;
    fseek(f, 0, SEEK_END);
    const auto size = static_cast<std::size_t>(ftell(f));
    fseek(f, 0, SEEK_SET);
    data.resize(size);
    const auto read = fread(data.data(), 1, size, f);
    data.resize(read);
    fclose(f);
    return data;
}

// 以给定输出布局解码整个文件，返回统计。layout=0 表示 sound system F，1 表示 binaural。
Stats DecodeWith(const std::vector<std::uint8_t>& file, bool binaural) {
    Stats stats;
    auto* decoder = IAMF_decoder_open();
    if (decoder == nullptr) { std::printf("  IAMF_decoder_open 失败\n"); return stats; }

    int layoutChannels = 0;
    if (binaural) {
        layoutChannels = IAMF_layout_binaural_channels_count();
        const auto rc = IAMF_decoder_output_layout_set_binaural(decoder);
        std::printf("  set_binaural -> %d（binaural 声道数=%d）\n", rc, layoutChannels);
        if (rc != IAMF_OK) {
            std::printf("  => binaural 被拒绝，本例中将回退 sound system F\n");
            IAMF_decoder_close(decoder);
            return stats;
        }
    } else {
        const auto rc = IAMF_decoder_output_layout_set_sound_system(decoder, SOUND_SYSTEM_F);
        layoutChannels = 12;
        std::printf("  set_sound_system(F) -> %d\n", rc);
        if (rc != IAMF_OK) { IAMF_decoder_close(decoder); return stats; }
    }

    // configure：喂整个文件（IAMF_ERR_BUFFER_TOO_SMALL 表示还要更多描述符字节）。
    // 块大小逐步放大：HOA/多元素文件的描述符 OBU 可以远超 64KB，固定小块会在
    // 文件耗尽前永远等不到"够用"。
    std::size_t used = 0;
    bool configured = false;
    std::size_t chunkSize = 1 << 16;
    while (used < file.size()) {
        const auto chunk = static_cast<std::uint32_t>(
            std::min<std::size_t>(chunkSize, file.size() - used));
        std::uint32_t consumed = 0;
        const auto rc = IAMF_decoder_configure(decoder, file.data() + used, chunk, &consumed);
        if (rc == IAMF_OK) { configured = true; used += consumed; break; }
        if (rc != IAMF_ERR_BUFFER_TOO_SMALL || consumed == 0) break;
        used += consumed;
        chunkSize = std::min<std::size_t>(chunkSize * 4, 4 << 20);
    }
    if (!configured) {
        std::printf("  configure 失败（布局 %s）\n", binaural ? "binaural" : "F");
        IAMF_decoder_close(decoder);
        return stats;
    }

    const auto* info = IAMF_decoder_get_stream_info(decoder);
    const auto maxFrame = info != nullptr ? static_cast<std::size_t>(info->max_frame_size) : 1024;
    if (info != nullptr)
        std::printf("  采样率=%d max_frame_size=%zu\n",
            static_cast<int>(info->iamf_stream_info.sampling_rate), maxFrame);

    // PCM 缓冲按布局声道数分配
    const auto outChannels = binaural && layoutChannels > 0 ? layoutChannels : 12;
    std::vector<std::int16_t> pcm(maxFrame * static_cast<std::size_t>(outChannels) * 2u, 0);

    // 从配置消费点继续喂剩余字节
    double sumSqLr = 0.0;
    std::uint64_t lrCount = 0;
    std::size_t cursor = used;
    bool fileEnd = false;
    while (true) {
        std::uint32_t consumed = 0;
        const std::uint8_t* data = nullptr;
        std::uint32_t size = 0;
        if (cursor < file.size()) {
            data = file.data() + cursor;
            size = static_cast<std::uint32_t>(std::min<std::size_t>(1 << 16, file.size() - cursor));
        } else if (!fileEnd) {
            fileEnd = true;  // 先让最后一轮把 size=0 的排空请求发出去
        }
        const auto produced = IAMF_decoder_decode(decoder, data, size, &consumed, pcm.data());
        if (consumed > 0) cursor += consumed;
        if (produced > 0) {
            const auto frames = static_cast<std::uint64_t>(produced) / (outChannels * 2);
            stats.samples += frames;
            for (std::uint64_t f = 0; f < frames; ++f) {
                const auto* s = pcm.data() + f * outChannels;
                for (int c = 0; c < outChannels; ++c) {
                    const auto v = static_cast<double>(s[c]) / 32768.0;
                    stats.peak = std::max(stats.peak, std::fabs(v));
                    if (std::fabs(v) > 1e-4) { ++stats.nonSilent; break; }
                }
                if (outChannels >= 2) {
                    const auto l = static_cast<double>(s[0]) / 32768.0;
                    const auto r = static_cast<double>(s[1]) / 32768.0;
                    const auto d = l - r;
                    sumSqLr += d * d;
                    ++lrCount;
                }
            }
        }
        if (produced <= 0 && fileEnd) break;
        if (produced < 0) { std::printf("  decode 返回 %d\n", produced); break; }
    }
    stats.channels = outChannels;
    stats.lrDifferenceRms = lrCount > 0 ? std::sqrt(sumSqLr / static_cast<double>(lrCount)) : 0.0;
    stats.ok = stats.samples > 0;
    IAMF_decoder_close(decoder);
    return stats;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("用法: %s <file.iamf>\n", argv[0]);
        return 1;
    }
    const auto file = ReadFile(argv[1]);
    if (file.empty()) { std::printf("读不到 %s\n", argv[1]); return 2; }
    std::printf("文件 %s（%zu B）\n\n", argv[1], file.size());

    std::printf("[A] sound system F（当前内核路径：12ch -> swr 折叠）\n");
    const auto a = DecodeWith(file, false);
    std::printf("  => 声道=%d 帧=%llu 非静音帧=%llu 峰值=%.4f RMS(L-R)=%.4f\n\n",
        a.channels, (unsigned long long)a.samples, (unsigned long long)a.nonSilent, a.peak, a.lrDifferenceRms);

    std::printf("[B] binaural（本案路径：libiamf 直接出 2ch）\n");
    const auto b = DecodeWith(file, true);
    std::printf("  => 声道=%d 帧=%llu 非静音帧=%llu 峰值=%.4f RMS(L-R)=%.4f\n\n",
        b.channels, (unsigned long long)b.samples, (unsigned long long)b.nonSilent, b.peak, b.lrDifferenceRms);

    std::printf("判词：\n");
    std::printf("  binaural 声道数==2 : %s\n", b.channels == 2 ? "是" : "否");
    std::printf("  binaural 非静音     : %s（%llu/%llu 帧）\n", b.nonSilent > 0 ? "是" : "否",
        (unsigned long long)b.nonSilent, (unsigned long long)b.samples);
    std::printf("  binaural L-R 有差   : %s（RMS=%.4f）\n", b.lrDifferenceRms > 1e-4 ? "是" : "否",
        b.lrDifferenceRms);
    return b.channels == 2 && b.nonSilent > 0 ? 0 : 3;
}
