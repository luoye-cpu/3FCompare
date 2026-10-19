# tools/patches — 内核自定义补丁存档目录

> **状态说明（2026-10-17 修订：0014 退役，顶层无补丁）**
>
> 本目录是**唯一权威补丁 + 历史归档**两层结构，顶层只在有"现行树待重放的分歧"时放补丁：
>
> - **`0014-baseline-vs-a87046e-api16.patch` 已退役入 `history/`（2026-10-17）**：它描述的是旧谱系
>   （`3fcompare/api16-kernel` 相对 `a87046e` 的分歧，内容 = `git diff a87046e HEAD -- FFF.Native` 全集，
>   含 `ShaderBytecode.h` 生成物与 `tools/generate_shader_bytecode.py`；曾对 `a87046e` 原始树正向应用后与
>   当时内核 HEAD 的 15 个文件逐字节相同，实测 `applied-vs-HEAD differing files = 0`）。
>   `third_party/fff_project` 已换到新谱系（`upstream-sync`，HEAD 不含钉死基线 d2f5826），
>   该补丁正向、反向 `--check` 都不再匹配 ⇒ `tools\构建全部.ps1` 每次都在补丁段中止。
>   按脚本自身的处置指引移入 `history/`——它是留档，不再是待应用项。
> - **`0013-baseline-vs-a87046e.patch` 更早已退役入 `history/`**：它只到方案 A 为止，API 16 落地后
>   反向探测不再匹配（哨兵 C 项就是抓这个的）。旧 9 个补丁同样在 `history/`（未删除、未改写，SHA1 逐一核对一致）。
> - `tools/构建全部.ps1` 的 `Get-ChildItem $PatchesDir -Filter *.patch` **不递归**：
>   `history/` 下的补丁不再参与重放；**顶层无补丁时补丁段直接跳过**（脚本 L171-173 打印"补丁均为最新"）。
>
> 上游升级的正确流程见 `third_party/fff_project/PATCHES.md`（人工重移植 + 打新归档 tag），
> 体检信息用 `tools/更新内核.ps1` 查询。

## 补丁状态表（机器可读）

```patch-status
0014-baseline-vs-a87046e-api16.patch | retired-into-history (2026-10-17)
0013-baseline-vs-a87046e.patch | superseded-by-0014
0001-vrr-tearing-present-config.patch | absorbed-into-0013
0002-zoom-stall-fixes.patch | absorbed-into-0013
0003-0005-local-optimizations.patch | absorbed-into-0013
0006-0007-zoom-pan-fix.patch | absorbed-into-0013
0008-resize-resample-diag.patch | absorbed-into-0013
0009-grid-cell-cover-zoom.patch | dead-not-in-tree
0010-single-presenter-pipeline.patch | absorbed-into-0013
0011-present-observability.patch | absorbed-into-0013
0012-exclude-offpump-presents.patch | absorbed-into-0013
```

取值含义：`in-tree`（本补丁就是当前分歧的权威描述）／
`retired-into-history`（描述旧谱系的分歧，现行树不再匹配，仅作留档、不参与重放）／
`absorbed-into-0013`（其内容已由 0013 承载）／`dead-not-in-tree`（有证据表明树里根本没有）。

### 每一行是怎么判出来的

对每个补丁取所有新增行（`^+`，去掉 `+++`，去首尾空白，长度 ≥12），
逐行与两棵树的全文行集合做**整行定长比对**（`grep -Fx -f`）：
`HEAD` 的 `FFF.Native` 全树（44 个文件）与上游 `a87046e` 的同一棵树。
`in-tree%` = 命中 HEAD 的新增行数占比；`headOnly` = 命中 HEAD 但**不**命中上游的行数，
即「只可能来自我们的分歧」⇒ 由 0013 承载（0013 = HEAD − 上游）。

| 补丁 | 新增行 | 在HEAD | headOnly | 两树皆无 | in-tree% | 判定 |
|---|---|---|---|---|---|---|
| 0013 | 500 | 500 | 453 | 0 | 100% | in-tree（定义即 HEAD−上游） |
| 0012 | 72 | 72 | 71 | 0 | 100% | absorbed-into-0013 |
| 0010 | 248 | 246 | 219 | 2 | 99% | absorbed-into-0013 |
| 0011 | 107 | 85 | 79 | 22 | 79% | absorbed-into-0013 |
| 0003 | 331 | 153 | 19 | 178 | 46% | absorbed-into-0013 |
| 0008 | 164 | 73 | 5 | 91 | 44% | absorbed-into-0013 |
| 0006 | 460 | 197 | 44 | 263 | 42% | absorbed-into-0013 |
| 0001 | 98 | 27 | 19 | 71 | 27% | absorbed-into-0013 |
| 0002 | 85 | 20 | 1 | 65 | 23% | absorbed-into-0013 |
| 0009 | 56 | **0** | **0** | **56** | **0%** | **dead-not-in-tree** |

- 0009 判 `dead-not-in-tree` 的依据是**双重**的：56 条新增行**一条都不在树里**；
  且其 50 个候选标识符（`[A-Za-z_][A-Za-z0-9_]{6,}`）中有 **17 个在 HEAD 与上游里都查不到**
  （`CalculateZoomViewportScale`、`useCachedViewTransform`、`shaderZoom`、`coverScale`、
  `viewportScale`、`clippedTop/Bottom/Left/Right`、`drawWidth/drawHeight`、
  `overflowX/overflowY`、`letterbox`，以及 3 个只出现在注释里的散文词
  `Further`/`disappears`/`progressively`）。网格单元 cover-zoom 这条路整个没落地。
- 其余 8 个判 `absorbed-into-0013`：它们的目标文件全部落在 0013 的 6 个文件范围内
  （已逐个核对 `^+++ b/`），且树里已带这套改动。需要说清楚的是：
  **逐行字面存活率并不都是 100%**（0001/0002/0003/0006/0008 只有 23%–46%）——
  重移植时这些补丁被改写/重排过，剩余行既不在 HEAD 也不在上游，属于**重移植过程中被丢弃或换写**
  的旧实现文本，而不是"还需要补打"的缺口：当前内核的真实分歧以 0013 为准。
  换句话说，`absorbed-into-0013` 断言的是"内容归属"，不是"文本逐字保留"。

## 验证方法（复现时注意三个坑）

正向等价性验证：`git archive a87046e FFF.Native | tar -x` 到沙盒 → 应用 0013 →
6 个文件与 `git show HEAD:<path>` 逐字节比对（含**单字符变异**的反向对照，确认判据会判红）。

1. **坑一：在仓库内的目录里 `git apply` 会静默空跑。** 沙盒位于主仓库 `C:/PLAN/3FCompare`
   之内，`git apply` 会按**主仓库**根解析路径，对 6 个文件全部打印
   `Skipped patch 'FFF.Native/...'` 然后**退出码 0**——看起来成功，实则一个字节都没动。
   必须让 git 认为自己在仓库外：
   `GIT_CEILING_DIRECTORIES=<沙盒的父目录> git apply --no-index ...`
   （ceiling 要指向 cwd 的**祖先目录**，设成 cwd 本身不生效）。
2. **坑二：本机系统级 `core.autocrlf=true`。** `git archive` 导出与 `git apply` 写入都会被
   转成 CRLF，与 blob（LF）逐字节比对必然全红。验证时加 `-c core.autocrlf=false -c core.eol=lf`，
   或先把导出件 `sed -i 's/\r$//'` 归一化，并**先核对归一化后的树等于上游 blob**再应用补丁。
3. **坑三（坑一的反面，2026-09-26 真栽过一次）：`git apply -R` 不带 `--check` 就是真回滚。**
   哨兵 `tools/check_kernel_ledger.py` 的"0013 反向可打性"探测原先拼成 `git apply -R <patch>`，
   ⇒ 每跑一次就把 `in-tree` 的状态从工作树上剥掉一层（−597 行），而它的退出码 0 又被读成
   "反向可打 ⇒ 内容确在树中 ⇒ **PASS**"。坑一是"退出 0 而什么都没做"，这条是"退出 0 且做了你不想要的事"，
   两者都会伪装成绿灯，且**破坏方向与判绿方向一致**，所以没有任何一项检查会喊。
   现在：两条分支都带 `--check`；哨兵另有 F 项（进入/离开各取一次 `FFF.Native` 工作树指纹，不等即红）。
   见证值 = `261da1267762`（修好的树 + 方案 A 未提交）；不等 ⇒ 先 `git checkout HEAD -- FFF.Native`
   再 `py .3fc_dumps/kernel-upgrade-20260924/apply_planA.py` 复活，别去猜是谁动的。


## 背景

`third_party/fff_project/` 已被 **解除 git submodule 跟踪**（整体不入库）：

- 它包含第三方内核源码 + 构建产物，依赖 `tools/构建全部.ps1` 从上游获取/构建
- `.gitignore` 中已整体忽略 `third_party/fff_project/`

⚠️ **可复现性风险**：归档分支与 tag 目前**只存在于本机**，远端 `Lake1059/FFF_Project`
没有 `3fcompare-kernel-*` tag。新机器裸克隆无法复现基线——`tools/构建全部.ps1`
会**报错中止**（不会静默退回到上游默认分支）。修复办法：

```bash
git -C third_party/fff_project push origin 3fcompare/zoom-viewport-cover
git -C third_party/fff_project push origin 3fcompare-kernel-2026.9.11.1
```

## 补丁规范（新增补丁时仍适用）

- **命名**：按用途/功能命名，如 `vrr-swapchain.patch`、`viewport-subregion.patch`
- **格式**：标准 `git diff` / `git apply` 可应用的统一 diff 格式
- **生成**：在 `third_party/fff_project/` 内修改后
  ```powershell
  git -C third_party/fff_project diff > tools/patches/<名称>.patch
  ```
- **顶层补丁的再生成**（每次合并/重移植上游后重跑一次，然后更新上面的状态表。
  ⚠ 截至 2026-10-17 顶层**没有**权威补丁（0014 退役入 `history/`，现行树是纯上游 `upstream-sync`）；
  下一条分歧出现时按顺延编号新增，不要复用 0013/0014 编号。下文命令里出现的旧编号只是当年写示例时的形状）：
  ```powershell
  git -C third_party/fff_project diff <新上游锚点> HEAD -- FFF.Native > tools/patches/<顺延编号>-baseline-vs-<短sha>.patch
  ```
  范围**只要 `FFF.Native`**：`PATCHES.md` 与宿主工程（`FFF.Player` / `FFF.Player.Tests`）
  不进补丁——宿主与上游逐字节相同，台账由另一条线维护。

## 应用（幂等）

`tools/构建全部.ps1` 是补丁应用的**唯一**入口，判定顺序：

1. `git apply -R --check` 成功 → 判定为「已应用」→ **跳过**（幂等的关键）
2. 否则 `git apply --ignore-whitespace` → 失败再退 `git apply --3way`
3. 仍失败 → **throw 阻断构建**，并提示用 `-SkipPatches` 明确放行

不再依赖「补丁 mtime 与标记文件比较」的时间戳启发式：
旧实现一旦有任一补丁失败就不刷新标记，下次重跑会把全部补丁再打一遍，
已应用的必然再失败 —— 形成永久卡死。

### 〔历史 · 2026-09-26〕实测：`Invoke-KernelPatches` 会怎么对待 0013

> ⚠ 本节的基线/SHA 读数（`b765a1f`、`-AllowKernelDrift` 等）是**写作当时**的实况，
> 保留作故障形状的记录；现行基线见 `tools/README.md` 的「内核基线」一节。
> （`-AllowKernelDrift` 参数早已不存在，见 `tools/README.md` 参数表。）

对顶层现存的唯一补丁，三步探测在内核仓库里实测：

| 探测 | 命令 | 退出码 |
|---|---|---|
| ① 反向（幂等判据） | `git apply -R --check --ignore-whitespace 0013…patch` | **0** |
| ② 正向 | `git apply --ignore-whitespace` | 1（改动已在树里，预期如此） |
| ③ 三方 | `git apply --3way` | 1（同上） |

⇒ 脚本走第 ① 步即 `continue`，打印「⏭ 0013…已应用，跳过」，**不会 throw**。
（②③ 只用了 `--check`  dry-run 形态，未真的改树。）

⚠️ 但这只代表 `Invoke-KernelPatches` 这一步。**在它之前**脚本还有一道基线闸门：
`$KernelBaselineSha` 仍是 `b765a1f8…`（旧归档 `3fcompare-kernel-2026.9.11.1`），
而内核 HEAD 已是 `a8e437e`（merge 上游 `a87046e` 之后）。于是
`Ensure-KernelBaseline` 会先尝试 `git checkout b765a1f8…` 把内核**倒回旧基线**，
`Assert-KernelHeadMatches` 随即 throw（除非加 `-AllowKernelDrift`）。
也就是说：**当前 `tools/构建全部.ps1` 在到达补丁重放之前就会中断**，
而且一旦真的回到 `b765a1f8` 那棵树，0013（针对 `a87046e`/`HEAD`）必然三个探测全失败 → throw。
按铁律（只判读、不改用户脚本）这里只报告，不代改：需要把 `$KernelBaselineSha`/`$KernelBaselineTag`
同步到新归档提交后，重放链路才真正可用。


## API 16 内核进出货树：实测代价（2026-09-30）

> ✅ **【已解决 · 2026-09-30】** 下面记录的"API 16 只在 `3fp/kernel`、fork 树还是 API 15"的困局
> 已按文末"该走的路"**走完**：人工重移植落地为 fork 树 `3fcompare/api16-kernel` 分支的两笔提交
> （f344c3c 落 API 16 语义、3940990 记账），基线推进到
> `3fcompare-kernel-2026.9.30.1 / 3940990`，重放件换为 `0014-baseline-vs-a87046e-api16.patch`
> （0013 退役入 `history/`），门禁 `[2/11]` 配对检查转绿。以下过程记录保留，供下次重移植参考。
> （2026-10-17 再修订：0014 本身也已退役入 `history/`——现行树换到 `upstream-sync` 谱系后
> 它不再可匹配，见文件头"状态说明"；本节记录的是当时代的代价，正文不动。）

当时现状：托管侧要求 API 16（`Fff3FpEngine.cs` 的 `ConfigVersion = 16`、`FFF3FP_SetFitLimitToNative` 等 26 个入口），
而 `KernelDllPath` 指向的 `third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll` 是
**HEAD `b661365` 编出来的 API 15**，缺 `SetFitLimitToNative` ⇒ 门禁 `[2/11]` 判红（配对守卫打
`kernel=15 / managed=16`）。API 16 的全部实现只存在于 PR 工作树 `3fp/kernel`（HEAD `33ac900`）。

**两条捷径都试过、都不成立**（读数在 `tests/output/prcheck/`）：

| 捷径 | 实测 | 结论 |
|---|---|---|
| 把 `KernelDllPath` / 部署根指向 `3fp/kernel` | 两树文件级差异：`VideoRenderer.cpp` 有 **487 行是 fork 独有**、`PlayerSession.cpp` 54 行、`FFF.Player.Api.h` 18 行、`PlayerApi.cpp` 7 行（`3FR/Core/Session.cpp` 为 0） | ❌ 换树 = 静默丢 fork 侧内容（已知例证：泵线程 present 观测 `NotePumpOwnedPresent*` 这组函数在 `3fp/kernel` 里根本不存在）；且 `构建全部.ps1` 的补丁/ffmpeg/vcpkg/runtime 全都挂在 `$ForkRoot` 上，只改两处会让"校验的"与"进包的"不是同一份 |
| 把 PR 补丁直接落到 fork 树 | `git apply --check -p1 --ignore-whitespace` 逐文件：**8 个冲突 / 6 个干净**（冲突：`FFF.Player.Api.h`、`PlayerApi.cpp`、`PlayerSession.h/.cpp`、`VideoRenderer.h/.cpp`、两个 `FFF.Player` VB；干净：`ShaderBytecode.h`、`HdrProcessor.h`、`WasapiCapture.cpp`、`VideoMuxer.cpp`、`FFF.Native.rc`、`KernelTimelineTests.vb`） | ❌ 不是机械步骤，是**手工重移植**（下面第 1 步），且必须把 6 个干净文件的语义与 8 个冲突文件的移植一起再验一轮真机 |
| 把 `3fp/kernel` 编好的 DLL 拷进 `third_party/.../x64/Release/` | 能做到"绿" | ❌ **禁止**：树不能再生产自己的产物，下次 `构建全部.ps1` 就把它覆盖回 15；这正是本目录铁律要防的形状（产物与源码脱钩） |

**该走的路（就是本目录既有流程）**：按 `third_party/fff_project/PATCHES.md` 做人工重移植 —— 在 fork 的分支上
把 API 16 的 9 组改动逐组落地（`sdrScRgbMode` + `WantsScRgbPresentationPath`、纸白锚定与 DISPLAYCONFIG 兜底、
gamut 三态 + `ShaderBytecode.h` 重生成、有符号 dest/对称钳位/`z<1`、`SetFitLimitToNative`、present 能力位与偏好分离、
日志汇、3FR 两处、并发与生命周期三处），提交 → 更新 `PATCHES.md` 分类清单 → 打新归档 tag →
同步 `tools/构建全部.ps1` 的 `$KernelBaselineSha`/`$KernelBaselineTag` → `tools/构建全部.ps1` 重建 →
`[2/11]` 应转为绿。**注意版本语义**：`sdrScRgbMode` 是配置字段新增 ⇒ `PlayerApiVersion 15→16` 与
`FFF3FPConfiguration` 尺寸（80→84 B）必须一起进，否则重演 09-27 那条"上游改布局不升版本"的教训。
PR 正文/提交信息见 `3fp/patches/combined-pr-body.md`、`combined-commit-message.txt`。

⚠ 上面那段"`$KernelBaselineSha` 仍是 `b765a1f8…`、构建会在到达补丁重放前中断"的陈述**已过期**：
2026-09-30 实测 `tools/构建全部.ps1:31` 为 `b661365…`，与 fork HEAD 相同，门禁 `[1/11]` 打
`✅ 内核基线 head=b66136550a99 tag=3fcompare-kernel-2026.9.27.1`。原文保留是因为它记的是当时的真实故障形状。

## 工作流

1. 修改 `third_party/fff_project/` 内源码
2. 提交到 `3fcompare/zoom-viewport-cover` 分支（补丁文件仅作留档时可选导出）
3. 更新 `third_party/fff_project/PATCHES.md` 的分类清单
4. 打新归档 tag，并同步 `tools/构建全部.ps1` 的 `$KernelBaselineSha`
5. 重建：`tools/构建全部.ps1`
