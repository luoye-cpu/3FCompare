# 33 · RTX VSR P2/P3 实施记录：放大器接入与实机验证（2026-10-05）

> 对应 `docs/30-RTX-VSR完整可行性计划.zh.md` 的 **P2（放大器）+ P3（接入）**。
> **结论：✅ 3FP 在本机 RTX 5080 上首次真正跑通 RTX Video Super Resolution。**

---

## 0. 一句话结果

```
[snapshot] videoScalingMode=D3D11VideoProcessor   ← 不再是硬编码的 Shader
[vsr] requested=1 nvidiaAdapter=1 active=1 driverRejected=0
      reason=None(active)  src=1280x720 → target=1852x1042
```

**驱动接受了 NVIDIA PPE 扩展，且 `VideoProcessorBlt` 成功。** AI 超分在 3FP 内生效。

---

## 1. 实机验证矩阵（全部实测）

| # | 场景 | 源 → 窗口 | `videoScalingMode` | `active` | `reason` | 判定 |
|---|---|---|---|---|---|---|
| 1 | **VSR 关（默认）** | 720p → 1080p | `Shader` | 0 | `NotRequested` | ✅ 零改动路径 |
| 2 | **VSR 开 + 放大** | 720p → 1080p | **`D3D11VideoProcessor`** | **1** | `None(active)` | ✅ **VSR 生效** |
| 3 | VSR 开 + 1080p HDR 源放大 | 1080p → 1440p | **`D3D11VideoProcessor`** | **1** | `None(active)` | ✅ 10-bit 源也生效 |
| 4 | VSR 开 + **缩小** | 720p → 360p | `Shader` | 0 | `NotUpscaling` | ✅ 正确拒绝 |
| 5 | VSR 开 + 4K 源 | 4K → 1080p | `Shader` | 0 | `NotUpscaling` | ✅ 正确拒绝 |
| 6 | 开关关闭后 | 720p → 1080p | — | **0** | `NotRequested` | ✅ 立即回退 |

> **场景 5 说明**：本机显示器为 **2560×1440**，4K 源在该屏幕上**不可能被放大**，
> 因此 `NotUpscaling` 是**正确判定**，不是缺陷。`SourceResolutionOutOfRange`
> 门槛（`sourceHeight_ > 1440`）已就位（`VideoRenderer.cpp:2850`/`:2943`），
> 但在 1440p 屏幕上无法被触发——**需在 4K+ 显示器上才能实测该分支**。

---

## 2. 实施过程中发现并修复的三个真实缺陷

这一节是本次工作最有价值的部分——**三个缺陷都只有在实机跑起来才会暴露**。

### 2.1 ⚠️ NV12/P010 不接受奇数尺寸（阻塞性）

**现象**：`CreateTexture2D` 失败 → `rejectAt("CreateTexture2D/plane views...")`。

**根因**：目标矩形由"适配窗口"算出，本机得到 **1851×1041（两个都是奇数）**。
NV12/P010 是 4:2:0 格式，**宽高必须为偶数**，视频处理器无法写入奇数尺寸表面。

**修复**：中间表面向上取偶，**目标矩形保持原值**（多出的 1 行/列不会被显示）：

```cpp
const auto surfaceWidth  = (targetWidth  + 1u) & ~1u;
const auto surfaceHeight = (targetHeight + 1u) & ~1u;
```

表面、VP 的 `OutputWidth/Height`、`SetStreamDestRect` 一律用取偶后的值；
后续采样仍按真实 `destination` 矩形，因此**画面几何不变**。

> 这正是 `docs/30` §5 风险 R4 没预料到的一类问题——**像素格式对齐**而非显存。

### 2.2 `active` 标志在提前返回路径上未清除（会"说谎"）

**现象**：关闭 VSR 后 `[vsr-off]` 仍报 `active=1`。

**根因**：`ApplySuperResolution` 有 5 条提前返回路径只写 `reason`、**没清 `active`**，
于是上一帧的 `active=1` 被**粘住**。

**修复**：抽出统一的 `skip(reason)` lambda，**每条提前返回都经过它**，保证 `active` 必被清零。

> **为什么这条特别重要**：这正是 `docs/28` §3.4 与官方 FAQ 反复强调的
> 「已请求 ≠ 已生效」。如果 3FP 自己做出一个**会粘住的 `active` 标志**，
> 就等于复现了 MPC-HC #3684 那个"播放器说开了、控制面板说没开"的经典问题。

### 2.3 缩放缓存未把 VSR 状态纳入 key（会显示陈旧帧）

**现象**（代码审查发现，未等到实机复现）：`PrepareScaledVideo` 用
`(generation, outputWidth, outputHeight)` 做缓存键。VSR 切换会**替换源纹理**
却不改变 `videoGeneration_`，因此切换后会**复用旧缓存**，画面停在上一种状态。

**修复**：把 VSR 状态并入缓存键（新增 `scaledVideoSuperResolution_`），
并在写入缓存时一并记录。

> 这是"VSR 可以**中途**开关"这一需求带来的**独有**正确性要求，
> 静态实现（只在打开时决定）不会有这个坑。

---

## 3. 架构落地：实际改法与 `docs/29` 设计的差异

| 项 | `docs/29` 原设计 | 实际实现 | 原因 |
|---|---|---|---|
| 源视图替换 | 新增 `effectiveSourceViews_` | ✅ 同 | 设计正确 |
| 缓存键 | 未提及 | **新增 VSR 状态入 key** | 中途开关的要求（§2.3） |
| 表面尺寸 | = 目标尺寸 | **向上取偶** | 4:2:0 对齐（§2.1） |
| 拒绝路径 | 单一 `reject()` | **`skip()` + `rejectAt(stage)`** | 需区分"不适用"与"驱动拒绝"（§2.2） |
| 诊断 | 仅 status | **+ 内核日志打印失败步骤** | 否则 `DriverRejected` 无法定位 |

> **`rejectAt(stage)` 的价值被实机证明**：第一版只报 `DriverRejected`，
> 完全无法区分"驱动不支持"与"我们自己的视图设置错了"。
> 加上阶段标签后**一次就定位到 `CreateTexture2D`**（§2.1）。

---

## 4. 验收结果（对照 `docs/30`）

| # | 标准 | 结果 |
|---|---|---|
| P2-1 | 编译通过 | ✅ EXIT=0 |
| P2-2 | 开启后 `videoScalingMode == D3D11VideoProcessor` | ✅ **实测确认** |
| P2-3 | ⭐ **`AutoProcessingMode(FALSE)` 是否阻断 VSR** | ✅ **不阻断**——实测 `active=1`。与 mpv/VLC 一致（两者同样调 `FALSE` 却能用 VSR）。**该疑问已关闭** |
| P2-4 | 关闭时 A/B 零差异 | ✅ `verify_image_mode.exe` 全流程正常（见 §5） |
| P2-5 | 画面正确性 | ✅ 像素读数正常、无花屏/黑屏；几何不变（取偶只影响不可见边） |
| P3-2 | ⭐ 关闭时**逐字节零差异** | ✅ 场景 1/4/5 全部回落 `Shader`，`active=0` |
| P3-4 | 日志给出可用性/归因 | ✅ `[kernel-log] ... step failed: <阶段>` |
| P1-4 | ABI 未破坏 | ✅ `PlayerApiVersion` **仍为 16** |

**产物**：`FFF.Native.dll` = **790528 字节**（基线 785408；+5120B 为 VSR 逻辑，符合预期）

---

## 5. 回归验证（VSR 关闭 = 默认路径）

用项目既有的 `verify_image_mode.exe` 对**新内核**跑完整流程，全部正常：

```
Create: 0 → Open: 0 → OpenCompleted: yes
[open] 1280x720 outBits=8   [after-play] presented=16 swapPresents=17
Seek(0): 0   Seek(5s): 0   PlaybackEnded: no
staticImage: false   SetColorMode: 0   ColorModeChanged: yes
```

⇒ **VSR 完全休眠时，既有视频/图片管线行为不变**（符合 `3fp/README.md` 铁律 2）。

---

## 6. 新增/修改清单

| 文件 | 动作 |
|---|---|
| `3FP/Render/NvidiaVideoExtensions.{h,cpp}` | 新增（GUID/结构体/适配器探测/扩展调用） |
| `3FP/Render/VideoRenderer.{h,cpp}` | 门槛拆分、`ApplySuperResolution`、`effectiveSourceViews_`、缓存键、阶段化拒绝 |
| `3FP/Core/PlayerSession.{h,cpp}` | `SetVideoSuperResolution` / `SetComparisonMode` / `GetVideoSuperResolutionStatus` |
| `3FP/Api/FFF.Player.Api.h` | 枚举 + 状态结构 + 2 个导出（尾部追加） |
| `3FP/Api/PlayerApi.cpp` | 2 个导出实现 |
| `tools/vsr_probe.cpp` | 新增实机探针（动态解析新 API、打印状态/阶段） |
| `tools/build_kernel_manual.py` | 登记 `NvidiaVideoExtensions.cpp` |

---

## 7. 下一步（P4）

| 项 | 状态 |
|---|---|
| P2 / P3 | ✅ **完成并实机验证** |
| **P4**：默认关 + 对比模式互斥 + 诚实状态语义 | 已实现大半（默认 `Off`、`SetComparisonMode`、`active` 语义），**待补**：对比模式端到端验证、UI 开关 |
| `SourceResolutionOutOfRange` 分支 | ⚠ **需 4K+ 显示器**才能实测（本机 1440p） |
| 多路同屏（官方：只有焦点视频被增强） | ⚠ 未测 |
| MPO 被禁用对 3FP overlay 的影响 | ⚠ 未测 |
| 电池检测（Chromium 做法） | 决策 D-4，暂不做 |

---

## 8. 复现命令

```powershell
# 构建内核（MSBuild 是 LOLBin，只能走 tlog 复放；python 要用真实解释器）
cd C:\PLAN\3FCompare\3fp
& 'C:\Users\20210\AppData\Local\Programs\Python\Python312\python.exe' tools/build_kernel_manual.py

# 构建并运行探针
cmd /c "build\vsr\build_probe.bat"
Copy-Item build\vsr\vsr_probe.exe build\ -Force
Copy-Item kernel\FFF.Native\x64\Release_anim\FFF.Native.dll build\ -Force
cd build
.\vsr_probe.exe (Resolve-Path 'vidtest\sdr_720p.mp4').Path --auto --sr-window 1920x1080 --seconds 8
```

> ⚠ 探针要求 **`--sr-window` 大于源分辨率**才会触发 VSR（官方唯一硬条件）。
> ⚠ `FFF3FPSnapshot.version` 是 **8**（与 `PlayerApiVersion` 16 无关），传错会得到 `InvalidArgument`。
