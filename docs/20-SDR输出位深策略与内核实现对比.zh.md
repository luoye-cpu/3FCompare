# 20 · SDR 是否也该用 16 位？—— 与内核实现的对比分析

日期：2026-09-18
问题：HDR 走 16 位 scRGB 是正确的（`docs/19`）；那 SDR 是否也该做 Windows 能力分析，
在许可时改用 16 位？

**结论（先说）：不建议把 SDR 输出改成 16F。** 阻碍不是"Windows 认不认"
（16 位 scRGB 确实被 Windows 承认），而是**换格式等于换输出契约**，会牵动传递函数与
SDR 白点调整两处，收益却很小、带宽成本翻倍。内核当前的做法是自洽且刻意的。

---

## 1. 内核现在怎么做的

### 1.1 位深选择：SDR 侧**零能力分析**

`VideoRenderer.cpp:2022`：

```cpp
std::uint32_t PlayerVideoRenderer::PreferredOutputBitDepth(
    const std::uint32_t sourceBitDepth, const bool hdr) noexcept {
    return OutputBitDepthForSource(sourceBitDepth, hdr);   // 纯函数
}

constexpr std::uint32_t OutputBitDepthForSource(...) noexcept {
    // HDR output uses a 16-bit scRGB floating-point swap chain (linear
    // Rec.709 primaries, 1.0 = 80 nits) ... Floating point is never used for
    // SDR so DWM applies the Windows HDR SDR-white adjustment exactly once.
    if (hdr) return 16;
    if (sourceBitDepth > 8) return 10;
    return 8;
}
```

⇒ SDR 的位深**只取决于源位深**：8-bit 源 → 8，10-bit 源 → 10。没有任何显示器能力参与。

### 1.2 能力分析确实有，但**只服务 HDR**

`OutputSupportsHdr()`（`:2027`）：

| 步骤 | 实现 |
|---|---|
| 取输出描述 | `IDXGIOutput6::GetDesc1()`（DXGI 1.6） |
| 判定 | `BitsPerColor >= 10` **且** `ColorSpace ∈ {RGB_FULL_G2084_NONE_P2020, RGB_STUDIO_G2084_NONE_P2020}` |
| 亮度 | `ReadWindowsDisplayLuminance`（AdvancedColorInfo）取 min/max/full-frame nits |
| 兜底 | 亮度字段为空时保留上次值，最终回退 1000 nits（不阻塞 scRGB 尝试） |
| 缓存 | 按 HMONITOR 缓存 `HdrSupportProbeCacheDuration`；换显示器失效 |
| 粘性回退 | `hdrSwapChainRejected_`：该显示器上 HDR 交换链被拒后就不再反复尝试 |
| 绕过 | `forceHdrOutput_` 无视门控 |

⚠ 注意判据用的是**当前输出色彩空间**，不是显示器硬件上限 ⇒ 语义是"Windows 现在是否开着 HDR"，
与系统开关联动（`DisplayCapabilities.cs` 注释里有同样的说明）。

### 1.3 两条输出契约（这是关键）

`VideoRenderer.cpp:578-593`，同一个像素着色器里两个分支：

```hlsl
// HDR（ColorMode==2）
if (ColorMode == 2) {
    // scRGB swap-chain contract: linear Rec.709 primaries, 1.0 = 80 nits.
    float3 rec709Nits = Source2020 == 0 ? nits : To709(nits);
    return float4(rec709Nits / 80.0, 1);        // ← 线性光
}

// SDR
float3 sdr = ToBt709(To709(ToneHdrToSdr(nits, HdrPeak, SdrPeak)) / SdrPeak);
return float4(sdr, 1);                          // ← gamma 编码（BtOne = BT.709 OETF）
```

| | 交换链格式 | 数值语义 |
|---|---|---|
| HDR | `R16G16B16A16_FLOAT` | **线性光**，1.0 = 80 nits |
| SDR | `B8G8R8A8_UNORM` / `R10G10B10A2_UNORM` | **gamma 编码**（BT.709 OETF） |

⇒ **位数差异只是表象，语义差异才是本质。**

## 2. 为什么不建议把 SDR 改成 16F

### 2.1 换格式 = 换传递函数（决定性）

SDR 分支现在输出的是 gamma 编码值。把它直接放进 scRGB 交换链，Windows 会**按线性光解读**
⇒ 暗部被大幅抬升、整体发灰发白。

要改，就必须同时把 SDR 分支改成 `nits / 80.0` 线性输出——这不是"能力允许就开"的开关，
而是**整条输出管线语义的变更**。

### 2.2 SDR 白点调整会错位（内核注释已点明这个坑）

- UNORM 交换链：DWM **自动**施加 SDR 白点提升（用户可在系统里调 "SDR content brightness"）。
- FP16 scRGB 交换链：内容被视为 Advanced Color，DWM **不再**施加，改由应用自己映射白点。

两端都做 → 双重提亮；都不做 → 偏暗。内核那句
*"so DWM applies the Windows HDR SDR-white adjustment **exactly once**"*
就是这条不变量。**动 SDR 格式会直接破坏它。**

### 2.3 收益有限

- 8-bit 源：输出再精细，源本身只有 256 级，**色带不会消失**（台阶在源端）。
- 10-bit 源：已有 `R10G10B10A2_UNORM`（1024 级），再上 16F 边际收益很小。
- 真正消除 SDR 色带要靠**处理链路全程高位深 + 抖动**，不是最终交换链位数。

### 2.4 成本实打实

| 格式 | 字节/像素 |
|---|---|
| `B8G8R8A8_UNORM` | 4 |
| `R10G10B10A2_UNORM` | 4 |
| `R16G16B16A16_FLOAT` | **8（2 倍）** |

对本应用（**多路 4K 同时呈现**）是显存带宽翻倍。项目已有实测：4 路 4K 时主力卡
利用率 93.7%，跨适配器呈现本身已是瓶颈 —— 在这里加倍带宽换边际画质，不划算。

## 3. 那"能力分析"该用在哪？

现有 `PreferredOutputBitDepth` 是纯函数，确实**有**该补的探测，但不是"SDR 上 16 位"：

1. **10 位输出是否值得开**：目前 10-bit 源**无条件**建 `R10G10B10A2_UNORM`，
   没有探测桌面是否支持 10 位输出。在 8-bit 桌面上 DWM 会做一次降位（通常可接受，
   但不是零成本）。这里可以补 `BitsPerColor >= 10` 的探测来避免无谓转换。
2. **真想在 HDR 显示器上消除 SDR 色带**：正确做法不是"SDR 换 16 位格式"，
   而是**以 HDR 管线呈现 SDR 内容** —— 即让 SDR 内容也走 `ColorMode==2` 的线性
   scRGB 分支，把 SDR 白点显式映射为 80 nits（1.0）。Windows 支持这种做法，
   主流播放器也这么干；但它本质上是 `hdr = true` 的语义，属于"HDR 模式呈现 SDR"，
   而不是"SDR 用更高位数"。
   - 内核已具备基础：`sdrPeakNits_`/`SdrPaperWhiteNits`、托管侧
     `ToneMappingParameters` 已算 `paperWhiteNits`（HLG 参考 203 nits）。
   - ⚠ 若真做这条，必须**同时**关掉 DWM 那一次白点调整（因为此时应用自己做了），
     即维持"exactly once"。

## 4. 建议

| 项 | 建议 |
|---|---|
| SDR 输出改 16F | **不做**（契约 + 白点 + 收益/成本均不支持） |
| 补 10 位输出能力探测 | 可做，收益小但风险低 |
| "HDR 管线呈现 SDR 内容" | 可作为独立特性评估（解决的是真需求：HDR 屏上的 SDR 色带），但属于新功能，不是格式开关 |
| `OutputBitDepthForSource` 的 "exactly once" 不变量 | **必须保留**，动任何输出格式前先确认没有破坏它 |

> 附：本机默认观察到 `outputBitDepth=10 hdr=0`，按上述规则属于
> "SDR 路径 + 10-bit 源" 的正常选择，**不是** HDR 回退 —— 这条已在 `docs/19` §2 更正。
