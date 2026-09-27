# tools/patches — 内核自定义补丁存档目录

> **状态说明（2026-09-26 修订：队列重建）**
>
> 本目录现在是**唯一权威补丁 + 历史归档**两层结构：
>
> - **顶层只保留 `0013-baseline-vs-a87046e.patch`**：内容就是
>   `git -C third_party/fff_project diff a87046e HEAD -- FFF.Native`，
>   即「已提交的内核分歧」全集（6 个文件，+578 / −115 行）。它是可重放的：
>   对 `a87046e` 的原始树正向应用后，6 个文件与 `HEAD` 的 blob **逐字节相同**。
> - **旧 9 个补丁整体移入 `history/`**（未删除、未改写，SHA1 逐一核对一致）。
>   `tools/构建全部.ps1` 的 `Get-ChildItem $PatchesDir -Filter *.patch` **不递归**，
>   因此 `history/` 下的补丁不再参与重放。
>
> 上游升级的正确流程见 `third_party/fff_project/PATCHES.md`（人工重移植 + 打新归档 tag），
> 体检信息用 `tools/更新内核.ps1` 查询。

## 补丁状态表（机器可读）

```patch-status
0013-baseline-vs-a87046e.patch | in-tree
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
- **0013 的再生成**（每次合并上游后重跑一次，然后更新上面的状态表）：
  ```powershell
  git -C third_party/fff_project diff <新上游锚点> HEAD -- FFF.Native > tools/patches/0013-baseline-vs-<短sha>.patch
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

### 实测：`Invoke-KernelPatches` 会怎么对待 0013（2026-09-26）

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


## 工作流

1. 修改 `third_party/fff_project/` 内源码
2. 提交到 `3fcompare/zoom-viewport-cover` 分支（补丁文件仅作留档时可选导出）
3. 更新 `third_party/fff_project/PATCHES.md` 的分类清单
4. 打新归档 tag，并同步 `tools/构建全部.ps1` 的 `$KernelBaselineSha`
5. 重建：`tools/构建全部.ps1`
