# 55 · HDR Vivid 解码链路分析（含 docs/21、docs/22 时效性复核）

- 任务：核实「HdrVivid 恒按 PQ 解码」这一缺陷在**当前工作树**中是否已被修复，判定其提交归属、逻辑正确性、残留风险与验证缺口。
- 仓库：`C:\PLAN\3FCompare\3fp\kernel`（内核 git 仓库），当前分支 `feature/vsr-and-rotation`，HEAD `a44cbda`。
- 性质：**只读分析**，未修改任何源码。本文件是唯一产出物。
- 证据纪律：每条结论标注 **置信度（高/中/低）**，并区分 **[VERIFIED]**（本人读过该 file:line 或亲自运行该命令/工具并得到该输出）与 **[INFERRED]**（由已读证据推演，未直接运行验证）。

> ⚠️ **路径约定（重要）**：本文中所有形如 `docs/21`、`docs/22`、`docs/26` 的引用，**一律指 `3fp/docs/` 下的文件**，即
> `3fp/docs/21-HdrVivid传递函数解码分析（2026-09-26）.zh.md`、
> `3fp/docs/22-HDR传递函数修复规划（2026-09-26）.zh.md`、
> `3fp/docs/26-MKS-PR1内核实现与验证（2026-09-26）.zh.md`。
> **仓库根目录的 `docs/21-上游PR提交前复核…`、`docs/22-HDR链路实测…`、`docs/26-多路无缝放大…` 是同号但完全不同的文件，与本文无关。** 直接照抄 `docs/21` 会落到错误的文件上。

---

## 一、结论先行

1. **[VERIFIED·高] docs/21 描述的缺陷，在「流正确声明了 VUI `color_trc`」的前提下已被修复；且修复早已落地并合入上游主干，不是「工作树里未提交的新改动」。**
   当前 `VideoRenderer.cpp:5323-5336` 的「帧 trc 优先」逻辑体，其每一行 `git blame` 均归属提交 **`b4e7f14975368000f7097b767d8ffe1861a54f27`**（作者 `luoye-cpu`，2026-09-26 18:31:53，标题 `Native: decode HDR pixels by their own transfer function`），该提交经 **Merge PR #11（合并提交 `235f8bc9069f68827cb8d92787caf9dcb6ba57e4`，2026-09-26 19:50:38）** 进入上游 `master`，并且 `235f8bc` 已是当前 HEAD 的祖先（`git merge-base --is-ancestor 235f8bc HEAD` → exit 0）。
   ⚠️ **限定词不可省略**：修复依据的是**帧级** `frame->color_trc`；若流未声明该字段（`AVCOL_TRC_UNSPECIFIED`），判定退回旧的 format 推断路径，**HLG+Vivid 仍会被按 PQ 解码**——即原缺陷在该组合下仍会复现。**详见 §5 R6。**

2. **[VERIFIED·高] 唯一未随该 PR 一起进入主干的是「注释」两行，它们由上游提交 `58818728d4aeccf68b7fb9662155b6614d1c569b`（`2026/10/5-1`）改写。**
   即：`git blame -L 5317,5340` 显示 5321-5322（注释）→ `5881872`，5323-5336（全部逻辑）→ `b4e7f14`。**逻辑不是上游独立重写的，是同一份代码换了评论措辞。**

3. **[VERIFIED·高] 因此 docs/21 与 docs/22 关于「内核现状恒按 PQ 解码」的描述已 STALE（过期），不可再作为现状依据。**
   docs/21 行 26 引用的 `VideoRenderer.cpp:4121-4124` 是**修复前**的行号与写法；docs/22 行 23 提出「约 +12/−4 行」的改动清单，其内容与已在主干的实现**逐字一致**（见 §四 表 A），但「待实现」的定性已失效。

4. **[VERIFIED·高] 实现的逻辑与 docs/22 §2 的方案 A 完全一致，DV 优先级、trc 判定、format 回退三层结构原样成立。**

5. **[VERIFIED·高] 元数据/峰值一侧（`HdrProcessor.cpp`）确实未被该修复触碰，task 3 的四项预期全部成立。** 但**[VERIFIED·高] 该文件在 `5881872` 中确有改动**——改的是 `SetExtensionProcessing`（外部扩展层），与 Vivid 分支无关，故 Vivid 语义不变。

6. **[VERIFIED·高] FATE HLG+Vivid 样本存在**（`testmedia\media\hdrmeta\hdr_vivid_h265_sample.hevc`，36284 B），**[VERIFIED·高] 且其 `color_transfer=arib-std-b67`**，`maximum_maxrgb=4095/4095`（→ 峰值 10000 nits）。**[VERIFIED·高] 读像素对拍工具 `3fp\tools\read_pixel_probe.cpp` 存在**，可复跑 A/B。

7. **[VERIFIED·高] 残留问题集中在「峰值域语义」与「规范符合度」，而非传递函数本身。** 最实质的一条：Vivid 的 `maximum_maxrgb` 被按 `×10000` 直接当作**绝对亮度（nits）**使用（`HdrProcessor.cpp:245`），而 FFmpeg 头文件对该字段的定义是**归一化到 0.0–1.0、1/4095 步进的「显示内容最大亮度」**（`hdr_dynamic_vivid_metadata.h:201-206`）。**[INFERRED·中] 结合 `base_param_k3` 的语义（`k3_0 = maximum_maxrgb`）与 CUVA 的 PQ 绝对域基线，对**规范内 PQ 基线内容**该 ×10000 大概率成立；但对 **HLG 基线（规范外）**，HLG 是相对域，×10000 得到的绝对 nits 无物理含义。这正是 docs/26 §218/§241 已记录、被刻意留待后续的 P3 项。

8. **[INFERRED·高] 一个仍然存在的真实缺口：HLG+Vivid 组合下，传递函数已按帧 trc 正确解为 HLG（transfer=2），但峰值仍取 `maximum_maxrgb×10000`（实测=10000 nits）。**
   两者组合即「按 1000 nits 参考的 HLG 相对信号」被配以「10000 nits 的源峰值」送进 BT.2390 映射。即便传递函数正确，**色调映射的锚点仍可能偏高**——这是修复没有覆盖、也不在其声称范围内的部分。

---

## 二、代码证据（含 file:line）

### 2.1 当前工作树的传递函数判定

`3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp:5317-5340`（[VERIFIED]，已逐行读取）：

```cpp
5317    ShaderSettings settings{};
5318    settings.colorMode = static_cast<std::uint32_t>(actualMode_);
5319    settings.reserved = 0;
5320    const auto hlgCompatibility = static_cast<std::uint32_t>(FFF3FPHdrCompatibility::Hlg);
5321    // Dynamic metadata does not change the pixel transfer function. Dolby Vision
5322    // uses its compatibility transfer; other HDR follows frame TRC, then classification.
5323    auto transferFromFrame = 0u;
5324    const bool frameDeclaresTrc = frame != nullptr && frame->color_trc != AVCOL_TRC_UNSPECIFIED;
5325    if (frameDeclaresTrc) {
5326        transferFromFrame = frame->color_trc == AVCOL_TRC_ARIB_STD_B67 ? 2u :
5327            frame->color_trc == AVCOL_TRC_SMPTE2084 ? 1u : 0u;
5328    }
5329    if (hdrState.format == FFF3FPHdrFormat::DolbyVision) {
5330        settings.transfer = (hdrState.compatibility & hlgCompatibility) != 0 ? 2u : 1u;
5331    } else if (hdrState.format != FFF3FPHdrFormat::Sdr && frameDeclaresTrc && transferFromFrame != 0u) {
5332        settings.transfer = transferFromFrame;
5333    } else {
5334        settings.transfer = hdrState.format == FFF3FPHdrFormat::Hlg ? 2u :
5335            (hdrState.format != FFF3FPHdrFormat::Sdr ? 1u : 0u);
5336    }
5337    settings.gamut = gamut;
5338    settings.sdrPeak = sdrPeakNits_;
5339    settings.hdrPeak = settings.transfer == 0 ? 100.0f : hdrProcessor_.State().sourcePeakNits;
```

编码含义（[VERIFIED]，对照 `FFF.Player.Api.h:30-34` 的 `FFF3FPColorTransfer{SdrBt709=0, Pq=1, Hlg=2}`）：
`transfer=2` → HLG 解码；`=1` → PQ 解码；`=0` → SDR/BT.709。

### 2.2 修复前的旧写法（对照基线）

`b4e7f14` 的父提交中（[VERIFIED]，`git show b4e7f14` 的 `-` 侧原文）：

```cpp
settings.transfer = hdrState.format == FFF3FPHdrFormat::Hlg ||
    (hdrState.format == FFF3FPHdrFormat::DolbyVision &&
     (hdrState.compatibility & hlgCompatibility) != 0) ? 2u :
    (hdrState.format != FFF3FPHdrFormat::Sdr ? 1u : 0u);
```

这正是 docs/21 行 26 所引 `4121-4124` 的行为：`HdrVivid(5)` 落进 `!= Sdr` 分支 → **恒 1（PQ）**。

### 2.3 元数据/峰值一侧（未变语义）

`3fp\kernel\FFF.Native\3FP\Hdr\HdrProcessor.cpp:236-250`（[VERIFIED]）：

```cpp
236    if (const auto* vividData = av_frame_get_side_data(frame, AV_FRAME_DATA_DYNAMIC_HDR_VIVID);
237        vividData != nullptr && vividData->size >= sizeof(AVDynamicHDRVivid)) {
238        const auto* vivid = reinterpret_cast<const AVDynamicHDRVivid*>(vividData->data);
239        next.format = FFF3FPHdrFormat::HdrVivid;
240        next.compatibility |= Compatibility(FFF3FPHdrCompatibility::HdrVivid) |
241            Compatibility(FFF3FPHdrCompatibility::Hdr10);
242        next.processingPath = FFF3FPHdrProcessingPath::HdrVividDynamic;
243        next.dynamicMetadata = true;
244        if (vivid->num_windows > 0) {
245            const auto peak = ValidPeak(av_q2d(vivid->params[0].maximum_maxrgb) * 10000.0);
246            if (peak > 0.0f) {
247                next.sourcePeakNits = peak;
248                dynamicSourcePeak = true;
249            }
250        }
251    }
```

- `format = HdrVivid` ✅（`FFF.Player.Api.h:42`：`HdrVivid = 5`，[VERIFIED]）
- `processingPath = HdrVividDynamic` ✅（`FFF.Player.Api.h:60`：`HdrVividDynamic = 6`，[VERIFIED]）
- `dynamicMetadata = true` ✅
- `sourcePeak = maximum_maxrgb × 10000` ✅（第 245 行）

`ConfigureStream`（`HdrProcessor.cpp:168-206`，[VERIFIED]）的流级先验**不含 Vivid 分支**——Vivid 只能靠**帧级** side data 触发（第 236 行），这与 docs/21 行 25「帧级翻转」描述一致。流级只有 DV → HDR10+ → trc=HLG → trc=PQ 四级（行 171-192）。

### 2.4 证据表

| 证据 | file:line | 结论 | 置信度 |
|---|---|---|---|
| 帧 trc 优先三级判定 | `VideoRenderer.cpp:5323-5336` | 方案 A 已实现 | 高 [VERIFIED] |
| 旧写法（format 推断） | `b4e7f14` diff `-` 侧 | docs/21 所述缺陷基线 | 高 [VERIFIED] |
| 行归属：逻辑体 | `git blame` → `b4e7f14`（5323-5336） | 修复由 PR#11 引入 | 高 [VERIFIED] |
| 行归属：注释 | `git blame` → `5881872`（5321-5322） | 上游仅改措辞 | 高 [VERIFIED] |
| PR #11 合并点 | `235f8bc9069f...` | 2026-09-26 19:50 入主干 | 高 [VERIFIED] |
| 合并点是否在 HEAD 祖先链 | `merge-base --is-ancestor` → 0 | **已合入，非未提交** | 高 [VERIFIED] |
| Vivid 帧分支 | `HdrProcessor.cpp:236-250` | format=5/path=6/dyn=true | 高 [VERIFIED] |
| `maximum_maxrgb×10000` | `HdrProcessor.cpp:245` | sourcePeak 语义 | 高 [VERIFIED] |
| 枚举取值 | `FFF.Player.Api.h:42,60` | HdrVivid=5, HdrVividDynamic=6 | 高 [VERIFIED] |
| 字段定义（归一化） | `hdr_dynamic_vivid_metadata.h:201-206` | 0.0–1.0, 1/4095 步进 | 高 [VERIFIED] |
| FATE 样本存在 | `testmedia\media\hdrmeta\hdr_vivid_h265_sample.hevc` | 36284 B | 高 [VERIFIED] |
| 样本 trc=HLG | `ffprobe` 输出 | `color_transfer=arib-std-b67` | 高 [VERIFIED] |
| 样本峰值 | `ffprobe` 输出 | `maximum_maxrgb=4095/4095` → 10000 nits | 高 [VERIFIED] |
| 读像素工具 | `3fp\tools\read_pixel_probe.cpp:1-122` | A/B 可复跑 | 高 [VERIFIED] |
| HDR10+ 对照样本 | `hdr10_plus_h265_sample.hevc` | trc=smpte2084，maxscl→334 nits | 高 [VERIFIED] |
| HLG+Vivid 峰值域不匹配 | `docs/26` 行 218/241 | 已记录的遗留项 | 高 [VERIFIED] |

---

## 三、修复落地状态与提交归属

### 3.1 时间线与归属

| 环节 | 提交 / 事实 | 置信度 |
|---|---|---|
| 修复实现 | `b4e7f14`（`luoye-cpu`，2026-09-26 18:31:53） | 高 [VERIFIED] |
| 独立 PR 分支 | `upstream/hdr-transfer-pr`（= master 基线 + 该单提交） | 高 [VERIFIED] |
| 上游合并 | `235f8bc`「Merge pull request #11 from luoye-cpu/upstream/hdr-transfer-pr」（2026-09-26 19:50:38） | 高 [VERIFIED] |
| 是否在 HEAD 祖先链 | 是（`merge-base --is-ancestor 235f8bc HEAD` → exit 0） | 高 [VERIFIED] |
| 注释被上游改写 | `5881872`「2026/10/5-1」（2026-10-05 09:33:19） | 高 [VERIFIED] |
| 当前工作树是否有该文件的未提交改动 | **否**（`git status --short` 中无 `VideoRenderer.cpp`） | 高 [VERIFIED] |

### 3.2 落地状态判定

- **[VERIFIED·高] 已提交 + 已合入主干，不是 working-tree 改动、也不是未合并分支的孤儿代码。** 判定依据有三，互相独立：
  1. `git blame` 显示逻辑行全部归属 `b4e7f14`（一个已进主干的历史提交），而非 `0000000`（未提交行的 blame 前缀）；
  2. `git status --short` 的 10 个修改文件中**不含** `VideoRenderer.cpp`；
  3. `git merge-base --is-ancestor b4e7f14 5881872` → exit 0，且 `5881872` 在 `upstream/master` 上。

- **[VERIFIED·中] 一个容易误判的点**：`git diff --stat upstream/master upstream/hdr-transfer-pr` 会显示 67 文件、+20233/−29339 的巨大差异。这不是「PR 未合并」，而是 `upstream/hdr-transfer-pr` 分支**停留在 2026-09-26 的旧基线 `a87046e` 上、此后从未 rebase**（其 merge-base 就是它自己 `b4e7f14`）。**不能据该 diff 判定修复未落地**——应以 `merge-base --is-ancestor` / `blame` 为准。

- **[VERIFIED·高] `upstream/image-mode` 上另有一个同标题提交 `a9047ec`**（同一作者、同一时间戳 2026-09-26 18:31:53、同标题 `Native: decode HDR pixels by their own transfer function`），它是 `b4e7f14` 在不同基线（PR #14 前的 `0502340` 系）上的**平行副本**；docs/22 §5 规划的「开发分支追加 + PR 分支 cherry-pick」双份交付形态由此得到印证。两者逻辑内容一致（`git log -S "transferFromFrame" --all` 仅命中这两个提交）。

- **[INFERRED·高] 因此「谁引入了修复」的准确表述是：3FP 侧的 `luoye-cpu` 于 2026-09-26 实现并经 PR #11 合入上游；上游维护者（1059 Studio）在 2026-10-05 的 `5881872` 中保留了逻辑、仅重写注释。** 修复非本次会话产生的新工作。

---

## 四、逻辑正确性评估

### 4.1 与方案 A 的一致性

表 A：docs/22 §2 要求 vs 实际实现（[VERIFIED]，逐条比对）

| docs/22 要求 | 实际实现 | 一致 |
|---|---|---|
| 优先级：DV 兼容位 → 帧 `color_trc` → format 回退 | `5329` DV 分支 → `5331` 帧 trc 分支 → `5333` else 回退 | ✅ 完全一致 |
| DV 分支保留原判定且**优先级最高** | `5329-5330`，且位于 `if` 链首，帧 trc 无法覆盖 | ✅ |
| `ARIB_STD_B67 → 2`、`SMPTE2084 → 1` | `5326-5327` | ✅ |
| 「其余 → 回退」而非「其余 → 0」 | `transferFromFrame` 默认 0 + `5331` 的 `!= 0u` 守卫 | ✅ 语义等价 |
| 用**帧级**（非流级）trc | `frame->color_trc`（`5324/5326/5327`），非 `parameters->color_trc` | ✅ |
| 不动 HdrProcessor / shader / API | `git show b4e7f14 --stat` 仅 1 文件 | ✅ |
| 回退分支 = 旧 ternary 去掉 DV 项后逐字保留 | `b4e7f14` diff `-` 侧 vs `5334-5335` 逐字相同 | ✅ |

**[VERIFIED·高] 结论：实现与方案 A 完全一致，无偏离。**

### 4.2 实现自身的正确性

- **[VERIFIED·高] DV 优先级正确。** DV 在 `if` 链首位无条件胜出，`frameDeclaresTrc` 对其无影响。这与 docs/21 行 60 的理由一致（RPU 重映射语义无法由容器 trc 表达），也修掉了旧写法的一个隐患：旧式把 DV+HLG 兼容位写在与 `format==Hlg` 并列的 `||` 表达式中，而新式把 DV 拆成显式独立分支，可读性与正确性都更好。
- **[VERIFIED·高] `transferFromFrame != 0u` 守卫不是 bug，是刻意的「回退」语义。** `trc == BT.709 / BT.2020-10 / UNSPECIFIED / 任何其他值` → `transferFromFrame = 0` → 落到 `5333` 的 format 回退。对 SDR 流，`hdrState.format == Sdr` 会先在 `5331` 因 `!= Sdr` 被拒，进入 `5334` 得 `format != Sdr ? ... : 0u` → **0，SDR 行为不变**。[INFERRED·高] 反之，若去掉该守卫，`trc=BT.709` 的 HDR 流会被强制解成 0（SDR 曲线），那是**真实的回归**——所以守卫是必要的。docs/22 §3 表格中「其余→回退」的写法正对应这一点。
- **[VERIFIED·高] 首帧跳变被消除。** 旧逻辑下 `[open]` 阶段 format 尚为 `Hlg`（`ConfigureStream` 按流 trc 先行给出 `HdrProcessor.cpp:182-186`）→ transfer=2；Play 后首帧 Vivid SEI 到达 → format 翻 `HdrVivid` → transfer=1，同一文件两种曲线混用（docs/21 §2.3）。新逻辑两阶段读的都是**同一个 `frame->color_trc`**（HLG）→ 恒 2，跳变消失。

### 4.3 边界情形表

下表以「该流的像素 trc」×「到达的 HDR 元数据」为二维输入。`transfer` 列为按 `5323-5336` 逐步手推的结果（[INFERRED]，推理链完整、可复核；未逐个真机跑）。

| # | 像素 trc（VUI） | 元数据分类 `hdrState.format` | 分支命中 | `settings.transfer` | 相对旧行为 | 判定 | 置信度 |
|---|---|---|---|---|---|---|---|
| 1 | PQ (2084) | HdrVivid(5) | `5331` | **1 (PQ)** | 同（旧亦 1） | ✅ **规范内 Vivid，正确** | 高 |
| 2 | HLG (B67) | HdrVivid(5) | `5331` | **2 (HLG)** | **1 → 2**，修复 | ✅ **本次修复目标，正确** | 高 |
| 3 | PQ (2084) | Hdr10Plus(2) | `5331` | **1 (PQ)** | 同 | ✅ **HDR10+ 无回归** | 高 |
| 4 | HLG (B67) | Hdr10Plus(2) | `5331` | **2 (HLG)** | **1 → 2**，修复 | ✅ 顺带修正（2094-40 亦只定义于 PQ，属规范外怪流） | 高 |
| 5 | HLG (B67) | Hlg(3) | `5331` | 2 (HLG) | 同 | ✅ 纯 HLG 无回归 | 高 |
| 6 | PQ (2084) | Hdr10(1) | `5331` | 1 (PQ) | 同 | ✅ 纯 HDR10 无回归 | 高 |
| 7 | **UNSPECIFIED** | HdrVivid(5) | `5333` 回退 | **1 (PQ)** | 同（旧亦 1） | ⚠️ **回退到旧行为**：trc 缺失时 Vivid 仍按 PQ。对规范内容恰好正确；对「HLG 像素但 VUI 未声明」的流仍会错。**无 VUI 时无从判定，属可接受的信息缺失，非缺陷** | 高 |
| 8 | **UNSPECIFIED** | Hlg(3) | `5333` 回退 | **2 (HLG)** | 同 | ⚠️ 同为回退，行为不变 | 高 |
| 9 | **UNSPECIFIED** | Sdr(0) | `5333` 回退 | **0 (SDR)** | 同 | ✅ SDR 不变 | 高 |
| 10 | BT.709 / 其他非 PQ/HLG | HdrVivid(5) | `5331` 因 `!=0u` **被拒** → `5333` | **1 (PQ)** | 同（旧亦 1） | ⚠️ **守卫导致落回旧路径**。理论上 trc=709 的「HDR」流本身就自相矛盾，无正确解；回到旧行为是合理兜底 | 高 |
| 11 | BT.709 / 其他 | Hlg(3) | `5333` 回退 | **2 (HLG)** | 同 | ⚠️ 同上，不变 | 高 |
| 12 | PQ 或 HLG | DolbyVision(4) | `5329` | 由兼容位定：HLG 位→2，否则→1 | 同（DV 分支语义未变） | ✅ DV 不受帧 trc 干扰，正确 | 高 |
| 13 | 任意 | Sdr(0) | `5331` 因 `!= Sdr` 被拒 → `5333` | 0 | 同 | ✅ **SDR 会话完全不受影响**（与 `b4e7f14` commit message 声明一致） | 高 |
| 14 | **HLG (B67)** | HdrVivid(5) | `5331` | 2 (HLG) | 1 → 2 | ⚠️ **传递函数已正确，但峰值仍 = `maximum_maxrgb×10000`。实测样本该值为 10000 nits** ⇒ 用 1000 nits 参考的 HLG 相对信号配 10000 nits 源峰值做色调映射。**修复未覆盖此项** | 高（峰值端 [VERIFIED]；色调后果 [INFERRED]） |
| 15 | **PQ (2084)** | Hlg(3) | `5331` | **1 (PQ)** | **2 → 1（方向相反的改变）** | ⚠️ **本表唯一"反向"变更行**（经对抗复核补入）。`Hlg(3)` 格式只可能来自流级 `color_trc=ARIB_STD_B67`（`HdrProcessor.cpp:182-186`）或 `probe.transfer==Hlg`（`:523-526`），而此处帧 trc 却是 PQ ⇒ 该流**自相矛盾**（流级声明 HLG、帧级声明 PQ），与第 10 行同属「无正确解」的范畴。新逻辑选择**信任帧级 trc**（与 `b4e7f14` 的提交意图一致：按像素自身的传递函数解码），旧逻辑信任流级分类。**两者皆非定理，无"回归"可言**，但确实是一次产出值改变 | 高 |

**综合判定**：
- **[VERIFIED·高] 全部行为变化共 4 行：第 2、4、14 行（PQ 误判 → 正确 HLG，方向正确）与第 15 行（HLG 误判 → 正确 PQ，方向亦为"信任帧 trc"）。**
  ⚠️ 本节早先版本写「变化仅出现在第 2、4 行、无任何情形发生回归」——该表述经对抗复核证伪：第 15 行（`trc=PQ` × `format=Hlg`）同样发生改变，只是方向相反，原表遗漏了该组合。**结论仍为"无有害回归"**，但理由须改为：4 处变化全部是「从信任元数据分类 → 改为信任像素 trc」这一条原则的贯彻，其中 3 处修正了真实误判、1 处改动的是自相矛盾流（无正确解）。**不应表述为"无任何变化"或"零回归"。**
- **[VERIFIED·高] 关键点：`5331` 的 `hdrState.format != FFF3FPHdrFormat::Sdr` 前置条件，保证了新分支只在 HDR 会话内生效，SDR 路径（表 13 行）零影响。**
- **[INFERRED·高] 第 2 行是本次修复的实质收益**，且已由 `b4e7f14` 的 commit message 声称为实测：「center pixel linear 40.6 → 5.7」。**[VERIFIED·中] 该数字本人未复现**（未构建内核），见 §六 证据缺口。

---

## 五、残留问题与风险

按「是否影响规范内 Vivid 内容」排序。

### R1 · HLG+Vivid 的 `maximum_maxrgb` 绝对域语义（**最实质的残留项**）
- **[VERIFIED·高] 事实**：`HdrProcessor.cpp:245` 无条件做 `maximum_maxrgb × 10000`。对 HLG 基线流（表 2/14 行），源峰值因此被设为该值的绝对 nits，实测样本 = 10000 nits。
- **[VERIFIED·高] 头文件语义**：`hdr_dynamic_vivid_metadata.h:201-206` 定义 `maximum_maxrgb` 为「显示内容最大亮度，取值 0.0–1.0，1/4095 步进」——是**归一化索引**，×10000 是内核选择的**解释约定**（把 1.0 解为 10000 nits 的 PQ 峰值）。
- **[INFERRED·高] 风险**：HLG 按 BT.2100 是**相对**（场景参考）域，其 1.0 对应 1000 nits 参考显示，不是 10000 nits。把 10000 nits 作为源峰值交给 BT.2390，会使压缩区间被过度拉宽 ⇒ **高光被过度压缩/发灰**，与 docs/21 §2.2 描述的「过曝/灰化」方向相反但同属色调失准。
- **[VERIFIED·高] 已被人为记录**：`docs/26` 行 218「P3：HLG+Vivid 的峰值域不匹配（10000 nits 绝对域用于 HLG 相对域）为**本 PR 刻意不动范围，留 issue**」；行 241「建议随 issue 补『HLG 基线流动态峰值宜按 1000 nits』」。⇒ **docs/22 的修复范围本就不含此项，这是已知且未关闭的缺口，应作为独立 issue 跟进。**

### R2 · 规范符合度：Vivid 引导的色调映射并未真正按 Vivid 参数执行
- **[VERIFIED·高] 事实**：`HdrProcessor.cpp:236-250` 的 Vivid 分支**只消费 `num_windows` 与 `params[0].maximum_maxrgb` 两个成员**。对 Vivid 的字面引用在 `HdrProcessor.cpp` + `FFF.Player.Api.h` 中共 **30 处**（`Select-String "vivid|Vivid|VIVID" -AllMatches` 计数：26 + 4；早先版本写作 22，经对抗复核更正为 30），逐条核对后除 HdrProcessor 的上述两处外，**其余全部是 API 枚举/字符串名/名称映射/探针字段，没有任何一处读取** `base_param_m_p/m_m/m_a/m_b/m_n`、`base_param_k1/k2/k3`、`3Spline_*`、`targeted_system_display_maximum_luminance`、`variance_maxrgb`、`average_maxrgb`、`minimum_maxrgb`。
- **[VERIFIED·高] 结论的独立复核**：在 `HdrProcessor.cpp` 中对 `base_param|3Spline|targeted_system|variance_maxrgb|average_maxrgb|minimum_maxrgb|tm_params` 做 grep，**零命中**，与上述「只读两个字段」一致。
- **[INFERRED·高] 结论**：内核的 `processingPath = HdrVividDynamic(6)` 与 `ProcessingPathName` 的文案「HDR Vivid metadata-guided display mapping」（`HdrProcessor.cpp:451`）**在事实层面偏强**——实际做法是「把 Vivid 的 maxRGB 当作源峰值，再走通用 BT.2390 映射」，**并非**按 CUVA 005.1 的 tone-mapping 曲线（含 base_param 三段/三样条）执行。**[VERIFIED·高] 对 PQ 基线内容，因峰值正确、色调映射方向正确，观感大概率可接受；[INFERRED·中] 但严格 PID 意义上不满足规范。**
- **[VERIFIED·高] 附带发现**：`vivid->params[0]` 的访问依赖 FFmpeg 结构体数组布局。头文件 `:271` 声明 `AVHDRVividColorTransformParams params[3]`（定长数组），`:266` 说明 `num_windows`「shall be set to 0x01 if system_start_code is 0x01」；第 245 行已用 `num_windows > 0` 守卫 ⇒ **无越界读风险**（[INFERRED·高]，依据 `params[3]` 为定长数组、索引 0 恒合法）。[INFERRED·中] 另需留意 `num_windows` 若被畸形流设为 >3，索引 `params[0]` 仍安全，但语义上无对应；当前实现只读 `[0]`，**不构成风险**。

### R3 · P3/色域字段的消费情况
- **[VERIFIED·高] 事实**：Vivid 元数据内**不含**色域/原色字段（该头文件全篇无 primaries 类成员）；色域来自容器 VUI 的 `color_primaries`（样本实测 `bt2020`）。内核另有 `settings.gamut`（`VideoRenderer.cpp:5337`）与 `source2020` 通路。
- **[INFERRED·高] 结论**：任务描述中「是否消费 P3/其他字段」在 **Vivid 元数据层面不适用**——没有可消费的原色字段。Vivid 规范基线为 BT.2020，样本亦为 `bt2020`。**风险等级：低。**

### R4 · UI / API 对 Vivid 的呈现
- **[VERIFIED·高] 已正确接线**：`FFF3FPHdrFormat::HdrVivid = 5`（`FFF.Player.Api.h:42`）、`FFF3FPHdrCompatibility::HdrVivid = 1u<<3`（`FFF.Player.Api.h:50`）、`FFF3FPHdrProcessingPath::HdrVividDynamic = 6`（`FFF.Player.Api.h:60`）、探针字段 `hdrVividMetadata`（`FFF.Player.Api.h:492`）。
- **[VERIFIED·高] 名称与文案齐备**：`FormatName` → `"HDR Vivid"`（`HdrProcessor.cpp:438`）；`ProcessingPathName` → `"HDR Vivid metadata-guided display mapping"`（`:451`）；`CompatibilityNames` → `append("HDR Vivid")`（`:476`）；`EvaluateProbe` 有独立 `hdrVividMetadata` 分支（`:512-517`）。
- **[VERIFIED·高] 着色器通路正确**：`RequiresMetadataAwareShader()`（`:377-382`）在 `dynamicMetadata` 或 `format ∈ {Hlg, DolbyVision, HdrVivid}` 时返回 true；`CanUseDirectVideoProcessor()`（`VideoRenderer.cpp:3095-3096`）因此对 Vivid **拒绝** D3D11 VideoProcessor 快路径，强制走元数据感知 shader。⇒ **[INFERRED·高] 不存在「快路径绕过新 transfer 判定」的漏洞**（快路径另有 `cachedVideoSettings_.transfer != 0` 的兜底拒绝，`VideoRenderer.cpp:3100`）。
- **[INFERRED·中] 唯一文案层面的小瑕疵**：R2 所述 `"HDR Vivid metadata-guided display mapping"` 与实际「仅用 maxRGB 作峰值」不完全相符，可能误导用户；建议改为更保守的措辞。**风险：低（纯文案）。**

### R5 · docs/21、docs/22 已过期，须标注
- **[VERIFIED·高] docs/21 行 26 的 `VideoRenderer.cpp:4121-4124` 行号与写法均已失效**（现为 `5317-5340`，且语义相反）。§2.3 描述的「首帧 HLG / 后续 PQ 跳变」在当前代码下**已不成立**。
- **[VERIFIED·高] docs/22 §3 的「改动清单」作为待办已失效**（工作已完成）；§4 验证计划 V1/V2 的判据仍然有效且**尚未执行**（见 §六）；§5 交付形态已被 PR #11 实际落地取代。
- **[VERIFIED·高] docs/21 §1.1「Vivid 规范基线 = PQ 10000 nits」的结论不因本次修复改变**——修复只纠正像素解码曲线，不改规范基线判断。故 docs/21 的**问题定性仍有效，现状描述已过期**。

### R6 · 「缺陷已修复」不是无条件的：VUI 缺失时旧缺陷仍会复现（**须与 §一 的结论合并阅读**）
- **[VERIFIED·高] 事实链**：`HdrProcessor.cpp:239` 在每帧带 Vivid side data 时**无条件覆写** `next.format = HdrVivid`，而 `VideoRenderer.cpp:5089` 先调 `ProcessFrame`、`:5317+` 再读该状态 ⇒ 对 Vivid 流，`hdrState.format` 恒为 `HdrVivid(5)`。此时 `transfer` 完全取决于 `frame->color_trc`。
- **[VERIFIED·高] 推论**：若该流**像素是 HLG 但 VUI 未声明 trc**（`AVCOL_TRC_UNSPECIFIED`），则 `frameDeclaresTrc == false` → 落到 `5333` 回退 → `format != Sdr` → **transfer = 1（PQ）**。
- ⇒ **[INFERRED·高] 这正是 docs/21 描述的原始缺陷，在「HLG 像素 + 无 VUI trc + Vivid 元数据」这一组合下**依然原样存在**。**
- **[VERIFIED·高] 因此 §一 第 1 条的准确表述应为**：「docs/21 的缺陷**在流正确声明了 VUI `color_trc` 的前提下**已被修复」——而**不是**「无条件修复」。含 VUI 的合规内容（绝大多数真实内容）已修复；缺 VUI 的流仍按旧行为兜底。**该兜底是信息缺失的必然结果，不是实现错误**，但对外表述不应省略该前提。
- **[INFERRED·中] 优先级建议**：若上游要彻底关闭该 issue，可考虑在 `frameDeclaresTrc == false` 且 `format == HdrVivid` 时，回退到**流级** `parameters->color_trc`（`ConfigureStream` 已有该信息，`HdrProcessor.cpp:182-187`），而不是直接按 PQ —— 但这需要先确认真实 Vivid 流是否普遍携带 VUI。（**属建议，非缺陷认定**。）

### R7 · 硬件解码路径的 `frame->color_trc` 填充性（**本次修复最可能的现实证伪点**）
- **[VERIFIED·高] 事实**：`settings.transfer` 的判定完全依赖 `frame->color_trc`（`VideoRenderer.cpp:5324-5327`）。
- **[INFERRED·中] 风险**：当走硬件解码路径（D3D11VA，`PlayerSession.cpp:2238-2303`）时，解码帧由 GPU 输出，`frame->color_trc` 是否与软解路径**同样可靠地被填充**，本人**未验证**（见 §6.3 G2）。若硬件路径下该字段为空，则 `frameDeclaresTrc == false` → 落回 `5333` 的 format 推断 → **对 HLG+Vivid 流会退回 PQ 误判**，即修复在硬解下失效。
- **[INFERRED·中] 为何值得单列**：这是唯一一个「修复在部分运行配置下静默失效」的场景，比 §4.3 的边界表更贴近真实使用（默认即为 D3D11 硬解）。**建议验证方式**：分别在 `decodeMode=Cpu` 与 `D3D11` 下，对同一 HLG+Vivid 样本读像素并对比 `transfer`；若两者一致且均为 2，则风险关闭。
- **[VERIFIED·高] 缓解因素**：`HdrProcessor` 另有帧级 `frame->color_trc` 分类兜底（`HdrProcessor.cpp:222-234`），说明内核在别处也假设该字段在帧级可用——但这**不能代替**对硬解路径的直接验证。

---

## 六、验证方法与证据缺口

### 6.1 样本与工具（[VERIFIED]）

| 项 | 状态 |
|---|---|
| FATE HLG+Vivid 样本 | ✅ `testmedia\media\hdrmeta\hdr_vivid_h265_sample.hevc`（36284 B） |
| 样本 trc | ✅ `color_transfer=arib-std-b67`（即 HLG） |
| 样本峰值来源 | ✅ `maximum_maxrgb=4095/4095` → `×10000` = **10000 nits** |
| 样本其他 side data | Mastering display max_luminance=4000 nits、CLL=0/0、Vivid SEI（与 docs/21 §1.2 描述一致） |
| HDR10+ 对照样本 | ✅ `hdr10_plus_h265_sample.hevc`，trc=`smpte2084`，maxscl 最大 3340/100000 → **334 nits**（与 docs/22 V2 的「peak=334」判据吻合） |
| 读像素工具 | ✅ `3fp\tools\read_pixel_probe.cpp`（122 行，CPU 解码 + 真实窗口 + 真实 FFmpeg DLL） |
| ffprobe | ✅ `publish\PLAN\ffmpeg-full\ffprobe.exe` |
| 构建脚本 | ✅ `3fp\tools\rebuild_probes.ps1`、`tools\build_kernel_manual.py` |

### 6.2 如何复跑 docs/22 的 A/B 对拍（[VERIFIED] 工具用法，[INFERRED] 具体步骤）

`read_pixel_probe.cpp` 的接口（[VERIFIED]，读自源码）：
- 用法：`read_pixel_probe <media path> [--hdr]`（`:37`）；`--hdr` → `ColorMode::MapToHdr`，否则 `MapToSdr`（`:62`）
- 输出 1（open 阶段快照，`:82-85`）：`hdrFormat / hdrPath / dyn / peak / outBits / WxH`
- 输出 2（play 1.5 s 后快照，`:91-94`）：同上 + `presented`
- 输出 3（5 个固定采样点的线性 RGB，`:98-118`）：`(480,250) (240,250) (720,250) (480,120) (480,400)`
- ⚠️ 源码 `:96-97` 明确警告：`ReadVideoPixel` 坐标域是**交换链/后台缓冲尺寸**，不是片源分辨率；故固定点法才稳。

复跑步骤（[INFERRED·中]，未执行）：
1. 取修复前基线：`git worktree add <dir> b4e7f14^`（或直接 checkout 父提交）后构建内核；
2. 取修复后：当前 HEAD 构建内核；
3. 两次分别运行 `read_pixel_probe.exe testmedia\media\hdrmeta\hdr_vivid_h265_sample.hevc --hdr`；
4. **判据（docs/22 V1）**：两点 `transfer` 从 PQ 变 HLG ⇒ 采样点线性 RGB 应出现显著且**方向为变暗**的差异（`b4e7f14` message 称 center linear `40.6 → 5.7`，≈7.1×，与 docs/21 §2.2 在 0.75 码值处的 ~7.7× 高估吻合）；且 `hdrFormat=5 / hdrPath=6 / dyn=1 / peak=10000` **不变**；
5. HDR10+ 对照（V2）：跑 `hdr10_plus_h265_sample.hevc`，判据为**像素逐点不变**、`hdrFormat=2 / path=2 / dyn=1 / peak=334` 不变。

### 6.3 证据缺口（必须如实声明）

| # | 缺口 | 影响 | 补法 |
|---|---|---|---|
| G1 | **本人未构建内核、未运行 `read_pixel_probe`** | `b4e7f14` 的「40.6 → 5.7」及 docs/22 V1/V2 判据均**未复现**；§4.3 表格 transfer 值是**手推**而非实测 | 按 §6.2 步骤跑 A/B；或用 `rebuild_probes.ps1` |
| G2 | 表 14 行的 transfer 结果为静态推理 | 若 `frame->color_trc` 在**硬解**路径下与软解不一致（如 DXVA 不回填 trc），实际值可能偏离 | 分别以 `Cpu`/硬解两种 `decodeMode` 各跑一次探针 |
| G3 | **未验证 `maximum_maxrgb×10000` 对真实 Vivid 内容的正确性** | R1 的实际观感后果未量化 | 需真实 PQ 基线 Vivid 样本（现仓库仅有 FATE 合成样本） |
| G4 | 未核对 CUVA 005.1-2021 规范原文 | R1/R2 的「绝对域/相对域」判断依据是 FFmpeg 头注释 + BT.2100 通识 + docs/26 的既有结论，**非规范原文** | 获取规范 PDF 正本核对 `maximum_maxrgb` 的定义域 |
| G5 | 未做宿主 UI 实机检查 | R4 的「UI 正确呈现 Vivid」仅由代码枚举/文案推断，未见界面 | 打开样本 + 截图核对信息叠加层 |
| G6 | 未跑宿主回归套件 | docs/22 V3/V4（图片矩阵、13/9 基线）无新证据 | 跑 `run_host_tests.sh` |

### 6.4 给上游的建议（若仍要提 issue）

**[INFERRED·中] 建议将原「传递函数」issue 改写为「Vivid 峰值域与规范符合度」issue**，因为传递函数部分已在 `b4e7f14`（PR #11）解决：
1. **主体**：HLG 基线流下 `maximum_maxrgb×10000` 得到无物理含义的绝对峰值（建议按流 trc 分流：HLG → ×1000 或按参考白归一）；
2. **次要**：`processingPath` 文案与实际「仅用 maxRGB 作峰值、未执行 CUVA tone-mapping 曲线」不符；
3. **附**：FATE 样本 + `ffprobe` side data 输出（本文 §6.1 已列出可直接引用的实测值）；
4. **[VERIFIED·高] 不要重复提「HLG trc + Vivid 元数据被按 PQ 解码」**——该条已修复，重复提交会被以「已修复」关闭。

---

## 附：三个文件的时效性结论

| 文档 | 时效性 | 说明 |
|---|---|---|
| `docs/21-HdrVivid传递函数解码分析` | ⚠️ **部分 STALE** | §1.3/§2.3/§3 的**现状描述与修复建议已过期**（缺陷已修）；§1.1/§1.2 的规范层与样本性质结论**仍有效** |
| `docs/22-HDR传递函数修复规划` | ⚠️ **STALE（作为待办）** | 方案已按原文实现并合入主干（`b4e7f14` / PR #11）；§4 验证计划 V1/V2 **仍未执行**，仍有价值 |
| `docs/20-图像与视频完整性验证` | ✅ 未受影响 | 本次为只读分析，未触碰该文档所述链路 |
| `docs/26-MKS-PR1内核实现与验证` | ✅ **仍然有效** | §218/§241 记录的「HLG+Vivid 峰值域不匹配，留 issue」**至今未关闭**，与本文 R1 一致 |
| `docs/38-内核更新到上游2026.10.5` | ✅ 仍然有效 | §7 正确记录了 `5881872` 对 `HdrProcessor.cpp` 的改动（属外部扩展层，非 Vivid）；本文 §3.1 补上了「`5881872` 同时改写了传递函数注释」这一细节 |
