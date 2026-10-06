# 更新日志

本项目采用语义化版本（MAJOR.MINOR.PATCH），提交信息统一带 `[vX.Y.Z]` 前缀，
版本号、git tag、`csproj` 的 `<Version>` 三者保持同步。

> 本文件按**版本号倒序**排列（最新在最前）。
> ⚠ 曾因 `[v0.2.5]` 被追加到文件**末尾**（在 `v0.1.0-beta` 之后）而被误判为"缺失"，
> 现已修正。核对版本时直接看文件开头，不要只看前几行就下结论。

> ⚠ **`[未发布]` 段（2026-09-28 ~ 09-30）不等于根仓提交历史。**
> 这些条目记录的是**当前工作树与嵌套仓 `3fp/kernel` 的状态**：其中形如 `ff0f17a`、`33ac900`
> 的提交号属于 `3fp/kernel` 的 PR 分支（该目录本身被 `.gitignore` 忽略、不进主仓），
> 不是主仓库 `main` 的提交。核对主仓历史请用 `git log`，不要用本文件的 SHA 反推。
> 另：这些变更尚未体现在版本号上——`csproj` 仍为 `0.3.1-beta`，与已打 tag 的
> `v0.3.1-beta` 同名不同内容；出下一个包前应先推进版本号。

## [0.3.2-beta] — 2026-09-30 · 仓库整理：git 追踪收敛 + 结构文档纠偏 + 命名空间归位

- 🔒 **追踪整理**：`.gitignore` 补齐 4 类缺口——`/3fc-followhand/`（嵌套 git 仓的整树副本，567 文件）、
  `/3fc-backups/`（一次性备份快照，含 5.3 MB tar）、`tests/output/**` 改整目录忽略（原只按扩展名拦，
  272 个 `prcheck/master-tree/` 的上游 `.vb/.h/.cpp/.resx` 源码快照漏网，与整目录忽略的
  `third_party/fff_project/` 规则自相矛盾）、`*.obj` / `*.zip` / `*.tar` / `tools/_*.ps1`。
  未追踪文件 **441 → 0**；应入库的 91 个（3fp/docs 24、3fp/tools 38、3fp/patches 19、tools 5、
  tests 1、3fp/samples/tools 2、tools/patches 2）已入库。已跟踪台账用 `!` 白名单放行。
- 🗂 **归档**（`git mv` 保留历史）：`docs/HANDOFF-*` ×2、`docs/46-交接文档` → `docs/archive/`；
  `3fp/docs/05/10/12/19` → `3fp/docs/archive/`；`3fp/STATUS.md` 重构为短状态页（641 → 398 行），
  头部变更叙事与「六、工作日志」迁入 `3fp/docs/archive/status-log-2026-09.md`。
- 🧹 **删除 `tools/SettingsDialogPreview/`**：其引用的 WinForms `SettingsDialog` 类在 Avalonia 迁移时
  已删除（全仓 `class SettingsDialog` 零命中），且 csproj 硬编码指向不存在的 `src/3FCompare.App` 工程
  ——该工具自迁移起就不可编译。
- 🏷 **命名空间归位**（目录改名后遗留）：`Themes/*` 三文件 `_3FCompare` → `_3FCompare.Themes`；
  `Localization/LanguageManager.cs` `_3FCompare.App` → `_3FCompare.Localization`；
  23 个文件的 `using _3FCompare.App;` 全量迁移 + 7 个主题使用方补 `using _3FCompare.Themes;`。
  安全性依据：`_3FCompare.App` 精确命名空间下**只有 LanguageManager 一个类型**，无遗漏面。
  `Platform/` 抓帧组保留 `_3FCompare.App.Capture`（csproj:172 有注释说明的"零改动引用"决策，不动）。
- 📝 **文档纠偏**：README（Avalonia 12 / v0.3.1 / submodule 说法 / E3 冒烟始终走演示引擎 /
  裸克隆需先 `git clone .3fc_kernel_baseline.bundle` / PACKAGING_SPEC 链接 / 内嵌旧日志封档）、
  tools/README（参数表 4 个不存在的参数、基线 3940990、26 个必需导出、补记构建期硬依赖的
  `源码指纹.ps1`）、tests/README（补 Platform.Tests）、3fp/README（`-AllowKernelDrift` 已不存在、
  分支不固定）、3fp/samples/README（17/11 口径矛盾）、CHANGELOG 头部（嵌套仓 SHA 归属警示）。
- 📜 **历史污染核查（结论：干净）**：62 提交、856 可达对象中——可疑二进制扩展名
  （dll/pdb/exe/zip/7z/mp4/mkv/obj/lib/ilk/nupkg/so/a）**零命中**；`third_party/fff_project`
  历史上仅以 gitlink（submodule 指针）形式出现、无内容入库；`tests/output` 仅 3 个台账文本；
  最大 blob 为有意入库的 `.3fc_kernel_baseline.bundle`（≈2 MB）与 `wip-rescue` 台账补丁（2 MB）。
- 🏷 csproj 版本 `0.3.1-beta` → `0.3.2-beta`（两工程同步）：未发布变更此前与已打 tag 的
  `v0.3.1-beta` 同名不同内容，先推进版本号再出包。

## [未发布] — 2026-09-30 · 内核 PR 落到上游 tip：基线 33ac900→5d2cb21，剔除已删测试文件，提交已建 ff0f17a

- 🔴 **上游在我们写 PR 期间前进了 3 笔**（tip `33ac900` → `5d2cb21`），其中**删掉 `FFF.Player.Tests`
  整个工程**（`KernelTimelineTests.vb` −164、`DiscPlaybackTests.vb` −266、`Program.vb` −6253、
  `.vbproj` −32、`Native/*` 全删）与 **`tools/测试3FP内核.ps1`**（−43）；另改了
  `VideoRenderer.cpp`（+53，ColorExtension 增强帧）、`播放器控制器/媒体信息/呈现器.vb`。
  ⇒ 我们新增的视口回归用例**没有落点**：`git apply --check` 报
  `FFF.Player.Tests/KernelTimelineTests.vb: No such file or directory`，`--exclude` 该文件后 exit=0。
- ✅ **处置**：基线改到 tip `5d2cb21`（`merge-base --is-ancestor 33ac900 5d2cb21` = 0，旧基线仍在
  tip 上）；PR **剔除测试文件**（14 文件 +13,882/−13,118）；commit message 与 PR 正文的 Tests 段
  重写——不再引用已不存在的套件与用例，改为下游宿主几何/速率判据的实测读数，并**明说"本提交不带
  回归用例"及原因**，避免评审把它当遗漏。
- ✅ **编译验证（实跑）**：`tools/构建3FP.ps1 -Configuration Release -SkipTests` ⇒ **0 警告 0 错误**，
  内核与 `FFF.Player` 均产出。**必须 `-SkipTests`**（测试工程已被上游删除，否则必红）。
- 📌 **两个环境坑（都不是本 PR 的问题）**：① `FFF.Player.vbproj` 用 `..\..\LakeUI` 引**仓库外**的
  LakeUI 项目 ⇒ 验证用 worktree 必须建在 `3fp/<一层>/`，放到 `tests/output` 会冒 455 个
  `未定义类型“LakeUI.*”`；② worktree 缺 `third_party`/`runtime` 需 junction 复用主树，
  删 worktree 前**必须先 `rmdir` 掉 junction**（递归删除会跟随链接）。
- 🧹 **规整**：散落的临时 worktree（`tests/output/wt-tip`、`3fp/pr-check-wt`）已全部清理，
  `git worktree list` 只剩主树 `3fp/kernel` 与既有的 `.3fc_dumps/pr-api16-wt`。
- 📦 **状态：已推送**（2026-09-30）。提交 `ff0f17a` → fork 分支 `upstream/api16-scrgb-viewport-pr`；
  推送时上游 tip 仍为 `5d2cb21`，本分支直接建在它上面 ⇒ 无需 rebase，mergeable 应直接 clean。
  出货补丁重生成 md5 `5737f1d0e4496382987d4794f84eaf65`，旧版（基于 33ac900、15 文件）另存
  `combined-pr-vs-33ac900.patch.bak`。
  **开 PR 链接**（base 必须是上游，勿用 push 回显里 fork 自家的那个）：
  `https://github.com/Lake1059/FFF_Project/compare/master...luoye-cpu:upstream/api16-scrgb-viewport-pr?expand=1`
  —— 正文取 `combined-pr-body.md` 中 `---8<---` 之间的英文段（含"本 PR 不带回归用例"的说明）。

## [未发布] — 2026-09-30 深夜 · 内核 PR 第二轮独立复审：出货物文案对齐代码，补丁进入可提交态

复审对象 = 一树一 PR 的出货物（`3fp/patches/combined-pr.patch` 15 文件 +13,981/−13,118 +
`combined-commit-message.txt` + `combined-pr-body.md`）。方式：**只读源码核对**。

- ✅ **上一轮"已上报未修"里 4 条其实已收口**（旧记录未回头更新，本轮逐条核对源码）：
  `extensionEligible_/extensionAttempted_` 的 `Render()` 写点已在 `deviceMutex_` 内
  （`VideoRenderer.cpp:4307`，4296 已早退未持锁分支）、`ReleaseDeviceObjects()` 两个调用点
  （`:5356`/`:5424`）均持锁；内核日志行 `\n` 已单独发射（`:2242-2244`）；
  `kShaderGamutTristate` 全树已无；`FFF3FPLogCallback` 注释已写"异常逃逸即终止进程"
  （`FFF.Player.Api.h:232-235`）。**仍在**：`extern` 两种写法并存（纯风格）、
  `swapAllowTearing_` 非原子（实际靠 `presentMutex_` 串行）。
- 🔴 **新发现（诊断层，不是链错）**：三处色彩门禁里只有 `EnsureSwapChain`（`:2409`）拿到
  真实位深（参数），`SetWindow`（`:2008`）与 `SetColorMode`（`:2109`）**仍传成员**
  `sourceBitDepth_`，而它们在首帧前运行 ⇒ 首帧到达前 `ActualColorMode()` 对"即将走 scRGB 的
  10bit SDR 源"报 `MapToSdr`，`fallbackReason_` 也写成"仅 HDR/广色域可用"。**最终链由
  `EnsureSwapChain` 定夺，链是对的**，漂的是诊断。而 commit message 原写"bit depth is a
  parameter, not the member"⇒ 与代码不符，评审读到会失信任。
  ⇒ 已改**出货物文案**而非代码：commit message 那句改为"只有交换链门禁拿到参数、另两处只能判
  HDR/广色域并留下深度项"，并在 commit message 与 PR 正文各加一段 "Known, deliberately not
  changed here"（说明为什么不在本提交里改：需要把位深带进会话建立或推迟诊断，属另一处改动）。
- ⚠ **本轮未做的事（不得当成验过）**：所有命令审批超时 ⇒ **未重编译、未跑
  `测试3FP内核.ps1`、未复跑 `verify_combined_patch.sh`**，"补丁 md5 `aa0a4e86…` 与工作树仍
  一致"这条**本轮没有实测**；本轮**未改任何源码**（只改出货物文案），按理不影响该一致性，
  但提交前仍应补跑一次自检。
- 📌 **状态**：素材齐备（patch + 一笔英文 commit message + 中英 PR 正文 + 提交步骤），
  只差远端动作授权：`fetch` 上游确认 tip → 新名分支（**勿沿用 `upstream/api16-viewport-pr`**，
  那条线删过 164 行测试）→ `git apply --3way` → `commit -F` → `push` → 开 PR。

## [未发布] — 2026-09-30 · 内核 PR 独立复审：修回我自己写坏的三处，新内核能力进发布说明

审查方式：逐行读 `3fp/kernel` 的 14 文件 diff（手写部分 +719/−98），回读每一处被引用的源码，
每条判据配变异臂。**下面三条缺陷全是我本轮自己写进去的**，不是隔壁 agent 的回补。

### 1) `FFF3FP_SetPresentConfig`：三个后果，最重的一条是"整个入口是 no-op"
第一版把「交换链是否带 `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`」（`swapAllowTearing_`，链创建时由
`CheckFeatureSupport` 写）**与**「用户是否要撕裂呈现」合成同一个变量：
- 开启：`PresentCurrentFrame` 只在 `interactiveMove` 分支读它，正常播放分支硬编码 `Present(1, 0)`
  ⇒ **调用成功但呈现节奏完全不变**（我的头文件注释却写着"让 VRR 显示器按自身节奏扫描输出"）。
- 系统不支持撕裂时开启：下一次 `ResizeBuffers` 会在**没带该 flag 建的链**上补 `ALLOW_TEARING`
  ⇒ `DXGI_ERROR_INVALID_CALL`，改尺寸直接失败。反向（不支持却曾带过 flag）同形。
- 关掉：把能力位一起清了 ⇒ 设备/链重建后偏好丢失，注释却承诺"remembered across re-creation"。
另有裸 `bool` 被会话工作线程写、呈现线程读（本仓跨线程一律 `std::atomic` 的约定）。
⇒ 拆成 `swapAllowTearing_`（能力，仍由创建路径写）+ `tearingRequested_.atomic<bool>`（偏好），
撕裂只在"能力 ∧ 偏好"同时成立时用于正常呈现分支。**实现提到声明的水位**，没有反过来放宽注释。
- 连带更正：README 里"2026-09-16 已实测支持 `SetPresentConfig`"那行读数来自**另一棵树**
  （`third_party` 的自研 VRR 补丁），不能迁移成"上游 PR 树也已验"；真实撕裂节奏仍待有 VRR 的屏上复测。

### 2) 上游新用例 `测试视口封顶与缩小`：取样点写死 640×360，只在 100% 缩放成立
`Program.vb` 是 `HighDpiMode.PerMonitorV2`，`ClientSize` 是 DIP ⇒ 150% 缩放下后缓冲其实是 960×540，
而封顶盒是**源原生尺寸**（不随缩放变）。写死坐标会把"盒内/盒外"读反，报成几何错——
本机 96 DPI 恰好掩盖了它。⇒ 改 `GetClientRect` 实测设备像素，三点全表达成"中心 + 源几何推出的偏移"
（dy 170 > 90 > 60 > 45 > 20），并加两条夹具前提自检（后缓冲须大于源、须容得下 dy=170）。
顺带把"封顶内"与"缩小外"收成**同一个点**：它必须从亮翻到黑，比两个不相干点更硬。

### 3) `ShaderBytecode.h` 被生成器重排成整文件重写（48k 行）
生成器的行格式（每项 `%6d, `）与在库版本（每行 6 项、首列宽 7 其余宽 4、行尾一个空格、
数组末项不带逗号）不一致 ⇒ 只有 1 个数组真的变了，diff 却是 24,112+/24,075− 全文重写，
`git log -p` 对该文件就此失效。⇒ 生成器复刻原格式；现 **13,041+/13,004−**，
按数组逐项比对证明**其余 6 个数组逐字节全等**，只有 `FFFPixelShaderBytecode` 134,540→134,752 B
（DXBC 偏移 4 起是源码哈希，P3 分支使长度增长）。

### 判据与读数（本轮全部实跑）
| 项 | 读数 |
|---|---|
| 上游工程 Release 构建 | 0 警告 0 错误（`FFF.Native` + `FFF.Player` + `FFF.Player.Tests`） |
| 上游内核套件（改后） | exit=0；`FIT-LIMIT: 后缓冲=640×360 铺满底=1.000 封顶外=0.000 封顶内=1.000 缩小内=1.000 缩小外=0.000` |
| 变异臂 M1（`z<1` 退回 no-op） | **红**：`缩放 0.5 没有真的缩小：同一点 {X=320,Y=240} 仍是画面（亮度 1.000）` ⇒ 判据有牙 |
| 变异臂 M2（封顶被忽略） | **红**：`封顶后 {X=320,Y=350} 仍是画面（亮度 1.000）⇒ 适配盒没有封顶到源原生尺寸` ⇒ 两臂各自抓各自那条断言 |
| 恢复后重建 + 套件复跑 | exit=0，同一行 FIT-LIMIT 读数复现；变异脚本自检 `RESTORE sha0=1D2CAD404AC7 sha1=…equal=True`（源码逐字节还原） |
| 产物守卫 `check_kernel_exports.py` | OK，API 16，必需入口全在（含 `Redraw`/`SetPresentConfig`/`SetLogCallback`/`SetFitLimitToNative`） |
| 配对守卫 `check_kernel_pairing.py` | OK：kernel=16 / managed=16；`RenderTargetInfo` v2 ↔ v2 |
| `dotnet test` Platform.Tests | 475 通过 / 0 跳过（含 `ShippedShortcutsNote` 2 项，守的就是随包 `使用说明.txt` 模板） |
| `pack.ps1` 语法自检 | ParseErrors=0 |

### 更正：上面那行"上游套件 exit=0"**不可复现**，本机现在判不了精确呈现计数（A/B/A 已排除本 PR）

03:0x–03:2x 复跑同一份代码 18 轮，`--kernel-timeline-regression` 大面积红在
`确保(已呈现视频帧数 = 期望帧数)` 这一条（短落 1–13 帧，落点随机：`CPU late-video 13/15`、
`GPU short 27/30`、`CPU silent 24/30`…）。**交错 A/B/A 对照**（每块 6 轮，`tests/output/prcheck/ab_result.txt`）：

| 块 | 内核 | 结果 |
|---|---|---|
| A | 本 PR 的 API 16 | 5/6 红（短落幅度 1–3 帧） |
| B | **改动前的 API 15 快照**（`.3fc_dumps/kernel-pre-followhand-20260929`，绑定门槛临时钉 15） | **6/6 红，且短得更狠**（`8/15`、`7/15`、`11/15` ⇒ 缺 27–53%） |
| A2 | 本 PR 的 API 16 | 6/6 红 |
⇒ **不是这份 PR 引入的回归**：昨天那份内核同样红，幅度更大。今天那句 present 改动的对照臂也等红（见上表 M 记录），两臂合起来把内核侧摘干净了。
⇒ 真正的判据缺陷在**套件自己**：`等待()` 用的是 15 s 的墙钟（所以"播完"永远先成立），红是"到点少呈现几帧"——
在 RTSS 钩子在场、GPU/DWM 调度被前台竞争牵动的本机，这类"恰好 N 帧"的精确计数断言**不是可用判据**。
对照：3FCompare 的 `--selftest` 用速率型判据（`presented +60/秒`、`视图变换压力测试`），同一时段 4 步几何判据全绿
（`真 1:1 封顶通过：zoom=1 ⇒ Dest 1280×720，比值 1.000`；`缩放下限通过：z=0.6 ⇒ 1197×673`；`锚点 ≤0.5px`）。
⇒ 所以本仓此前所有"上游套件 exit=0"的读数**只在当时那台机器的状态下成立**，不能当成随时可复现的门禁；
要么给套件补"窗口必须可见/前台 + 记录 elapsed"的前置自检并让不满足时判 SKIP，要么把该断言改成
"排空完成 ∧ drop=0 ∧ coalesce=0"。这条留给上游，**不夹进本 PR**（本 PR 不碰该断言）。
本机状态：`MSIAfterburner` + `RTSS` + `RTSSHooksLoader64` 在跑，属 3FCompare 的使用态，未按禁项去关它——
判法只有对照臂与判据口径，没有"清环境拿绿灯"。


### 内核在 3FP 宿主里的可用性（28 个自带入口，全部带退出码）

驱动：`tests/output/prcheck/run-3fp-suites.ps1` → `run3fp_v2.txt`；补测：`refix.txt`、`attrib.txt`。
**25 绿 / 3 不绿**，三条不绿各有归属，没有一条判给这份 PR：

| 不绿项 | 读数 | 归属 |
|---|---|---|
| `--unknown-duration-progress-regression` | `Unable to cast Panel → LakeUI.ModernPanel`（站点 `Program.vb:1210` 反射找设置页根面板） | 测试宿主自己的 UI 断链，**不在改动清单内**，内核不在这条路径上 |
| `--kernel-timeline-regression` | `已呈现帧数 == 期望帧数` 随机短落 | 见上面那节 A/B/A：改动前的 API 15 内核 6/6 红且缺 27–53% ⇒ 本机 present 钩子下的判据失效 |
| `--hdr-pixel-regression` | `测试失败：未覆盖 FP16 后缓冲。` | 本机没有 Advanced Color 链（同机另一路读数 `位深=8`）⇒ scRGB/P3 类判据在本机**不可判**，非缺陷 |

三条曾被我误报成红的，实为**夹具选择错误**，换对素材即绿：

- `--decoder-switch-audio-regression` 断言 `打开快照.当前音频流 >= 0` ⇒ **本机 `testmedia/media/real/` 里 7 个文件全部 `audio=NONE`**（逐个 ffprobe），任何真素材都喂不动它；用"真实 4K 画面 + 440Hz 正弦音"合成 A/V 后 **exit=0**（两臂都绿）。
- `--unknown-duration-media-regression` 要"容器里没有总时长"的流 ⇒ 裸 Annex-B `.h264` 后 **exit=0**。
- `--video-scaling-regression` 在等"整秒实时音视频码率"⇒ 需要带音轨且近似 CBR 的流；我原先那份 CRF 可变码率无音轨的夹具导致 `视频帧/Present 601/602` 超时。换 `-b:v 6M -maxrate 6M` 带音轨夹具后 **当前内核 3/3 绿，改动前内核 3/3 绿** ⇒ 那条"恒定差 1"的担心不成立，撤回。
⇒ 顺带自证一处 harness 小错：`attrib.txt` 首行 `Unrecognized option 'select_streams'` 是我把 ffprobe 的参数发给了 ffmpeg；
   但"夹具真的有音轨"这件事是被套件自己的 `当前音频流 >= 0` 断言独立证明的，不需要那条打印。

现场还原自检：`vb_restored_equal=True`、测试 bin 的 `FFF.Native.dll = EB3B8A5B63C2`（= 当前源码构建产物），
3FCompare 的 Release bin 同步到同一哈希。


### 打包链 P0 已解：API 16 落进出货树 + Debug 配置补齐（同日第二批）

**做法不是放宽守卫，也不是改 `KernelDllPath`。** 先前量的两条捷径都被读数否掉：换内核树会丢
fork 独有的 487 行（`VideoRenderer.cpp`），硬套 PR 补丁 14 个文件里 8 个冲突。
走本仓自己的补丁队列机制：`git fetch <PR树> HEAD` 取来 preimage blob ⇒ `git apply --3way` 做真三方合并
⇒ 8 处冲突 + **5 处本树特有对账**逐条人工裁决（日志 sink 全局量重复声明、PR 的析构修复引用本树不存在的
`ReleasePrimariesCarrierFilter` ⇒ 还原并注明、本树无宽色域跟踪 ⇒ 随 PR 补 `sourceWideGamut_`/
`IsWideGamutSource()`、`SetPresentConfig` 声明重复、解引用 `AVCodecParameters` 需补
`#include <libavcodec/codec_par.h>`）。fork 分支 `3fcompare/api16-kernel` 两笔提交
`f344c3c` + `3940990`（15 文件 / +13,994 / −13,114）。

**账本与可复现性**（内核侧 `PATCHES.md` §十三 + D10 成员/探测/标识符入账）：

- 重放件换 `tools/patches/0014-baseline-vs-a87046e-api16.patch`，0013 退役进 `history/`。
  **不只测"能打上"**：把补丁正向应用到原始 `a87046e` 树后与 HEAD 的 15 个文件逐个 `diff`
  ⇒ `applied-vs-HEAD differing files = 0`。
- `$KernelBaselineSha/Tag` 同步到 `3940990…` / `3fcompare-kernel-2026.9.30.1`；
  `.3fc_kernel_baseline.bundle` 重建（45 个 head，含新 tag；`bundle verify` 说
  "records a complete history"），旧包挪 `.3fc_dumps/kernel-baseline-2026.9.27.1.bundle` 不删。
- 哨兵 `tools/check_kernel_ledger.py --upstream a87046e`：**7 项全 PASS、exit 0**
  （A1/A2 26 个成员双向、B 37 条探测双向复现、C 11 个补丁状态与目录位置互洽、D 未点名长标识符 0、E 覆盖、F 只读）。
  并跑 `--selftest` 证明它仍有牙：四类构造缺陷（少登记成员 / 表里多幽灵成员 / 探测期望被改错 / 整族探测被删）
  **全部按预期判红**。

**Release 侧验收**：`tools/构建全部.ps1 -Configuration Release` exit=0，识别出补丁"已在树中（反向 --check 通过）"
不重复应用，两处 bin 各部署一份，`bin` 与进包内核同哈希 `e9a18ddf492a`；
`.3fc_kernel_build.json` = `head=3940990 tag=3fcompare-kernel-2026.9.30.1 dirty=False`。
门禁 `-SkipSelfTest -SkipPackageSelfTest` 重跑：`[1/11] 内核基线 ✅`、`[2/11] 内核导出面 ✅`
（导出面 **87**、配对 `kernel=16 / managed=16`、bin 副本一致）——**P0 关闭**；
Release 编译 0 错误、单元 729 + Platform 475 全过、E3 冒烟 exit=0；
剩下 4 项 ❌ 全是我自己传的 skip（显式降级，按设计记红）。
出货内核（不是 PR 树那份）在应用侧真机再验一次：`真 1:1 封顶通过 zoom=1 ⇒ Dest 1280×720 比值 1.000`、
`缩放下限通过 z=0.6 ⇒ 1197×673`、`单路锚点 5 跳变 ≤0.5px`、`presented +50/秒`，整轮 **exit=0**，
`[内核]` 前缀行仍在写进 `logs/app-*.log`（19 行）⇒ 日志汇在出货树上同样通。

**Debug 侧**：`构建全部.ps1 -Configuration Debug` exit=0，Debug bin 的内核从"缺 4 个入口"变成
**API 16 / 87 导出 / 必需入口齐**（`check_kernel_exports.py` 结论 OK）⇒ 09-30 上午那条
"Debug 跑真机必 `EntryPointNotFoundException`"关闭。
⚠ 顺带记一条 harness 口径（**不是产物问题，也别去为此放宽守卫**）：`check_kernel_pairing.py`
从 DLL 路径上溯四层找 `PlayerSession.cpp` 读 v1/v2 门，对**部署副本**必然落空并判 FAIL；
要看真值就用 `-KernelConfiguration Debug` 走门禁（校验 fork 树里那份，源码可読）。

**导出面口径**：出货树 87 vs PR 工作树 89，差的是 `FFF3FP_ProbeSubtitleStreams` 与
`FFF3FP_GetImageInfo`——本树基线（`a87046e`）不含 MKS/图片模式那两笔上游合并，
**不是漏移植**。宿主侧同理：PR 里带来的 `FF3FP_GetImageInfo`/`原生图片信息`/MKS 探测绑定
在移植时**主动剔除**（本树没有这些成员，硬引进来编不过也不该编进来）。

**Debug 批（同日第二批）+ 顺手修掉门禁自己两处假信号**：

| 项 | 读数 |
|---|---|
| Debug 内核 | `构建全部.ps1 -Configuration Debug` exit=0；Debug bin 从"缺 4 个入口"变成 **API 16 / 87 导出 / 必需入口齐**（`check_kernel_exports.py` 结论 OK）⇒ 上午那条"Debug 跑真机必 `EntryPointNotFoundException`"关闭 |
| 两套哈希对得上 | Release `8c083265bb86`、Debug `50c86906b847`；**bin 副本与各自配置的构建产物逐一相同**（同一次实测里比过四个文件），所以 `[2/11]` 的一致性断言是真比过而不是碰巧 |
| 门禁缺陷 A（已修） | `[2/11]` 的 bin 比较固定取 `$Exe`（Release）所在目录 ⇒ `-KernelConfiguration Debug` 时拿 Debug 内核比 Release 副本，**恒红**。改为按 `$KernelConfiguration` 取对应 bin |
| 门禁缺陷 B（已修，且证明它有牙） | `[1/11]` 不校验清单里的 `configuration` 是否等于本次请求 ⇒ 实测出现过"Release 一轮里基线项打印 `cfg=Debug` 却判 ✅"。补一条断言后：用只有 Release 清单的状态跑 `-KernelConfiguration Debug` ⇒ **如预期判红** `✗ 清单配置 Release ≠ 本次 -KernelConfiguration Debug（先跑 …）`；补跑 `构建全部.ps1 -Configuration Debug` 后 `[1/11]`/`[2/11]` 双双 ✅ |
| 哨兵 | `check_kernel_ledger.py --upstream a87046e` 7 项全 PASS（exit 0）；`--selftest` 四类构造缺陷全部判红 ⇒ 新加的账不是"糊过去"的 |

⚠ 台账里这些 DLL 哈希只是**当轮实测**：PE 时间戳使每次重建哈希都变，所以真值由门禁在运行时比对
（`[1/11]` 比清单 vs 磁盘、`[2/11]` 比进包 vs bin），**不要拿本表当比对基线**；本表的作用是记下"那一刻确实比过"。



### 发布说明（用户可见）
`pack.ps1` 生成的包内 `使用说明.txt` 新增「🎨 画面呈现（内核 API 16）」段，双语写明：
SDR 10bit/广色域走 16bit scRGB（设置里那个开关）、P3 按 P3 换算、平移两个方向都跟手、
倍率可 <1.00× 真缩小、VRR 低延迟呈现（实验）。**并写明本版程序要求随包内核 API 16**：
配 API 15 内核不是"少几个功能"，而是所有会话创建失败（表现为"未就绪"）⇒ 内核与程序必须整包一起换。
`真 1:1 / SetFitLimitToNative` 目前只有内核入口与 `--selftest` 调用点，**界面开关未接**，故不写进用户说明。

### PR 就绪化的整理（非缺陷，接在 1)2)3) 之后）
- `VideoRenderer.cpp` 里那段 DISPLAYCONFIG 经验注释原文点名了另一个项目（含其仓内路径、许可证、
  一句与本工程无关的轶事）⇒ 重写为**只留可执行部分**：三条陷阱（`GET_TARGET_NAME` 条目步长须 72
  字节、Win11 只能用 `QDC_DATABASE_CURRENT`、`GET_SDR_WHITE_LEVEL` 的 `header.id` 须是 **target** id）
  与"200 尼特 vs 本核 80 尼特"的换算警示。顺手删掉了未经本仓实测的裸枚举值 `0x0b`（代码用的是枚举名）。
- `ShaderBytecode.h` 的头注释点名 `tools/generate_shader_bytecode.py` ⇒ 脚本与生成物**必须同批入库**，
  否则引用悬空；`tools/发布3FP.ps1` 是本仓发布脚本（含本机路径），**不入库**。

### 已上报未修（留给上游定夺或下一轮）
- `extensionAttempted_/extensionEligible_` 四个写点里只有 `Render()` 那一处（取锁之前）没在
  `deviceMutex_` 下；我只给 `ConfigureHdrStream` 加了锁 ⇒ 同步故事不完整。补齐要动 `Render()`
  的赋值位置，且 `ReleaseDeviceObjects()` 依 2026 行注释"调用方须已持锁"，重复加锁会自锁死。
- `kShaderGamutTristate` 现为恒 `true` 的编译期开关，它唯一的作用是"别在 DXBC 重生成前宣布 P3"，
  而本 PR 同时带了新 DXBC ⇒ 对上游是多余分支。
- `PlayerSession::Fail` 在函数体内 `extern` 声明 `FFF3FP_KernelLogImpl`，`VideoRenderer.cpp`
  在文件作用域声明同一符号；两种写法并存。
- `FFF3FPLogCallback` 类型带 `noexcept`：托管回调若让异常逃出即 `terminate`；头注释只警示了委托
  **生命周期**，没写"不得抛"。
- 内核日志行为了同时喂 sink 而去掉了 `\n` ⇒ `OutputDebugStringA` 侧连续两行会粘连。
- 3FR `VideoMuxer` 新增"`mixAudioSources` + 采样率 ≠ 48000 直接 `InvalidArgument`"：对既有主机是
  **行为变更**（原本接受并录出漂约 8.8% 的音轨），PR 正文必须单列一句。本仓不受影响——
  实测 `src/` 里没有任何混音录制调用点（只有媒体信息展示读 `AudioSampleRate`）。

### PR 形态
一树一 PR（不拆）：14 个改动文件 + 新增 `tools/generate_shader_bytecode.py`（`ShaderBytecode.h`
的头注释引用它，不同批入库就是悬空引用）。**`tools/发布3FP.ps1` 不入库**（本仓发布脚本，含本机路径）。
上游历史约定 D10「一个 PR 只做一个提交」⇒ 按主题分 9 段写在 PR 正文里，提交仍是一笔。



## [未发布] — 2026-09-29 · API 16 内核补齐到能出货：入口回补 + 有符号 dest + 真 1:1

### 审查（隔壁 agent 的三入口回补）
- `FFF3FP_Redraw / SetPresentConfig / SetLogCallback` 的移植**与出货中的 v15 逐行一致**——我原先怀疑的
  两处（函数内 `extern` 重复声明、`SetPresentConfig` 挤在 `RenderTargetInfo` 的文档块里）都是 v15 原有的
  形状，不是移植引入；照抄才是对的，改了就变成两棵树行为分叉而配对守卫查不出来。
- v15 另有两处 `FFF3FP_KernelLogImpl` 调用点（泵线程 slow Present / off-pump Present）**没跟着搬**：
  `3fp/kernel` 树里根本没有 `NotePumpOwnedPresent*` 这组函数 ⇒ 不是漏移植。代价：API 16 内核的
  `logs/app-*.log` 不会有那两行泵诊断。两棵树按行数的发散（同 `FFF.Native/3FP` 比）：
  VideoRenderer.cpp 916、PlayerSession.cpp 554、VideoRenderer.h 103、Api 头 68、其余 <70，
  `Subtitle/SubtitleProbe.*` 只在 `3fp/kernel`。

### P0 我自己查出来的第二条缺口：有符号 dest（RenderTargetInfo v2）
- 托管侧早已按 `Version==2 / int DestX` 读（`Fff3FpEngine.cs` 建结构时写死 2），而 `3fp/kernel` 仍是
  `version=1 / uint32_t destX` 配 `max(0,·)` 单边钳 ⇒ 负原点被折成巨大无符号，"往右下平移"那半段量程整段消失。
  **v1/v2 字节宽度相同 ⇒ 导出面与 API 版本两项都查不出来**，只有配对之外的源码级比对能查。
- 回补 6 处（`VideoDestination` 原点、`CalculateVideoDestination`/`CalculateLyricsCoverDestination` 三处
  返回、变换块改对称区间 + `lround`、`RenderTargetInfo.destX/Y`、`lastDestX_/Y_`、ABI 结构 + 会话版本门）。
  `tests/output/check_signed_dest_port.py` 逐区域与 v15 比对：8/8 全等；变异（退回一处 `uint32_t`）⇒ 该区域判红。

### 新增内核入口 `FFF3FP_SetFitLimitToNative`（#12 真 1:1，纯新增不动 zoom 语义）
- 打开后适配盒封顶到源原生尺寸（走 `CalculateVideoDestination` 已有的原生分支，不新写几何），于是
  **zoom 数字恒等于 屏幕px/视频px**：`zoom=1` 就是逐像素 1:1，窗口比源大时四周留黑边。
  窗口比源小时封顶物理上不生效（内核条件本就是 `sourceW ≤ outputW`）⇒ 那条判据 `RecordSelfTestSkip`，不记通过。
  不递增 API 版本（D9：纯新增 ABI 不破坏），靠配对守卫的必需清单第 26 项钉住存在性。
- 真机两侧（`tests/output/selftest_REVERT.log` / `selftest_MUT.log`，1280×720 自然内容夹具）：
  有实现 ⇒ `zoom=1 → Dest 1280×720`（比值 1.000）、`zoom=2 → 比值 2`；
  变异把 `|| fitLimitToNative_` 摘掉 ⇒ 判红并指名 `Dest 1995×1122，源 1280×720（宽差 715px，高差 402px）`。

### 主树第一次跑进真机（此前只能借 HEAD 工作树 + API 15 内核）
- `--selftest tests/output/selftest_720p_nat.mp4` 整轮 exit=0、0 SKIP。跟手四档 z=1.15/2/4/8 拖
  99.8×56.1 物理像素 ⇒ 内核原点 **−150/−998/−2993/−6983** 且画面走 100.0/56.0 px（负原点这一读数本身
  就是 v2 回补的现场证据）；锚点五跳变最大偏差 **0.5 px**；`check_kernel_pairing.py` 对新内核
  `exports 89 / kernel=16 managed=16 / OK`，对 third_party 那份则报
  `missing: FFF3FP_SetFitLimitToNative` + `API version 15 != 16` 双条 FAIL（对照臂有效）。
- 离线：Core 729 / Platform 454 全绿（接口加一项，三个测试替身同步补齐）。

### 诚实限制
- **内核重建不逐字节确定**：还原到同一份源码后重编，SHA 从 `fc97705a…` 变成 `2c0c4c74…`（大小同为 787968 B）。
  ⇒ "还原后指纹不变"不能当回退证据；本轮回退的证据是源码文本一致（`grep MUTATION` 命中 0）+ 行为复跑转绿。
- 出货链**仍然指向 `third_party/fff_project`**（`3FCompare.csproj:55` 的 `KernelDllPath`，`构建全部.ps1`
  的 `$ForkRoot`、`验证守卫.ps1` 的 `$NativeInputs` 同源），而 D6 明令内核改动不写进 third_party
  ⇒ "编出来的进包内核"与"托管要的那份"现在分属两棵树，门禁 [2/11] 的 bin-SHA 断言据此判红。
  翻转这条链会动发布基线（补丁流水线、FFmpeg/libass 落点、[1/11] 的 HEAD/脏树戳记），单独一批等点头。
- `zoom ≤ 1.0001` 仍是 no-op（本轮刻意不重新定义 zoom 语义，z<1 时钳制区间会反向 ⇒ `std::clamp` UB，
  要做得连量程符号一起改）。真 1:1 已由封顶覆盖，"比适配还小"仍未开。

### `zoom < 1` 现在真的缩小（倍率域两侧都通）
- 内核变换段的门槛从 `zoom > 1.0001f` 改成 `|z − 1| > 1e-4`，并配套改两处才成立：原点区间由
  `[x₀ − (盒宽 − 拟合), x₀]` 改成"两端取 min/max"（z<1 时旧写法区间**反向**，`std::clamp(lo > hi)`
  是未定义行为），平移量程从 `max(maxPan, 0)·fitW` 改成 `|盒宽 − 拟合|/2`（旧写法在 z<1 把它吞成 0 ⇒
  **平移死**）。盒宽另有 `max(1, ·)` 兜底，0.05× 也不会出 0×0。
- 托管侧三处同步（都是同一条内核式子的取逆）：`ViewPan.Accumulate` 不再拒绝 z≤1、量程取绝对值；
  `AnchorPan` 的闭式从 `[(z₀−1)(1+pan₀)+2u(z₁−z₀)]/(z₁−1) − 1` 换成两 regime 通用的
  `[(z₀−z₁)(1−2u) + pan₀|z₀−1|]/|z₁−1|`（在 z₀,z₁>1 内展开逐项相等 ⇒ 放大那一路没被碰过，
  真机读数逐位复现）；`KernelZoomEpsilon=1.0001` 换成 `KernelIdentityBand=1e-4` +
  `KernelAppliesTransform(z)`，`FitBoxFromDest` 与"按下左键能不能拖"的门槛都改读它
  （原来写死 `zoom > 1.001f`，放开后会变成"内核认 pan、app 不给拖"）。
- 缩略图小地图的 `SharedZoom > 1.001f` 门槛**没动**：它画的是"放大时我在源里的哪一块"，
  z<1 时整幅都在屏幕上，不是同一条语义。滚轮/下拉的倍率域仍是 [1,32]（那是 UI 口径，见下）。
- 判据（`--selftest` 里原「缩放下限观测」升成「缩放下限」，观测不再够用）：z=0.6 ⇒ `Dest 1197×673`
  （拟合盒 1995×1122 的 0.6 倍），黑边内可走全程 798px 且 pan=±1 各贴一端，z=0.05 ⇒ 99×56 仍有下限。
  变异（量程退回 `max(·,0)`）⇒ 判红并指名 `pan=+1 原点 399（期望贴左 0）、pan=−1 原点 399（期望贴右 798）`
  —— 两端都停在中心正是"平移死"的形状（`tests/output/selftest_MUT2_4k.log`）。
  回归护栏：同轮 z=1.15/2/4/8 的跟手读数（画面走 100.0/56.0px、内核原点 −150/−998/−2993/−6983）
  与锚点五跳变（≤0.5px）与改动前逐位相同。离线 Platform 测试集 454→462（+8 条全在 `ViewPanTests`），
  Core 仍 729；
  旧断言里"z=0.5 算退化"这条镜像跟着语义一起改了（改用带内 0.99995 顶位）。

### 两条收尾：失配自证 + 配对守卫补 dest 这一项
- 「放大镜-探针一致性」失配时现在**先取证再抛**（新增 `MagnifierOverlay.SampleWindowForSelfTest`，
  把最近一次成功回读的窗口左上角与当时光标物理像素交出来，再摊开中心 5×5 缓冲邻域找探针值落在哪一格）。
  合成硬边界夹具实测：放大镜读缓冲格 (997,280) 且其光标物理像素同为 (997,280)（自洽），探针值
  (0.000,0.059,1.000) 在邻域内相对格 **(+1,−2) 残差 0.0000 精确命中** ⇒ 定案为"两路读了不同的格"
  （源域整数截断 ↔ 缓冲格号的相位差），排除"读错面/拿过期格/不是同一代帧"两种真缺陷形状。
  自然内容下两者差不到阈值（4K `0.0000`、720p `0.0039`，两轮 exit=0）。**阈值没有放宽**：
  §11.16 记的那条"未解释"就此结案为 #13 的决策数据，而不是被调参抹平。
  顺带把 `ZoomGrid` 从 `private const` 升成 `internal`，自测里"与 MagnifierOverlay.ZoomGrid 同值"
  那句手抄的 `const int Grid = 12` 改读真常量。
- `check_kernel_pairing.py` 从两项变三项：API 版本 / 必需导出 26 / **`RenderTargetInfo` 版本门槛**
  （托管侧取 `new Fff3FpRenderTargetInfo` 初始化器里的申报值，内核侧从 DLL 所在树往上四层找
  `PlayerSession.cpp` 的 `info.version != N`）。**量不到就拦** —— 本轮撞的洞正是"v1/v2 字节宽度相同，
  导出表与 API 版本都看不出符号性"。双侧验过：临时把树里门槛改回 `!= 1`（源码级，不用重编）⇒
  `FAIL: RenderTargetInfo abi mismatch … every Dest-based readout goes silent`（exit 1），
  同一变异被 `check_signed_dest_port.py` 独立抓到（`session version gate` 区域缺指名的那行）；
  撤回后两路均 exit 0。

### #15 结案：探针哨兵分支从"判不了"变成"结构上跑不回来"
- 原来挂账的理由是要伪造 Avalonia `PointerEventArgs` 才能跑到无会话那条分支。这轮不去造事件，
  而是把"有会话映射成功 / 映射失败 / 无会话"三份决定合成**一个出口**：非哨兵值只能来自
  `MapPointerToVideoPixel`（它本身要求有会话）⇒ P1-5 那个"把表面 DIP 坐标当源像素下标显示"
  的形状在结构上没有回来的路径，比"加一条 UI 注入判据"更硬（不依赖测试是否跑到那条分支）。
  抽出的纯函数 `MainWindow.ProbePointOrSentinel` 配 4 条离线判据（Platform 471→475）。
- 两个变异各自只红自己该红的那档：哨兵改 `(0,0)` ⇒ 2 红；写成 `mapped is { } m && m.X > 0`
  （把 0 当没取到）⇒ 恰好 1 红，正是专为该形状准备的那条 `SourceOrigin_…`。
  后者第一版**根本没编译过**（三元把 `(int,int)?` 带进结果类型，CS0266）——
  "变异没红"要先分清是没生效还是没编译。
- 重构后真机复跑 `selftest_probe_refactor.log`：exit=0、0 失败（一致性 0.0039、缩放下限、真 1:1 封顶均 ✅）。

## [未发布] — 2026-09-29 · 单路放大：拖动跟手律、滚轮光标锚点（真机闭环）

### 行为
- **拖动增益随倍率归一化**：`ViewPan.Accumulate` 的分母由"窗口短边 DIP"改为**被拖那一路的适配盒**
  （物理像素，横纵各算），并补上内核量程里的 `(z−1)` 因子（`VideoRenderer.cpp:4817-4820` 把 1 个
  单位 pan 换算成 `fitW·(z−1)/2` 像素）。旧式合出来的结果是"画面位移 = 光标位移 ×(z−1)"：
  z=1.15 只有 0.15 倍、z=4 快 3 倍，**恰好在 z=2 侥幸 1:1** —— 单点复查发现不了，这是它长期存活的原因。
- **滚轮改倍率以光标为锚**：新增 `ViewPan.AnchorPan`（"同一源点缩放前后落在同一屏幕位置"的反解）。
  旧实现只动 zoom 不动 pan ⇒ 内核永远围绕拟合盒中心放缩。光标落在黑边时退化为"可见区中心那一点"，
  **不是**源中心 —— 已平移时用源中心会把画面猛地拽回去（黑边占大半画面是常态，不是意外）。
- **对比模式同一退化 + 拖动中实时提交**：相对对齐支的黑边退化改用同一条可见区中心算法
  （`VisibleCenterFractions`，两路共用一份）；拖动不再"松手才一次提交"，按 ~60Hz 合并提交 ——
  `--magnifybench` 新加的 `[平移提交]` 实测 z=2/2 路单次 p50=0.03ms、p95=0.27ms、max=0.78ms，
  34 次/秒连续提交期间 59.87 fps/路（同轮基准 58.92、放大态 59.14，零 hitch）
  ⇒ docs/26 当年反对逐帧 `SetWindowRgn` 是估算，数据支持跟手。
- **亚像素口径**：`VideoPixelMap` 新增 `MapToSourceFraction`/`MapFallbackFraction`，整数像素出口由它
  派生（同一口径），锚点不再被截成整像素 —— 截断误差每格最多偏一个显示后像素且同向累加。
- **锚点优先级收成一条纯函数**：`ViewPan.ResolveAnchorFraction(光标下那点, 可见区中心那点)` →
  光标 → 可见中心 → 源中心。单路与对比相对对齐支原来各写一份 `?? 0.5`，改一份漏一份就是
  "同一个手势两种手感"；现在 3 条离线用例分别钉住三级，两条变异（优先级颠倒 / 退回 `?? 0.5`）
  各自只被抓一条 ⇒ 判据分得清是哪一级退化丢了。
- **状态栏少一个假倍数**：并列的 `×N` 是"相对适配框"的倍率，长得像屏幕:视频比却不是那件事，已撤；
  真比值仍由像素可信度串给出（zoom/pan 之后的 Dest ÷ 源尺寸），滚轮改倍率时它跟着变，反馈没丢。
- **视图变换跨会话重放**：`SyncController` 现在留档最近一次广播的 `(zoom, panX, panY)`，
  `AddSlot` 时补发给新建的会话（锁外调用，避免原生回调回头抢控制器的锁）。此前"保证多路看到同一区域"
  只对**当时存在**的会话成立 —— 加路、把新文件开进空 lane、崩溃自愈重建出来的那一路会停在"铺满窗口"，
  与其余路看到不同区域且屏幕上毫无提示；用户随手一拖又把整套值广播过去，表现为"那一路突然跳一下放大"。
  先写失败用例复现（`AddSlot_AfterSetViewTransform_NewSessionReceivesSameTransform`），修完转绿；
  变异（把补发条件短路）⇒ 该用例回红。**真机两侧也拿到了**：`--selftest` 新增「视图变换跨会话重放」
  （度量是各路 `DestWidth/SwapWidth`，自带"z=1→2 必须翻倍"的尺子自检）—— 工装无修复臂
  `各路比值=(2.000, 1.000)` 判红，搬入修复后 `(2.000, 2.000)` 整轮 0 ❌ 0 SKIP。
  首版这条判据是**假绿**的：轮询新会话出帧的循环里又调了一次广播，正好把要测的缺陷盖掉；删掉才翻红。
- **探针兜底不再造假坐标**：无会话时报 `-1`（与"光标落在黑边"同一口径），此前显示的是表面 DIP，
  数字随窗口尺寸变而与片源无关。

### 验收（读数指到日志）
- 离线：Core 728 / Platform 451 通过。`ViewPanTests` 29 条的参照是**按内核 destination 公式另写的
  第二份实现**，不是被测式子自比。变异注入：去掉 `(z−1)` → 7 红；去掉锚点补偿 → 7 红
  （其中 2 条是被变异暴露出来的无牙夹具——"顶到边界就只断言在 [-1,1] 内"，已改成由参照判可达性）。
- 真机（HEAD 工作树 + 原版 API 15 内核；`tests/output/selftest-round2-20260929.log`、
  `magnifybench-pancommit-z2-20260929.log`）：拖 99.8×56.1 物理像素，z=1.15/2/4/8 下内核原点均移动
  **100.0/56.0 px**；5 组倍率跳变（1→2、2→4、4→8、8→4、4→2）光标下那点最大偏差 **0.5 px**；
  抽掉锚点补偿同点漂 **499 px**，抽掉 `(z−1)` 则 z=1.15 只走 15 px、z=8 走 699 px（症状原样复现）。
- **拖动实时提交有判据了**：`--comparemodetest` 新增「对比-拖动实时提交」——把 crop 播种到 (0.06,0.06)、
  喂一次 `OnSurfaceMove(dx=+24)`、**不调松手**，断言 crop 已变小且纵轴纹丝不动（真机实测
  `0.0600 → 0.0432`，`tests/output/comparemodetest-dragjudge2-r1.log`）。位移特意取 24px 而不是 140px：
  140 会把 crop 一口气推到 0 钳位，那时"变了"分不清是真提交还是顶到头。变异（把 16ms 合并窗口
  调到永不触发）⇒ 该步判红并打 `crop 仍是 (0.0600,0.0600)`。
- `--comparemodetest 5` 两臂各 3 轮：改动臂 1 次段错误、HEAD 臂 2 次 ⇒ 崩溃属既有的 5 路崩溃族
  （n=3 不足以下因果结论，且改动的那条拖动路径本模式根本不注入）；两臂"滚轮→无缝放大"验收点均通过。

### 记账（未做 / 待定）
- **`zoom ≤ 1.0001` 时内核整段跳过视口变换**：实机下发 0.6 与 0.05，Dest 恒 1995×1122 = no-op
  （自测"缩放下限观测"）。⇒ 要做出 ICAT 那种"屏幕像素:视频像素"的绝对比值与真 1:1，必须给内核加
  入口（`limitToNativeSize` 只服务封面/歌词，`FFF3FP_Set*` 全集里没有该项），托管侧改不出来。
  并行会话正把内核抬到 API 16（scRGB），撞车风险高，未动。
  **已做**（见上一块）：入口 `FFF3FP_SetFitLimitToNative` 落 API 16 树，绝对比值与真 1:1 由它覆盖；
  `zoom<1` 这条本身仍未开（要连钳制区间的方向一起改）。
- 放大镜按源像素取格**未做，但回读路径已量定**：内核契约写死了它取的是哪个面 —— `FFF3FP_ReadVideoPixelRegion`
  的声明注释是 "Samples a rectangular region of the **presented frame** in ONE staging copy"
  （`third_party/.../Api/FFF.Player.Api.h:516-521`）⇒ 拿到的永远是"呈现后"的那份，不是解码帧。
  实机测（4K 片源、Dest 1995×1122 的窗口）：适配态比值 **0.520**（12 个源像素只占 7 个屏幕像素 ⇒ 真值已被
  重采样混掉，任何取格方式都还原不了），z=2 起比值 ≥1 才可得；单次取格回读 1.03–4.86ms，
  **成本不是障碍**（`--selftest` 新增「放大镜取格预算」观测行，只记录不判红）。
  本轮只让"哪一格才是光标下那点"可见（`TryCursorCell` + 9 条用例；变异"硬标中心格"红 6 条）。
- 拖动中实时提交**没有自动化功能判据**：`--comparemodetest` 不注入鼠标拖动，本轮只有成本数据；
  行为改动靠复用同一条 `CommitComparePan` 保证数学不变。

## [未发布] — 2026-09-29 · 3FP 全量审查（5 路并行）：P0×1 + P1×9 + P2 修复

### P0（渲染线程 UAF）
- **`fallbackReason_` 无锁 std::string 跨线程读写**：呈现线程每帧写（`EnsureSwapChain` 门禁）、托管层经公有 `FallbackReason()` 随时拷贝，字符串扩容释放旧堆块时构成 use-after-free。修复：独立 `fallbackMutex_` 叶子锁（与 `errorMutex_` 同模式），7 个触点（5 赋值 + 1 clear + 1 读取）全部收口。

### P1（9 项，内核 5 / 录制 2 / 宿主 2）
- **extension 标志无锁清理**：`ConfigureHdrStream` 在 deviceMutex_ 外清 `extensionAttempted_/extensionEligible_`，与持锁的 `EnsurePipeline` 并发可重入扩展 shader 创建块 ⇒ 覆盖泄漏 `extensionShader_` + 杜比视界当帧错色。修复：清理收进 deviceMutex_。
- **`hdrSwapChainRejected_` 粘滞**：同一显示器上 `SetColorSpace1` 失败后无条件短路重探测 ⇒ 播放中打开 Windows HDR 无法自愈。修复：rejected 状态随 750ms 探测缓存过期重试。
- **析构顺序 UAF**：`PlayerSession` 析构先释放 `gamutGraph_` 滤镜图后 join worker ⇒ P3 静态图打开中销毁会话构成 UAF。修复：join 先行（worker 的 DoClose 已释放过，二次为兜底空操作）。
- **WASAPI `GetNextPacketSize` 失败静默挂起**：藏在循环条件里的失败让采集线程永久阻塞在事件等待，音轨整段静音且零诊断。修复：显式错误分支（SetError + SetEvent），failureCallback 恢复触发。
- **混音非 48k 尺度错配**：AudioMixer 时间线硬编码 48 kHz 而 Session 按 `audioSampleRate` 馈送绝对位置 ⇒ 音画不同步 ~8.8% + 缓冲无界膨胀。修复：`VideoMuxer::Initialize` 对 `mixAudioSources && rate != 48000` 显式拒绝（InvalidArgument）。
- **`SyncController.AddSlot` 异常逃逸**：锁外原生 `SetViewTransform` 补放无边界，并发 Dispose 时 `ObjectDisposedException` 直接从 AddSlot 逃逸，违反类自身线程安全契约。修复：补 ReportRuntimeError 边界。
- **`CopySettings` 漏拷 `CompareAlign`**：设置改对齐模式后已落盘但内存不同步 ⇒ 本次会话对比放大全部按旧模式换算（静默不生效）。修复：补拷贝。
- **崩溃重建回调 `SafeSeek+Play` 竞态**：与 `LoadSessionSnapshot` 已修缺陷同型（fire-and-forget Seek 无 happens-before），重建路径漏覆盖。修复：改同步 `SeekNow`。
- **（随修）rc 字符串 api 15→16 三处**：FileVersion/ProductVersion/ApiVersion 与 VER_API 对齐，部署现场核对不再误判。

### P2 修复（随 PR 一并）
- 探针负分量钳位对齐生产 shader（P3 负分量不再被 CPU 参考实现吞掉）；SDR 白"部分成功"沿用上次值（避免播放中亮度突跳 80 兜底）；`sdr_white_probe` verdict 按 `AdvancedColorKind==3` 门控（SDR/WCG 桌面不再误报 CORRECT——实测修正）；`static_assert(sizeof(ShaderSettings)==128)` 锚定 cbuffer；WASAPI 采集、ABI 冒烟、构建脚本的健壮性修复（rmtree 移出 probe 分支 / probe 目录创建 / 环境结果不误报 ABI 判负）；测试补 `SdrScRgbMode=-5→0` 下界断言。

### 已记录未修（P2，留待后续）
- VideoProcessor 直通路径为死代码（`actualVideoScalingMode_` 初值误导）；`ActualColorMode/SourcePeakNits` 无锁 getter；CopySdrFrame 仅 8bit（10bit SDR 封面抓帧受限）；命令队列无界；TimedText FP16 可见像素计数错位；Disc 菜单整图重拷贝；DISPLAYCONFIG 第二读数在本机读 0（WinRT 主路径正常，仅兜底失效，疑似 TrueToneCap 坑 1 变体）。

### 回归
内核 **785408 B**（0 失败）；宿主 0 错；单测 **729/0**；V1（10bit SDR → outBits=16）；V7 探针 exit=0（WCG 桌面正确输出 inconclusive）。

## [未发布] — 2026-09-29 · SDR 10bit / 广色域优先走 16bit scRGB 呈现（内核 API 15 → 16）

### 呈现链路
- **新增设置「SDR 广色域/10bit 呈现」**（默认"自动"）：显示器开启 Advanced Color 时，位深 > 8 或广色域 primaries 的 SDR 源改用 `R16G16B16A16_FLOAT` scRGB 链，避免在 gamma `R10G10B10A2` 链上被 DWM 二次处理、以及广色域被着色器折叠回 Rec.709；显示器未开启 Advanced Color 时回退原 SDR 链，与关闭时行为一致。10bit SDR 源的真实收益是**避免色域/精度重写与呈现契约统一**（字幕改为线性空间混合），不是"量化精度跃升"——FP16 线性与 10bit gamma 域各有区间优劣，不做过度承诺。
- **内核门槛收敛为一处**：新增 `WantsScRgbPresentationPath()`，SetWindow / SetColorMode / EnsureSwapChain 三处判定改为调用它，在既有"HDR 源或广色域源"之外追加"SDR 且位深 > 8"。此前三处各写一遍 `sourceHdr || wideGamut`，是"UI 显示与实际呈现漂移"的温床。
- **元数据诚实性**：SDR 源走 scRGB 时改发 `DXGI_HDR_METADATA_TYPE_NONE`。此前 `SetHdrMetadata()` 对任何 scRGB 链都发 HDR10 块，而 `BuildDxgiHdr10Metadata()` 对 SDR 源写的是默认值（2020 primaries + 100nits 峰值），等于声明了源从未声称的峰值。

### 契约与兼容性
- **不新增 `FFF3FPColorMode` 枚举值**：SDR 源复用 `actualMode_ = MapToHdr` 承载 scRGB 链——shader 数学（`ToLinear709 × PaperWhite / 80`）、字幕 FP16 自适应、位深决策（`PreferredOutputBitDepth` 的 hdr 参数语义本就是"呈现链"）全部现成，ABI 与守卫零改动。
- **内核 API 15 → 16**：`FFF3FPConfiguration` 尾部追加 `sdrScRgbMode`（内核对 > 1 返回 `InvalidArgument`）；托管 `Fff3FpEngine.ConfigVersion`、`FFF.Native.rc` 的 `VER_API` 同步递增。版本是**严格相等**校验，三者必须同发。
- **会话级**：设置改动对新开会话生效，不对已开会话热切换（与 HardwareDecode 同一口径）。

### 待办（本次未落地，非遗漏）
- **P3 三态已接线**：`ResolveSourceGamut()` 三态（0=709 / 1=2020 / 2=P3）+ HLSL `P3To709` 矩阵 + `ShaderBytecode.h` 由 fxc 重新生成 + `kShaderGamutTristate = true`。顺序纪律：翻转开关必须在字节码重生成**之后**（旧 DXBC 会把 gamut=2 当 Rec.2020 处理，比不接线更差）；脚本 `tools/generate_shader_bytecode.py`（初版规划曾标注"未接线"，随后已由 `build_sdr_scrgb.py` 闭环）。
- **已编译验证**：内核 23 编译单元 0 错（产物 780800 B，`3fp/kernel` 树）、宿主 `dotnet build` 0 错、单测 **728 通过 / 0 失败**、`FFF3FP_GetApiVersion() = 16` 与托管 `ConfigVersion = 16` 一致。P3 三态随 `ShaderBytecode.h` 重新生成一并接线（`kShaderGamutTristate = true`）。
- **待实机验证**：V1–V11 矩阵与白电平 A/B（PaperWhite 80 vs 200）需在 Advanced Color（HDR）显示器上跑，判据见 `3fp/docs/27` §7.3。

### 亮度对齐（SDR 内容在 scRGB 链上不再凭空指定白点）
- **问题**：scRGB 是绝对亮度契约（1.0 = 80 nits），SDR 源不声明白点；以前走 SDR 链时该锚点由 DWM 按 Windows「SDR 内容亮度」决定，播放器里根本没有这个数。
- **解法**：读 `AdvancedColorInfo::SdrWhiteLevelInNits`（内核已有同对象的 WinRT 读取代码）当纸白 ⇒ 亮度与以前的 SDR 链一致，且**自动跟随用户的 HDR 校准滑杆**（播放器别无途径读到滑杆）。
- **兜底**：系统未上报时用 **80**（scRGB 契约参考白），而不是 HDR 链的 200 图形白；HDR 源仍沿用配置的 `paperWhiteNits_`。
- **范围**：画面、字幕叠加白、封面 tint 共用同一刻度（`EffectivePaperWhiteNits()`）；**不改 ABI**（值在内核内部读取使用，API 仍为 16）。
- **验证**：拖「SDR 内容亮度」滑杆，画面亮度应随之变化（不变则读到了 0 并回退 80）；另与设置「从不」对比应基本一致。

### 构建与工具（本次新增）
- **`tools/build_kernel_3fp.py`**：构建 `3fp/kernel/FFF.Native`（应用真正使用的内核树；`third_party/fff_project` 是提 PR 用的仓库，缺广色域门禁）。复放 vcxproj 的 Release|x64 配置，产物落 `x64/Release_sdr/`（不覆盖既有 Release）。
- **`tools/build_sdr_scrgb.py`**：一键且**顺序不可交换**——① fxc 重生成 `ShaderBytecode.h`；② 翻转 `kShaderGamutTristate`（P3 接线）；③ 编译内核；④ `--sync` 同步 DLL。反序会让旧 DXBC 把 P3 当 Rec.2020 处理（比现状更差）。
- **`tools/generate_shader_bytecode.py`**：从 `VideoRenderer.cpp` 的内嵌 HLSL 重新生成预编译 DXBC 头。生产 shader **不走运行时编译**，改 HLSL 后必须跑它才生效。

## [未发布] — 2026-09-28 · 门禁"没等到"被误报成"没起来" · 判据改确定性 · 恒真判据修好后打出一个真缺陷

### 验收判据去时序化（发布包第一次出绿）
- **悬浮传输栏几何**：原先"发一次 resize → 固定等一下 → 量"，量到的是**还没落地的尺寸**。
  现改为等 `GeometrySyncs` **代数前进**（产品确实重算过），再**重试判决本身**直到收敛；
  只等"重算发生"不够 —— 第一次重算读到的是尚未落地的主窗尺寸（实测要 2 次）。
  实测 3 轮收敛于 67/105/123/124 ms（均"重算 2 次"）。有牙验证：把 `Width/Height` 写入摘掉但保留
  `GeometrySyncs++` ⇒ 该步立刻判红（"浮条宽度不符 实际=640 期望=1312.7 DIP"），不是被等成绿。
- **滚轮缩放-节流补发**：前置"至少丢一次"原本只靠一次同步连发，UI 线程被抢走就整串跨过 16ms 窗口
  （旧读数 90.30ms / 丢弃 0 ⇒ 假红）。现改为**先按真实节奏试 3 次，再走确定性兜底**：
  把节流时钟 `_lastPanApplyTicks` 伪造成"刚刚下发过一次"，随后那一次调用**必然**落在窗口内
  —— 留给单次同步调用的余量是整个 16ms，而不是要求整串 4 次都挤进 16ms。
  于是**没有"跳过本步"这第三种结局**：伪装窗口内仍零丢弃 ⇒ 只能是节流失效 ⇒ 判红。
  ⚠ 这条兜底是被变异逼出来的：只重试不兜底时，"摘掉节流"会退化成 SKIP 而非红
  （节流失效后每次调用都做完整下发，连发自己被拖慢到 19~48ms，永远"跨过窗口"）。
  两次变异都实测过：摘节流 ⇒ 红（exit=1）；让自然连发每次都跨过窗口（61.91ms）⇒ 兜底一次命中
  （伪装轮 0.10ms / 丢弃 4）⇒ 该步照常 PASS。计时改用 Stopwatch（`DateTime.UtcNow` 粒度约 15.6ms，量 16ms 不可信）。
- 稳定读数：Debug 连跑 5 轮 ⇒ 该步 5/5 首轮命中（0.25~5.74ms），4 轮 exit=0，**1 轮 0xC0000005 崩溃**（见下）。
  full 发布包重建后包内 `--selftest` **exit=0 通过**（本版本第一次），连跑读数见「已知未决」。

### 门禁自身的两个错
- **`Invoke-Cmd` 不等 GUI 子进程**：`$raw = & $exe … 2>&1`（赋值给变量）对 GUI 子系统程序**不等待** ⇒
  `$LASTEXITCODE` 停在 null ⇒ 被归一成 `-1` ⇒ 所有 `*-test` 行显示"命令未能启动，请在真实终端运行"。
  同一份包内 exe 同一条命令实测：赋值式 433ms / 0 行 / exit 空，管道流式收 2538ms / 7 行 / exit=2。
  改成流式收集（`pack.ps1` 那份一直是对的，两版分叉就是复发温床）。
- **SKIP 不再被报成 PASS**：`--selftest` 的退出码不反映跳过项（有跳过照样 exit=0，只在末尾打一条横幅），
  `[7/11] 单路全量` 因此会把"没验"报成绿。现在把横幅声明的条数与实际数到的条数**互相核对**并纳入判决，
  不等即红（防"正则失配 ⇒ 数到 0 ⇒ 全绿"）。`tools/_probe_gate_skipcount.ps1` 用**真实自测日志**当夹具：
  真·全绿轮判绿、真·带 SKIP 轮判红且带出原因、exit=1 判红、"横幅说 3 条实到 0 条"判红。

### 把恒真的判据修好，它立刻打出一个真缺陷
- **`路启用态` 的三个判据缺陷**（上一节末尾我把它判成"负载敏感"，**那句是错的**）：
  取"全路最差"当判决（报红的从来是第 1/2/3 路，本单元停用/再启用的是第 4 路）、
  在自己的 helper 把整组 `Pause()` 之后立刻读一次（`TickDriftCore` 在非播放态整段早退 ⇒ "永不收敛"成为必然红，
  实测 40 次采样位置一毫秒不动）、只吃一次采样。调用栈钩子点名的就是本单元 helper 收尾那句暂停。
  现改为：只对本单元那几路判决、非播放态的采样作废并由判据自己重新起播、轮询到收敛或超时；
  一次有效采样都没有 ⇒ 登记 SKIP。**阈值 250ms 一字未改。**
- **相位③补上前置后当场判红 13/20 轮**——我先判成"再启用漏补一次呈现"并加了 `RedrawAll()`，
  **这个判读被实测否掉、改动已撤回**：把判据从"只等 400ms 读一次"改成**有界轮询**（上限 2s）之后，
  在**没有** `RedrawAll` 的构建上 4/4 通过（`norwd_r1..r4`），而那一帧本来就在 **600~900ms** 落地
  （`poll_r2/r5/r7`：第 6/6/9 次采样才看到 presented 涨）⇒ 那 13 轮红是**判据在跟解码抢跑**。
  同一份代码两批分别 0/10 与 5/8，也只是负载/时序，不是产品变了。
  ⚠ 记两条规矩：①"我加了东西它就绿了"不构成因果，要把那处改动单独关掉再跑；
  ②读 presented/位置这类"会晚到的量"必须配"等到它到达"，否则判的是竞速。
- **换来的收获**：这一枪原本恒真（组在播时必然通过），现在自己立前置（`Pause()` + 验"确实停了"，
  停不成直接判红）、改成有界轮询，并新增**相位④**：在暂停态下量"新占格那几路 vs 规范时间"——
  组停着 ⇒ `TickDriftCore` 整段早退、没有校正会来补 ⇒ 这是唯一能干净盯住"再启用有没有 Seek"的时刻。
  正常读数 6ms / 9ms（阈值 250ms）；把启用分支的 `Seek` 摘掉 ⇒ 2/2 判红（`mutD_r1/r3`）⇒ 有牙。
- **WM_SIZE→Redraw 的"debounce"此前只写在注释里**：`RedrawDebounceMs` 从未被引用，每次尺寸变化各发一次内核调用。
  现按声明实现"同一表面在途只留一次"（已回内核确认 `EnsureSwapChain` 在调用那一刻才取客户区尺寸 ⇒ 合并不会丢最终尺寸）。
  ⚠ **它不是崩溃带的修复**：带着合并的那一臂仍崩 9/16（见下一条）。

### 崩溃带定到一处被第三方改写的 DXGI 入口；#42 有了受控探针，并修掉一处真时序缺陷
- **抓栈不再靠猜**：四次 first-chance 现场（`tests/output/crashcap/`，6 次尝试崩 3 次）里
  **4/4 的 RSP+0 都是 `dxgi!+0x33AE4`** —— 即 `+0x33ADF` 对 `dxgi!+0x33AF0` 的那次调用；
  两次 AV 的 RIP 还是同一个地址（`0x7FFF8BFB9700`，不在任何模块里）⇒ 不是随机踩内存。
- **`dxgi!+0x33AF0` 在本机被改写**：新工具 `tools/_probe_code_patch_diff.py` 把被测进程**内存里的代码页**
  与 Authenticode 有效的磁盘镜像逐页比 ⇒ dxgi 有 4 处自有代码节入口（`+0x19530/+0x199D0/+0x33AF0/+0x37180`）
  被换成 5 字节 `E9`，跳板洞里的绝对值指向 **`RTSSHooks64.dll`+0x72620/0x72F20/0x73540/0x73B60**
  （RivaTuner Statistics Server）；该 DLL 的字符串表自报 hook `IDXGISwapChain::Present/Present1/ResizeBuffers/
  ResizeBuffers1/CreateSwapChainForHwnd`。对照臂 `d3d11/DWrite/ucrtbase` 的 `.text` 逐页 **0 处差异**。
  ⚠ 口径：`fothk` 节每个模块都变（微软自己的转发表，装载时必然改写）⇒ **只有自有代码节才算证据**。
  ⚠ 边界：n=4 不够写成因果句；且跳过那次 `Redraw` 的臂仍 3/16 ⇒ 产品侧只能降低进入频率，不能清零。
  `tools/dump_capture.py` 现在同时记录 `rip_bytes`（RIP-8 起 32 字节），用来判"执行的是不是改写中的字节"。
- **修掉一处确凿的时序缺陷**（呈现停滞的轻量恢复）：原写法是 `_sync.Pause(); await Task.Delay(120); _sync.Play();`。
  内核两个入口的守卫都**在调用那一刻读状态、动作 Enqueue 后才执行**（`Play()` 只接受 Ready/Paused/Ended），
  所以工作线程被 4K 解码/被上面那个 hook 卡住超过 120 ms 时，`Play` 看到的是仍 `Playing` ⇒ 空转返回，
  随后排队中的 `Pause` 才落地 ⇒ **该路被永久停在 Paused**，UI 以为在播，而 `TickDriftCore` 刻意不校正 Paused 路
  ⇒ 无人恢复 = **#42 的形状**。改成"读回来再发下一步"：新增 `WaitRoutesSettledAsync`，
  等到启用路都读不到 `Playing` 才发 `Play`，发完再等到读到 `Playing`，不齐则补发一次并留 `LightRecoveryReplay` 日志。
  **这条改动端到端跑过**：新增单元 `sync.stall-recovery`（"只 `_sync.Pause()`、不动 `_isPlaying`"造停滞），
  实测 `PASS (2258 ms)`、presented 12→19、1754ms 内整组回到 Playing（`tests/output/stallrecovery-3.log`）。
  ⚠ 第一版把"前提被轮询 545 行抢走、看门狗没触发"写成了"恢复序列本身的缺陷"并判红 —— 那是**我的夹具**没立起前提，
  现已改成 `RecordSelfTestSkip`（不判红也不判绿），断言消息里不再写未证的因果。
- **接上一直被我丢掉的那份证据**：内核 `SetState` 本来就 Emit `{"state":N,"operation":"pause|seek|step-frame|…"}`，
  而 `PlaybackCoordinator.HandleEngineEvent` 的 switch 没有 `StateChanged` 分支。现在记
  `[路3] … 状态→Paused（内核操作=pause）`（带路编号，因为同一素材开 4 路时分不出哪一格）；**只记录、不判决**。
- **新增自带夹具的验收单元** `sync.seek-parks`（别名「单路受控Seek」）：不直接调 `Session.Seek`，
  而是人为挪 offset 让 `TickDriftCore` 自己发那句 Seek（同一条路径、同一套前置）。
  判据先跑**体内阳性对照**（故意 `Pause()` 一次，本判据必须报 stuck，报不出来就自判失败）。
  读数：相位 A（从 Playing 出发的一次校正 Seek）5/5、相位 B（停用→再启用那一路）5/5 未永久冻结，
  但 **3/5 轮再启用后先停 ~0.85–1.2s 才恢复** ⇒ "再启用后停一段"是常态，#42 是这段没恢复的版本，
  问题因此缩成"那一次恢复在什么条件下会丢"。静态一侧也否掉了原假设：`DoSeek` 只在 `Ended` 时置 Paused。
  ⚠ 口径订正：最初写成"整个窗口每拍都是 Paused"，而 `Pause()` 本身也是异步落地 ⇒ 第一拍还读到 `Playing`，
  阳性对照连红两次；旧口径的错法是**把中途开始的冻结读成绿灯**（往安全侧偏），已改成"后半段全 Paused 且位置不动"。
- **#42 根因确认并修掉**：再启用某一路时那次 `Play()` 会被内核**状态守卫**拒掉，而这次拒绝被
  `SetActiveRoutes` 的 catch 咽掉 ⇒ "要播"永久丢失；随后队列里那条 `Pause` 才落地 ⇒ 该路
  `Active=true`、位置正确、停在 `Paused`，而 `TickDriftCore` 刻意不校正 Paused ⇒ 永久冻住（=用户看到的"一格不动"）。
  复现与定案靠三个独立读数：① 探针新相位 C（两次 `SetActiveRoutes` 之间不 await）
  **C 3/5、B 1/5** 判红；② 日志顺序 —— `启用第N路开始` 与 `停用…` 同毫秒、`状态→Paused（内核操作=pause）`
  排在其后、且**没有**配对的"启用完成"；③ 修法自身留痕 ——
  `第 2 路起播被状态守卫拒掉（Play 失败: InvalidState）⇒ 记 PlayPending` 紧跟 `第 2 路补发起播成功`。
  **修法**：`SyncSlot.PlayPending` + `SyncController.RetryPendingPlays()`（由 UI 轮询每拍补发，
  补发前重新核对"仍启用/未失败/整组仍要播"，**用户主动暂停的路不会被叫醒**，multitest 依赖的语义一字未动；
  连续 20 次仍被拒才报运行错误）。牙的证明：同一判据改前 FAIL、改后 PASS（A/B/C 各 5/5）。
  判据同时加了**再确认**（窗口内像停住时再等 4s 才算 stuck）—— 因为实测**补发成功后排空队列还要 1.6s**（16:50:58.617 → 16:51:00.211），2s 窗口会把这种"慢"读成"永久"；
  4 路重跑 `PASS sync.seek-parks (46333 ms)` + `PASS sync.stall-recovery`（2 项/失败 0/跳过 0）。
  ⚠ 5 路那一档换上新判据后连跑三轮全崩（`0xC00000FD`、`0xC0000005`×2）拿不到判决 ⇒
  "5 路下 B 那 2/5 是慢、还是第二条停住路径"**未定论**（账本 §11.11 末）。
  ⚠ §11.8 那一次的现场没有这组配对留痕 ⇒ "那一次也是这条"是高置信推断，不是读数。
- **#17 现场涨到 7 次，都是同一处**：整轮 `--comparemodetest 5` 连跑三轮全崩并各抓到 first-chance
  （RIP 三条都在 `0x7FFF8BFB9000–0x7FFF8BFC0000` 这条未映射带里）⇒ **7/7 的 `RSP+0` 都是
  `dxgi!+0x33AE4`**（对被改写入口 `+0x33AF0` 那次调用的返回地址），栈上还有 `dxgi!+0x33AF5`
  （5 字节补丁后的回跳点）与 `RTSSHooks64!+0x240600`。n=7 + 机理自洽 ⇒ "崩溃发生在经第三方改写入口
  进入 DXGI 的那次调用之内"这句现在写得出口；RTSS 为何交出无效地址仍**未证**（不在我们这一侧）。
- **#16（覆盖站点 Z 序率）有数了**：从 `[ZOrder] 汇总` 留痕聚合 ——
  `LayoutOverlayWindow/HideOverlay` **4525 次采样 / 101 个显示周期：below=0、notInChain=0、flips=0**；
  `FloatingTransportWindow/OnClosed` 仅 **12 次采样 / 2 周期** ⇒ 样本不足以定率。
  诚实结论：这条工作负载下没再出现 Below，但**确诊 Below 才插 `HWND_TOP` 的补救分支 flips=0
  ⇒ 一次都没被走到，该路径目前无覆盖**（要覆盖得造注入，属另一批）。
- 回归：`728/728`（Core）+ `419/419`（Platform）exit=0（`tests/output/coretests-3.log` / `plattests-3.log`）；
  两个新单元组合跑 exit=0（2 项、失败 0、跳过 0）；新增单元 `sync.seek-parks`（三相位受控探针，体内带阳性对照）
  与 `sync.stall-recovery`（停滞唤醒恢复）。

### 已知未决（本轮新增的读数）
- 崩溃带的**暴露点被一次同二进制 A/B 收窄**：跳过 WM_SIZE 那次 `FFF3FP_Redraw` 后
  崩溃从 **9/16 降到 3/16**（两臂交替跑）⇒ 这条调用**放大**崩溃带，但**不是唯一源头**。
  机制候选指向内核 `PlayerVideoRenderer::Redraw()` 在**调用线程上**同步做 `EnsureSwapChain`
  （交换链的呈现属于专职 owner 线程）。真正的修法在内核侧，属另一批（要重编内核 + 重钉基线）。
  复现旋钮 `FC_SKIP_RESIZE_REDRAW=1` 留在代码里。
  ⚠ "子 HWND 已拆而 Redraw 还打进去"这条假设被读数**否掉**了：Debug 整轮 1032 次回调，陈旧 0。
- **上面那条"机制候选指向内核 Redraw"已被四次 first-chance 现场取代**（详见账本 §11.9）：
  现场 4/4 的 RSP+0 都是 `dxgi!+0x33AE4`，即 `+0x33ADF` 对 `dxgi!+0x33AF0` 的那次调用；而本机跑起来的
  进程里 `dxgi.dll` 有 4 处自有代码节入口（`+0x19530/+0x199D0/+0x33AF0/+0x37180`）**被改写成 5 字节 `E9`**，
  跳板洞里的绝对值指向 `RTSSHooks64.dll`（RivaTuner Statistics Server）`+0x726xx..+0x73Bxx`，
  该 DLL 的字符串表自报 hook 的正是 `IDXGISwapChain::Present/Present1/ResizeBuffers/ResizeBuffers1/CreateSwapChainForHwnd`。
  两条 AV 的 RIP 是同一个地址 ⇒ 不是随机踩内存。对照臂 `d3d11/DWrite/ucrtbase` 的 `.text` 逐页 0 处差异。
  ⇒ 崩溃带的**形状**定在"进入被第三方改写的 DXGI 入口后，控制流被交给无效地址/改写中的中间态"；
  ⚠ n=4 不够写成因果句，且跳过那次 Redraw 的一臂仍 3/16 ⇒ 我们这一侧只能降低进入频率（WM_SIZE 合并正是它），
  **没有任何改动可以把它清零**，发布包的稳定性因此在装了 RTSS 的机器上仍受影响。
  取证工具 `tools/_probe_code_patch_diff.py`（新增）与 `dump_capture.py` 的 `rip_bytes` 留痕（本次扩展）。
- **新登记的 #42**：偶尔有一路在**已 Active 的情况下**被按停（`State=Paused`、位置一毫秒都不动）而 master 照常播，
  于是它与规范时间的差随时间线性长大（实测 6718ms / 6442ms，约 1/8~1/7 轮）；
  `TickDriftCore` 刻意不校正 Paused 路（那是 multitest 判别实验依赖的语义）⇒ 没有任何机制会恢复它。
  用户可见后果：某一格冻在旧帧，其余格照常播。
  **第一个候选修法已被实测否掉**（把再启用的判据改成"意图旗 || master 在播"，冻结照旧）⇒ 那句
  `启用第 4 路（playing=False）` 是症状不是原因。下一步先给会话层 `Session.Pause()` 挂调用栈留痕。
  ▶ **现值演进（2026-09-28，§11.10–§11.11）**：留痕已接上，但根因**不是**托管 `Pause()` 被调用 ——
  是再启用那次 `Play()` 被内核状态守卫拒掉且异常被咽掉。已修（`PlayPending` + 轮询补发），
  上面那句"下一步"不要再照着做。
- **门禁第一次跑满 11 项：10 PASS / 1 FAIL**（此前 6 PASS / 5 FAIL，那 5 项全被误记成"环境起不了进程"）。

  唯一红是 `--comparemodetest 5` 崩在 0xC0000005（Debug 同样复现 ⇒ 与 AOT 无关）。

- 本节第一条草稿里我把 `路启用态` 的整轮红判成"**负载/前置残留敏感**"、并写"本轮只登记不修"——
  **那句判读是错的**，加了状态/位置增量读数后被推翻，真因与修法见上面一节。留这条是为了记住：
  "红只在整轮出现"不等于"负载造成"，也可能只是**别的单元的残留状态被这笔账算到了它头上**。

## [v0.3.1-beta] — 2026-09-27 · 视图模式立成一列 · 未占格的路改为不启用 · 发布链修断链

内核基线：`3fcompare-kernel-2026.9.27.1` / `b661365`（含方案A：`RenderTargetInfo` v2，
`destX/destY` 改有符号 —— v1 的 uint32 把放大平移量程的一半静默吃掉）。

### 界面与交互
- **视图菜单顶部新增「视图模式」一列**（三项互斥单选 + 标快捷键）：**标准模式 `G`**（均匀网格）/
  **A/B 可拖动对比 `V`**（分格 + 可拖分割线，按路数收敛，`C` 细调变体）/
  **左右拉动对比 `S`**（两路铺满、`SetWindowRgn` 裁成互补两半，左右拖动揭示；对标 ICAT Single Screen）。
  三项本就存在，只是埋在「对比模式」子菜单里叫"叠加/分屏/网格"，看不出是一组可切换的模式。
- **整条分割线都能抓**：原先只有线正中间半径 14 DIP 的圆点能拖，窗口越高越明显地"瞄不准"。
  单轴手柄沿轴放开到整条线；ABC / ABCD 的交叉点型**刻意不放开**（那种手柄一次改两个分量，
  沿整条线抓会把另一轴一起拽走，表现为"没抓的那条线跳了"）。DIP 与物理两条命中路径收敛成一份实现。
- **撤掉两个重复概念入口**：「A-B 滑块视图」（含裸 `B` 键与 `AbSliderView` 控件）只画渐变占位、
  真机模式下会整块遮住真实画面；「显示 对比网格」的实际动作是折叠侧栏，与「视图 → 侧栏」三态重复。
  裸 `B` 随之从固定键位表里释放（镜像断言同批改）。
- 传输栏 tooltip 与状态栏文案改为**跟着设置走**（步进量、绑定键位、实测启用集合），不再写死。

### 性能
- **没占格的路真正停用而不是只隐藏**：`SyncSlot.Active` + `SetActiveRoutes`，未启用的路被 Pause 且
  从播放 / 步进 / Seek / 漂移校正里整体移出；重新占格时先 Seek 回规范时间再按播放意图恢复。
  实测 5 路进分屏：未占格的第 5 路 1.2 秒 presented 由 **+41 → +0**（可见路 55~66 同速），
  回网格后又满速恢复且与 master 相差 ≤250ms。9 路用 ABCD 时相当于省下 5 路的解码与呈现线程。

### 发布链与验收
- **修好门禁 [1/11] 的断链**：`构建全部.ps1` 一次重写把内核基线常量与 `.3fc_kernel_sha` 的写入者一起删了，
  门禁仍去那里提常量 ⇒ 恒红。现在构建侧只**记录**（成功路径写 `.3fc_kernel_build.json`：
  head / dirty / dirtyFiles / kernelTag / dllSha256），门禁侧拦三件事：不是基线提交、工作树脏、
  **构建之后磁盘上那份 DLL 又被换了**。
- **修好补丁重放的假失败**：`git apply` 失败即中止整个构建，而"补丁描述的分歧已在树中"恰恰是正常状态；
  注释写着"视为成功"代码里却没有这条判断。改成三态判据（正向可应用 / 反向可应用=已在树中 / 真冲突才停）。
- **门禁扩面**：新增 `对比模式回归`（`--comparemodetest` 5 路，且要求"跳过 0"）——此前 16 个隔离单元整批在门禁之外。
- **SKIP 不再算 PASS**：判据体自己登记跳过时汇总表记 SKIP。实测 2 路 `25 项 / 跳过 3`、5 路 `31 项 / 跳过 0`。
- 出货 `使用说明.txt` 的键位段此前在说反话（`←→ 帧步进`，实际默认 `←/→`=按秒、`A/D`=按帧），已按实况重写，
  并加 `ShippedShortcutsNoteTests` 把文案的键位列与 `TransportKeys` 默认值钉住。
- 诊断：`SessionAutosave` 两处静默吞异常改为留痕；`AppLog` 停止后丢弃日志改为一次性可见告警；
  `SetActiveRoutes` 逐路留痕（本机 4K 多路的偶发崩溃带就落在这个窗口，靠它把崩溃定位到了无缝放大那一步）。

### 已知未决
- 4K 多路（4~5 路）偶发 `BEX64`/`c0000005` 崩溃：本轮实测 Release 新内核下 3 次里 1 次；
  WER 归档共 22 份、最早 09-21，属既有崩溃带，**未归因**（抓栈待办 #17）。
- 覆盖层画线与揭示边界实测差 **1.00 DIP**（= 格子的 `+1` 内缩，系统性），已用 ≤1.5 DIP 判据钉住并打印读数，
  刻意不把内缩规则抄进渲染器。
- `v0.3.0-beta` 从未打过 tag（当时的计划是"发布时补"，没补）。本次只标 0.3.1，不给旧提交追溯打标签。

### 上一轮（原「未发布」段，一并随本版发布）

### 验证流程拆分（切片 0 已落地，方案见 `docs/49`）
`--comparemodetest` 原本是**单一 try 的线性剧本**：第一条判据抛异常就 `ExitSelfTest(1)`，
后面所有验收点整批不执行 —— 今天的三条历史判据错误全部是"第一次被执行到的那一刻"才暴露的。
现改为 **per-step 隔离 + 末尾 `PASS/FAIL` 汇总表**（同一进程、同一次起盘，判据内容一字未改）：
- 实测：未注入时 2 / 3 / 4 路分别隔离出 23 / 27 / 29 个验收点，全绿 exit=0；
- **变异验牙**：在「左右互换」强制抛异常 ⇒ 其后的 轮换 / 对齐 / 变体×2 / 分屏 / 网格 / 退出
  全部照常执行并逐条上报，整轮 exit=1（"汇总"不等于"通过"）；
- 边界（未纳入本片）：起盘与进入模式这类**前置**仍走 fail-fast（它们失败时后续判据无意义，
  降级成记录会造绿）；流程里的**内联**代码块与 `--selftest` 尚未隔离。

### 验证流程拆分（方案与明细账见 `docs/49`）
- **切片 0（已落地）**：`--comparemodetest` 的验收点由"单 try 线性剧本"改为 per-step 隔离 +
  末尾 `PASS/FAIL` 汇总（判据内容一字未改）。变异验牙：强制「左右互换」抛异常后，其后
  轮换 / 对齐 / 变体×2 / 分屏 / 网格 / 退出 全部照常执行并逐条上报，整轮 exit=1。
- **切片 1（已落地）**：① 每条自动化日志开头自证产物身份（`[产物] process=… InformationalVersion=…
  mtime=…`，挂在所有模式共用的看门狗入口）；② 新增 `tools/验证守卫.ps1`，比较"最新构建输入 vs 产物"，
  退出码 3=产物陈旧 / 4=构建失败即终止（**不回退旧产物**）/ 6=输入枚举不足 fail-closed。
  三条各在本机真实触发过一次（陈旧 763 s、`MSB3027` 被两个 GUI 实例锁定、枚举打空）。
  第一版用 `Get-ChildItem -Path 'src\**\*.cs' -Recurse` 在 PS 5.1 下静默枚不到 .cs，
  拿一个 09-22 的 `.slnx` 就把守卫判成"新鲜"——**守卫自己差点变成假绿源头**，故改为 .NET 枚举 +
  逐类别最低数量 + 两侧文件名全部打印。
- 边界：守卫目前基于 mtime（跨机器拿来的产物需显式 `-SkipSourceCheck` 并在结论里注明未经校验）；
  源码内容指纹内嵌（`--build-id`）留给切片 2；门禁/`pack.ps1` 尚未接选择器。

- **切片 2（部分落地）**：`FC_STEPS` 选择器（逗号分隔 / `*` 通配 / `part:P6`），未匹配的单元记 **SKIP 且计入分母**，
  汇总表按 docs/49 的目标部分分组；实测 4 路全跑 29 项 0 跳过、单跑 `对比-对齐模式` = 1 PASS + 26 SKIP。
  选择器顺带暴露并修掉一处耦合：末尾"整轮必须真的下发过一次区域"的兜底断言依赖被跳过单元的副作用，
  现在选择器生效时显式声明"本次不适用"（全跑时断言原样生效）。
- **切片 2b：源码内容指纹**（新增 `tools/源码指纹.ps1` + csproj 的 `GenerateSourceFingerprint` 目标）：
  构建期把 App 工程每个 `.cs/.axaml` 的 `路径=SHA256` 拼成指纹，落 `obj/.../SourceFingerprint.cs`
  （日志 `[产物] source-fingerprint=…`）与输出目录 `source-fingerprint.txt`；`验证守卫.ps1` 增加逐项比对，
  能点名"是哪个文件没进这次构建"（实测 hash 篡改与"新增文件未编入"两种情形都判 exit 3 并点名文件），
  老产物缺基准文件时明说"只做了 mtime 级校验"。脚本没产出 `.cs` 即 `<Error>` 编不过（fail-closed）。
  逻辑没放进 MSBuild 是实测逼出来的：Item `Include` 值结尾的 `;` 被当分隔符吃掉（`&#59;` 也被吃）、
  `%(Hash)` 在变换里展开成空串。

- **切片 2：单元宿主**（真实模式实测）：登记 5 个自带夹具的单元
  （`lane.add-remove-contract` / `cells.swap` / `cells.rotate` / `align.modes` / `magnify.seamless`），
  `FC_STEPS` 命中即只做一次共同夹具 + 各自前置，整轮剧本一行不跑。
  单跑一条 **11 171 ms → 4 828 ms（2.3×）**，同一单元在剧本里 730 ms / 宿主里 811 ms（顺序无关）；
  未命中任何登记单元时打印"退回整轮剧本**全量执行**"并真的全跑（27 项 PASS、0 SKIP）；
  不设选择器的回归对照 29 项 PASS / 兜底仍生效 / exit 0。
- 这一轮**抓到并修掉两处我自己引入的假绿**：① 中文别名选中单元后又被英文 id 二次筛成 SKIP ⇒
  一项没跑却打印"所选单元全部通过 ✓" exit 0，改用 `StepNamedAsync` 并加 fail-closed
  "选中单元必须留下读数，少一行即判不可信"；② 退回剧本时选择器仍生效 ⇒ 27 项全 SKIP 后照样"全部通过"，
  退回时改为解除筛选。规律重申：**退出码和"全部通过"字样都不是证据，逐单元读数行才是**。

- **切片 2 续：登记单元 5 → 10**（真实模式 `FC_STEPS=*` 一次跑完，**10 690 ms**、10 项 PASS、0 失败 0 跳过）：
  新增 `layout.enter-ab`、`layout.mode-sweep`、`variants.shape`、`magnify.wheel`、`exit.cleanup`。
  至此每个登记单元都有自己的实测成本（此前剧本里 12/29 项测出 0 ms，成本被前置吞掉）。
  单跑 `FC_STEPS=cells.swap` 时命令行传"2 路"，宿主仍按单元声明的 `Routes=3` 起盘 ⇒ 前置由单元声明而非剧本参数。
  回归对照：无选择器时剧本 29 项 PASS / 0 SKIP / exit 0 与登记前一致；Platform.Tests 369 全绿；守卫内容指纹一致。
- **内联块开始转单元，登记数 10 → 11**：`对比-分割驱动重排`（剧本里 50 行内联块）搬成
  `SplitRelayoutUnitAsync`。宿主侧 `FC_STEPS=*` 4 路 **11 项全 PASS / exit 0**，多出行
  `PASS layout.split-relayout (172 ms)`；剧本侧回归 `exit=0`、**28 项**、0 失败、
  `裁剪下发累计 17 次`（29→28 是有意合并：原先 cells 驱动与 crop 下发各占一行，现归进同一单元行，
  累计仍生效故兜底没失效）。搬出时把"不能要求宽高都变"那段断言形式说明一并放进方法注释并标注勿改回。
- **登记数 11 → 13**：`模式-分屏入口` / `模式-网格入口` 两段内联块（剧本里 72 行）搬成
  `SplitEntryUnit` / `GridEntryUnit`；宿主 `FC_STEPS=*` 自动按单元声明起 **5 路**夹具，13 项全 PASS / exit 0。
  搬出时堵上一个**恒真子断言**：≤4 路时"多余路应被隐藏"那段循环一次都不执行，现按 R4 显式打印
  "子断言跳过（该分支恒真）"，5 路下才有真读数（`第 4..4 路确被隐藏`）。
  ⚠ 这个前提我第一版写成 `throw`，把剧本在 4 路上从"恒真的绿"变成 `exit=1` —— 是搬移造成的回归，
  已改为"显式跳过 + 单元自己声明更强夹具"；改后 4 路与 5 路剧本均 `exit=0`、28 项、0 失败。
- **独立审查轮**：只读子 agent 交出 8 条"假绿嫌疑"，我逐条回代码核实 —— **7 条成立、1 条驳回**，
  其中 3 条是我这几轮自己引入的（两个恒假的 `< 0` 判据、剧本三行漏打 `_step` 标签、
  兜底豁免条件写成了"选择器非空"而非"真的跳过了"）。修复后实测：
  `FC_STEPS=part:P6` 精确选中 5 个 P6 单元（此前恒 0 命中⇒退回整轮）；宿主 13 项全 PASS 且
  分组不再有"未归类"；剧本侧 `放大镜浮窗坐标` 从 4 行回到 1 行、`对比-放大闸门/无缝放大/放大复位`
  各归其名；未命中时全量 28 项 PASS 且 `裁剪下发累计 17 次` 走正常兜底。
  详细逐条采纳/驳回见 `docs/49`。另查出我的 python 改写重复插了两行注释（仅注释）、
  以及两次替换因 `\r\n` 模式静默 MISS —— 都靠"改后计数"当场抓到。
- ⚠ 本轮自查撤掉一条我写进 `docs/49` 的**假陈述**（声称"顺带修掉一处静默失效的缺失读数比较"，
  实际那段代码工作正常、我没有改它）。

### 顺带修掉的三条 `--comparemodetest` 判据（同一轮里首次被执行到）
门禁此前总在这几步**之前**中止（`docs/46` 记为"步名 0 命中"），所以三条判据是**今天第一次真的跑**，
一跑就红——三条全部是判据自身的问题，产品侧读数是正确的：
- **对齐模式**：原判据断"等分辨率下像素级必须与相对逐值相同"。该前提对任何 `z>1` 都不成立——
  像素级模式下共用 `CropX/Y` 的含义是"可平移范围内的相对位置"，等分辨率下也满足
  `crop = view·(1-1/z)`（z=2、view=0.25 ⇒ 0.125，与纯算术层 `CompareCropPlannerAlignTests` 一致，
  也与本文件内 `无缝放大` 判据自己的注释一致）。改为按定义断言：各路露出的**源像素起点/宽度**
  等于独立复算值、区域不出画面，并补上"相对模式逐路恒等共用值"这一半（此前只查了 `Align` 字段）。
  判红 6/6 → 判绿 6/6。
- **轮换画面（3/4 路）**：原判据拿"第 0 路 `Bounds` 变了没"当"换位生效"的代理。读数是
  `_compareRoute=[1,2]`、`Grid.CellRoute=[1,2]`（轮换确实生效），而 Avalonia 对
  `IsVisible=false` 的控件**不回写 Bounds**——`CompareGridView.ArrangeOverride` 里那句
  `Arrange(0,0,0,0)` 不落地 ⇒ 被撤下的那一路带着陈旧矩形，与真正占格的那一路读出同一个值，
  于是"位置必须变"与"两格矩形必须互不相同"双双假红。改为断言**每格由哪一路占据**
  （`Grid.GetSurface((格号+1) mod 路数).Bounds == 该格矩形`）+ 掉出格表者必须 `IsVisible=false`，
  并给周期回归补上"重新可见"以免它成为构造恒等。
- **letterbox 修正（4 路）**：该判据两侧都是同一批公开纯函数的**浮点**结果（不涉及 OS 区域回读），
  却沿用了像素级容差；4 路 2×2 的格宽高比≈源的 16:9，修正量本身只有约 0.5 单位 ⇒ 判据在这套几何下
  没有鉴别力（读数里 663.478 vs 664 就是这个量级，产品其实修正了）。改为浮点等值判定，
  并把"本步实际看到的最小修正量"打进成功日志，避免"小到看不见"冒充绿灯。
- 另：`无缝放大` 的"子 HWND 尺寸必须落到 OS 窗口"一条由固定 200ms 等待改为**有界等待（≤2s）**。
  触发时的读数是"实得恰为 1× 格尺寸"⇒ 是 AfterRender 那一拍**还没落**，不是被工作区钳制
  （被钳会给出≈工作区宽）。判决与消息原文未改；等待循环内**不**再调 `UpdateLayout()`
  （实测会连带触发裁剪下发、把判红点搬到别处）。
- 真实模式现状：`--comparemodetest` 2 / 3 / 4 路均 **exit=0 且"全部通过"**，`--selftest` **exit=0**。
  未解一项：4 路 + **演示模式**下 `放大镜浮窗坐标` 判红
  （`Avalonia Δ=(932.0,262.0) Win32 Δ=(665.3,374.7)`，同一份源码的两种模式下复现唯一差异就是模式）。

### 缺陷（用户读到的症状是"左下角加路点了没反应"）
「加路 / 减路 / D1~D9」把新 lane 数锚在**会话数** `_sync.Count` 上，而不是**网格路数** `Grid.Count`。
但素材恒占前 `_sync.Count` 条 lane、尾部允许存在空 lane（冷启动的默认形态就是 2 条空 lane），
于是三个后果：

- **A/B 下按 `+` 两次再按 `−` 两次 ⇒ 两路素材连同网格被清空**。`−` 撤的是 `_sync` 的最后一路会话
  （连同它的解码位置与偏移），`SetCount` 撤的却是最后一条 lane ⇒ 两个方向不互逆。
- **0 素材时「加路」是减路**：`SetCount(_sync.Count + 1)` = `SetCount(1)`，默认 2 格空网格缩成 1 格；
  而「减路」因 `_sync.Count <= 0` 裸 return，只能靠 `D2` 恢复。
- **A/B 下加路看不见**：AB 格表只有 2 格，第 3 条 lane 被 `CompareGridView.ArrangeOverride` 隐藏，
  而模式收敛看的是会话数（仍是 2）⇒ 既不升到 ABC，也没有任何提示。
- 附带：`D2` 在 9 路时会把网格撤到 2 路，与注释承诺的"只加不减"相反；加路撞 9 路上限时静默。

### 修复
- 锚点统一为网格路数，规则收敛到两个 `internal static` 纯函数：`LaneCountAfterAdd` / `PlanLaneRemove`
  （它们是这两个数值的唯一生产者），`GrowLanes`（D1~D9）同锚点。
- 减路优先撤尾部空 lane（素材、偏移、播放位置一律不动），没有空 lane 才撤最后一路素材。
- 两处静默改为可见：加路触 9 路上限写状态栏 + `AppLog`；对比模式下新 lane 落在格表之外时
  提示"只显示前 N 路（按 C 切换模式）"。

### 证据
- 新增真机判据 `AssertLaneAddRemoveContractAsync`，**同时挂在 `--selftest` 与 `--comparemodetest` 上**
  ——此前发布门禁 11 项里没有任何一步按过这两个键，`--comparemodetest` 也是直接 `Grid.SetCount(routes)` 起盘。
- 修复前判红（真实模式，两处都停在②"撤空 lane 不得动素材"）：
  selftest（`small.mp4`，1 路）`网格 3→0（期望回 1） 路数 1→0`；
  comparemodetest（4K H.264 真素材，2 路 A/B）`网格 4→0（期望回 2） 路数 2→0`。
- 修复后：`--selftest`（真素材 4K H.264 + AV1 两枚素材）**exit=0**、新步 ✓；
  `LaneCountContractTests` 15 例钉住 lane/route **分叉**的取值（真机跑到那两步时 lane==route，
  新旧两套锚点算出同一个数 ⇒ "0 素材冷启动把 2 格缩成 1 格"这一支只有这张真值表拦得住）；
  Platform 369 / Core 720 全绿；Release 编译 0 错误、1 告警（既有 `AVLN3001`）。
- `--comparemodetest 2` 整测仍停在**既有的**不判稳步骤（两次分别红在 `对比-对齐模式` 与 `无缝放大 z=2`，
  后者红形状与 docs/46 记录的 `r=4` 同款）。已用"关掉本步的对照跑"在同素材下复现同一处红 ⇒ 与本修复无关。

## [v0.3.0-beta] — 2026-09-23 · AB / ABC / ABCD 模式功能补全（依据 docs/47 可行性报告）

本轮不是"找回丢失的代码"：报告核验后确认笔记里的多数需求在 Git 历史中**从未存在过**，
工作是"在既有骨架上接线与扩展"。按报告的分期建议落地，未触碰内核 API（版本仍为 15）。

### 新增模式（Core）
- **AB 竖分**（`CompareMode.AbVertical`）：AB 支持上下分栏。框架层（`SplitAxis.Y` / `CursorY` /
  覆盖层分支）本就就绪，补的是 Core 的 `ComputeCells` / `HandleAxisAt` / `HandlePositionAt` 分支。
- **ABC 三列**（`CompareMode.AbcColumns`）：复用 `SplitParams` 的两个自由度作两条竖线
  （`OrderPair` 排序 + 最小间隔，杜绝负宽中间列与"两线重合后卡死"），无需扩展结构体。

### 结构改动（有两处会静默失败，已配守卫测试）
- **可用模式判定改为集合判定**：原 `IsAvailable` / `CoerceMode` 用"序数 ≤ 上界"，其前提是
  "可用集合恰为枚举前缀"；加入变体后不再成立。回退目标改为**只在标准形态（Ab/Abc/Abcd）里挑**，
  避免"4 路 ABCD 掉到 2 路"变成上下分栏。
- **手柄模型从单值升级为多值**：新增 `HandleCount` / `HandleAxisAt` / `HandlePositionAt`。
  覆盖层的绘制、命中测试（含 `WM_NCHITTEST` 物理坐标版）与拖动全部改为逐个手柄。
- `ModeVariantTests` 用 `Enum.GetValues` 遍历钉住上述四处 switch —— C# 对带 `_ =>` 兜底的
  switch **不报** CS8509，漏改分支是静默的（表现为"竖分被画成左右分"）。

### 交互
- **滚轮 → 多路同步无缝放大**（报告 4.2，此前内核完整但生产 UI 无任何触发路径）：
  对比模式下滚轮改 `SetCompareMagnify` 且**以光标为锚点**（`AnchorCrop`），
  缩小到底退出；放大态可拖动平移，**松手时提交一次**（不逐帧 `SetWindowRgn`）。
  拒绝提示按倍率去重，连续滚动不再刷屏。
- **左右互换 / 轮换画面**：新增"格 → 路"映射（`CompareGridView.CellRoute`），
  排版、命中测试、裁剪下发、`CellMagnify.SourceOf` 四处共用同一份映射。
  轮换按**路号**循环，故 4 路用 AB 也能轮换到第 3、4 路（按格左移做不到）。
- **打开后自动进入对比模式**（设置项 `AutoEnterCompare`，默认关闭）：挂在 `onAllOpened`
  上（此刻路数才准确），并保住用户已拖好的分割位置。

### 修复
- **R7（docs/41:119）**：放大源尺寸原本只在启用那一刻读一次，增删路数后不刷新
  ⇒ 新增的路取到 `(0,0)`（按"铺满"换算，露出**不同**源区间）。改为长度不符即重读。

### 审查后补修（本轮自查 + 子代理独立评审发现）
- **ABC 三列：两线交叉后拖错分隔条**（功能性）。手柄画在**排序后**的位置上，而写回哪个
  分量原先由覆盖层按固定索引猜 ⇒ 把左线拖过右线后，"拖动 A|B 分隔条却改变了 B|C 的分界"。
  新增 `CompareLayout.HandleComponentAt` 让 Core 给出分量身份（手柄 0 恒为靠左那条）。
- **像素级对齐：右侧/底部一大块永远看不到**（阻塞）。视口位置的合法区间是 `[0,1]`，
  但平移与滚轮两处仍按相对对齐的 `[0, 1-1/z]` 钳位（z=2 时丢整整 1/4 画幅，且不可逆）。
- **像素级对齐：滚轮锚点坐标域混用**（阻塞）。`AnchorCrop` 的输入/输出是源画面归一化坐标，
  而像素级对齐下 `_compareCropX` 是"视口位置" ⇒ 两个域直接相减，从 1× 起第一次滚轮就偏。
  改为 `AnchorPixelAligned`（在像素坐标域算，再反算回视口位置）。
- **像素级对齐：拖动平移慢 `z/(z-1)` 倍**：除数应为 `Wc·(z-1)` 而非 `Wc·z`（格宽显示的
  是 `minW/z` 个源像素，而可平移总量是 `minW·(1-1/z)`）。
- **像素预算闸门可被 route 绕过**：估算把"格 i"与"路 i"当同序，互换/轮换后错位
  ⇒ 4 路用 AB 轮换到 [2,3] 时算的是没在显示的两路。改为按 `RouteIndexOfCell` 配对。
- **预算估算早于状态更新**：`_compareAlign` 与源尺寸在估算之后才赋值 ⇒ 首次滚轮用上一次的
  模式与空源尺寸估算，低估到 `(minW/W_i)²`，且状态栏报"已放大"而画面仍是 1:1。
- **格→路映射跨素材残留**：换一批全新素材后仍沿用上次的映射（一进来就是左右互换，
  且菜单无任何勾选反映）。打开新文件时若当前无路则清除。

### 分辨率对齐：做成可切换的模式（设置 → 分辨率对齐）
- **相对对齐**（默认，既有行为）：各路共用归一化裁剪区间 ⇒ 露出各自画面中相同的相对位置。
- **像素级对齐**：各路露出**相同源像素尺寸**、且像素起点相同的区域（以最小的那一路为基准）。
  实现上关键是"逐路有效倍率" `z_i = z·W_i/minW` —— `Magnify` 的构造决定"格恒好露出 1/z"，
  **只挪裁剪位置改变不了露出的像素数**，必须让高分辨率路的窗口放得更大。
  副作用已计入：像素预算按 `z_i` 累加（否则 4K 与 1080p 同场、z=2 时 4K 路实为 8 倍，
  按共用 z 估算会低估到 1/16，闸门形同虚设）。
- `z=1` 时**没有可平移的余地**（起点须让最小路也容得下 ⇒ `p ≤ minW·(1-1/z)`，上界为 0），
  故此时只能贴左上；要平移需先放大。
- 读不到任何一路源尺寸（演示模式 / 纯音频）时**显式提示**退化，不静默当成已对齐。

### 未做（需先确认需求，见 docs/47 §八）
- **每格下拉框选源**（4.4 完整形态）：映射内核已完成，UI 暂用"互换 + 轮换"覆盖；
  覆盖窗口上的 ComboBox 交互在本项目**从未实测**（docs/30/31/42 三项均列为待做），
  待真机验证后再升级。

## [v0.2.5] — 2026-09-15（含 2026-09-16 补充项）

以「完整管线审查」为主线的一轮质量收敛：两轮工程基建审查 + 一轮业务正确性审查，
外加一次渲染停滞专项排查。

### 业务正确性（本轮重点，用户可直接感知）

- **帧步进先全路暂停**：播放中按 F12 时，master 被引核置 Paused 而从路走 Seek
  （Seek 不改变播放状态）⇒ master 停住、从路继续跑，画面立刻错帧。
- **放大镜采样点修正**：改用该路 surface 内坐标 + DPI 缩放，并绑定到指针命中的那一路
  （原实现传面板全局坐标、漏乘 RenderScaling、未排除 letterbox）。
- **像素回读坐标域修正**（09-16 补）：内核 `FFF3FP_ReadVideoPixel` 的坐标域是
  **后台缓冲 / 交换链**尺寸，**不是**片源分辨率。原实现把片源坐标直传 ⇒
  窗口小于片源时（常态）探针读到偏右下的位置，甚至越界返回黑。
  已在 `Core/Backend/PixelReadback.cs` 收敛为**单一实现**：展示仍用片源坐标
  （对用户才有意义），只有读取走换算；同时修掉同一缺陷的第二个受害者 `DiffOverlayView`。
  （注：此前判定"探针对、放大镜错"，实测**两者都错过**，此处已订正。）
- **偏移面板步长改用选中路帧率**：原一律取 master 的 fps，选中 60fps 路而 master 是 24fps 时
  一次「±1 帧」会挪 41.7ms 而非 16.7ms。
- **master 偏移恒为 0**：它是规范时间轴的基准，原先连它自己也被加偏移，
  导致 SeekTo 与帧步进两套语义矛盾、每次微调都累积。
- **移除 master 时偏移重基准**：新 master 偏移归零、其余路减去它的旧偏移。
- **Seek 按各路自身时长钳制**：短片源不再被 Seek 到超出片尾。
- **帧步进串行化**：加互斥门，避免并发步进交错。

### 交互

- **滚轮缩放最终值不再被节流吞掉**（09-16 补）：`ApplyViewTransform()` 的 16 ms 节流是
  **直接丢弃**而非合并补发；滚轮事件停止后不会再有调用 ⇒ 最后一次缩放**永久丢失**、
  画面与状态栏不符且**不自愈**（触控板惯性滚动间隔 8–16 ms，极易命中；
  "缩放后立刻重置视图"同样会被吞）。改为丢弃时挂一次性 UI 线程补发（16 ms），
  补发前校验 `_sync.Count > 0`，避免会话释放后 P/Invoke 触发 0xC0000005。

### 新增能力

- **播放期漂移检测与周期校正**（leader/follower）：各路独立时钟必然发散，
  以 master 为基准，偏差超半帧才校正，1s 冷却防抖，仅在播放中生效。
  ⚠ 参数（1s / 半帧）为保守值，**尚未在真实多路长片实测调参**。
- **呈现停滞看门狗**（09-16 补）：判定逻辑从 `MainWindow.Playback` 下沉为 Core 的
  `Sync.RenderStallWatchdog`（纯计算、可单测）。历史上两个真实缺陷都出在**判定**上
  （P1-5 暂停时空转累加、P1-6 用轮询次数当时间 ⇒ 平移期间 415 ms 误判），
  下沉后可用确定性用例覆盖。

### 数据可靠性与持久化

- **配置原子写**（`AtomicFile`）：tmp + fsync + 原子替换。
  原 `File.WriteAllText` 会先截断，写失败即损坏配置并静默回退默认。
- **AppSettings 版本号字段**：迁移改为按版本触发。
  原无条件执行会把合法的副屏负坐标（X=-1）每次启动都清掉。
- **设置值收敛**（`AppSettings.Normalize`）：拦住合法 JSON 里的越界值。
- **会话文件路径校验**：拒绝相对路径与 UNC（后者会外泄 NTLM 凭据），
  被跳过的路径记日志，不再静默。
- **FFmpeg 目录校验**：拒绝 UNC / 相对路径 / 不存在 / 无 `avcodec-*.dll` 的目录。
  （保持配置与 exe 同目录以支撑便携部署，靠校验而非搬家来加固。）

### 性能

- 空闲时轮询从 62Hz 降到 1Hz（原「无会话」那一档因 early-return 永不执行）。
- 探针 GPU 回读节流（33ms）。
- 放大镜热路径分配优化（缓冲复用、画刷静态化、字幕缓存）。

### 排查

- **渲染停滞**：定位到「子 HWND resize 后须由应用调 `Redraw()` 才会继续 flips」
  的内核契约，第一级停滞恢复补上 `RedrawAll`。⚠ 疗效**未经复现验证**（12 次未复现），
  仅判定逻辑已下沉并补测试。完整记录见 `docs/16`。

### 复核后判定为非缺陷（未改动行为）

- 帧步进的「帧率一致性分支」：帧率不同时按帧号对齐反而指向不同时刻的内容，
  现有按时间对齐才是正确的。已修正误导性注释，并新增帧率差异状态栏提示。
- 配置迁 `%APPDATA%`：便携部署依赖「配置与 exe 同目录」，不迁。

### 测试

- 单元测试 97 → **175**（+78），每条修复均做反向验证（摘掉修复确认报错）。
  09-16 追加：`PixelReadbackTests`（9 条）、`RenderStallWatchdogTests`（10 条）、
  漂移校正确定性用例（5 条）。
- 自测新增两条实机断言（09-16）：
  - 「滚轮缩放-节流补发」：同步连发 4 次 ⇒ 断言丢弃 ≥1 且补发后内核值 == UI 值。
  - 「放大镜-探针一致性」：3×3 九宫格逐点比对两者 RGB，任一点差 >0.02 即失败。
    单点可能"碰巧一致"而漏判 RenderScaling 类错误，故用九宫格。

### 已知未决

- **多路播放崩溃**：根因在上游 `FFF.Native`（崩溃点 `dxgi.dll`；交换链改写持
  `deviceMutex_`、Present 持 `presentMutex_`，两锁互不排斥），已提交上游 issue #7，等待响应。
  09-16 复测量化（2 路 4K `--multitest`）：20 s 预算 3 次 = 1 过 2 崩；
  25 s 预算 2 次 = 0 过 2 崩；码 `0xC0000005` / `0xC000001D`（**SIGILL** ——
  托管 C# 不可能产生，必在原生侧）。⇒ **与片尾无关**，是播放时长累积的概率性崩溃。
- **09-17 上游 PR #8 已合并但复测无效**（详见 `docs/18`）：上游合并了
  `824093d`（`PresentTimedText` ↔ `EnsureSwapChain` 竞态修复），本地内核已迁到该基线并重建
  （`3fcompare-kernel-2026.9.17.1`）。复测 **12 次崩 6 次 = 50%**，与修复前 23 次崩 12 次
  = 52% **无差异**。源码核对：issue #7 建议的 5 条只修了 2 条，
  `RequestRecoveryIfDeviceLost()`（500 ms 空闲轮询路径）与 `ClearSurface()` 仍只在
  `deviceMutex_` 下访问交换链 —— 这正是"零交互也会崩"的成因。仍需回上游跟进。
- **09-17 附带发现**：多路静置播放 8s 后漂移达 **138~195 ms**，超 `--multitest` 的 100 ms 阈值
  （2/10 次）。这是"漂移校正参数未实测"的第一批真实数据，1s 冷却 + 半帧阈值偏保守。
  崩溃率未降但已有 4/10 次能跑完全程，该项**不再完全阻塞**，可开始实机调参。
- **渲染停滞疗效未证实**：`RedrawAll()` 12 次未复现，需先复现停滞才能判定疗效。

---

## [v0.2.0] — 2026-09-14

- 内核基线 SHA 收敛为唯一真源；内核 DLL 与 SHA 绑定
- 工具脚本：使用说明 BOM 编码修正、门禁 `LASTEXITCODE` 假绿修复
- 崩溃转储符号化工具链 + 多路崩溃函数名级归因
- 纯重构：拆出 `MainWindow.Playback.cs`

## [v0.1.4] — 2026-09-14

两轮管线审查的修复：
- PAINTSTRUCT 结构体尺寸错误导致的栈溢出
- 倍速跳位、放大镜自锁、会话布局还原
- 重建计数语义、内核 SHA 写入时机、部署缺件校验
- C2/C3/C4/C6 回归断言（纯计算下沉 Core + 端到端断言）
- 版本真源统一 + 门禁可诊断性

## [v0.1.3] — 2026-09-06

- 缩放、启动同步、工具 UI 稳定化（合并 PR #1）

## [v0.1.2] — 2026-09-01

- 方向键移动修复
- 主要功能完成

## [v0.1.1] — 2026-08-23

- 鼠标交互架构定型：滚轮走 Avalonia 路由，按下/移动/释放走 WndProc
- 平移管线重构：SurfaceCreated 时序修复 + ApplyViewTransform 节流
- 性能：ReadMediaInfo 缓存、渐变刷缓存、StatusInfo 去抖

## [v0.1.0-beta] — 2026-08-23

首个可用版本：Avalonia 11 全新实现，鼠标交互 / 平移管线 / 网格对比骨架。
