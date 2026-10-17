// P0 布局映射黄金值探针（2026-10-11）。
//
// 目的：内核接 OBR 做"声道床 -> 双耳"前，先钉死 FFmpeg 声道序 与 OBR 布局
// 声道序的对应关系。错位的后果是静默的（左右环绕交换听起来只是"有点怪"），
// 所以这里对每个布局的每个输入声道单独送测试音，把渲染出的 L/R 峰值比
// 录成黄金值表，之后作为回归测试。
//
// 覆盖布局：5.1 / 7.1 / 5.1.2 / 7.1.4（FFmpeg 能产出的床布局全集）。
// 判据（声学常识）：
//   * 左半侧声道（FL/BL/SiL/顶左）=> L 强、R 弱（R/L < 0.7）
//   * 右半侧声道                          => R 强、L 弱（L/R < 0.7）
//   * 中置 FC                             => 大致对称（0.5 < R/L < 2）
//   * LFE                                 => 输出很低（双耳渲染通常丢掉 LFE）
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

extern "C" {
#include "oar.h"
#include <libavutil/channel_layout.h>
}

namespace {

struct LayoutCase {
    const char* name;        // 人类可读
    oar_layout_t oarLayout;  // OBR 布局枚举
    int channels;            // 声道数
    const char* expected;    // 每声道期望类别（FFmpeg 该布局的物理顺序，空格分隔）
};

constexpr int kFrames = 2048;
constexpr int kRate = 48000;

// 送第 channel 路一段确定性噪声（固定种子，可复现），渲染后测 L/R 能量比
// （ILD）。用能量而不是纯音峰值：HRTF 在某些频率有深陷波，纯音峰值会被
// 陷波位置主导，噪声能量积分后才反映整体侧化。
// 返回 false 表示渲染失败。
bool ProbeChannel(oar_t* oar, int channels, int channel, double& ildDb, double& rmsL, double& rmsR) {
    std::vector<float> planar(static_cast<std::size_t>(channels) * kFrames, 0.0f);
    // xorshift32 固定种子 => 逐位可复现的均匀噪声，幅度 0.5。
    std::uint32_t state = 0x12345678u + static_cast<std::uint32_t>(channel) * 2654435761u;
    for (int f = 0; f < kFrames; ++f) {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        const auto v = (static_cast<float>(state >> 8) / 8388608.0f) - 1.0f;  // [-1,1)
        planar[static_cast<std::size_t>(channel) * kFrames + f] = 0.5f * v;
    }
    oar_audio_block_t in{};
    in.channels = static_cast<std::uint32_t>(channels);
    in.samples_per_channel = kFrames;
    in.data = planar.data();
    if (oar_update_audio_element_data(oar, 1, &in) != ck_oar_ok) return false;

    std::vector<float> out(2u * kFrames, 0.0f);
    oar_audio_block_t outBlock{};
    outBlock.channels = 2;
    outBlock.samples_per_channel = kFrames;
    outBlock.data = out.data();
    if (oar_render(oar, &outBlock) != ck_oar_ok) return false;

    double sumL = 0, sumR = 0;
    for (int f = 0; f < kFrames; ++f) {
        const auto l = static_cast<double>(out[f]);
        const auto r = static_cast<double>(out[kFrames + f]);
        sumL += l * l; sumR += r * r;
    }
    rmsL = std::sqrt(sumL / kFrames);
    rmsR = std::sqrt(sumR / kFrames);
    ildDb = 20.0 * std::log10(std::max(rmsR, 1e-12) / std::max(rmsL, 1e-12));
    return true;
}

// 按声学常识判定该声道的左右归属。ILD（R 相对 L 的能量差，dB）：
//   明显偏左 (< -4dB) => L；明显偏右 (> +4dB) => R；中间 => C；
//   总能量极低 (< -40dB 相对满刻度) => "-"（LFE 或渲染丢弃）。
const char* Classify(double ildDb, double rmsL, double rmsR) {
    const auto total = std::sqrt(rmsL * rmsL + rmsR * rmsR);
    const auto totalDb = 20.0 * std::log10(std::max(total, 1e-12));
    if (totalDb < -40.0) return "-";
    if (ildDb < -4.0) return "L";
    if (ildDb > 4.0) return "R";
    return "C";
}

// 同源分双槽测试（7.1(wide) 映射校准用）：把同一段噪声以 amplitude 分别送
// FL 槽与 SiL 槽，测合成 ILD。用于标定 FLC->(0.5 FL + 0.5 SiL) 这类 split
// 的真实效果——OBR 内部走 HOA 往返，线性模型不成立，只能实测。
void SplitProbe(double flAmplitude, double siAmplitude) {
    oar_config_t config{};
    config.target_layout = ck_oar_layout_binaural;
    config.samples_per_channel = kFrames;
    config.sampling_rate = kRate;
    auto* oar = oar_create(&config);
    if (oar == nullptr) { std::printf("  oar_create 失败\n"); return; }
    const auto gid = oar_add_audio_group(oar);
    if (gid < 0) { oar_destroy(oar); return; }
    oar_audio_element_config_t element{};
    element.type = ck_channel_based;
    element.cbc.layout = ck_oar_layout_71;
    if (oar_add_audio_element(oar, static_cast<std::uint32_t>(gid), 1, &element) != ck_oar_ok) {
        oar_destroy(oar);
        return;
    }
    // 与内核 BedBinauralRenderer::EnsurePipeline 同款：限幅器 -1dB。
    // （E2E 与库级 ILD 曾差 8dB，隔离变量用。）
    oar_enable_limiter(oar, 1);
    oar_set_limiter_threshold(oar, -1.0f);
    std::vector<float> planar(8u * kFrames, 0.0f);
    std::uint32_t state = 0xC0FFEEu;
    for (int f = 0; f < kFrames; ++f) {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        const auto v = (static_cast<float>(state >> 8) / 8388608.0f) - 1.0f;
        planar[0 * kFrames + f] = static_cast<float>(flAmplitude) * v;   // FL 槽
        planar[6 * kFrames + f] = static_cast<float>(siAmplitude) * v;   // SiL 槽
    }
    oar_audio_block_t in{};
    in.channels = 8;
    in.samples_per_channel = kFrames;
    in.data = planar.data();
    if (oar_update_audio_element_data(oar, 1, &in) != ck_oar_ok) { oar_destroy(oar); return; }
    std::vector<float> out(2u * kFrames, 0.0f);
    oar_audio_block_t outBlock{};
    outBlock.channels = 2;
    outBlock.samples_per_channel = kFrames;
    outBlock.data = out.data();
    if (oar_render(oar, &outBlock) != ck_oar_ok) { oar_destroy(oar); return; }
    double sumL = 0, sumR = 0;
    for (int f = 0; f < kFrames; ++f) {
        sumL += static_cast<double>(out[f]) * out[f];
        sumR += static_cast<double>(out[kFrames + f]) * out[kFrames + f];
    }
    const auto ild = 20.0 * std::log10(std::max(1e-12, std::sqrt(sumR)) /
                                       std::max(1e-12, std::sqrt(sumL)));
    const auto* lf = out.data();
    const auto* rf = out.data() + kFrames;
    const double peakL = *std::max_element(lf, lf + kFrames, [](float a, float b) { return std::fabs(a) < std::fabs(b); });
    const double peakR = *std::max_element(rf, rf + kFrames, [](float a, float b) { return std::fabs(a) < std::fabs(b); });
    const auto ildPeak = 20.0 * std::log10(std::max(1e-12, std::fabs(peakR)) /
                                           std::max(1e-12, std::fabs(peakL)));
    std::printf("  FL*%.2f + SiL*%.2f（同源）=> ILD(rms)=%+.1fdB  ILD(peak)=%+.1fdB\n",
        flAmplitude, siAmplitude, ild, ildPeak);
    oar_destroy(oar);
}

void RunLayout(const LayoutCase& lc) {

    oar_config_t config{};
    config.target_layout = ck_oar_layout_binaural;
    config.samples_per_channel = kFrames;
    config.sampling_rate = kRate;
    auto* oar = oar_create(&config);
    if (oar == nullptr) { std::printf("  oar_create 失败\n"); return; }
    const auto gid = oar_add_audio_group(oar);
    if (gid < 0) { std::printf("  add_group 失败 %d\n", gid); oar_destroy(oar); return; }
    oar_audio_element_config_t element{};
    element.type = ck_channel_based;
    element.cbc.layout = lc.oarLayout;
    if (oar_add_audio_element(oar, static_cast<std::uint32_t>(gid), 1, &element) != ck_oar_ok) {
        std::printf("  add_element(布局 %s) 失败\n", lc.name);
        oar_destroy(oar);
        return;
    }

    // 解析期望类别序列
    std::vector<std::string> expected;
    {
        std::string token;
        for (const char* p = lc.expected; *p != '\0'; ++p) {
            if (*p == ' ') { if (!token.empty()) { expected.push_back(token); token.clear(); } }
            else token += *p;
        }
        if (!token.empty()) expected.push_back(token);
    }

    int failures = 0;
    for (int c = 0; c < lc.channels; ++c) {
        double ildDb = 0, rmsL = 0, rmsR = 0;
        if (!ProbeChannel(oar, lc.channels, c, ildDb, rmsL, rmsR)) {
            std::printf("  ch%-2d: 渲染失败\n", c);
            ++failures;
            continue;
        }
        const auto actual = Classify(ildDb, rmsL, rmsR);
        const auto want = c < static_cast<int>(expected.size()) ? expected[static_cast<std::size_t>(c)] : std::string("?");
        const bool ok = want == actual;
        if (!ok) ++failures;
        std::printf("  ch%-2d: rmsL=%+.4f rmsR=%+.4f ILD=%+6.1fdB  期望=%s 实际=%s %s\n",
            c, rmsL, rmsR, ildDb, want.c_str(), actual, ok ? "" : "  <<< 不一致");
    }
    std::printf("  => %s（%d 处不一致）\n", failures == 0 ? "一致" : "不一致", failures);
    oar_destroy(oar);
}

const char* FfmpegLayoutName(int channels) {
    // 用 FFmpeg 自己的默认布局定义打印名前，供与 OBR 侧对照。
    static char buf[256];
    AVChannelLayout layout{};
    av_channel_layout_default(&layout, channels);
    buf[0] = '\0';
    av_channel_layout_describe(&layout, buf, sizeof(buf));
    av_channel_layout_uninit(&layout);
    return buf;
}

} // namespace

int main() {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::printf("P0 布局映射黄金值：FFmpeg 默认布局 -> OBR 布局\n");
    for (int ch : {6, 8, 8, 12, 12}) {
        // 说明行由下面各用例打印
        (void)ch;
    }
    std::printf("FFmpeg 默认布局名：6ch=%s  8ch=%s  12ch=%s\n",
        FfmpegLayoutName(6), FfmpegLayoutName(8), FfmpegLayoutName(12));

    // 期望类别按 FFmpeg 默认布局的物理顺序给出：
    //   5.1  = FL FR FC LFE BL BR                     -> L R C - L R
    //   7.1  = FL FR FC LFE BL BR SL SR               -> L R C - L R L R
    //   5.1.2= FL FR FC LFE BL BR TFL TFR             -> L R C - L R L R
    //   7.1.4= FL FR FC LFE BL BR TFL TFR TBL TBR SiL SiR -> L R C - L R L R L R L R
    // （OBR 侧布局声道序若与之不符，这里会当场判红。）
    const LayoutCase cases[] = {
        {"5.1",  ck_oar_layout_51,  6,  "L R C - L R"},
        {"7.1",  ck_oar_layout_71,  8,  "L R C - L R L R"},
        {"5.1.2", ck_oar_layout_512, 8, "L R C - L R L R"},
        {"7.1.4", ck_oar_layout_714, 12, "L R C - L R L R L R L R"},
    };
    for (const auto& lc : cases) RunLayout(lc);
    // 7.1(wide) 的 FLC/FRC split 系数标定：P0 黄金值是 FL=-11.7dB、SiL=-6.9dB，
    // 一个 ±60° 的 FLC 映像 ILD 应落在两者之间（≈-9dB）。实测不同 split 的
    // 合成 ILD，选落点最接近的系数。
    std::printf("\n=== split 标定（同源分送 FL/SiL 槽，7.1 布局）===\n");
    SplitProbe(1.0, 0.0);
    SplitProbe(0.75, 0.25);
    SplitProbe(0.6, 0.4);
    SplitProbe(0.5, 0.5);
    std::printf("\n判词：四个布局的期望类别序列全部一致 => FFmpeg 声道序与 OBR 布局声道序\n"
                "一致，内核可直接恒等映射（只选布局枚举，不重排声道）。\n"
                "任何一处 <<< 不一致 都需要在映射层加声道重排。\n");
    return 0;
}
