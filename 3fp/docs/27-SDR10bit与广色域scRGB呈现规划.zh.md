# 27 - SDR 10bit 与广色域的 scRGB 呈现规划

- 日期：2026-09-29
- 类型：特性规划（PR 前置）
- 状态：**代码已实施**（内核 + 宿主 + 测试 + 探针）；**待重编译内核与实机验证**（本机无 MSVC/Windows SDK）。实施偏差见 §11
- 关联：`13-HDR与scRGB完整验证（2026-09-26）`、`06-图片模式接线审查（2026-09-24）` §1.4、`00-图片模式设计规格` §9、`docs/20-SDR输出位深策略与内核实现对比.zh.md`

---

## 1. 背景与问题

### 1.1 现状输出链路

| 源类型 | Auto 决策（HDR 显示器） | 交换链 | 色域处理 |
|---|---|---|---|
| HDR（PQ/HLG） | MapToHdr | `R16G16B16A16_FLOAT` + `SetColorSpace1(G10_NONE_P709)` | 2020/P3→709 线性，DWM tonemap |
| SDR 广色域（2020/P3 primaries） | MapToHdr（`IsWideGamutSource()` 放行） | 同上 | **shader 未转换（P3 色相错误）**，见 §6.2 |
| SDR 10bit（BT.709） | MapToSdr | `R10G10B10A2_UNORM`（gamma 域，无显式声明） | 709，无压缩 |
| SDR 8bit | MapToSdr | `B8G8R8A8_UNORM` | 709，无压缩 |

关键代码：

- 内核最终仲裁：`VideoRenderer.cpp:2185-2202`（`EnsureSwapChain`）
  `wantsHdrPath = sourceHdr || wideGamut`，再与 `OutputSupportsHdr()` 相与。
- SDR 位深策略：`VideoRenderer.cpp:93-102`（`OutputBitDepthForSource`），SDR 上限 10bit。
- SDR 交换链刻意不声明：`VideoRenderer.cpp:2301-2306`（DWM "SDR 白点 exactly once" 不变量）。

### 1.2 要解决的问题

SDR 源为 **10bit** 或 **广色域** 时，当前呈现存在两类压缩/损失：

1. **色域压缩**：SDR 链 shader 把 BT.2020 域折叠进 BT.709（`VideoRenderer.cpp:592`
   `rgb=ToBt709(To709(ToLinear709(rgb)))`）；P3 primaries 甚至不做任何矩阵变换。
   8/10bit gamma 域 BT.709 交换链物理上放不下 P3/2020 色度。
2. **二次处理与契约混用**：10bit gamma 域 R10A2 链在 DWM 合成时被隐式处理，
   字幕/OSD 在 gamma 域混合，与 HDR 链（FP16 线性混合）行为不一致。

### 1.3 已有基础（本特性的"半成品"现状）

- **广色域 SDR 源 + MapToHdr 已走 scRGB**：`sourceWideGamut_` 认 BT2020/SMPTE431/432
  （`VideoRenderer.cpp:1961-1963`），门槛三处（SetWindow :1853 / SetColorMode :1928 /
  EnsureSwapChain :2185）均已放行。**但仅当用户手选 MapToHdr 或显示器被判 HDR 时生效**，
  且 P3 色相错误（§6.2）。
- **shader 对 SDR 源的 scRGB 输出数学已通**（`VideoRenderer.cpp:595-600`）：
  `out = ToLinear709(rgb) × PaperWhite / 80`（2020 源先过 `To709` 纯矩阵，FP16 承载负分量）。
- **字幕/OSD/backdrop 已自适应 FP16 线性链**（overlay 纹理 :3250/:3275、
  `PaperWhite/80` 刻度 :707-714），无耦合改动。
- **HDR↔SDR 链切换路径完备**：SDR→HDR 走 `ResizeBuffers`+`SetColorSpace1`
  （:2358-2381），离开 HDR 整链重建（:2330-2342）。
- **位深决策天然正确**：`PreferredOutputBitDepth(sourceBitDepth, hdr)` 的 `hdr` 参数
  语义是"呈现链是否 scRGB"而非"源是否 HDR"（:2199/:2217），`actualMode_` 进入
  MapToHdr 即得 16bit——**位深函数本身无需修改**。

## 2. 目标与非目标

### 2.1 目标

1. 新增用户设置：**SDR 源（位深 >8bit 或广色域 primaries）在 Advanced Color
   激活的显示器上，优先走 16bit scRGB FP16 呈现**，避免 R10A2 gamma 链与
   BT.709 色域折叠。
2. SDR 源走 scRGB 链时，**不再发送伪 HDR10 元数据**（现状会发出
   "2020 primaries + 100nits 峰值"的假元数据，见 §6.3）。
3. 修正 **P3 primaries 在视频路径的色相错误**（`Source2020` 开关不认 P3，
   现状 scRGB 链上 P3 被按 709 解释，特性完整性的前提）。
4. 全程可回退：设置三态，`Never` 恢复完全现状；显示器/链路拒绝时回落现有 SDR 链。

### 2.2 非目标（本 PR 明确不做）

1. **SDR 桌面（AC 未激活）强走 scRGB**：`OutputSupportsHdr()` 仍为唯一显示器门控。
   SDR 桌面上 FP16+G10 显式契约行为未验证且破坏 `docs/20` 的 exactly-once 论证。
   "绕过显示器门控"的实验需求由既有 `ForceHdrOutput` 管道承担（已通、未接 UI）。
2. 不改 `ColorModeSetting` 枚举与 UI 三档（Auto/SDR/HDR）语义，不加
   TransportBar 档位。
3. 不新增 `FFF3FPColorMode` 枚举值：SDR 源走 scRGB 复用 `actualMode_ = MapToHdr`
   承载（shader `ColorMode==2` 数学、字幕自适应、位深决策全部现成）。
4. 已开会话的热切换：策略为会话级配置，设置保存后**新会话生效**（与
   HardwareDecode 等会话级选项口径一致）。
5. 静态图专项验证：链路天然共用（同一 `VideoRenderer`），仅随视频用例回归，
   不单独扩图片验证矩阵（13 号文档样本集复用）。

## 3. 技术方案总览

```
[C# 设置] SdrScRgbMode: 0=Never(旧行为) / 1=Auto(默认: AC 显示器上启用)
    │  EngineSessionOptions.SdrScRgbMode → Fff3FpConfiguration 尾部追加 (api 15→16)
    ▼
[内核] VideoRenderer 新增策略位 sdrScRgbAllowed_
    │
    ├─ 三处门槛扩展 (SetWindow:1853 / SetColorMode:1928 / EnsureSwapChain:2185):
    │     wantsHdrPath = sourceHdr || wideGamut
    │                  || (sdrScRgbAllowed_ && sourceBitDepth_ > 8)
    │
    ├─ 显示器门控不变: OutputSupportsHdr()（AC 激活才放行，SDR 桌面回退现状）
    │
    ├─ actualMode_ = MapToHdr →
    │     位深 16 (PreferredOutputBitDepth 现有逻辑) → FP16 scRGB 链
    │     shader ColorMode==2 + Transfer==0 → ToLinear709×PaperWhite/80（现成）
    │
    ├─ SetHdrMetadata SDR 分支（新增）: 源为 SDR → SetHDRMetaData(TYPE_NONE)
    │
    └─ 拒绝/回退: 复用现有 hdrSwapChainRejected_ + HDR→SDR 整链重建
```

### 3.1 为什么复用 MapToHdr 承载而不新增 ColorMode 枚举值

| 维度 | 复用 MapToHdr（选定） | 新增 ColorMode=3 |
|---|---|---|
| shader 分支 | `ColorMode==2` 现成 | 590/591/596/706 四处字面比较新增分支 |
| 位深/交换链 | 现成（hdr 参数驱动 16bit） | 需要新判定层 |
| 字幕/OSD | 已按 `actualMode_` 自适应 | 同左（无差异） |
| ABI/守卫 | 零改动 | 三处 `mode > MapToHdr` 守卫 + C# 枚举 + 序列化 |
| 语义 | "呈现链走 HDR 管线"与源类型解耦，与 Windows AC 语义一致 | 引入"第四种模式"概念，UI/文档解释成本高 |

`FFF3FpColorMode` 保持 3 值不变，是本方案 ABI 与逻辑改动最小的核心决策。

## 4. 详细设计

### 4.1 内核：策略位与门槛扩展

**配置通道**（会话级，仿 `ForceHdrOutput` 先例）：

- `3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h`：`FFF3FPConfiguration` 尾部追加
  `std::uint32_t SdrScRgbMode; // 0=Never, 1=Auto(AC 显示器上启用)`，API 版本
  宏 15→16（与 `Fff3FpNative.cs` 的 `ConfigVersion` 严格同步，两侧严格相等校验）。
- `PlayerApi.cpp` / `PlayerSession.cpp`：解析透传至 `VideoRenderer`
  （setter 或经既有会话配置路径）。
- `VideoRenderer.h/.cpp`：成员 `sdrScRgbAllowed_`。

**门槛扩展**（三处同步，保持现有"最终仲裁者在 EnsureSwapChain"结构）：

```cpp
// EnsureSwapChain (VideoRenderer.cpp:2185 附近)，SetWindow/SetColorMode 同型
const auto sourceHdr = hdrProcessor_.IsHdrSource();
const auto wideGamut = IsWideGamutSource();
const auto sdrHighPrecision = sdrScRgbAllowed_ && sourceBitDepth_ > 8;
const auto wantsHdrPath = sourceHdr || wideGamut || sdrHighPrecision;
const auto nextMode = wantsHdrPath && OutputSupportsHdr() ?
    FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
```

- `fallbackReason_` 文案：`wantsHdrPath` 命中但显示器不支持时，区分两条
  （"Advanced Color 未激活，SDR 高精度呈现被跳过" / 现有 HDR 文案），便于探针判读。
- `RawHdrAsSdr` 不受影响：三处门槛均在 `requestedMode_ == MapToHdr` 分支内。
- SDR 8bit 源不受影响：`sourceBitDepth_ > 8` 不命中，维持 BGRA8 隐式契约。

### 4.2 内核：HDR10 元数据的 SDR 分支

现状：`SetHdrMetadata()`（:3979-3984）只要 `swapHdr_` 为真就无条件发 HDR10；
`BuildDxgiHdr10Metadata`（`HdrProcessor.cpp:419-449`）对 SDR 源会写出
**2020 primaries + MaxMastering=100 + MaxCLL=100** 的假元数据
（`sourcePeakNits` 默认 100，`hasPrimaries=false` 不被检查）。

修改：`SetHdrMetadata()` 增加 SDR 源分支——

```cpp
if (hdrProcessor_.State().format == FFF3FPHdrFormat::Sdr) {
    swapChain_->SetHDRMetaData(DXGI_HDR_METADATA_TYPE_NONE, 0, nullptr);
    return;
}
```

理由：SDR 源无 mastering/CLL 语义，发 `TYPE_NONE` 让 DWM 用系统默认映射
（scRGB 内容 ≤ PaperWhite/80 ≈ 2.5 线性，常见 600–1000nits 显示器下安全）；
比发假 2020 元数据更诚实且避免误导 DWM tone mapper。
**风险项 R2 实测覆盖**（§7）。

### 4.3 内核：P3 primaries 修正（随本 PR，独立 commit）

问题：`IsRec2020`（:165-168）不认 `AVCOL_PRI_SMPTE431/432`，`source2020`
（:4002）→ `Source2020=0` → scRGB 输出不做 gamut 矩阵 → P3 线性值被 DWM
按 709 基色错读（色相/饱和度错误）。此错误在**现状 MapToHdr + P3 视频**即存在。

方案：`Source2020` 扩为三态（cbuffer `uint Source2020`：0=709、1=2020、2=P3）：

1. `PlayerSession.cpp` 解码侧：`sourceWideGamut` 判定保持；`source2020` 判定
   （`VideoRenderer.cpp:4002`）改为 `GamutKind(frame)`：
   `BT2020(primaries/colorspace) → 1`，`SMPTE431/432 → 2`，否则 0。
2. shader（:599）：

```hlsl
float3 rec709Nits = Gamut==0 ? nits : (Gamut==1 ? To709(nits) : ToP3To709(nits));
```

3. 新增 P3-D65→709 线性矩阵（**纯矩阵，无 saturate**，负分量由 FP16 承载），
   参考数值（实现时用 `EvaluateVideoColorTransform` 数值对拍验证）：

```
[  1.224810, -0.224970, -0.000025 ]
[ -0.042043,  1.042084,  0.000018 ]
[ -0.019642, -0.078649,  1.098527 ]
```

4. CPU 参考实现 `EvaluateVideoColorTransform`（:1549-1594）同步三态；
   同时处理其既有差异（:1575-1577 对 scRGB 输出 `std::max(0,·)` 而 shader 不钳——
   探针对拍场景保留差异并注释，或统一为不钳，二选一，实现时定）。
5. **SDR 折叠路径不动**（:592 `ToBt709(To709(...))` 仅 709/2020 两种输入，
   P3 在 MapToSdr 下仍按现状走 0 态——该路径的 P3→709 折叠属于既有行为，
   不在本 PR 扩大范围）。

### 4.4 C# 宿主：设置与管道

| 文件 | 改动 |
|---|---|
| `src/3FCompare.Core/Settings/AppSettings.cs` | 新增 `public int SdrScRgbMode { get; set; } = 1;`（int 模式惯例，见 CompareAlign 注释）；`Normalize()` 增加 `Math.Clamp(SdrScRgbMode, 0, 1)`。**不改 `ColorModeSetting` 上界 3**（L45） |
| `src/3FCompare.Core/Backend/IPlayerEngine.cs` | `EngineSessionOptions` 新字段 `public int SdrScRgbMode { get; init; } = 0;`（DTO 默认中立 0=Never，宿主显式传值，仿 `ForceHdrOutput` 注释口径） |
| `src/3FCompare.Core/Backend/Interop/Fff3FpNative.cs` | `Fff3FpConfiguration` 尾部追加 `public uint SdrScRgbMode;`；`ConfigVersion` 15→16（严格相等校验） |
| `src/3FCompare.Core/Backend/Fff3FpEngine.cs` | `CreateSession` 装配 `SdrScRgbMode`（L33-52 一带） |
| `src/3FCompare/Services/PlaybackCoordinator.cs` | L666-677 会话创建：`SdrScRgbMode = _settings.SdrScRgbMode` |
| `src/3FCompare/Views/SettingsWindow.cs` | Status_Color 节新增选项（ComboBox 或开关）；**两处深拷贝同步**（L89-92 与 L607-611），漏一处 = 设置存不住 |
| `src/3FCompare/Localization/LanguageManager.cs` | 新文案键，中英两表同加（L255/L499 旁） |
| `src/3FCompare/MainWindow.axaml.cs` | 设置保存回写（L2681 附近）；不热切（§2.2-4） |

设置语义文案（草案）：
- `0 = 关闭（SDR 始终走传统 10bit/8bit 链）`
- `1 = 自动（默认）：显示器开启 HDR（Advanced Color）时，SDR 10bit/广色域内容经 scRGB 呈现`

### 4.5 `ToneMappingParameters` 是否需要新分支

不需要。SDR 源走 MapToHdr 时沿用现有 MapToHdr 分支：`paperWhite=200` 固定、
`sdrPeak=clamp(displayPeak*0.2,100,300)`。SDR 白 = 200/80 = 2.5 线性，
与 HDR 链上字幕白（`PaperWhite/80`，:707-714）同刻度，链内一致。
白电平策略（是否为 SDR-scRGB 单独设 80/160）列为实机 A/B 验证项（§7 V6），
若有结论由后续小 PR 调整，不阻塞本特性。

## 5. 改动文件清单（汇总）

**内核（`3fp/kernel/FFF.Native/`）**
1. `Api/FFF.Player.Api.h` — 配置字段 + 版本 16
2. `Api/PlayerApi.cpp` — 解析透传
3. `Core/PlayerSession.cpp/.h` — 策略位传递
4. `Render/VideoRenderer.cpp/.h` — 策略位成员、三处门槛、fallbackReason、
   `SetHdrMetadata` SDR 分支、gamut 三态 + P3 矩阵 + CPU 对拍同步
5. `Hdr/HdrProcessor.cpp` —（可选）`BuildDxgiHdr10Metadata` 防 SDR 源误用的注释/断言

**宿主（`src/`）**
6-13. 见 §4.4 表

**工具/测试**
14. `3fp/tools/verify_image_mode.cpp` — 新增 `--sdr-scrgb` 开关（配置
    `SdrScRgbMode=1`），`[open]/[after-play]` 段继续输出 `actual/outBits`
15. `tests/3FCompare.Core.Tests/` — AppSettingsNormalizeTests（钳位）、
    EngineDtoContractTests（DTO 默认 0）、ColorModeHelperTests（不受影响，
    回归确认）
16. `CHANGELOG.md` + 本文档 + 实机验证报告（实施后编号 28）

## 6. 关键技术论证

### 6.1 "10bit 源走 scRGB"的收益口径（避免过度承诺）

- **色域承载**：FP16 允许负分量与 >1.0 线性值，2020/P3 基色无需折叠——这是对
  广色域源的决定性收益。
- **10bit 709 源**：收益是**契约统一**（线性光链、字幕线性混合、与 HDR 内容
  同一呈现路径）+ 消除 R10A2 gamma 域在 DWM 合成中的隐式二次处理；
  量化精度上 FP16 线性与 10bit gamma 域各有区间优劣（FP16 暗部 subnormal 极优、
  中灰 ~11bit 有效尾数与 gamma 域 10bit 相当），**不承诺可见精度提升**。
- 文档与 PR 描述以"避免色彩压缩 + 呈现契约统一"为准绳。

### 6.2 P3 修正是特性完整性的前提

广色域定义含 P3（`sourceWideGamut_` 认 SMPTE431/432）。不修 §4.3，
"P3 源优先 scRGB"只会把现状的"折叠进 709"变成"P3 被错读为 709"，
与目标相悖。故 P3 三态修正并入同一 PR（独立 commit，便于评审与回滚）。

### 6.3 元数据诚实性

SDR 源发 `TYPE_NONE`（§4.2）后，DWM 对无元数据 scRGB 链使用系统默认映射；
内容线性值上限 ≈ PaperWhite/80 = 2.5（200nits 名义），远低于常见显示器能力。
若实测发现系统默认映射对 2.5 的超白处理不佳（过饱和/裁剪），备选方案是
发"源 primaries + MaxMastering=PaperWhite"的保守真元数据（需给
`BuildDxgiHdr10Metadata` 加 SDR 分支），作为 V7 验证的决策点。

### 6.4 与 docs/20 "exactly-once" 论证的关系

`docs/20` 反对的是"SDR 隐式链上叠 FP16"导致 DWM 白点双重调整。本特性走
**显式 AC 契约**（`SetColorSpace1(G10_NONE_P709)`）：窗口进入 Advanced Color
合成，1.0=80nits 语义由 DWM 一次映射，不与隐式 SDR 路径叠加；离开时整链重建
回隐式契约（既有路径 :2330-2342）。论证自洽。

## 7. 测试与验证计划

### 7.1 单测（`tests/3FCompare.Core.Tests/`）

- AppSettingsNormalizeTests：`SdrScRgbMode=-5/99 → 0/1 钳位`；合法值保留。
- EngineDtoContractTests：`options.SdrScRgbMode == 0`（DTO 默认中立）。
- ColorModeHelperTests：回归确认不受新设置影响。
- ToneMappingParametersTests：回归（无新分支）。

### 7.2 内核探针（`verify_image_mode.cpp --sdr-scrgb`）

判据沿用 13 号文档方法论：`outBits == 16` 且 `actual == MapToHdr` = scRGB 生效；
`outBits == 10/8` 且 fallbackReason 有值 = 正确回退。

### 7.3 实机矩阵（HDR 显示器 + SDR 显示器各一台）

| # | 用例 | 期望 |
|---|---|---|
| V1 | 10bit BT.709 SDR（HEVC Main10）× mode=1 × HDR 显示器 | `actual=MapToHdr, outBits=16`，无 fallbackReason |
| V2 | 同源 × mode=0 | 回归现状 `MapToSdr/10` |
| V3 | BT.2020 广色域 SDR 源 × mode=1 | 16bit 链 + 色块对比 R10A2 基线截图：饱和色不再折叠 |
| V4 | P3 视频（SMPTE432）× mode=1 | 16bit 链 + 修正后色相与参考渲染器一致 |
| V5 | mode=1 × SDR 显示器 | `MapToSdr/10`，fallbackReason 指明 AC 未激活 |
| V6 | 白电平 A/B：PaperWhite 80 vs 200（V1 源） | 主观亮度结论落验证报告（§4.5） |
| V7 | V1 场景元数据观察（DWM tonemap 行为） | 决定 `TYPE_NONE` vs 保守真元数据（§6.3） |
| V8 | 静态图回归（13 号样本集：P3 PNG / PQ AVIF / sRGB PNG） | 既有通过用例不回退 |
| V9 | 字幕叠加（SRT/MKS）× V1 场景 | 字幕白与画面白同刻度、无线性混合发灰 |
| V10 | 运行中关闭系统 HDR | 整链重建回 SDR，无 device-lost（既有路径） |
| V11 | mode=1 × 8bit 709 SDR 源 | 维持 8bit BGRA8 隐式链（不误伤） |

### 7.4 回归门

- `3fp/tools/run_host_tests.sh` 全绿；
- 内核重编译双镜像一致（`tools/build_kernel_manual.py`，`x64/Release` 与
  `build/` `cmp` 逐字节）；
- `dump_capture.py` 跑 30 分钟长播无 FATAL 转储。

## 8. 风险与缓解

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| R1 | SDR 内容整体亮度观感变化（200nits 名义 × DWM 映射，与用户"SDR 内容亮度"滑杆叠加） | 中 | 三态设置可回退；V6 A/B 定刻度；必要时后续 PR 为 SDR-scRGB 单独定纸白 |
| R2 | 无 HDR10 元数据时 DWM 对 ≤2.5 超白的映射行为未确证 | 中 | V7 实测；备选保守真元数据方案已备（§6.3） |
| R3 | ABI 版本 16 两侧不同步 → 会话创建直接失败 | 低 | 严格相等校验本就是防错设计；单测 + 探针首跑即暴露 |
| R4 | P3 修正改变现状 MapToHdr P3 图片的呈现（变正确但属行为变化） | 低 | CHANGELOG 明示；V4/V8 前后对比留档 |
| R5 | 三处门槛不同步导致 UI 显示与实际呈现漂移 | 低 | 以 EnsureSwapChain 为最终仲裁（既有结构），SetWindow/SetColorMode 仅做早判；探针读 `actual` |
| R6 | NativeAOT 序列化/旧配置兼容 | 低 | int 字段 + Normalize 钳位，缺字段即默认值（既有约定，无需递增 CurrentVersion） |

## 9. 实施步骤与 PR 组织

单 PR、四 commit（上游交付时按惯例 squash 为一个提交）：

1. **C1 内核主特性**：配置字段（api 16）+ 策略位 + 三处门槛 +
   `SetHdrMetadata` SDR 分支 + fallbackReason + 探针开关。
2. **C2 P3 三态修正**：gamut 判定 + shader 矩阵 + CPU 对拍（§4.3）。
3. **C3 宿主**：设置/管道/UI/文案（§4.4 表 6-13）。
4. **C4 测试与文档**：单测 + 本文档状态更新 + 实机验证报告（28 号）+
   CHANGELOG `[未发布]` 条目。

PR 正文按 `3fp/docs/05` 惯例：概要 / 内核侧改动表 / 宿主侧改动 /
行为不变性声明（`FFF3FpColorMode` 枚举与既有导出签名不变，仅配置结构尾部
追加 + 版本 16）/ 如何验证（探针命令 + V 矩阵摘录）/
"不在本 PR 夹带"（SDR 桌面强走、TransportBar 档位、纸白独立刻度）。

## 10. 兼容性与回滚

- 设置默认 `Auto=1`：仅改变 **AC 激活显示器**上 SDR 10bit/广色域源的行为；
  SDR 桌面、8bit 源、`RawHdrAsSdr` 用户行为零变化。
- 手改配置 `SdrScRgbMode=0` 即完全回滚呈现行为；内核层面 C1/C2 可分别 revert。
- 旧版宿主 + 新内核：`Size` 校验使多余字段被忽略？——**否**，结构尺寸严格校验
  下旧宿主传旧 Size 会被拒；因此**本特性要求宿主与内核同仓同发**（既有惯例，
  ConfigVersion 严格相等校验即为该约束服务）。

---

## 11. 实施记录（2026-09-29）

### 11.1 已落地改动

**内核（`3fp/kernel/FFF.Native/`）**

| 文件 | 改动 |
|---|---|
| `Api/FFF.Player.Api.h` | `FFF3FPConfiguration` 尾部追加 `sdrScRgbMode`（带语义注释）；`FFF3FPColorTransform.source2020` 注明三态语义 |
| `Api/PlayerApi.cpp` | `PlayerApiVersion` 15 → 16；`FFF3FP_Create` 校验 `sdrScRgbMode > 1` |
| `FFF.Native.rc` | `VER_API` 15 → 16 |
| `Core/PlayerSession.cpp` | 构造时 `videoRenderer_.SetSdrScRgbMode(...)`（**先于** `SetColorMode`，后者要用它算门控） |
| `Render/VideoRenderer.h/.cpp` | 新增 `SetSdrScRgbMode()` + 成员 `sdrScRgbMode_`；新增 `WantsScRgbPresentationPath()`；三处门控改为调用它；`SetHdrMetadata()` 增加 SDR 源 `TYPE_NONE` 分支；`ConvertP3To709()` + `P3To709` HLSL 矩阵 + `ResolveSourceGamut()`（三态，受 `kShaderGamutTristate` 保护）；`ShaderSettings/CachedVideoSettings.source2020` → `gamut`；`EvaluateVideoColorTransform` 支持 `source2020 == 2` 并放宽校验到 `> 2` |

**宿主（`src/`）**

| 文件 | 改动 |
|---|---|
| `Backend/Interop/Fff3FpNative.cs` | 结构体尾部追加 `SdrScRgbMode` |
| `Backend/Fff3FpEngine.cs` | `ConfigVersion` 15 → 16；装配并钳位到 [0,1] |
| `Backend/IPlayerEngine.cs` | `EngineSessionOptions.SdrScRgbMode`（DTO 默认 0 = 中立） |
| `Settings/AppSettings.cs` | 属性（默认 1）+ `Normalize()` 钳位 |
| `Services/PlaybackCoordinator.cs` | 会话创建传值 |
| `Views/SettingsWindow.cs` | Status_Color 节新增下拉框（**两处**深拷贝 + changed 链 + 读回钳位均已同步） |
| `Localization/LanguageManager.cs` | 4 个新键，中英双表 |
| `MainWindow.axaml.cs` | `CopySettings` 同步新字段（不做热切换） |

**工具 / 测试 / 文档**

- `3fp/tools/verify_image_mode.cpp`：新增 `--sdr-scrgb` 开关并打印选项。
- 单测：`AppSettingsNormalizeTests`（`SdrScRgbMode=99 → 1`）、`EngineDtoContractTests`（DTO 默认 0）。
- `tools/generate_shader_bytecode.py`：新增，从 `VideoRenderer.cpp` 内嵌 HLSL 重新生成 `ShaderBytecode.h`。
- `CHANGELOG.md`：`[未发布]` 条目。

### 11.2 与规划的偏差（以及为什么）

1. **三处门控抽成一个函数**：规划只说"扩展三处条件"，实现时改为统一的
   `WantsScRgbPresentationPath()`。三处各写一遍 `sourceHdr || wideGamut || (policy && bits>8)`
   迟早漂移（现状三处已经是复制粘贴），抽函数是顺手的 correctness 改进。
2. **P3 三态未接线（关键）**：实施中发现生产 shader 是**预编译 DXBC**（`ShaderBytecode.h`，
   由 `CreatePixelShader(FFFPixelShaderBytecode, ...)` 直接消费），改 HLSL 源码字符串
   **不会**改变运行时行为。若现在就向旧字节码发 `Gamut=2`，旧 shader 的
   `Source2020 != 0` 判定会把 P3 当 Rec.2020 处理（**比现状更差**）。因此：
   - HLSL 与 CPU 侧三态逻辑已写好（源码与未来字节码同源）；
   - `kShaderGamutTristate = false` 使 P3 源维持现状行为，不引入回归；
   - **启用步骤**：① 在有 Windows SDK 的机器跑
     `python tools/generate_shader_bytecode.py`（必要时 `--fxc <路径>`）；
     ② 把 `kShaderGamutTristate` 翻为 `true`；③ 重编译内核，跑 V4/V8。
3. ~~**未做编译与实机验证**~~（**已被后续实施推翻**：随后在同一机器确认了
   MSVC 14.51 + SDK 10.0.28000 工具链，内核/宿主均已编译验证、V1/V11/V7 已实测，
   见 §11.5 与 STATUS.md——此条仅保留作时间线记录）。
4. **UI 只加在设置窗口**：未动 TransportBar 三档（符合 §2.2 非目标）；
   也未新增 `ColorModeSetting` 成员（符合 §3.1 决策 D1）。

### 11.3 下一步（按序）

> **先确认构建源**：3FCompare 真正跑的内核是 `3fp/kernel/FFF.Native`（被 `.gitignore`
> 忽略的本地副本，`STATUS.md` 记录其产物为 776192 B，含 `IsWideGamutSource` 等
> 3FCompare 私有扩展）；`third_party/fff_project` 是**提上游 PR 用的 git 仓库**，
> 且**不含**广色域门禁（`IsWideGamutSource` 缺失）。**不要**用 third_party 的
> 构建产物当应用内核——否则本特性的"广色域"一半直接失效。

**一条命令完成内核侧（顺序不能反）**：

```
python tools/build_sdr_scrgb.py --sync
```

它依次做：① `generate_shader_bytecode.py` 用 fxc 重新生成 `ShaderBytecode.h`；
② 把 `kShaderGamutTristate` 翻为 `true`（P3 接线）；③ `build_kernel_3fp.py`
以 vcxproj 的 Release|x64 配置编译 `3fp/kernel`（产物 `x64/Release_sdr/`）；
④ `--sync` 把 DLL 复制到 `src/3FCompare/bin/{Debug,Release}` 与 `3fp/build`
（各留 `.bak` 并 touch 保证 mtime > 源码）。任一步失败即停，不会留下半成品 DLL。

拆开手动执行（等价）：

```
python tools/generate_shader_bytecode.py        # 需 fxc（自动探测最新 Windows SDK）
python tools/build_kernel_3fp.py                # 需要时才加 --config <名字>
```

**宿主与验证**：

0. **ABI 冒烟检查**（改了 `FFF3FPConfiguration` 就必须跑；托管结构布局漂移不会编译报错，
   只表现为"所有会话创建 InvalidArgument"）：
   ```
   python tools/check_kernel_config_abi.py
   ```
   实测：`sizeof = 80`、`GetApiVersion = 16`、`sdrScRgbMode=0/1 → Success`、`=2 → InvalidArgument`。
1. `dotnet build src/3FCompare/3FCompare.csproj -v q --nologo`，再 `bash 3fp/tools/run_host_tests.sh`
   （重点：`AppSettingsNormalizeTests`、`EngineDtoContractTests`、
   `LanguageManagerKeysTests` 双表键一致）。
2. 内核探针：`verify_image_mode <样本> --hdr --sdr-scrgb`，判据 `outBits == 16`。
3. 跑 §7.3 的 V1–V11；先判 `outBits=16 / actual=MapToHdr`，再看白电平（V6/V7）。
4. 白电平若需独立刻度，另开小 PR 调 `ToneMappingParameters`，不阻塞本特性。
5. P3 验证走 V4 + V8（P3 PNG 静态图回归）。

### 11.4 实机验证记录（2026-09-29，探针 `verify_image_mode`）

环境：**非 HDR 桌面**（系统 Advanced Color 未开启）也能跑通部分判据，因为 scRGB 链
的放行只取决于 `OutputSupportsHdr()`——本次机器上它为真（显示器报 G2084），
所以 `outBits=16` 判据可验证；亮度观感类（V6/V7）仍需真 HDR 环境主观确认。

| # | 样本 | 参数 | 结果 | 判读 |
|---|---|---|---|---|
| 1 | `meta/02_display_p3_1920x1080.png` | `--hdr --sdr-scrgb` | `actual=MapToHdr`、`outBits=16`、`isHdrSource=0`、`WIDE_GAMUT=1`、`primaries=BT.2020` | ✅ **SDR 广色域源走 16bit scRGB**（P3 图经 BT.2020 载体） |
| 2 | `basic/01b_srgb_1920x1080.png`（8bit sRGB） | `--hdr --sdr-scrgb` | `actual=MapToSdr`、`outBits=8`、`bitDepth=8` | ✅ **V11 通过**：8bit SDR 源即使开启新策略也不被误伤 |
| 3 | `basic/11a_16bit_rgb48.png` | `--hdr` | `Open: -1`（state=Idle） | ⚠ **样本本身打不开**，与本次改动无关：换回改动前内核（API 15 的 `.bak`）跑同一命令同样是 `Open: -1` |
| **4** | **`testmedia/media/real/real_4k_hevc10_60m.mp4`**（HEVC Main10，SDR） | `--hdr --sdr-scrgb` | `actual=MapToHdr`、`outBits=16`、`isHdrSource=0` | ✅ **V1 通过**：10bit SDR 源经 16bit scRGB 呈现 |
| **5** | 同上（**对照**） | `--hdr` | `actual=MapToSdr`、`outBits=10` | ✅ 基线：同一片源在旧策略下走 gamma `R10G10B10A2` |
| **6** | `testmedia/media/real/real_4k_h264_60m.mp4`（8bit） | `--hdr --sdr-scrgb` | `actual=MapToSdr`、`outBits=8` | ✅ **V11 通过**（视频版）：8bit 源即便开启策略也不被误伤 |

**判读要点**

- 对照 4/5 只有 `outBits` 从 10 变 16，其余全同 ⇒ 生效的正是
  `WantsScRgbPresentationPath()` 里新增的那一项（`sdrScRgbMode_ != 0 && sourceBitDepth_ > 8`）。
- 基线是 `MapToSdr`/10 这一点**反过来证明了该样本确实是"非广色域、非 HDR"**：
  若它是 BT.2020/P3，旧逻辑的 `wideGamut` 分支本就会把它放行到 16。
- 判据必须看 `[after-play]` 段：`[open]`/`[hdr]` 段还没有呈现帧，`outBits` 恒为 8
  （13 号文档 §3 已警告过"只看 [open] 段"会误判）。

片源位置：`testmedia/media/real/`（`SOURCES.md` 记录了来源），
`real_4k_hevc10_60m.mp4` = HEVC Main10 SDR、`real_4k_hevc_hdr10_60m.mp4` = HDR10、
`real_4k_av1_10bit_60m.mp4` = AV1 10bit（另两个 10bit 候选，可作交叉验证）。

---

## 12. 亮度对齐：让 SDR 内容在 scRGB 链上和以前一样亮

### 12.1 问题

scRGB 是**绝对亮度**契约（`1.0 = 80 nits`），而 SDR 源从不声明"我的白是多少 nits"。
以前走 SDR 链时这个锚点根本不存在于播放器里——输出 gamma 码值，白点由 DWM 按
Windows「SDR 内容亮度」决定。切到 scRGB 后必须自己给一个数。

### 12.2 解法：直接读 Windows 的 SDR 白电平

不再拍脑袋选 80/120/200，而是读系统那个值本身：

```
AdvancedColorInfo::SdrWhiteLevelInNits   （WinRT；内核已有同对象的读取代码）
```

它正是 DWM 把普通 SDR 窗口白色映射到的亮度 ⇒ 用它当纸白，**亮度就与以前的 SDR 链一致**，
并且**自动跟随用户在设置里拖动的滑杆**（播放器没有其他途径读到滑杆）。

### 12.3 落点

| 文件 | 改动 |
|---|---|
| `Hdr/HdrProcessor.h` | `HdrDisplayCapabilities` 新增 `sdrWhiteLevelNits`（0 = 未上报） |
| `Render/VideoRenderer.cpp` | `ReadWindowsDisplayLuminance()` 增读 `get_SdrWhiteLevelInNits`（失败不影响既有亮度读取）；探测后写入 `sdrWhiteLevelNits_`；能力沿用分支一并保留该值 |
| `Render/VideoRenderer.{h,cpp}` | 新增 `EffectivePaperWhiteNits()`：`SDR 源 && actualMode==MapToHdr` → 系统 SDR 白；读不到 → **80**（scRGB 契约参考白，比 200 更接近常规 SDR 观感）；HDR 源 → 仍用配置的 `paperWhiteNits_`（图形白约定） |
| 三处使用点 | `settings.paperWhite`（Render）与两处 `cachedVideoSettings_.paperWhite` 全部改走 `EffectivePaperWhiteNits()` ⇒ 画面、字幕（overlay 白）、封面 tint 共用同一刻度 |

**不涉及 ABI**：值在内核内部读取与使用，不改任何导出结构 ⇒ 仍是 API 16。

### 12.4 验证方法（无需仪器）

1. **滑杆跟随**：Windows 设置里拖动「SDR 内容亮度」，播放同一 SDR 片源——
   画面亮度**随之变化** ⇒ 系统值已生效；**完全不变** ⇒ 读到了 0 并回退到 80。
2. **与旧链对比**：设置切「从不」（SDR 链）与「自动」（scRGB 链）播同一帧，亮度应基本一致。

### 12.5 V7 定案工具已落地（`3fp/tools/sdr_white_probe.cpp`）

不再靠肉眼猜：探针自建 FP16 scRGB 窗口（G10 契约）输出已知线性值 1.0，再用
**Float16 Windows.Graphics.Capture 帧池**捕获自己的窗口，读回 DWM 合成后的同一线性值：

```
python tools/build_kernel_3fp.py --white-probe   # 编译（C++/WinRT + cppwinrt 头）
3fp\build\sdr_white_probe.exe
```

判读：`captured ≈ 1.0` ⇒ 无二次映射，纸白 = SdrWhite 的实现正确；
`captured ≈ SdrWhite/80` ⇒ 存在二次映射，`EffectivePaperWhiteNits()` 改回固定 80。

本机（SDR 桌面）实测：`captured = 1.0000`，`exit=0`——管线健全。**HDR 桌面终判
（2026-09-29，用户开启 HDR 后实测）**：`captured = 1.0000`（输出 1.0 → DWM 合成后仍
1.0）⇒ **DWM 对显式 scRGB 窗口不做 SDR 白二次映射，12.2 的锚点选择定案正确**。
同轮实机（`verify_image_mode real_4k_hevc10_60m.mp4 --hdr --sdr-scrgb`）：
`actual=MapToHdr, outBits=16`，且 `sdrWhite(WinRT) = 120 nits` ⇒ 10bit SDR 内容的
scRGB 白落在 120/80 = 1.5，与"以前的 SDR 链观感"（DWM 把 sRGB 白映射到 120 nits）对齐。

附带发现两处工程坑（已处理）：
① SDK 10.0.28000 起 `DISPLAYCONFIG_DEVICE_INFO_HEADER` 多了 `size` 字段、
`DisplayConfigGetDeviceInfo` 变单参数（老 SDK 双参数），已用 `requires` 写成两形态
通吃的模板包装；② WGC free-threaded 帧池的 teardown 与主线程退出存在竞态
（0xC0000005，`native/wgc_capture` 的 Graveyard 同源问题），探针以 `ExitProcess`
规避（结论先落盘）。

**待查（非阻塞）**：本机 DISPLAYCONFIG 路径读 0（`sdrWhite(DISPLAYCONFIG)=0`），而
WinRT 同屏读到 120——第二读数在此机器/驱动上不工作，可能踩中 TrueToneCap 坑 1 的
变体（GET_SOURCE_NAME 包大小/匹配方式）。因 WinRT 主路径正常、DISPLAYCONFIG 仅兜底，
不影响特性；留待有复现环境时修。

交叉印证：TrueToneCap 的捕获侧实测（`JpegGainMapEncoder.cs:180-182`）断言
「SDR 内容在 HDR 桌面被 DWM 提升到 SdrWhiteLevel（如 200 nits = 2.5 scRGB）」，
与 12.2 的锚点选择一致；但**它自己的预览窗口把 SDR 白放在 1.0 且无补偿**（呈现侧
未经检验）——本节探针正是补上这个检验手段。

⚠ **操作教训**：用 `.bak` 旧 DLL 做对照实验后，必须把新 DLL 复制回去并复核
`GetApiVersion()=16`——探针进程未退出时 `Copy-Item` 会报"文件被占用"而**静默留下旧内核**
（本次踩到，已恢复：`3fp/build` API 16 / `src/bin` 780800 B）。
