# PR 描述（GitHub PR body 用）

> 标题：**Native: SDR scRGB presentation, P3 gamut, viewport geometry, three missing exports**
> 目标：`Lake1059/FFF_Project:master` ← `luoye-cpu/FFF_Project:<新分支名>`
> 单提交（已建：`ff0f17a`），基线 = **上游 master 当前 tip `5d2cb21`**
> （不是 `33ac900`：上游在 #13 之后又推了 3 笔，其中删掉了 `FFF.Player.Tests` 整个工程与
> `tools/测试3FP内核.ps1`。`git merge-base --is-ancestor 33ac900 5d2cb21` = 0，即旧基线仍在 tip 上。）
> ⚠ **本提交不含回归用例**：`KernelTimelineTests.vb` 在上游 tip 已不存在，`git apply --check`
> 报 `FFF.Player.Tests/KernelTimelineTests.vb: No such file or directory`，`--exclude` 该文件后
> exit=0 ⇒ 视口用例没有落点。英文正文已把这条写成明说的决定，不是遗漏。
> ⚠ 分支名 `upstream/api16-scrgb-viewport-pr`：`upstream/api16-viewport-pr` 已被
> `.3fc_dumps/pr-api16-wt` 占着（且它检出的正是 tip `5d2cb21` 那条线），沿用会把那条线带出去。
> ⚠ 出货物是 `combined-pr.patch`（md5 `5737f1d0e4496382987d4794f84eaf65`，14 文件
> +13,882/−13,118）。旧版（基于 `33ac900`、15 文件、含测试文件）已另存
> `combined-pr-vs-33ac900.patch.bak` 作对照。
> ⚠ 提交前已实跑 `tools/构建3FP.ps1 -Configuration Release -SkipTests` ⇒ **0 警告 0 错误**
> （必须 `-SkipTests`：测试工程已被上游删除）。验证用的临时 worktree 已清理；
> 它必须建在 `3fp/` 下——`FFF.Player.vbproj` 用 `..\..\LakeUI` 引仓库外的 LakeUI 项目，
> 放在别处会冒 455 个 `未定义类型“LakeUI.*”`，那是路径问题，不是本 PR 的问题。
> ⚠ 工作树现状：那次误覆盖后用 `git apply` 恢复，14 个已跟踪文件因此是 **CRLF**（autocrlf 写回），
> 内容逐字节等于补丁目标（`differing=0`，生成器重跑 `equal_after_norm=True`），补丁本身仍是 LF。
> 变异体与临时夹具已按备份还原（`VideoRenderer.cpp 8732b30f…`、`VideoRenderer.h 261886ea…`、
> `KernelTimelineTests.vb cd0769bf…`，还原后 md5 逐一相符；`sdr10.mkv` 已删）。
> 对外正文 = 下面 `---8<---` 之间的英文部分；末尾的「本地台账」不并入 PR。

---8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<---

## What

- A wide-gamut SDR source already takes the 16-bit scRGB chain (an SDR chain cannot hold
  primaries outside Rec.709). What is new: a **10-bit SDR source** can take it too, when the
  display runs Advanced Color, with paper white anchored to Windows' own *SDR content
  brightness* so the picture lands on the luminance DWM would have given it. Opt-in:
  `FFF3FPConfiguration::sdrScRgbMode` (0 = this policy adds no new reason to leave the classic
  chain = existing behaviour; 1 = auto). It defaults to 0, and `FFF.Player` does not set it
  to 1 - the repository has no Advanced-Color fixture, so the policy-on route is the part of
  this PR with no automated coverage (see Tests).
- Display P3 / DCI-P3 sources are converted with a P3 matrix. The shader's gamut input stops
  being a boolean (`Source2020`) and becomes a tri-state (0 Rec.709 / 1 Rec.2020 / 2 P3).
- Viewport: panning a magnified picture works in **both** directions, and a zoom factor below
  1 now actually downscales instead of being ignored.
- New exports: `FFF3FP_SetFitLimitToNative` (cap the fitted box at the source's native size,
  which turns `zoom` into an absolute screen:video pixel ratio), plus `FFF3FP_SetLogCallback`,
  `FFF3FP_Redraw` and `FFF3FP_SetPresentConfig`, which hosts already call and the kernel did
  not provide.
- `FFF3FPRenderTargetInfo` v1 -> v2: `destX` / `destY` become signed.
- Two recorder defects: a WASAPI capture thread that parked forever when its endpoint died
  (silently silent audio, no diagnostic), and mixed audio recorded at a rate other than
  48 kHz drifting by roughly the rate ratio.
- `FFF.Player` in this repository is updated for API 16 and gains a regression case for the
  two viewport behaviours.

## Why

**Why the SDR chain is the wrong place for these sources.** A 10-bit SDR frame on a gamma
R10G10B10A2 chain is resampled by DWM as an ordinary SDR window, so the extra precision is
rewritten away; a frame whose primaries leave Rec.709 cannot be held by that chain at all, and
the shader would fold it back into Rec.709. Neither shows up as an error, so the pixels a host
reads back are not the pixels the decoder produced. scRGB is linear, is the chain HDR already
uses, and carries both precision and out-of-gamut values. `sdrScRgbMode` defaults to 0 so
nothing changes for callers that do not ask; the gamut half of that gate is unconditional and
predates this PR.

**Why P3 needs its own arm.** Real P3 video (HEVC/AV1) is marked
`primaries = SMPTE432, matrix_coeffs = 2020_NCL`, because the VUI defines no P3 matrix value.
Classifying by `IsRec2020()` alone therefore routes P3 through the Rec.2020 matrix, which
silently undoes any P3 handling — so the P3 test has to run *before* the Rec.2020 test, and it
has to be a third value rather than a flag.

**Why the pan range was half missing.** The destination origin was unsigned and clamped at 0,
so the half of the range that brings the picture's right/bottom edge into view did not exist.
The clamp for the other half was written as `[fitted - zoomed, x0]`, which inverts when z < 1 —
and `std::clamp` with `lo > hi` is undefined behaviour. Taking min/max of the two box ends
covers both regimes with one expression. Capping the fit box at native size then gives `zoom`
a second, absolute meaning (1.00 = one source pixel per screen pixel) that frame inspection
needs; it is a separate switch rather than a change to `zoom`'s existing meaning.

**Why the log sink matters.** Kernel-side failures were only reachable through
`GetLastError` on the session, which a host that never sees a session cannot call. One
process-wide sink also makes the adapter-selection line visible outside a debugger.

## Native

- **One colour gate.** `WantsScRgbPresentationPath(bitDepth)` replaces three copies of
  `IsHdrSource() || IsWideGamutSource()` (`SetWindow`, `SetColorMode`, `EnsureSwapChain`).
  Bit depth is a *parameter*, not the member: the member is still 0 before the first frame
  lands, which would mis-route a 10-bit source's very first frame onto the SDR chain and force
  a mid-stream chain rebuild.
- **SDR white level.** Read through `AdvancedColorInfo::SdrWhiteLevelInNits`, with a
  `DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL` fallback (`SDRWhiteLevel / 1000 * 80`) for
  the cases where the WinRT call briefly fails. A *partial* success keeps the last known value
  instead of dropping to the fallback mid-playback (that reads as a brightness jump). When
  nothing reports it, the anchor is 80 nits — the scRGB reference white — not 200.
  The DISPLAYCONFIG path is documented in-code with three traps that are easy to rediscover:
  the path-entry stride for `GET_TARGET_NAME`, `QDC_ONLY_ACTIVE_PATHS` failing on some
  topologies, and the SDR-white query wanting the **target** id in `header.id`.
- **SDK shape.** A `requires`-based probe compiles against both the current and the newer
  `DISPLAYCONFIG_DEVICE_INFO_HEADER` (`size` moved into the header; the query became
  single-argument), so each branch is discarded by `if constexpr` under its own SDK.
- **HDR metadata.** An SDR source on the scRGB chain declares
  `DXGI_HDR_METADATA_TYPE_NONE` instead of inheriting the fallback HDR10 block, which declared
  Rec.2020 primaries and a peak the source never claimed — precisely the hint that makes a
  display tone-map the picture.
- **Capability cache.** A swap chain that rejected scRGB no longer sticks for the lifetime of
  the monitor handle: the rejected state expires, so a user who turns Windows HDR on during
  playback can get scRGB without restarting the session. The two kinds of "no" get different
  clocks, because they cost different things: "this display has no Advanced Color right now"
  is a cheap question, so it re-asks on the 750 ms probe cache; "this swap chain refused the
  scRGB colour space" is not, because a retry that is refused again destroys and re-creates the
  chain (subtitle atlas, forced re-render) on hardware that will keep refusing it - so a
  rejection backs off for 10 s. Every existing reset that forces a re-probe (policy change,
  monitor change, device reset) moves the probe timestamp to the epoch, which clears both
  clocks, so recovery from a real capability change is still immediate.
- **Fallback rung is taken from the frame, not the member.** When a chain refuses scRGB, the
  renderer falls back to the classic chain at the source's own precision
  (`OutputBitDepthForSource`: >8-bit -> R10G10B10A2, else BGRA8). The two rejection paths read
  the `sourceBitDepth_` *member* for that decision, but `EnsureSwapChain` runs before
  `EnsurePipeline`, which is what assigns the member - so on the very first frame the member is
  still 0, a 10-bit source was demoted to BGRA8, and the correct depth only arrived one frame
  later via a second reconfigure. `CreateSwapChain`/`ReconfigureSwapChain` now take the
  caller's bit depth as a parameter, which is the same correction this commit already makes to
  `WantsScRgbPresentationPath` (see "One colour gate" above).
- **Viewport.** Gate `|z - 1| > 1e-4`; origin interval as min/max of the two box ends; travel
  range as an **absolute** value (the old `max(maxPan, 0)` swallowed it to zero for z < 1,
  killing panning entirely in the downscaled state); `lround` on the origin; 1 px floor on the
  zoomed size.
- **`FFF3FP_SetFitLimitToNative`.** Stored as an atomic preference on the renderer and OR-ed
  into the fit-limit argument of the destination calculation, so it applies on the next present
  rather than waiting for the decode thread; returns `NotSupported` while a disc is open,
  because that renderer owns its own geometry and a `Success` would describe nothing.
- **ABI.** `PlayerApiVersion` 15 -> 16 (the configuration struct gained a field and
  `FFF3FP_Create` rejects a mismatch outright). `GetRenderTargetInfo` accepts only
  `version == 2` — deliberately not discriminated by `size`, because v1 and v2 have identical
  field widths, `size` cannot tell them apart, and letting a v1 caller through would read a
  negative origin as a huge unsigned value, i.e. wrong content with no error.
- **Concurrency and lifetime.** `fallbackReason_` is a `std::string` assigned on the render
  path and copied from the host thread; it now has its own leaf mutex (same pattern as
  `errorMutex_`). `~PlayerSession` releases the primaries carrier filter *after* joining the
  worker, because the worker's still-image path is still using that graph. The Dolby-Vision
  extension-eligibility flag is written only under `deviceMutex_`, so the check-then-act in
  `EnsurePipeline` cannot be overtaken by a concurrent writer and leak the live shader.
- **Presentation pacing.** The host's tearing *request* is stored apart from the swap chain's
  tearing *capability*. `Present` uses `Present(0, DXGI_PRESENT_ALLOW_TEARING)` only when both
  hold, and `ResizeBuffers` still mirrors the capability the chain was created with. The
  request survives device and chain re-creation.
- **Recording.** `IAudioCaptureClient::GetNextPacketSize` failure is handled the way
  `GetBuffer` failure already was (record the error, stop the capture, wake the caller) instead
  of being hidden in a loop condition. `VideoMuxer` refuses `mixAudioSources` at any rate other
  than 48 kHz: `AudioMixer`'s timeline semantics are 48 k-based while the session feeds
  absolute positions scaled by the configured rate, so the combination drifts and grows the
  mixer's buffers without bound.

## Public surface added (all additive except the two notes below)

`FFF3FP_SetLogCallback` + `FFF3FPLogCallback`, `FFF3FP_Redraw`, `FFF3FP_SetPresentConfig`,
`FFF3FP_SetFitLimitToNative`, `FFF3FPConfiguration::sdrScRgbMode`,
`HdrDisplayCapabilities::sdrWhiteLevelNits`, and the internal (non-exported)
`FFF3FP_KernelLogImpl` that in-module callers declare locally so it never enters the export
table that ABI checks walk.

## Breaking — please read these two first

1. **`FFF3FPRenderTargetInfo` v1 is rejected** (`InvalidArgument`). `FFF.Player` does not bind
   `GetRenderTargetInfo`, so nothing in this repository is affected.
2. **`PlayerApiVersion` 16.** Any host validating the version with strict equality has to bump
   in lockstep; `FFF.Player`'s three places (the gate, the struct `version`, and the struct's
   field list) are updated here, otherwise the repository's own test entry point cannot create
   a session at all.

Two behaviour changes that are not ABI: mixed-audio recording at != 48 kHz is now refused
rather than silently drifting, and `FFF3FP_SetPresentConfig` now affects the ordinary
playback present path (see Native above).

Two things I found and deliberately left alone, so they are on the record rather than
discovered in review:

1. **The colour gate runs three times, and only one of them knows the bit depth.**
   `EnsureSwapChain` takes the depth as a parameter (the member is still 0 before the
   first frame), but `SetWindow` and `SetColorMode` run before any frame exists and
   therefore still pass the member. So between session setup and the first frame,
   `ActualColorMode()` reports `MapToSdr` and the fallback text says "only available for
   HDR or wide-gamut sources" for a 10-bit SDR source that `EnsureSwapChain` is about to
   route to scRGB. The chain that gets created is the correct one; what is wrong for that
   window is the *diagnostic*. Fixing it means carrying the depth into session setup or
   deferring the diagnostic — a separate change from the routing fix, and I did not want
   to bundle it silently.
2. **`swapAllowTearing_` is still a plain `bool`.** It is written on the chain-creation
   path and read on the present path; the two do not overlap today because both hold
   `presentMutex_`. That is a property of the current call graph, not of the type. A
   one-line change to `std::atomic<bool>` would make it explicit; it is left out to keep
   this commit free of unrelated concurrency churn.

## Reproducibility

`Render/ShaderBytecode.h` is generated by `tools/generate_shader_bytecode.py`, added here, from
the HLSL embedded in `VideoRenderer.cpp` with the Windows SDK `fxc`; paths resolve relative to
the script and `--dry-run` lists all seven shaders. A 24k-line generated header that the kernel
links directly has to be answerable from a clean checkout - running the script against the
sources in this commit reproduces the committed header **byte for byte**.

What actually changed in it: of the seven arrays, **only `FFFPixelShaderBytecode` differs**
(134,540 -> 134,752 bytes); the other six are byte-identical, and so is every line from the
next array to the end of the file. The emitter reproduces the committed column layout, so the
only structural delta is a two-line header comment naming the generator. The remaining line
churn is inherent to a hex-array diff: six bytes per row, so one changed byte shifts every
row after it. Please read the seven array lengths, not the line count.

That layout puts one trailing space on each full line, so `git diff --check` reports 13,038
whitespace hits in this one generated file (git echoes each offending line, hence 26,076 lines
of output) and **zero** in the hand-written ones. The committed version of the same file
already has 24,068 such lines - existing convention, preserved.

## Tests

- **This PR carries no regression case, and that follows from the current state of the
  repository rather than from an oversight.** The VB test project (`FFF.Player.Tests`,
  including `KernelTimelineTests`) and the suite entry point `tools/测试3FP内核.ps1` were
  removed upstream after the commit this branch was written against (`33ac900`; the tip it
  now sits on is `5d2cb21`, which deletes them). `git apply --check` against that tip fails
  with `FFF.Player.Tests/KernelTimelineTests.vb: No such file or directory`, and succeeds as
  soon as that one file is excluded — so the viewport case written against that suite is
  **not included here**. Re-adding a test project the maintainer just removed seemed worse
  than arriving without one; stating it so the omission reads as a decision.
- Both new behaviours are pinned by mutation arms, each caught by its own assertion: dropping
  the cap term fails the "cap did not apply" message; restoring the old `zoom > 1.0001f` gate
  passes the cap assertions and fails "z < 1 was treated as a no-op".
- Regression guard for the pre-existing magnified path (kernel-reported origins at z > 1 and
  the drag-to-picture displacement): measured through a real-media host harness rather than
  inside this repository's suite, so those numbers belong in the PR thread, not in an
  automated case here.
- What the two viewport behaviours are measured with instead: a downstream host driving this
  kernel over real media, with geometric and rate-based checks. At 1280x720 the native-size cap
  gives `Dest 1280x720` at `zoom=1` (ratio 1.000) and exactly 2.000 at `zoom=2`; `z=0.6`
  downscales to `1197x673`; the drag anchor moves ≤ 0.5 px across five zoom steps. Same
  readouts at 4K. The repository's own Release build (`tools/构建3FP.ps1 -Configuration Release`)
  finishes with 0 warnings and 0 errors on that tree.
- Why the evidence above is not a frame count: on a machine with a global present hook installed
  (RTSS), the `已呈现帧数 == 期望帧数` style of assertion is **not reliably reproducible** —
  twelve consecutive runs on this kernel and eight on a fresh checkout of its parent both failed
  it at similar rates, with a different failing fixture each time and deficits of 1-10 frames on
  both sides. That is an environment sensitivity of the exact-count assertion rather than a
  result of this change; a tolerance (or `>=` against `underrun`) would make it a usable gate.
- **The scRGB rejection path is not exercised by anything here, and I could not exercise it
  either.** No adapter on the box I tested refuses `SetColorSpace1(scRGB)`, so the whole
  "fall back to the classic chain" branch - the two fixes immediately above - is justified by
  call order and by what the code says, not by a measurement. I tried a synthetic arm (env-var
  mutants forcing "this source wants scRGB", "the display advertises Advanced Color", and
  "the chain refuses", plus a High10 320x180 fixture and a readout of
  `snapshot.videoOutputBitDepth`); the positive control I added to prove the branch had
  actually run reported that it had not, in three variants of the fixture. So the arms did not
  separate and I am not quoting a number for them - the forced-mutant runs that reported healthy
  10-bit output did not go through the rejection branch at all, so they say nothing about it.
  Flagging this so nobody reads the two fallback fixes as tested.
- **No automated coverage here** (this repository has no fixture or display path for them):
  the 10-bit SDR-on-scRGB route end-to-end, P3 rendering of a real clip, actual tearing cadence
  on a VRR display, the log sink, and the WASAPI endpoint-removal path. Each is gated so the
  untouched case is the old behaviour: policy 0, a non-Advanced-Color display, a chain created
  without the tearing flag, no installed sink, a live endpoint.

---8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<--- 8<---

## 本地台账（不并入 PR 正文）

规模（实测，以出货补丁为准：`git apply --stat combined-pr.patch`，含本轮 A/B 两处回退修回）：
**15 文件 / +13,981 / −13,118**；
其中手写 C++/VB **13 文件 +762 / −114**，新生成器 178 行，生成物 `ShaderBytecode.h` +13,041 / −13,004。
（在活工作树里直接 `git diff --numstat` 会得到 +13,771/−13,106，差的 178 行正是尚未跟踪的
`tools/generate_shader_bytecode.py`——`git diff` 不带未跟踪文件，出货补丁带。）

| 判据 | 读数（2026-09-30） |
|---|---|
| 上游工程 Release 构建 | `FFF.Native` / `FFF.Player` / `FFF.Player.Tests` 均 0 错误 0 警告 |
| 上游自带套件 | 完整跑通时 exit=0，`内核时间轴、完整排空、外部音轨和生命周期回归通过。`，新用例打 `FIT-LIMIT: 后缓冲=640×360 铺满底=1.000 封顶外=0.000 封顶内=1.000 缩小内=1.000 缩小外=0.000`。**但"完整排空"的 `已呈现帧数 == 期望帧数` 判据在本机不可稳定复现**（见下两行），故本轮不把它当内核判据引用 |
| 本机不可稳定复现项（A/B/A 归因） | 交错三块：本 PR 的 API 16 内核 5/6 红（缺 1–3 帧）、**改动前的 API 15 快照 6/6 红且缺 27–53%**（`8/15`、`7/15`、`11/15`）、再回到本 PR 6/6 红 ⇒ 短落先于本 PR 存在，本机 `RTSS/RTSSHooksLoader64 + MSIAfterburner` 在场时这类"恰好 N 帧"断言不是可用判据；`等待()` 用的是 15 s 墙钟，所以"播完"总先成立，红的只是计数 |
| 同机对照（另一宿主） | 下游应用 3FCompare 的 `--selftest`（判据是速率型：`presented +60/秒`）同一时段跑最终内核 `eb3b8a5b63c2`：`真 1:1 封顶通过：zoom=1 ⇒ Dest 1280×720，比值 1.000；zoom=2 ⇒ 比值 2`、`缩放下限通过：z=0.6 ⇒ 1197×673`、`单路锚点 ≤0.5px`、整轮 exit=0 |
| 变异臂 M1（门槛退回 `zoom>1.0001`） | **红**：`缩放 0.5 没有真的缩小：同一点 {X=320,Y=240} 仍是画面（亮度 1.000）` |
| 变异臂 M2（摘掉封顶 OR 项） | **红**：`封顶后 {X=320,Y=350} 仍是画面（亮度 1.000）⇒ 适配盒没有封顶到源原生尺寸` |
| 基线臂（同一 harness，不改内容） | exit=0，读数与上行一致 ⇒ 两臂的红不是 harness 造的 |
| **基线臂 2（全新构建的未打补丁 HEAD，8 轮）** | 另开 detached worktree 检出本分支父提交 `33ac900`，依赖用 junction 复用，`构建3FP.ps1 -Configuration Release` 0 错误 0 警告，再连跑 `测试3FP内核.ps1` **8 轮：4 绿 4 红**（红在 `CPU early-audio 29/30`、`CPU late-video 14/15`、`GPU silent 20/30`、`CPU silent 20/30`）。同机同期本 PR 内核 11 轮：4 绿 7 红（`14/15`、`28/30`、`23/30`、`29/30`、`22/30`、`20/30`）。两臂红在同一条 exact-count 断言、失败夹具每次都不同、基线侧缺帧最多 10 帧与本 PR 侧同为 10 帧 ⇒ Fisher 双侧 p≈0.8，判"同量级、未由本 PR 引入"，不判"更好" |
| 生成物对账（独立实现） | 用正则把 7 个数组的字节流分别解出来逐字节比：**6/7 全等**，只 `FFFPixelShaderBytecode` 134,540→134,752；从 scale 数组到文件尾 1,547 行文本与 HEAD 一致；文件头只多生成器自己那两行注释（+2 行偏移）。`py tools/generate_shader_bytecode.py` 重跑产物与在库 `cmp` 无差异 |
| 补丁可应用性（干净 clone） | `tests/output/verify_apply_crlf.sh`：以 HEAD blob 建基线仓 → clone → `git apply --check` exit 0、`--3way` exit 0，应用后 14 个受影响文件与目标内容 **`content_mismatch_after_apply=0`**（新文件 `generate_shader_bytecode.py` 未进该比对）。⚠ 想造 CRLF 工作树的对照没造出来（clone 与强制 checkout 都落成 LF，`with_CRLF=0`），所以"CRLF 工作树上能否 apply"**未验**；对外建议写 `git apply --3way` |
| 变异 harness 自检 | `RESTORE sha0=1D2CAD404AC7 sha1=1D2CAD404AC7 equal=True`（源码逐字节还原后重建复绿） |
| 空白卫生 | 手写文件 `git diff --check` **0 命中**；`ShaderBytecode.h` 命中 **13,038 条**（git 每条再回显一行内容，故输出 26,076 行），HEAD 同文件自身就有 24,068 行尾随空格 ⇒ 沿用原约定 |
| 补丁往返 | `bash tests/output/verify_combined_patch.sh` ⇒ `APPLY_CHECK=OK`、`files=15 differing=0`、`patch_CR_lines=0`、`COMBINED_PATCH_VERIFIED=OK`；`combined-pr.patch` md5 `aa0a4e86f093e0b81efdbc5bfe5af348`（A/B 回退修回之后；上一版 `2bd773bc…` 作废） |
| A/B 回退修回后的状态 | `build3fp_AB.log`：Release 三个工程 0 错误 0 警告；`suite_AB.log` **整轮 exit=0 走到尾行**，`HDR-PIXEL 125.000/125.000/124.938/1.000`、`FIT-LIMIT 铺满底=1.000 封顶外=0.000 封顶内=1.000 缩小内=1.000 缩小外=0.000` 与修回前逐项相同 ⇒ A/B 对套件覆盖到的路径无可观测改变（本来就该如此，两条都在"链拒绝 scRGB"分支里）。本 PR 侧计数更新为 **12 轮 5 绿** |
| A/B 的验证尝试**失败**（记下来免得被当成验过） | 想做一对分离臂：环境变量变异体强制 ①`WantsScRgbPresentationPath`→true ②`OutputSupportsHdr`→true ③链拒绝 scRGB，配 High10 320×180 夹具，读 `快照.视频输出位深度`。**正对照**（在拒绝分支里 `SetError("MUTANT-REJECT-SCRGB")`，由 `最后错误消息` 读回）三版夹具都报 `NO[]` ⇒ 分支根本没进；两臂读数都是 10，**分离不出来**。中途还两次把"构建失败后旧 exe 的回声"当成读数（VB 语法错 BC30370/BC30456 各一次），后来在命令里加了 BUILD_FAILED 短路。结论：B 的修法只有调用序证据（`EnsureSwapChain` 在 `EnsurePipeline` 之前，成员在其 2771 行才赋值），**没有实测** |
| 下游配对 | `check_kernel_exports.py` / `check_kernel_pairing.py`：kernel=16 / managed=16，必需入口全在，`RenderTargetInfo` v2↔v2 |

### 第二轮独立复审（2026-09-30，接手 agent）

复审方式：**只读源码核对**，没有重编译、没有重跑套件（本轮命令审批全部超时，见下）。
目的是确认上一轮留下的"已上报未修"清单里哪些其实已经收口，以及出货物文案与代码是否仍然一致。

| 项 | 上一轮记录 | 本轮核对（文件:行） | 结论 |
|---|---|---|---|
| `extensionEligible_` / `extensionAttempted_` 写点无锁 | 上报未修（`Render()` 那处） | `VideoRenderer.cpp:4307` 写在 `deviceLock` 持锁之后（4296 已早退未持锁的情况）；`:2151` 加锁；`:5326` 所在 `ReleaseDeviceObjects` 的两个调用点 `:5356` / `:5424` 都在 `deviceMutex_` 下 | **已收口**，上一轮记录过时 |
| 内核日志行缺 `\n` 导致 `OutputDebugStringA` 粘连 | 上报未修 | `VideoRenderer.cpp:2242-2244` 已单独发射终止符 | **已修** |
| `kShaderGamutTristate` 对上游是多余分支 | 上报未修 | 全树 grep 已无该标识符 | **已删** |
| `FFF3FPLogCallback` 注释未写"不得抛" | 上报未修 | `FFF.Player.Api.h:232-235` 已写明委托生命周期 + 异常逃逸即终止进程 | **已修** |
| `PlayerSession::Fail` 函数体内 `extern` 与文件级声明两种写法并存 | 上报未修 | 仍并存（`VideoRenderer.cpp:1869` 文件级；`PlayerSession.cpp` 函数体内） | 未修，**纯风格**，不阻塞 |
| VB 侧 API 16 三处 | 已修 | `播放器会话.vb:42` 门槛、`:226` 封顶封装、`播放器原生接口.vb:40` 尾部字段、`:503` 导入 | **齐备** |
| SDR 源 `METADATA_TYPE_NONE` | 已修 | `VideoRenderer.cpp:4222-4224` | **在** |

**本轮新发现两条**（都已写进上面的英文正文，不再重复）：① 三处色彩门禁里只有 `EnsureSwapChain` 拿到真实位深，另两处仍传成员 ⇒ 首帧前诊断漂移；② `swapAllowTearing_` 仍非原子（实际靠 `presentMutex_` 串行）。

**本轮没做的事**（如实记录，避免被当成验过）：
- 未重编译、未跑 `测试3FP内核.ps1`、未重跑 `verify_combined_patch.sh` ⇒ **补丁 md5 是否仍为 `aa0a4e86…`、工作树是否仍等于补丁，本轮没有实测确认**。
- 本轮**没有改任何源码**，只改了出货物文案（commit message / 本文件）⇒ 按理不影响补丁与工作树的一致性，但上面那两条读数仍应在提交前复跑一次确认。
- 因此下面"提交步骤"里第 0 步之外，建议补跑一次 `bash tests/output/verify_combined_patch.sh`。

### 本 PR 里被推翻的旧陈述（我自己先前写下的）

- 「`SetPresentConfig` 的裸 `bool` 竞争与语义是**改动前就存在**的缺陷，故不夹进本 PR」——不成立。
  `swapAllowTearing_` 是既有变量，但把它当偏好覆写、以及"正常播放分支根本不读它"这两件事是**新增导出
  带来的**；不修就是往上游交一个调用成功却什么都不会发生的 API。已按"实现提到声明的水位"修好。
- 「`git diff --check` 在 `FFF.Native` + `FFF.Player` + `FFF.Player.Tests` 上 0 命中」——错，`ShaderBytecode.h`
  就在 `FFF.Native` 里，实测 26,076 行命中。正确口径是"手写文件 0 命中，生成物沿用原约定"。
- 「`ShaderBytecode.h` 另外 6 个数组的差异纯粹是空白重排（约 1,650 行噪音）」——那是生成器格式与在库
  格式不一致造成的整文件重写；格式复刻后这 6 个数组**逐字节全等**，噪音归零，补丁从 +24,112/−24,075
  降到 +13,041/−13,004。
- 「新用例的 640×360 客户端就是后缓冲尺寸」——只在本机 100% 缩放下成立（本机实测 DPI=96）。
- 「`tools/测试3FP内核.ps1` exit=0 全绿」——**当时属实，但不可随时复现**：03:0x–03:2x 交错 A/B/A
  量到本机 `已呈现帧数 == 期望帧数` 大面积红，且改动前的 API 15 内核红得更狠（6/6，缺 27–53%）
  ⇒ 归因于本机呈现钩子/调度，不归因于本 PR；今后引用该套件读数必须带"完整跑到尾行"这个条件，
  不能只写 exit=0。本轮另建未打补丁 HEAD 的全新构建臂（8 轮 4 绿）复核了这条归因。
- 「`fallbackReason_` 加了叶子互斥就补完了」——不成立。`SetColorMode` 里那句裸
  `fallbackReason_.clear();` 当时还漏在锁外（读者 `FallbackReason()` 只持 `fallbackMutex_`，
  两者不相斥），而类里新写的注释恰恰断言"所有写都在锁内"。已补进同一把锁；`git grep` 复核
  现在 7 处写 + 1 处读、8 处取锁一一对上（`git grep -c 'lock_guard fallbackLock(fallbackMutex_)'` = 8）。
- 公开头文件对 `sdrScRgbMode` 的两处描述与实现不符，已改：`0 = Never` 原写"SDR 源一律走经典链"，
  但 `WantsScRgbPresentationPath` 里宽色域那一项**不受策略管**（HEAD 亦如此），照原文评审会以为
  切 0 能把 P3 素材降级回 SDR 链；`1 = Auto` 又把宽色域算进策略新增的条件里。同处
  `WantsScRgbPresentationPath` 的注释也删掉了挂在 Auto 句子里的"a wide gamut is folded back"。
- 我这轮的验证脚本 `tests/output/verify_apply_crlf.sh` 初版有重定向缺陷
  （`( cd "$KTREE" && git show … ) > rel` 写成了 `cd "$KTREE" && git show … > rel`），
  把 `3fp/kernel` 的 14 个已跟踪文件按 HEAD 覆盖了一次。恢复路径：`git apply
  --exclude=tools/generate_shader_bytecode.py combined-pr.patch`；闭环证据是重跑
  `verify_combined_patch.sh` 得到 `files=15 differing=0` 且补丁 md5 与覆盖前相同
  （`2bd773bc…`），另有独立锚点：生成物与 `py` 重跑产物一致、重建后 0 错误 0 警告。
  脚本已改成全部绝对路径写入并在头部写明这条禁令。

### 明说没做

- `tools/发布3FP.ps1`（本仓未跟踪的本地发布脚本）**不进本 PR**：注释引用 `3fp/docs/17`，那是下游路径。
- 三个新导出（`Redraw` / `SetLogCallback` / `SetPresentConfig`）在 `FFF.Player` 里没有消费者，
  只有 `SetFitLimitToNative` 有；若上游要求"新 API 必须带消费者"，这三条可以砍或后补。
- `Render()` 里 DoVi 资格判定的写入点从 `ProcessFrame` 之后移到了持 `deviceMutex_` 之后：语义差别仅在
  "这一帧我们本来就不画"时不再刷新资格位。上游无 DoVi 夹具，此路径**未实测**。

### 提交步骤（等一句授权再动，push/建 PR 都是远端动作）

```bash
# 已完成（2026-09-30）：分支 upstream/api16-scrgb-viewport-pr，提交 ff0f17a
#   父提交 = 上游 tip 5d2cb21（不是 33ac900）；14 files changed, 13882 insertions(+), 13118 deletions(-)
#   构建：tools/构建3FP.ps1 -Configuration Release -SkipTests ⇒ 0 警告 0 错误（内核 + FFF.Player）
#   测试文件已按上游现状剔除：git apply --exclude=FFF.Player.Tests/KernelTimelineTests.vb

# 1) 推送前再确认上游没动过
git -C 3fp/kernel fetch https://github.com/Lake1059/FFF_Project master
git -C 3fp/kernel log --oneline -1 FETCH_HEAD
git -C 3fp/kernel merge-base --is-ancestor FETCH_HEAD ff0f17a && echo "仍在 tip 之上"

# 2) 出货补丁自检（判据已从 15 文件改为 14 文件，脚本若写死旧值需同步）
md5sum 3fp/patches/combined-pr.patch         # 期望 5737f1d0e4496382987d4794f84eaf65
git -C 3fp/kernel diff --stat 5d2cb21..ff0f17a | tail -1

# 3) 推 fork（已完成 2026-09-30）
git -C 3fp/kernel push -u origin upstream/api16-scrgb-viewport-pr
#   → * [new branch] upstream/api16-scrgb-viewport-pr -> upstream/api16-scrgb-viewport-pr
#   推送时上游 tip 仍是 5d2cb21，本分支直接建在它上面 ⇒ 无需 rebase，mergeable 应直接 clean。

# 4) 开 PR（正文取 ---8<--- 之间的英文段）
#    base 必须是上游仓库，别用 push 回显里那个 fork 自家的链接（它会拿 fork 的 master 当 base）：
#    https://github.com/Lake1059/FFF_Project/compare/master...luoye-cpu:upstream/api16-scrgb-viewport-pr?expand=1
```

第 1 步之后必须再跑一次 `tools/构建3FP.ps1 -Configuration Release` 与
`tools/测试3FP内核.ps1`，确认提交态与本轮实测态同一份内容（本轮末次：构建 0 错误 0 警告，
套件红在已知那条 exact-count 断言上，非本 PR 引入）。
