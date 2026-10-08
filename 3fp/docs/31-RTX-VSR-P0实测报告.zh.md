# 31 · RTX VSR P0 实测报告（2026-10-05）

> 对应 `docs/30-RTX-VSR完整可行性计划.zh.md` 的 **P0 阶段**。
> 目的：在投入任何代码前，用实测回答"读代码无法回答"的问题，确认方案前提。
> 结论：**P0 全部通过，方案可以开工。**

---

## 0. 结论摘要

| 编号 | 问题 | 结果 |
|---|---|---|
| **P0-1** | 本机 RTX VSR 是否可用/已开 | ✅ **`isSupported: true`、`isEnabled: true`、`quality: 5`** |
| **P0-2** | RTX Video HDR 是否已开 | ✅ 查询成功：**`isEnabled: false`**（未开，非阻塞） |
| **P0-3** | 能否直接验证驱动扩展 | ✅ **可以**——用 NVIDIA 驱动自身的状态 API 读真值（见 §2） |
| **P0-4** | 3FP 是否确实拿不到 VSR | ✅ 代码层已确证（死代码 + 硬编码 `Shader`），见 §4 |

**⇒ 环境前提全部满足，无需安装额外播放器即可验证。**

---

## 1. 硬件与驱动（实测）

```
GPU          : NVIDIA GeForce RTX 5080      DriverVersion 32.0.16.1692 (2026-09-04)
GPU(2)       : NVIDIA GeForce RTX 4060 Laptop GPU  （同一驱动包）
显示器       : PHL 27M2N5800P（主）/ AUO B160QAN02.7（笔记本内屏）
NVIDIA App   : 已安装（Version 11.0.9.251）
系统         : Windows（HDR 主输出 \\.\DISPLAY5，10-bit PQ，417.71 nits — 见 docs/13）
```

> ⚠ 本机是**双 GPU（5080 + 4060 Laptop）**。这与 `docs/13` 记录一致：`DISPLAY5`（5080）为 Primary。
> **对 VSR 的影响**：必须确认 3FP 的 D3D11 设备建在 **5080** 上，否则扩展可能不可用。
> 3FP 已有 `preferredAdapterIndex` 机制（API v15），必要时可显式指定。

---

## 2. ⭐ 关键方法：直接读 NVIDIA 驱动的 VSR 真值

**这是本次 P0 最有价值的发现**——不必依赖第三方播放器，**NVIDIA 驱动自己就暴露了完整的 VSR 状态**。

NVIDIA App 通过 `NvCplDisplayPlugin` 的插件 API 查询，日志落在：
`%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA App\console.log`

实测抓到的完整状态块（2026-10-05 14:19:30）：

```json
// === RTX Video Super Resolution ===
"VSR state fetched: {
    "isSupported": true,      ← ✅ 硬件+驱动支持 VSR
    "isEnabled":   true,      ← ✅ 用户已在 NVIDIA App 中开启
    "quality":     5,         ← 画质档位（5 = Auto 档，见 FAQ 的 GPU Utilization）
    "isActive":    false,     ← 当前无视频播放，故未激活
    "status":      0,
    "gpuUtil":     2,
    "disableVsrOnGameLaunch": 0,
    "vsrStatusIndicator":     1   ← 官方状态指示器已开
}"

// === RTX Video HDR ===
{
    "isSupported": true,
    "isEnabled":   false,     ← 未开启（本方案只做 VSR，不受影响）
    "errorCode":   0,
    "peakBrightness":    417, ← 与本机实测 417.71 nits 吻合（docs/13）
    "minPeakBrightness": 400,
    "maxPeakBrightness": 417,
    "middleGray": 50, "contrast": 0, "saturation": 0,
    "disableHdrOnGameLaunch": 0,
    "hdrStatusIndicator": 1
}
```

**可用于查询的 API 名（从日志提取）**：

| API | 用途 |
|---|---|
| `GetSuperResolutionInfo` | VSR 支持/质量信息 |
| `GetSuperResolutionGpuUtilization` | GPU 占用档位 |
| `GetSuperResolutionCurrentStatus` | **当前是否正在生效**（对应 `isActive`） |
| `GetRTXVideoFlags` | RTX Video 总开关标志 |

> **对本方案的意义**：
> 1. **P0 不需要装 VLC 就能验证**——驱动已给出真值；
> 2. `isActive` 这个字段正是 `docs/28` §3.4「已请求 ≠ 已生效」的**官方实现**，印证了 3FP 新 API 用 `active` 语义（而非 `enabled`）是**与官方一致的正确选择**；
> 3. 未来 3FP 可考虑（**非必需**）查询同类状态做交叉校验。

---

## 3. P0-2：第三方播放器验证

| 播放器 | 状态 |
|---|---|
| VLC | ❌ 未安装 |
| mpv | ❌ 未安装 |
| **PotPlayer** | ✅ 已安装（`C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe`） |
| MPC-HC/BE | ❌ 未安装 |

**结论：不必依赖它们。** 驱动侧真值（§2）已足以确认 `isSupported && isEnabled`。

> **对后续阶段的建议**：P2 验证时，**优先用自建探针直接调扩展 + 读 `GetSuperResolutionCurrentStatus`**，
> 比"装个 VLC 看画质"更**可量化**（能拿到 `isActive` 布尔值，而非主观判断）。

---

## 4. P0-4：3FP 确实拿不到 VSR（代码确证）

三重证据链：

| # | 证据 | 位置 |
|---|---|---|
| 1 | VP 通路是**死代码**——全仓库搜索 `DrawWithVideoProcessor` / `CanUseDirectVideoProcessor` 只有定义、**零调用点** | `VideoRenderer.cpp:2792`、`:3184` |
| 2 | 渲染路径硬编码为着色器缩放 | `:4932` `actualVideoScalingMode_.store(Shader)` |
| 3 | 着色器链**只缩不放**，放大发生在像素着色器采样阶段（而 VSR 要求放大发生在 `VideoProcessorBlt` 内） | `EnsurePlaneScaleChain:3000-3002`、`:3135-3136` |

⇒ **驱动侧 `isEnabled: true` 而 3FP 侧必然无效——正是本方案要解决的问题。**

---

## 5. 对方案的影响（P0 → P1 的输入）

| 项 | 调整 |
|---|---|
| **环境前提** | ✅ 满足，**P1 可立即开工** |
| **验证手段** | 改用「自建探针 + 驱动状态 API」，**不依赖第三方播放器**（更可量化） |
| **新增注意事项** | ⚠ **双 GPU**：需确认 3FP 的 D3D11 设备在 **5080** 上；建议 P1 探针**打印实际适配器名与 VendorId** |
| **`isActive` 语义** | 官方实现印证 → 3FP 新 API 用 `active` 字段名与注释（`docs/29` §3.5）**正确** |
| **画质档位** | 实测 `quality: 5`（Auto 档）——3FP **不需要**自己传质量参数（该档位由 NVIDIA App 控制，扩展里没有质量字段） |

> **重要澄清**：`quality` 是 **NVIDIA App/控制面板**的全局设置，**不在 `SetStreamExtension` 的载荷里**
> （载荷只有 `{version, method, enable}`）。⇒ 3FP 只需 `enable=1`，**画质由用户在 NVIDIA 侧决定**。
> 这与 mpv/VLC/Chromium 的实现完全一致（它们也都只传 enable）。

---

## 6. 待办（进入 P1）

- [x] P0-1 VSR 支持与开启状态 —— ✅ 已确认
- [x] P0-2 HDR 状态 —— ✅ 已确认（未开，不影响）
- [x] P0-3 验证手段 —— ✅ 改用驱动状态 API
- [x] P0-4 3FP 现状确证 —— ✅ 三重证据
- [ ] **P1**：扩展头 + 状态 API（零渲染改动）
      ⚠ 探针需**打印适配器名/VendorId**，确认 5080 被选中

---

## 附：原始证据

- NVIDIA App 日志：`%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA App\console.log`（含上述 JSON）
- GPU：`Get-CimInstance Win32_VideoController`（RTX 5080，驱动 32.0.16.1692）
- 显示器/HDR：本报告 §1，与 `3fp/docs/13-HDR与scRGB完整验证（2026-09-26）.zh.md` §1 一致
