// IAMF 声道换位逻辑的**单元测试**（不依赖内核，直接验证映射）。
//
// 为什么这样测：内核解出的 PCM 会立刻经 swr 下混到设备声道，中间 PCM 无法从
// 外部抓取；而换位逻辑本身是纯函数，直接测它才能给出确定结论。
//
// 地面真值（bed_order_out 探针实测）：
//   12ch: out[k] <- src[0,1,2,3,6,7,4,5,8,9,10,11]
//   10ch: out[k] <- src[0,1,2,3,6,7,4,5,8,9]
//    8ch: out[k] <- src[0,1,2,3,6,7,4,5]
// 其余声道数不换位。
//
// 用法：iamf_reorder_test

#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {
constexpr bool NeedsSideBackSwap(const int channels) noexcept {
    return channels == 8 || channels == 10 || channels == 12;
}

void ReorderFromIamfToChannelLayout(std::uint8_t* const interleaved,
    const std::size_t sampleCount, const int channels, const int bytesPerSample) noexcept {
    if (!NeedsSideBackSwap(channels)) return;
    const auto frameBytes = static_cast<std::size_t>(channels) * bytesPerSample;
    std::uint8_t scratch[64]{};
    for (std::size_t sample = 0; sample < sampleCount; ++sample) {
        auto* const base = interleaved + sample * frameBytes;
        for (int pair = 0; pair < 2; ++pair) {
            auto* const a = base + static_cast<std::size_t>(4 + pair) * bytesPerSample;
            auto* const b = base + static_cast<std::size_t>(6 + pair) * bytesPerSample;
            std::memcpy(scratch, a, bytesPerSample);
            std::memcpy(a, b, bytesPerSample);
            std::memcpy(b, scratch, bytesPerSample);
        }
    }
}

int g_failures = 0;
void Check(const bool condition, const char* what) {
    printf("  [%s] %s\n", condition ? "OK" : "FAIL", what);
    if (!condition) ++g_failures;
}

/// 用 16-bit 样本跑一遍换位，返回每路首个样本值。
std::vector<std::int16_t> RunSwap(const int channels, const int samples) {
    std::vector<std::int16_t> data(static_cast<std::size_t>(channels) * samples);
    // 第 c 路全部填 c+1，便于识别
    for (int s = 0; s < samples; ++s)
        for (int c = 0; c < channels; ++c)
            data[static_cast<std::size_t>(s) * channels + c] = static_cast<std::int16_t>(c + 1);
    ReorderFromIamfToChannelLayout(reinterpret_cast<std::uint8_t*>(data.data()),
        static_cast<std::size_t>(samples), channels, 2);
    return data;
}
} // namespace

int wmain() {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    printf("=== 12 声道（7.1.4）===\n");
    {
        auto d = RunSwap(12, 3);
        const int expect[12] = {1, 2, 3, 4, 7, 8, 5, 6, 9, 10, 11, 12};
        auto ok = true;
        for (int c = 0; c < 12; ++c)
            if (d[static_cast<std::size_t>(c)] != expect[c]) ok = false;
        printf("  实测: ");
        for (int c = 0; c < 12; ++c) printf("%d ", d[static_cast<std::size_t>(c)]);
        printf("\n  期望: ");
        for (int c = 0; c < 12; ++c) printf("%d ", expect[c]);
        printf("\n");
        Check(ok, "12 声道映射 == src[0,1,2,3,6,7,4,5,8,9,10,11]");
        Check(d[2 * 12 + 4] == 7 && d[2 * 12 + 6] == 5, "第 2 个样本同样换位（非仅首样本）");
    }

    printf("\n=== 10 声道（7.1.2）===\n");
    {
        auto d = RunSwap(10, 2);
        const int expect[10] = {1, 2, 3, 4, 7, 8, 5, 6, 9, 10};
        auto ok = true;
        for (int c = 0; c < 10; ++c)
            if (d[static_cast<std::size_t>(c)] != expect[c]) ok = false;
        Check(ok, "10 声道映射 == src[0,1,2,3,6,7,4,5,8,9]");
    }

    printf("\n=== 8 声道（7.1）===\n");
    {
        auto d = RunSwap(8, 2);
        const int expect[8] = {1, 2, 3, 4, 7, 8, 5, 6};
        auto ok = true;
        for (int c = 0; c < 8; ++c)
            if (d[static_cast<std::size_t>(c)] != expect[c]) ok = false;
        Check(ok, "8 声道映射 == src[0,1,2,3,6,7,4,5]");
    }

    printf("\n=== 不应换位的布局 ===\n");
    for (const int channels : {1, 2, 6, 11, 14, 16, 24}) {
        auto d = RunSwap(channels, 2);
        auto unchanged = true;
        for (int c = 0; c < channels; ++c)
            if (d[static_cast<std::size_t>(c)] != c + 1) unchanged = false;
        char label[64]{};
        snprintf(label, sizeof(label), "%d 声道保持原序（无侧/后对）", channels);
        Check(unchanged, label);
    }

    printf("\n=== 幂等性：换两次应回到原序 ===\n");
    {
        const int channels = 12;
        auto d = RunSwap(channels, 2);
        ReorderFromIamfToChannelLayout(reinterpret_cast<std::uint8_t*>(d.data()), 2, channels, 2);
        auto restored = true;
        for (int c = 0; c < channels; ++c)
            if (d[static_cast<std::size_t>(c)] != c + 1) restored = false;
        Check(restored, "二次换位还原（说明是纯对换，非错位搬移）");
    }

    printf("\n%s（失败 %d）\n", g_failures == 0 ? "全部通过" : "**有失败**", g_failures);
    return g_failures == 0 ? 0 : 1;
}
