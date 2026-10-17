# HDR10+ 之后的第二支 PR —— 其余全部本地工作合成一支（2026-10-09）

> **一句话**：一支 PR 装下 `#16` 之外的全部内核/宿主工作 —— 分支 `upstream/kernel-all` @ **`72c0aec`**，
> 父 = **上游 master 本体 `12d8afc`**，34 文件 / +27,626 −21,931（其中 89.6% 是两份生成物字节码的整表重排，
> 手写面 `VideoRenderer.cpp` +1,100/−80 等约 2,200 行）。**已 push 到 fork，未提 PR**（那一下归你）。

打开就是新建 PR 页。⚠ 分支现在是**两笔**（`4878da0` 内容 + `72c0aec` 构建修复），GitHub 只会用**首笔**的说明填描述 ⇒ 要么把第二笔的说明并进去，要么先压平成一笔：

```
https://github.com/Lake1059/FFF_Project/compare/master...luoye-cpu:FFF_Project:upstream/kernel-all?expand=1
```

分支从 `4878da0` 前进到 `72c0aec` 的原因（审查轮当场发现并修的）：`FFF.Player.Tests/Program.vb` 调用了仓库里已不存在的 `Assert/CheckPlaylist/CheckSubtitleBuffers/CheckPlayback`（上游在自己的 `ce6f51c`/`5881872` 里删掉了宿主例程，我们那条 merge 把手工裁决丢了半），而 `3F_Project.slnx:9` 收录该工程 ⇒ **解决方案编不过**。修法=回到上游的 `Return 0` 并只保留我们自己的 `--lyrics-kit-regression` 派发。同笔还升了 `.rc`（16→19，否则它自己的发布对等门会拒）与一处 C4267（`IamfAudioDecoder.cpp:132` 形参类型）。台账：`3fp/docs/78-两支已提交PR的全面审查台账（2026-10-09）.zh.md`。

## 一、为什么不是三支

我先前按"音频 / 性能 / 交互"规划三支，**实测否掉了**，三条都可在盘上重跑：

1. **两组互锁**：`b5db0a4`（性能）写 `cachedVideoSettings_.viewRotation`，而该结构体字段由 `f1e3019`（交互）引入
   （冲突原文 `VideoRenderer.cpp:2959-2969`）。lane S1 六种排列全测过：**C→B→A = 8 冲突是下限**，
   与整条重放相同 ⇒ 拆分只多不少（A 单独 2 冲突、B 5、C 3；`f1e3019` 一笔就撞 9 个文件 / 216 region）。
   根因是区间里有 **3 个 merge 提交**（`69c405d` `03090da` `a44cbda`），cherry-pick 丢弃它们的手工裁决。
2. **IAMF 在上游编不出来**：`vcpkg.json` 与上游逐字相同（只有 `libass/libbluray`），vcxproj 引
   `..\third_party\libiamf\…` + `iamf.lib;oar.lib`，而 `.gitignore:46 third_party/` ⇒ 新克隆必然链接失败。
   ⇒ 本支补 `tools/准备Libiamf.ps1`（钉 `b276f4371a…`，Release|x64，static，`/MD`）+ README 前置说明。
3. **自适应政策与 layout-0 几何修复不能拆**：`docs/76 §六`「四刀缺任何一刀，TP 立刻复刻本裁切」；
   而工作树里那份修复的折入条件引用 `floatRgb`（第四条工作线 JPEG XR 的字段）。⇒ 本支把**政策 + 四刀 + JPEG XR
   浮点输入**一起带上，避免"带政策不带几何"的已知缺陷形状。

三支合成一支之后，`git merge-tree` 预演的 9 文件冲突面整块消失（不再有"两支各自对上游、互相冲突"这件事）。

## 二、这一支的构造与验收（全部当场跑）

| 项 | 读数 | 件 |
|---|---|---|
| 内容来源 | `git checkout feature/vsr-and-rotation -- .` + 工作树 4 个未提交件逐字节覆盖；**逐文件 sha256 与实验树工作树相等**（30 条路径全对，另加 1 条只存在于工作树的 `文件关联管理器.vb`） | `prsplit3/inventory_groups.py` |
| 与 tip 的差 | 恰好那 4 个未提交件（`PlayerSession.cpp` 14、`ShaderBytecode.h`、`VideoRenderer.cpp` 143、`文件关联管理器.vb` 3） | `git diff --cached --stat feature/vsr-and-rotation` |
| 编译 | `-t:Rebuild` Release x64 **0 error / 0 warning**（修掉 2 个 C4267 之后重跑） | `prsplit3/build-scrubbed.log` |
| 件 | 与 `72c0aec` 同树的构建：`FFF.Native.dll` 1,319,936 B / sha256[:16] `269F9D5A6CBF7DAE`（已归档 `artifacts/FFF.Native.kernelall-269F9D5A6CBF7DAE.dll`；修复前那笔留作对照 `…kernelall-E2E35BC74A6BD2FB.dll`，**它不对应现在这支**）；`check_kernel_exports.py` **API version = 19 / 结论 OK** | `_3fp-fix/artifacts/FFF.Native.kernelall-*.dll` |
| 版本四处对齐 | `PlayerApi.cpp` = 19、`FFF.Native.rc` = 19、`FFF.Player.vbproj <PlayerApiVersion>` = 19、宿主 `期望API版本` = 19UI（**`.rc` 原本还是 16**，本支修正） | 第二笔改动，已压进同一笔 |
| 补丁可用性 | `git format-patch -1` ⇒ 2,385,733 B；在 `12d8afc` 干净 worktree 上 `git am --keep-cr` **exit 0**，落地树哈希 `aea1f9bb…` **与分支树相同** | `prsplit3/0001-kernel-followup.patch` |
| 私货引用 | `docs/NN` / `3fp/` / `3FCompare` / 绝对私有路径 / 其它仓 fixture 路径：扫出 **12 → 17 处替换 → 0** | `prsplit3/list_branch_refs.py`、`scrub_branch_refs.py` |

`git am` 那 14,676 行 whitespace 警告来自生成物数组的既有版式，不是新增语义。

## 三、写进提交说明的"未验"清单（不是客套，是没有件）

- **宿主 `FFF.Player` 本机编不过**（缺 `LakeUI.*`，910 个 BC30002）⇒ VB 面按构造逐项核过字段序/宽度/两处版本门，**未经编译**。请上游编一次。
- **RTX VSR 未量**：本机 `NVIDIA App\console.log`（824 KB，10-09）里 VSR 只有两段、均 `isEnabled:false`/`quality:0`，
  一次 `true` 都没有 ⇒ 这一支**不做任何 VSR 像素级断言**，只说"接口与几何通路已接上、未实机验证"。
- **IAMF 声道置换**只对照 libiamf 自身的渲染次序（`bed_order` 33/33 那份外部件），**没有**内核侧对已应用置换的抓取判据。
- `tools/准备Libiamf.ps1` **未端到端跑过**（跑它要往本仓之外写构建目录）；已核的是它读的 5 条源路径与它落的 10 个文件名逐条在盘上对得上。
- **本支不加测试**（除随带的 `FFF.Player.Tests/LyricsKitRegression.vb`；其素材包不随仓库分发，
  缺目录现在退 2 并打印原因，不再写死 `C:\PLAN\kit\kit`）。
- lane S3 普查：除 `_3fp-fix/laneL1`、`laneM` 外**没有任何探针 stdout**，各笔提交说明里的 `Regression: …` 是复述；
  最常被引用的 `0 / 3,686,400` 遮挡字节出现 9 次、全仓 0 命中 ⇒ **该数没写进这支的提交说明**。

## 四、复现

```bash
git -C C:/PLAN/3FCompare/_3fp-fix/prsplit3/work log -1 --stat | head -40
git -C C:/PLAN/3FCompare/_3fp-fix/prsplit3/work diff --shortstat upstream/master..HEAD
py C:/PLAN/3FCompare/_3fp-fix/prsplit3/list_branch_refs.py     # 期望 hits: 0
py C:/PLAN/3FCompare/tools/check_kernel_exports.py \
   C:/PLAN/3FCompare/_3fp-fix/prsplit3/work/FFF.Native/x64/Release/FFF.Native.dll
```

推送命令（已执行，留档）：

```bash
cd /c/PLAN/3FCompare/_3fp-fix/prsplit3/work
GIT_TERMINAL_PROMPT=0 git push https://github.com/luoye-cpu/FFF_Project.git \
  upstream/kernel-all:refs/heads/upstream/kernel-all
# 复核：ls-remote 回 72c0aeca0a5b5980fec47c752adec200c42c859a == 本地 HEAD
```

## 五、与 `#16` 的关系（要如实说）

`#16`（HDR10+/ST 2094-40，18 文件）**不在这一支里** —— 这一支来自实验树 `3fp/kernel`，那棵树从没带过注入那套。
两支都改 `VideoRenderer.{cpp,h}`、`PlayerSession.{cpp,h}`、`PlayerApi.cpp`、`FFF.Player.Api.h`、`ShaderBytecode.h`
与 `FFF3FPSnapshot` 尾部字段：`#16` 把 API 定到 **17**，本支定到 **19**（17 自适应字段 / 18 解码线程档位 / 19 `iamfContentChannels`）。
⇒ **先合 `#16`，这一支必须 rebase 并重新编号**（快照尾部字段顺序也要重排）。lane S1 把这条列为 10 个待裁语义点之首。
