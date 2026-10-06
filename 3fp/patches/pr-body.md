# PR 描述（GitHub PR body 用）

> 标题：**Add a picture mode for still and animated images**
> 目标：`Lake1059/FFF_Project:master` ← `luoye-cpu/FFF_Project:upstream/image-mode-pr`
> 单提交（squash），基于上游 `a87046e`（2026/9/24-3）

---

## What

Adds a picture mode: still images open in a dedicated viewer (folder list,
zoom, pan, prev/next, no timeline), animated images (animated WebP, APNG,
animated AVIF/HEIC) play and loop, JPEG XR joins the supported formats, and
file associations gain three picture tiers. Video behaviour is unchanged.

## Why

Still images currently ride the video pipeline and are treated as finished
one-frame playback — pictures report `PlaybackEnded` immediately, seeking is
rejected or clamped, animated/JXR formats fall through, and 2:1 panoramas
race with the 360 mode. Every picture path here is gated behind picture-mode
or still-image checks, so video takes no new code paths.

## Native

- **Still-image semantics**: decide stillness by frame count instead of the
  demuxer name (AVIF/HEIC ride `mov`); opening no longer ends playback; seek
  is a no-op; sources wider than 16384 px are rejected up front with a clear
  message instead of a vague renderer error.
- **`FFF3FP_GetImageInfo` (new, additive)**: frame count, animation, EXIF
  rotation, pixel format/bit depth, ICC, alpha, colour primaries/transfer.
  No struct or version changes — `PlayerApiVersion` stays 15. The details
  are built on the worker thread while the media opens and handed out
  under the snapshot lock (decoder-owned frames never cross threads).
  An older host never calls the new entry point, and a new host cannot
  create a session on an older DLL at all (version gate).
- **Animated image looping** at session level:
  - APNG: the demuxer loses a frame at every wrap-around (upstream CLI does
    the same), so the session opens it with `ignore_loop` and rewinds at EOF;
  - animated WebP: its `av_seek_frame` reports success but the demuxer then
    supplies no packets, so it loops inside the demuxer (`ignore_loop=0`);
  - animated AVIF/HEIC: the animation track is selected instead of the still
    cover item, then looped by the same session rewind.
- **Wide gamut preserved**: Display P3 stills are converted to BT.2020
  (zscale) and, with HDR sources, drive a 16-bit scRGB swap chain when the
  display supports it; SDR sources still fall back. The three colour-mode
  gates that previously disagreed are kept in step and arbitrated by
  `EnsureSwapChain` (previously P3 photos were silently clipped to
  Rec.709).

## Player

- Picture mode: folder list, zoom/pan, prev/next, timeline hidden; gated off
  for videos; yields to the 360 mode (2:1 panoramas stay 360 — regression
  covered).
- Animated images play and loop; `.jxr/.wdp/.hdp` join the media whitelist.
- File associations: three picture tiers (39 formats, each verified to decode
  *and* present with the bundled kernel before inclusion).
- Keyboard handoff between picture / 360 / clip modes respects handled keys.

## Compatibility

- API is additive; no ABI break, no version bump.
- Video behaviour unchanged. Documented exceptions: at zoom > 1 the pan
  clamp no longer shows a black edge on one side (shared renderer path);
  re-binding the same output window re-probes the display refresh rate
  (defensive; hosts do not trigger it).
- Known edge: a mov-family file whose first video track holds a single
  frame and a later track holds many is treated as an animated image (the
  same heuristic that picks the animation track of AVIF/HEIC sequences).

## Verification

- Kernel (MSVC Release x64) and host build clean.
- 17-sample probe matrix: still/animated/HDR/wide-gamut/oversize —
  state machine, `GetImageInfo` fields, and swap-chain bit depth (16 for
  HDR/P3, 8 fallback for SDR) all as designed.
- All five animated formats loop for minutes with zero stalls or dropped
  frames.
- Generated SDR and HDR10 videos and a 2:1 clip still play, seek and end
  normally (`PlaybackEnded` on real videos preserved).
- Host regression suite: same pass/fail set before and after the change.

## Notes

- `FFF3FP_GetImageInfo` is exposed for hosts that want picture details
  (e.g. a metadata panel or pausing animated images on their first frame);
  the current host drives the picture mode off the existing `staticImage`
  media-info flag.
- The pan clamp change and the animated-loop behaviour are the only two
  places where already-released behaviour differs; both are listed here on
  purpose.
