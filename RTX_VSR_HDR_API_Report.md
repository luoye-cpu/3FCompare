# RTX Video Super Resolution & RTX Video HDR — Developer API Surface on Windows

**Technical research report**
Compiled from primary sources: NVIDIA, Microsoft, Chromium, mpv source, GitHub API.
`web_search` was confirmed non-functional during this research (error: *"DeepSeek returned no web_search_tool_result blocks"*). All findings below come from `web_fetch` against directly-addressed URLs, listed in **Sources**.

---

## 0. Executive summary — the central correction

The framing in the research brief assumed RTX VSR is exposed as **one** thing, probably an `ID3D11VideoProcessor`-adjacent extension. The evidence shows there are **three distinct, separately-designed interfaces**, and conflating them is the main risk in this report:

| # | Interface | Shape | Who uses it |
|---|---|---|---|
| **A** | **DXVA/D3D11 Video Processor *private driver extension*** | `ID3D11VideoContext::VideoProcessorSetStreamExtension` + vendor GUID | Browsers (Chrome/Edge), mpv, and **any** app that already does `VideoProcessorBlt` scaling. This is the "free / zero-code" path. |
| **B** | **NVIDIA RTX Video SDK** (aka "RTX Video SDK 1.1") | **NGX** feature API: `NVSDK_NGX_D3D11_*`, `nvsdk_ngx_defs_vsr.h` / `_truehdr.h`, driver DLLs `nvngx_vsr.dll`, `nvngx_truehdr.dll` | Developers who want an **explicit, controllable** VSR/TrueHDR pass on their own `ID3D11Texture2D`s. **Not** a video-processor, **not** DXVA. |
| **C** | **NVIDIA Video Effects SDK (Maxine VFX)** — `SuperRes` / `Upscale` / `ArtifactReduction` | Separate `nvvfx` filter API | Different product. Often mislabelled "RTX VSR" in blog posts. **Do not conflate.** |

> **Bottom line:** there is an official **RTX Video SDK**, and it is **NGX-based**, giving you an **input/output `ID3D11Resource` pair** — *not* an `ID3D11VideoProcessor`. Separately, VSR/HDR *are* also reachable "for free" through the DXVA video processor via **vendor-private stream extensions** (`VideoProcessorSetStreamExtension`) — this is what Chrome and mpv actually do.

---

## 1. Public API surface for RTX VSR

### 1.1 There is an official "RTX Video SDK" — and it is NGX-based

**VERIFIED — high confidence.** A working open-source project that links against it describes it precisely:

> "Offline NVIDIA RTX Video Super Resolution for existing video files. Decode with FFmpeg, run VSR through the **RTX Video SDK 1.1 (`nvngx_vsr.dll`)**"
> — `github.com/DaSilva-Adriano/rtx-vsr-lab` README

> "Unpack **RTX Video SDK 1.1** so `include\nvsdk_ngx.h` exists. Default path: `C:\VSR\RTX_Video_SDK_v1.1.0`."
> — same README (build instructions)

Note the header name: **`nvsdk_ngx.h`** — this is NVIDIA **NGX**, the same framework DLSS uses. The SDK version string observed is **1.1.0**.

### 1.2 Verbatim headers included (from a compiled, working wrapper)

From `native/vsr_ngx.cpp` (fetched verbatim):

```c
#include <d3d11_4.h>
#include <dxgi.h>

#include <nvsdk_ngx_defs.h>
#include <nvsdk_ngx_defs_truehdr.h>
#include <nvsdk_ngx_defs_vsr.h>
#include <nvsdk_ngx_helpers_truehdr.h>
#include <nvsdk_ngx_helpers_vsr.h>
```

Linked library (verbatim `#pragma comment`):

```c
#pragma comment(lib, "nvsdk_ngx_s.lib")
```

The presence of **separate `_vsr` and `_truehdr` def/helper headers** is direct evidence that **VSR and TrueHDR are two separate NGX features**, not one.

### 1.3 The two feature DLLs (driver-provided)

```c
auto v = join_path(d, L"nvngx_vsr.dll");
auto t = join_path(d, L"nvngx_truehdr.dll");
```

- **`nvngx_vsr.dll`** → RTX Video Super Resolution
- **`nvngx_truehdr.dll`** → RTX Video HDR (TrueHDR)

These are located via an NGX search-path list, not by hard-coding:

```c
NVSDK_NGX_FeatureCommonInfo featureInfo{};
featureInfo.PathListInfo.Path = pathPtrs.data();
featureInfo.PathListInfo.Length = static_cast<unsigned>(pathPtrs.size());
```

Search dirs observed: the exe/module dir, `C:\VSR`, and the SDK's `bin\Windows\x64\rel` / `dev` folders.

### 1.4 Verbatim API surface — initialisation, capability query, create, evaluate

**Init / capability / scratch:**

```c
NVSDK_NGX_Result ngx = NVSDK_NGX_D3D11_Init(kAppId, appData.c_str(), device, &featureInfo);
ngx = NVSDK_NGX_D3D11_GetCapabilityParameters(&params);
ngx = NVSDK_NGX_D3D11_GetScratchBufferSize(NVSDK_NGX_Feature_VSR, params, &scratch);
ngx = NVSDK_NGX_D3D11_GetScratchBufferSize(NVSDK_NGX_Feature_TrueHDR, params, &scratch);
```

**Create / release handles:**

```c
NVSDK_NGX_Feature_Create_Params create{};
ngx = NGX_D3D11_CREATE_VSR_EXT(ctx, &vsr, params, &create);

NVSDK_NGX_Feature_Create_Params thcreate{};
ngx = NGX_D3D11_CREATE_TRUEHDR_EXT(ctx, &thdr, params, &thcreate);

NVSDK_NGX_D3D11_ReleaseFeature(vsr);
NVSDK_NGX_D3D11_ReleaseFeature(thdr);
NVSDK_NGX_D3D11_DestroyParameters(params);
NVSDK_NGX_D3D11_Shutdown1(device);
```

**VSR evaluate — verbatim, including the parameter struct and every field:**

```c
NVSDK_NGX_D3D11_VSR_Eval_Params p{};
p.pInput  = inRes;      // ID3D11Resource*
p.pOutput = outRes;     // ID3D11Resource*
p.InputSubrectBase.X = 0;
p.InputSubrectBase.Y = 0;
p.InputSubrectSize.Width  = static_cast<unsigned>(inW);
p.InputSubrectSize.Height = static_cast<unsigned>(inH);
p.OutputSubrectBase.X = static_cast<unsigned>(outX);
p.OutputSubrectBase.Y = static_cast<unsigned>(outY);
p.OutputSubrectSize.Width  = static_cast<unsigned>(outW);
p.OutputSubrectSize.Height = static_cast<unsigned>(outH);
p.QualityLevel = static_cast<NVSDK_NGX_VSR_QualityLevel>(quality);

NVSDK_NGX_Result r = NGX_D3D11_EVALUATE_VSR_EXT(ctx, vsr, params, &p);
```

**TrueHDR evaluate — verbatim. Note the tone-mapping controls:**

```c
NVSDK_NGX_D3D11_TRUEHDR_Eval_Params p{};
p.pInput  = inRes;
p.pOutput = outRes;
p.InputSubrectTL.X = x;  p.InputSubrectTL.Y = y;
p.InputSubrectBR.Width  = x + w;
p.InputSubrectBR.Height = y + h;
p.OutputSubrectTL.X = x; p.OutputSubrectTL.Y = y;
p.OutputSubrectBR.Width  = x + w;
p.OutputSubrectBR.Height = y + h;
p.Contrast    = 100;
p.Saturation  = 100;
p.MiddleGray  = 50;
p.MaxLuminance = 1000;

NVSDK_NGX_Result r = NGX_D3D11_EVALUATE_TRUEHDR_EXT(ctx, thdr, params, &p);
```

So the TrueHDR API exposes **Contrast, Saturation, MiddleGray, MaxLuminance** — these are the programmatic equivalents of the NVIDIA App's "RTX Video HDR" sliders.

### 1.5 Direct answer to Q1's interface-name question

- ❌ **No** `NvVSRSuperResolution`, **no** `NVVSRSuperResolution` interface was found in any source. Those names appear to be invented; **do not use them.**
- ❌ **No** D3D11-based `ID3D11VSR`-style COM interface exists.
- ✅ The real surface is the **NGX** entry points listed above, plus the DXVA extension GUIDs in §2.

### 1.6 Version numbers and GPU support

| Claim | Status |
|---|---|
| **RTX Video SDK 1.1 / v1.1.0** exists (`nvngx_vsr.dll`, `nvngx_truehdr.dll`) | **VERIFIED** (project README + build script default path) |
| VSR supported on **RTX 20/30/40/50 series** | **VERIFIED** for 20/30/40/50 as a family claim; see caveat below |
| VSR launched on **RTX 40 & 30 series** (Feb 2023) | **VERIFIED** — NVIDIA blog headline: "RTX Video Super Resolution Now Available for GeForce RTX 40 and 30 Series GPUs" |
| RTX Video HDR launched Jan 2024 | **VERIFIED** — NVIDIA blog, Jan 24 2024 |
| **"VSR 1.0 / 1.5 / 2.0"** as *API* version numbers | **UNVERIFIED / probably a category error.** The quality slider is **1–4**. I found **no** evidence of "VSR 2.0" or "VSR 1.5" as API version designators. |
| Which GPU series supports *which* VSR quality level | **UNVERIFIED** — no authoritative table found |

**Quality level — VERIFIED semantics:**

```c
if (quality < 1 || quality > 4) {
    set_status(st, 0, NVSDK_NGX_Result_FAIL_InvalidParameter,
               "quality must be 1-4 (0 is bicubic, not VSR)");
    return 0;
}
```

`p.QualityLevel = static_cast<NVSDK_NGX_VSR_QualityLevel>(quality)` — so `NVSDK_NGX_VSR_QualityLevel` is an enum whose valid range is 1–4, and **0 means bicubic (i.e. not VSR)**. This maps directly to the NVIDIA Control Panel / NVIDIA App "1 to 4" slider.

> ⚠️ **UNVERIFIED:** I could not retrieve the official `developer.nvidia.com/rtx-video-sdk` landing page (all guessed URLs returned 404, and `developer.nvidia.com/rtx-video-sdk` resolved to a generic **AI for Media** page). The **download URL `https://developer.nvidia.com/rtx-video-sdk`** is cited by the project README as the SDK source. Treat as **developer-reported, not first-party-confirmed.**
>
> ⚠️ **UNVERIFIED:** The `NVSDK_NGX_VSR_QualityLevel` enum's member names. Only the *range* (1–4) and the cast are verified.

---

## 2. Interoperation with DXVA / D3D11 Video Processor

### 2.1 Direct answer

- **The RTX Video SDK does *not* interoperate with DXVA / `ID3D11VideoProcessor` at all.** It is an independent NGX feature operating on `ID3D11Resource`s on an `ID3D11Device`. §1 above.
- **Separately, the *driver* does expose VSR and TrueHDR as vendor-private extensions on the standard DXVA video processor.** This is a private, undisclosed-to-Microsoft contract, discovered via two independent reverse-engineering-grade implementations that agree exactly.

### 2.2 The DXVA extension mechanism — VERIFIED, verbatim GUIDs and structs

The mechanism is **`ID3D11VideoContext::VideoProcessorSetStreamExtension`** — a documented Microsoft API for "driver-specific state on a video processing stream" — combined with **NVIDIA-private GUIDs**.

**From Chromium `ui/gl/swap_chain_presenter.cc` (verbatim):**

```cpp
HRESULT ToggleNvidiaVpSuperResolution(ID3D11VideoContext* video_context,
                                      ID3D11VideoProcessor* video_processor,
                                      bool enable) {
  constexpr GUID kNvidiaPPEInterfaceGUID = {
      0xd43ce1b3, 0x1f4b, 0x48ac,
      {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};
  constexpr UINT kStreamExtensionVersionV1 = 0x1;
  constexpr UINT kStreamExtensionMethodSuperResolution = 0x2;

  struct {
    UINT version;
    UINT method;
    UINT enable;
  } stream_extension_info = {kStreamExtensionVersionV1,
                             kStreamExtensionMethodSuperResolution,
                             enable ? 1u : 0u};

  HRESULT hr = video_context->VideoProcessorSetStreamExtension(
      video_processor, 0, &kNvidiaPPEInterfaceGUID,
      sizeof(stream_extension_info), &stream_extension_info);
  ...
}
```

**From mpv `video/filter/vf_d3d11vpp.c` (verbatim) — same GUID, same numbers:**

```c
#ifndef NVIDIA_PPE_INTERFACE_GUID
DEFINE_GUID(NVIDIA_PPE_INTERFACE_GUID,
            0xd43ce1b3, 0x1f4b, 0x48ac, 0xba, 0xee,
            0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7);
#endif

static void enable_nvidia_rtx_extension(struct mp_filter *vf)
{
    struct nvidia_ext {
        unsigned int version;
        unsigned int method;
        unsigned int enable;
    } ext = {1, 2, 1};

    HRESULT hr = ID3D11VideoContext_VideoProcessorSetStreamExtension(
                     p->video_ctx, p->video_proc, 0,
                     &NVIDIA_PPE_INTERFACE_GUID, sizeof(ext), &ext);

    if (FAILED(hr)) {
        MP_WARN(vf, "Failed to enable NVIDIA RTX Super Resolution: %s\n", ...);
    } else {
        MP_VERBOSE(vf, "NVIDIA RTX Super Resolution enabled.\n");
    }
}
```

> **The two independent implementations agree on all three constants** — `{version=1, method=2, enable=1}` and the GUID `d43ce1b3-1f4b-48ac-baee-c3c25375e6f7`. This is strong verification. "PPE" is presumably the driver-internal codename.

### 2.3 RTX Video HDR (TrueHDR) as a DXVA extension — VERIFIED

**Chromium, verbatim — including the capability probe:**

```cpp
constexpr GUID kNvidiaTrueHDRInterfaceGUID = {
    0xfdd62bb4, 0x620b, 0x4fd7,
    {0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3}};

bool NvidiaDriverSupportsTrueHDR(ID3D11VideoContext* video_context,
                                 ID3D11VideoProcessor* video_processor) {
  UINT driver_supports_true_hdr = 0;
  HRESULT hr = video_context->VideoProcessorGetStreamExtension(
      video_processor, 0, &kNvidiaTrueHDRInterfaceGUID,
      sizeof(driver_supports_true_hdr), &driver_supports_true_hdr);

  // The runtime never fails the GetStreamExtension hr unless a bad memory size
  // is provided.
  if (FAILED(hr)) { ... return false; }

  return (driver_supports_true_hdr == 1);
}

HRESULT ToggleNvidiaVpTrueHDR(bool driver_supports_vp_auto_hdr, ...) {
  constexpr UINT kStreamExtensionVersionV4 = 0x4;
  constexpr UINT kStreamExtensionMethodTrueHDR = 0x3;
  struct {
    UINT version;
    UINT method;
    UINT enable : 1;
    UINT reserved : 31;
  } stream_extension_info = {kStreamExtensionVersionV4,
                             kStreamExtensionMethodTrueHDR, enable ? 1u : 0u,
                             0u};
  HRESULT hr = video_context->VideoProcessorSetStreamExtension(
      video_processor, 0, &kNvidiaTrueHDRInterfaceGUID,
      sizeof(stream_extension_info), &stream_extension_info);
  ...
}
```

**mpv agrees exactly:**

```c
#ifndef NVIDIA_TRUE_HDR_INTERFACE_GUID
DEFINE_GUID(NVIDIA_TRUE_HDR_INTERFACE_GUID,
            0xfdd62bb4, 0x620b, 0x4fd7, 0x9a, 0xb3,
            0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3);
#endif
```
```c
    struct nvidia_ext {
        unsigned int version;
        unsigned int method;
        unsigned int enable : 1;
        unsigned int reserved : 31;
    } ext = {4, 3, 1};
```

**Summary table of the DXVA extension contract:**

| Feature | GUID | version | method | Payload |
|---|---|---|---|---|
| RTX VSR ("PPE") | `d43ce1b3-1f4b-48ac-baee-c3c25375e6f7` | `1` (`0x1`) | `2` (`0x2`) | `{UINT version; UINT method; UINT enable;}` |
| RTX Video HDR (TrueHDR) | `fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3` | `4` (`0x4`) | `3` (`0x3`) | `{UINT version; UINT method; UINT enable:1; UINT reserved:31;}` |
| Intel VSR (for contrast) | `edd1d4b9-8659-4cbc-a4d6-9831a2163ac3` | — | — | uses `SetOutputExtension` + `SetStreamExtension` |

### 2.4 Is it a "driver-level Video Processor MFT / DXVA extension players get for free"?

**Answer: an extension, yes — but not via an MFT.**

- ✅ **Driver-level DXVA extension: VERIFIED.** It hooks the standard `ID3D11VideoProcessor`, so an app already doing `VideoProcessorBlt` scaling gets VSR by adding **one extension call**.
- ❌ **"Video Processor MFT" as the delivery mechanism: UNVERIFIED / likely wrong.** No source showed an MFT. The path is direct `ID3D11VideoContext` → `ID3D11VideoProcessor`.
- ⚠️ **"for free" is conditional, not automatic.** It is free *in code terms* (~15 lines), but it is **off unless the app explicitly opts in**, and it is additionally **gated by the user's driver setting** and by whether the video processor actually needs to scale (§3).

### 2.5 Important: an "enable" call is required — it is NOT implicit

Both Chrome and mpv **explicitly call the extension**. It is not that "the video processor magically upscales with AI". The app must:

1. Create the video processor with a **larger output than input** (§3).
2. Call `VideoProcessorSetStreamExtension(..., &NVIDIA_PPE_INTERFACE_GUID, ..., &ext)` with `enable = 1`.

### 2.6 `ID3D11VideoProcessorEnumerator` / "custom rate" clarification

The brief asked whether there is a "custom rate" mechanism. **VERIFIED — it exists but is unrelated to VSR.** mpv enumerates:

```c
D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS rcaps;
hr = ID3D11VideoProcessorEnumerator_GetVideoProcessorRateConversionCaps(p->vp_enum, n, &rcaps);
...
for (UINT c = 0; c < rcaps.CustomRateCount; c++) {
    D3D11_VIDEO_PROCESSOR_CUSTOM_RATE cr;
    ID3D11VideoProcessorEnumerator_GetVideoProcessorCustomRate(p->vp_enum, n, c, &cr);
}
```

And notes verbatim in a comment:

> "None of the major GPU vendors seem to support custom rates for proper IVTC frame dropping. Only frame reconstruction is supported."
> "custom rate would be perfect for that, but it's not supported by any driver in practice."

So: **custom rate ≠ VSR.** It is a frame-rate/deinterlacing feature, and it is effectively unimplemented. **Do not cite it as the VSR hook.**

---

## 3. The "zero-code" path — what a player must actually do

### 3.1 The mechanism, stated precisely

**VSR is applied by the driver inside `VideoProcessorBlt`, when (a) the video processor is configured to scale up, and (b) the app has enabled the NVIDIA PPE stream extension.**

The "super resolution is enabled when the video processor does the scaling" claim is **VERIFIED in substance, with an important nuance**: scaling is **necessary but not sufficient**. Opt-in is also required.

### 3.2 Evidence — mpv's implementation (the clearest worked example)

The filter computes an **upscaled output size** purely from a scale factor:

```c
p->out_params = p->params;
p->out_params.w = (int)(p->opts->scale * p->params.w);
p->out_params.w += p->out_params.w % 2 != 0;
p->out_params.h = (int)(p->opts->scale * p->params.h);
p->out_params.h += p->out_params.h % 2 != 0;
```

It only engages the video processor when the geometry actually changes:

```c
p->require_filtering = !mp_image_params_static_equal(&p->params, &p->out_params) ||
                       want_nvidia_true_hdr(vf);
...
if (!mp_refqueue_should_deint(p->queue) && !p->require_filtering) {
    // no filtering  → passthrough, VideoProcessorBlt never called
    mp_refqueue_write_out_pin(p->queue, in);
} else {
    mp_refqueue_write_out_pin(p->queue, render(vf));
}
```

> **This is the crux:** at `scale = 1.0` (the default), `out_params == params`, `require_filtering` is false, and **the video processor is never invoked — so VSR can never run.** This is exactly the "silently does nothing" trap.

The video processor is created with **differing input/output dimensions**:

```c
D3D11_VIDEO_PROCESSOR_CONTENT_DESC vpdesc = {
    .InputFrameFormat = p->d3d_frame_format,
    .InputWidth  = p->c_w,
    .InputHeight = p->c_h,
    .OutputWidth = p->out_params.w,      // ← larger than input ⇒ scaling
    .OutputHeight = p->out_params.h,
};
hr = ID3D11VideoDevice_CreateVideoProcessorEnumerator(p->video_dev, &vpdesc, &p->vp_enum);
...
hr = ID3D11VideoDevice_CreateVideoProcessor(p->video_dev, p->vp_enum, rindex, &p->video_proc);
```

Then the standard `VideoProcessorBlt` call:

```c
hr = ID3D11VideoContext_VideoProcessorBlt(p->video_ctx, p->video_proc,
                                          out_view, p->output_seq, 1, &stream);
```

And enabling VSR is **only two lines** (plus the extension struct):

```c
case SCALING_NVIDIA_RTX:
    enable_nvidia_rtx_extension(vf);
    break;
```

mpv exposes this as `--vf=d3d11vpp=scaling-mode=nvidia:scale=2.0`.

### 3.3 Evidence — mpv's own notes on the scale-factor requirement

mpv-based real-world guidance (installer README) states it plainly:

> "**RTX VSR needs a scale factor.** mpv's `d3d11vpp` filter only runs VSR when it has to resize the frame. The setup window therefore installs a small Lua script that matches each video to its current displayed size: on a 4K display, fullscreen 540p uses 4x, 720p uses 3x, 1080p uses 2x, 1440p uses 1.5x, and 4K uses 1x."

> "You can also verify this visually... If you get other messages, your source is NOT being upscaled."

### 3.4 Evidence — Chrome's equivalent

Chrome sets an explicit output/dest rect at swap-chain size and enables the extension per-Blt:

```cpp
bool use_vp_super_resolution = enable_vp_super_resolution_ && !is_on_battery_power_;
if (enable_vp_super_resolution_) {
  hr = ToggleVpSuperResolution(gpu_vendor_id_, video_context.Get(),
                               video_processor.Get(), use_vp_super_resolution);
  if (FAILED(hr)) {
    enable_vp_super_resolution_ = false;
    use_vp_super_resolution = false;
  }
}
```

Note two extra real-world behaviours visible here:

- **Battery gating:** `&& !is_on_battery_power_` — Chrome disables VSR on battery.
- **Graceful retry:** if `VideoProcessorBlt` fails with VSR on, Chrome **disables VSR and retries the Blt** — and if the retry succeeds, it permanently stops using VSR. A robust implementation should copy this.

### 3.5 Checklist: the actual "free VSR" recipe

1. Decode with **DXVA hardware decoding** into a D3D11 texture (`NV12`/`P010`). ✅ *Soft-decode + upload also works — see note below.*
2. `QueryInterface` the `ID3D11Device` → `ID3D11VideoDevice`; the context → `ID3D11VideoContext`.
3. Create `ID3D11VideoProcessorEnumerator` with `OutputWidth/Height` **> input** (this is the scaling that triggers VSR).
4. `CreateVideoProcessor` → `ID3D11VideoProcessor`.
5. Wrap input/output textures in `ID3D11VideoProcessorInputView` / `ID3D11VideoProcessorOutputView`.
6. **Call `VideoProcessorSetStreamExtension(vp, 0, &NVIDIA_PPE_INTERFACE_GUID, sizeof(ext), &ext)` with `{1, 2, 1}`.**
7. `VideoProcessorBlt(...)` into the output view.
8. For HDR: **first** `VideoProcessorGetStreamExtension` with the TrueHDR GUID to check support, then `SetStreamExtension` with `{4, 3, 1}`; tag output as **BT.2020 + PQ (HDR10)** and use a 10-bit format.

**Note on software decoding — VERIFIED works:**

> "**Software decoding is fine.** Codecs without a D3D11 decoder fall back to software and mpv uploads the frames to D3D11 for the filter."

So the requirement is a **D3D11 video processor**, not necessarily a hardware decoder.

### 3.6 Answers to the brief's specific sub-hypotheses

| Hypothesis from brief | Verdict |
|---|---|
| Set a "Super Resolution" flag | ✅ **TRUE** — `enable=1` in the PPE stream extension |
| Render through the D3D11 Video Processor with a specific output size | ✅ **TRUE** — output must be larger than input |
| Avoid using the display's native scaling | ✅ **TRUE in effect** — the app must scale in the VP, not let DWM/display do it. Chrome even has a feature flag literally named **`kDisableVPBLTUpscale`** to *move* scaling off VP-Blt and onto DWM, which would defeat VSR. |
| Use `IDXGIDevice`/DXGI scaling instead of a shader | ⚠️ **MISLEADING** — DXGI *Present*/swap-chain stretch scaling does **not** trigger the VP path. Scaling must happen **inside `VideoProcessorBlt`**. |
| `ID3D11VideoProcessor` + `D3D11_VIDEO_PROCESSOR_STREAM` | ✅ **TRUE** — both are used, exactly as in mpv's `render()` |

---

## 4. RTX Video HDR — API story and driver toggles

### 4.1 API story: it does **both**

**VERIFIED — HDR has two independent routes, mirroring VSR:**

1. **DXVA extension** (free path): GUID `fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3`, `{version=4, method=3, enable=1}`. §2.3.
2. **NGX feature** (explicit path): `nvngx_truehdr.dll`, `NGX_D3D11_CREATE_TRUEHDR_EXT`, `NGX_D3D11_EVALUATE_TRUEHDR_EXT`, `NVSDK_NGX_D3D11_TRUEHDR_Eval_Params`. §1.4.

It rides on the **same `ID3D11VideoProcessor`** in the DXVA route — but it is a **separate feature with its own GUID, its own version number, and its own capability probe**.

### 4.2 HDR output requirements — VERIFIED

**mpv, verbatim:**

```c
if (want_nvidia_true_hdr(vf) && enable_nvidia_true_hdr(vf)) {
    // NVIDIA RTX Video HDR seems to require BT.2020+PQ RGB output.
    p->out_params.color = pl_color_space_hdr10;
    p->out_params.color.hdr.max_luma = 1000;
    MP_WARN(vf, "Tagging image output as HDR with max-luma=1000 nits for "
                "NVIDIA RTX Video HDR. This is only a guess. ...");
    const enum mp_imgfmt output_format = IMGFMT_X2BGR10;   // 10-bit
    ...
}
```

**Chrome, verbatim:**

```cpp
DXGI_COLOR_SPACE_TYPE swap_dxgi_color_space =
    use_vp_auto_hdr ? gfx::ColorSpaceWin::GetDXGIColorSpace(
                          gfx::ColorSpace::CreateHDR10())
                    : output_dxgi_color_space;
```
and it prefers `DXGI_FORMAT_R10G10B10A2_UNORM` for the swap chain when HDR is in play.

**So the output constraints are: 10-bit format (`R10G10B10A2_UNORM` / `X2BGR10`), BT.2020 primaries, PQ (SMPTE ST 2084) transfer.** ✅

### 4.3 SDR-only input — VERIFIED

```c
static bool want_nvidia_true_hdr(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    return p->opts->nvidia_true_hdr && !pl_color_space_is_hdr(&p->params.color);
}
```
```c
if (p->opts->nvidia_true_hdr && pl_color_space_is_hdr(&p->params.color)) {
    MP_WARN(vf, "NVIDIA RTX Video HDR requested, but the source is "
                "already HDR, not used.\n");
}
```

Upstream commit confirms this is deliberate:
> "**vf_d3d11vpp: enable NVIDIA RTX Video only for SDR input** — This filter only converts SDR to HDR, so disable it if we have already HDR on input. Note that it also seems to be disabled if **HDR is disabled in Windows**, but we don't check that, because filters are not related to display." — commit `5abce15f1`, 2026-06-28

> Note the author's observation that it also seems gated on **Windows HDR being enabled** — flagged as the author's inference, not confirmed by NVIDIA. Treat as **WEAK / UNVERIFIED**.

### 4.4 Driver settings — VERIFIED

| Control | Detail | Status |
|---|---|---|
| **NVIDIA Control Panel → "Adjust video image settings" → "RTX video enhancement"** → check **Super resolution**, quality **1–4** | Verbatim from NVIDIA's launch blog | **VERIFIED** |
| Same panel → enable **HDR** | NVIDIA blog Jan 2024: *"Open the NVIDIA Control Panel and navigate to Adjust video image settings > RTX Video Enhancement — then enable HDR."* | **VERIFIED** |
| Windows HDR must be on (System > Display > HDR) for HDR path | NVIDIA blog Jan 2024 | **VERIFIED** |
| **NVIDIA App → System/Graphics → Video** ("RTX Video enhancement" / "RTX Video") | Modern installer docs: *"you need an NVIDIA GPU with Super Resolution and HDR switched on in the **NVIDIA App under System > Video**"* | **VERIFIED (third-party doc)** |
| **Per-app override** in NVIDIA App | | **UNVERIFIED** — I found **no** first-party documentation of a per-app VSR/HDR override. |

> ⚠️ **UNVERIFIED:** The brief asks specifically about "NVIDIA App > RTX Video per-app override". I could not verify that this exists. NVIDIA App does have a per-application 3D-settings override system generally, but **I found no evidence it extends to RTX Video VSR/HDR**. **Do not assert this without further checking.**
>
> ⚠️ **UNVERIFIED:** The RTX Video FAQ (`nvidia.custhelp.com/app/answers/detail/a_id/5448`) returned **HTTP 403** (Oracle-hosted support portal outage). This is the canonical FAQ and likely resolves many open questions — worth a retry.

### 4.5 Critical HDR finding — a real trap

From the mpv-based installer README (tested on an RTX 5090):

> "**RTX Video HDR does nothing on mpv 0.41.0.** That release turns on the driver extension but never retags the output as HDR10, so the frames come out identical. Screenshots taken with and without it had the same SHA256, for both SDR and HDR sources."

> "**`gpu-api=d3d11` and the `vf=` line go together.** The filter can only be created on a D3D11 context, and `vf=` is global, so if mpv renders with another API the filter fails to build and playback dies..."

**Lesson for third-party players: enabling the extension is not enough — you must also correctly retag the output as HDR10 (BT.2020 + PQ) and use a 10-bit surface, or the result is visually identical to SDR.** This is the single most likely cause of "I enabled it and nothing happened."

---

## 5. Input and output constraints

| Constraint | Status | Evidence |
|---|---|---|
| **VSR source must be ≤ 1920×1080** | ⚠️ **PARTIALLY VERIFIED — the brief's specific claim is NOT confirmed** | I found **no** first-party statement of a hard 1080p input cap for VSR. What *is* verified: (a) VSR quality 1–4, (b) VSR output capped at 3840×2160 in the NGX wrapper, (c) launch framing was "upscale lower-resolution content **up to 4K**". |
| **VSR output capped at 3840×2160** | ✅ **VERIFIED** | `kMaxOutW = 3840; kMaxOutH = 2160;` and `"output exceeds 3840x2160"` in the NGX wrapper. NVIDIA App slider maxes at 4K. |
| **No VSR on sources ≥ 1440p** | ✅ **VERIFIED (project-level policy)** | `rtx-vsr-lab` README: *"Sources at or above 2560×1440 skip VSR (the SDK is not used as a downscaler)."* and *"VSR is not applied to sources ≥ 1440p."* |
| **VSR is not a downscaler** | ✅ **VERIFIED** | `"Downscaling is refused."` (same README). Consistent with §3: the VP must scale **up**. |
| **Minimum source size** | ⚠️ **Conflicting.** VFX-SDK ≥160×90; rtx-vsr-lab rejects `< 640×360` | These are **different SDKs**. The 160×90 figure belongs to the **Maxine VFX** SuperRes filter, **not** the NGX RTX Video SDK. **Do not mix them.** |
| **Codec support** | ⚠️ **UNVERIFIED for the NGX SDK** | Browsers support "nearly all content streamed in Chrome/Edge". No codec allow-list found. |
| **NV12 / P010 only** | ⚠️ **UNVERIFIED as a hard constraint; contradicted in practice** | Chrome uses `NV12`, `P010`, `YUY2`, `BGRA`, `RGB10A2` swap chains. The NGX wrapper used `R8G8B8A8_UNORM` **input**. So **NV12/P010-only is NOT a general rule.** |
| **No 10-bit for VSR** | ⚠️ **UNVERIFIED, and the evidence runs the other way** | NGX VSR output `DXGI_FORMAT_R8G8B8A8_UNORM`; but with TrueHDR chained it is `R10G10B10A2_UNORM`. Chrome's VP path handles P010 SDR content. So "no 10-bit for VSR" is **not supported** by what I found. |
| **TrueHDR requires HDR10 display** | ✅ **VERIFIED** | *"RTX Video HDR requires an RTX GPU connected to an HDR10-compatible monitor or TV."* + Windows HDR enabled |
| **TrueHDR is SDR-input only** | ✅ **VERIFIED** | §4.3 |
| **DXVA-decoded input required** | ❌ **FALSE** | Software decode → upload to D3D11 works. §3.5 |

---

## 6. Officially documented capability query

### 6.1 DXVA route — YES, and it is the recommended pattern

**TrueHDR has a documented, working capability probe.** Verbatim (Chromium):

```cpp
bool NvidiaDriverSupportsTrueHDR(ID3D11VideoContext* video_context,
                                 ID3D11VideoProcessor* video_processor) {
  UINT driver_supports_true_hdr = 0;
  HRESULT hr = video_context->VideoProcessorGetStreamExtension(
      video_processor, 0, &kNvidiaTrueHDRInterfaceGUID,
      sizeof(driver_supports_true_hdr), &driver_supports_true_hdr);

  // The runtime never fails the GetStreamExtension hr unless a bad memory size
  // is provided.
  if (FAILED(hr)) { ... return false; }
  return (driver_supports_true_hdr == 1);
}
```

mpv does the same and warns on failure:

```c
HRESULT hr = ID3D11VideoContext_VideoProcessorGetStreamExtension(
                 p->video_ctx, p->video_proc, 0,
                 &NVIDIA_TRUE_HDR_INTERFACE_GUID, sizeof(supported), &supported);
if (FAILED(hr) || !supported) {
    MP_WARN(vf, "NVIDIA RTX Video HDR not supported.\n");
    return false;
}
```

> ⚠️ **Important caveat, verbatim from Chromium: "The runtime never fails the GetStreamExtension hr unless a bad memory size is provided."** So `HRESULT` alone is **not** a support signal — you must **check the returned `UINT` is `== 1`**.

**VSR capability probe: NOT FOUND.** Neither Chromium nor mpv does a `GetStreamExtension` probe for VSR — they call `SetStreamExtension` and treat **`FAILED(hr)` on the Set as the only signal**, with a Blt-failure retry as a second safety net. **There is no verified documented VSR probe.** Practical approach: attempt the Set and handle failure; do not assume support.

**Other documented DXGI/D3D11 queries available:**
- `ID3D11VideoProcessorEnumerator::GetVideoProcessorCaps` → `D3D11_VIDEO_PROCESSOR_CAPS`
- `ID3D11VideoProcessorEnumerator::GetVideoProcessorRateConversionCaps` → `RateConversionCapsCount`, `ProcessorCaps`, `PastFrames`, `FutureFrames`, `CustomRateCount`
- `IDXGIAdapter::GetDesc` → `DXGI_ADAPTER_DESC.VendorId` — **`0x10DE` = NVIDIA** (Chrome gates on exactly this: `if (gpu_vendor_id == 0x10de)`)
- `IDXGIOutput::GetDesc1` → `DXGI_OUTPUT_DESC1` (`BitsPerColor`, color primaries) for HDR display detection

### 6.2 NGX route — YES, explicitly designed for it

This is the cleanest answer to Q6. Verbatim capability parameters read after `NVSDK_NGX_D3D11_GetCapabilityParameters(&params)`:

```c
params->Get(NVSDK_NGX_Parameter_VSR_Available, &vsr_avail);
params->Get(NVSDK_NGX_Parameter_TrueHDR_Available, &thdr_avail);
params->Get(NVSDK_NGX_Parameter_VSR_NeedsUpdatedDriver, &need_drv);
params->Get(NVSDK_NGX_Parameter_VSR_MinDriverVersionMajor, &maj);
params->Get(NVSDK_NGX_Parameter_VSR_MinDriverVersionMinor, &minr);
params->Get(NVSDK_NGX_Parameter_VSR_FeatureInitResult, &init_res);
```

| Parameter | Meaning |
|---|---|
| `NVSDK_NGX_Parameter_VSR_Available` | Is VSR usable on this GPU/driver? |
| `NVSDK_NGX_Parameter_TrueHDR_Available` | Is TrueHDR usable? |
| `NVSDK_NGX_Parameter_VSR_NeedsUpdatedDriver` | Driver too old |
| `NVSDK_NGX_Parameter_VSR_MinDriverVersionMajor` / `...Minor` | Minimum required driver version |
| `NVSDK_NGX_Parameter_VSR_FeatureInitResult` | Feature init result code |

> ✅ **This is the officially-documented way for a third-party app to query whether VSR/HDR will be applied — but only on the NGX (RTX Video SDK) path.** On the DXVA path there is a probe for HDR only, and none for VSR.
>
> ⚠️ **UNVERIFIED:** I could not retrieve NVIDIA's own reference page naming these parameters; they are verified from working code that compiles against `nvsdk_ngx_defs_vsr.h` / `nvsdk_ngx_defs_truehdr.h`. The names are verbatim from source, not invented.

---

## 7. Explicit UNVERIFIED list (please do not treat these as findings)

1. **"VSR 1.0 / 1.5 / 2.0" version numbers** — no evidence found. The SDK version observed is **1.1**; the quality slider is **1–4**. Possible category error.
2. **Per-GPU-series × per-quality-level support matrix** — not found.
3. **NVIDIA App "RTX Video" per-app override** — not verified to exist.
4. **A hard ≤1920×1080 input cap for VSR** — not confirmed first-party.
5. **Codec allow-list for VSR** — not found.
6. **"NV12/P010 only"** and **"no 10-bit for VSR"** — not supported; evidence suggests otherwise.
7. **`NVSDK_NGX_VSR_QualityLevel` enum member names** — only the 1–4 range is verified.
8. **`developer.nvidia.com/rtx-video-sdk`** as SDK download page — cited by a third party; my direct fetches 404'd.
9. **The RTX Video FAQ (custhelp id 5448)** — HTTP 403, could not read. **High-value retry target.**
10. **Exact VSR max input resolution and scaling-ratio limits** — not found.
11. **Whether the NGX and DXVA routes produce bit-identical output** — not tested.

---

## 8. Sources — every URL successfully fetched, and what it said

### NVIDIA

| URL | Result |
|---|---|
| `https://blogs.nvidia.com/blog/rtx-video-super-resolution/` | ✅ **HTTP 200.** Launch post, Feb 28 2023. "**RTX Video Super Resolution Now Available for GeForce RTX 40 and 30 Series GPUs**". Chrome 110.0.5481.105+/Edge 110.0.1587.56+ required. Explains the network (single pass; predicts residual, superimposed on traditional upscale). **Verbatim enable path:** *"launch the NVIDIA Control Panel and open 'Adjust video image settings.' Check the super resolution box under 'RTX video enhancement' and select a quality from one to four."* Confirms **quality 1–4 = the NVIDIA App/CP scale**. |
| `https://blogs.nvidia.com/blog/rtx-video-hdr-remix-studio-driver/` | ✅ **HTTP 200.** RTX Video HDR launch, Jan 24 2024 (January Studio Driver). *"transforms standard dynamic range video playing in internet browsers into stunning high dynamic range (HDR) on **HDR10 displays**."* **Verbatim enable steps:** (1) install driver, (2) *"Ensure Windows HDR features are enabled by navigating to System > Display > HDR"*, (3) *"Open the NVIDIA Control Panel and navigate to Adjust video image settings > RTX Video Enhancement — then enable HDR."* *"RTX Video HDR requires an RTX GPU connected to an HDR10-compatible monitor or TV."* Notes VSR + HDR are usable together. |
| `https://docs.nvidia.com/video-technologies/index.html` | ✅ **HTTP 200.** NVIDIA Video Technologies docs hub. Lists **only** Optical Flow SDK 5.0, Video Codec SDK 13.1, PyNvVideoCodec 2.2, plus Video Codec SDK archive versions. **No RTX Video Super Resolution SDK is listed here** — a notable negative result. |
| `https://developer.nvidia.com/topics/ai/generative-ai/ai-for-media` | ✅ **HTTP 200.** NVIDIA "AI for Media" page. This is where several `developer.nvidia.com/rtx-video-sdk*` URLs **redirect**. Describes AI-for-Media SDKs/NIM for broadcast; does **not** document the RTX Video SDK API. |
| `https://nvidia.custhelp.com/app/answers/detail/a_id/5448` | ❌ **HTTP 403** — Oracle support portal outage page. The RTX Video FAQ. **Not read.** |
| `https://developer.nvidia.com/rtx-video-super-resolution` | ❌ HTTP 404 |
| `https://developer.nvidia.com/video-technologies` | ❌ HTTP 404 |
| `https://docs.nvidia.com/video-technologies/rtx-video-super-resolution/index.html` | ❌ HTTP 404 |
| `https://developer.nvidia.com/rtx-video-sdk` | ❌ Redirected to the AI-for-Media page (no dedicated SDK landing page served) |
| `https://developer.nvidia.com/downloads/rtx-video-sdk` | ❌ HTTP 404 |
| `https://developer.nvidia.com/rtx-video-super-resolution-sdk-download` | ❌ HTTP 404 |
| `https://docs.nvidia.com/rtx-video-sdk/index.html` | ❌ HTTP 404 |
| `https://www.nvidia.com/en-us/geforce/guides/nvidia-rtx-video-super-resolution/` | ❌ HTTP 404 |
| `https://www.nvidia.com/en-us/geforce/rtx-video/` | ❌ HTTP 404 |
| `https://www.nvidia.com/en-us/geforce/news/rtx-video-super-resolution-sdk/` | ❌ HTTP 404 |

### Microsoft (Microsoft Learn)

| URL | Result |
|---|---|
| `https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nn-d3d11-id3d11videocontext` | ✅ **HTTP 200.** The **DXVA video-processor host interface**. Confirms and lists, verbatim, **`ID3D11VideoContext::VideoProcessorSetStreamExtension`** — *"Sets a driver-specific state on a video processing stream."* — and **`VideoProcessorGetStreamExtension`** — *"Gets a driver-specific state for a video processing stream."* Also `VideoProcessorSetOutputExtension` / `GetOutputExtension`, `VideoProcessorBlt` — *"Performs a video processing operation on one or more input samples and writes the result to a Direct3D surface."* Header **`d3d11.h`**, min **Windows 8**. D3D9 equivalents named include **`IDXVAHD_VideoProcessor`** and `IDirectXVideoProcessor`. |
| `https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nn-d3d11_1-id3d11videocontext1` | ✅ **HTTP 200.** `ID3D11VideoContext1` (header **`d3d11_1.h`**, min **Windows 10**), inherits `ID3D11VideoContext`. Adds `VideoProcessorSetStreamColorSpace1` / `SetOutputColorSpace1` and **`VideoProcessorGetBehaviorHints`** — *"Returns driver hints that indicate which of the video processor operations are best performed using multi-plane overlay hardware rather than ID3D11VideoContext::VideoProcessorBlt."* Relevant to HDR/overlay paths. |
| `https://learn.microsoft.com/en-us/windows/win32/api/dxva2api/nn-dxva2api-id3d11videoprocessor` | ❌ HTTP 404 (the interface lives under `d3d11.h`, not `dxva2api.h`) |
| `https://learn.microsoft.com/en-us/windows/win32/api/dxva2api/nn-dxva2api-id3d11videoprocessorenumerator` | ❌ HTTP 404 |
| `https://learn.microsoft.com/en-us/windows/win32/api/dxva2api/nn-dxva2api-id3d11videocontext` | ❌ HTTP 404 |
| `https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11videocontext1-videoprocessorsetstreamextension` | ❌ HTTP 404 (correct path is under `d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamextension`) |
| `https://learn.microsoft.com/en-us/windows/win32/api/dxva2api/nf-dxva2api-id3d11videocontext-videoprocessorsetstreamextension` | ❌ HTTP 404 |
| `https://learn.microsoft.com/en-us/windows/win32/api/dxva2api/nf-dxva2api-id3d11videocontext-videoprocessorgetstreamextension` | ❌ HTTP 404 |

### Chromium (canonical reference implementation)

| URL | Result |
|---|---|
| `https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/swap_chain_presenter.cc` | ✅ **HTTP 200.** **The single most valuable source.** Contains verbatim: `kNvidiaPPEInterfaceGUID = {0xd43ce1b3, 0x1f4b, 0x48ac, {0xba,0xee,0xc3,0xc2,0x53,0x75,0xe6,0xf7}}`; `kStreamExtensionVersionV1 = 0x1`; `kStreamExtensionMethodSuperResolution = 0x2`; `ToggleNvidiaVpSuperResolution()`; `kNvidiaTrueHDRInterfaceGUID = {0xfdd62bb4, 0x620b, 0x4fd7, {0x9a,0xb3,0x1e,0x59,0xd0,0xd5,0x44,0xb3}}`; `kStreamExtensionVersionV4 = 0x4`; `kStreamExtensionMethodTrueHDR = 0x3`; `NvidiaDriverSupportsTrueHDR()` using `VideoProcessorGetStreamExtension`; vendor gate on `gpu_vendor_id == 0x10de`; battery gating; Blt-failure retry logic; `VideoProcessorBlt` with `D3D11_VIDEO_PROCESSOR_STREAM`. Also mpeg's cited comment: *"For video processor extensions identifiers reference see: https://chromium.googlesource.com/..."* (The Chrome source itself confirms it is the origin of mpv's constants.) Also shows `kDisableVPBLTUpscale` and `kLimitMFSwapChainSize` feature flags. |
| `https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/gl/swap_chain_presenter.cc` | ❌ `fetch failed` (googlesource unreachable; GitHub mirror used instead) |

### mpv (independent reference implementation — corroborates Chrome exactly)

| URL | Result |
|---|---|
| `https://raw.githubusercontent.com/mpv-player/mpv/master/video/filter/vf_d3d11vpp.c` | ✅ **HTTP 200.** **Full source read.** Filter `d3d11vpp`. Verbatim: both NVIDIA GUIDs + Intel VPE GUID; `enable_nvidia_rtx_extension()` with `{1, 2, 1}`; `enable_nvidia_true_hdr()` with `{4, 3, 1}` plus `supports_nvidia_true_hdr()` probe; `recreate_video_proc()` building `D3D11_VIDEO_PROCESSOR_CONTENT_DESC` with **different input/output sizes**; `D3D11_VIDEO_PROCESSOR_CUSTOM_RATE` enumeration and the *"custom rate ... not supported by any driver in practice"* comment; "This is supposed to stop drivers from fucking up the video quality" (`VideoProcessorSetStreamAutoProcessingMode(..., FALSE)`); `IMGFMT_X2BGR10` + HDR10 tag for TrueHDR with `max_luma = 1000` and the *"This is only a guess"* warning; options `scaling-mode={standard,intel,nvidia}`, `scale`, `nvidia-true-hdr`, `mode`, `parity`; the `require_filtering` / no-filtering passthrough branch. |
| `https://api.github.com/repos/mpv-player/mpv/contents/video/filter` | ✅ HTTP 200 — used to locate `vf_d3d11vpp.c` |
| `https://api.github.com/search/commits?q=repo:mpv-player/mpv+RTX+Video+HDR` | ✅ **HTTP 200.** Three commits: **`6ca3752f7`** *"vf_d3d11vpp: add NVIDIA RTX Video HDR support"* (2024-09-30); **`f7ad4a5f8`** *"vf_d3d11vpp: set output format for NVIDIA RTX Video HDR — It outputs RGB tone mapped image, set params accordingly"* (2026-02-19); **`5abce15f1`** *"vf_d3d11vpp: enable NVIDIA RTX Video only for SDR input — This filter only converts SDR to HDR, so disable it if we have already HDR on input. Note that it also seems to be disabled if HDR is disabled in Windows, but we don't check that"* (2026-06-28). |
| `https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/d3d11/context.c` | ✅ HTTP 200. mpv's d3d11 VO context. **Contains no VSR code** — useful negative control confirming VSR lives in the `d3d11vpp` video *filter*, not the output/render path. |

### GitHub (SDK usage, real-world evidence)

| URL | Result |
|---|---|
| `https://raw.githubusercontent.com/DaSilva-Adriano/rtx-vsr-lab/main/README.md` | ✅ **HTTP 200.** *"run VSR through the **RTX Video SDK 1.1 (`nvngx_vsr.dll`)**"*; requires `include\nvsdk_ngx.h` from *"RTX Video SDK 1.1"* (`https://developer.nvidia.com/rtx-video-sdk`, default `C:\VSR\RTX_Video_SDK_v1.1.0`); *"nvngx_vsr.dll from your NVIDIA driver or the RTX Video SDK — not redistributed here"*; optional `nvngx_truehdr.dll`; GPU list *"(RTX 20/30/40/50)"*; *"Output is capped at 3840×2160"*; *"Sources at or above 2560×1440 skip VSR"*; *"Downscaling is refused"*; *"VSR is not applied to sources ≥ 1440p"*; quality 1–4. |
| `https://raw.githubusercontent.com/DaSilva-Adriano/rtx-vsr-lab/main/native/vsr_ngx.cpp` | ✅ **HTTP 200.** **Full NGX integration source read** — every API name, struct field, capability parameter and format in §1.4/§6.2 is verbatim from here. Also: `kMaxOutW=3840/kMaxOutH=2160`; format `R8G8B8A8_UNORM` in, `R10G10B10A2_UNORM` for TrueHDR out; `kAppId = 0`; `kVendorNvidia = 0x10DE`; `NVSDK_NGX_Result` name table. Output subrect mechanism (`OutputSubrectBase`/`OutputSubrectSize`) with letterbox handling. |
| `https://raw.githubusercontent.com/DaSilva-Adriano/rtx-vsr-lab/main/native/vsr_ngx.h` | ✅ **HTTP 200.** Wrapper's own C ABI (`vsr_init`, `vsr_get_info`, `vsr_try_size`, `vsr_evaluate`, `vsr_resize_rgba`, `vsr_ngx_name`; structs `VsrStatus`, `VsrInfo` with `vsr_available`/`truehdr_available`/`needs_updated_driver`/`min_driver_major/minor`/`feature_init_result`). **These are the wrapper's names, NOT NVIDIA's** — do not cite as NVIDIA API. |
| `https://api.github.com/repos/DaSilva-Adriano/rtx-vsr-lab` | ✅ HTTP 200 — repo metadata; description *"RTX VSR Lab — offline NVIDIA RTX Video Super Resolution (SDK 1.1)"* |
| `https://api.github.com/repos/DaSilva-Adriano/rtx-vsr-lab/git/trees/main?recursive=1` | ✅ HTTP 200 — file listing confirming `native/vsr_ngx.cpp`, `_vsr_ngx.dll` |
| `https://api.github.com/repos/mpv-player/mpv/git/blobs/0b5ca24c3088da8562e3bd3b60fc4a041ed25eee` | ✅ HTTP 200 (base64 blob of `vf_d3d11vpp.c`) |
| `https://raw.githubusercontent.com/Bemjo/OBS-RTX-SuperResolution/main/README.md` | ✅ **HTTP 200.** **Critical for disambiguation.** This "RTX Super Resolution" OBS plugin actually uses the **NVIDIA Video Effects SDK (Maxine VFX)** — links to `docs.nvidia.com/deeplearning/maxine/vfx-sdk-programming-guide/index.html#super-res-filter` and the **Broadcast SDK** download. Its limits are VFX limits, **not** RTX Video SDK limits: *"Super Resolution has limits on the accepted resolutions of your source size, it cannot accept anything lower than **160x90**"*; *"Artifact Reduction ... cannot be smaller than 160x90 or larger than **1920x1080**"*; *"4/3x is not supported"*; modes weak/strong. Requires **RTX 2060 or better**. |
| `https://raw.githubusercontent.com/fmolfinoo/mpv-rtx-video/main/README.md` | ✅ **HTTP 200.** Real-world field notes: *"mpv's `d3d11vpp` filter only runs VSR when it has to resize the frame"*; the 4K-display scaling table (540p→4x, 720p→3x, 1080p→2x, 1440p→1.5x, 4K→1x); *"RTX Video HDR does nothing on mpv 0.41.0. That release turns on the driver extension but never retags the output as HDR10, so the frames come out identical. Screenshots ... had the same SHA256"*; fixed by `f7ad4a5f8` and `5abce15f1`; **`[d3d11vpp] NVIDIA RTX Video HDR enabled.` / `VO: [gpu-next] 2560x1440 d3d11[x2bgr10]`**; *"It only applies to SDR video. On HDR files mpv prints `source is already HDR, not used`"*; *"`gpu-api=d3d11` and the `vf=` line go together"*; *"Software decoding is fine"*; *"you need an NVIDIA GPU with Super Resolution and HDR switched on in the NVIDIA App under **System > Video**"*. |
| `https://api.github.com/search/repositories?q=RTX+Video+Super+Resolution` | ✅ HTTP 200 — 37 repos; surfaced `rtx-vsr-lab`, `Bemjo/OBS-RTX-SuperResolution`, `fmolfinoo/mpv-rtx-video`, `Merserk/dlss5-visual-enhancer`, `kent-3/rtx-upscale` |
| `https://api.github.com/search/repositories?q=org%3ANVIDIA+video+sdk` | ✅ HTTP 200 — only `NVIDIA/video-sdk-samples` (Video **Codec** SDK) and `NVIDIA/DeepStream`. **No RTX Video SDK repo in the NVIDIA org.** Confirms the SDK is a gated developer download, not open source. |
| `https://api.github.com/search/repositories?q=NVSDK+RTX+Video` | ✅ HTTP 200 — `total_count: 0` |
| `https://api.github.com/search/repositories?q=rtx+video+sdk+ngx` | ✅ HTTP 200 — `total_count: 0` |
| `https://api.github.com/search/code?q=NVVIDEO_SUPER_RESOLUTION` | ❌ HTTP 401 — GitHub code search requires auth |
| `https://github.com/NVIDIA/VideoProcessingFramework` | ✅ HTTP 200 — but this is the **Python VPF** (decode/encode/CSC bindings). **No VSR content.** |
| `https://github.com/NVIDIA/RTX-Video-Super-Resolution-SDK` | ❌ HTTP 404 — **this repo does not exist** |

### Search engines / other

| URL | Result |
|---|---|
| `https://cn.bing.com/search?q=...&ensearch=1` | ✅ HTTP 200 but **regionally filtered** (Chinese results, NVIDIA consumer pages only). Not usable for developer-API research. |
| `https://www.bing.com/search?q=...` | ❌ cross-origin redirect to `cn.bing.com` |
| `https://duckduckgo.com/html/?q=...` | ❌ `fetch failed` |
| `https://html.duckduckgo.com/html/?q=...` | ❌ `fetch failed` |
| `https://lite.duckduckgo.com/lite/?q=...` | ❌ `fetch failed` |
| `https://www.mojeek.com/search?q=...` | ❌ HTTP 403 — anti-automation block |
| `https://www.nvidia.com/en-us/software/nvidia-app/` | ✅ HTTP 200 — NVIDIA App marketing page; no RTX Video per-app override documentation |
| `https://web.archive.org/web/*/developer.nvidia.com/rtx-video-sdk` | ❌ unreachable — **Wayback Machine blocked** (archive.org resolves to non-public IP; `web.archive.org` fetch failed). This was the intended fallback for the dead NVIDIA pages. |

### Tooling note

`web_search` — ❌ **Non-functional**, confirmed on first attempt:
> `Error: DeepSeek returned no web_search_tool_result blocks; the request may not have triggered native web search`
> Endpoint `http://localhost:4000/v1/messages`. Configured via Settings > Plugins > Plugin configuration > Web search, or `DEEPSEEK_SEARCH_BASE_URL` / `web-search-deepseek.baseURL`. **Only the user should change this.**

All research was therefore done via `web_fetch` + the GitHub REST API (`api.github.com`), which proved to be the most effective discovery mechanism given search was unavailable.

---

## 9. Recommended next steps to close the gaps

1. **Retry the RTX Video FAQ** — `https://nvidia.custhelp.com/app/answers/detail/a_id/5448/~/rtx-video-super-resolution-faq` (was HTTP 403). Likely answers input-resolution, codec, and per-GPU questions.
2. **Log in to `developer.nvidia.com` and download the RTX Video SDK** to read the real `nvsdk_ngx_defs_vsr.h` / `_truehdr.h`. This would resolve items 1, 2, 7, 10 in §7.
3. **Retry the Wayback Machine** from an unrestricted network to recover the archived `developer.nvidia.com/rtx-video-sdk` landing page and version history.
4. **Confirm the "per-app override" claim** with a current NVIDIA App screenshot or release notes.
5. **Instrument a capture**: call `VideoProcessorGetStreamExtension` with the PPE GUID across GPUs/driver versions to empirically determine whether a VSR probe is possible.
