# PR #17 · 重做轮（2026-10-09）

分支：`luoye-cpu:upstream/kernel-all` → **`ad646a72dc6c0170c0fab0188ca859fb8f7844b5`**（一笔提交）
基线：`Lake1059:master` = **`ac8f1bff23b71446c2adf6d4269731b464dfa1cb`**（已含 #16）
推送方式：`--force-with-lease`（旧尖端 `72c0aec…`），因为这一支是**重放到新 master 后压成单提交**的历史；
上游 `master` 本机无权直推（`git push --dry-run` 得 `Permission to Lake1059/FFF_Project.git denied to luoye-cpu` / 403），
所以按你的指示并进 #17 一起提交。

下面是可直接粘进 PR 描述正文（English）：

---

## Summary

Rebased onto `master` after #16 landed, squashed into one commit, and the review round's
five real defects are fixed here rather than left in the tree. The parent of this commit
is exactly current `master`, so the merge is a clean fast-forward.

Contents: IAMF immersive audio through the AOM reference decoder, adaptive
downscale-before-upload with its layout-0 geometry, float-RGB input (JPEG XR), video
super resolution and Nvidia extensions, view rotation and still-image scaling mode,
the inline-lyrics kit, and the host contract for all of it.

## What changed compared with the previous state of this PR

1. **Rebased onto the merge of #16.** 16 conflict hunks resolved. The notable one is
   `PlayerVideoRenderer::Render`, where #16 added `frameIndex` and this branch added
   `imageMode`: the merged signature keeps both, and every call site was re-checked
   against it.
2. **Snapshot fields are appended at the tail, not inserted in the middle.** The earlier
   version put `iamfActive` / `iamfChannels` / `iamfSoundSystem` between
   `decodedAudioFrames` and `audioPosition100ns` while its own comment claimed the
   struct stayed layout-compatible. Both the C header and the host mirror now append.
3. **`PlayerApiVersion` is 18, not 17 or 19.** #16 occupies 17 (ST 2094 snapshot
   diagnostics). The four sites that must agree — `PlayerApi.cpp`, `FFF.Native.rc`,
   host `期望API版本`, `<PlayerApiVersion>` — are 18, and the build prints that it
   verified them.
4. **The solution builds.** `FFF.Player.Tests/Program.vb` called four routines that no
   longer exist anywhere in the repository, so the submitted state could not compile.
5. **Five review defects fixed** (section below).
6. **Build prerequisite named.** `tools/构建3FP.ps1` now stops with the reason instead of
   letting the compiler emit several hundred `undefined type LakeUI.*` errors.

## Defects fixed in this round

| # | Defect | Where | Evidence |
|---|---|---|---|
| A | HDR metadata was never re-submitted after the swap chain was created or resized: the "only push when it moved" cache is reset only on the SDR branch, so a new chain could get nothing while `isHdrSource` kept reporting HDR | `VideoRenderer.cpp` `CreateSwapChain` / `ReconfigureSwapChain` → new `InvalidateHdrMetadataCache()` | **Not measured on a panel** — no HDR display and no metadata-capture probe on this machine. Shape is from code. |
| B | The injection API accepted a preset whose declared anchors never rise above the knee. Anchors are validated non-decreasing from `kneePointY`, so a flat line passed; every Bézier segment then has both endpoints and both slopes at zero, which maps the range to 0 — with an 11 to 12 anchor preset about 91% of the 256-row curve texture | `HdrProcessor.cpp` `BuildInjectedWindow` | Such an entry is now rejected. The bitstream parser is untouched, because shipped HDR10+ legitimately uses the no-curve shape (`tone_mapping_flag == 0`), and that path still degrades to the static curve as before. |
| C | Two feeders drove one libiamf handle (file pump and demuxer packets) while sharing `handle_`, `pcm_` and the byte cursor; interleaving corrupts access-unit framing, and libiamf rejects an audio-frame OBU for a substream it already holds | `PlayerSession.cpp` `PumpPlayback` and `DecodePendingPacket` | The pump is the sole owner whenever it can run; a session with no audio renderer keeps the packet path. `FallBackFromIamf` clears the decoder and re-opens FFmpeg, so the fallback still works. |
| D | The packet path stamped PTS in 100 ns and the session re-read it through the stream time base, so a 1024-sample frame's `213333` became `4.44 s` (**~208×**) and the renderer synthesised silence to cover the jump | `PlayerSession.cpp` `DecodeIamfPacket` | Same conversion and same convention as `9df3783` applied on the pump side; that commit fixed one of the two paths. |
| E | A snapshot rejected by the kernel's own size/version gate was swallowed, and the overlay printed "尚未打开媒体" — an ABI violation rendered as "healthy but nothing loaded" | `播放器控制器.vb` `安全读取会话` | That class of failure now reports once per session through the existing `播放错误` event; other result codes keep the previous non-fatal behaviour. |

Also: `FFF.Native.rc` used to declare api 16 while the runtime reported a different
number, and two `C4267` truncations in `IamfAudioDecoder.cpp` are gone.

## ABI, measured rather than asserted

Compiled both headers with the installed MSVC (14.51.36231) and printed `sizeof`:

| struct | current `master` (#16) | this branch |
|---|---|---|
| `FFF3FPConfiguration` | 80 B | **112 B** |
| `FFF3FPSnapshot` | 368 B / 63 fields | **392 B / 68 fields** |

Because everything is appended, the offsets #16 published do not move. `FFF3FP_Create`
rejects a smaller configuration struct, and `FFF3FP_GetSnapshot` rejects a smaller
snapshot struct, so a host that was not rebuilt is refused instead of reading past the
end of its own struct.

## Shape

`git diff --shortstat ac8f1bf..ad646a7` → **36 files, +28,399 −22,639**.
Generated shader headers account for 23,407 of the added lines (≈82%):
`ShaderBytecode.h` +15,388 −14,896, `SdrShaderBytecode.h` +8,019 −7,533. Hand-written
core: `VideoRenderer.cpp` +1,107 −80, `PlayerSession.cpp` +777 −23,
`IamfAudioDecoder.cpp` +678 (new file).

## Verified on this machine

- Release build of `FFF.Native` + `FFF.Player` + `FFF.Player.Tests`: **0 errors, 0 warnings**.
  The build prints `3FP API 版本：内核=18 宿主发送=期望API版本 宿主校验=18 工程声明=18`.
- `ShaderBytecode.h` and `SdrShaderBytecode.h` are the generator's own output for the
  merged HLSL (fxc 10.0.28000.0). Re-running the generator after the five fixes reproduces
  both byte for byte, which is the proof that none of them touched HLSL.
- Kernel artefact: 1,350,656 B, sha256 `821da168c671d2c9…`.

## Build prerequisite (this bit me, so it is now stated)

`FFF.Player` references the UI library by hint path
`..\..\LakeUI\LakeUI\bin\Debug\…\LakeUI.dll`. LakeUI is a **sibling repository** that is
not vendored here and ships no binaries, so a fresh machine gets hundreds of
`undefined type LakeUI.*` errors that never name the cause. Build it first:

```powershell
dotnet build ..\LakeUI\LakeUI\LakeUI.vbproj -c Debug
```

`tools/构建3FP.ps1` now checks for that DLL before compiling and says so. IAMF needs one
more prepared dependency: `tools/准备Libiamf.ps1` (pinned commit, `/MD` static libs);
without it `FFF.Native.vcxproj` fails to link on `iamf.lib`.

## Not verified, left as claims rather than readings

- No HDR display here: fixes A and B are argued from code, not measured on a panel.
- No in-tree probe measures IAMF audio position, so D is arithmetic plus a clean build,
  not a listening test.
- K1 (window-0 normalisation denominator) stays open: the reference implementation is
  self-pinning and the normative text is not publicly available, so a conformant
  bitstream can read red against it.
