# 上游 PR 草案 · HDR10+（ST 2094-40）动态元数据引导的色调映射

> **状态**：草案（2026-10-08）。配套规划：`3fp/docs/58-HDR10+动态元数据正确解码实现规划.zh.md`。
> **本文件不含真实 patch**——按裁决 D3，本轮只出规划与草案。真实 `.patch` 在实施阶段按 §提交拆分 产出为
> `3fp/patches/hdr10plus-dynamic-metadata.patch`。
> **提交前必做**：先搜上游仓库/issue（规划 §九 Q4），避免重复劳动；本机对上游无写权限，走 issue 探路 → PR。

---

## 一、PR 标题（建议）

```
3FP/HDR: apply ST 2094-40 (HDR10+) dynamic metadata to tone mapping
```

## 二、问题陈述（Issue 先行版）

### 现状

`HdrProcessor::ProcessFrame`（`FFF.Native/3FP/Hdr/HdrProcessor.cpp:299-317`）检测 `AV_FRAME_DATA_DYNAMIC_HDR_PLUS` 并把流分类为 `Hdr10Plus`、`processingPath = Hdr10PlusDynamic`，但**只消费 `params[0].maxscl`**：

```cpp
if (plus->num_windows > 0) {
    const auto& window = plus->params[0];
    auto maximum = 0.0;
    for (const auto& channel : window.maxscl)
        maximum = std::max(maximum, av_q2d(channel));
    const auto peak = ValidPeak(maximum * 10000.0);
    if (peak > 0.0f) { next.sourcePeakNits = peak; dynamicSourcePeak = true; }
}
```

其余影响映射的字段全部未读：

- `params[*].tone_mapping_flag` / `knee_point_x` / `knee_point_y`
- `params[*].num_bezier_curve_anchors` / `bezier_curve_anchors[15]`
- `params[*].num_distribution_maxrgb_percentiles` / `distribution_maxrgb[15]`
- `params[*].average_maxrgb` / `fraction_bright_pixels`
- `num_windows`（多窗口：目前只取 `params[0]`）
- `targeted_system_display_maximum_luminance`

并且渲染端**没有承载逐帧参数的通道**：像素着色器 cbuffer 只有 `SdrPeak/HdrPeak/PaperWhite/TargetPeak` 四个标量（`VideoRenderer.cpp:446-456`），`settings.hdrPeak` 对整段视频是一个常数（`:4619`）。

### 后果

1. **逐场景自适应完全缺失**。HDR10+ 的核心价值是"每个场景携带自己的映射曲线"，现实现退化为"HDR10 + 一个全局常数峰值"，与 HDR10 无实质差别。
2. **`processingPath` 上报名不副实**。`ProcessingPathName(Hdr10PlusDynamic)` 返回 `"HDR10+ metadata-guided display mapping"`（`:476`），但实际没有任何 metadata-guided 映射发生。
3. **缺元数据帧引起逐帧跳变**。`:321-326` 的 `!dynamicSourcePeak` 分支在本帧无 SEI 时回退静态峰值，若 SEI 非逐帧存在，`sourcePeakNits` 会来回跳，观感为忽明忽暗。

### 明确**不是**问题的地方（避免误报）

`maxscl × 10000` 的换算**是正确的**：`maxscl` 按 `libavutil/hdr_dynamic_metadata.h:150-157` 是 *"maximum of the color components of **linearized RGB** values"*，即已线性化、1.0 = 10000 cd/m² ⇒ 朴素线性比例即可，**不应**再套一次 ST 2084 反 EOTF。

---

## 三、改动范围

| 文件 | 性质 |
|---|---|
| `3FP/Hdr/HdrDynamicMetadata.h` | **新增**：定长元数据结构 |
| `3FP/Hdr/HdrMetadataParse.{h,cpp}` | **新增**：纯函数解析层（可脱离 GPU 单测） |
| `3FP/Hdr/HdrToneCurve.{h,cpp}` | **新增**：PCHIP 曲线重建 + LUT 生成 |
| `3FP/Hdr/HdrProcessor.{h,cpp}` | 改：挂载/填充元数据；hold 语义 |
| `3FP/Render/VideoRenderer.{h,cpp}` | 改：新 cbuffer、LUT 传递、`SetHdrMetadata` 动态化、transfer 修正 |
| `3FP/Render/ShaderBytecode.h` | 改（**生成物**，重生成） |
| `3FP/Api/FFF.Player.Api.h`、`PlayerApi.cpp` | 改：`PlayerApiVersion` ++、快照诊断字段 |
| `3FP/Core/PlayerSession.cpp` | 改：JSON 诊断输出 |

**ABI**：新增快照字段必须升 `PlayerApiVersion`。⚠ 上游有"改结构体布局不升版本"的前科（见 3FC 侧 `PATCHES.md` §〇），本 PR 不重复该错误。

---

## 四、设计要点（评审关注处）

### 4.1 为什么是独立 cbuffer 而不是扩 `ShaderSettings`

新增 `DynamicHdrSettings`（`register(b1)`），**不动** `ShaderSettings` 布局。这样：

> 无 HDR10+ 元数据的流，新 buffer 全零、`enabled == 0`，着色器走**逐字符未改**的原分支 ⇒ **像素输出逐位不变**。

这是可证明的强条件，也是本 PR 不破坏既有视频路径的依据。

### 4.2 曲线重建用 PCHIP 而非普通三次样条

ST 2094-40 给的是 `knee_point` + 若干 `bezier_curve_anchors`，**未强制接收端插值方法**。本 PR 选择 **PCHIP（单调三次 Hermite）**，理由：

- 色调映射函数必须**单调不减**；PCHIP 按构造保证，普通样条不保证（会出现反相/色带）。
- 无迭代、无需求解线性方程组，约 40 行。
- 端点行为可显式指定，天然满足 `(knee_x, knee_y) → (1, 1)`。

**这是本 PR 最需要评审的一项设计选择**，若上游有既定插值约定，应以约定为准。

### 4.3 曲线在 CPU 端求值为 LUT，不在着色器里跑 PCHIP

逐帧在 CPU 生成 256 项 1D LUT 传入 GPU。理由：

- CPU 实现可**脱离 GPU 单测**（单调性/端点/边界）。
- 避免每像素分支发散与分段查找。
- cbuffer 只需传 LUT 控制信息，不必塞 3 窗 × 15 锚点。

### 4.4 元数据 hold 语义 + 上界

真实 HDR10+ 流并非逐帧带 SEI。本帧无元数据时**沿用上一帧**（避免跳变），但**连续 60 帧**（约 1–2 s）无元数据即判定"该流不携带动态元数据"，退回静态路径。避免 seek 后长期持有陈旧元数据。

### 4.5 多窗口边界插值

`num_windows == 3` 时相邻窗口参数不同会在边界产生可见条带。在边界设过渡带（窗口跨度 5%），带内对映射参数线性插值。

### 4.6 `targeted_system_display_maximum_luminance` 的单位陷阱

头文件 L265-269：*"in units of **0.0001** candelas per square metre"* ⇒ **必须 × 10000** 才是 nits。该值优先于 `maxscl` 作为源峰值（它是规范定义的权威目标亮度）。

### 4.7 `distribution_maxrgb` 的字段命名陷阱

`AVHDRPlusPercentile`（L39-53）两个字段**命名与语义相反**：

- `uint8_t percentage` → 百分比（0..100）
- `AVRational percentile` → **线性化 maxRGB 值**（0..1）

按字段名想当然会取错量，且两者都在合法数值域内、不会立刻报错。

---

## 五、同批必须带的独立修正（**单独成 commit**）

### 5.1 transfer 传递函数由像素 trc 决定

`VideoRenderer.cpp:4609-4616` 目前除 Dolby Vision 外，按 **metadata 分类**推断像素解码传递函数。对"HLG 像素 + 元数据分类非 HLG"的组合（规范外但真实存在）会按 PQ 解码：`3fp/docs/21` 推演 0.75 码值处亮度高估约 **7.7×**、0.90 处约 **18×**，并伴随首帧/后续帧传递函数跳变。

修法（`3fp/docs/22` 方案 A）：`transfer` 改由 `frame->color_trc` 决定，DV 兼容位优先级保留。

**依据**：ST 2094 系动态元数据定义在"**不改变像素编码**"的前提下 —— 元数据到达不应改变像素解码方式。

⚠ **这与 HDR10+ 功能无耦合，必须单独成 commit**，便于上游独立评估与单独取舍。

---

## 六、验证证据（PR 中需附）

| # | 项 | 方法 | 期望 |
|---|---|---|---|
| 1 | **无元数据流逐位不变** | FATE 无 SEI HDR10 + 自制样本，`read_pixel_probe` 全画面哈希 | 改动前后**完全相等** |
| 2 | HDR10+ 解析正确 | 自研探针 vs `ffprobe` 原始 T.35 **独立对拍** | 字段级一致（不得自证） |
| 3 | 逐帧自适应生效 | 探针打印 `hdrPeak` **时间序列** | 随场景变化，非恒定 |
| 4 | 曲线单调性 | `HdrToneCurve` 单测 | 单调、`f(1)=1`、`f(knee_x)=knee_y` |
| 5 | 边界降级 | 构造 `num_windows=2/4`、`tone_mapping_flag=0`、锚点非单调、全零 maxscl | 走降级，无 UB |
| 6 | 开关连续性 | `enabled` 0↔1 切换 | 无可见跳变 |
| 7 | transfer 修正 | FATE HDR10+（PQ）/ FATE Vivid（HLG） | HDR10+ 逐位不变；Vivid 过曝消除（中心像素线性 40.59 → 5.74，见 3FC 侧 `3fp/docs/archive/status-log-2026-09`） |
| 8 | 性能 | 4K/60 高码率 | 相对基线退化 < 3% |

**样本来源**（FFmpeg 官方 FATE 测试库，上游可直接取用）：

- `hevc/hdr10_plus_h265_sample.hevc` — 1280×720 HEVC 10bit PQ，含帧级 ST 2094-40 SEI
- `hevc/hdr_vivid_h265_sample.hevc` — 3840×2160 HEVC 10bit HLG，含 CUVA 005.1-2021 Vivid SEI（用于第 7 项对照）

---

## 七、提交拆分

| commit | 内容 | 行为变化 |
|---|---|---|
| 1 | 解析层 + 诊断字段（`HdrMetadataParse`、快照字段） | **无** |
| 2 | **transfer 修正**（§5.1，独立缺陷） | 有 |
| 3 | PCHIP + LUT（纯新增，未接线） | **无** |
| 4 | SDR 路径接线（核心功能） | 有 |
| 5 | MapToHdr：`SetHdrMetadata` 逐帧化 + 限流 | 有 |
| 6 | `ShaderBytecode.h` 重生成 | 无（机械） |

> commit 1 与 3 **不改变任何行为**，可独立合并；commit 2 是独立缺陷修正；只有 4/5 是本 PR 的功能主体。这样上游可以按需拆分取舍。

---

## 八、非目标（PR 中明确声明不做）

- **不实现 Dolby Vision RPU 应用**（需 Dolby 授权，维持现有 fallback）。
- **不实现 ST 2094-30（HDR Vivid）曲线**（数据面留位，后续单独 PR）。
- **不实现椭圆像素选择器**（local dimming）。仅读入诊断，不参与映射。
- **不自解析 SEI、不改 FFmpeg**（继续消费解码器 frame side data，符合"元数据不改变像素编码"前提）。
- **不输出 HDR10+ 信令**（播放器不产出）。

---

## 九、已知风险（PR 中如实声明）

| # | 风险 | 说明 |
|---|---|---|
| R1 | PCHIP 是**我们的选择**，规范未强制 | 需上游确认是否有既定插值约定 |
| R2 | 多窗口边界插值宽度（5%）是经验值 | 可能存在更优取值，需实机观感确认 |
| R3 | hold 上界 60 帧是经验值 | 依赖流特性，可后续调整 |
| R4 | 多窗口片元着色器 branch divergence | 已用 LUT 缓解，仍需 4K 实测 |
| R5 | `SetHDRMetaData` 逐帧调用对 DWM 的影响 | 已限流（>2% 变更才提交），需实机确认无闪烁 |
