// HLG→scRGB OOTF 域回归探针：断言 HLG 解码按 BT.2100 在亮度域施加 OOTF。
//
// 背景：HlgToNits（shader 与 C++ 参考实现两份）曾把 pow(·,1.2) 逐通道施加。
// 逐通道与亮度域 OOTF 仅对灰阶等效：对饱和像素它等效于每通道乘 Yc^(-0.2)，
// 纯蓝最大过亮 1.76x，经 DWM rolloff 呈现为高光滚白（过曝），见 docs/141。
//
// 锚点（MapToHdr + source2020=1，即生产 scRGB 臂的同合同诊断路径）：
//   灰 0.75 → BT.2408 参考白 203.15 nits → scRGB (2.5394)^3（修复前后不变）
//   红 1.0  → 765.41 nits 刺激 → (15.8869,-1.1916,-0.1737)（旧实现 20.7561,…）
//   绿 1.0  → 925.22 nits 刺激 → (-6.7962,13.1023,-1.1632)（旧实现 -7.3455,14.1613,…）
//   蓝 1.0  → 568.34 nits 刺激 → (-0.5175,-0.0593,7.9478)（旧实现 -0.9106,…,13.9841）
//   晚霞 (0.99,0.34,0.08) → (14.9616,-0.7174,-0.1803)（旧实现 19.2927,…）
//
// 用法：直接运行（链接 FFF.Native.lib，无需播放器句柄）。
// 退出码：0=全部锚点通过；1=存在失败。

#include <cmath>
#include <cstdio>
#include <iterator>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

struct Anchor {
    const char* name;
    float input[3];
    float expected[3];
};

constexpr Anchor kAnchors[] = {
    {"gray075", {0.75f, 0.75f, 0.75f}, {2.5394f, 2.5394f, 2.5394f}},
    {"red1.0 ", {1.0f, 0.0f, 0.0f}, {15.8869f, -1.1916f, -0.1737f}},
    {"green1.0", {0.0f, 1.0f, 0.0f}, {-6.7962f, 13.1023f, -1.1632f}},
    {"blue1.0", {0.0f, 0.0f, 1.0f}, {-0.5175f, -0.0593f, 7.9478f}},
    {"sunset ", {0.99f, 0.34f, 0.08f}, {14.9616f, -0.7174f, -0.1803f}},
};

constexpr float kTolerance = 0.02f;

FFF3FPColorTransform MakeTransform(const float input[3]) {
    FFF3FPColorTransform transform{};
    transform.size = sizeof(transform);
    transform.version = 20;
    transform.colorMode = FFF3FPColorMode::MapToHdr;   // scRGB 臂：To709 后 /80
    transform.transfer = FFF3FPColorTransfer::Hlg;
    transform.source2020 = 1;
    transform.inputRed = input[0];
    transform.inputGreen = input[1];
    transform.inputBlue = input[2];
    transform.sdrPeakNits = 203.0f;
    transform.sourcePeakNits = 1000.0f;
    transform.paperWhiteNits = 203.0f;
    return transform;
}

} // namespace

int wmain() {
    int failed = 0;
    for (const auto& anchor : kAnchors) {
        auto transform = MakeTransform(anchor.input);
        if (FFF3FP_EvaluateColorTransform(&transform) != FFFResult::Success) {
            std::printf("[FAIL] %s EvaluateColorTransform 拒绝调用\n", anchor.name);
            ++failed;
            continue;
        }
        const float got[3] = {transform.outputRed, transform.outputGreen,
            transform.outputBlue};
        bool pass = true;
        for (int i = 0; i < 3; ++i) {
            if (std::fabs(got[i] - anchor.expected[i]) > kTolerance) pass = false;
        }
        if (pass) {
            std::printf("[PASS] %s got=(%.4f,%.4f,%.4f)\n",
                anchor.name, got[0], got[1], got[2]);
        } else {
            std::printf("[FAIL] %s got=(%.4f,%.4f,%.4f) want=(%.4f,%.4f,%.4f)"
                "  —— 若 got 接近旧实现 (20.7561/-7.3455/-0.9106/19.2927 一族)，"
                "说明内核仍在逐通道做 OOTF\n",
                anchor.name, got[0], got[1], got[2],
                anchor.expected[0], anchor.expected[1], anchor.expected[2]);
            ++failed;
        }
    }
    std::printf("%s：%d/%d 锚点通过\n",
        failed == 0 ? "OK" : "NG", static_cast<int>(std::size(kAnchors)) - failed,
        static_cast<int>(std::size(kAnchors)));
    return failed == 0 ? 0 : 1;
}
