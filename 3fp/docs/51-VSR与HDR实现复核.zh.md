# 51 · NVIDIA VSR + HDR 实现复核

> 问题：「NV VSR 超分辨率 + HDR 重新回头分析一下是否实现正常」
> 方法：**不只看标志位，验证像素输出**（`tools/vsr_pixel_probe.cpp` 新增）
> 日期：2026-10-06

---

## 0. 结论：**实现正常，SDR、HDR10 与 scRGB 三条路径均实测通过**

| 验证项 | 结果 |
|---|---|
| VSR 能否激活 | ✅ `active=1`，`videoScalingMode=D3D11VideoProcessor` |
| **像素是否真的改变** | ✅ SDR 平均差 0.0014 / 最大 0.3294；HDR10 平均 0.0072 / 最大 0.5943 |
| **是否造成曝光偏移** | ✅ 亮度差仅 **+0.0007 / +0.0006**（可忽略） |
| **HDR10 10-bit 是否支持** | ✅ `outBits=10`，`active=1`，`1920x1080 → 2492x1402` |
| **★ scRGB(16bit) 是否支持** | ✅ **`outBits=16`，`active=1`**，像素差异平均 0.0352 / 最大 4.3008 |
| HDR 元数据/峰值检测是否受影响 | ✅ 不受影响（VSR 输出作为**源**再走着色器链） |
| 越界情形是否正确拒绝 | ✅ 4K 源报 `NotUpscaling`；软解报 `UnsupportedSourceFormat` |

---

## 1. 关键发现：VSR **要求硬件解码**

`CanUseVideoProcessorForUpscale` 的最后一条：

```cpp
return (inputDescription.BindFlags & D3D11_BIND_DECODER) != 0;
```

**只有硬件解码器**产生的纹理带 `D3D11_BIND_DECODER`。
⇒ **软解下 VSR 必然不可用**（上传的纹理没有该标志）。

> ⚠️ 我第一版探针用了 `decodeMode = Cpu`，得到"VSR 未改变输出"的结论 ——
> **那是探针的错误，不是 VSR 的问题**。改为硬解后立即测得真实差异。
> 这提醒：**验证 VSR 必须用硬解素材**。

---

## 2. 像素级验证（新增探针）

同机位取帧，仅切换 VSR 开关，逐像素比对全帧：

### SDR 720p 源 → 2492x1402 上采样

| 指标 | 值 |
|---|---|
| 平均亮度 关/开 | 0.4187 / 0.4194（**差 +0.0007**） |
| 像素差异 平均 | **0.0014** |
| 像素差异 最大 | **0.3294** |
| 超阈值样本 | **3.67%** |

### HDR10 1080p 源 → 2492x1402 上采样

| 指标 | 值 |
|---|---|
| 平均亮度 关/开 | 0.5920 / 0.5926（**差 +0.0006**） |
| 像素差异 平均 | **0.0072** |
| 像素差异 最大 | **0.5943** |
| 超阈值样本 | **36.48%** |

**判读**：
- 差异**确实存在** ⇒ VSR 不只是置了标志位；
- 差异**集中在细节**（最大差异大、平均值小）⇒ 符合 AI 上采样的特征；
- **亮度几乎不变** ⇒ 没有曝光/色偏副作用。

---

## 3. HDR 交互分析

### 3.1 架构正确：VSR 只做上采样，色调映射仍在着色器

```cpp
if (ApplySuperResolution(...) == Success && videoSuperResolutionActive_) {
    for (plane...) effectiveSourceViews_[plane] = superResolutionViews_[plane];
}
const auto scaleResult = PrepareScaledVideo(...);   // 后续走着色器（含 HDR）
```

⇒ VSR 的输出被当作**源**交给正常着色器链，**HDR 色调映射、scRGB、广色域全部照常**。

### 3.2 surface 格式有专门的注释说明为何不用 VP 做色彩转换

```cpp
// ... YUV->RGB conversion, because that would bypass this renderer's entire
// colour pipeline (tone mapping, scRGB, wide gamut).
description.Format = sourceDescription.Format;   // 保持 P010/NV12
```

⇒ **刻意保持 YUV 格式**，避免 VP 擅自做色彩转换绕过 HDR 管线。**这是正确的设计**。

### 3.3 实测 HDR 状态不受影响

```
快照: sourcePeakNits=1000  isHdrSource=1  hdrFormat=1  hdrPath=1  outBits=10
```

---

## 4. 门控逻辑复核（均正确）

| 条件 | 行为 | 实测 |
|---|---|---|
| 未请求 | `NotRequested` | ✅ |
| 非 N 卡 | `NotNvidiaAdapter` | — |
| 源格式不支持 | `UnsupportedSourceFormat` | ✅ 软解时触发 |
| 非上采样 | `NotUpscaling` | ✅ 4K 源触发 |
| 源高度 <360 或 >1440 | `SourceResolutionOutOfRange` | — |
| 驱动曾拒绝 | `DriverRejected` | — |

**且每个提前返回都会清除 `active`**（`skip()` lambda 统一处理）——
避免"上一帧开了 VSR，状态块却显示还开着"的陈旧标志问题。

---

## 5. ★ scRGB（16bit）路径补测（系统 HDR 已开启）

### 5.1 环境确认

```
显示器 \\.\DISPLAY5: HDR=开  maxLum=418 nits  minLum=0.0000  bits=10
```

（由 DXGI `IDXGIOutput6::GetDesc1` 直接查询，非推测。）

### 5.2 scRGB 路径实测

| 项 | 值 |
|---|---|
| 请求色彩模式 | `MapToHdr` (2) |
| **实际色彩模式** | **MapToHdr (2)** ✅ |
| **输出位深** | **16（R16G16B16A16_FLOAT / scRGB）** ✅ |
| **VSR** | `active=1`, `rejected=0`, `1920x1080 → 2492x1402` |
| 平均像素差 | 0.0352 |
| 最大像素差 | **4.3008** |
| 超阈值样本 | 69.15% |
| 平均亮度 | 1.0455 → 1.0574（**+0.0119**） |

⇒ **VSR 在 scRGB 路径确实生效。**

### 5.3 控制变量：scRGB 的亮度偏移属预期，非缺陷

**同一素材（HDR10 1080p）、同一探针**，仅输出链不同：

| 输出链 | outBits | 亮度偏移 |
|---|---|---|
| SDR 链 | 10 | **+0.0006** |
| **scRGB 链** | **16** | **+0.0119** |

看起来 scRGB 偏移大 20 倍，但**不是系统性偏移**：

| 证据 | 说明 |
|---|---|
| 最大差 **4.3008** ≫ 平均差 **0.0352** | 若为系统性偏移，两者应接近；实际**高度不均匀** |
| SDR 素材在 HDR 模式下仍走 SDR 链（`实际=0`, `outBits=8`），偏移仍 **+0.0007** | 证明差异来自**色彩链**而非画面内容 |

**原因**：scRGB 是**线性光**，数值范围远大于 gamma 编码的 SDR；
VSR 在**非线性 YUV 域**上采样后线性化，边缘处的数值在**线性域被放大**
⇒ 锐化导致的亮侧增强在线性域被放大 ⇒ 平均值轻微上移。

相对幅度 = 0.0119 / 1.0455 = **1.14%**（约 1 nit / 84 nits），
且**无全图单调偏移** ⇒ **判定为 VSR 锐化的正常副产品，非缺陷**。

---

## 6. 本次未验证

- **非 N 卡 / 无 VSR 驱动的机器** —— 本机 RTX 5080 支持，无法测试拒绝路径；
- **1440p 上限边界**（如 1440p 源放大到 4K）—— 未构造该素材；
- **对比模式（comparison mode）** —— 探针支持 `--comparison` 但本轮未测；
- **主观画质** —— 像素差异证明"生效"，但"是否更好看"需目视比对。

---

## 附：复现

```powershell
cd C:\PLAN\3FCompare\3fp\build

# ① 激活状态（必须硬解；720p/1080p 源 + 放大窗口）
.\vsr_probe.exe <素材> --auto --sr-window 2560x1440 --seconds 5

# ② SDR 链像素差异
.\vsr_pixel_probe.exe <素材> --sr-window 2560x1440

# ③ scRGB 链（需系统 HDR 开启；同时打印显示器 HDR 状态）
.\vsr_scrgb_probe.exe <HDR素材> --sr-window 2560x1440
```
