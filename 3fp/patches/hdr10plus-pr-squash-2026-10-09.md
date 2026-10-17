# HDR10+ 上游 PR · 切分与提交方案（2026-10-09）

> 状态：**方案，未执行**。§五 的每条 git 命令都要用户点头后才跑（本仓纪律 D10/D11 + "改动不自动提交"）。
> 范围：内核工程 `third_party/fff_project`（`3fcompare/hdr10plus` @`d2f5826`）。**不涉及 3FCompare 宿主**。

---

## 一、这笔 PR 装的是什么

基线 `3940990`（9.30 出货点）之上的 5 笔已提交 + 本轮未提交的修复：

| 层 | 内容 |
|---|---|
| 已提交 5 笔 | `e0a7d1e` 解析 ST 2094-40/30（行为中性）→ `4b50a05` 把动态曲线接进 SDR tone mapping → `9bf4df7` 逐帧 DXGI metadata + 对 Vivid 说实话 → `f1d3ce5` 注入 API → `d2f5826` 多窗口 + 修 cross-window blend |
| 规模 | 区间 `15 files +26768/−24124`；**剔生成物后 `14 files +1471/−14`**，5 个全新文件 683 行 |
| 本轮未提交（TP 工作树 6 文件） | `Hdr/HdrProcessor.{h,cpp}`、`Render/VideoRenderer.{h,cpp}`、`Core/PlayerSession.cpp`、`Hdr/HdrDynamicMetadata.h` |

本轮 6 个文件里的东西（全部有成对读数，指针见 §四）：
- **D1** 注入项按 `FFF3FPSnapshot::frameIndex` 匹配，不再误用 `frame->pts`（此前稀疏场景表恒解析到最后一条）；
- **D2** 暂停 / EOF 装表在**诊断与画面两处都生效**（走呈现路径重刷常量与曲线纹理，不在 API 线程改共享状态）；
- **D5** 注入项自带峰值时用之，否则回落流级基线，不再混挂该帧比特流的动态峰值；
- 会话侧 `snapshot_` 写回改为 `Enqueue`（与 `SetColorMode` 同一线程亲和纪律）；
- `HdrDynamicMetadata.h` 的单位注释修正（`targeted_system_display_maximum_luminance` 已是 cd/m²，不该 ×10000）。

## 二、必须先进去的前置（缺一条就别提）

1. **`3fp/kernel` 侧的 shader 排版回归（RD-1）**：`ShaderBytecode.h` 工作树版把在库的 7/4 参差列换成 width-6 均匀列 ⇒ **49,035 行差异全是排版**，按值只有 `FFFPixelShaderBytecode` 真变。上游会把这当成噪声。（Lane L1 在做）
2. **API 契约文档（RE-05）**：`FFF.Player.Api.h` 里"窗口无峰值时峰值归谁""暂停/EOF 装表的生效路径"两条目前没写。（Lane L2 在做）
3. **回归四路在修复版 DLL 上重跑**：Vivid / HDR10+ / SDR / JXR。⚠ 已知 TP 树跑 Vivid 读 **`peak=10000`**（锚点 4000）——**这不是注入改动造成的**，是 `vividBaselineIsHlg` 只存在于 `3fp/kernel` 的跨树事实；若这笔 PR 不带 Vivid 门控，PR 描述里必须写明它**不含**该项，别让上游以为出货侧已修。
4. **`HDR: …` 提交信息三行（验收/变异/账本）数字全部现读复算**，不沿用本文件里的转述数。

## 三、切分方案

上游只要一笔（D10）。建议分两步走，避免"一笔里混两件事"：

- **PR-1A（先提）**：`e0a7d1e..d2f5826` 五笔压成一笔 —— HDR10+ 解析 + 动态映射 + 逐帧 DXGI metadata + 注入 API + 多窗口。
- **PR-1B（随后，依赖 1A 合并）**：本轮 6 文件的语义修复单独一笔（`HDR: fix the injection API's frame key, paused-state visibility, and peak ownership`）。理由：1A 已被复核为"ABI/版本纪律干净"，把未评审的新改动捆进去会让上游 reviewer 在同一笔里既看特性又看修复。

> 若用户更希望"一笔到位"，那就 1A+1B 一起 squash，但 PR 描述里必须逐条列 §一 的四项修复与它们的判据，不然读不出来。

## 四、证据指针（提交信息 `账本：` 那行用）

| 项 | 盘上位置 |
|---|---|
| 注入语义改前红 / 改后对 | `_3fc_tmp/handoff-20261008-lanes/lead-p0/{before-*,fixed2-*,re03-*,pixel-re03}.log` + `README.md` |
| 三轮构建哈希 | `E5A7ADCB53B8DDB9`（基线）→ `08D567E3ECAF5683` → `8CCE0D383BBED30F` → `5DFD793BCFFEF68E`；TP `x64/Release` 已还原回基线并断言与 `.3fc_kernel_build.json` 的 `dllSha256` 相符 |
| 6 项 FAIL 改判为夹具 | `_3fc_tmp/handoff-20261008-lanes/laneA-inject/{repro-inject-FATE-verbatim.txt,repro-inject-rolloff.txt,repeat-inject.txt,fate-fixture-metadata.txt}` |
| 多窗口端到端（交接 #7 判已完成） | 同上目录 `repro-window-pixel.txt`（三区域各 `7/7`、cross-fade 在） |
| 3FP 正式账本（本轮新写） | `3fp/docs/73-…`、`74-…`、`75-…`（Lane L3 落地中） |

## 五、待点头后才跑的命令（逐条列出，不在此执行）

```bash
# 1) 在 TP 仓内开 PR 分支（本地，可逆）
git -C third_party/fff_project switch -c upstream/hdr10plus-pr 3940990
git -C third_party/fff_project diff 3940990..d2f5826 -- . ':(exclude)FFF.Native/3FP/Render/ShaderBytecode.h' \
  > /tmp/pr1a.patch          # 剔生成物后 14 files +1471/−14
#    生成物按 RD-1 的紧凑版式单独重生成后一并 apply

# 2) squash 成一笔并写三行提交信息（版本号放最前）
git -C third_party/fff_project apply --3way /tmp/pr1a.patch   # ⚠ 需先 fetch PR 树 blob，否则 --3way 退化成硬套
git -C third_party/fff_project commit -m "[v0.3.3-beta] HDR: parse and apply ST 2094-40 dynamic metadata (incl. injection API)"

# 3) 推送 + 开 PR（远端，另需一次点头；本机无 gh CLI ⇒ 推送后人工在网页开，或带 token 打 api.github.com）
git -C third_party/fff_project push -u fork upstream/hdr10plus-pr
```

⚠ §五-2 那条 `apply --3way` 有前提：**两仓不共享 object**（`3fp/kernel` 与 `third_party/fff_project` 的 remote 不同、无 alternates），跨树补 pre-image blob 要先 `git fetch <树路径> HEAD`，否则三方合并退化成硬套（此前实测 8/14 文件直接 reject）。
⚠ 若只提 1A（不碰实验树），本条风险不适用 —— 这正是把 1B 拆开的另一个理由。
