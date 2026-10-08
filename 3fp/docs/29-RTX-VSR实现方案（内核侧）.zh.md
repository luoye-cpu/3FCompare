# 29 · 3FP 实现 RTX Video Super Resolution 的具体方案

> 前置阅读：`docs/28-RTX-VSR与自动HDR播放器支持分析.zh.md`（机制、参考实现、官方规格）
> 本文档回答：**在 3FP 上具体怎么改**。
> 对象：`3fp/kernel/FFF.Native/3FP/`（播放器内核，非 3FC）。改动只落 `3fp/`，走 `patches/` 提上游 PR。
> 撰写日期：2026-10-05

---

## 0. 结论摘要

| 项 | 结论 |
|---|---|
| **可行性** | ✅ **高度可行**。3FP 已有完整 `ID3D11VideoProcessor` 通路（虽为死代码），**距离可用只差"接入 + 补扩展调用"** |
| **改动规模** | 约 **250~350 行**（含新文件），分 4 个提交 |
| **核心难点** | ⚠️ **不是 VSR 本身，而是与 3FP 现有"多趟着色器缩小链"的架构冲突**（见 §2） |
| **最大风险** | VSR 改变像素 ⇒ **破坏 3FCompare 的像素级可复现性**，必须默认关 + 对比模式强制禁用 |
| **硬件** | ✅ 本机 RTX 5080 + 10-bit PQ HDR 已就绪，**可实机闭环验证** |
| **官方规格** | 源 **360p~1440p**、必须**正在放大**、需显式 opt-in |

---

## 1. 3FP 渲染现状（已逐行核实）

### 1.1 主渲染路径

```
解码纹理 sourceTextures_[] (NV12/P010, BIND_DECODER)
  └─> sourceViews_[]  (plane 0/1/2 的 SRV)
        └─> DrawCachedVideo()                    VideoRenderer.cpp:4841
              ├─ CalculateVideoDestination()      → 目标矩形 destination
              ├─ PrepareScaledVideo(dest.w, dest.h, views)   :4914  ← 多趟缩小链
              │     └─ 每平面: EnsurePlaneScaleChain() :2995
              │           └─ ExecuteScalePass() 逐趟 :3084
              └─ DrawWithShader(target, dest...)  :4928
                    └─ 像素着色器采样 scaledSourceViews_，画到交换链
```

### 1.2 ★ 关键发现：现有缩放链是「只缩不放」的

`EnsurePlaneScaleChain`（`:2995`）有两条硬约束：

```cpp
if (targetWidth > sourceWidth || targetHeight > sourceHeight)
    return FFFResult::InvalidArgument;          // :3000-3002  拒绝放大
...
auto targetWidth = std::min(planeWidth, outputWidth);   // :3135  目标不超过源
auto targetHeight = std::min(planeHeight, outputHeight); // :3136
```

且 `PrepareScaledVideo` 的调用点也用 `min` 夹住目标：

```cpp
// VideoRenderer.cpp:3135-3136
const auto targetWidth  = std::min(planeWidth,  outputWidth);
const auto targetHeight = std::min(planeHeight, outputHeight);
```

**含义**：3FP 的着色器链**只负责降低到显示尺寸，从不放大**。放大是由最终的 `DrawWithShader` 在采样时用硬件双线性/各向异性完成的（`destination.width > sourceWidth_` 时）。

**这正是 3FP 拿不到 VSR 的根本原因**——放大发生在**像素着色器采样阶段**，而 VSR 要求放大发生在 **`VideoProcessorBlt`** 内部。§1.3 与官方 FAQ 的 *"only be enabled if the video requires upscaling"* 在此汇合。

### 1.3 死代码资产盘点

| 函数 | 行号 | 状态 |
|---|---|---|
| `EnsureVideoProcessor` | `:2805` | ✅ 实现完整（含格式/色彩空间校验） |
| `EnsureVideoProcessorInputSurface` | `:2908` | ✅ |
| `RenderVideoProcessorInput` | `:3176` | ✅ |
| `DrawWithVideoProcessor` | `:3184` | ✅ 含 `VideoProcessorBlt` |
| `CanUseDirectVideoProcessor` | `:2792` | ⚠️ 门槛过严（见 §3.1） |
| `ReleaseVideoProcessor` 等 | `:3230+` | ✅ |
| **调用点** | — | ❌ **一个都没有** |

---

## 2. ⚠️ 核心架构冲突（必须先解决这个，否则改了也没用）

### 2.1 冲突的本质

VSR 要求：**放大在 `VideoProcessorBlt` 内完成，输出一张已是目标分辨率的纹理**。
3FP 现状：**放大在最终 `DrawWithShader` 的采样器里完成**。

两条路互斥。直接"在 `PrepareScaledVideo` 里插一个 VP 分支"是**行不通的**，因为：

1. `PrepareScaledVideo` 的语义是"把平面缩到 `outputWidth × outputHeight`"，且**只缩不放**；
2. VP 通路需要的是"把整帧放大到 `destination` 尺寸"，是**另一个粒度**（整帧 vs 逐平面）；
3. `DrawWithVideoProcessor` 接收的是**已合成的整帧纹理**，而 3FP 在 `DrawCachedVideo` 阶段手上是**多平面**（NV12 的 Y/UV 分离）。

### 2.2 三种可选架构（推荐 B）

#### 方案 A：VP 直接接管整帧放大（**不推荐**）

让 `DrawWithVideoProcessor` 直接吃 NV12 解码纹理，`VideoProcessorBlt` 输出到 `destination` 尺寸的中转纹理，再由 `DrawWithShader` 以 1:1 画上去。

- ❌ **问题**：VP 会顺带做 YUV→RGB 色彩转换，而 3FP 的色彩管理（HDR 色调映射、scRGB、广色域）**全在像素着色器里**。让 VP 先转一次 RGB 会**绕过 3FP 整个色彩管线**，与 `docs/13`/`docs/20` 的既定契约冲突。
- ❌ 需要 `ID3D11VideoContext1::VideoProcessorSetStreamColorSpace1` 精确喂参数，且各驱动行为不一。
- ⇒ 这与 3FP "色彩由自己做"的设计哲学**根本对立**。

#### 方案 B：VP 只做「放大」这一步，色彩仍归 3FP（✅ **推荐**）

**把 VP 当作一个纯粹的"空间放大器"，串在解码纹理与现有缩放链之间：**

```
解码纹理 (NV12/P010 多平面)
  └─> ① VP 放大: VideoProcessorBlt(解码纹理 → 放大后的 NV12/P010 纹理)
  │        · 同格式进出(NV12→NV12)，不做色彩转换
  │        · 在此启用 NVIDIA PPE 扩展 ⇒ VSR 在 Blt 内生效
  └─> ② 走 3FP 原有链路(不变): sourceViews_ → PrepareScaledVideo → DrawWithShader
           · 色彩管理、HDR、scRGB 全部原样保留
```

**关键设计**：VP 的输入/输出**都是 NV12/P010**（同格式），只做分辨率放大，**不碰色彩**。这样：
- ✅ 3FP 的色彩管线**完全不受影响**（VSR 的输出仍是 YUV，继续走原路）
- ✅ 放大发生在 `Blt` 内部 ⇒ **官方"必须放大"条件满足**
- ✅ 与 `docs/13`/`docs/20` 的 scRGB/HDR 契约**零冲突**
- ✅ HDR 源也能受益（VSR 1.5+ 支持 HDR，见官方 FAQ）

**为什么这与 `PrepareScaledVideo` 的"只缩不放"不矛盾**（关键澄清）：

VSR 生效后，`PrepareScaledVideo` 拿到的"源"已经是**放大后的纹理**（尺寸 = 目标尺寸）。于是它计算的 `targetWidth = min(planeWidth, outputWidth)` 会**自然相等**，链路**退化为零趟**（`EnsurePlaneScaleChain` 的 `while` 条件不成立），直接返回放大纹理的视图。

⇒ **这正是设计的美妙之处**：VSR 只是把"源"换成了更大的源，`PrepareScaledVideo` 一行不用改逻辑（只把 `sourceViews_` 换成 `effectiveSourceViews_`），**缩放链自动退化**，`DrawWithShader` 变成 1:1 采样。

```
无 VSR:  1920x1080 源 ─[缩小链: 若有需要]─> DrawWithShader 采样放大到 2560x1440(双线性)
有 VSR:  1920x1080 源 ─[VP+VSR 放大]─> 2560x1440 纹理 ─[缩小链=0趟]─> 1:1 采样上屏
                                          ↑ 放大部分由 AI 完成
```

> ⚠️ **必须确认**：当 `destination` 小于源（缩小场景）时 **不应启用 VSR**（官方：只放大）。
> `ShouldEnableNvidiaSuperResolution` 已含 `targetWidth <= sourceWidth && targetHeight <= sourceHeight → false`。
> 且 VSR 输出纹理尺寸 = `destination` 尺寸，因此**不会**出现"放大后又缩小"的浪费。

> **代价**：多一次全帧 GPU 拷贝 + 一次 AI 推理。这正是 VSR 的固有成本，不可避免。
> **注意**：VP 输入输出的 buff 尺寸会变大（放大后），需按 `destination` 尺寸分配并缓存。

#### 方案 C：完全绕过 3FP 缩放链（**不推荐**）

让 VP 直接输出到交换链。⇒ 同方案 A 的色彩问题，且破坏 overlay/字幕合成顺序。**否决**。

---

## 3. 方案 B 的详细设计

### 3.1 第一步：拆分门槛函数（解决问题①）

现有 `CanUseDirectVideoProcessor`（`:2792`）把 VSR 与 TrueHDR 的条件混在一起，且含**与官方规格不符**的项。拆成两个：

```cpp
// VideoRenderer.h — 新增
/// VP 是否可用于「空间放大」这一步（与色彩模式无关）。
/// 只保留真正的 GPU/格式前提，不掺 HDR/SDR 判定。
bool CanUseVideoProcessorForUpscale() const noexcept;
/// NVIDIA RTX VSR 是否应当启用（在其上叠加厂商与官方规格门槛）。
bool ShouldEnableNvidiaSuperResolution(
    std::uint32_t sourceWidth, std::uint32_t sourceHeight,
    std::uint32_t targetWidth, std::uint32_t targetHeight) const noexcept;
```

```cpp
// VideoRenderer.cpp
bool PlayerVideoRenderer::CanUseVideoProcessorForUpscale() const noexcept {
    // 必须是 GPU 解码纹理（VP 的输入视图要求）
    if (sourceInputLayout_ != 2 || sourceTextures_[0] == nullptr || sourceInterlaced_)
        return false;
    if (sourceBitDepth_ > 10) return false;      // NV12(8) / P010(10)
    if (sourceCoverArt_) return false;           // 封面图不走视频通路
    if (projection360Enabled_.load(std::memory_order_acquire) != 0)
        return false;                            // 360 投影需按视角采样，VP 放大无意义
    D3D11_TEXTURE2D_DESC desc{};
    sourceTextures_[0]->GetDesc(&desc);
    return (desc.BindFlags & D3D11_BIND_DECODER) != 0;
}
```

> **相对旧版放宽了 4 项**（均有依据）：
> - 去掉 `!sourceExternal_`：**软解上传到 D3D11 同样可行**（官方 FAQ 未要求硬解；Chromium 用 staging texture 上传）
> - 去掉 `cachedVideoSettings_.transfer != 0` / `actualMode_ != MapToSdr`：**VSR 与色彩模式无关**（官方 FAQ：VSR 也放大 HDR 视频）
> - 去掉 `swapOutputBits_ > 10`：与 VSR 无关，且会误杀 scRGB 路径
> - 放宽 `sourceChromaLocation_ != AVCHROMA_LOC_LEFT`：接受 `UNSPECIFIED`（大量片源如此）

```cpp
bool PlayerVideoRenderer::ShouldEnableNvidiaSuperResolution(
    const std::uint32_t sourceWidth, const std::uint32_t sourceHeight,
    const std::uint32_t targetWidth, const std::uint32_t targetHeight) const noexcept {
    // 用户开关 + 开关之外的运行时条件
    if (!superResolutionEnabled_.load(std::memory_order_acquire)) return false;
    if (!nvidiaSuperResolutionAvailable_) return false;   // §3.2 探测结果
    if (!CanUseVideoProcessorForUpscale()) return false;

    // ★ 必须真的在放大 —— 官方唯一硬条件
    if (targetWidth <= sourceWidth && targetHeight <= sourceHeight) return false;

    // ★ 官方规格：输入分辨率 360p ~ 1440p（按源高度判定）
    if (sourceHeight < 360 || sourceHeight > 1440) return false;

    return true;
}
```

> **`360p~1440p` 的判定口径**：官方 FAQ 按 "360p/1440p" 表述（即**高度**）。3FP 应按 `sourceHeight` 判定。
> 对 2.35:1 宽银幕等非常规比例，官方未明确按高还是按宽——**建议按高度**（与 Kodi 的 `iWidth > 2560` 不同，但 Kodi 是宽银幕不敏感的近似）。
> ⚠️ **需实机确认**：是否应按"较短边"判定。可先用高度，实测后再定。

### 3.2 第二步：NVIDIA 能力探测（解决问题②）

**新增文件** `3fp/kernel/FFF.Native/3FP/Render/NvidiaVideoExtensions.h`：

```cpp
#pragma once
// NVIDIA 驱动级 Video Processor 私有扩展。
// GUID 与结构体取自公开实现（五份独立源码逐字节一致）：
//   Chromium ui/gl/swap_chain_presenter.cc
//   mpv      video/filter/vf_d3d11vpp.c
//   VLC      modules/video_output/win32/d3d11_scaler.cpp
//   Kodi     xbmc/.../HwDecRender/DXVAHD.cpp
//   MPCVR    Source/D3D11VP.cpp
// 技术公开、无授权门槛：https://docs/28

#include <d3d11.h>
#include <dxgi.h>

namespace FFF3FP::NvidiaVideo {

inline constexpr GUID PpeInterfaceGuid{
    0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};
inline constexpr UINT StreamExtensionVersionV1 = 0x1;
inline constexpr UINT StreamExtensionMethodSuperResolution = 0x2;

struct SuperResolutionStreamExtension {
    UINT version;
    UINT method;
    UINT enable;
};

inline constexpr UINT NvidiaVendorId = 0x10DEU;

/// 适配器是否为 NVIDIA（Chromium 用同一判据）。
inline bool IsNvidiaAdapter(ID3D11Device* device) noexcept {
    IDXGIDevice* dxgiDevice = nullptr;
    if (device == nullptr ||
        FAILED(device->QueryInterface(__uuidof(IDXGIDevice),
            reinterpret_cast<void**>(&dxgiDevice))) || dxgiDevice == nullptr)
        return false;
    IDXGIAdapter* adapter = nullptr;
    const auto got = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(got) || adapter == nullptr) return false;
    DXGI_ADAPTER_DESC desc{};
    const auto described = adapter->GetDesc(&desc);
    adapter->Release();
    return SUCCEEDED(described) && desc.VendorId == NvidiaVendorId;
}

/// 探测驱动是否支持 VSR 扩展。
/// ⚠ 注意：GetStreamExtension 只在内存大小非法时才 FAILED(hr)，
/// 真正的支持信号在 out 参数里（Chromium 源码原文警告）。
/// 但 NVIDIA 侧并无可靠的 Get 探测（Chromium/mpv 都只靠 Set + Blt 失败判定），
/// 因此本函数返回"适配器是 NVIDIA"这一必要条件，最终可用性由 Blt 失败降级确定。
HRESULT SetSuperResolution(ID3D11VideoContext* videoContext,
    ID3D11VideoProcessor* videoProcessor, bool enable) noexcept;

} // namespace FFF3FP::NvidiaVideo
```

```cpp
// NvidiaVideoExtensions.cpp
HRESULT SetSuperResolution(ID3D11VideoContext* videoContext,
    ID3D11VideoProcessor* videoProcessor, const bool enable) noexcept {
    if (videoContext == nullptr || videoProcessor == nullptr) return E_POINTER;
    const SuperResolutionStreamExtension ext{
        StreamExtensionVersionV1, StreamExtensionMethodSuperResolution, enable ? 1u : 0u};
    return videoContext->VideoProcessorSetStreamExtension(
        videoProcessor, 0, &PpeInterfaceGuid, sizeof(ext), &ext);
}
```

> **为什么不做 `GetStreamExtension` 探测？**
> NVIDIA 的 PPE 扩展**没有公开的 Get 语义**（TrueHDR 有，VSR 没有）。Chromium 与 mpv 都**直接 Set + 靠 Blt 失败降级**。3FP 照此办理（见 §3.6）。

### 3.3 第三步：VP「放大器」的建立与缓存

**新增成员**（`VideoRenderer.h`）：

```cpp
// ---- RTX VSR（VP 空间放大）----
// 与 videoProcessor_ 系列不同：这是一条「同格式放大」专用通路，
// 输入/输出都是 NV12/P010，不参与色彩转换。
ID3D11Texture2D* superResolutionTexture_ = nullptr;   // 放大后的 YUV 纹理
ID3D11ShaderResourceView* superResolutionViews_[3]{}; // 其平面 SRV（喂回原链路）

// 探测与状态
bool nvidiaAdapter_ = false;
bool nvidiaSuperResolutionAvailable_ = false;   // 一次性探测
bool nvidiaSuperResolutionFailed_ = false;      // Blt 失败后永久关闭（sticky）
std::atomic<bool> superResolutionEnabled_{ false };  // 用户开关（默认关）
// 当前 SR 通路是否真的在用（诊断用，喂给 Snapshot）
std::atomic<bool> superResolutionActive_{ false };

// 缓存键：源尺寸/格式 + 目标尺寸，避免每帧重建
std::uint32_t superResolutionSourceWidth_ = 0;
std::uint32_t superResolutionSourceHeight_ = 0;
std::uint32_t superResolutionTargetWidth_ = 0;
std::uint32_t superResolutionTargetHeight_ = 0;
std::uint32_t superResolutionFormat_ = 0;
bool superResolutionConfigured_ = false;
```

**放大执行**（在 `EnsurePipeline` 之后、`PrepareScaledVideo` 之前调用）：

```cpp
FFFResult PlayerVideoRenderer::ApplySuperResolution(const std::uint32_t targetWidth,
    const std::uint32_t targetHeight) noexcept {
    const auto disable = [this]() noexcept {
        nvidiaSuperResolutionFailed_.store(true, std::memory_order_release);
        superResolutionActive_.store(false, std::memory_order_release);
        return FFFResult::NotSupported;
    };

    if (sourceTextures_[0] == nullptr || sourceViews_[0] == nullptr)
        return FFFResult::InvalidState;
    if (!ShouldEnableNvidiaSuperResolution(sourceWidth_, sourceHeight_,
            targetWidth, targetHeight))
        return FFFResult::Success;   // 不适用不算失败

    // 需要 ID3D11VideoDevice / VideoContext
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext> videoContext;
    if (FAILED(device_->QueryInterface(IID_PPV_ARGS(&videoDevice))) ||
        FAILED(context_->QueryInterface(IID_PPV_ARGS(&videoContext))))
        return disable();

    D3D11_TEXTURE2D_DESC sourceDesc{};
    sourceTextures_[0]->GetDesc(&sourceDesc);

    // 目标纹理：与源同格式（NV12/P010），尺寸 = 放大后
    const bool sameConfig = superResolutionConfigured_ &&
        superResolutionSourceWidth_ == sourceWidth_ &&
        superResolutionSourceHeight_ == sourceHeight_ &&
        superResolutionTargetWidth_ == targetWidth &&
        superResolutionTargetHeight_ == targetHeight &&
        superResolutionFormat_ == sourceDesc.Format;
    if (!sameConfig) {
        ReleaseSuperResolutionTexture();
        D3D11_TEXTURE2D_DESC description{};
        description.Width = targetWidth;
        description.Height = targetHeight;
        description.MipLevels = description.ArraySize = 1;
        description.Format = sourceDesc.Format;      // ★ 同格式，不转色彩
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(device_->CreateTexture2D(&description, nullptr,
                &superResolutionTexture_))) {
            ReleaseSuperResolutionTexture();
            return disable();
        }
        if (!CreateYuvPlaneViews(superResolutionTexture_, sourceDesc.Format,
                superResolutionViews_)) {
            ReleaseSuperResolutionTexture();
            return disable();
        }
        superResolutionSourceWidth_ = sourceWidth_;
        superResolutionSourceHeight_ = sourceHeight_;
        superResolutionTargetWidth_ = targetWidth;
        superResolutionTargetHeight_ = targetHeight;
        superResolutionFormat_ = sourceDesc.Format;
        superResolutionConfigured_ = true;
    }

    // 建立 VP（enumerator + processor）：输入源尺寸 → 输出放大尺寸，同格式
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
    content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputFrameRate = { 60, 1 };
    content.InputWidth = sourceWidth_;
    content.InputHeight = sourceHeight_;
    content.OutputFrameRate = content.InputFrameRate;
    content.OutputWidth = targetWidth;
    content.OutputHeight = targetHeight;
    content.Usage = D3D11_VIDEO_USAGE_OPTIMAL_QUALITY;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    if (FAILED(videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator)) ||
        FAILED(videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor)))
        return disable();

    UINT inputSupport = 0, outputSupport = 0;
    if (FAILED(enumerator->CheckVideoProcessorFormat(sourceDesc.Format, &inputSupport)) ||
        FAILED(enumerator->CheckVideoProcessorFormat(sourceDesc.Format, &outputSupport)) ||
        (inputSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
        (outputSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        return disable();

    videoContext->VideoProcessorSetStreamFrameFormat(processor.Get(), 0,
        D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    // 保持与 3FP 既有策略一致：不做驱动隐式处理（锐化/降噪）。
    // ⚠ 待实机确认这是否影响 VSR 生效（mpv/VLC 同样关掉它却仍能开 VSR）。
    videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);

    const RECT source{0, 0, static_cast<LONG>(sourceWidth_),
        static_cast<LONG>(sourceHeight_)};
    videoContext->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &source);
    // ★ 全目标 = 放大
    const RECT destination{0, 0, static_cast<LONG>(targetWidth),
        static_cast<LONG>(targetHeight)};
    videoContext->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &destination);

    // ★★ 启用 RTX VSR（核心一行）
    if (FAILED(NvidiaVideo::SetSuperResolution(videoContext.Get(), processor.Get(), true))) {
        SetError("NVIDIA RTX Super Resolution extension was rejected by the driver.");
        return disable();
    }

    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDescription{};
    inputDescription.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11VideoProcessorInputView> inputView;
    if (FAILED(videoDevice->CreateVideoProcessorInputView(sourceTextures_[0],
            enumerator.Get(), &inputDescription, &inputView)))
        return disable();

    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputDescription{};
    outputDescription.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11VideoProcessorOutputView> outputView;
    if (FAILED(videoDevice->CreateVideoProcessorOutputView(superResolutionTexture_,
            enumerator.Get(), &outputDescription, &outputView)))
        return disable();

    D3D11_VIDEO_PROCESSOR_STREAM stream{};
    stream.Enable = TRUE;
    stream.OutputIndex = 0;
    stream.InputFrameOrField = 0;
    stream.pInputSurface = inputView.Get();
    if (FAILED(videoContext->VideoProcessorBlt(processor.Get(), outputView.Get(), 0, 1,
            &stream)))
        return disable();    // ★ 降级：永久关闭 SR，本帧回落到原路径

    // 让后续链路改用放大后的纹理
    for (std::size_t plane = 0; plane < ARRAYSIZE(superResolutionViews_); ++plane)
        sourceViews_[plane] = superResolutionViews_[plane];
    superResolutionActive_.store(true, std::memory_order_release);
    return FFFResult::Success;
}
```

> **注意 `sourceViews_` 的归属**：`sourceViews_[]` 由 `EnsurePipeline`（`:2644`）创建并持有，**只在管线配置变化时重建**（`:2779-2780`）。它代表"解码帧的平面视图"，是管线的**长期状态**。
> ⇒ **绝不能在 `ApplySuperResolution` 里直接改写 `sourceViews_`**，否则会污染管线状态、下一帧错乱。
> **正确做法**：新增独立的 `effectiveSourceViews_[3]`，每次渲染由 `sourceViews_` 起算：
> ```cpp
> // DrawCachedVideo 开头（或 PrepareScaledVideo 之前）
> for (std::size_t i = 0; i < ARRAYSIZE(effectiveSourceViews_); ++i)
>     effectiveSourceViews_[i] = sourceViews_[i];        // 默认 = 原始解码平面
> if (ApplySuperResolution(dw, dh) == FFFResult::Success &&
>     superResolutionActive_.load(std::memory_order_acquire)) {
>     for (std::size_t i = 0; i < ARRAYSIZE(effectiveSourceViews_); ++i)
>         effectiveSourceViews_[i] = superResolutionViews_[i];  // 换成放大后的平面
> }
> // 把 effectiveSourceViews_ 传给 PrepareScaledVideo
> ```
> 这样 **`sourceViews_` 保持纯净**，VSR 的有无只影响一次局部数组赋值。（对应 §5 风险 R3。）

> **平面视图的创建格式**（与 `EnsurePipeline:2730-2735` 保持一致，务必对齐，否则颜色会错）：
> ```cpp
> D3D11_SHADER_RESOURCE_VIEW_DESC luma{};
> luma.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
> luma.Texture2D.MipLevels = 1;
> luma.Format = bitDepth > 8 ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
> auto chroma = luma;
> chroma.Format = bitDepth > 8 ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;
> // NV12: views[0]=luma, views[1]=chroma（同一张纹理的两个视图）
> ```
> ⚠️ VP 输出的是 **NV12/P010 打包纹理**（单张，Y 与 UV 在同一 resource 的不同 plane），
> 因此 `CreateShaderResourceView` 需按上述 plane 格式建**两个视图指向同一张纹理**——与 3FP 现有做法一致。

### 3.4 第四步：接上调用点

**调用时序**（必须严格按此顺序，否则会用到上一帧的尺寸）。`ProcessFrame`（`:4326`）的现有顺序是：

```
EnsureSwapChain(frame->width, frame->height, bitDepth)   :4323
EnsurePipeline(width, height, layout, ...)               :4326   ← sourceViews_ 在此就绪
  ...（Dolby Vision 颜色扩展等）...
DrawCachedVideo(target)                                  :4841
    ├─ CalculateVideoDestination()  → destination
    ├─ ★ ApplySuperResolution(destination.w, destination.h)   ← 插在这里
    ├─ PrepareScaledVideo(...)
    └─ DrawWithShader(...)
```

⇒ **VSR 的正确插入点是 `DrawCachedVideo` 内、`CalculateVideoDestination` 之后**（因为**只有到这一步才知道目标尺寸**，而"是否在放大"正是官方唯一硬条件）。

```cpp
// VideoRenderer.cpp — DrawCachedVideo 内，替换 :4906-4917
ID3D11ShaderResourceView* presentationViews[3]{};
if (projection360) {
    std::copy(std::begin(sourceViews_), std::end(sourceViews_), presentationViews);
} else {
    // ★ 1) 先尝试 RTX VSR：把解码纹理放大到目标尺寸
    //     不适用（非 NVIDIA / 未开 / 未放大 / 源分辨率越界）时静默跳过
    //     真正失败（Blt 被拒）时内部已 sticky 降级，这里也按跳过处理
    (void)ApplySuperResolution(destination.width, destination.height);

    // ★ 2) 用「有效平面视图」走原有链路（VSR 生效时是放大后的纹理）
    const auto scaleResult = PrepareScaledVideo(destination.width, destination.height,
        presentationViews);
    if (scaleResult != FFFResult::Success) return scaleResult;
}
```

并将 `ApplySuperResolution` 改为**同时更新 `effectiveSourceViews_`**，且让 `PrepareScaledVideo` 从它取源——需把 `PrepareScaledVideo` 内对 `sourceViews_` 的引用（`:3127`/`:3156`）改为 `effectiveSourceViews_`：

```cpp
// PrepareScaledVideo 内
if (plane >= planeCount || effectiveSourceViews_[plane] == nullptr) { ... }
...
auto* currentView = effectiveSourceViews_[plane];
```

> **设计要点**：`effectiveSourceViews_` 是**每帧重建的局部真相**，`sourceViews_` 是**管线长期状态**。二者分离后，VSR 的开/关/中途失效都不会污染管线。

并把 `:4932` 的硬编码改为按实际路径写：

```cpp
// 原：actualVideoScalingMode_.store(FFF3FPVideoScalingMode::Shader);
if (result == FFFResult::Success) {
    actualVideoScalingMode_.store(
        superResolutionActive_.load(std::memory_order_acquire)
            ? FFF3FPVideoScalingMode::D3D11VideoProcessor
            : FFF3FPVideoScalingMode::Shader);
    // ...
```

### 3.5 第五步：对外 API（默认关 + 诚实措辞）

`FFF.Player.Api.h` **尾部追加**（保持 ABI 兼容，`version` 递增到 17）：

```cpp
enum class FFF3FPVideoSuperResolution : std::uint32_t {
    Off = 0,        // 默认
    Auto = 1,       // 满足官方条件时启用
};

// --- API v17 追加 ---
FFF3FP_API FFFResult FFF3FP_SetVideoSuperResolution(FFF3FPHandle player,
    FFF3FPVideoSuperResolution mode) noexcept;

struct FFF3FPVideoSuperResolutionStatus {
    std::uint32_t size;
    std::uint32_t version;          // 1
    FFF3FPVideoSuperResolution requested;
    // 适配器是否为 NVIDIA（VendorId == 0x10DE）
    std::uint32_t nvidiaAdapter;
    // 驱动接受并成功 Blt 过至少一帧（"已请求且未失败"）
    std::uint32_t active;
    // 因 Blt 失败被永久关闭（sticky）
    std::uint32_t driverRejected;
    // 本次未启用的原因（0=无, 1=非NVIDIA, 2=未在放大, 3=源分辨率越界, 4=格式不支持）
    std::uint32_t ineligibleReason;
    std::uint32_t sourceWidth;
    std::uint32_t sourceHeight;
};
FFF3FP_API FFFResult FFF3FP_GetVideoSuperResolutionStatus(FFF3FPHandle player,
    FFF3FPVideoSuperResolutionStatus* status) noexcept;
```

> **⚠️ 措辞必须诚实**（业界共识，见 `docs/28` §3.4）：字段名叫 `active` 而非 `enabled`，且**注释明确说明它只代表"驱动接受了请求且 Blt 未失败"**，不代表画质一定提升。官方 FAQ 也证实 NVIDIA 自己需要专门的 status indicator。

**`FFF3FPSnapshot` 追加**（尾部）：`std::uint32_t videoSuperResolution;`（0=off, 1=active）。

### 3.6 第六步：降级与诊断日志

```cpp
// Blt 失败时（ApplySuperResolution 内 disable()）：
SetError("NVIDIA RTX Super Resolution was rejected; falling back to shader scaling.");
// 并打印一次日志（仿 VLC 的 "turning VSR ON/OFF"）：
//   "[3fp] RTX VSR: unavailable (driver rejected the extension), disabled for this session."
//   "[3fp] RTX VSR: active (1920x1080 -> 2560x1440)."
```

**降级必须是 sticky 的**：一旦 `nvidiaSuperResolutionFailed_ = true`，本会话不再尝试——避免每帧重复失败的开销（Chromium 用 `enable_vp_super_resolution_ = false` 的做法）。

### 3.7 第七步：与 3FCompare 互斥（**安全要求，非可选项**）

VSR 会**改变像素**，直接破坏 3FC 的像素级对比可复现性，也与 `VideoRenderer.cpp:2888-2890` 那条"保持可复现的渲染契约"注释矛盾。

```cpp
// 对比/剪辑模式下强制禁用。
// ⚠ 现有代码中【没有】comparisonModeActive_ 这个成员——它是本方案的**新增项**。
//    3FP 已有模式隔离机制（见 docs/00 的模式开关），应复用同一套开关，
//    而不是另造一个标志，以免出现"两个真相"。
FFFResult PlayerVideoRenderer::SetVideoSuperResolution(
    const FFF3FPVideoSuperResolution mode) noexcept {
    if (mode != FFF3FPVideoSuperResolution::Off && IsComparisonModeActive())
        return FFFResult::InvalidState;
    superResolutionEnabled_.store(mode == FFF3FPVideoSuperResolution::Auto,
        std::memory_order_release);
    return FFFResult::Success;
}
```

> **落点提示**：3FP 的模式隔离有既定设施（`3fp/docs/00-图片模式设计规格.zh.md` §2 的模式开关）。
> 本方案应**挂到同一套开关上**：进入对比/剪辑/图片模式时**自动关掉 VSR**，无需新增独立标志。

**建议默认值**：`Off`。参考 mpv（`scaling-mode=standard` 默认）与 VLC（`d3d11-upscale-mode=linear` 默认）——**两家都默认关**。

---

## 4. 分阶段实施与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P0 实测** | 用 VLC（`--d3d11-upscale-mode=super`）确认本机 VSR 生效；用 3FP 确认 `videoScalingMode` 恒为 `Shader` | VLC 侧 NVIDIA 指示器亮；3FP 侧恒 `Shader` |
| **P1 探测** | 只加 `NvidiaVideoExtensions.h` + `SetVideoSuperResolution` + `Snapshot` 字段，**不改渲染** | 新 API 返回 `nvidiaAdapter=1`；渲染行为**逐字节不变** |
| **P2 放大器** | 实现 `ApplySuperResolution` + `effectiveSourceViews_`，但**默认关**；用探针强制打开做单帧验证 | 打开后 `videoScalingMode=D3D11VideoProcessor`；`PrepareScaledVideo` 趟数为 0；像素读数合理 |
| **P3 接入** | 接 `DrawCachedVideo`；加降级与日志 | 关闭时**零回归**（`verify_image_mode` / 宿主测试） |
| **P4 互斥** | 与对比模式互斥；UI 开关 | 对比模式下拒绝启用 |

**回归要求**（沿用项目铁律）：关闭 SR 时，视频路径行为**逐字节不变**——`3fp/README.md` 铁律 2 明文要求。

---

## 5. 风险清单

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| **R1** | **VSR 破坏像素级可复现性** | 🔴 高——动摇 3FC 核心用途 | 默认 `Off` + 对比模式强制禁用（§3.7） |
| **R2** | `AutoProcessingMode(FALSE)` 可能阻断 VSR | 🟡 中 | P0 实测；若阻断则改为 `TRUE` 或延后设置 |
| **R3** | 直接改 `sourceViews_` 会累积状态错误 | 🟡 中 | **改为新增 `effectiveSourceViews_`**，每帧由原始视图重建 |
| **R4** | 放大后纹理显存翻倍（1440p→4K 约 4 倍） | 🟡 中 | 缓存单张 + 尺寸变化即释放；4K 下 NV12 约 12MB，可接受 |
| **R5** | **启用后 MPO 被系统禁用** | 🟡 中 | 官方 FAQ 明确；可能影响 3FP overlay/多窗口表现，需实测 |
| **R6** | 多路同屏时只有"焦点视频"被增强 | 🟡 中 | 官方 FAQ 明确；3FP 多路场景需明确策略（建议仅当前路启用） |
| **R7** | 官方 `360p~1440p` 是"按高还是按短边"未明 | 🟢 低 | 先用高度，实测后定 |
| **R8** | 各驱动对 PPE 扩展行为不一 | 🟢 低 | sticky 降级已覆盖 |

---

## 6. 关键结论

1. **3FP 的 VSR 接入点不在 `PrepareScaledVideo` 内部，而在它之前**——因为 3FP 的着色器链**只缩不放**（`EnsurePlaneScaleChain:3000` 显式拒绝放大、`:3135` 用 `min` 夹目标），而 VSR 要求放大在 `Blt` 内发生。
2. **正确架构是"VP 只做同格式空间放大，色彩仍归 3FP"**（方案 B）：VP 输入输出都是 NV12/P010，不碰色彩 ⇒ 对既有 scRGB/HDR 色彩管线零侵入。
3. **`PrepareScaledVideo` 只需一处改动**（`sourceViews_` → `effectiveSourceViews_`）：VSR 生效后源已等于目标尺寸，缩小链**自动退化为 0 趟**，`DrawWithShader` 变 1:1 采样。**无需改动缩放逻辑本身。**
4. **必须新增 `effectiveSourceViews_`，不能改写 `sourceViews_`**——后者是 `EnsurePipeline` 持有的管线长期状态，改写会造成跨帧污染。
5. **改动量可控（250~350 行）**，且 P1 阶段即可完成"可探测但不影响渲染"，风险最低。
6. **真实难点是工程纪律而非技术**：默认关、对比模式互斥、措辞诚实（`active` 而非 `enabled`）、sticky 降级。
7. **本机 RTX 5080 + HDR 就绪**，P0 可立即实测，无需等待硬件。

> **上游 PR 建议**：VSR 属**新增功能**，符合 `3fp/README.md` 铁律 2（"只加不改"）——应由模式开关门控，关闭时不可达。建议作为独立 PR（可命名 `feature/rtx-video-super-resolution`），与图片模式 PR 分开，便于上游单独评估。
