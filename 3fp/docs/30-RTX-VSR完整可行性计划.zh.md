# 30 · 3FP 接入 RTX Video Super Resolution —— 完整可行性计划方案

> **文档定位**：本文是**可执行的项目计划**，回答"做不做、怎么做、按什么顺序做、怎么验收、有什么风险"。
> **上游文档**：`docs/28`（机制与官方规格）、`docs/29`（内核侧技术设计）
> **对象**：`3fp/kernel/FFF.Native/3FP/`（3FP 播放器内核）
> **撰写日期**：2026-10-05
> **状态**：方案已定，**待用户决策是否开工**

---

## 第 0 部分 · 决策摘要（一页看完）

### 0.1 结论

| 问题 | 结论 |
|---|---|
| **能不能做** | ✅ **能做**，且 3FP 已具备 90% 的结构基础 |
| **值不值得做** | ⚠️ **有条件值得**——见 §0.3 的"三个前提"，其中前提③与 3FC 核心用途冲突 |
| **工作量** | **约 250~350 行**内核代码 + 约 80 行探针；**4 个提交** |
| **周期** | 按阶段计：P0 半天、P1 1 天、P2 1~2 天、P3 1 天、P4 半天 |
| **风险等级** | 🟡 **中**（技术风险低，**产品/正确性风险中等**） |
| **硬件前提** | ✅ **已满足**——本机 RTX 5080 + 10-bit PQ HDR（`\\.\DISPLAY5`） |
| **最大阻塞** | 无技术阻塞；**唯一决策点是"是否允许在 3FP 里改变像素"** |

### 0.2 一句话方案

> 把 3FP 已有的（但是死代码的）`ID3D11VideoProcessor` 通路改造成一个**纯空间放大器**，
> 插在解码纹理与现有缩放链之间，通过 NVIDIA 私有扩展启用 VSR；
> **色彩管线完全不动**，默认关闭，与对比模式强制互斥。

### 0.3 三个前提（必须全部满足才建议开工）

| # | 前提 | 现状 | 谁来定 |
|---|---|---|---|
| ① | 本机可实机验证 | ✅ 满足（RTX 5080 + HDR） | — |
| ② | 改动符合 3FP「只加不改」铁律 | ✅ 可满足（模式开关门控，关闭时不可达） | 设计已保证 |
| ③ | **可接受"开启后像素不再逐字节可复现"** | ⚠️ **需用户明确同意** | **用户** |

> **前提③是真正的决策点。** 3FP 的核心价值之一是配合 3FCompare 做**帧级/像素级对比**（`docs/25`、`docs/26`）。
> VSR 是 AI 推理，**会改变像素**，这与 3FC 的对比用途在语义上直接冲突。
> ⇒ **本方案的处理是"默认关 + 对比模式强制互斥"**，把选择权交给用户。若用户认为"3FP 永远不该改变像素"，则**本方案应终止**。

---

## 第 1 部分 · 背景与依据

### 1.1 问题陈述

3FP 目前**结构性地无法获得 RTX VSR**。这不是配置问题，是架构决定的：

| 事实 | 证据 |
|---|---|
| 3FP 的着色器缩放链**只缩不放** | `EnsurePlaneScaleChain:3000-3002` 显式拒绝 `target > source`；`:3135-3136` 用 `min` 夹目标 |
| 放大实际发生在**像素着色器采样** | `DrawWithShader` 以 `destination` 尺寸采样，双线性完成放大 |
| VSR 要求放大发生在 **`VideoProcessorBlt` 内** | 官方 FAQ：*"only be enabled if the video requires upscaling"* |
| 3FP 有 VP 通路代码但**从未被调用** | `DrawWithVideoProcessor:3184`、`CanUseDirectVideoProcessor:2792` 全仓库无调用点 |
| 因此 `videoScalingMode` **恒为 `Shader`** | `:4932` 硬编码 `actualVideoScalingMode_.store(Shader)` |

⇒ **结论**：即便用户在 NVIDIA 控制面板打开 RTX 视频增强，3FP 也必然无效。

### 1.2 官方规格（已由官方 FAQ 一手确认，见 `docs/28` §7.1）

| 项 | 规格 |
|---|---|
| 输入分辨率 | **360p ~ 1440p** |
| 触发条件 | **必须正在放大**（唯一硬条件） |
| 输出 | 支持到 **5K 显示器** |
| GPU | 所有 RTX；VSR 首发 Ampere/Ada，**RTX 20 系自 VSR 1.5 起支持** |
| 画质档位 | 1–4（+ Auto） |
| 操作系统 | Windows 10/11 64-bit |
| 启用方式 | 控制面板 `Adjust Video Image Settings → RTX Video Enhancements`；或 NVIDIA App `System > Video` |
| 副作用 | **启用后 MPO 被系统自动禁用** |
| 状态指示 | 官方提供 status indicator（说明"已请求≠已生效"是公认问题） |

### 1.3 技术调用（五份独立实现交叉验证，逐字节一致）

```cpp
// NVIDIA_PPE_INTERFACE_GUID = d43ce1b3-1f4b-48ac-baee-c3c25375e6f7
struct { UINT version = 1; UINT method = 2; UINT enable; } ext;
videoContext->VideoProcessorSetStreamExtension(
    videoProcessor, 0, &kNvidiaPpeInterfaceGuid, sizeof(ext), &ext);
```

来源：Chromium `ui/gl/swap_chain_presenter.cc`、mpv `vf_d3d11vpp.c`、VLC `d3d11_scaler.cpp`、Kodi `DXVAHD.cpp`、MPCVR `D3D11VP.cpp`。

---

## 第 2 部分 · 技术方案（选定方案 B）

### 2.1 方案选型

| 方案 | 描述 | 判定 |
|---|---|---|
| A | VP 直接接管整帧放大（含 YUV→RGB） | ❌ **否决**——绕过 3FP 全部色彩管线，与 `docs/13`/`docs/20` 契约对立 |
| **B** | **VP 只做同格式空间放大，色彩仍归 3FP** | ✅ **选定** |
| C | VP 直接输出到交换链 | ❌ 否决——同 A 的色彩问题，且破坏 overlay 合成顺序 |

### 2.2 方案 B 数据流

```
解码纹理 (NV12/P010, BIND_DECODER, 多平面)
  │
  ├─[无 VSR]─────────────────────────────────────────────┐
  │                                                       │
  └─[有 VSR]─> VP 放大 (同格式进出) ─> 放大后的 NV12/P010 ─┤
                 ↑ 仅此处调用 SetStreamExtension          │
                                                          ▼
                            effectiveSourceViews_[] (平面 SRV)
                                     │
                                     ▼
                        PrepareScaledVideo()  ← 源已=目标 ⇒ 链路退化为 0 趟
                                     │
                                     ▼
                            DrawWithShader()  ← 1:1 采样，色彩/HDR/scRGB 全保留
```

### 2.3 为什么这个方案干净

**VSR 生效后，`PrepareScaledVideo` 拿到的源纹理尺寸已经等于目标尺寸**，于是：

```cpp
// EnsurePlaneScaleChain:3049 —— 条件不成立，循环体一次都不执行
while (width > targetWidth || height > targetHeight) { ... }
```

⇒ **缩放链自动退化为 0 趟**，直接返回放大纹理的视图。**缩放逻辑一行都不用改**（只需把 `sourceViews_` 换成 `effectiveSourceViews_`）。

### 2.4 关键正确性约束

| 约束 | 原因 |
|---|---|
| **绝不改写 `sourceViews_`** | 它由 `EnsurePipeline:2644` 创建并持有，是**管线长期状态**（`:2779-2780`）；改写会造成跨帧污染。必须新增 `effectiveSourceViews_`（每帧重建） |
| **插入点必须在 `CalculateVideoDestination` 之后** | 只有那时才知道目标尺寸，而"是否在放大"是唯一硬条件 |
| **平面 SRV 格式必须与 `EnsurePipeline:2730-2735` 一致** | 否则颜色错乱（`R8_UNORM`/`R8G8_UNORM` vs `R16_UNORM`/`R16G16_UNORM`） |
| **`targetWidth/Height` = `destination` 尺寸** | 避免"放大后又缩小"的浪费 |
| **降级必须 sticky** | Blt 一旦失败，本会话不再尝试（避免每帧重复失败） |

---

## 第 3 部分 · 实施计划（分阶段）

### 阶段总览

| 阶段 | 目标 | 交付物 | 验收 | 预估 |
|---|---|---|---|---|
| **P0** | 实测确认前提 | 实测报告 | VLC 侧 VSR 生效；3FP 侧恒 `Shader` | 0.5 天 |
| **P1** | 可探测，**零渲染改动** | 扩展头 + 状态 API | 新 API 可用；渲染**逐字节不变** | 1 天 |
| **P2** | 放大器（默认关） | `ApplySuperResolution` | 强制打开后 `videoScalingMode=VP` | 1~2 天 |
| **P3** | 接入主链路 | 调用点 + 降级 | 关闭时**零回归** | 1 天 |
| **P4** | 安全互斥与开关 | API + 模式联动 | 对比模式拒绝启用 | 0.5 天 |

---

### P0 · 可行性实测（**必须先做，可能否决整个方案**）

**目的**：在投入任何代码前，用实测回答三个"读代码无法回答"的问题。

| # | 实测项 | 方法 | 判定 |
|---|---|---|---|
| P0-1 | 本机 NVIDIA 面板的 RTX 视频增强**是否已开** | 打开控制面板 `Adjust Video Image Settings → RTX Video Enhancements`；或 NVIDIA App `System > Video` | 记录 Super Resolution 开关与画质档位 |
| P0-2 | **VLC 能否拿到 VSR** | `vlc --d3d11-upscale-mode=super <1440p片源>`，放大到全屏 | 播放 1440p 以下片源，看 NVIDIA 指示器 / GPU 占用变化 |
| P0-3 | **3FP 是否确实拿不到** | 同一片源用 3FP 全屏播放，读 `FFF3FP_GetSnapshot().videoScalingMode` | **预期恒为 `Shader`**——这正是要修的问题 |
| P0-4 | ⭐ **`AutoProcessingMode(FALSE)` 是否阻断 VSR** | 在 VLC 里观察其行为（VLC 也调用 `FALSE` 却仍能开 VSR）⇒ 间接证据 | 若 VLC 能开，则 3FP 的 `FALSE` 大概率也不阻断（**待 P2 直接验证**） |

**P0 产出**：`3fp/docs/31-RTX-VSR-P0实测报告.zh.md`（含截图/日志/读数）

> **否决条件**：若 P0-2 显示本机 VLC 也无法启用 VSR（例如驱动/显示器链路不支持），则**暂停方案**，先解决环境问题。

> **测试素材建议**：用 720p/1080p 片源（在官方 360p~1440p 区间内），放大到全屏。
> 本机已有 `3fp/build/vidtest/{sdr_720p,hdr10_1080p}.mp4`（见 `3fp/docs/20`）。

---

### P1 · 扩展与能力探测（**零渲染改动，风险最低**）

**目标**：把 NVIDIA 扩展与状态查询做出来，但**完全不碰渲染路径**。

**交付物**：

| 文件 | 动作 | 说明 |
|---|---|---|
| `3FP/Render/NvidiaVideoExtensions.h` | **新增** | GUID、结构体、`IsNvidiaAdapter()`、`SetSuperResolution()` |
| `3FP/Render/NvidiaVideoExtensions.cpp` | **新增** | 上述实现（约 60 行） |
| `3FP/Render/VideoRenderer.h` | 改 | 新增探测成员 + `SetVideoSuperResolution()` / 状态查询声明 |
| `3FP/Render/VideoRenderer.cpp` | 改 | 实现探测（在 `EnsureDevice()` 里做一次）+ 状态访问器 |
| `3FP/Api/FFF.Player.Api.h` | 改 | **尾部追加**新枚举/结构/函数声明（**不升 API 版本**，见下方🔴） |
| `3FP/Api/PlayerApi.cpp` | 改 | `PlayerApiVersion = 17` + 两个新导出的转发 |

**🔴 关键发现：API 版本号是"硬闸"，升版会打断 3FC**

`FFF3FP_Create`（`PlayerApi.cpp:82-83`）**硬校验版本号**：

```cpp
if (configuration->size < sizeof(FFF3FPConfiguration) ||
    configuration->version != PlayerApiVersion ||   // ← 不等就 InvalidArgument
    ...) return FFFResult::InvalidArgument;
```

且 `:12-14` 有明确注释：

> *"FFF3FP_Create **rejects a mismatched version outright**, so **every consumer of this header MUST be rebuilt and bumped in lockstep**."*

⇒ **升到 v17 会让旧宿主（3FC 的 `third_party/fff_project`）立即无法创建播放器实例。**

**应对策略（二选一，建议 (a)）**：

| 方案 | 做法 | 代价 |
|---|---|---|
| **(a) 本阶段不升版** ✅ | **保持 `PlayerApiVersion = 16` 不变**，VSR 只通过**新增导出函数**暴露（`FFF3FP_SetVideoSuperResolution` 等）。新增函数**不动 `FFF3FPConfiguration` 结构**，因此旧宿主仍能创建实例，新宿主用 `GetProcAddress` 探测新函数 | 零破坏；代价是 VSR 开不了"创建时配置"，只能创建后设置（**完全够用**） |
| (b) 升版到 17 | 严格遵循 lockstep 纪律 | 需**同步重移植进 `third_party/fff_project`**（`PATCHES.md` 流程）+ 重建 3FC，**影响面大** |

> **强烈建议 (a)**。理由：VSR 是**运行时可切换的开关**，不需要在 `FFF3FPConfiguration` 里占位。
> 这样**不触碰 ABI 版本**，`FFF3FPConfiguration` 结构体**一个字节都不变**，3FC 侧零影响。
> `FFF3FPSnapshot` 若要加字段，同样**尾部追加 + `size`/`version` 版本化**（该结构已有此机制），不需要升全局 API 版本。

⇒ **修正后的 P1 验收**：`PlayerApiVersion` **保持 16**；新增函数通过 `GetProcAddress` 探测可用性。

**⚠️ 关键工程约束（本项目特有）**：

> 本机 `MSBuild.exe` **被安全策略判为 LOLBin**（bash 与 PowerShell 下均被拒），
> 内核构建唯一入口是 `3fp/tools/build_kernel_manual.py`（复放 tlog 命令）。
> **该脚本的 `EXTRA_SOURCES` 是白名单——新增 `.cpp` 必须在此登记**，否则不会被编译：
> ```python
> EXTRA_SOURCES = [
>     # 已登记：SubtitleProbe.cpp
>     (os.path.join(ROOT, "3FP", "Render", "NvidiaVideoExtensions.cpp"),
>      os.path.join(ROOT, "3FP", "Render", "VideoRenderer.cpp")),   # 模板：同目录、选项一致
> ]
> ```

**验收标准**：

| # | 标准 | 判定 |
|---|---|---|
| P1-1 | 内核编译通过 | `python 3fp/tools/build_kernel_manual.py` → EXIT=0，产出 `FFF.Native.dll` |
| P1-2 | 新 API 可调用 | 探针用 `GetProcAddress` 解析 `FFF3FP_GetVideoSuperResolutionStatus`，`nvidiaAdapter=1` |
| P1-3 | ⭐ **渲染逐字节不变** | 用 `tools/ab_baseline_vs_patched.sh` 跑 A/B，**所有用例输出与基线一致** |
| P1-4 | ⭐ **ABI 零破坏** | `PlayerApiVersion` **仍为 16**；`FFF3FPConfiguration` **结构不变**；旧宿主（3FC）**仍能正常创建实例** |

> **P1 是最安全的切入点**：即使后续 P2/P3 失败，P1 本身提供了"能探测驱动能力"的价值，且零回归。

---

### P2 · VP 放大器（默认关闭）

**目标**：实现 `ApplySuperResolution()`，但**默认关**，仅用探针强制打开验证。

**交付物**：

| 动作 | 说明 |
|---|---|
| 新增 `CanUseVideoProcessorForUpscale()` | 拆自 `CanUseDirectVideoProcessor:2792`，**放宽 4 项**（见下方放宽清单） |
| 新增 `ShouldEnableNvidiaSuperResolution()` | 叠加厂商/官方规格门槛（含 **360p~1440p**、**必须放大**） |
| 新增 `ApplySuperResolution(w, h)` | VP 建立 + 缓存 + `Blt` + sticky 降级（约 150 行） |
| 新增 `effectiveSourceViews_[3]` | 每帧由 `sourceViews_` 起算 |
| `PrepareScaledVideo` 改 2 处 | `sourceViews_` → `effectiveSourceViews_`（`:3127`、`:3156`） |

**门槛放宽清单（均有依据）**：

| 原条件 | 动作 | 依据 |
|---|---|---|
| `!sourceExternal_` | **删** | 软解上传 D3D11 同样可行（官方未要求硬解；Chromium 用 staging 上传） |
| `cachedVideoSettings_.transfer != 0` | **删** | VSR 与色彩模式无关（官方：VSR 也放大 HDR 视频） |
| `actualMode_ != MapToSdr` | **删** | 同上；且它把 VSR/TrueHDR 混为一谈 |
| `swapOutputBits_ > 10` | **删** | 与 VSR 无关，且会误杀 scRGB 路径 |
| `sourceChromaLocation_ != LEFT` | **放宽** | 接受 `UNSPECIFIED`（大量片源如此） |

**验收标准**：

| # | 标准 |
|---|---|
| P2-1 | 编译通过 |
| P2-2 | 探针强制开启后，`videoScalingMode == D3D11VideoProcessor` |
| P2-3 | ⭐ **确认 `AutoProcessingMode(FALSE)` 是否阻断**——若阻断，改为 `TRUE` 或调整设置顺序，并记录结论 |
| P2-4 | 关闭时 A/B **零差异**（回归门禁） |
| P2-5 | 放大后画面**主观正确**（用 `FFF3FP_ReadVideoPixelRegion` 读数，与关闭时对比亮度/色度不偏移） |

---

### P3 · 接入主渲染链路

**目标**：把放大器接进 `DrawCachedVideo`，并实现降级。

**改动点**（精确到行）：

```cpp
// VideoRenderer.cpp:4906-4917 —— DrawCachedVideo 内
ID3D11ShaderResourceView* presentationViews[3]{};
if (projection360) {
    std::copy(std::begin(sourceViews_), std::end(sourceViews_), presentationViews);
} else {
    // ★ 新增 1：尝试 VSR（不适用/失败均静默跳过，内部已 sticky 降级）
    (void)ApplySuperResolution(destination.width, destination.height);
    // ★ 新增 2：PrepareScaledVideo 内部改用 effectiveSourceViews_
    const auto scaleResult = PrepareScaledVideo(destination.width, destination.height,
        presentationViews);
    if (scaleResult != FFFResult::Success) return scaleResult;
}

// VideoRenderer.cpp:4932 —— 改为按实际路径写
actualVideoScalingMode_.store(
    superResolutionActive_.load(std::memory_order_acquire)
        ? FFF3FPVideoScalingMode::D3D11VideoProcessor
        : FFF3FPVideoScalingMode::Shader);
```

**降级逻辑**（sticky）：

```cpp
// Blt 失败 ⇒ 永久关闭，本会话不再尝试
nvidiaSuperResolutionFailed_.store(true, std::memory_order_release);
superResolutionActive_.store(false, std::memory_order_release);
SetError("NVIDIA RTX Super Resolution was rejected; falling back to shader scaling.");
```

**验收标准**：

| # | 标准 |
|---|---|
| P3-1 | 编译通过 |
| P3-2 | ⭐ **关闭 VSR 时 A/B 逐字节零差异**（铁律 2：视频模式行为不变） |
| P3-3 | 开启后画面正常，无黑屏/花屏/卡顿 |
| P3-4 | 日志出现 `RTX VSR: active (WxH -> WxH)` 或明确的不可用原因 |
| P3-5 | 宿主测试 `bash 3fp/tools/run_host_tests.sh` **无新失败**（注意脚本 §①警告：构建失败会假绿） |

---

### P4 · 安全互斥与用户开关

**目标**：默认关 + 对比模式强制互斥 + 诚实的状态语义。

**交付物**：

| 动作 | 说明 |
|---|---|
| `FFF3FP_SetVideoSuperResolution(mode)` | `Off`（默认）/ `Auto` |
| `FFF3FP_GetVideoSuperResolutionStatus()` | 返回 `requested`/`nvidiaAdapter`/`active`/`driverRejected`/`ineligibleReason` |
| `FFF3FPSnapshot` 尾部追加 | `std::uint32_t videoSuperResolution;` |
| **模式联动** | 挂到 3FP 既有模式开关（`docs/00` §2），进入对比/剪辑/图片模式时自动关 |

**⚠️ 措辞要求**：字段叫 **`active`** 而非 `enabled`，注释须写明：
> *"active 仅表示驱动接受了扩展请求且 `VideoProcessorBlt` 未失败，**不代表画质一定提升**。"*
> 依据：NVIDIA 官方自己都要做 status indicator（官方 FAQ）；MPCVR changelog 原文 *"only means that the request was sent successfully"*。

**验收标准**：

| # | 标准 |
|---|---|
| P4-1 | 默认 `Off`，新装用户不受影响 |
| P4-2 | 对比模式下 `SetVideoSuperResolution(Auto)` 返回 `InvalidState` |
| P4-3 | 状态字段语义与实际一致（含 `driverRejected` 场景） |
| P4-4 | **ABI 零破坏**：`PlayerApiVersion` 仍 16，旧宿主仍能创建实例 |

---

## 第 4 部分 · 验收体系

### 4.1 三层验收

| 层 | 手段 | 通过标准 |
|---|---|---|
| **L1 编译** | `python 3fp/tools/build_kernel_manual.py` | EXIT=0，产出 `FFF.Native.dll` |
| **L2 行为** | `bash 3fp/tools/ab_baseline_vs_patched.sh`（基线 vs 改动，**同批次交替**） | **关闭 VSR 时全部用例逐字节一致**；开启时缩放模式字段变化 |
| **L3 回归** | `bash 3fp/tools/run_host_tests.sh` + `verify_image_mode.cpp` | 无新失败（注意脚本的"假绿"警告） |

### 4.2 新增探针

建议新增 `3fp/tools/vsr_probe.cpp`（仿 `verify_image_mode.cpp`）：

- **动态解析新 API**（`GetProcAddress`）——**必须**，否则基线内核会因找不到入口点而加载失败（`ab_baseline_vs_patched.sh` §12 的经验）
- 输出：适配器 VendorId、`requested`/`active`/`driverRejected`、`videoScalingMode`、源/目标尺寸
- 支持 `--force-sr` 强制开启（P2 阶段验证用）

### 4.3 回归门禁（沿用项目纪律）

- ⚠️ **构建必须先成功**：构建失败时测试会跑**陈旧二进制**并"全绿"（`run_host_tests.sh` §①踩过）
- ⚠️ **不要用 `| tail` 取退出码**（拿到的是 tail 的状态）
- ⚠️ A/B 必须**同批次交替**（A,B,A,B…），避免环境漂移被误读为版本差异

---

## 第 5 部分 · 风险登记册

| # | 风险 | 概率 | 影响 | 等级 | 缓解措施 |
|---|---|---|---|---|---|
| **R1** | **VSR 破坏像素级可复现性**，与 3FC 对比用途冲突 | 高 | 🔴 高 | 🔴 | **默认 `Off` + 对比模式强制互斥**；文档明示 |
| **R2** | `AutoProcessingMode(FALSE)` 阻断 VSR | 中 | 🟡 中 | 🟡 | P2-3 实测；若阻断改 `TRUE`/调序。VLC 同样调 `FALSE` 却能开 ⇒ 大概率不阻断 |
| **R3** | 误改 `sourceViews_` 造成跨帧污染 | 中 | 🔴 高 | 🟡 | 设计已隔离：只用 `effectiveSourceViews_`；code review 重点 |
| **R4** | 放大纹理显存增长（1440p→4K ≈ 3.5x） | 高 | 🟡 中 | 🟡 | 单张缓存 + 尺寸变化即释放；4K NV12 ≈ 12MB，可接受 |
| **R5** | **启用后 MPO 被系统禁用**（官方明文） | 高 | 🟡 中 | 🟡 | 实测 3FP overlay/多窗口表现；必要时提示用户 |
| **R6** | 多路同屏只有"焦点视频"被增强（官方明文） | 高 | 🟡 中 | 🟡 | 3FP 多路场景明确策略：建议仅当前路启用 |
| **R7** | 官方 `360p~1440p` 未说明按高还是按短边 | 中 | 🟢 低 | 🟢 | 先用**高度**；P2 实测后定 |
| **R8** | 各驱动对 PPE 扩展行为不一 | 中 | 🟢 低 | 🟢 | sticky 降级已覆盖 |
| **R9** | 新增 `.cpp` 未登记 `EXTRA_SOURCES` ⇒ 静默不编译 | **高** | 🟡 中 | 🟡 | P1 明确登记；链接期会报未解析符号，留意 |
| **R11** | **升 API 版本号打断 3FC**（`FFF3FP_Create` 硬拒版本不符） | 高（若升版） | 🔴 高 | 🟡 | **保持 `PlayerApiVersion = 16`**，只用新增导出函数（见 P1 的🔴说明） |
| **R12** | `FFF3FPSnapshot` 加字段破坏旧宿主读取 | 中 | 🟡 中 | 🟢 | 尾部追加 + 沿用该结构既有的 `size`/`version` 版本化机制 |
| **R10** | 与图片模式 PR 冲突 | 低 | 🟡 中 | 🟢 | **独立 PR**（`feature/rtx-video-super-resolution`），与图片模式分开 |

---

## 第 6 部分 · 上游 PR 策略

### 6.1 与铁律的符合性

| 铁律（`3fp/README.md`） | 本方案符合性 |
|---|---|
| **铁律 1**：绝不改 `third_party/fff_project/` | ✅ 改动只落 `3fp/kernel/` |
| **铁律 2**：视频模式"只加不改" | ✅ 新增分支由开关门控；**关闭时逐字节不变**（P1-3/P3-2 验收） |
| **铁律 3**：大文件不入库 | ✅ 无大文件 |

### 6.2 PR 建议

| 项 | 建议 |
|---|---|
| 分支名 | `feature/rtx-video-super-resolution` |
| 提交数 | **4 个提交**（对应 P1~P4），最终 squash 为 1 个给上游（沿用 D10/D11 纪律） |
| 与图片模式 PR 关系 | **分开**，便于上游单独评估 |
| PR 定位 | **新增可选功能**，默认关，零回归——对上游是纯增量 |

> ⚠️ 沿用项目纪律：**推送与开 PR 须用户确认**（`STATUS.md` 3.6 同款约定）。

---

## 第 7 部分 · 待决策事项（需用户裁定）

| # | 决策点 | 选项 | 建议 |
|---|---|---|---|
| **D-1** | **是否允许 3FP 改变像素**（前提③） | (a) 允许，默认关 (b) 不允许，终止方案 | **(a)**——把选择权给用户，默认不影响任何人 |
| D-2 | 默认值 | `Off` / `Auto` | **`Off`**（mpv 与 VLC 均默认关） |
| D-3 | 是否同时做 TrueHDR | 只做 VSR / 一起做 | **只做 VSR**（TrueHDR 与 scRGB 契约冲突，见 `docs/29` §4.5） |
| D-4 | 是否做电池检测（Chromium 做法） | 做 / 不做 | **暂不做**，先看 P2 实测功耗 |
| D-5 | UI 呈现 | 设置项 / 仅 API | 先 **仅 API**，UI 待 P4 后评估 |
| **D-6** | **是否升 API 版本号** | (a) 不升，只用新增函数 (b) 升到 17 | **(a)**——升版会硬拒旧宿主，需同步重移植进 3FC 基线 |

---

## 第 8 部分 · 附：关键代码位置索引

| 位置 | 内容 |
|---|---|
| `VideoRenderer.cpp:2644` | `EnsurePipeline` — 创建 `sourceViews_` |
| `VideoRenderer.cpp:2730-2735` | 平面 SRV 格式（**必须对齐**） |
| `VideoRenderer.cpp:2792` | `CanUseDirectVideoProcessor` — 待拆分的旧门槛 |
| `VideoRenderer.cpp:2891` | `AutoProcessingMode(FALSE)` — **R2 关注点** |
| `VideoRenderer.cpp:2995` | `EnsurePlaneScaleChain` — 只缩不放（`:3000`、`:3135`） |
| `VideoRenderer.cpp:3049` | `while` 循环 — **VSR 生效时退化为 0 趟** |
| `VideoRenderer.cpp:3113` | `PrepareScaledVideo` — 改 `effectiveSourceViews_`（`:3127`/`:3156`） |
| `VideoRenderer.cpp:3184` | `DrawWithVideoProcessor` — 参考实现 |
| `VideoRenderer.cpp:4326` | `EnsurePipeline` 调用点 |
| `VideoRenderer.cpp:4841` | `DrawCachedVideo` |
| `VideoRenderer.cpp:4906-4917` | **VSR 插入点** |
| `VideoRenderer.cpp:4932` | 硬编码 `Shader` — 待改 |
| `PlayerApi.cpp:16` | `PlayerApiVersion = 16` → 17 |
| `tools/build_kernel_manual.py:39` | `EXTRA_SOURCES` — **新增 .cpp 必须登记** |

---

## 第 9 部分 · 一页纸执行清单

```
□ 用户裁定 D-1（是否允许改变像素）—— 不做则终止
□ P0  实测：面板状态 / VLC 能开 / 3FP 恒 Shader → 产出 docs/31
□ P1  扩展头 + 状态 API（零渲染改动）
      □ NvidiaVideoExtensions.{h,cpp}
      □ 登记 build_kernel_manual.py EXTRA_SOURCES   ← 易漏！
      □ 新增导出函数（⚠ 保持 PlayerApiVersion = 16，不动 Configuration）  ← 防打断 3FC
      □ 验收：编译通过 + A/B 零差异 + 旧宿主仍能创建实例
□ P2  ApplySuperResolution（默认关）
      □ 拆门槛（放宽 4 项）+ 官方 360p~1440p
      □ effectiveSourceViews_（不动 sourceViews_）
      □ 实测 AutoProcessingMode(FALSE) 是否阻断
      □ 验收：强制开启后 videoScalingMode=VP；关闭时零差异
□ P3  接入 DrawCachedVideo + sticky 降级
      □ 验收：关闭时 A/B 逐字节零差异 + 宿主测试无新失败
□ P4  默认关 + 对比模式互斥 + 诚实状态语义
      □ 验收：默认 Off；对比模式拒绝；PlayerApiVersion 仍 16（旧宿主可创建实例）
□ 上游 PR（须用户确认）：feature/rtx-video-super-resolution，4 提交 squash 为 1
```

---

**结论**：技术上**完全可行且路径清晰**（250~350 行、5 个阶段、风险可控），
唯一需要用户拍板的是**前提③——是否允许 3FP 改变像素**。
本方案的处理方式（默认关 + 对比模式互斥）已把这个决策的影响面降到最低：
**不开启的用户，行为逐字节不变。**
