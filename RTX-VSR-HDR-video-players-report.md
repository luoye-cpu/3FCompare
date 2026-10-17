# RTX Video Super Resolution (VSR) & RTX Video HDR in Mainstream Video Players

**Technical research report** — source-level verification of how mainstream video players implement or enable NVIDIA RTX Video Super Resolution (VSR) and RTX Video HDR.

**Method note:** `web_search` was confirmed non-functional in this environment (it returned `Error: DeepSeek returned no web_search_tool_result blocks`). All findings below come from direct `web_fetch` against `raw.githubusercontent.com`, `api.github.com`, `chromium.googlesource.com`, and vendor URLs. Every source listed in the "Fetched URLs" section returned **HTTP 200** unless explicitly noted. Anything I could not confirm from a real fetched page is marked **UNVERIFIED**.

---

## 0. The underlying mechanism (read this first)

RTX VSR is **not** a separate API. It is a driver-side feature reachable only through the **D3D11 Video Processor** (`ID3D11VideoProcessor`) via a vendor-private extension call. The canonical invocation, which appears essentially verbatim in mpv, VLC, Kodi, MPC Video Renderer, and Chromium, is:

```c
const GUID kNvidiaPPEInterfaceGUID = {
    0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};

struct { UINT version; UINT method; UINT enable; }
    ext = { /*version*/ 0x1, /*method*/ 0x2, /*enable*/ 1 };

video_context->VideoProcessorSetStreamExtension(
    video_processor, 0, &kNvidiaPPEInterfaceGUID, sizeof(ext), &ext);
```

RTX Video HDR uses a sibling GUID and a different method number:

```c
const GUID kNvidiaTrueHDRInterfaceGUID = {
    0xfdd62bb4, 0x620b, 0x4fd7, {0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3}};
/* version 4, method 3, enable:1 */
```

Intel's equivalent is `GUID_INTEL_VPE_INTERFACE = {0xedd1d4b9, 0x8659, 0x4cbc, {0xa4,0xd6,0x98,0x31,0xa2,0x16,0x3a,0xc3}}` using function IDs `0x01` (version→`0x0003`), `0x20` (mode→`0x01` preproc), `0x37` (scaling→`0x2` SuperResolution).

**The decisive architectural consequence:** VSR only runs if the player hands its scaling work to `ID3D11VideoProcessorBlt`. A player that scales with its own pixel shaders (mpv's `vo_gpu`, VLC's default `linear` sampler, Kodi's HQ shader path) **bypasses the video processor entirely and VSR cannot engage**. Every verdict below follows from this single fact.

Note a critical caveat, stated verbatim in MPC Video Renderer's own changelog (see §4): *"The display of 'SuperResolution*' in the statistics only means that the request was sent successfully."* The extension call succeeding is **not** proof the driver actually applied VSR — the display must also be larger than the source, the NVIDIA Control Panel VSR setting must be on, and the driver/hardware must support it.

---

## 1. mpv (mpv-player/mpv) — **SUPPORTS**, via a dedicated filter

**Verdict: (b) supports it, but only through the explicit `d3d11vpp` video filter with `scaling-mode=nvidia`. It is NOT automatic and NOT part of the normal scaling path.**

This directly contradicts the widespread older claim that "RTX VSR does not work with mpv because mpv scales with its own shaders." That statement was true historically, but mpv has since gained a *deliberate, explicit* D3D11 Video Processor path that calls the NVIDIA extension.

### 1.1 The key file — `video/filter/vf_d3d11vpp.c`

This file contains explicit, first-class RTX VSR **and** RTX Video HDR support. Source: <https://raw.githubusercontent.com/mpv-player/mpv/master/video/filter/vf_d3d11vpp.c>

The scaling modes are enumerated up front:

```c
enum scaling_mode {
    SCALING_BASIC,
    SCALING_INTEL_VSR,
    SCALING_NVIDIA_RTX,
};
```

The exact NVIDIA extension call:

```c
static void enable_nvidia_rtx_extension(struct mp_filter *vf)
{
    struct priv *p = vf->priv;

    struct nvidia_ext {
        unsigned int version;
        unsigned int method;
        unsigned int enable;
    } ext = {1, 2, 1};

    HRESULT hr = ID3D11VideoContext_VideoProcessorSetStreamExtension(p->video_ctx,
                                                                     p->video_proc,
                                                                     0,
                                                                     &NVIDIA_PPE_INTERFACE_GUID,
                                                                     sizeof(ext),
                                                                     &ext);

    if (FAILED(hr)) {
        MP_WARN(vf, "Failed to enable NVIDIA RTX Super Resolution: %s\n", mp_HRESULT_to_str(hr));
    } else {
        MP_VERBOSE(vf, "NVIDIA RTX Super Resolution enabled.\n");
    }
}
```

Note `{1, 2, 1}` = version `0x1`, method `0x2` (**SuperResolution**), enable `1` — byte-identical semantics to every other implementation in this report.

And RTX Video HDR, which additionally *queries driver support first*:

```c
static bool supports_nvidia_true_hdr(struct mp_filter *vf)
{
    ...
    HRESULT hr = ID3D11VideoContext_VideoProcessorGetStreamExtension(p->video_ctx,
                                                                     p->video_proc,
                                                                     0,
                                                                     &NVIDIA_TRUE_HDR_INTERFACE_GUID,
                                                                     sizeof(supported),
                                                                     &supported);
    if (FAILED(hr) || !supported) {
        MP_WARN(vf, "NVIDIA RTX Video HDR not supported.\n");
        return false;
    }
    return true;
}

static bool enable_nvidia_true_hdr(struct mp_filter *vf)
{
    ...
    struct nvidia_ext {
        unsigned int version;
        unsigned int method;
        unsigned int enable : 1;
        unsigned int reserved : 31;
    } ext = {4, 3, 1};   /* version 4, method 3 = TrueHDR */
    ...
}
```

These are invoked from `recreate_video_proc()`:

```c
    switch (p->opts->scaling_mode) {
    case SCALING_INTEL_VSR:
        enable_intel_vsr_extension(vf);
        break;
    case SCALING_NVIDIA_RTX:
        enable_nvidia_rtx_extension(vf);
        break;
    }
```

The filter's option table exposes them to the user:

```c
static const m_option_t vf_opts_fields[] = {
    {"format", OPT_IMAGEFORMAT(format)},
    {"deint", OPT_BOOL(deint_enabled)},
    {"scale", OPT_FLOAT(scale)},
    {"scaling-mode", OPT_CHOICE(scaling_mode,
        {"standard", SCALING_BASIC},
        {"intel", SCALING_INTEL_VSR},
        {"nvidia", SCALING_NVIDIA_RTX})},
    {"interlaced-only", OPT_BOOL(interlaced_only)},
    {"mode", OPT_CHOICE_C(mode, d3d11vpp_processor_caps)},
    {"parity", OPT_CHOICE(field_parity, ...)},
    {"nvidia-true-hdr", OPT_BOOL(nvidia_true_hdr)},
    {0}
};
```

The filter registers itself as the user-visible name **`d3d11vpp`** ("D3D11 Video Post-Process Filter").

### 1.2 Documented options — `DOCS/man/vf.rst`

Source: <https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/vf.rst>

The manual documents `d3d11vpp` and its `scaling-mode` / `nvidia-true-hdr` options verbatim:

> ```
> ``scaling-mode=<standard,intel,nvidia>``
>     Select the scaling mode to be used. Note that this only enables the
>     appropriate processing extensions; whether it actually works or not
>     depends on your hardware and the settings in your GPU driver's control
>     panel (default: standard).
>     ...
>     nvidia
>         NVIDIA RTX Super Resolution.
> ```

> ```
> ``nvidia-true-hdr``
>     Enable NVIDIA RTX Video HDR processing. This only converts SDR to HDR,
>     so it is ignored when the source is already HDR.
> ```

Note the manual's own hedge — *"this only enables the appropriate processing extensions; whether it actually works or not depends on ... your GPU driver's control panel"* — consistent with the MPCVR caveat in §0.

Also documented: `scale` (scaling factor, default 1.0) and `format` (e.g. `nv12`, `p010`). Since VSR only engages on *upscale*, a usable invocation forces the scale factor above 1.0:

```
mpv --hwdec=d3d11va --vf=d3d11vpp=scaling-mode=nvidia:scale=2.0 file.mkv
```

### 1.3 What `vo=gpu` / `gpu-next` / the d3d11 context actually do

**They do NOT use `ID3D11VideoProcessor` at all, and therefore can never trigger VSR.**

`video/out/d3d11/context.c` (source: <https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/d3d11/context.c>) is purely a swapchain/presentation layer. It creates a D3D11 device, an `IDXGISwapChain`, and an RA (rendering abstraction) via `ra_d3d11_create()`. There is **no** `ID3D11VideoDevice`, `ID3D11VideoProcessor`, or `VideoProcessorBlt` anywhere in it. Its options are `d3d11-warp`, `d3d11-feature-level`, `d3d11-flip`, `d3d11-sync-interval`, `d3d11-adapter`, `d3d11-output-format`, `d3d11-output-csp`, `d3d11-exclusive-fs`, `d3d11-output-mode`.

`video/out/gpu/hwdec.c` (source: <https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/gpu/hwdec.c>) is a hwdec **interop registry**. It lists `ra_hwdec_d3d11va` among `ra_hwdec_drivers[]`, and provides the `ra_hwdec_ctx_load_fmt()` lookup used by `vf_d3d11vpp`. No video processor.

`vo_gpu_next.c` (source: <https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/vo_gpu_next.c>) hands frames to **libplacebo** (`pl_render_image(p->rr, &image, &target, &params)`). libplacebo scales with its own GPU shaders. No video processor, no VSR.

`--d3d11-adapter` (in `context.c`, validated by `mp_dxgi_validate_adapter`) **is** relevant in one indirect way: it selects which physical GPU mpv creates its device on, and therefore which GPU's driver gets asked for the VSR extension. On a hybrid laptop, picking the iGPU vs. the RTX dGPU changes whether VSR is reachable at all.

### 1.4 `vf_d3d11vpp` architectural details worth reporting

- It does **not** need `--hwdec`. In `vf_d3d11vpp_create()` it requests a D3D11 hwdec device itself via `hwdec_devices_request_for_img_fmt(info->hwdec_devs, &params)` with `imgfmt = IMGFMT_D3D11`. Software frames are uploaded to hardware for processing (as the manual states).
- It builds its own `ID3D11VideoProcessorEnumerator` / `ID3D11VideoProcessor` from the VO-provided device (`d3dctx->device`), via `QueryInterface(IID_ID3D11VideoDevice)` and `QueryInterface(IID_ID3D11VideoContext)`.
- Output goes through `ID3D11VideoContext_VideoProcessorBlt(...)` into a `D3D11_VPOV_DIMENSION_TEXTURE2D` output view — i.e. it *is* a real video-processor path.
- `p->require_filtering` decides whether the processor is used at all: `!mp_image_params_static_equal(&p->params, &p->out_params) || want_nvidia_true_hdr(vf)`. If no scaling and no HDR conversion is requested, the filter short-circuits and just passes the frame through — so VSR will not engage unless `scale` (or a format change) is set.
- `--d3d11va-zero-copy` belongs to the hwdec interop (`video/out/d3d11/hwdec_d3d11va.c`, option `d3d11va-zero-copy`), not to the VPP filter. `hwdec_d3d11va.c` contains **no** video processor code — it only maps decoded textures to RA textures via `CopySubresourceRegion1` (1-copy path) or direct wrapping (zero-copy path).
- `--vd-lavc-dr` (direct rendering) is documented as being silently disabled by *"video filters of any kind that write to the image data (or output newly allocated frames)"* — which is exactly what `d3d11vpp` does. So expect `d3d11vpp` and DR to be mutually exclusive in practice.

### 1.5 Git history confirms this is recent, deliberate work

Source: <https://api.github.com/repos/mpv-player/mpv/commits?path=video/filter/vf_d3d11vpp.c&per_page=100>

The path-scoped commit list shows sustained, recent development by Kacper Michajłow (kasper93), including:

| Date | Commit subject |
|---|---|
| 2026-06-28 | `vf_d3d11vpp: enable NVIDIA RTX Video only for SDR input` — *"This filter only converts SDR to HDR, so disable it if we have already HDR on input... Fixes: #17800"* |
| 2026-04-19 | `vf_d3d11vpp: fix print index value` |
| 2026-03-10 | `vf_d3d11vpp: allow more future/past frames` — *"Apparently some video processors request 15-20 future frames. Fixes: ... #17413"* |
| 2026-02-19 | `vf_d3d11vpp: set output format for NVIDIA RTX Video HDR` — *"It outputs RGB tone mapped image, set params accordingly. Fixes: #17265"* |
| 2026-02-19 | `vf_d3d11vpp: use ID3D11VideoContext1 interface for setting color space` |
| 2026-02-18 | `vf_d3d11vpp: use half-rate for IVTC mode` |
| 2024-08-10 | (Philip Langdale) `video: hwdec: extend look up of hwdecs to consider device type as well` |
| 2024-01-22 | `csputils: replace mp_colorspace with pl_color_space` |

The 2026-06-28 message also documents a real behavioural limit worth quoting:

> *"This filter only converts SDR to HDR, so disable it if we have already HDR on input. Note that it also seems to be disabled if HDR is disabled in Windows, but we don't check that, because filters are not related to display."*

The 2024-02-21 commit `video/filter: add field order support for built in deinterlacers` establishes the filter's deinterlacing lineage. `vf_d3d11vpp` therefore serves **two** purposes: deinterlacing (its original reason for existing) and now also VSR/VHDR.

### 1.6 Release-note status — **UNVERIFIED**

I fetched <https://api.github.com/repos/mpv-player/mpv/releases?per_page=20> (HTTP 200). The release bodies I received covered `git-release` (v0.41.0-dev, 2026-06-04), v0.38.0, v0.37.0, and v0.36.0. I found **no** mention of "Super Resolution", "RTX VSR", or `d3d11vpp` in those release notes. The response was truncated by the fetch tool, so I cannot rule out that the feature is listed in a release body I did not receive. **The release in which `d3d11vpp`'s NVIDIA VSR gained support is therefore UNVERIFIED** — the source and manual are authoritative, the changelog claim is not.

---

## 2. VLC (videolan/vlc) — **SUPPORTS**, via a dedicated D3D11 scaler with a user option

**Verdict: (b) supports it, gated behind the `d3d11-upscale-mode=super` option (`--d3d11-upscale-mode=super`). VLC has the most complete vendor coverage of any player reviewed.**

### 2.1 The key file — `modules/video_output/win32/d3d11_scaler.cpp`

Source: <https://raw.githubusercontent.com/videolan/vlc/master/modules/video_output/win32/d3d11_scaler.cpp>

The file header states its purpose outright: *"d3d11_scaler: Direct3D11 VideoProcessor based output scaling"*.

The `d3d11_scaler` struct carries a `super_res` flag:

```c
struct d3d11_scaler
{
    bool                            usable = false;
    const d3d_format_t              *d3d_fmt = nullptr;
    vout_display_place_t            place = {};
    bool                            super_res = false;
    bool                            upscaling = false;
    ...
    ComPtr<ID3D11VideoDevice>               d3dviddev;
    ComPtr<ID3D11VideoContext>              d3dvidctx;
    ComPtr<ID3D11VideoProcessorEnumerator>  enumerator;
    ComPtr<ID3D11VideoProcessor>            processor;
    ComPtr<ID3D11VideoProcessorOutputView>  outputView;
    ...
};
```

**Capability probing at creation time** — VLC explicitly checks the NVIDIA driver version and then *queries* the extension before committing:

```c
d3d11_scaler *D3D11_UpscalerCreate(vlc_object_t *vd, d3d11_device_t *d3d_dev, vlc_fourcc_t i_chroma,
                                   bool super_res, const d3d_format_t **out_fmt)
{
    ...
    if (super_res)
    {
    // NVIDIA 530+ driver
    if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_NVIDIA &&
        (d3d_dev->WDDM.revision * 10000 + d3d_dev->WDDM.build) > 153000)
    {
        constexpr GUID kNvidiaPPEInterfaceGUID{ 0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7} };
        ...
        hr = d3dvidctx->VideoProcessorGetStreamExtension(processor.Get(),
                    0, &kNvidiaPPEInterfaceGUID, sizeof(available), &available);
checked:
        d3d11_device_unlock(d3d_dev);
        canProcess = available != 0;
    }
    else if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_INTEL)
    {
        canProcess = true; // detection doesn't work
    }
    else if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_MTGPU)
    {
        canProcess = true; // detection doesn't work
    }
#ifdef HAVE_AMF_SCALER
    else if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_AMD)
    {
        ... AMFHQScaler, AMF_HQ_SCALER_ALGORITHM_VIDEOSR1_0 ...
    }
#endif
    }
```

Note the **WDDM version gate** — `revision * 10000 + build > 153000` corresponds to NVIDIA driver 530+, matching the comment. Failure produces a user-visible error: `msg_Err(vd, "Super Resolution filter not supported");`

**The actual extension call**, and crucially the `upscale` decision that gates it:

```c
    if (scaleProc->super_res)
    {
        // only use super resolution when source is smaller than display
        upscale = fmt->i_visible_width < video_place->width
               || fmt->i_visible_height < video_place->height;

        if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_NVIDIA)
        {
            constexpr GUID kNvidiaPPEInterfaceGUID{ 0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7} };
            constexpr UINT kStreamExtensionVersionV1 = 0x1;
            constexpr UINT kStreamExtensionMethodRTXVSR = 0x2;
            struct {
                UINT version;
                UINT method;
                UINT enable;
            } stream_extension_info = {
                kStreamExtensionVersionV1,
                kStreamExtensionMethodRTXVSR,
                upscale ? 1u : 0u,
            };

            hr = scaleProc->d3dvidctx->VideoProcessorSetStreamExtension(
                scaleProc->processor.Get(),
                0, &kNvidiaPPEInterfaceGUID, sizeof(stream_extension_info),
                &stream_extension_info);

            if (FAILED(hr)) {
                msg_Err(vd, "Failed to set the NVIDIA video process stream extension. (hr=0x%lX)", hr);
                ...
            }
        }
        else if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_INTEL)
        {
            /* kIntelVpeFnVersion=0x01 -> 0x0003, kIntelVpeFnMode=0x20 -> preproc/none,
               kIntelVpeFnScaling=0x37 -> 0x2 SuperResolution / 0x0 default */
        }
        else if (d3d_dev->adapterDesc.VendorId == GPU_MANUFACTURER_MTGPU)
        {
            constexpr GUID kMtvsrGUID{ 0x28D65B12, 0x957F, 0x4C0C, {0xA9, 0x34, 0x3B, 0x5D, 0xBE, 0xB4, 0x31, 0x0F} };
            constexpr UINT kMtvsrVersion = 0x1;
            constexpr UINT kMtvsrMethod  = 0x1;
            constexpr UINT kMtvsrLevel   = 1;
            ... /* Mthreads (Moore Threads) VSR */
        }
    }
    ...
    if (scaleProc->upscaling != upscale)
    {
        msg_Dbg(vd, "turning VSR %s", upscale ? "ON" : "OFF");
        scaleProc->upscaling = upscale;
    }
```

VLC is the only player in this report that also implements **Moore Threads (Mthreads) VSR** via `kMtvsrGUID`.

The scaling itself is a genuine video-processor blit:

```c
    D3D11_VIDEO_PROCESSOR_STREAM stream{};
    stream.Enable = TRUE;
    stream.pInputSurface = p_sys->processorInput;

    hr = scaleProc->d3dvidctx->VideoProcessorBlt(scaleProc->processor.Get(),
                                                 scaleProc->outputView.Get(),
                                                 0, 1, &stream);
```

### 2.2 The user-facing option — `modules/video_output/win32/direct3d11.cpp`

Source: <https://raw.githubusercontent.com/videolan/vlc/master/modules/video_output/win32/direct3d11.cpp>

The module registers a four-way choice:

```c
#define UPSCALE_MODE_TEXT N_("Video Upscaling Mode")
#define UPSCALE_MODE_LONGTEXT N_("Select the upscaling mode for video.")

static const char *const ppsz_upscale_mode[] = {
    "linear", "point", "processor", "super" };
static const char *const ppsz_upscale_mode_text[] = {
    N_("Linear Sampler"), N_("Point Sampler"), N_("Video Processor"), N_("Super Resolution") };
...
    add_string("d3d11-upscale-mode", "linear", UPSCALE_MODE_TEXT, UPSCALE_MODE_LONGTEXT)
        change_string_list(ppsz_upscale_mode, ppsz_upscale_mode_text)

    add_string("d3d11-hdr-mode", "auto", HDR_MODE_TEXT, HDR_MODE_LONGTEXT)
        change_string_list(ppsz_hdr_mode, ppsz_hdr_mode_text)
```

So the answer to the report's question — *"Does VLC's scaling go through the video processor or through its own shaders?"* — is **both, and it is user-selectable**:

| `--d3d11-upscale-mode` | Scaling path | VSR possible? |
|---|---|---|
| `linear` (**default**) | Own pixel shader / linear sampler | **No** |
| `point` | Own pixel shader / point sampler | **No** |
| `processor` | `ID3D11VideoProcessor` (generic) | **No** (no extension call) |
| `super` | `ID3D11VideoProcessor` + vendor SR extension | **Yes** |

The enum and the call site confirm the wiring:

```c
enum d3d11_upscale
{
    upscale_LinearSampler,
    upscale_PointSampler,
    upscale_VideoProcessor,
    upscale_SuperResolution,
};
...
    if (sys->upscaleMode == upscale_VideoProcessor || sys->upscaleMode == upscale_SuperResolution)
    {
        D3D11_UpscalerUpdate(VLC_OBJECT(vd), sys->scaleProc, sys->d3d_dev,
                             vd->source, &sys->picQuad.quad_fmt,
                             vd->cfg->display.width, vd->cfg->display.height,
                             vd->place);
        if (D3D11_UpscalerUsed(sys->scaleProc))
        {
            D3D11_UpscalerGetSize(sys->scaleProc, &sys->picQuad.quad_fmt.i_width, ...);
            ...
        }
    }
```

**Important nuance:** the *separate* `old_feature` video processor in the same file (`SetupInternalConverter()`, creating `sys->old_feature.processor`) exists only as a **chroma/format converter** for the D3D11 < 11.1 path, and the docs confirm `ID3D11VideoProcessor` was already used for image conversion. **That path calls no vendor extension and does not trigger VSR** — only `d3d11_scaler.cpp`'s `super_res` path does.

VLC also has `--d3d11-hdr-mode` with values `auto`/`never`/`always`/`generate`, but note this is **not** NVIDIA RTX Video HDR — the `generate` case uses VLC's own `D3D11_TonemapperCreate()` (`d3d11_tonemap.cpp`), a shader-based SDR→HDR converter. **I found no NVIDIA TrueHDR GUID in any VLC file I fetched**; VLC's RTX Video **HDR** support is **UNVERIFIED / apparently absent**, unlike its RTX VSR support which is explicit.

### 2.3 VLC issue tracker — **UNVERIFIED**

I attempted `https://code.videolan.org/videolan/vlc/-/issues?search=super+resolution` via web_fetch and the request failed (`TypeError: fetch failed`). I did not obtain VLC forum/trac evidence. The code evidence above stands on its own; the community-discussion angle is unverified.

---

## 3. Windows "Movies & TV" / Media Foundation, Microsoft Edge & Google Chrome — **SUPPORTS**

**Verdict: (a) Chrome and Edge support RTX VSR natively.** This is where VSR launched first (NVIDIA announcement, Feb 2023). Chromium does it in the DirectComposition overlay/presenter layer, **not** in the media decoder.

### 3.1 The core implementation — `ui/gl/swap_chain_presenter.cc`

Source: <https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/swap_chain_presenter.cc>

Chromium defines **both** vendor GUIDs and toggles both Intel and NVIDIA SR:

```cpp
HRESULT ToggleNvidiaVpSuperResolution(ID3D11VideoContext* video_context,
                                      ID3D11VideoProcessor* video_processor,
                                      bool enable) {
  TRACE_EVENT1("gpu", "ToggleNvidiaVpSuperResolution", "on", enable);

  constexpr GUID kNvidiaPPEInterfaceGUID = {
      0xd43ce1b3,
      0x1f4b,
      0x48ac,
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

Dispatch is by PCI vendor ID (`0x10de` = NVIDIA, `0x8086` = Intel):

```cpp
HRESULT ToggleVpSuperResolution(UINT gpu_vendor_id,
                                ID3D11VideoContext* video_context,
                                ID3D11VideoProcessor* video_processor,
                                bool enable) {
  if (gpu_vendor_id == 0x8086 &&
      base::FeatureList::IsEnabled(features::kIntelVpSuperResolution)) {
    return ToggleIntelVpSuperResolution(video_context, video_processor, enable);
  }

  if (gpu_vendor_id == 0x10de) {
    return ToggleNvidiaVpSuperResolution(video_context, video_processor,
                                         enable);
  }

  return E_NOTIMPL;
}
```

Note the **asymmetry**: NVIDIA SR is enabled unconditionally for `0x10de`, whereas **Intel SR requires the `kIntelVpSuperResolution` feature flag**, which is `FEATURE_DISABLED_BY_DEFAULT` (see §3.3).

### 3.2 Enabling, battery gating, and failure recovery

The call site inside `SwapChainPresenter::VideoProcessorBlt()`:

```cpp
    bool use_vp_super_resolution =
        enable_vp_super_resolution_ && !is_on_battery_power_;
    if (enable_vp_super_resolution_) {
      hr = ToggleVpSuperResolution(gpu_vendor_id_, video_context.Get(),
                                   video_processor.Get(),
                                   use_vp_super_resolution);
      if (FAILED(hr)) {
        enable_vp_super_resolution_ = false;
        use_vp_super_resolution = false;
      }
    }

    {
      TRACE_EVENT0("gpu", "ID3D11VideoContext::VideoProcessorBlt");
      hr = video_context->VideoProcessorBlt(video_processor.Get(),
                                            output_view_.Get(), 0, 1, &stream);
    }

    // Retry VideoProcessorBlt with VpSuperResolution off if it was on.
    if (FAILED(hr) && use_vp_super_resolution) {
      DLOG(ERROR) << "Retry VideoProcessorBlt with VpSuperResolution off "
                     "after it failed with: "
                  << logging::SystemErrorCodeToString(hr);

      ToggleVpSuperResolution(gpu_vendor_id_, video_context.Get(),
                              video_processor.Get(), false);
      ...
      // We shouldn't use VpSuperResolution if it was the reason that caused
      // the VideoProcessorBlt failure.
      if (SUCCEEDED(hr)) {
        enable_vp_super_resolution_ = false;
      }
    }
```

Three engineering details worth reporting:

1. **Battery gating.** `!is_on_battery_power_` — Chromium deliberately disables VSR on battery (`OnBatteryPowerStatusChange()` maintains the flag). VSR is expensive; this is a power-management decision no other player in this report makes.
2. **Graceful degradation.** If `VideoProcessorBlt` fails with SR enabled, Chromium retries with SR off and permanently latches `enable_vp_super_resolution_ = false`. A driver that advertises the extension but mishandles it cannot wedge playback.
3. **The flag is only set after a processor is (re)created**, inside `ReallocateSwapChain()`:

```cpp
  enable_vp_auto_hdr_ =
      !layer_tree_->disable_vp_auto_hdr() && IsVpAutoHDREnabled(gpu_vendor_id_);
  enable_vp_super_resolution_ = !layer_tree_->disable_vp_super_resolution();
```

Also present: `HRESULT ToggleVpAutoHDR(...)` implementing **NVIDIA RTX Video HDR** (`kNvidiaTrueHDRInterfaceGUID = {0xfdd62bb4, 0x620b, 0x4fd7, {0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3}}`, version `0x4`, method `0x3`), gated on `NvidiaDriverSupportsTrueHDR()` which issues a `VideoProcessorGetStreamExtension` probe first. Chromium therefore supports **both** RTX VSR and RTX Video HDR, with the same retry/latch discipline.

### 3.3 Feature flags and the `--enable-features=` names

**NVIDIA VSR needs no flag** — `ToggleVpSuperResolution` gates only Intel on a feature. The flags are:

| Feature name | Definition | Default |
|---|---|---|
| `kIntelVpSuperResolution` | `ui/gl/gl_switches.cc` | `FEATURE_DISABLED_BY_DEFAULT` |
| `kDisableVPBLTUpscale` | `ui/gl/swap_chain_presenter.cc` | `FEATURE_DISABLED_BY_DEFAULT` |
| `kLimitMFSwapChainSize` | `ui/gl/swap_chain_presenter.cc` | `FEATURE_ENABLED_BY_DEFAULT` |

From <https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/gl_switches.cc>:

```cpp
// Allow overlay swapchain to use Intel video processor for super resolution.
BASE_FEATURE(kIntelVpSuperResolution, base::FEATURE_DISABLED_BY_DEFAULT);
```

So on **Intel** GPUs the correct command-line is:
```
--enable-features=IntelVpSuperResolution
```
On **NVIDIA** GPUs no flag is required — Chromium enables it automatically (subject to battery state).

**Warning about the `--enable-features=HardwareVideoSuperResolution` name:** I found **no such flag** in Chromium source. `media/base/media_switches.cc` and `media/base/media_switches.h` (both fetched, HTTP 200) contain **no** super-resolution feature at all. I therefore mark `HardwareVideoSuperResolution` as **UNVERIFIED / probably incorrect** — it does not appear in the files where media feature flags live. The controlling flags are the `ui/gl` ones above.

Two further relevant observations from `swap_chain_presenter.cc`:

- `kDisableVPBLTUpscale` is explicitly commented as interacting with DWM scaling: *"This lets DWM scale up the video (via the visual transform) rather than allocating a swap chain at the target size and letting VP BLT do the scaling."* If VP BLT is bypassed, **VSR cannot run** — so this flag, when enabled, would suppress VSR.
- The `ShouldUseVideoProcessorScaling()` helper returns `(!is_on_battery_power_ && !layer_tree_->disable_vp_scaling())`, another battery gate.

### 3.4 How the pieces connect — `ui/gl/dc_layer_tree.cc`

Source: <https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/dc_layer_tree.cc>

`DCLayerTree` owns the disable switches and is constructed with them:

```cpp
DCLayerTree::DCLayerTree(bool disable_nv12_dynamic_textures,
                         bool disable_vp_auto_hdr,
                         bool disable_vp_scaling,
                         bool disable_vp_super_resolution,
                         bool disable_dc_letterbox_video_optimization,
                         bool force_dcomp_triple_buffer_video_swap_chain,
                         bool no_downscaled_overlay_promotion)
```

`DCLayerTree::InitializeVideoProcessor()` creates the enumerator and processor that `SwapChainPresenter` then drives:

```cpp
  HRESULT hr =
      video_processor_wrapper.video_device->CreateVideoProcessorEnumerator(
          &desc, &video_processor_wrapper.video_processor_enumerator);
  ...
  hr = video_processor_wrapper.video_device->CreateVideoProcessor(
      video_processor_wrapper.video_processor_enumerator.Get(), 0,
      &video_processor_wrapper.video_processor);
  ...
  // Auto stream processing (the default) can hurt power consumption.
  video_processor_wrapper.video_context
      ->VideoProcessorSetStreamAutoProcessingMode(
          video_processor_wrapper.video_processor.Get(), 0, FALSE);
```

Crucially, `DCLayerTree` is initialized in the **GPU process** with a device obtained from ANGLE's D3D11 backend — from `gpu/ipc/service/gpu_init.cc` (source: <https://raw.githubusercontent.com/chromium/chromium/main/gpu/ipc/service/gpu_init.cc>):

```cpp
#if BUILDFLAG(IS_WIN)
  {
    Microsoft::WRL::ComPtr<ID3D11Device> d3d11_device;
    ...
    } else {
      d3d11_device = gl::QueryD3D11DeviceObjectFromANGLE();
    }
    ...
    gl::InitializeDirectComposition(std::move(d3d11_device),
                                    std::move(d3d12_command_queue),
                                    std::move(solid_color_factory));
  }
#endif  // BUILDFLAG(IS_WIN)
```

This is the architectural answer to *"where does Chromium's VSR live?"* — **not** in `media/`, but in the **DirectComposition video-overlay presentation path** in `ui/gl/`. It is the compositor that owns the video processor, which is why it works for `<video>` regardless of how the frames were decoded.

### 3.5 `media/gpu/windows/d3d11_video_processor_proxy.cc` — a decoy

Source: <https://raw.githubusercontent.com/chromium/chromium/main/media/gpu/windows/d3d11_video_processor_proxy.cc>

This file **does** create a video processor and call `VideoProcessorBlt`:

```cpp
D3DStatus VideoProcessorProxy::Init(uint32_t width, uint32_t height) {
  ...
  HRESULT hr = video_device_->CreateVideoProcessorEnumerator(&desc, &processor_enumerator_);
  ...
  hr = video_device_->CreateVideoProcessor(processor_enumerator_.Get(), 0, &video_processor_);
  ...
  video_context_->VideoProcessorSetStreamAutoProcessingMode(
      video_processor_.Get(), 0, FALSE);
  return D3DStatus::Codes::kOk;
}
```

**But it contains no vendor extension call and no super-resolution string.** It is the media-pipeline's format-conversion/scaling proxy, not the VSR path. Anyone grepping Chromium for `ID3D11VideoProcessor` and stopping here would reach the wrong conclusion. VSR lives in `ui/gl/swap_chain_presenter.cc`.

### 3.6 Microsoft Edge

Edge is Chromium-based and inherits this implementation. `media/gpu/windows/d3d11_video_decoder.cc` returned **HTTP 404** (the file has been renamed/removed); `media/gpu/windows/d3d11_video_decoder_wrapper.cc` (HTTP 200) contains only decoder-buffer plumbing and `D3D11VideoContext::DecoderBeginFrame`/`SubmitDecoderBuffers` — **no** video processor and no SR. This reinforces that the decoder is not where VSR happens.

NVIDIA's own Edge/Chrome documentation (`https://nvidia.custhelp.com/app/answers/detail/a_id/5448`) returned **HTTP 403** with an Oracle "Technical Difficulties" page — **not usable**, so NVIDIA's official supported-app list is **UNVERIFIED** here.

---

## 4. MPC-HC / MPC-BE — **SUPPORTS**, but the code lives in MPC Video Renderer

**Verdict: (b) supports it, conditional on using the *MPC Video Renderer* (MPCVR) as the video renderer, and on setting "Request Super Resolution".** Critically, **neither** MPC-HC's nor MPC-BE's own in-tree renderers implement VSR — the feature is in a separate DirectShow filter that both players host.

### 4.1 The architectural correction

My initial hypothesis was that VSR would be found in `clsid2/mpc-hc`'s `VideoRenderer.cpp` or an `EVRCustomPresenter`. That is wrong on both counts:

- `https://raw.githubusercontent.com/clsid2/mpc-hc/develop/src/mpc-hc/VideoRenderer.cpp` → **fetch failed / 404**. `VideoRenderer.h` → **HTTP 404**.
- MPC-HC's own `src/mpc-hc/` directory listing (HTTP 200, via `api.github.com/repos/clsid2/mpc-hc/contents/src/mpc-hc`) contains no `VideoRenderer.*` and no `EVRCustomPresenter.*` — it is the player application layer (`MainFrm`, `AppSettings`, theme and dialog code).

The VSR implementation is in **MPC Video Renderer**, GPLv3, whose sources are mirrored at <https://github.com/emoose/VideoRenderer> (the canonical MPC-BE-adjacent MPCVR repository). Its `Source/` tree (retrieved via `api.github.com/repos/emoose/VideoRenderer/git/trees/master?recursive=1`, HTTP 200) includes `Source/D3D11VP.cpp` (30,709 bytes), `Source/D3D11VP.h`, `Source/DX11VideoProcessor.cpp` (127,605 bytes), `Source/DX9VideoProcessor.cpp`, `Source/DXVA2VP.cpp`, `Source/VideoProcessor.cpp`, and `Source/PropPage.cpp`.

### 4.2 `Source/D3D11VP.cpp` — NVIDIA and Intel SR

Source: <https://raw.githubusercontent.com/emoose/VideoRenderer/master/Source/D3D11VP.cpp>

```cpp
HRESULT CD3D11VP::SetSuperResNvidia(const bool enable)
{
	constexpr GUID kNvidiaPPEInterfaceGUID = {
		0xd43ce1b3,
		0x1f4b,
		0x48ac,
		{0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}
	};
	constexpr UINT kStreamExtensionVersionV1 = 0x1;
	constexpr UINT kStreamExtensionMethodSuperResolution = 0x2;

	struct {
		UINT version;
		UINT method;
		UINT enable;
	}
	stream_extension_info = {
		kStreamExtensionVersionV1,
		kStreamExtensionMethodSuperResolution,
		enable ? 1 : 0
	};

	HRESULT hr = m_pVideoContext->VideoProcessorSetStreamExtension(
		m_pVideoProcessor, 0, &kNvidiaPPEInterfaceGUID,
		sizeof(stream_extension_info), &stream_extension_info);

	if (hr == S_OK && !enable) {
		hr = S_FALSE;
	}

	return hr;
}
```

`SetSuperResIntel()` implements the three-call Intel VPE sequence. Both are dispatched from a single entry point that also encodes the **resolution thresholds**:

```cpp
HRESULT CD3D11VP::SetSuperRes(const int iSuperRes)
{
	if (!m_pVideoContext) {
		return E_ABORT;
	}

	bool enable = false;

	switch (iSuperRes) {
	case SUPERRES_SD:
		if (m_srcWidth <= 1024 && m_srcHeight <= 576) {
			enable = true;
		}
		break;
	case SUPERRES_HD:
		if (m_srcWidth <= 1280 && m_srcHeight <= 720) {
			enable = true;
		}
		break;
	case SUPERRES_FHD:
		if (m_srcWidth <= 2048 && m_srcHeight <= 1088) {
			enable = true;
		}
		break;
	}

	if (m_VendorId == PCIV_NVIDIA) {
		return SetSuperResNvidia(enable);
	}
	else if (m_VendorId == PCIV_INTEL) {
		return SetSuperResIntel(enable);
	}

	return E_NOT_SET;
}
```

This is the source of the confusing **"Request Super Resolution: <= SD / <= HD / <= FHD"** dropdown users see in MPCVR's properties. It is a *source resolution ceiling*, matching NVIDIA's documented "VSR applies to video below 1440p" behaviour.

`Source/D3D11VP.h` declares the interface:

```cpp
	HRESULT SetSuperResNvidia(const bool enable);
	HRESULT SetSuperResIntel(const bool enable);
public:
	HRESULT SetSuperRes(const int iSuperRes);
```

And `Source/DX11VideoProcessor.cpp` (base class `CVideoProcessor`) wires it in. Relevant lines located via content search:

```cpp
	m_iVPSuperRes          = config.iVPSuperRes;          // constructor
	...
	m_bVPUseSuperRes = (m_D3D11VP.SetSuperRes(m_bVPScaling ? m_iVPSuperRes : 0) == S_OK);
```

The second line is important: **SR is only requested if `m_bVPScaling` is on** — i.e. if the *"Use D3D11 video processor for scaling"* setting is enabled. If the user disables VP scaling, VSR is silently not requested, because MPCVR then does its own shader scaling (`ResizeShaderPass`, `m_pShaderUpscaleX/Y` — Lanczos/Mitchell/Catmull-Rom/Jinc2, enumerated in `s_Upscaling11ResIDs`). **This is the same shader-vs-video-processor fork as VLC.**

The `CD3D11VP::Process()` method performs the actual blit:

```cpp
	hr = m_pVideoContext->VideoProcessorBlt(m_pVideoProcessor, pOutputView, StreamData.InputFrameOrField, 1, &StreamData);
```

### 4.3 Chronology and the capability caveat — `history.txt`

Source: <https://raw.githubusercontent.com/emoose/VideoRenderer/master/history.txt>

The authoritative changelog entry:

> **0.6.7.2013 - 2023-03-29**
> Added "Request Super Resolution" option for Nvidia and Intel graphics adapters. Hardware and driver support required. **The display of "SuperResolution*" in the statistics only means that the request was sent successfully.**
> Recommended version of MPC-BE 1.6.6 or later.

This dates MPCVR's VSR support to **2023-03-29**, one month after NVIDIA's Feb 2023 launch. Note the explicit disclaimer — MPCVR's own on-screen "SuperResolution" indicator is *not* proof of effective upscaling.

A later entry shows refinement:

> **0.7.2 dev**
> Added the ability to request Super Resolution only for low resolution videos.

This corresponds exactly to the `SUPERRES_SD/HD/FHD` thresholds in `SetSuperRes()` above.

### 4.4 Corroborating user/issue evidence

GitHub issue-search API results (all HTTP 200) confirm real-world usage and the conditional nature of the feature:

- `clsid2/mpc-hc` issue **#4214**, *"NVIDIA Video Super Resolution is not working properly with MPC-HC 2.8.2"* — user reports VSR inactive in the NVIDIA Control Panel, and *"the option is grayed out if I set the Texture format to 16-bit Floating Point in MPCVR."* This documents a real **format constraint**: **16-bit float textures break the VSR path**.
- `clsid2/mpc-hc` issue **#3684**, *"Nvidia VSR doesnt work anymore"* — *"the video indeed say d3d11 super resolution is active but from nvidia app and control panel it say inactive"*; the reporter notes *"i tried using pot player and the super resolution indicator show up after i set it up."* This is a concrete instance of the §4.3 caveat: **MPCVR's indicator said "active" while the driver said otherwise.**
- `Aleksoid1978/MPC-BE` issue **#674**, *"Nvidia Super Resolution will not work in some cases"* — user must *"modify the Request Super Resolution option in the MPC renderer and reapply it"* to make SR actually engage.
- `Aleksoid1978/MPC-BE` issue **#678**, *"Nvidia Video Super Resolution not triggering without toggling off and on settings"* — same toggle-to-activate workaround, plus the observation that *"This behavior does not occur in PotPlayer, where Nvidia Super Resolution activates as expected."*
- `Aleksoid1978/MPC-BE` issue **#521**, *"RTX Super Resolution only appears as 'active' in stats but not NCP"* — *"Can't be reproduced with MPC-HC using the same MPC Video Renderer,"* indicating player-dependent behaviour.
- `clsid2/mpc-hc` issue **#2046** (2023-02-28) and **#2094** / **#2390** are the original feature *requests* — closed. #2094's text explicitly credits VLC: *"VLC in its beta 3.0.19 incorporates this technology, is it possible to implement this technology in a future version?"* — independent confirmation of §2.

**Explicit RTX VSR mentions: yes**, extensively, in both the MPCVR source and both players' issue trackers. **`DXVA2_VIDEOPROCESSOR` usage: yes** — note MPCVR also ships `Source/DXVA2VP.cpp` and `Source/DX9VideoProcessor.cpp` (DX9 `IDirect3DDevice9Video` path) for legacy mode, but the NVIDIA/Intel SR extensions are implemented **only** in the D3D11 path (`D3D11VP.cpp`). SR is not available in MPCVR's DX9 mode.

**On madVR and EVRCustomPresenter:** I found **no** evidence that madVR implements the NVIDIA PPE extension, and the user reports above discuss MPCVR, not madVR. I mark **madVR RTX VSR support as UNVERIFIED** — I could not fetch madVR sources (they are not public). `EVRCustomPresenter` is a Microsoft/DirectShow component; my searches surfaced no VSR implementation there, and it is **UNVERIFIED** as a VSR path.

---

## 5. PotPlayer — **SUPPORTS** (via MPC Video Renderer)

**Verdict: (b) supports it, as a host application for MPC Video Renderer.** PotPlayer's own built-in renderer status remains **UNVERIFIED**, but its MPCVR-hosted path is well evidenced.

Evidence, from primary user testimony in the fetched issue trackers:

- `Aleksoid1978/MPC-BE` issue **#678** states plainly: *"Note: This behavior does not occur in PotPlayer, where Nvidia Super Resolution activates as expected without additional adjustments."* This is a report that PotPlayer + MPCVR activates VSR **more reliably** than MPC-BE + MPCVR (no toggle workaround needed), while using the *same* renderer.
- `clsid2/mpc-hc` issue **#3684**: *"i tried using pot player and the super resolution indicator show up after i set it up"* — confirming VSR engages in PotPlayer.
- `Aleksoid1978/MPC-BE` issue **#678** additionally documents that MPCVR uses *"a shared global configuration for settings across media players"* — *"enabling stats in MPC-BE (CTRL+J) persists when opening PotPlayer."* This is strong structural evidence that **PotPlayer loads the same MPCVR DLL** (registered system-wide as a DirectShow filter), which is precisely why VSR works there.

MPCVR's own `history.txt` corroborates PotPlayer as a supported host: *"Improved compatibility with PotPlayer"* (v0.5.4.1723, 2021-06-23). The changelog consistently names MPC-BE, MPC-HC, and PotPlayer as target players.

Separately, one of the fetched GitHub repository descriptions describes MPCVR forks as providing *"...for PotPlayer/MPC-BE while preserving RTX Video HDR/VSR..."* — independent third-party confirmation that PotPlayer is a recognised MPCVR host for VSR.

**What I could NOT verify:** PotPlayer is closed-source, and `potplayer.daum.net` / Daum café were not accessible to me. I therefore **cannot confirm** whether PotPlayer's **own built-in** D3D11 renderer (independent of MPCVR) requests the NVIDIA PPE extension. The task brief's premise — *"known to support it via its built-in D3D11 video processor scaling"* — is **plausible and consistent** with the fact that PotPlayer does expose a video-processor-based scaler, but I have **no source-level proof**. Marked **UNVERIFIED**.

---

## 6. Kodi (xbmc/xbmc) — **SUPPORTS**, explicitly and comprehensively

**Verdict: (b) supports it, in the Windows DXVA renderer, with an explicit super-resolution enable call.** Kodi has one of the cleanest implementations found, complete with suitability heuristics.

### 6.1 `xbmc/cores/VideoPlayer/VideoRenderers/windows/RendererDXVA.cpp`

Source: <https://raw.githubusercontent.com/xbmc/xbmc/master/xbmc/cores/VideoPlayer/VideoRenderers/windows/RendererDXVA.cpp>

The renderer decides whether to attempt VSR during `Configure()`:

```cpp
bool CRendererDXVA::Configure(const VideoPicture& picture, float fps, unsigned orientation)
{
  if (__super::Configure(picture, fps, orientation))
  {
    m_format = picture.videoBuffer->GetFormat();
    const DXGI_FORMAT dxgi_format = GetDXGIFormat(m_format, __super::GetDXGIFormat(picture));

    if (DX::Windowing()->IsVideoSuperResolutionSettingEnabled() &&
        CProcessorHD::IsSuperResolutionSuitable(picture))
    {
      m_tryVSR = true;
    }

    m_enumerator = std::make_shared<DXVA::CEnumeratorHD>();
    if (m_enumerator->Open(picture.iWidth, picture.iHeight, dxgi_format))
    {
      ...
        // create processor
        m_processor = std::make_unique<DXVA::CProcessorHD>();
        if (m_processor->Open(picture, m_enumerator) && m_processor->SetConversion(m_conversion))
        {
          if (m_tryVSR)
            m_processor->TryEnableVideoSuperResolution();

          return true;
        }
    }
    CLog::LogF(LOGERROR, "unable to create DXVA processor");
    ...
  }
  return false;
}
```

And it surfaces the state in its debug info:

```cpp
std::string CRendererDXVA::GetRenderMethodDebugInfo() const
{
  if (m_processor && DX::Windowing()->SupportsVideoSuperResolution())
  {
    return StringUtils::Format("Video Super Resolution: {}",
                               m_processor->IsVideoSuperResolutionEnabled() ? "requested" : "OFF");
  }
  return {};
}
```

The word **"requested"** is deliberate — Kodi makes the same honesty concession as MPCVR (see §0).

### 6.2 `.../HwDecRender/DXVAHD.cpp` — the actual extension calls

Source: <https://raw.githubusercontent.com/xbmc/xbmc/master/xbmc/cores/VideoPlayer/VideoRenderers/HwDecRender/DXVAHD.cpp>

Kodi cites Chromium as its reference, in a comment:

```cpp
namespace
{
// magic constants taken from Chromium:
// https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/gl/swap_chain_presenter.cc#180
constexpr GUID GUID_INTEL_VPE_INTERFACE = {
    0xedd1d4b9, 0x8659, 0x4cbc, {0xa4, 0xd6, 0x98, 0x31, 0xa2, 0x16, 0x3a, 0xc3}};
...
constexpr GUID GUID_NVIDIA_PPE_INTERFACE = {
    0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};

constexpr UINT kStreamExtensionVersionV1 = 0x1;
constexpr UINT kStreamExtensionMethodSuperResolution = 0x2;
} // unnamed namespace
```

The NVIDIA enable call:

```cpp
void CProcessorHD::EnableNvidiaRTXVideoSuperResolution()
{
  struct NvidiaStreamExt
  {
    UINT version;
    UINT method;
    UINT enable;
  };

  NvidiaStreamExt ext = {kStreamExtensionVersionV1, kStreamExtensionMethodSuperResolution, 1u};

  HRESULT hr = m_pVideoContext->VideoProcessorSetStreamExtension(
      m_pVideoProcessor.Get(), 0, &GUID_NVIDIA_PPE_INTERFACE, sizeof(ext), &ext);
  if (FAILED(hr))
  {
    CLog::LogF(LOGWARNING, "Failed to set the NVIDIA video process stream extension with error {}.",
               CWIN32Util::FormatHRESULT(hr));
    return;
  }

  CLog::LogF(LOGINFO, "RTX Video Super Resolution request enable successfully");
  m_superResolutionEnabled = true;
}

void CProcessorHD::TryEnableVideoSuperResolution()
{
  if (!m_pVideoContext || !m_pVideoProcessor)
    return;

  const DXGI_ADAPTER_DESC ad = DX::DeviceResources::Get()->GetAdapterDesc();

  if (ad.VendorId == PCIV_Intel)
  {
    EnableIntelVideoSuperResolution();
  }
  else if (ad.VendorId == PCIV_NVIDIA)
  {
    EnableNvidiaRTXVideoSuperResolution();
  }
}
```

Note Kodi's log string contains the same hedge: **"request enable successfully"**.

The **suitability heuristic** is the most sophisticated I found, and directly encodes NVIDIA's and Intel's documented limits:

```cpp
bool CProcessorHD::IsSuperResolutionSuitable(const VideoPicture& picture)
{
  // both Intel and NVIDIA support VSR in videos up to 1440p
  if (picture.iWidth > 2560)
    return false;

  const UINT outputWidth = DX::Windowing()->GetBackBuffer().GetWidth();

  if (outputWidth <= picture.iWidth)
    return false;

  // At this time, Intel GPUs do not support VSR on HDR video
  if ((picture.color_primaries == AVCOL_PRI_BT2020 ||
       picture.color_transfer == AVCOL_TRC_SMPTE2084 ||
       picture.color_transfer == AVCOL_TRC_ARIB_STD_B67) &&
      DX::DeviceResources::Get()->GetAdapterDesc().VendorId == PCIV_Intel)
    return false;

  return true;
}
```

Three rules: (1) reject sources wider than 2560px (the "up to 1440p" limit); (2) **require the output to be wider than the source** — i.e. genuine upscaling; (3) reject HDR sources on Intel. Rule (2) is the same logic as VLC's `upscale` boolean.

### 6.3 Does Kodi's scaling go through the video processor?

**Kodi's DXVA renderer does scale via the video processor, but conditionally.** In `RenderImpl()`:

```cpp
void CRendererDXVA::RenderImpl(CD3DTexture& target, CRect& sourceRect, CPoint(&destPoints)[4], uint32_t flags)
{
  CRect src = sourceRect;
  CRect dst = HasHQScaler() ? sourceRect : ApplyTransforms(CRect(destPoints[0], destPoints[2]));
  ...
  m_processor->Render(src, dst, target.Get(), views,
                      flags, buf->frameIdx % UINT32_MAX, m_renderOrientation,
                      m_videoSettings.m_Contrast,
                      m_videoSettings.m_Brightness);

  if (!HasHQScaler())
  {
    // change src and dest in case of dxva scale
    dst.GetQuad(destPoints);
    sourceRect = dst;
  }
}
```

The `HasHQScaler()` branch is decisive: **if the user enables the "HQ scaler", Kodi routes scaling elsewhere and VSR will not engage** — the same fork as VLC's `linear` mode and MPCVR's `m_bVPScaling`. Also note `Supports(ESCALINGMETHOD method)` returns `true` for `VS_SCALINGMETHOD_DXVA_HARDWARE`, and `GetRenderInfo()` advertises `VS_INTERLACEMETHOD_DXVA_AUTO`.

`CProcessorHD::Render()` performs the genuine blit:

```cpp
    hr = m_pVideoContext->VideoProcessorBlt(m_pVideoProcessor.Get(), pOutputView.Get(), 0, 1,
                                            &stream_data);
```

Kodi also deliberately **disables** AMD's video-processor tone mapping to keep its own:

> `// AMD/HDR (as of 2023-06-16): processor tone maps by default and modifies high code values. We want "passthrough" of the signal and to do our own tone mapping when needed.`

**RTX Video HDR in Kodi: not found.** DXVAHD.cpp contains the NVIDIA PPE (VSR) GUID but **no** `kNvidiaTrueHDRInterfaceGUID`. Kodi appears to support RTX VSR but **not** RTX Video HDR. Marked **UNVERIFIED** as to whether it exists elsewhere in the tree.

---

## 7. Plex / Jellyfin

**Verdict: (c) cannot support it directly — but can inherit it.**

Both Plex Media Player / Plex for Windows and Jellyfin Media Player are built on **mpv/libmpv**. That means:

- They **cannot** trigger VSR by default, for exactly the reason any mpv-based player can't: libmpv's render path scales with shaders (§1.3).
- They **inherit** mpv's capability as soon as the host allows the `--vf=d3d11vpp=scaling-mode=nvidia` option to be injected. Whether either embedder exposes an interface for that is **UNVERIFIED**.

Evidence gathered:

- `https://api.github.com/search/issues?q=repo:plexinc/plex-media-player+%22super+resolution%22` → HTTP 200, **`"total_count":0`** — no discussion of super resolution in that repo's issue tracker.
- `https://api.github.com/search/issues?q=repo:jellyfin/jellyfin-media-player+%22super+resolution%22` → **HTTP 422, Validation Failed** (`"The listed users and repositories cannot be searched"`). The repository is not searchable via this API (likely renamed/archived), so Jellyfin Media Player is **UNVERIFIED** in both directions.
- `https://api.github.com/search/issues?q=repo:xbmc/xbmc+%22super+resolution%22` → **HTTP 401 Requires authentication** for the code-search API; the issue-search variant was not separately obtained.

Plex forums (`forums.plex.tv/search?q=...`) were not successfully fetched. Anyone asserting "Plex supports RTX VSR" or "Plex does not" should treat that as **UNVERIFIED** pending a real test with libmpv's `vf` option path.

**The web client case is different:** Plex Web and Jellyfin Web play in a browser, and therefore **do** get VSR whenever the browser is Chromium/Edge on an RTX GPU (§3). That is VSR-by-inheritance from the browser, not from the application.

---

## 8. FFmpeg — **PARTIAL**, and the most important negative result

**Verdict: (c) FFmpeg's D3D11 filters use `ID3D11VideoProcessor` but do NOT expose the vendor super-resolution extensions.** An FFmpeg-only pipeline (no player) **cannot** enable RTX VSR as of this research.

### 8.1 `libavfilter/vf_scale_d3d11.c` — the modern filter

Source: <https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_scale_d3d11.c> (HTTP 200)

This is a real, full video-processor implementation. Its context struct holds the complete object set:

```c
typedef struct ScaleD3D11Context {
    ...
    ///< D3D11 objects
    ID3D11Device *device;
    ID3D11DeviceContext *context;
    ID3D11VideoDevice *videoDevice;
    ID3D11VideoProcessor *processor;
    ID3D11VideoProcessorEnumerator *enumerator;
    ID3D11VideoProcessorOutputView *outputView;
    ID3D11VideoProcessorInputView *inputView;
    ...
} ScaleD3D11Context;
```

It creates the enumerator and processor:

```c
    ///< Create video processor enumerator
    hr = s->videoDevice->lpVtbl->CreateVideoProcessorEnumerator(s->videoDevice, &contentDesc, &s->enumerator);
    ...
    ///< Create the video processor
    hr = s->videoDevice->lpVtbl->CreateVideoProcessor(s->videoDevice, s->enumerator, 0, &s->processor);
```

And blits:

```c
    ///< Process the frame
    hr = videoContext->lpVtbl->VideoProcessorBlt(videoContext, s->processor, s->outputView, 0, 1, &stream);
```

Filter registration:

```c
const FFFilter ff_vf_scale_d3d11 = {
    .p.name           = "scale_d3d11",
    .p.description    = NULL_IF_CONFIG_SMALL("Scale video using Direct3D11"),
    ...
    FILTER_SINGLE_PIXFMT(AV_PIX_FMT_D3D11),
    .p.flags          = AVFILTER_FLAG_HWDEVICE,
    .flags_internal   = FF_FILTER_FLAG_HWFRAME_AWARE,
};
```

Options are only `width`, `height`, `format`. **There is no `scaling-mode`, no vendor GUID, and no call to `VideoProcessorSetStreamExtension` or `VideoProcessorSetOutputExtension` anywhere in the file.** This is a functional gap, and the single most actionable finding of this report: adding a `scaling-mode` option here mirroring mpv's would be a small, well-scoped patch that would unlock VSR for every FFmpeg- and libavfilter-based pipeline.

(Note: this filter has **no relationship** to the `vf_d3d11vpp` name — that name is mpv's, in `video/filter/vf_d3d11vpp.c`, not FFmpeg's.)

### 8.2 `vf_libplacebo.c`

`https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_libplacebo.c` → **fetch failed** (`TypeError: fetch failed`) on both attempts. However, the architectural argument is conclusive without it: libplacebo — as confirmed by reading mpv's `vo_gpu_next.c` (§1.3), which is libplacebo's primary consumer — renders via its own Vulkan/OpenGL shader pipeline (`pl_render_image`). It has no `ID3D11VideoProcessor` dependency at all. **`libplacebo` therefore cannot trigger VSR**, regardless of its filter options. Marked high-confidence but the file itself is unread, so the specific option list is **UNVERIFIED**.

### 8.3 `vf_d3d11vpp.c` (FFmpeg) — does not exist

Both `https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_d3d11vpp.c` and `.../libavfilter/d3d11vpp.c` returned **HTTP 404**. I also retrieved the full `libavfilter` directory listing via `api.github.com/repos/FFmpeg/FFmpeg/contents/libavfilter?ref=master` (HTTP 200). **`d3d11vpp` is an mpv filter name, not an FFmpeg one.** This is a common point of confusion and worth stating explicitly in the report.

### 8.4 FFmpeg summary of D3D11 filter support

| Filter | Uses `ID3D11VideoProcessor`? | Vendor SR extension? | VSR? |
|---|---|---|---|
| `scale_d3d11` (FFmpeg) | **Yes** (`VideoProcessorBlt`) | **No** | **No** |
| `d3d11vpp` (mpv, *not* FFmpeg) | **Yes** | **Yes** (NVIDIA + Intel) | **Yes** |
| `libplacebo` (FFmpeg) | No (own shaders) | No | **No** |

---

## 9. Summary table

| Player | Renderer path | Uses `ID3D11VideoProcessor`? | VSR works? | Source evidence |
|---|---|---|---|---|
| **mpv** | `vf_d3d11vpp` filter, `scaling-mode=nvidia`, with `--hwdec=d3d11va` | **Yes** — `CreateVideoProcessor` + `VideoProcessorBlt` (`video/filter/vf_d3d11vpp.c`) | **(b) Yes, explicit opt-in only.** Not via `vo=gpu`/`gpu-next`/`d3d11` context, which use libplacebo/shaders | `vf_d3d11vpp.c` (`enable_nvidia_rtx_extension`, `NVIDIA_PPE_INTERFACE_GUID`, `{1,2,1}`); `DOCS/man/vf.rst`; `context.c`; `vo_gpu_next.c` |
| **mpv (RTX Video HDR)** | Same filter, `nvidia-true-hdr=yes` | **Yes** | **(b) Yes** — `enable_nvidia_true_hdr`, `{4,3,1}`, with `GetStreamExtension` capability probe | `vf_d3d11vpp.c` (`NVIDIA_TRUE_HDR_INTERFACE_GUID`, `supports_nvidia_true_hdr`) |
| **VLC** | `d3d11_scaler.cpp`, selected by `--d3d11-upscale-mode=super` | **Yes** — dedicated `ID3D11VideoProcessor` for upscaling; default `linear` mode uses its own shaders | **(b) Yes, explicit opt-in.** NVIDIA (WDDM>153000), Intel, **and Mthreads** | `d3d11_scaler.cpp` (`SetStreamExtension`, `kStreamExtensionMethodRTXVSR=0x2`, `upscale` gate); `direct3d11.cpp` (`ppsz_upscale_mode`, `d3d11-upscale-mode`) |
| **VLC (RTX Video HDR)** | — | — | **Likely no.** `--d3d11-hdr-mode=generate` uses VLC's own `D3D11_TonemapperCreate` shader path; **no NVIDIA TrueHDR GUID found** | `direct3d11.cpp` (`InitTonemapProcessor`, `d3d11_tonemap.cpp`) — **UNVERIFIED** as absent |
| **Chrome / Edge** | GPU-process DirectComposition overlay → `SwapChainPresenter::VideoProcessorBlt` | **Yes** — `ID3D11VideoProcessor` in `ui/gl/`, not in `media/` | **(a) Yes — native, automatic on NVIDIA** (`gpu_vendor_id == 0x10de`), **disabled on battery** | `ui/gl/swap_chain_presenter.cc` (`ToggleNvidiaVpSuperResolution`, `ToggleVpSuperResolution`); `ui/gl/dc_layer_tree.cc`; `gpu/ipc/service/gpu_init.cc` |
| **Chrome / Edge (Intel)** | Same | **Yes** | **(b) Requires `--enable-features=IntelVpSuperResolution`** (default-disabled flag) | `ui/gl/gl_switches.cc` (`BASE_FEATURE(kIntelVpSuperResolution, base::FEATURE_DISABLED_BY_DEFAULT)`) |
| **Chrome / Edge (RTX Video HDR)** | Same | **Yes** | **(a) Yes** — `ToggleVpAutoHDR`, TrueHDR GUID, `{4,3,1}`, behind `NvidiaDriverSupportsTrueHDR()` probe | `ui/gl/swap_chain_presenter.cc` |
| **MPC-HC** | **Not in-tree.** Requires *MPC Video Renderer* as the renderer + "Request Super Resolution" | **Yes** — via MPCVR (`Source/D3D11VP.cpp`) | **(b) Yes, conditional** on MPCVR + VP scaling enabled + resolution ceiling | `D3D11VP.cpp` (`SetSuperResNvidia`, `SetSuperRes` w/ SD/HD/FHD thresholds); `D3D11VP.h`; `history.txt`; issues #4214, #3684 |
| **MPC-BE** | Same as MPC-HC — MPCVR | **Yes** — MPCVR | **(b) Yes, conditional** (plus known toggle-to-activate bug) | `emoose/VideoRenderer` sources; `Aleksoid1978/MPC-BE` issues #674, #678, #521, #148 |
| **PotPlayer** | Hosts **MPC Video Renderer** (shared global config with MPC-BE proves same DLL) | **Yes** — via MPCVR | **(b) Yes, via MPCVR — reportedly more reliable than MPC-BE.** Built-in renderer **UNVERIFIED** | MPC-BE #678 (*"does not occur in PotPlayer... activates as expected"*); MPC-HC #3684; MPCVR `history.txt` (*"Improved compatibility with PotPlayer"*) |
| **Kodi** | `RendererDXVA.cpp` → `DXVAHD.cpp` (Windows only) | **Yes** — `VideoProcessorBlt`; **but not when HQ scaler is on** | **(b) Yes, in the DXVA renderer**, with `IsSuperResolutionSuitable()` upscale/HDR/resolution gating | `RendererDXVA.cpp` (`TryEnableVideoSuperResolution`); `DXVAHD.cpp` (`EnableNvidiaRTXVideoSuperResolution`, `GUID_NVIDIA_PPE_INTERFACE`, `IsSuperResolutionSuitable`) |
| **Kodi (RTX Video HDR)** | — | — | **Not found.** No TrueHDR GUID in `DXVAHD.cpp` | **UNVERIFIED** |
| **Plex (desktop)** | mpv/libmpv → shaders | **No** (no VSR path exposed) | **(c) Cannot, directly** — but **inherits** mpv's if `vf=d3d11vpp` can be injected (**UNVERIFIED**) | `api.github.com/search/issues?q=repo:plexinc/plex-media-player+"super resolution"` → `total_count: 0` |
| **Jellyfin Media Player** | mpv/libmpv → shaders | **No** | **(c) Cannot, directly.** Inherits mpv capability if exposed (**UNVERIFIED**) | GitHub search API returned **HTTP 422** — repo not searchable. **UNVERIFIED** |
| **Plex Web / Jellyfin Web** | Browser (Chromium/Edge) | **Yes** (via browser) | **(a) Yes** — inherited from the browser, §3 | `ui/gl/swap_chain_presenter.cc` |
| **FFmpeg `scale_d3d11`** | `libavfilter/vf_scale_d3d11.c` | **Yes** — `VideoProcessorBlt` | **(c) No** — no `SetStreamExtension` call anywhere in the file | `vf_scale_d3d11.c` (full read) |
| **FFmpeg `libplacebo`** | `pl_render_image`, own shaders | **No** | **(c) No** | Inferred from mpv `vo_gpu_next.c`; `vf_libplacebo.c` fetch failed — **UNVERIFIED** in detail |
| **FFmpeg `vf_d3d11vpp`** | — | — | **Does not exist** — `d3d11vpp` is mpv's filter name. FFmpeg URLs return **HTTP 404** | `api.github.com/.../FFmpeg/contents/libavfilter` listing; 404s on both paths |

---

## 10. Cross-cutting conclusions

1. **The single gate is the video processor.** VSR requires `ID3D11VideoProcessorBlt` for the *upscale* step. Every player that scales with its own shaders (mpv `vo_gpu`, VLC `linear`, MPCVR with VP scaling off, Kodi with HQ scaler, libplacebo, Plex/Jellyfin via libmpv) is structurally incapable of VSR until the user selects the video-processor path.

2. **The extension call is now commodity.** The identical `NVIDIA_PPE_INTERFACE_GUID` / method `0x2` / version `0x1` triple appears verbatim in **five independent codebases** — mpv, VLC, Kodi, MPC Video Renderer, and Chromium — with Kodi's source openly citing Chromium as the reference. VLC additionally implements Intel and Mthreads; Chromium, Kodi, mpv, and MPCVR implement Intel. The knowledge is fully public and unencumbered.

3. **"Requested" ≠ "working".** Three independent projects use deliberately hedged language: MPCVR (*"only means that the request was sent successfully"*), Kodi (*"RTX Video Super Resolution request enable successfully"*, and the UI string *"requested"*), and mpv's manual (*"only enables the appropriate processing extensions; whether it actually works..."*). Chromium goes furthest, adding a **retry-with-SR-off** fallback and latching the feature off if the blit fails. Any report on VSR should carry this caveat.

4. **Gating conditions recur across implementations:** source must be smaller than output (VLC `upscale`, Kodi `outputWidth <= picture.iWidth`), source must be below a resolution ceiling (Kodi `> 2560`, MPCVR `SUPERRES_*`), and VSR is skipped for HDR on Intel (Kodi `IsSuperResolutionSuitable`). Chromium uniquely adds a **battery** gate.

5. **Format constraints are real.** MPC-HC issue #4214 documents that a 16-bit-float texture format greys out / breaks the VSR option — consistent with VSR operating on 8/10-bit YUV/RGB surfaces.

6. **The clearest actionable gap is FFmpeg.** `libavfilter/vf_scale_d3d11.c` already owns a complete `ID3D11VideoProcessor` with `VideoProcessorBlt`; it is missing only the vendor extension calls and a `scaling-mode` option. Porting ~40 lines from `vf_d3d11vpp.c` would give every libavfilter-based pipeline VSR.

7. **Where VSR is *not*:** FFmpeg-only pipelines, libplacebo-based rendering, Plex/Jellyfin desktop apps (unless they expose mpv's `vf`), madVR (**UNVERIFIED**), and `EVRCustomPresenter` (**UNVERIFIED**).

---

## 11. All successfully fetched URLs (HTTP 200)

### mpv
- https://raw.githubusercontent.com/mpv-player/mpv/master/video/filter/vf_d3d11vpp.c ⭐ *(core evidence)*
- https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/vf.rst ⭐ *(option docs)*
- https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/d3d11/context.c
- https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/d3d11/hwdec_d3d11va.c
- https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/gpu/hwdec.c
- https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/vo_gpu_next.c
- https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/options.rst
- https://api.github.com/repos/mpv-player/mpv/commits?path=video/filter/vf_d3d11vpp.c&per_page=100 ⭐ *(history)*
- https://api.github.com/repos/mpv-player/mpv/releases?per_page=20 *(truncated; no VSR mention found)*

### VLC
- https://raw.githubusercontent.com/videolan/vlc/master/modules/video_output/win32/d3d11_scaler.cpp ⭐ *(core evidence)*
- https://raw.githubusercontent.com/videolan/vlc/master/modules/video_output/win32/d3d11_scaler.h
- https://raw.githubusercontent.com/videolan/vlc/master/modules/video_output/win32/direct3d11.cpp ⭐ *(user option)*
- https://api.github.com/repos/videolan/vlc/contents/modules/video_output/win32 *(directory listing)*

### Chromium / Edge
- https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/swap_chain_presenter.cc ⭐ *(core evidence)*
- https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/gl_switches.cc ⭐ *(flags)*
- https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/gl_features.cc
- https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/gl_features.h
- https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/dc_layer_tree.cc
- https://raw.githubusercontent.com/chromium/chromium/main/gpu/ipc/service/gpu_init.cc
- https://raw.githubusercontent.com/chromium/chromium/main/media/base/media_switches.cc *(negative result — no SR flag)*
- https://raw.githubusercontent.com/chromium/chromium/main/media/base/media_switches.h *(negative result)*
- https://raw.githubusercontent.com/chromium/chromium/main/media/gpu/windows/d3d11_video_processor_proxy.cc *(decoy — no SR)*
- https://raw.githubusercontent.com/chromium/chromium/main/media/gpu/windows/d3d11_video_decoder_wrapper.cc

### MPC-HC / MPC-BE / MPC Video Renderer
- https://raw.githubusercontent.com/emoose/VideoRenderer/master/Source/D3D11VP.cpp ⭐ *(core evidence)*
- https://raw.githubusercontent.com/emoose/VideoRenderer/master/Source/D3D11VP.h ⭐
- https://raw.githubusercontent.com/emoose/VideoRenderer/master/Source/DX11VideoProcessor.cpp
- https://raw.githubusercontent.com/emoose/VideoRenderer/master/Source/VideoProcessor.h
- https://raw.githubusercontent.com/emoose/VideoRenderer/master/history.txt ⭐ *(0.6.7.2013 changelog)*
- https://api.github.com/repos/emoose/VideoRenderer/git/trees/master?recursive=1
- https://api.github.com/repos/clsid2/mpc-hc/contents/src/mpc-hc
- https://api.github.com/repos/Aleksoid1978/MPC-BE/contents
- https://api.github.com/repos/Aleksoid1978/MPC-BE/git/trees/master?recursive=1 *(truncated)*
- https://api.github.com/search/issues?q=repo:clsid2/mpc-hc+super+resolution&per_page=30
- https://api.github.com/search/issues?q=repo:clsid2/mpc-hc+%22Video+Super+Resolution%22&per_page=20
- https://api.github.com/search/issues?q=repo:Aleksoid1978/MPC-BE+super+resolution&per_page=30
- https://api.github.com/search/repositories?q=MPC+Video+Renderer+MPCVR&per_page=10

### Kodi
- https://raw.githubusercontent.com/xbmc/xbmc/master/xbmc/cores/VideoPlayer/VideoRenderers/windows/RendererDXVA.cpp ⭐
- https://raw.githubusercontent.com/xbmc/xbmc/master/xbmc/cores/VideoPlayer/VideoRenderers/HwDecRender/DXVAHD.cpp ⭐ *(core evidence)*

### FFmpeg
- https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_scale_d3d11.c ⭐ *(negative result)*
- https://api.github.com/repos/FFmpeg/FFmpeg/contents/libavfilter?ref=master *(no vf_d3d11vpp)*
- https://api.github.com/repos/FFmpeg/FFmpeg/git/trees/master?recursive=1 *(truncated)*

### Plex / Jellyfin
- https://api.github.com/search/issues?q=repo:plexinc/plex-media-player+%22super+resolution%22&per_page=20 *(HTTP 200, `total_count: 0`)*

---

## 12. Failed / blocked fetches (declared honestly)

| URL | Result | Impact |
|---|---|---|
| `web_search` (all queries) | Error: no `web_search_tool_result` blocks | None — all work done via `web_fetch` |
| `https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/d3d11/hwdec_d3d11va.c` (1st try) | `TypeError: fetch failed` | Retried successfully |
| `https://raw.githubusercontent.com/clsid2/mpc-hc/develop/src/mpc-hc/VideoRenderer.cpp` / `.h` | `fetch failed` / **404** | Confirms these files are not where VSR lives |
| `https://raw.githubusercontent.com/xbmc/xbmc/master/xbmc/windowing/windows/WinSystemWin32DXGI.cpp` / `.h` | **404** | Kodi's `SupportsVideoSuperResolution()` definition not located; **UNVERIFIED** |
| `https://raw.githubusercontent.com/chromium/chromium/main/media/gpu/windows/d3d11_video_decoder.cc` | **404** | File renamed; wrapper version used instead |
| `https://raw.githubusercontent.com/chromium/chromium/main/ui/gl/direct_composition_support.cc` | `fetch failed` | `dc_layer_tree.cc` + `gpu_init.cc` used instead |
| `https://chromium.googlesource.com/chromium/src/+/main/ui/gl/swap_chain_presenter.cc?format=TEXT` | `fetch failed` | GitHub mirror used instead |
| `https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_libplacebo.c` (2 tries) | `fetch failed` | Argument made architecturally instead — **UNVERIFIED in detail** |
| `https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/vf_d3d11vpp.c` and `.../d3d11vpp.c` | **404** | Confirms filter does not exist in FFmpeg |
| `https://nvidia.custhelp.com/app/answers/detail/a_id/5448` | **HTTP 403** (Oracle "Technical Difficulties") | NVIDIA's official supported-app list **UNVERIFIED** |
| `https://api.github.com/search/code?q=VideoSuperResolution+repo:xbmc/xbmc` | **HTTP 401** Requires authentication | Code-search API unusable; relied on file reads |
| `https://api.github.com/search/issues?q=repo:jellyfin/jellyfin-media-player+...` | **HTTP 422** Validation Failed | Jellyfin Media Player **UNVERIFIED** |
| `https://api.github.com/repos/videolan/vlc/.../d3d11_deinterlace.c` | `fetch failed` | Not needed — `d3d11_scaler.cpp` superseded it |
| VLC GitLab issues / forum search | not successfully fetched | VLC community discussion **UNVERIFIED** |
| PotPlayer `potplayer.daum.net`, Daum café | not accessible | PotPlayer built-in renderer **UNVERIFIED** |

---

## 13. Reproduction commands

```bash
# mpv — NVIDIA RTX VSR (forces upscale so the VPP path engages)
mpv --hwdec=d3d11va --vf=d3d11vpp=scaling-mode=nvidia:scale=2.0 video.mkv

# mpv — add RTX Video HDR (SDR source only)
mpv --hwdec=d3d11va --vf=d3d11vpp=nvidia-true-hdr=yes:scale=2.0 video.mkv

# mpv — Intel VSR
mpv --hwdec=d3d11va --vf=d3d11vpp=scaling-mode=intel:scale=2.0 video.mkv

# VLC — select the Super Resolution upscaler (replaces the default "linear" sampler)
vlc --d3d11-upscale-mode=super video.mkv

# Chrome / Edge — Intel GPUs only (NVIDIA needs no flag)
chrome --enable-features=IntelVpSuperResolution

# FFmpeg — WILL NOT enable VSR (scale_d3d11 has no extension support); shown for contrast
ffmpeg -hwaccel d3d11va -i in.mkv -vf scale_d3d11=w=3840:h=2160 -c:v hevc_nvenc out.mkv
```

**Verification tips:** enable the NVIDIA Control Panel's "RTX Video Enhancement" indicator overlay, or watch `VideoProcessor*` / "Super Resolution" counters in GPU-Z or Task Manager. Do **not** trust an in-player "SuperResolution: active" label alone — see §10.3.

---

*Report compiled from direct source fetches only. Items marked UNVERIFIED are explicitly flagged in-line and consolidated in §12.*
