# HDR10+ / Vivid 上游 PR 包（2026-10-09）— 分支已建、文本已写，**推送与开 PR 由你手动做**

> ⛔ **本件已被取代**（同日 11:xx）：用户改要「一个 PR 装下全部需提交内容」⇒ 唯一交付件是
> [`hdr10plus-pr-all-2026-10-09.md`](hdr10plus-pr-all-2026-10-09.md)（一笔 `1db27cf`，base `3940990`，
> 无回归链已把本件的 6/7 比对换成 **244/244 全树 blob 等值** + 产物上重跑的七条）。
> 本件的两处已知弱点也正因此消掉：1B/1C **各自没单独编过**，且 1A/1B/1C 按上游 master 开 PR 会把本地台账 `PATCHES.md` 一起提交上去。
> 保留本件只为留痕，不要照它操作。

三笔分支建在**一次性克隆** `C:\PLAN\3FCompare\_3fp-fix\tp-pr` 里（活树 `third_party/fff_project` 一个字节没动、索引没动、没提交）。

| 分支 | 基 | 提交 | 内容 |
|---|---|---|---|
| `upstream/hdr10plus-pr` | `3940990` | `57c911d` | 特性五笔压成一笔（`e0a7d1e..d2f5826`：ST 2094-40/30 解析 → 动态曲线接进 SDR 映射 → 逐帧 DXGI metadata → 注入 API → 多窗口 + cross-window blend） |
| `upstream/hdr10plus-fixes` | 上面那笔之上 | `fdae4f0` | 本轮语义修复 + API 契约（7 文件中的 6 个 + `HdrProcessor.cpp` 的 8 个注入 hunk） |
| `upstream/vivid-hlg-baseline` | `3940990`（**独立于特性**） | `b825761` | HLG 基线门控（`HdrProcessor.cpp` 的 2 个 Vivid hunk） |

**拆分无损已证**：把 1B 与 1C 的改动合起来，与活树 7 个文件逐字节比对 **6/7 全等**；唯一不等的 `HdrDynamicMetadata.h` 是克隆检出时 `core.autocrlf=true` 的 CRLF smudge（克隆侧 124 个 CR、活树 0 个），**忽略行尾后逐字节相等** ⇒ 内容零丢失。

**未验（别当已验）**：只验过**合并态**能编（`A7399C7E24B925A0`，0 error / 0 warning，四路回归全过）。1B、1C **各自单独**是否可编译**没编过**——上游若分开评审，请在各自分支各编一次再提。

---

## 推送 + 开 PR（你手动）

```bash
cd /c/PLAN/3FCompare/_3fp-fix/tp-pr
git remote -v                      # origin 指向活树本地路径；要推 GitHub 得先加 fork
git remote add fork https://github.com/luoye-cpu/FFF_Project.git
git push -u fork upstream/vivid-hlg-baseline      # 建议先提这笔：最小、独立
git push -u fork upstream/hdr10plus-pr
git push -u fork upstream/hdr10plus-fixes
```
本机**没有 `gh`**，开 PR 只能在网页上做；下面的标题/正文直接粘。

---

## PR 文本

### ① `upstream/vivid-hlg-baseline`（最小、可独立合并）
**标题** `HDR: stop reading a Vivid HLG baseline through the PQ absolute domain`

> `maximum_maxrgb` is a maxRGB statistic normalised into `[0,1]` on a 1/4095 grid and the
> FFmpeg header documents no unit for it. Multiplying by 10000 is only self-consistent for a
> **PQ** baseline, where `PQ EOTF(1.0) == 10000 cd/m²` and the value sits near full code. For an
> **HLG** baseline the signal is relative/scene-referred — a full-code HLG signal means
> ~1000 cd/m² on the nominal BT.2100 reference display — so the absolute reading inflated
> `sourcePeakNits` by ~10x and blew out highlights.
>
> The gate reads `streamState_.format` (assigned, never OR-ed) rather than the
> `compatibility` bit set (which is accumulating and inherited), and it is captured before the
> Vivid branch relabels `next.format`, so a genuinely PQ-baseline stream cannot be pinned to the
> HLG branch.
>
> Measured, same probe both sides: HLG baseline `peak 10000 -> 4000`; HLG with no static
> mastering metadata `peak = 1000`; **PQ baseline with `maximum_maxrgb = 2048/4095` still
> `5001` = round(2048/4095 × 10000)** — i.e. the absolute path is untouched where it is valid.
> HDR10+ (`peak` in the 300–430 band) and SDR (`peak=100 outBits=8`) unchanged.

### ② `upstream/hdr10plus-pr`（特性主体）
**标题** `HDR: parse and apply ST 2094-40 dynamic metadata, with an injection API`
> 正文用现有 5 笔的信息（`git show <sha>` 逐条已在仓库日志里），强调三点：
> 解析是行为中性的（`behaviour-neutral`）；映射只接进 **SDR** 通路（scRGB 通路交线性光给显示，
> 曲线会二次映射）；注入 API 是为"素材不带 SEI 时也要能验映射"这一条存在的。

### ③ `upstream/hdr10plus-fixes`（语义修复，依赖 ②）
**标题** `HDR: key injection on the frame index, apply it while paused, own the peak`

> Three defects in the injection API, each found by running the API against a real
> multi-frame HDR10+ stream (the earlier six "failures" were a **fixture** artefact: the FATE
> vector decodes exactly one frame, so nothing reached `ProcessFrame` after a table was installed).
>
> 1. **Frame key.** `frame->pts` was used where `FFF.Player.Api.h` documents
>    `FFF3FPSnapshot::frameIndex`. With a 1/15360 time base at 60 fps, frame 4 already carries
>    `pts = 1024`, so every sparse scene list resolved to its last entry. Measured: entries
>    authored for `frameIndex < 30 / 30..59 / >= 60` all read `targeted = 333` before; after, the
>    same probe reads `111 / 222 / 333` with the flip landing exactly at 30.
> 2. **Paused / end-of-stream.** `injectedActive_` was only written inside `ProcessFrame`, so
>    installing a table when no further frame arrives returned `Success` while changing nothing.
>    The table is now re-evaluated against the cached frame and the shader inputs are refreshed
>    **on the present path** (`DrawWithShader` consumes a dirty flag) rather than on the calling
>    thread, so `cachedVideoSettings_` and the constant buffers are never touched outside the
>    device lock. Pixel-level before/after on a paused session: `0/5 samples moved` ->
>    **`3/5 moved, and 5/5 back to baseline after clear`** (the two that stay put are pure black,
>    which no curve can move).
> 3. **Peak ownership.** An injected entry without a usable `maxScl` kept the peak this frame's
>    bitstream parse had written, so the state was a hybrid — curve injected, peak from the
>    stream — while the source still reported `Injected`. Injected entries now own the peak:
>    theirs when present, otherwise the stream-level baseline; never this frame's dynamic value.
>
> `FFF.Player.Api.h` also states the three contracts explicitly now (frame-index matching, the
> present-path visibility requirement incl. "a cached frame and a window are required", and peak
> ownership). The per-frame transfer decision was factored into `ResolveDecodeTransfer` and is
> shared with the refresh path, so the two cannot diverge.
>
> Not claimed: the 15 assertion suites here are ours; no host application calls this API yet, so
> treat it as kernel-level.

---

## 参考读数（全部当场从盘上日志复算，件在 `_3fp-fix/`）

| 判据 | 值 | 日志 |
|---|---|---|
| 合并态构建 | `A7399C7E24B925A0`，805,888 B，0 error / 0 warning | `_3fp-fix/tp-vivid-port.log` |
| Vivid 规范夹具 | `hdrFormat=5 hdrPath=6 dyn=1 peak=4000 outBits=10` | `_3fp-fix/port-vivid-canon.log` |
| HLG + 无静态 | `peak=1000` | `port-vivid-hlg-nostatic.log` |
| HLG + 无静态 + maxrgb 2048 | 仍 `peak=1000` | `port-vivid-hlg-nostatic-maxrgb2048.log` |
| **PQ + maxrgb 2048** | `peak=5001`（×10000 路未被切掉） | `port-vivid-pq-maxrgb2048.log` |
| HDR10+ / SDR | `fmt=2 path=2 dyn=1 peak=314` / `fmt=0 peak=100 outBits=8` | `port-hdr10plus.log`、`port-sdr.log` |
| 注入套件 | 真夹具 `=== PASS (0 failures) ===`；FATE 单帧夹具 `GUARD FAIL` exit 8 | `_3fp-fix/run-after-tmp` 两轮 |

⚠ **复跑前置**：`run-after-tmp/` 里那份被测 `FFF.Native.dll`（`A7399C7E24B925A0`）已按 lane N 的重复件账清理，
字节留存于 `_3fp-fix/artifacts/FFF.Native.vividport-A7399C7E24B925A0.dll`；四个探针 exe 仍在旧址。
⇒ 重跑注入/像素判据需先把那份 artifacts 拷进 `run-after-tmp/`（同哈希，读数字节级可复现）。

**出货输入现状**：`third_party/fff_project/.../x64/Release/FFF.Native.dll` 已还原为基线
`E5A7ADCB53B8DDB9`（803,328 B @ 10-09 09:41:31），与 `.3fc_kernel_build.json` 的 `dllSha256` 逐字符相符；
活树仍是 7 个未提交文件（未 commit、未 push）。

---

## 本包**不含**的那一条修复（以及为什么不含）

| 项 | 处置 |
|---|---|
| layout-0 自适应缩放的**真裁切**（41 点判据：改前 34/41 平均 0.8143 max 1.0 → 改后 4/41 平均 0.0035 max 0.082；control 臂逐点全等） | **不在上面三个分支里**。修法在 `3fp/kernel`（实验树）的 `VideoRenderer.cpp`，补丁 `3fp/patches/RD3-layout0-adaptive-crop.patch`（正/反向均验过逐字节），账本 `3fp/docs/76` |
| 为什么不能单独提上游 PR | 自适应政策本身是实验树本地提交 `cedb7bb`（2026-10-06），`git branch -r --contains cedb7bb` **输出为空** ⇒ 该特性在**任何远端都不存在**，上游没有可以承接这个 fix 的对象。⇒ 等 `feature/vsr-and-rotation` 提案时**合进 `cedb7bb` 那一笔**（D10：一笔一 PR），不单独开 PR |
| 出货树要不要跟着改 | **不要**：`third_party/fff_project/FFF.Native/3FP/` 全树搜 `adaptive` 0 命中 ⇒ 无可达性。若日后把政策带进 TP，必须**同带四刀**（几何口径同源），锚点见 `docs/76 §六`——只带政策不带四刀，TP 立刻复刻这个裁切 |

