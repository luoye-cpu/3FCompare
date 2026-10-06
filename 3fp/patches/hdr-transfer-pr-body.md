# HDR transfer PR · 提交信息与正文（二合一）

> 标题：**Decode HDR pixels by their own transfer function**
> 目标：`Lake1059/FFF_Project:master` ← `luoye-cpu:upstream/hdr-transfer-pr`
> 单提交 `b4e7f14`（1 文件 +25/−4），基于上游 `a87046e`

---

## Commit message

```
Native: decode HDR pixels by their own transfer function

The decode transfer function was inferred from the HDR metadata
classification (Hlg format -> HLG, everything else HDR -> PQ). Dynamic
metadata such as HDR Vivid can arrive on a stream whose samples are
encoded with a different transfer function: decoding that HLG signal as
PQ over-brightens the highlights by roughly 8-18x (measured with the
FFmpeg fate-suite HDR Vivid sample: center pixel linear 40.6 -> 5.7).

ST 2094 metadata is defined for streams that keep their own encoding, so
the transfer now follows the frame's color_trc whenever it declares one.
Dolby Vision keeps its compatibility-layer decision, frames without a
declared trc keep the previous format-based inference, and SDR sessions
are untouched. HDR10+ sample pixels are bit-identical before/after.
```

## PR body

### What

`PlayerVideoRenderer::Render()` picked the shader decode transfer function
from the HDR metadata classification (`Hlg` → HLG, any other HDR format →
PQ). When a stream carries dynamic metadata (HDR10+/HDR Vivid) whose
samples use a different transfer function — e.g. an HLG-encoded stream
with HDR Vivid metadata, exactly like FFmpeg's own fate-suite sample
`hevc/hdr_vivid_h265_sample.hevc` — the samples get decoded with the wrong
curve: an HLG signal read as PQ over-brightens the highlights by roughly
8-18x, and the transfer flips mid-playback (first frame HLG, later frames
PQ).

### Fix

Priority order for `settings.transfer`:

1. **Dolby Vision**: unchanged — the RPU remapping decides via the
   compatibility layer, which a container trc cannot express.
2. **The frame's own `color_trc`** when it declares HLG or PQ: ST 2094
   metadata is defined for streams that keep their own encoding, so
   metadata arriving later must not change how samples are decoded.
3. **Fallback**: the previous format-based inference for frames that
   declare nothing.

SDR sessions are untouched (transfer stays 0).

### Verification

- FFmpeg fate-suite `hevc/hdr_vivid_h265_sample.hevc` (HLG + CUVA Vivid):
  center pixel linear RGB **40.59 → 5.74** (the over-brightened highlight
  returns to a sane value); `hdrFormat=HDR Vivid`,
  `processingPath=HDR Vivid dynamic`, `dynamicHdrMetadataActive=1`,
  `sourcePeak=10000` all unchanged.
- FFmpeg fate-suite `hevc/hdr10_plus_h265_sample.hevc` (PQ + HDR10+):
  all probed pixels **bit-identical** before/after; `sourcePeak=334`
  unchanged.
- Generated SDR / HDR10 (PQ) / HLG videos and a 2:1 clip: formats, paths
  and swap-chain bit depths unchanged.
- Host regression suite: same pass/fail set as the upstream baseline.

### Notes

- The HDR Vivid baseline per CUVA 005.1-2021 is PQ/BT.2020, so real-world
  Vivid content is unaffected today; the HLG combination is an
  out-of-spec case that this fix hardens against (and it removes the
  first-frame flip for any late-metadata stream).
