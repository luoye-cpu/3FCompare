# 3FP 可变帧率（VFR）内容周期与帧率模式 —— 本轮改动快照（2026-10-16）

## 为什么这里放的是文件副本，不是补丁

`3fp/kernel/` **整树被外层仓 `.gitignore` 忽略**（该规则的注释写明：与 `third_party/fff_project/`
同上游不同基线，"本树只落试验性改动"；不入库的原因是第三方源码＋构建产物＋嵌套 git 仓）。
所以动内核源码后在外层仓 `git diff` 是空的——我第一次按常规存 diff，拿到的是 **0 字节文件**，
那不是"改动没落盘"，是根本没跟踪。内核目录自己有 `.git`，`kernel-dirty-full-20261016.patch`
是那份仓的整树脏差异，**但它不只含我的改动**（见下"归属"）。真正的还原点是逐文件副本。

> **本轮收尾状态**：内核改动已落在内核仓提交 `7832e74`（分支 `pr/all-local-extra`）里，
> 4 files / +126 −11。提交后工作树剩下的 `kernel-worktree-remaining-foreign-20261016.patch`
> （80 行）全是别人的活（`Api/FFF.Player.Api.h` 8/4、`Audio/BedBinauralRenderer.h` 4/2、
> `Audio/IamfAudioDecoder.cpp` 2/1、`README.md` 2/1，加 `PlayerSession.cpp` 那句 `:4010` 注释改写），
> 一行没被动过。本目录的 `.cpp/.h` 副本是**提交前**的还原点，留在盘上作应急，不入库
> （它们和跟踪文件重复，会随源码演进而过期）。

## 归属：哪几段是我这轮改的

内核仓现在 8 个 `M` 文件。本轮开始前就有 4 个是别人的、我没碰：
`FFF.Native/3FP/Api/FFF.Player.Api.h`、`Audio/BedBinauralRenderer.h`、
`Audio/IamfAudioDecoder.cpp`、`README.md`。我动的是这 4 个，它们开始前都是干净的：

| 副本文件 | 我的落点 | 归属／机制证据 |
| --- | --- | --- |
| `PlayerSession.cpp` | ① `PresentVideoFrame` 的四级取数链（下一帧 PTS 差 → `frame->duration` → `avg`→`r` → 0＝未知）；② 帧率散布累积＋两阶段发布；③ 新函数 `ResetCadenceSpread`／`MeasuredFrameRateMode`／`FrameRateModeLabel`；④ `PumpPlayback` 里那次重发布；⑤ `FFF_CADENCE_TRACE` 诊断 | 同文件 `:4010` 那句 `DTS/AC-3 access unit → cinema-surround access unit` **不是我写的**：`git show HEAD:` 里前一句命中 1 次、后一句 0 次 ⇒ 它是工作树改动，而我本轮对该文件的全部操作里没有一次输入过那句文本 |
| `PlayerSession.h` | 散布四个成员＋`publishedFrameRateMode_`＋`MinimumCadenceModeSamples`＋三个方法声明 | 本轮开始前该文件干净（不在 M 列表里） |
| `VideoRenderer.cpp` | `UpdatePresentGrid` 末尾停留判带改用**上一次**的 ratio（`scoredSlotRatio_`）＋两处清零 | `git diff --numstat` 与我这两段一致 |
| `VideoRenderer.h` | `scoredSlotRatio_` 成员声明 | 4/0 |
| `generate_samples.sh` | 第 `[12]` 节（12a/12b/12c 共 7 件）＋核验段 `vfr_selfcheck`/`cfr_selfcheck` | 外层 diff 154/2，其中含别人开始前就有的 `[9b]` 大面积条纹节 28 行 |
| `present_pacing_probe.cpp` | ⑥ 可变帧率臂（4 条判据）＋饥饿前置＋无缓冲 stdout | 该文件本轮开始前就是**未跟踪新文件**（10-15 那轮建的）；提交它等于替那轮把它入库，提交信息里写明 |
| `content_period_probe.cpp` | 整份新建：无窗口四臂＋⑥ frameRateMode 判据 | — |
| `run_content_period.sh` | 整份新建，**入库在 `3fp/tools/`**（原先放 `3fp/build/`，那目录被 ignore，会随构建清掉） | — |

外层跟踪的两个文件 diff 在 `tracked-repo-hunks.patch`（`docs/03` 与 `generate_samples.sh`）。

## 判词为什么必须分两阶段发布

第一版把 `frameRateMode` 做成"测够 12 帧就发布一次"。⑥ 判据当场判红：12b 的前 2 秒全是 20fps，
第 12 帧时散布确实是 0 ⇒ 那一版发布 `constant` 并没有算错，但**一次性的话就永远停在那儿**。
改成"判词变了才重发布"后，读数才走到 `variable`。⇒ `open 后` 与 `播完后` 两版必然不同，
这不是 bug 而是"到目前为止"的语义；`docs/03` §A12 已把这条写成取数前提。

## 还原点与消融复现

- 全量还原：用本目录的内核四文件副本逐文件覆盖，**保持 LF**。本轮 `Edit` 把 `VideoRenderer.cpp/.h`
  整文件翻成 CRLF 过一次，而 `core.autocrlf=true` 让 `git diff` 完全不报——只能按字节数 CR 才发现
  （worktree CR=7526/7518 行 vs HEAD CR=0）。
- 消融：把取数退回旧的两支（`frame->duration` 否则 `avg_frame_rate`）再重建 ⇒ 12b.**ts** 塌成一条
  `[长4.50s @20.0Hz]`（后段红），12b.**mp4** 仍全绿。跑法见 `docs/03` §A12 末尾。

## 证据日志（`logs/`，21 份，**不入库**）

本仓 `.gitignore:50` 有一条全局 `logs/`，所以这些日志只在盘上（`3fp/build/` 与这里各一份），
不进 git——`docs/03` §A12 的每个数都应当能在这里逐字找到。要复核就照下面名字去磁盘上取：

- 无窗口：`cp2_12b-ts.log`／`cp2_12b-mp4.log`（平台段两条＋⑥ variable）、
  `cp2_12c-ts.log`／`cp2_12c-mp4.log`（一条＋⑥ constant）、`cp2_real60.log`（真实 720p60 ⇒
  "晚段<60"前置主动判红）。
- 带窗口：`vfr_ts.log`（修复前整片卡 25Hz、丢 68、呈现 86）、`gui_ts_after.log`／`gui_final.log`
  （修复后 25→60 换档、⑥ 四条全绿）、`vfr_mp4.log`（控制，修复前就绿）。
- 诊断：`trace_ts.log`（一次性发布那版，⑥ 红）、`trace2.log`（两阶段发布那版，⑥ 绿＋rebuild 两次）。
- 消融：`ab_ts.log`／`ab_mp4.log`、`abl_60.log`／`abl_240.log`；CFR 基线：`base_*.log`。

## 本轮抓到的三种仪器假信号（都不是内核的账，但都造出过假读数）

1. **磁盘瞬时耗尽**：一次内核构建报 `C1085 … No space left on device`（重试即成功，事后 C: 仍有
   16.2 GB 空闲），而 `tail -2` 把错误吞了 ⇒ 我随后那轮"12b-ts 判红"跑的其实是上一轮消融留下的
   **旧 DLL**。补建重跑后恢复全绿。所以现在取数一律同看**构建 rc=0 ＋ DLL mtime 新于源 ＋ 读数确实变**。
2. **机器负载**：本轮下午有游戏客户端（`DeltaForceClient-Win64-Shipping.exe`）、一个 `ffmpeg.exe`
   和 `MsMpEng.exe` 在跑。§A9 三条 CFR 臂的呈现吞吐从旧记录（60fps→59.7、240fps→131.5）塌到
   35.6–40.7 与 52.3。**同一负载下的消融 A/B 复现同样塌陷**（60fps 臂：修复版 60.00Hz／35.6–40.7fps
   ／传统丢弃 153–195，消融版 60.00Hz／39.2fps／166）⇒ 归机器不归改动；而**两个构建的上限读数逐字
   相同**（60.00 / 24.00 / 144.0x），这才是 CFR 无回归的证据。`瞄格误差`、`屏幕停留带内比例` 在同一
   构建内部就红绿翻转 ⇒ 记未定，与 §A9 ④ 已登记的不可重复性同向。
3. **Bash 工具会吃掉 heredoc 里的反斜杠转义**：同一个坑撞了两次（先探针、后内核 trace），症状都是
   `error C2001: 字符串字面量中的换行符`。带 `\n`／`\"` 的 C++ 字面量要走 Write／Edit 工具，或先
   Write 成片段文件再由 python 逐字插入；不要在 heredoc 里手写。

## 收尾补充（同一天的第二轮：把能独立于机器的判据补上）

- **ⓔ CFR 吞吐闸（负载无关）**：无窗口支判"呈现帧 == 该率 × 媒体时钟实际推进秒数（±10%）"。
  12c 两容器实到 **91 对 20fps×4.51s=90.2（差 0.9%）**，四臂仍 `PASS 判6/红0`。
  它顶的是"内容周期换来源以后 CFR 会不会被掐住"这个风险面；§A9 那三条带窗口臂的吞吐当时因
  游戏客户端长期占 300%+ CPU 取不到干净数——**已在下面"第三轮收尾"里单跑复现，这条不再欠**。
- **上一条"裸流不出帧"作废——那是探针自己造的。** 补事件回读后看到：`content_period_probe`
  在 Play 前无条件调 `FFF3FP_Seek(player, 0)`，裸 Annex-B 不接受定位（事件 `result=-5`，消息自称
  "playback remained active"），紧接着 `state=Ended`，于是读到 `解码帧=0`。**同一件跳过那句 seek
  复跑**：`state=Playing｜position=3.41s｜解码 88 帧｜平台段一条 [2.99s @25.0Hz]`（25Hz 正是"源里没有
  节奏信息⇒合成时钟"的正确行为）。真缺陷因此换成更小的一条：**失败的 seek 会把会话判成结束，
  与它自己的错误消息矛盾**（`SetState(Ended)` 全仓只在 `FlushAtEnd` 一处）。护栏已装：新增前置
  "零帧＋定位失败 ⇒ 本臂不适用"（裸流上实测判红并写明成因；四臂仍 `PASS 判9/判7` 不退），
  这个坑不会再被当内核读数读第二次。附带那条假阳仍成立：CFR 裸流 `av_guess=20/1` 与
  假阳，实测那条路因无帧可测而不介入）。⇒ 因此**没给裸流建判据**：那种源节奏信息本就不存在
  （实测包级 PTS 输出 0 行），正确行为就是"未知⇒只剩面板"，本轮已在无窗口臂量到它落到 60.0Hz。
- **`STATUS.md` 的那九行增量快照没有提交**：该文件整个"当前快照（2026-10-09）"段加 §二′ 待办表
  都是队友**未提交**的工作（`git diff` 里它们和我的行混在同一个 hunk），我的插入点正落在那段
  中间，无法只暂存自己那几行。所以它留在工作树里，等 STATUS 那批活一起提交；本轮事实的正式
  落点已提交在 `docs/03` §A12 与上面的"补充节"。

## 第三轮收尾（CFR 基线转成实测，以及两条关于仪器本身的结论）

- **CFR 三条基线已实测复现，条件是一支一进程单独跑。** 排进同一个批处理脚本连着跑时，第一支
  60fps 只读到呈现 **41.1 fps／传统丢弃 148**；同一支单独跑读到 **59.5 fps／丢弃 1**——污染源是我
  自己那个跑批脚本（上一支窗口还没销毁完、DWM 还在落定，下一支已经开始计时）。单跑读数（跑前记
  整机 CPU 18.7–26.2%）对上了 §A9：60fps 上限 60.00Hz／59.5fps／丢 1（旧记录 59.7）；240fps 上限
  **144.02Hz**／**131.2fps**／显示速率栏 **528**、传统栏 0（旧记录 131.5／526，且 ④ 那条判据这次转绿）；
  24fps 24.00Hz／23.9fps／两栏 0。**唯一还红的是"屏幕停留带内 ≥95%"（实测 88.2%／93.4%）。**
- **§A9 ④ 要的"锁关掉"对照臂现在建不起来，原因在仪器形状不在负载**：撕裂路整轮 `holdSampled=0`
  ——停留计数只在锁在岗那条路上存在（旁路支把 `aimedSlotRatio_` 归零 ⇒ 判带前提
  `scoredSlotRatio_ >= 1.0` 永不成立）。拿它比"钉 vs 不钉"就是拿一个数比"没有数"。要定死得先让
  停留量测脱离"按瞄到的 ratio 开带"，改记独立直方图/分位。**本轮没做，也没假装做过。**
  同一趟的正面副产品：锁让位之后上限仍报 60.00Hz、仍呈现 60.0fps ⇒ **内容那一半不依赖网格**。
- **裸流缺陷从推理升级为实测排除**：无窗口探针补了快照回读（`state／position／duration／解码帧／
  队列／选中流`）之后，裸 Annex-B 读数是 `state=5(Ended)｜duration=0.00s｜解码帧=0｜队列=0｜
  选中流 v=0`（正常件对照：`state=3｜duration=6.03s｜解码 141｜队列 5`）⇒ 选流没问题，是"零帧
  就判结束"；而 `PresentVideoFrame` 在释放闸之后，`解码帧=0` 意味着本轮那段代码在那件源上
  **根本没执行**。剩下的根因不在本轮路径上，只登记不顺手改。

## 没做的事

`STATUS.md` 的"当前快照（2026-10-09）"那行**没改**：那是带日期戳的旧快照，今天去改它等于伪造旧
读数。今天的事实供你决定是否另开一行：内核仓 8 个 `M`；`x64/Release/FFF.Native.dll` 与
`3fp/build/FFF.Native.dll` 同 md5、5,500,928 B。
