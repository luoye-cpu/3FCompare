# 28 · NVIDIA RTX 视频超分（VSR）与自动 HDR（TrueHDR）在播放器上的支持机制分析

日期：2026-09-18（撰写）
分析对象：3FP（FFF.Player）自研 D3D11 渲染引擎，以及 mpv / VLC 等主流播放器的实现
结论先行：**3FP 已经具备接入 RTX VSR / RTX Video HDR 的全部结构基础**——`ID3D11VideoProcessor` 通路已经写好——但该通路目前是**死代码**（定义后从未被调用），且现有条件门槛（`CanUseDirectVideoProcessor`）把绝大多数真实工况都排除了。要真正支持，需要「启用 + 放开门槛 + 补 NVIDIA 私有扩展 GUID + 把 HDR 生成纳入 HDR 管线」四步。

---

## 1. 最重要的事实：RTX VSR/HDR 有两条完全不同的 API 通路

这是整个分析的地基。**"NVIDIA RTX 视频增强"在开发者侧对应两套互不相同的 API**，网上讨论经常把它们混为一谈，导致结论互相矛盾。

| | **通路 A：驱动级 Video Processor 扩展** | **通路 B：RTX Video SDK（NGX）** |
|---|---|---|
| 本质 | 在 `ID3D11VideoProcessor` 上调用厂商私有 COM 扩展 | 独立的 NGX 推理 SDK，输入/输出是一对纹理 |
| 调用方式 | `ID3D11VideoContext::VideoProcessorSetStreamExtension` + 厂商 GUID | `NVSDK_NGX_D3D11_Init` / `NGX_D3D11_CREATE_VSR_EXT` / `NGX_D3D11_EVALUATE_VSR_EXT` |
| 依赖 DLL | 无（驱动内置） | 驱动提供的 `nvngx_vsr.dll` / `nvngx_truehdr.dll` |
| 需要额外授权 | 否 | 需要（SDK 下载、`appId`） |
| 典型使用者 | **mpv `vf_d3d11vpp`、VLC `d3d11_scaler`、Chrome/Edge** | 需要精细控制的独立应用 |
| 对播放器改动量 | 小（几十行） | 大（新依赖、新生命周期） |

**对 3FP 而言，通路 A 是唯一现实的选择**，理由：
1. 3FP 已经有完整、可用的 `ID3D11VideoProcessor` 代码（见 §3）；
2. 通路 A 是"调一次扩展函数"级别的改动，不引入新的第三方二进制依赖，符合项目"没有第三方插手、全 GPU 渲染链路"的定位；
3. 主流开源播放器（mpv、VLC）全部选择通路 A，有可直接对照的参考实现。

> ⚠️ 注意区分第三个易混概念：**NVIDIA Video Effects SDK（Maxine VFX）** 里的 `SuperRes`/`Upscale` 效果是另一套东西（OBS 的 RTX 超分插件用的是它），与播放器场景的 RTX VSR 不是同一个 API。本报告不涉及。
>
> **特别提醒**：网上常见的「源必须 ≥160×90」「宽高比 ≤1920×1080」「不支持 4:3 放大」等数字，属于 **Maxine VFX SDK**，**不是** NGX RTX Video SDK 的约束。混淆这两个会得出错误结论。

### 1.1 通路 A 有五份互相独立的实现作为交叉验证

这是本次分析可信度的关键。**五个互不相关的代码库给出了逐字节相同的 GUID 与结构体**：

| 来源 | 文件 | 角色 |
|---|---|---|
| **Chromium** | `ui/gl/swap_chain_presenter.cc` | 浏览器（生产环境规模最大） |
| **mpv** | `video/filter/vf_d3d11vpp.c` | 开源播放器 |
| **VLC** | `modules/video_output/win32/d3d11_scaler.cpp` | 开源播放器 |
| **Kodi** | `xbmc/.../HwDecRender/DXVAHD.cpp` | 开源媒体中心 |
| **MPC Video Renderer** | `Source/D3D11VP.cpp` | MPC-HC/BE/PotPlayer 共用的渲染器 |

Kodi 的源码注释里**直接引用了 Chromium** 作为参考：
```cpp
// magic constants taken from Chromium:
// https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/gl/swap_chain_presenter.cc#180
```
五份独立实现得出同一组常量，基本排除了"某个项目写错"的可能。这也说明**该技术完全公开、无授权门槛**——播放器之间的差异**纯粹在于默认走哪条缩放路径**。

---

## 2. 通路 A 的确切机制（已从 mpv / VLC 源码逐字核实）

### 2.1 核心洞察：VSR 不是"一个开关"，而是"在 Video Processor 缩放时启用厂商后端"

`ID3D11VideoProcessor` 本来就是 D3D11 的**视频缩放/色彩转换**组件。NVIDIA 驱动把它在"放大"时的插值后端**替换成 AI 超分网络**，条件是应用通过私有扩展显式要求。

所以整条链路是：

```
解码纹理 (NV12/P010, D3D11_BIND_DECODER)
   └─> CreateVideoProcessorInputView
         └─> VideoProcessorBlt  ← 缩放发生在这里
               │  在此之前调用 VideoProcessorSetStreamExtension(NVIDIA_PPE_GUID, ...)
               │  驱动就用 AI 超分替代普通缩放
               └─> CreateVideoProcessorOutputView → 放大后的纹理
                     └─> 再进播放器自己的着色器做色彩/色调映射 → 上屏
```

**关键推论：超分发生在"Video Processor 输出"这一步，输出的是目标分辨率的纹理。播放器自己的缩放着色器必须让位**——否则你就是拿一个普通缩放的结果再去缩放，AI 超分无从发挥。

### 2.2 NVIDIA RTX VSR 的私有扩展（mpv / VLC 完全一致）

GUID 与结构体在两个项目中逐字节相同：

```cpp
// NVIDIA_PPE_INTERFACE_GUID
DEFINE_GUID(NVIDIA_PPE_INTERFACE_GUID,
            0xd43ce1b3, 0x1f4b, 0x48ac, 0xba, 0xee,
            0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7);

struct {
    UINT version;   // = 1
    UINT method;    // = 2   (RTX VSR)
    UINT enable;    // = 1 开 / 0 关
} ext = { 1, 2, 1 };

hr = videoContext->VideoProcessorSetStreamExtension(
        videoProcessor, 0, &NVIDIA_PPE_INTERFACE_GUID, sizeof(ext), &ext);
```

要点：
- 用 **`SetStreamExtension`**（流级），不是 `SetOutputExtension`（输出级）——Intel 才用 Output 级。
- `enable` 可以**逐帧切换**。VLC 就是这么做的：只有当「源分辨率 < 显示区域」时才置 1，否则置 0（避免缩小画面时被 AI 放大）。这比"全局开关"正确得多。
- 调用后**没有返回值可直接判定质量**；失败只表现为 `FAILED(hr)`。

### 2.3 RTX Video HDR（TrueHDR）的私有扩展

```cpp
// NVIDIA_TRUE_HDR_INTERFACE_GUID
DEFINE_GUID(NVIDIA_TRUE_HDR_INTERFACE_GUID,
            0xfdd62bb4, 0x620b, 0x4fd7, 0x9a, 0xb3,
            0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3);

// 先探测是否支持
UINT supported = 0;
hr = videoContext->VideoProcessorGetStreamExtension(
        videoProcessor, 0, &NVIDIA_TRUE_HDR_INTERFACE_GUID,
        sizeof(supported), &supported);
if (FAILED(hr) || !supported) { /* 不支持 */ }

// 再启用（mpv 逐字写法）
struct nvidia_ext {
    unsigned int version;
    unsigned int method;
    unsigned int enable : 1;
    unsigned int reserved : 31;
} ext = {4, 3, 1};      // version=4, method=3, enable=1

hr = videoContext->VideoProcessorSetStreamExtension(
        videoProcessor, 0, &NVIDIA_TRUE_HDR_INTERFACE_GUID, sizeof(ext), &ext);
```

注意 TrueHDR 的 `version` 是 **4**、`method` 是 **3**，与 VSR 的 `1`/`2` 不同——这是两个独立的功能 ID。

**这是 VSR 与 TrueHDR 最重要的差异：TrueHDR 必须先 `Get` 探测、再 `Set` 启用；VSR 只需 `Set`。** 这暗示 TrueHDR 的可用性依赖更多前置条件（输出色彩空间、驱动版本）。

### 2.4 致命约束：TrueHDR 要求输出是 BT.2020 + PQ

mpv 在启用 TrueHDR 后**强制改写输出参数**，这是硬性要求而非建议：

```c
if (want_nvidia_true_hdr(vf) && enable_nvidia_true_hdr(vf)) {
    // NVIDIA RTX Video HDR seems to require BT.2020+PQ RGB output.
    p->out_params.color = pl_color_space_hdr10;      // BT.2020 + PQ
    p->out_params.color.hdr.max_luma = 1000;
    p->out_params.hw_subfmt = IMGFMT_X2BGR10;        // DXGI_FORMAT_R10G10B10A2_UNORM
}
```

**这一段对 3FP 有直接且严重的冲击，见 §4.5（差距四）。**

#### ⚠️ 最大的实践陷阱：只调扩展是不够的，必须同时重标输出为 HDR10

这是一个**真实发生过的严重 bug**，值得 3FP 引以为戒：

> mpv **0.41.0** 版本"启用了驱动扩展，但从未把输出重标为 HDR10，导致出来的帧完全相同。开与不开截图的 SHA256 一模一样，无论 SDR 还是 HDR 源。"

mpv 随后用两个提交修复（SHA 已核实）：
- `6ca3752f7`（2024-09-23）*vf_d3d11vpp: add NVIDIA RTX Video HDR support*
- `f7ad4a5f8`（2026-02-19）*vf_d3d11vpp: set output format for NVIDIA RTX Video HDR* —— 提交信息：*"It outputs RGB tone mapped image, set params accordingly."*
- `5abce15f1`（2026-06-28）*enable NVIDIA RTX Video only for SDR input* —— 提交信息明确：*"This filter only converts SDR to HDR, so disable it if we have already HDR on input."*

**教训：启用 TrueHDR 扩展 ≠ 得到 HDR。必须同时（a）把输出色彩空间标为 BT.2020+PQ，（b）使用 10-bit 表面。** 否则结果与 SDR 逐位相同——而且**不会报任何错**。这是"我开了但没效果"的最可能原因。

这正是 §4.5 所说"3FP 已有 scRGB 管线会造成冲突"的具体后果：如果 3FP 只调用扩展而不改输出契约，就会**精确复现 mpv 0.41.0 的这个 bug**。

### 2.5 一个常见误解：`SetStreamAutoProcessingMode(FALSE)` 不会关掉 VSR

mpv 和 VLC 都在 Video Processor 上显式调用：

```c
// mpv 注释原文: "This is supposed to stop drivers from fucking up the video quality."
ID3D11VideoContext_VideoProcessorSetStreamAutoProcessingMode(ctx, proc, 0, FALSE);
```

3FP 现有代码也调用了这一句（`VideoRenderer.cpp:2891`），注释理由是"保持可复现的渲染契约"。

**但 VSR/TrueHDR 是通过 `SetStreamExtension` 显式请求的，属于"应用主动要求"，不在"automatically performing"范畴内**，因此 `AutoProcessingMode(FALSE)` 与 VSR 可以共存——mpv/VLC 就是活证据（先关自动处理，再开 VSR）。

不过这个细微差别值得在 3FP 里**实测确认**：Microsoft 文档对 `FALSE` 的描述是"the driver disables any extra video processing that it might be performing"（措辞很宽）。如果 3FP 实测发现 VSR 不生效，第一个要试的就是把这一行改成 `TRUE` 或延后调用顺序。（本报告标注为 **UNVERIFIED**：我无法在没有 NVIDIA RTX GPU + HDR 屏的机器上实测。）

---

## 3. 主流播放器怎么做的

### 3.1 mpv：`--vf=d3d11vpp:scaling-mode=nvidia`（参考实现，最清晰）

- **文件**：`video/filter/vf_d3d11vpp.c`（一个独立的用户滤镜）
- **默认状态**：`scaling_mode = SCALING_BASIC`（**默认关闭**，必须显式开启）
- **选项**：
  ```
  --vf-add=d3d11vpp:scaling-mode=nvidia     # 开 RTX VSR
  --vf-add=d3d11vpp:nvidia-true-hdr=yes     # 开 RTX Video HDR
  --vf-add=d3d11vpp:scale=2.0               # 放大倍数
  ```
- **实现特征**：
  - 用 `ID3D11VideoProcessorBlt` 做真正的缩放，输出到一张新纹理，再交给后续渲染链；
  - `enable_nvidia_rtx_extension()` 里 `{1, 2, 1}` 一次调用搞定；
  - TrueHDR 走 `supports_nvidia_true_hdr()` 探测 → `enable_nvidia_true_hdr()` 启用 → 强制 BT.2020/PQ + `X2BGR10`；
  - 还顺带支持 Intel VPE（`{edd1d4b9-8659-4cbc-a4d6-9831a2163ac3}`）和 MThreads。

**⚠️ 极其重要的一点：mpv 的主渲染路径（`vo=gpu` / `vo=gpu-next` / `hwdec=d3d11va`）本身并不使用 Video Processor。** `video/out/d3d11/hwdec_d3d11va.c` 全程用 `ra_d3d11_wrap_tex_video()` 把解码纹理包成着色器资源，用自己的着色器采样缩放。

**所以「mpv 不支持 RTX VSR」这个流传很广的说法，准确表述是：默认的 `vo=gpu`/`gpu-next` 路径不支持；只有显式挂上 `vf=d3d11vpp` 这个滤镜才支持。** 这正好是 3FP 当前处境的镜像——见 §4.2。

### 3.2 VLC：`--d3d11-upscale-mode=super`（做得最完善）

从我抓取的 `modules/video_output/win32/direct3d11.cpp` 逐字确认了用户可见选项：

```c
static const char *const ppsz_upscale_mode[] = {
    "linear", "point", "processor", "super" };
static const char *const ppsz_upscale_mode_text[] = {
    N_("Linear Sampler"), N_("Point Sampler"),
    N_("Video Processor"), N_("Super Resolution") };

add_string("d3d11-upscale-mode", "linear", UPSCALE_MODE_TEXT, UPSCALE_MODE_LONGTEXT)
```

**默认 `linear`（即默认关闭超分）。** 用户在"工具 → 偏好 → 视频"里可选「Super Resolution」。

- **文件**：`modules/video_output/win32/d3d11_scaler.cpp` / `.h`（专门为这件事新建的模块，2023 年，作者 Chilledheart / Steve Lhomme）
- **能力探测**（比 mpv 严谨，值得 3FP 照抄）：
  ```cpp
  // NVIDIA 530+ 驱动
  if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_NVIDIA &&
      (d3d_dev->WDDM.revision * 10000 + d3d_dev->WDDM.build) > 153000)
  {
      // 先用一个 1x1 级别的假 processor 探测扩展是否可用
      hr = d3dvidctx->VideoProcessorGetStreamExtension(
              processor.Get(), 0, &kNvidiaPPEInterfaceGUID,
              sizeof(available), &available);
      canProcess = available != 0;
  }
  ```
  注意 VLC 用 **`GetStreamExtension` 探测**（而不是盲调 `Set`），并对驱动版本做了门控。这是"能不能用"的可靠判定方式。
- **逐帧开关**（正确做法）：
  ```cpp
  // only use super resolution when source is smaller than display
  upscale = fmt->i_visible_width  < video_place->width
         || fmt->i_visible_height < video_place->height;
  stream_extension_info = { 1 /*version*/, 2 /*method*/, upscale ? 1u : 0u };
  ```
- **多厂商**：Intel VPE、MThreads（摩尔线程）MTVSR、AMD 走 AMF `AMFHQScaler`。三家都用同一套"私有扩展"模式。
- **日志**：`msg_Dbg(vd, "turning VSR %s", upscale ? "ON" : "OFF")` —— 便于用户确认状态。

### 3.3 浏览器（Chromium / Chrome / Edge）：`ui/gl/swap_chain_presenter.cc`

浏览器是**生产环境规模最大**的 RTX VSR 使用者。其实现有几个值得 3FP 借鉴的工程细节（源码逐字核实）：

```cpp
// 只在 NVIDIA 上启用，且扩展名区分 VSR 与 TrueHDR
constexpr GUID kNvidiaPPEInterfaceGUID = {
    0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};
constexpr UINT kStreamExtensionVersionV1 = 0x1;
constexpr UINT kStreamExtensionMethodSuperResolution = 0x2;
struct { UINT version; UINT method; UINT enable; }
    stream_extension_info = {kStreamExtensionVersionV1,
                             kStreamExtensionMethodSuperResolution,
                             enable ? 1u : 0u};
```

**三个关键工程细节（3FP 应当照抄）：**

1. **能力探测的正确姿势**——Chromium 明确警告 HRESULT 不可信：
   ```cpp
   HRESULT hr = video_context->VideoProcessorGetStreamExtension(
       video_processor, 0, &kNvidiaTrueHDRInterfaceGUID,
       sizeof(driver_supports_true_hdr), &driver_supports_true_hdr);
   // The runtime never fails the GetStreamExtension hr unless a bad memory size
   // is provided.
   return (driver_supports_true_hdr == 1);   // ← 必须检查返回的数值，不能只看 hr
   ```
   > **这修正了本报告早前的一个判断**：VLC 用 `available != 0` 判定是**对的**，而"只看 `FAILED(hr)`"是**错的**。`GetStreamExtension` 只要内存大小合法就返回成功，真正的支持信号在**输出参数**里。

2. **Blt 失败要有降级重试**——Chromium 在 `VideoProcessorBlt` 失败时，关掉 VSR 再重试一次：
   ```cpp
   if (FAILED(hr) && use_vp_super_resolution) {
     ToggleVpSuperResolution(gpu_vendor_id_, video_context.Get(),
                             video_processor.Get(), false);
     hr = video_context->VideoProcessorBlt(...);   // 重新 Blt
     if (SUCCEEDED(hr)) enable_vp_super_resolution_ = false;  // 永久禁用
   }
   ```
   因为**没有可靠的 VSR 探测手段**（见下），这是唯一稳健的可用性判定：试一次，失败就永久关掉。

3. **电池供电时禁用**：`bool use_vp_super_resolution = enable_vp_super_resolution_ && !is_on_battery_power_;`——VSR 耗电，笔记本上默认不用。

**⚠️ 一个容易踩的坑**：Chromium 里有两个 feature flag，`kDisableVPBLTUpscale`（默认关）和 `kLimitMFSwapChainSize`（**默认开**）。后者的作用注释写得很明确：

> *"Limit the video swap chain size that we request from Media Foundation, opting to do upscaling via DWM, rather than VPBLT."*

**也就是说：当缩放交给 DWM（而非 VP BLT）时，VSR 就不会触发。** 这与 §4.2 的"缩放必须发生在 Video Processor 内部"是同一件事的两面——如果 3FP 把自己的缩放交给 DXGI/DWM，VSR 同样不会生效。

### 3.4 一个贯穿所有实现的重要警告：「已请求」≠「已生效」

这是本次调研中最有实践价值的发现之一。**四个项目都明确承认自己无法确认 VSR 是否真的生效**：

| 项目 | 措辞 | 含义 |
|---|---|---|
| **MPCVR** | changelog 原文：*"The display of 'SuperResolution*' in the statistics only means that the request was sent successfully."* | 统计里显示 `SuperResolution*` **只代表请求发出去了** |
| **Kodi** | 日志 `"request enable successfully"`，UI 显示 `"Video Super Resolution: requested"` | 同样只承诺"已请求" |
| **mpv** | 手册明确说明是否真的生效取决于 GPU 驱动控制面板 | 播放器无法查询 |
| **Chromium** | 做 retry-and-latch-off 兜底 + 电池门控 | 用"试错"代替"查询" |

而且有**真实用户案例**证明这个警告不是多余的：MPC-HC issue **#3684** 标题即为 *"video says d3d11 super resolution is active but NVIDIA control panel says inactive"*（播放器说开了，NVIDIA 控制面板说没开）。MPC-BE issue **#678/#674** 也记录了"必须来回切换设置才激活"的问题。

**对 3FP 的直接含义**：不要做一个"VSR 已启用 ✓"的乐观 UI。要么如实显示"已请求"，要么用 §3.3 的 Blt 失败降级机制来判定。**绝不能把 `SetStreamExtension` 返回 S_OK 当作"VSR 正在工作"。**

### 3.5 FFmpeg 的机会（顺带发现，对 3FP 有参考价值）

`libavfilter/vf_scale_d3d11.c`（已逐字核实）是一个**完整、正确的 `ID3D11VideoProcessor` 缩放实现**：创建 enumerator + processor、`CreateVideoProcessorInputView`/`OutputView`、调用 `VideoProcessorBlt`。

但它**没有任何 `SetStreamExtension` / `SetOutputExtension` 调用**，选项只有 `width`/`height`/`format`。也就是说，**FFmpeg 的 D3D11 缩放滤镜目前完全无法触发 VSR**——它把该做的前置工作都做了，就差最后那一次扩展调用。

> **两点澄清**（都是常见的混淆）：
> - **`vf_d3d11vpp` 在 FFmpeg 中不存在**——它是 **mpv 的**滤镜名。在 FFmpeg 里搜这个名字只会 404。
> - FFmpeg 的 `scale_d3d11` 与 mpv 的 `vf_d3d11vpp` 功能重叠但**不是同一个东西**，前者缺扩展调用。

**这对 3FP 的启示**：3FP 的处境与 FFmpeg `scale_d3d11` 几乎一模一样——VP 通路齐全但缺扩展调用。这是"差最后一步"的典型形态，也再次印证 §4.2 的判断。

### 3.6 汇总对照表

| 播放器 | 渲染路径 | 用 Video Processor？ | RTX VSR | 用户开关 | 源码证据 |
|---|---|---|---|---|---|
| **mpv**（默认） | `vo=gpu/gpu-next` + `hwdec=d3d11va` | ❌ 纯着色器/libplacebo | ❌ **不支持** | — | `video/out/d3d11/hwdec_d3d11va.c` |
| **mpv**（挂滤镜） | `vf=d3d11vpp` | ✅ `VideoProcessorBlt` | ✅ 支持 | `scaling-mode=nvidia` | `video/filter/vf_d3d11vpp.c` |
| **VLC** | `direct3d11` vout + `d3d11_scaler` | ✅ | ✅ 支持（NVIDIA/Intel/MThreads） | `--d3d11-upscale-mode=super` | `d3d11_scaler.cpp` |
| **Chromium/Edge** | GPU 进程 DComp overlay | ✅ | ✅ **默认自动**（NVIDIA 无需 flag） | Intel 需 `IntelVpSuperResolution` | `ui/gl/swap_chain_presenter.cc` |
| **Kodi** | `RendererDXVA` → `DXVAHD` | ✅（HQ scaler 开启时不走） | ✅ 支持 | UI 显示 "requested" | `HwDecRender/DXVAHD.cpp` |
| **MPC-HC/BE/PotPlayer** | 经 MPC Video Renderer DLL | ✅ | ✅ 支持（需 MPCVR） | "Request Super Resolution" | MPCVR `Source/D3D11VP.cpp` |
| **FFmpeg `scale_d3d11`** | `vf_scale_d3d11.c` | ✅ **但无扩展调用** | ❌ **不能** | — | `libavfilter/vf_scale_d3d11.c` |
| **Plex/Jellyfin 桌面版** | libmpv → 着色器 | ❌ | ❌ 不能（除非注入 `vf`） | — | libmpv 架构 |
| **3FP**（现状） | 自研 D3D11 着色器 | ⚠️ 有代码但**从未调用** | ❌ 实际不支持 | 无 | `3FP/Render/VideoRenderer.cpp` |

**读表要点**：凡是"用 Video Processor 但没调扩展"的（FFmpeg、3FP），都拿不到 VSR；凡是"用自己的着色器缩放"的（mpv 默认、Plex），**结构上不可能**拿到 VSR。

#### Kodi 的门槛逻辑最值得借鉴

Kodi 的 `IsSuperResolutionSuitable()` 是目前见到最完善的判定（已逐字核实）：

```cpp
bool CProcessorHD::IsSuperResolutionSuitable(const VideoPicture& picture)
{
  // both Intel and NVIDIA support VSR in videos up to 1440p
  if (picture.iWidth > 2560)
    return false;                                    // ① 源分辨率上限（1440p）

  const UINT outputWidth = DX::Windowing()->GetBackBuffer().GetWidth();
  if (outputWidth <= picture.iWidth)
    return false;                                    // ② 必须真的在放大

  // At this time, Intel GPUs do not support VSR on HDR video
  if ((picture.color_primaries == AVCOL_PRI_BT2020 || ...) && VendorId == PCIV_Intel)
    return false;                                    // ③ Intel 不支持 HDR 源

  return true;
}
```

三点值得 3FP 采纳：
- **①源分辨率上限用 2560（1440p）而非 1920**——Kodi 注释明确写"both Intel and NVIDIA support VSR in videos up to 1440p"。这比流传的"≤1080p"更宽，也更可信（有实现依据）。**3FP 的门槛应采用这条，而非未证实的 1080p。**
- **②必须 `outputWidth > iWidth`**，与 VLC 的 `upscale` 判定一致——**这是唯一被所有实现一致确认的硬条件**。
- **③逐厂商区分 HDR 支持**——Intel 不支持 HDR 源，NVIDIA 支持。这正说明 VSR/TrueHDR 的厂商能力矩阵必须**运行时查询**，不能写死。

**⚠️ 另一个 Kodi 的坑**：Kodi 在 `RenderImpl()` 里用 `HasHQScaler() ? sourceRect : ...ApplyTransforms(...)` 分叉——**HQ 缩放器开启时就绕过了 VP 缩放，VSR 随之失效**。这又是"缩放必须在 Video Processor 内发生"的第三种表现形式。

---

## 4. 3FP 的现状与差距（逐条对照代码）

### 4.1 已有资产：VP 通路代码完整且质量不错

`3fp/kernel/FFF.Native/3FP/Render/VideoRenderer.cpp` 里已经实现了：

| 函数 | 行号 | 作用 |
|---|---|---|
| `EnsureVideoProcessor` | ~2805 | 创建 enumerator + processor，检查格式与色彩空间转换支持 |
| `EnsureVideoProcessorInputSurface` | ~2908 | 建源尺寸中转纹理 |
| `DrawWithVideoProcessor` | ~3184 | `CreateVideoProcessorInputView` + `SetStreamSourceRect/DestRect` + `VideoProcessorBlt` |
| `CanUseDirectVideoProcessor` | ~2792 | 可用性判定 |
| `ReleaseVideoProcessor` 等 | ~3174+ | 生命周期管理 |

`FFF3FPVideoScalingMode` 枚举也已定义（`Shader = 0` / `D3D11VideoProcessor = 1`），并通过 `FFF3FPSnapshot::videoScalingMode` 对外暴露诊断。

### 4.2 差距一（阻塞性）：VP 通路是死代码

对全仓库搜索 `DrawWithVideoProcessor` / `CanUseDirectVideoProcessor` / `RenderVideoProcessorInput` / `EnsureVideoProcessorInputSurface`，**只有定义处命中，没有任何调用点**：

```
3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp:2792  bool ...CanUseDirectVideoProcessor()   ← 定义
3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp:3184  ...DrawWithVideoProcessor(...)          ← 定义
（无调用点）
```

实际渲染路径是 `DrawCachedVideo` → `PrepareScaledVideo`（多趟着色器缩放链 `planeScaleChains_`）→ `ExecuteScalePass` → `DrawWithShader`，并在成功后写死：

```cpp
// VideoRenderer.cpp:4932
actualVideoScalingMode_.store(FFF3FPVideoScalingMode::Shader);
```

**含义：`videoScalingMode` 这个诊断字段永远只会返回 `Shader`。** 这与 §3.1 中 mpv 默认路径的情况完全同构：**3FP 目前 100% 走自研着色器缩放，因此哪怕装了 RTX 显卡、驱动开了 RTX 视频增强，也一定拿不到 VSR**——因为缩放根本没经过 Video Processor。

> 这也解释了为什么"打开 NVIDIA 控制面板的 RTX 视频增强"对 3FP 毫无效果。这不是 bug，是架构选择的必然后果。

### 4.3 差距二（阻塞性）：`CanUseDirectVideoProcessor` 的门槛过严

```cpp
// VideoRenderer.cpp:2792
bool PlayerVideoRenderer::CanUseDirectVideoProcessor() const noexcept {
    if (hdrProcessor_.RequiresMetadataAwareShader()) return false;
    if (!sourceExternal_ || sourceInputLayout_ != 2 || sourceTextures_[0] == nullptr ||
        sourceBitDepth_ > 10 || sourceInterlaced_ ||
        sourceChromaLocation_ != AVCHROMA_LOC_LEFT ||
        cachedVideoSettings_.transfer != 0 || actualMode_ != FFF3FPColorMode::MapToSdr ||
        swapOutputBits_ > 10)
        return false;
    ...
    return (inputDescription.BindFlags & D3D11_BIND_DECODER) != 0;
}
```

逐条评估：

| 条件 | 评估 |
|---|---|
| `!sourceExternal_` | 要求 GPU 硬解。**过于严格**——经核实，**软件解码后上传到 D3D11 纹理同样可以走 VSR**，硬解不是必要条件（详见 §6 约束表"DXVA 解码是否必需：❌ 否"）。但作为性能考虑保留硬解要求是合理的 |
| `sourceInputLayout_ != 2` | 限定 NV12/P010 双平面。⚠️ **比实际情况更严**。Chromium 的 VP 通路支持 `NV12`/`P010`/`YUY2`/`BGRA`/`RGB10A2`，NGX 包装层甚至用 `R8G8B8A8_UNORM` 作输入。**"仅 NV12/P010"不是普遍规则**，但对 NVIDIA VSR 而言保守限制在 NV12/P010 是安全的 |
| `sourceBitDepth_ > 10` | 保留。注意"VSR 不支持 10-bit"这一流传说法**未获证实**，证据反而相反（P010 的 SDR 内容可处理） |
| `sourceInterlaced_` | 合理（VSR 不支持交错） |
| `sourceChromaLocation_ != AVCHROMA_LOC_LEFT` | ⚠️ **过严**。很多源标 `UNSPECIFIED`，实际等价 LEFT；这一条会误杀大量正常片源 |
| `cachedVideoSettings_.transfer != 0` | 要求 SDR 传输函数。对 **VSR** 是多余的；对 **TrueHDR** 才是必要前提（且 TrueHDR **必须**只在 SDR 输入时启用——mpv `5abce15f1` 的教训） |
| `actualMode_ != MapToSdr` | 同上。**把"VSR 可用"和"TrueHDR 可用"两件事捆在一个门槛里了** |
| `swapOutputBits_ > 10` | ⚠️ **过严**。10-bit SDR 输出（`swapOutputBits_ == 10`）被允许，但 16-bit scRGB 被挡——而 TrueHDR 恰恰**要求** HDR 输出通路 |

**最关键的结构性问题：这个函数是"VSR 和 TrueHDR 共用一个开关"的设计，但两者的前提条件完全不同：**

- **VSR** 需要：`BIND_DECODER` 纹理 + NV12/P010 + 源分辨率 ≤ 1080p + 正在放大。**与 HDR/SDR 无关。**
- **TrueHDR** 需要：VSR 的前提**之外**，还要求 SDR 源 + 输出为 BT.2020+PQ（即 `swapOutputBits_ == 16` 的 scRGB HDR 通路）。

当前实现把 `actualMode_ != MapToSdr` 和 `swapOutputBits_ > 10` 也作为 VSR 的门槛，导致 **在 HDR 显示器上播放 SDR 片源时（真正最想要 VSR 的场景），只要走了 16-bit scRGB 通路就无法使用 VP**。这是需要拆分的。

### 4.4 差距三（缺失）：没有 NVIDIA 私有扩展调用

3FP 的 `EnsureVideoProcessor` 在配置完 processor 后就结束了，只设置了：
- `VideoProcessorSetStreamFrameFormat`
- `VideoProcessorSetStreamAutoProcessingMode(FALSE)`
- `VideoProcessorSetStreamColorSpace1` / `SetOutputColorSpace1`
- `VideoProcessorSetOutputBackgroundColor`

**没有 `VideoProcessorSetStreamExtension` / `GetStreamExtension` 的任何调用**，也就没有 NVIDIA 扩展 GUID。这正是 §2.2/§2.3 那段需要补上的代码。

### 4.5 差距四（架构冲突）：HDR 管线与 TrueHDR 的输出契约冲突

这是**最需要仔细设计**的一点，也是为什么不能无脑照抄 mpv。

3FP 的 HDR 策略（见 `docs/20`）：
- HDR 输出走 **16-bit scRGB**（`R16G16B16A16_FLOAT`，线性 Rec.709 primaries，`1.0 = 80 nits`）；
- SDR 输出走 8/10-bit，**由 DWM 独占负责 Windows HDR 的 SDR 白点调整**。

mpv 启用 TrueHDR 后强制输出 **BT.2020 + PQ + `R10G10B10A2_UNORM`（10-bit）**。

**两者是不同的输出契约**：3FP 是 scRGB 线性，mpv/TrueHDR 要 PQ 编码的 BT.2020。而且 3FP 已经有一套完整的 `HdrProcessor` 负责色调映射与 PQ/HLG 处理。

含义：
- TrueHDR 让**驱动**把 SDR 内容"生成"成 HDR，而 3FP 现有管线是**自己做**色彩管理。引入 TrueHDR 意味着在某一段把控制权交给驱动，之后 3FP 的 `HdrProcessor` 不能再对这个结果做一次 PQ 处理（会双重编码）。
- 因此 TrueHDR 不能简单"塞进"现有 VP 通路，而需要定义一条**独立分支**：`SDR 源 → VP(TrueHDR) → 已是 PQ/BT.2020 的 10-bit 纹理 → 直接上屏（跳过 HdrProcessor 的 transfer 处理）`。
- 这也与 3FP"真实 HDR 高亮"三挡模式（`MapToSdr` / `RawHdrAsSdr` / `MapToHdr`）的语义产生交互，需要明确 TrueHDR 在哪一挡下可用。

**建议：第一期只做 VSR，把 TrueHDR 单独立项**（理由见 §5）。

---

## 5. 需要做什么（分阶段建议）

### 阶段 0：先做可行性实测（不需要改代码）★ 最高优先

在做任何改动之前，先确认"值不值得做"。因为 §2.5 的 `AutoProcessingMode` 疑问和 §4.5 的契约冲突都无法靠读代码解决：

1. 用 **VLC**（`--d3d11-upscale-mode=super`）和 **mpv**（`--vf=d3d11vpp:scaling-mode=nvidia`）确认 VSR 确实生效；
2. 用 3FP 播放同一个 ≤1440p 片源并放大到全屏，用 `FFF3FP_GetSnapshot().videoScalingMode` 确认它恒为 `Shader`（预期如此）；
3. 记录 NVIDIA 控制面板 / NVIDIA App 中"RTX 视频增强"的开关状态对上述两者的影响。

#### ✅ 硬件前提已满足（2026-09-26 起，本机）

**本报告早前版本曾判断"无 RTX GPU + HDR 屏则无法验证，建议先不做"——该判断已失效。** 依项目自身实测记录（`3fp/docs/13` §1、`3fp/STATUS.md` §391）：

```
适配器[0] NVIDIA GeForce RTX 5080  VendorId=0x10DE
  输出[0] \\.\DISPLAY5  (Primary)
     BitsPerColor  = 10
     ColorSpace    = RGB_FULL_G2084_NONE_P2020 (PQ HDR10)   ← 已开启 HDR
     MaxLuminance  = 417.71 nits
```

**RTX 5080 + 10-bit PQ HDR 主输出**——这正好是 RTX VSR 与 RTX Video HDR 两项功能都需要的硬件条件。因此：

- **阶段 0 实测可以直接开工**，不存在硬件阻塞；
- **阶段 1 不再是"盲改"，可以实机闭环验证**（这是本次分析相比早前版本最重要的修正）；
- 唯一需要注意的是 `\\.\DISPLAY1`（RTX 4060 Laptop，`bits=8`/sRGB）仍在 DXGI 输出列表里，但 HDR 判据读 **Primary** 输出，不要被它误导。

> 与 `docs/17` §二 记录的"无 HDR 屏导致 HDR 项悬置"不同——那是 2026-09-16 的状态，**已被 09-26 的实机验证推翻**（`3fp/docs/04` 该条已标"⛔ 已过期"）。

### 阶段 1：让 VSR 真正跑起来（最小闭环）

1. **补私有扩展**：在 `EnsureVideoProcessor` 末尾（`VideoRenderer.cpp:2898` 之后）新增 NVIDIA PPE 扩展调用，结构与 mpv/VLC 一致：
   ```cpp
   // 建议：GUID 与结构体集中放在 VideoRenderer.h 或新的 NvidiaVideoExtensions.h
   constexpr GUID kNvidiaPpeInterfaceGuid{
       0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};
   ```
   **注意：Intel/MThreads/AMD 的 GUID 也建议一并定义**（VLC 的表格可直接照抄），即使第一期只启 NVIDIA，结构上留好位置——这三个 GUID 是公开在 VLC 源码里的。
2. **能力探测前置（注意 HRESULT 不可信）**：仿 VLC，用一个探针 processor 先 `GetStreamExtension` 判断可用性，**必须检查返回的 `UINT` 数值而非只看 HRESULT**——Chromium 源码明确警告 *"The runtime never fails the GetStreamExtension hr unless a bad memory size is provided"*。结果要缓存（3FP 已有 `videoProcessorConfigurationFailed_` 这类缓存位，扩展之）。adapter VendorId（NVIDIA = `0x10de`）可从 `DXGI_ADAPTER_DESC1` 读。
   > ⚠️ **注意：VSR 没有可靠的探测手段。** Chromium 和 mpv 都只能"直接调 `Set`，把 `FAILED(hr)` 当作唯一信号"。所以**必须**同时做第 8 步的降级重试，否则无法处理"探测报支持但实际不可用"的驱动。
3. **拆分门槛函数**：把 `CanUseDirectVideoProcessor()` 拆成两个语义清晰的判定：
   - `CanUseVideoProcessorForScaling()`：只关心 NV12/P010 + 非交错 + 位深 ≤10（**去掉 transfer/colorMode/swapOutputBits 三条**，并放宽 `AVCHROMA_LOC_LEFT` 以接受 `UNSPECIFIED`）；
   - `IsNvidiaSuperResolutionEligible()`：在其上叠加「NVIDIA 适配器（`0x10de`）+ 扩展可用 + **正在放大**（`destination.width > sourceWidth_`）+ **源分辨率落在官方区间 360p~1440p**」。**这是官方规格（§7.1），此前悬而未决，现已确定。**
     > 📌 注：官方区间是**按分辨率高度**表述的（360p/1440p），3FP 实现时应以**源高度**判定，并对非常规宽高比（如 2.35:1 宽银幕）确认口径。
4. **接上调用点**：在 `DrawCachedVideo`（`VideoRenderer.cpp:4914` 附近）的 `PrepareScaledVideo` **之前**加分支：满足条件时改走 VP 通路（`DrawWithVideoProcessor`），并把 `actualVideoScalingMode_` 写成 `D3D11VideoProcessor`（当前 `:4932` 是硬编码 `Shader`，需一并改成按实际路径写）。
5. **逐帧使能**：仿 VLC，仅在 `destination.width > sourceWidth_` 时把扩展 `enable` 置 1。3FP 有 `lastDestWidth_` 等诊断字段和 `CalculateVideoDestination`，判定放大很容易。
6. **放开 `AutoProcessingMode`**：若实测 VSR 不生效，把 `VideoRenderer.cpp:2891` 的 `FALSE` 改为 `TRUE` 再测（§2.5）。
7. **对外暴露（注意措辞诚实）**：`FFF3FPVideoScalingMode` 已有，建议再加一个"VSR 是否激活"的诊断位到 `FFF3FPSnapshot`，便于 UI 显示与自动化测试断言（注意 `size`/`version` ABI 约定，新字段追加在尾部）。仿 VLC 加一条 `turning VSR ON/OFF` 级别的日志。
   > ⚠️ **UI 不要写"VSR 已启用 ✓"。** 业界共识是「已请求 ≠ 已生效」（详见 §3.4），MPC-HC #3684 就是"播放器说开了、控制面板说没开"的实例。应显示"已请求"或"VP 路径中"，把判定交给第 8 步的降级机制。
8. **Blt 失败降级（必做，不可省）**：仿 Chromium——`VideoProcessorBlt` 失败且当时开着 VSR 时，**关掉扩展再重试一次**；若重试成功则**永久禁用** `enable_vp_super_resolution_`。这是唯一稳健的可用性判定，因为 VSR 无法可靠探测。
9. **可考虑：电池供电时禁用**（Chromium 的做法）——VSR 显著耗电，笔记本上可能不适合默认开启。3FP 若面向笔记本用户，建议纳入。

### 阶段 2：TrueHDR（建议单独立项）

必须解决 §4.5 的输出契约冲突后才有意义：
- 定义独立分支，让 TrueHDR 输出直接对接交换链，**跳过 `HdrProcessor` 的 transfer 处理**；
- 明确与三挡 HDR 模式的关系（TrueHDR 只对 SDR 源有意义，即 `MapToSdr` / 需要生成 HDR 的场景）；
- 输出格式需从 scRGB 16F 切到 `R10G10B10A2_UNORM` + BT.2020/PQ，这**与 `docs/20` 已确认的"SDR 输出不用 16F"结论不冲突但需要新的判定路径**。

### 阶段 3：工程化

- **VSR 与 3FCompare 的对比场景是冲突的**：3FP 的核心用途之一是"帧级精确对比/差异比较"（`docs/25` 多路无缝放大与比例式差异对比）。**AI 超分会改变像素内容**，使像素级对比失去可复现性——这与项目里已有的"保持可复现的渲染契约"注释（`VideoRenderer.cpp:2888-2890`）直接矛盾。

  ⇒ **强烈建议：VSR 默认关闭，且在与 3FCompare 联动的对比/剪辑模式下强制禁用。** 这既是正确性要求，也正好呼应了现有代码里那条注释的初衷。

- 参考 mpv/VLC 的做法，把开关做成**用户显式选项**（默认关），而不是自动启用。

---

## 6. 输入/输出约束核实表

网上关于 RTX VSR 的约束说法很多，逐条核实结果如下（**多项与流传说法不符**）：

| 约束 | 结论 | 依据 |
|---|---|---|
| **✅ 源分辨率 360p ~ 1440p** | ✅ **官方明确（此前悬置，现已确认）** | FAQ：*"RTX Video Super Resolution supports video input resolutions **from 360p to 1440p**"* —— 同时推翻"≤1080p"与"无上限"两种流传说法 |
| **✅ 输出支持到 5K** | ✅ **官方明确** | FAQ：*"RTX Video will support most displays **including 5K displays**"*（比 NGX 包装层的 4K 更宽） |
| **✅ 必须真的在放大** | ✅ **官方明确（唯一硬条件）** | FAQ：*"will **only be enabled if the video requires upscaling**"*；*"does not enhance downscaling"* |
| **✅ 窗口即可，无需全屏** | ✅ 官方明确 | FAQ：*"As long as video is played at its native resolution or higher, RTX Video will enhance video **in a window**"* |
| **✅ RTX 20 系支持（VSR 1.5）** | ✅ **已确认**（此前 UNVERIFIED） | FAQ：*"Support for **Turing based RTX GPUs** (Professional and **RTX 20-Series** GPUs)"*（2023-10 更新） |
| **✅ RTX 50 系（本机 5080）** | ✅ 官方口径覆盖 | FAQ：*"RTX Video supports **all RTX GPUs** on consumer notebook and desktops"* |
| **✅ 操作系统 Win10/11 64-bit** | ✅ 官方明确 | FAQ：*"supported on **64-bit versions of Windows 10 and 11**"* |
| **TrueHDR 需要 HDR10 显示器** | ✅ 已核实 | *"requires an RTX GPU connected to an HDR10-compatible monitor or TV"* + Windows HDR 须开启 |
| **TrueHDR 仅限 SDR 输入** | ✅ 已核实 | mpv `5abce15f1`；Chromium 亦仅对 SDR 启用；FAQ 亦列为"HDR content 不支持" |
| **DXVA 硬解是必需的？** | ❌ **否** | 软件解码后上传到 D3D11 纹理同样可行。**这修正了 §4.3 里对 `!sourceExternal_` 门槛的评价**——该门槛比实际需求更严 |
| **源必须 ≤1920×1080？** | ❌ **已被官方推翻** | 官方规格是 **360p~1440p**（见首行）。早前按社区共识列此条，现予更正 |
| **最小源尺寸 160×90？** | ⚠️ **属于另一个 SDK** | 这是 **Maxine VFX** SuperRes 的约束，**不是** RTX Video 的。官方 RTX Video 下限是 **360p** |
| **仅支持 NV12/P010？** | ⚠️ **未证实，且实际相反** | Chromium VP 通路用 `NV12`/`P010`/`YUY2`/`BGRA`/`RGB10A2`；NGX 包装层用 `R8G8B8A8_UNORM` 输入 |
| **VSR 不支持 10-bit？** | ❌ **已被官方推翻** | FAQ：*"VSR now also **upscales HDR video**"*（2025-01 更新）；且 P010 SDR 内容可处理 |
| **编解码器白名单？** | ⚠️ 官方未给白名单 | FAQ 只列"不支持的**内容**"：*"some **DRM protected content** and **YouTube shorts**"* |
| **MPO 会被禁用** | ✅ 官方明确（3FP 需注意） | FAQ：*"**Multiplane Overlay (MPO) is automatically disabled** when RTX Video Super Resolution or HDR are enabled"* |
| **与 NIS/DSR/DLDSR 冲突** | ✅ 官方明确 | FAQ：*"will not be active when a game is using NVIDIA Image Scaling (NIS), DSR or DLDSR"* |

> **说明**：本表已由官方 FAQ 全面校准（详见 §7.1）。仍有少数"未证实"项（如 NV12/P010 是否为硬约束），但**不影响 3FP 的改造方案**。

---

## 7. 官方一手来源已核实的事实

### 7.1 ⭐ 官方 RTX Video FAQ（已通过代理 + Wayback 取得全文）

这是**唯一权威的官方规格来源**，此前因 Oracle 后端故障无法直接访问。本次经**本机代理 `127.0.0.1:10808`** 打通 Wayback Machine，取到 **2025-01-30 更新版**全文（快照时间戳 `20250609234633`，HTTP 200）。全文已存档至 `3fp/docs/_evidence-RTX-Video-FAQ-official.txt`。

#### ★ 输入分辨率上限（此前悬而未决的核心问题）

> **"RTX Video Super Resolution supports video input resolutions from 360p to 1440p."**

**官方明确给出：360p ~ 1440p。** 这**同时推翻**了流传的两种说法：
- ❌ "源必须 ≤1080p" —— **错**，上限是 **1440p**
- ❌ "无上限" —— **也错**，官方有明确上下限

⇒ **Kodi 的 `iWidth > 2560` 判定（1440p）恰好与官方规格一致**，本报告 §3.6 建议采用 2560 是**正确的**，现已有官方依据。
⇒ **下限 360p** 是本报告此前完全未掌握的信息，**3FP 的门槛必须加上这一条**。

#### ★ 输出分辨率

> **"RTX Video will support most displays including 5K displays."**

**不止 4K——官方称支持到 5K。** 本报告此前写"输出上限 3840×2160"（源自 NGX 包装层的 `kMaxOutW/H`），**官方口径更宽**，特此更正。

#### ★ GPU 支持矩阵（此前标注 UNVERIFIED，现已确认）

| 问题 | 官方原文 |
|---|---|
| 总体 | *"RTX Video supports **all RTX GPUs** on consumer notebook and desktops."* |
| 首发 | *"RTX Video Super resolution **originally launched on** NVIDIA RTX **Ampere and Ada** generation GPUs."* |
| 1.5 更新（2023-10）新增 | *"Support for **Turing based RTX GPUs** (Professional and **RTX 20-Series** GPUs)"* |
| 专业卡 | *"supported on NVIDIA RTX Ampere and Ada generation desktop and professional laptop GPUs **from the 1000 class and up**"* |

⇒ **RTX 20 系（Turing）自 2023-10 的 VSR 1.5 起支持** —— 此前我把这条列为 UNVERIFIED，现确认。**RTX 50 系（本机 5080）** 属"all RTX GPUs"，官方口径覆盖。

#### ★ 其他关键官方事实

| 项 | 官方原文 |
|---|---|
| **唯一硬性启用条件** | *"RTX Video Super Resolution will **only be enabled if the video requires upscaling**."* |
| 窗口即可，无需全屏 | *"**No.** As long as video is played at its native resolution or higher, RTX Video will enhance video **in a window**."* |
| HDR 源 | *"RTX Video **HDR** … Not supported: … and **HDR content**."* |
| VSR 现也支持 HDR 源 | *"VSR now also **upscales HDR video**"*（2025-01 更新） |
| 操作系统 | *"64-bit versions of **Windows 10 and 11**"* |
| 官方点名的播放器 | *"video played on **VLC** as well as latest Chrome, Edge, and Firefox browsers"* |
| 多视频 | *"RTX Video will enhance the video **most recently put in focus**."* |
| 暂停时 | *"still active but with no new frames to process, RTX Video will **not use GPU resources**."* |
| 与 NIS/DSR/DLDSR 冲突 | *"will **not be active** when a game is using NVIDIA Image Scaling (NIS), Dynamic Super Resolution (DSR) or DLDSR."* |
| **MPO 被禁用** | *"**Multiplane Overlay (MPO) is automatically disabled** when RTX Video Super Resolution or HDR are enabled."* |
| 不支持的源 | *"some **DRM protected content** and **YouTube shorts**"* |

#### ★ 官方点名支持的播放器 / 应用（独立印证本报告的源码结论）

> *"RTX Video is available in the NVIDIA Control Panel and NVIDIA App. If turned on, the AI effects will be applied to video played on **VLC** as well as latest Chrome, Edge, and Firefox browsers."*
> *"**How do I get VLC with RTX Video Super Resolution Support** — A: This version is available on the **VLC download page**"*

⇒ **NVIDIA 官方明确点名 VLC**，并提供"带 RTX 超分的 VLC 版本"下载入口。这与本报告 §3.2 的**源码级发现完全吻合**（VLC `d3d11_scaler.cpp` 的 `super_res` 分支 / `--d3d11-upscale-mode=super`）——**两条独立证据链互证**。

⇒ 值得注意的是：官方**只点名了浏览器与 VLC**，未提 mpv / MPC / Kodi。这与 §3.1 的结论一致——**mpv 默认路径不支持，需显式挂 `vf=d3d11vpp`**。

⇒ 同时官方 FAQ 提及 *"NVIDIA has also released RTX Video as an SDK … Some example applications that have integrated RTX Video are **DaVinci Resolve** and **Wondershare Filmora**"* —— 即**通路 B（SDK）主要面向集成式应用**，播放器场景用通路 A 即可。

#### ★ 官方状态指示器 —— 直接印证 §3.4 的判断

> *"with the January 2024 update, a **status indicator** is now visible in the NVIDIA Control Panel to give users an indicator if Super Resolution or HDR are active."*
> *"we've added optional **on-screen indicators** to show when Video Super Resolution and Video HDR are active."*

⇒ **官方自己做了"是否真的在生效"的指示器**，这正是 §3.4「已请求 ≠ 已生效」的最有力佐证——**连 NVIDIA 都认为需要专门告诉用户"现在真的生效了"**。3FP 若做此功能，**应复用该指示器的语义，而不是自己猜**。

#### ★ NVIDIA App 路径（修正我此前"未找到依据"的结论）

> *"In our August 2024 NVIDIA app beta update, the app's **System tab** now includes a new **Video** section that adds RTX Video Enhancements… On the **System > Video** tab in the NVIDIA app…"*

⇒ **NVIDIA App 的路径是 `System > Video`，官方确认存在。** 我此前说"未找到官方依据"，现予更正。
但**"per-app override（按应用单独覆盖）"仍未获证实**——官方描述的粒度是**全局开关 + 滑杆**，未见按应用分别配置。

### 7.2 官方 RTX VSR / HDR 发布博客

**RTX VSR**（2023-02-28，*"Pixel Perfect…"*）：

| 事实 | 原文 |
|---|---|
| 首发 GPU | *"GeForce RTX **40 and 30 Series** GPU users can tap AI to upscale lower-resolution content up to 4K"* |
| 画质档位 | *"select a **quality from one to four**"* |
| 开启路径 | *"**NVIDIA Control Panel** → 'Adjust video image settings' → **super resolution** box under 'RTX video enhancement'"* |
| 应用 | *"nearly all content streamed in Google Chrome and Microsoft Edge"*；Chrome ≥110.0.5481.105、Edge ≥110.0.1587.56 |
| 算法 | *"performs **upscaling and compression artifact reduction in a single pass**"* |

**RTX Video HDR**（2024-01-24）：

| 事实 | 原文 |
|---|---|
| 显示器要求 | *"requires an **RTX GPU connected to an HDR10-compatible monitor or TV**"* |
| 仅 SDR 源 | *"transform **standard dynamic range video** … into … (HDR)"* |
| Windows 前提 | *"Ensure **Windows HDR** features are enabled … **System > Display > HDR**"* |
| 开启路径 | *"**Adjust video image settings > RTX Video Enhancement** — then enable **HDR**"* |
| 可与 VSR 并用 | *"RTX Video HDR and RTX Video Super Resolution **can be used together**"* |

### 7.3 ⚠️ 一处流传说法的更正

**「90% 内容 ≤1080p」不是技术上限。** FAQ 原文同样把它作为**流量统计**表述：

> *"Over 90% of all internet video is 1080p or less."*

它与技术规格 **"input resolutions from 360p to 1440p"** 是两回事。本报告早前版本曾把两者混淆，**特此更正**。

### 7.4 抓取过程与故障定性

| 来源 | 结果 | 诊断 |
|---|---|---|
| 官方 FAQ 直连（`web_fetch`） | HTTP 403 | **Oracle 后端故障**（每次请求事件号不同） |
| 官方 FAQ **经代理 `curl`** | **HTTP 403，同样失败** | ✅ **证明代理修不了它**——故障在 Oracle 侧，与网络路径无关 |
| 官方 FAQ **经代理 + Wayback** | ✅ **HTTP 200，全文取得** | 快照 `20250609234633`；**这是唯一成功路径** |
| `developer.nvidia.com/rtx-video-sdk` | 302 → AI-for-Media | **页面已下线**；5 个变体全 404 |
| `docs.nvidia.com/video-technologies/` | 200 但**无 RTX Video** | 该文档树只有 Optical Flow / Video Codec SDK / PyNvVideoCodec |

> **重要澄清**：FAQ 里确实提到 *"NVIDIA has also released RTX Video as an SDK to allow developers to integrate it into their apps"*，并点名 **DaVinci Resolve / Wondershare Filmora**。
> 即**官方确认存在 RTX Video SDK**，但**其文档站与下载页目前已下线/不可达**。这印证了 §1 通路 B 的存在，也说明它主要面向**集成式应用**而非播放器。**通路 A 仍是播放器的最优选择。**

---

## 8. 结论速览

| 问题 | 回答 |
|---|---|
| RTX VSR/HDR 在播放器上怎么实现？ | 两条路：①驱动级 `ID3D11VideoProcessor` 私有扩展（Chromium/mpv/VLC 走这条）；②NGX 的 RTX Video SDK。**3FP 应选①** |
| VSR 的确切调用？ | `VideoProcessorSetStreamExtension(proc, 0, {d43ce1b3-1f4b-48ac-baee-c3c25375e6f7}, {1,2,enable})` |
| TrueHDR 的确切调用？ | 先 `GetStreamExtension({fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3})` 探测**并检查返回数值**（HRESULT 不可信），再 `Set(...,{4,3,1})`；**并要求输出 BT.2020+PQ + 10-bit 表面** |
| 为什么"开了没效果"？ | ①缩放在 Video Processor 之外发生（**最常见**）；②TrueHDR 只调扩展未重标 HDR10（mpv 0.41.0 的实际 bug）；③源不在放大状态 |
| Chromium 支持吗？ | 支持，且是生产环境验证过的最佳工程参考（含 Blt 失败降级、电池禁用、能力探测警告） |
| mpv 支持吗？ | 默认 `vo=gpu` **不支持**；只有 `--vf=d3d11vpp:scaling-mode=nvidia` 才支持 |
| VLC 支持吗？ | 支持，`--d3d11-upscale-mode=super`（默认 `linear` 即关） |
| 3FP 现状？ | **VP 通路代码已写好但是死代码，从未被调用**；实际 100% 走着色器缩放 ⇒ 必然拿不到 VSR |
| 3FP 主要障碍？ | ①VP 通路未被接入；②`CanUseDirectVideoProcessor` 把 VSR/TrueHDR 门槛混在一起且过严；③缺 NVIDIA 扩展调用；④TrueHDR 的输出契约与现有 scRGB 管线冲突 |
| 最小可行改动？ | 补扩展调用 + 拆门槛 + 在 `DrawCachedVideo` 接分支 + 逐帧使能 + Blt 失败降级，**约 100~250 行**，属阶段 1 |
| **源分辨率门槛？** | ✅ **官方确定：360p ~ 1440p**（既非流传的 ≤1080p，也非无上限） |
| **输出分辨率上限？** | ✅ 官方：**支持到 5K 显示器**（比 NGX 包装层的 4K 更宽） |
| **GPU 支持？** | ✅ 官方：**所有 RTX GPU**；VSR 首发 Ampere/Ada，**RTX 20 系（Turing）自 VSR 1.5 起支持** |
| 硬件前提？ | ✅ **已满足**——本机 **RTX 5080**（`VendorId=0x10DE`）+ **10-bit PQ HDR** 主输出（`\\.\DISPLAY5`）⇒ 可实机验证，**无硬件阻塞** |
| 官方有状态指示器吗？ | ✅ **有**（2024-01 起）——官方自己就提供"是否真的在生效"的指示，**印证"已请求≠已生效"**，3FP 应复用其语义 |
| 最大风险？ | ①VSR 破坏像素级可复现性，与 3FCompare 对比用途冲突，**须默认关闭并强制互斥**；②TrueHDR 只调扩展不重标 HDR10 会静默失效（mpv 0.41.0 实例）；③**启用后 MPO 会被系统禁用**（可能影响 3FP 的多窗口/overlay 表现） |

---

## 附：本报告的证据来源

**一手源码（均已逐字抓取核对；五份实现交叉验证同一组 GUID）**
- **Chromium** `ui/gl/swap_chain_presenter.cc` — `ToggleNvidiaVpSuperResolution()` / `ToggleNvidiaVpTrueHDR()` / `NvidiaDriverSupportsTrueHDR()`；含能力探测警告（HRESULT 不可信）、Blt 失败降级重试、电池禁用、`kDisableVPBLTUpscale` / `kLimitMFSwapChainSize`
- **Kodi** `xbmc/cores/VideoPlayer/VideoRenderers/HwDecRender/DXVAHD.cpp` — `EnableNvidiaRTXVideoSuperResolution()` / `IsSuperResolutionSuitable()`；源码注释显式引用 Chromium
- **FFmpeg** `libavfilter/vf_scale_d3d11.c` — 完整 VP 缩放但**无扩展调用**（反证 VSR 需要显式 opt-in）
- mpv `video/filter/vf_d3d11vpp.c` — VSR/TrueHDR/Intel VPE 完整实现、选项定义、`AutoProcessingMode(FALSE)` 注释
- mpv `video/out/d3d11/hwdec_d3d11va.c` — 证明主渲染路径用着色器采样而非 Video Processor
- mpv 提交（SHA 已通过 GitHub API 核实）：`6ca3752f7`（新增 TrueHDR）、`f7ad4a5f8`（修正输出格式）、`5abce15f1`（限 SDR 输入）
- VLC `modules/video_output/win32/d3d11_scaler.cpp` — NVIDIA/Intel/MThreads 扩展、能力探测、逐帧开关
- VLC `modules/video_output/win32/d3d11_scaler.h` — `D3D11_UpscalerCreate(..., bool super_res, ...)`
- VLC `modules/video_output/win32/direct3d11.cpp` — `d3d11-upscale-mode` 选项（`linear`/`point`/`processor`/`super`）

**NVIDIA 官方（已成功抓取）**
- ⭐ **官方 RTX Video FAQ**（`custhelp.com/a_id/5448`）— 经**本机代理 + Wayback** 取得 2025-01-30 更新版全文（快照 `20250609234633`）。**输入 360p~1440p、输出支持 5K、GPU 矩阵、状态指示器等核心规格均出自此页。** 存档：`3fp/docs/_evidence-RTX-Video-FAQ-official.txt`
- [RTX Video Super Resolution 发布博客](https://blogs.nvidia.com/blog/rtx-video-super-resolution/)（2023-02-28）— RTX 40/30 系首发、画质 1–4、控制面板路径、Chrome/Edge 版本要求
- [RTX Video HDR 发布博客](https://blogs.nvidia.com/blog/rtx-video-hdr-remix-studio-driver/)（2024-01-24）— HDR10 显示器要求、SDR 源、Windows HDR 前提、与 VSR 并用
- `docs.nvidia.com/video-technologies/`（索引）— **反证**：官方技术文档树**不含** RTX Video

**NVIDIA 官方（不可达，原因已定性）**
- 官方 FAQ **直连** 与 **经代理直连** 均 HTTP 403 —— **Oracle 后端故障**（每次请求事件号不同），**代理也修不了**
- `developer.nvidia.com/rtx-video-sdk` — 已下线，302 到 AI-for-Media；5 个变体全 404。但 FAQ 正文**确认该 SDK 存在**（点名 DaVinci Resolve / Wondershare Filmora）

**Microsoft 文档**
- [`VideoProcessorSetStreamAutoProcessingMode`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamautoprocessingmode) — `Enable=FALSE` 语义："the driver disables any extra video processing that it might be performing"
- [`ID3D11VideoContext`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nn-d3d11-id3d11videocontext) — `SetStreamExtension` / `GetStreamExtension` / `Blt`

**参考文档（由协作 agent 产出，本报告已抽验其中的关键源码并确认）**
- `RTX_VSR_HDR_API_Report.md`（本仓库根目录）— NGX（通路 B）接口签名、约束核实表、UNVERIFIED 清单
- `RTX-VSR-HDR-video-players-report.md`（本仓库根目录）— 逐播放器调研、MPCVR/Kodi 细节、失败 URL 表

**本项目源码（对象 = 3FP 播放器，非 3FC）**
- `3fp/kernel/FFF.Native/3FP/Render/VideoRenderer.cpp`（VP 通路定义 `:2792`/`:2805`/`:2908`/`:3184`；渲染路径 `:4914`/`:4932`；getter `:5533`）
- `3fp/kernel/FFF.Native/3FP/Render/VideoRenderer.h`（`:277-299`、`:377-380`、`:446-454`、`:463`）
- `3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h`（`FFF3FPVideoScalingMode` `:20`、`videoScalingMode` `:189`）
- `3fp/kernel/FFF.Native/3FP/Api/PlayerApi.cpp`（`PlayerApiVersion = 16`）
- **硬件前提依据**：`3fp/docs/13-HDR与scRGB完整验证（2026-09-26）.zh.md` §1、`3fp/docs/04-实机验证报告（2026-09-24）.zh.md`、`3fp/STATUS.md`
- `docs/20-SDR输出位深策略与内核实现对比.zh.md` — scRGB 16F 契约与"DWM 独占 SDR 白点"设计
- `docs/25-多路无缝放大与比例式差异对比-可行性分析.zh.md` — 3FP 的像素级对比用途

> ⚠️ **作用域说明**：本报告针对 **3FP（FFF.Player 播放器内核）**，即 `3fp/kernel/FFF.Native/3FP/`。
> 3FP 的改动落在 `3fp/` 独立工作区，**不得直接改 `third_party/fff_project/`**（3FC 构建基线）——
> 需同步时走 `third_party/fff_project/PATCHES.md` 的重移植流程（见 `3fp/README.md` 铁律 1）。
> 上游方向：`Lake1059/FFF_Project`，经 fork `luoye-cpu/FFF_Project` 提 PR。

**明确未能验证（UNVERIFIED）——已大幅收窄**
- **NVIDIA App 是否有 RTX Video 的「per-app override」**——FAQ 确认 NVIDIA App 有 `System > Video` 页与滑杆，但**粒度是全局**，未见按应用分别配置。**不要断言其存在。**
- **NV12/P010 是否为硬性格式约束**——官方 FAQ 未提格式，只说"不支持的**内容**"（DRM / YouTube shorts）。Chromium 实测支持多格式，故倾向"非硬约束"，但**无官方明文**。
- **NGX（通路 B）的 API 细节**——SDK 下载页已下线，仅有第三方项目旁证。因通路 A 已足够，未影响结论。
- **VLC / Kodi 的 RTX Video HDR 支持**——未在其源码中找到 TrueHDR GUID，疑似缺失但未定论。
- `AutoProcessingMode(FALSE)` 是否影响 VSR 生效（§2.5）——**需实机验证**，本机已具备条件。

**本次分析的执行环境（供复现）**
- 用户启用 `danger-full-access` 后 `pwsh` **恢复可用**（此前 `0xC0000142`），本机代理 `127.0.0.1:10808`（v2rayN/xray，`ProxyEnable=1`）**可正常使用**。
- **关键方法**：官方 FAQ 在 Oracle 侧故障、直连与**经代理直连**均 403，最终经 **代理 + Wayback Machine** 取得全文。
  ```powershell
  # 实测有效的完整链路
  curl.exe -sSL -x http://127.0.0.1:10808 `
    "https://web.archive.org/web/20250609234633id_/https://nvidia.custhelp.com/app/answers/detail/a_id/5448/"
  ```
- `web_search` 工具**仍然失效**（返回 tool_result 错误），全程改用 `web_fetch` + `curl` 直取。
- ⚠️ 注意：`web_fetch` 为**服务端**抓取，**拒绝非公网 IP**，无法使用本机代理（`Error: URL hostname "127.0.0.1" resolves to a non-public IP address`）。**需要走代理时必须用 `curl.exe`（shell），不能用 `web_fetch`。**
