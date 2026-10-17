// swr 下混矩阵实测探针（2026-10-11）。
//
// 目的：回答"当前 12ch(7.1.4) -> 立体声的下混是否包含空间部分"。
// 方法：与 WasapiRenderer::EnsureResampler 完全同参建 swr（输出 FLT、默认立体声
// 布局、rematrix_maxval=0.95 等本轮新增选项），然后对 12 个输入声道逐一单独
// 送 -1 dBFS 测试音，记录输出 L/R 的贡献系数。
//
// 判读：
//   * 只有 L/R/C/S 有系数、高度声道（TFL/TFR/TBL/TBR）与后环为 0
//     => 折叠是"去空间"的，IAMF 渲到顶层的声场在立体声端消失。
//   * 每声道都有非零系数 => 全矩阵折叠，空间信息以矩阵混合保留。
//
// 只依赖 swresample + avutil，不加载内核。
#include <cstdio>
#include <cstring>
#include <vector>
#include <array>

extern "C" {
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
}

namespace {

constexpr double RematrixMaxval = 0.95;
constexpr double CenterMixLevel = 0.707107;
constexpr double SurroundMixLevel = 0.707107;
constexpr double LfeMixLevel = 0.0;

// 与内核 EnsureResampler 同款建 swr（含本轮 rematrix 选项，init 前设置）。
SwrContext* BuildLikeKernel(int inChannels, int outChannels) {
    AVChannelLayout inLayout{};
    AVChannelLayout outLayout{};
    av_channel_layout_default(&inLayout, inChannels);
    av_channel_layout_default(&outLayout, outChannels);
    SwrContext* swr = nullptr;
    const auto result = swr_alloc_set_opts2(&swr, &outLayout, AV_SAMPLE_FMT_FLT, 48000,
        &inLayout, AV_SAMPLE_FMT_FLT, 48000, 0, nullptr);
    av_channel_layout_uninit(&outLayout);
    av_channel_layout_uninit(&inLayout);
    if (result < 0 || swr == nullptr) return nullptr;
    unsigned rejected = 0;
    const auto set = [&](const char* name, double value) {
        if (av_opt_set_double(swr, name, value, 0) < 0) ++rejected;
    };
    set("rematrix_maxval", RematrixMaxval);
    set("center_mix_level", CenterMixLevel);
    set("surround_mix_level", SurroundMixLevel);
    set("lfe_mix_level", LfeMixLevel);
    if (rejected != 0) std::printf("  [警告] %u 个 rematrix 选项被拒绝\n", rejected);
    if (swr_init(swr) < 0) { swr_free(&swr); return nullptr; }
    return swr;
}

// 单声道 -1 dBFS 测试音 -> 输出每个声道的贡献。
void ProbeChannel(SwrContext* swr, int inChannels, int outChannels, int channel) {
    constexpr int kFrames = 256;
    const float level = 0.8912509f;  // -1 dBFS
    std::vector<float> in(static_cast<std::size_t>(kFrames) * inChannels, 0.0f);
    for (int f = 0; f < kFrames; ++f)
        in[static_cast<std::size_t>(f) * inChannels + channel] = level;
    std::vector<float> out(static_cast<std::size_t>(kFrames) * outChannels, 0.0f);
    const std::uint8_t* inPlanes[1] = {reinterpret_cast<const std::uint8_t*>(in.data())};
    std::uint8_t* outPlanes[1] = {reinterpret_cast<std::uint8_t*>(out.data())};
    const auto produced = swr_convert(swr, outPlanes, kFrames, inPlanes, kFrames);
    if (produced <= 0) { std::printf("  ch%-2d: <convert failed %d>\n", channel, produced); return; }
    std::array<double, 8> peak{};
    for (int f = 0; f < produced; ++f)
        for (int c = 0; c < outChannels && c < 8; ++c)
            peak[static_cast<std::size_t>(c)] = std::max(peak[static_cast<std::size_t>(c)],
                static_cast<double>(out[static_cast<std::size_t>(f) * outChannels + c]));
    std::printf("  ch%-2d:", channel);
    for (int c = 0; c < outChannels && c < 8; ++c)
        std::printf("  %s=%+.4f", c == 0 ? "L" : (c == 1 ? "R" : "?"), peak[static_cast<std::size_t>(c)]);
    std::printf("\n");
}

const char* ChannelName7_1_4(int index) {
    static const char* names[] = {"L", "R", "C", "LFE", "BL", "BR", "TFL", "TFR", "TBL", "TBR", "SiL", "SiR"};
    return index >= 0 && index < 12 ? names[index] : "?";
}

} // namespace

int main(int argc, char** argv) {
    const int outChannels = argc > 1 ? std::atoi(argv[1]) : 2;
    const int inChannels = 12;
    char inDesc[256]{};
    char outDesc[256]{};
    AVChannelLayout inLayout{};
    AVChannelLayout outLayout{};
    av_channel_layout_default(&inLayout, inChannels);
    av_channel_layout_default(&outLayout, outChannels);
    av_channel_layout_describe(&inLayout, inDesc, sizeof(inDesc));
    av_channel_layout_describe(&outLayout, outDesc, sizeof(outDesc));
    av_channel_layout_uninit(&inLayout);
    av_channel_layout_uninit(&outLayout);

    std::printf("swr 下混矩阵实测（与内核 EnsureResampler 同参，rematrix_maxval=%.2f）\n", RematrixMaxval);
    std::printf("输入: %s\n输出: %s\n\n", inDesc, outDesc);

    auto* swr = BuildLikeKernel(inChannels, outChannels);
    if (swr == nullptr) { std::printf("swr 创建失败\n"); return 1; }

    std::printf("每输入声道单独送 -1 dBFS 时的输出贡献：\n");
    for (int c = 0; c < inChannels; ++c) {
        std::printf("  [%s]", ChannelName7_1_4(c));
        ProbeChannel(swr, inChannels, outChannels, c);
    }

    // 汇总：高度四声道（6..9，TFL/TFR/TBL/TBR）的贡献占比
    double heightTotal = 0.0;
    for (int c = 6; c <= 9; ++c) {
        // 重测一遍取 L/R 峰值之和的近似
        constexpr int kFrames = 256;
        const float level = 0.8912509f;
        std::vector<float> in(static_cast<std::size_t>(kFrames) * inChannels, 0.0f);
        for (int f = 0; f < kFrames; ++f)
            in[static_cast<std::size_t>(f) * inChannels + c] = level;
        std::vector<float> out(static_cast<std::size_t>(kFrames) * outChannels, 0.0f);
        const std::uint8_t* inPlanes[1] = {reinterpret_cast<const std::uint8_t*>(in.data())};
        std::uint8_t* outPlanes[1] = {reinterpret_cast<std::uint8_t*>(out.data())};
        const auto produced = swr_convert(swr, outPlanes, kFrames, inPlanes, kFrames);
        for (int f = 0; f < produced; ++f)
            for (int ch = 0; ch < outChannels; ++ch)
                heightTotal = std::max(heightTotal, static_cast<double>(
                    out[static_cast<std::size_t>(f) * outChannels + ch]));
    }
    std::printf("\n高度声道（TFL/TFR/TBL/TBR）单独送 -1dBFS 时输出最大电平: %.4f\n", heightTotal);
    std::printf(heightTotal == 0.0
        ? "=> 高度声道在立体声折叠中被完全丢弃（无空间部分）\n"
        : "=> 高度声道有贡献（%.1f%%），空间部分部分保留\n", heightTotal * 100.0 / 0.8912509);

    swr_free(&swr);
    return 0;
}
