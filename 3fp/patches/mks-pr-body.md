# PR 描述（GitHub PR body 用）

> 标题：**Add MKS subtitle container support**
> 目标：`Lake1059/FFF_Project:master` ← `luoye-cpu/FFF_Project:upstream/mks-subtitle-pr`
> 单提交，基于上游 `a87046e`（2026/9/24-3）
> 对应上游 issue #2（mka 已做，mks 需要样本与实现）

---

## What

Adds support for external `.mks` subtitle containers: the host can now
enumerate their tracks and load them (text ASS/SSA/subrip and bitmap PGS
alike), fonts attached inside the container are actually used, and the
subtitle menu exposes every track with its language and title. Video and
audio behaviour is unchanged.

## Why

An external `.mks` is a subtitle-only Matroska container. Two things
broke today:

- The media open rejects files without playable video or audio streams,
  so the host had no way to learn which tracks a `.mks` carries - and
  the same `-1` stream index that "auto-selects" for bitmap subtitles
  fails outright for text tracks.
- libass never saw the fonts attached inside the container, so a fansub
  ASS rendered with fallback fonts. This is the same defect Jellyfin
  #15222 reports ("ASS in external MKS renders incorrectly due to
  missing fonts"); on real-world samples the attachments are 95% of the
  file size, and rendering with vs. without them differs measurably.

Both halves are needed: probing alone shows grey fallback text, fonts
alone leave the host nothing to open.

## Native (additive only; `PlayerApiVersion` stays 15)

- **`FFF3FP_ProbeSubtitleStreams` (new)**: probes the subtitle tracks of
  a container by path without opening a playback session. The JSON
  reuses the stream shape of `FFF3FP_GetMediaInfo` (clients deserialize
  both with the same type, gaining `language`/`title` for free) and
  adds a top-level `startTimeSeconds`, because a standalone container
  has no reference stream to normalize a nonzero start time against -
  real-world samples carry offsets of minutes, and the host can surface
  its existing subtitle-delay correction instead of an apparently dead
  first minute. A file without subtitle tracks is `Success` with an
  empty array, not an error.
- **Attached fonts are registered with libass** before the track is
  parsed (a font/opentype/sfnt mimetype, else a `.ttf/.otf/.ttc` name).
  This also benefits embedded ASS: the renderer previously only read
  fonts from the directories the host passes in, so attached fonts were
  ignored everywhere.

## Player

- `.mks` joins the external subtitle whitelist. Loading a container
  probes its tracks, picks one (explicit index, then default/forced,
  then the first) and dispatches by codec through the same generators
  the embedded-subtitle path uses: bitmap tracks (PGS/DVB/XSUB) go to
  the SUP generator, text tracks to the ASS generator. Private font
  directories are still resolved against the video; fonts attached to
  the container are handled by the kernel.
- The subtitle menu lists every track of a container with codec,
  language and title from the probe; switching tracks reloads
  atomically without dropping playback.
- The new entry point is resolved at runtime (no static import): a host
  paired with an older kernel reports the file as unsupported instead
  of failing to start.
- Known styling note: subrip inside a container renders through libass
  with its built-in default header, so its look follows libass (not the
  user's SRT style settings) - same as embedded subrip today.

## Verification

- Kernel probes: a 7-track official sample reports
  chi/eng/fre/ger/ita/jpn/spa; a video-only file returns an empty
  array; PGS renders through the bitmap path (689×56, 22291 opaque
  pixels, start 1.001 s). Attached-font rendering is bit-identical to
  supplying the same fonts as an external directory, and removing the
  registration restores the defective baseline (reverse check).
- Host regression (`--mks-subtitle-regression`): whitelist, scan,
  track selection, probe JSON deserialization, kernel-capability
  fallback, and a real-sample end to end through the production render
  dispatch (non-empty commands). The render-dispatch assertion fails
  when the MKS branch is removed (reverse check). Full host suite
  unchanged from baseline.
- Real-world sample: a community `.mks` (1 ASS + 9 font attachments)
  rendered with and without the fix differs exactly as an external font
  directory does - the attachments now load.

## Notes

- `startTimeSeconds` is exposed but not yet consumed; the host can use
  its existing subtitle-delay correction for containers that start
  away from zero.
- Sample files for testing (self-made and official-media remuxes, plus
  a real-sample derivative with the timeline normalized) can be
  attached to issue #2 on request; the fansub original is not
  redistributed.
