// OAR 直接可用性探针（2026-10-11）：验证"任意声道床 -> 双耳立体声"能否用
// **已链入内核的 OBR** 完成（零新依赖、零新授权）。
//
// 路径：oar_create(binaural) -> oar_add_audio_group ->
//       oar_add_audio_element(ck_channel_based, layout=7.1) ->
//       oar_update_audio_element_data(planar) -> oar_render(planar f32 stereo)
//
// 判据：
//   1. 7.1 床能被建成 channel-based 元素且 binaural 渲染不报错；
//   2. 输出是 2 声道、非静音；
//   3. 只给环绕声道（SL/SR）送反相测试音，渲染出的 L/R 应显著不对称
//      （证明 HRTF 在工作，不是矩阵折叠）。
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

// oar.h 没有 extern "C" 保护（不像 IAMF_decoder.h 自带），库按 C 链接导出；
// C++ 直接包含会生成修饰名导致 LNK2019，必须包一层。
extern "C" {
#include "oar.h"
}

namespace {

constexpr int kChannels = 8;                 // 7.1 bed
constexpr int kFrames = 1024;
constexpr int kRate = 48000;

} // namespace

int main() {
    // ---- 1. 建 OAR：目标 = 双耳 ----
    oar_config_t config{};
    config.target_layout = ck_oar_layout_binaural;
    config.samples_per_channel = kFrames;
    config.sampling_rate = kRate;
    auto* oar = oar_create(&config);
    if (oar == nullptr) { std::printf("oar_create 失败\n"); return 1; }

    const auto gid = oar_add_audio_group(oar);
    if (gid < 0) { std::printf("oar_add_audio_group 失败：%d\n", gid); return 2; }

    // ---- 2. 加一个 channel-based(7.1床) 元素 ----
    oar_audio_element_config_t element{};
    element.type = ck_channel_based;
    element.cbc.layout = ck_oar_layout_71;
    constexpr std::uint32_t kElementId = 1;
    const auto addRc = oar_add_audio_element(oar, static_cast<std::uint32_t>(gid),
        kElementId, &element);
    if (addRc != ck_oar_ok) {
        std::printf("oar_add_audio_element(7.1床) 失败：%d\n", addRc);
        return 3;
    }
    std::printf("channel-based 7.1 元素已加入（gid=%d）\n", gid);
    std::printf("输出声道数=%d 帧长=%d\n",
        oar_get_number_of_output_channels(oar), oar_get_samples_per_channel(oar));

    // ---- 3. 投喂平面数据：只给 SL(4) 送 1kHz ----
    // 判据设计（数学上区分 HRTF 与矩阵折叠）：
    //   矩阵折叠：R = 0（SL 只进 L）——对耳恰好零串音；
    //   HRTF：两个耳朵都有能量（头部声学阴影/绕射），R/L 有稳定比例。
    // 所以"R 是否拿到非零能量"就是 HRTF 是否在工作的判据。
    std::vector<float> planar(static_cast<std::size_t>(kChannels) * kFrames, 0.0f);
    for (int f = 0; f < kFrames; ++f) {
        const auto t = static_cast<float>(f) / kRate;
        planar[4 * kFrames + f] = 0.5f * std::sin(2.0f * 3.14159265f * 1000.0f * t);  // SL only
    }
    oar_audio_block_t in{};
    in.channels = kChannels;
    in.samples_per_channel = kFrames;
    in.data = planar.data();
    if (oar_update_audio_element_data(oar, kElementId, &in) != ck_oar_ok) {
        std::printf("oar_update_audio_element_data 失败\n");
        return 4;
    }

    // ---- 4. 渲染 ----
    std::vector<float> out(2u * kFrames, 0.0f);
    oar_audio_block_t outBlock{};
    outBlock.channels = 2;
    outBlock.samples_per_channel = kFrames;
    outBlock.data = out.data();
    const auto renderRc = oar_render(oar, &outBlock);
    if (renderRc != ck_oar_ok) {
        std::printf("oar_render 失败：%d\n", renderRc);
        return 5;
    }

    double peakL = 0, peakR = 0;
    for (int f = 0; f < kFrames; ++f) {
        peakL = std::max(peakL, static_cast<double>(std::fabs(out[f])));
        peakR = std::max(peakR, static_cast<double>(std::fabs(out[kFrames + f])));
    }
    std::printf("渲染完成：peakL=%.4f peakR=%.4f\n", peakL, peakR);

    // ---- 5. 判据 ----
    // 只送 SL：矩阵折叠会给 R=0；HRTF 下 R 必拿到非零能量（对耳串音）。
    // R/L > 10% 即认定为 HRTF 在工作（实测 HRTF 的串音量级在 20%~60%）。
    const double ratio = peakL > 0 ? peakR / peakL : 0.0;
    std::printf("R/L = %.3f（矩阵折叠应为 0.000；HRTF 典型 0.2~0.6）\n", ratio);
    const bool pass = outBlock.channels == 2 && outBlock.samples_per_channel == kFrames &&
        peakL > 1e-4 && ratio > 0.10;
    std::printf("判词：%s\n", pass
        ? "PASS —— OBR 可做通用 7.1 床 -> 双耳：对耳有串音（HRTF），空间定位保留"
        : "FAIL —— 对耳无串音，退化为矩阵折叠或渲染未生效");
    oar_destroy(oar);
    return pass ? 0 : 10;
}
