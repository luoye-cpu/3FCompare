# 上游 PR 底稿 · 3FP（2026-10-11）

> 目的：把两棵树当前的全部实现整理成**一支给 `Lake1059/FFF_Project` 的大 PR**。
> 本文件是底稿与阻断清单，不是提交说明。**远端动作（push / 开 PR）一律等用户点头**，本轮只做到本地可评审。
> 每条读数后标来源：`【我实核】`=主循环亲读；`【lane X】`=该路只读 lane 报、我已复算或两处独立同读；`【未判】`。
> 冻结输入：`tests/output/pr-assembly-20261011/snap-1725/{TP,KF}`（17:25:50，两树各 28 个脏件，498 件 / 11.1 MiB）——后续所有移植以快照为准，不再追活树。

---

## 一 上游现在在哪（实测）

| 项 | 读数 | 取数 |
|---|---|---|
| master tip | **`20bcc00`**，2026-10-09 13:28，`Merge pull request #18 from luoye-cpu/upstream/kernel-all` | `git rev-parse upstream/master`【我实核】 |
| 树内容 == `ad646a7` | `git diff --shortstat 20bcc00 ad646a7` 为空 | 【lane C】 |
| 已合 PR | #10 图片模式、#11 HDR transfer、#13 MKS、#14 api16 scRGB viewport、#15 性能、**#16 HDR10+/ST 2094-40**、**#18 kernel-all** | api.github.com pulls【lane C】 |
| 自我关闭 | #12（被 #13 取代）、#17（被 #18 取代），actor 都是 luoye-cpu，**非作者拒绝** | timeline【lane L】 |
| 当前 open PR | **0** | `pulls?state=open`【lane L】 |
| 上游已含的关键件 | `Audio/IamfAudioDecoder.*`、`Core/MediaText.h`、`Hdr/HdrMetadataParse.*`、`Hdr/HdrToneCurve.*`、`Render/NvidiaVideoExtensions.*`、`Subtitle/SubtitleProbe.*` | `git ls-tree`【我实核】 |
| CI / CONTRIBUTING / PR 模板 | **全无**（`.github/` 0 个） | `git ls-tree`【lane L】 |
| 评审摩擦 | #8–#18 的 review 评论数全为 0；#18 从 open 到 merge **2 分钟** | reviews/comments【lane L】 |
| 体量上限参照 | 最大被合 = **#18：36 files / +28,399 −22,639**，其中**生成物占 82%**，从未被要求剔除（#14/#15 亦收过生成物与生成器） | pulls/N/files【lane L】 |
| 标题/提交惯例 | 英文、`Native:` 或 `Native+Host:` 前缀、≤70 字符；**C++ 源码注释 0% 中文**（VideoRenderer.cpp 0/974、PlayerSession.cpp 0/561、Api.h 0/442），VB 宿主侧中文天然 | 【lane L】 |
| 禁改项 | 根 `README.md` 作者明令"所有 AI 禁止更改此文件" ⇒ **PR 面里必须为 0 改动** | 【lane L】 |

---

## 二 结构性判断：这不能是一次 raw diff

**两棵树都不是 `20bcc00` 的后代**【lane C+H 独立同读，我复核】：TP `3fcompare/hdr10plus@d2f5826` 与上游的 merge-base 是 `a87046e`，`rev-list --left-right` = **25/41**；KF `feature/vsr-and-rotation@782736e` = **4/34**。

⇒ 直接对 `20bcc00` 出 diff 会**删掉上游已有的东西**：9 个导出（`CopyFrame`/`GetImageInfo`/VSR×2/`ViewRotation`×2/`ZoomViewAt`/`ProbeSubtitleStreams`/`GetLastCopyFrameBitDepth`）、6 个类型、`Configuration` 7 个字段，以及 `project.md`、`kernel-probe.cpp`、`test-kernel-equivalence.ps1`、`compare-kernel-pixels.py`、`准备Libiamf.ps1(−110)`、`发布3FP.ps1(−74)`。**上游会当成破坏它的工具链**。

⇒ 正确姿势：**以 `20bcc00` 为骨，按特性做三方移植**（每桶单独 `git apply --check` 到临时 worktree），生成物一律在 `20bcc00` 上重新生成而不带旧 blob。

---

## 三 PR 该带的东西（按桶）

| 桶 | 内容 | 源 | 移植姿势 | 验收读数 |
|---|---|---|---|---|
| **HDR10+/ST 2094 注入语义** | `frameIndex` 匹配键、暂停/EOF 装表走呈现路径、多窗口注入修复、`GetHdrSignalDiagnostics` 诊断面、`Set/ClearDynamicMetadata` 通用载体 | TP 工作树（`Hdr/`、`Api/`、`Render/` 段） | 上游已有 #16 的基版 ⇒ **只带 TP 相对 #16 的增量**，逐 hunk 对账 | 注入套件 30 个 `Check(`、`=== PASS (0 failures) ===`；FATE 单帧件判红 exit 8 |
| **Vivid 峰值域** | `vividBaselineIsHlg` 读 `next.format`（今日由另一路与注释同水）+ `HdrMetadataParse`/`HdrToneCurve` | TP+KF 同形 | 单列一笔，附"门读哪个对象"的说明 | 两树同水【我实核 TP:269 / KF:254】；含 `color_trc` 缺失的真实素材**未判** |
| **IAMF + 床双耳** | `IamfAudioDecoder`(TP 803 行) + `BedBinauralRenderer.*`(530 行，KF/上游全无) + `WasapiRenderer` `MaximumChannels 8→24` + 快照 5 字段 | **骨架必须取上游**，只叠 TP 增量 | 上游 `PlayerSession.cpp:2966-2982` 的**双喂防波堤**（`if (audioRenderer_ != nullptr) return;` + 12 行解释）**TP/KF 都没有**【我实核逐字比对】；整份覆盖会把该 bug 带回去 | 快照字段生产者 `:2731-2739/:2770-2773/:2826-2830`【lane I】 |
| **呈现链语义修复** | swapchain/重配时的源位深沿调用链传入（`CreateSwapChain`/`ReconfigureSwapChain` 各加一参，4+4 调用点）；`ActualVideoScalingMode` 初值/复位改 `Shader` | TP（今日 12 刀内） | 内核 PR 一笔 | `cl /Zs` 四 TU PASS【我实核 09:34】 |
| **逐帧步进 / 位图字幕 / 外部音频偏移** | `duration==0` 不再当上界；无 end 的位图字幕补包时长→5 s 兜底；`ClearExternalAudio` 复位偏移 | TP | 内核 PR，三小笔 | 同上；**运行时未判** |
| **宿主侧** | LRC `[offset:]`；录制 `停止()` 把 `会话.停止()` 放进 `Finally`（保 trailer）；快照/配置版本钉位随内核 | TP `FFF.Player`/`FFF.Recorder` | `Native+Host:` 一笔 | LRC 探针 7/7 绿、HEAD 版 5/7 红【我实跑】；VB 单文件编译 0 语法错 + `BC30384` 牙【我实跑】 |
| **测试与门禁的牙** | `verify_animated.sh`/`verify_all_samples.sh` 收口（子进程码外泄 + 段数=清单 + **空清单判红**）；版本位同水矩阵扩进 `tools/check_kernel_pairing.py` | 主仓 `3fp/tools`、`tools/` | ⚠ **这两个都在 3FCompare 主仓，不在内核仓** ⇒ 不进上游 PR，属本地仓的账 | 判据块 7 臂全对【我实跑】；`--selftest` 13 条变异各自判红且还原指纹相等【我实跑】 |

---

## 四 阻断清单（不解就不能提）

1. **干净检出编不过（最高优先）**：`FFF.Native.vcxproj:125/151` 无条件 `FFF_WITH_LIBIAMF=1`，`:137/138/164` 依赖表**无任何 `Condition`**（硬列 `iamf/oar/obr/obr_capi/pffft/FLAC/opus/fdk-aac` + 59 个 `absl\*.lib`），且 `BedBinauralRenderer.cpp` 全文 **0 个 `#if`**（`:8 #include "oar.h"`）【我实核 `grep -c '#if'`=0；`grep -c Condition` 的 22 处均不在 iamf 条目上】。⇒ 缺库即 `C1083 IAMF_decoder.h`。修法：给宏与依赖加 `Condition/exists`，并给 `BedBinauralRenderer.*` 加门控。
2. **前置脚本未入库**：`tools/准备Libiamf.ps1` 至今 `??`【我实核 status】；`git ls-files third_party`=0 且 `.gitignore:46 third_party/` 无例外 ⇒ 上游无人能生成那套库。脚本本身可用（`git clone AOMediaCodec/libiamf` + `cmake -DIAMF_ENABLE_BINAURALIZER=ON` + 逐库校验），**但上游现有脚本不带 BINAURALIZER 开关、只产 iamf+oar ⇒ 按上游说明取库必链不上**【lane D/I】。
3. **ABI 布局互斥**：TP 把 iamf 四字段插在 `FFF3FPSnapshot` **中部**（自测偏移 136/140/144/380），**上游已把它们放在尾部**（idx 55–58）；且 TP 无任何 `static_assert/offsetof` 守卫【lane I】。同时版本号被抢注：TP `PlayerApiVersion=20`，而 `=18` 已被上游用于 `Configuration.adaptiveDownscaleBeforeUpload`，KF 是 19【lane C/I/H】。⇒ **必须改成尾部追加 + 版本序列与上游重排**，或整段交上游裁决。
4. **版本面三处不同水（现已可判红）**：`FFF.Native.rc` 的 `VER_API=18`、三个字符串仍是 `17/17/17`，DLL 嵌入 `FILEVERSION=18`，而内核运行时 `GetApiVersion()=20`；快照门 `!=11` 而发布戳 `=9`【我实核 + 新 checker 实跑 exit=1，6 条红】。
5. **未跟踪的验收面**：KF 的 `ImageZoom{,Live}Regression.vb`、`StreamSelectionRegression.vb`、`UI/图片缩放几何.vb` 共 **约 1,228 行全部未入 git**，而 KF 工作树的 `Program.vb:19-31` 已引用它们 ⇒ 一旦照现状进 PR，上游干净检出 **BC30451、`构建3FP.ps1:57-59` 响亮判红**；且 KF 那份 `Program.vb`（约 30 行）会**覆盖上游 5,713 行的 23 档注册表**，`LyricsKitRegression` 的硬编码路径还会回退上游已修的 `LYRICS_KIT_DIR`【lane G】。
6. **私货必须清洗**（否则上游读者打不开 / 泄露本机）：`docs/NN` 编号引用 **TP 3 处 + KF 23 处**；绝对路径 `KF/StreamSelectionRegression.vb:109` 的 `C:\PLAN\3FUI\fixtures\…live_7_1_4.iamf` 与 `:11/:99` 的 `_3fc_tmp/gen_multitrack_samples.sh`；`TP/Program.vb` 6 处 `C:\diagnostic\secret.srt` 类；`准备Libiamf.ps1:17` 的 `D:\cache\libiamf` 与 `:67` 的 cmake 绝对路径；TP `PlayerSession.cpp:388`、`IamfAudioDecoder.cpp:2253/2274` 共 8 处本机素材名；KF `PlayerSession.cpp:799` 的 `fprintf(stderr,"[queue] …")` 调试残留；口语化中文注释 **KF 13 处 + TP 1 处**（"本轮/另案/挂账/产品拍板/我"）【lane E，负断言带正控】。另：C++ 侧新增注释须英化（`BedBinauralRenderer.cpp` 57/69 条、`IamfAudioDecoder.cpp` 22/228 条）【lane L】。
7. **`PATCHES.md` 不能进上游**（+378 行，上游无此文件；且 `:298/311/352` 写着 `C:/PLAN/3FCompare/...`）【lane L/E】。

---

## 五 "声明高于实现"——上游会当场打回的文本（逐条实读过）

| # | 契约文本 | 实现实况 | 处置 |
|---|---|---|---|
| 1 | `SetDynamicMetadata` 的 payload "**绝非更小前缀…exact layout**"（`Api.h:968`） | 只用 `<` 判（`HdrProcessor.cpp:682/687/700`）⇒ **接受更大 payload 并静默丢多余字节** | 改成等值判或改文本；二选一 |
| 2 | "`numWindows==1` 时 `windows[0]` 必须覆盖全幅"（`Api.h:791`） | `BuildInjectedWindow:505-513` 只查范围/单调，无全幅校验；且着色器在 `DynamicWindowCount<=1` 时**忽略矩形** | 补校验 |
| 3 | "Windows are independent"（`Api.h:762-764`） | 归一化分母恒取 `windows[0].signalPeakNits`（`VideoRenderer.cpp:4682`、shader `:844` 单值），窗口 1/2 的 maxscl 无人消费 | **登记不修**，进 Not-claimed 交上游裁（ST 2094-40 分母逐字原文未拿到） |
| 4 | 头里"钉住以防 Marshal 漂移"（`Api.h:994-997`） | 只钉了 64/68/40 三个；**`Entry`=368 B、`Window`=112 B 未钉**（自测值），而 C# 侧手工摊平 28 float | 补两个 `static_assert` |
| 5 | `HdrDynamicMetadata.h:42` 把读者指向 `HdrToneCurve` | 该文件通篇无 Vivid/CUVA 段（恒置 `ToneMappingAbsent`） | 删悬空指引 |
| 6 | 解析层有 `distribution[]`/`distributionPeakNits`/`fractionBrightPixels` | **只写不读**，零消费者；`HasToneMappingFunction()` 源码零调用 | 删或声明为"仅供未来/诊断" |
| 7 | `ColorExtension.h:15` 硬编码 `FFF.DolbyVision.Test.dll` | 全盘无该件、发布脚本条件拷贝 ⇒ `ExternalDynamic` 与两个授权导出**事实永不激活**（失败模式安全） | Not-claimed 明写"未经真实扩展验证" |
| 8 | 注入是否"拥有" reported format | **现已同水**：`Api.h:867-899` 文本已降为 "deliberately does NOT own"（我给的旧指针 `:833-840` 已偏） | 作为"修上游合同文本"单列一笔 |

---

## 六 Not claimed（PR 正文直接抄这节）

未在物理 HDR 屏验证面板（本机无 HDR 屏）· ST 2094-40 曲线输入分母的规范性原句未取到 · CUVA 005.1 参数曲线不实现、不声称与 Vivid 解码器等价 · 不声称 RPU/FEL 已应用（仅诊断/标识）· 不声称注入会改 `hdrFormat` · 不声称 distribution/百分位影响画面 · 未在带 LakeUI 的干净机器上编过 `FFF.Player` · DolbyVision 扩展兼容路径未验 · 宿主镜像 `Fff3FpNative.cs` 未逐字段复核 · **所有运行时/像素级读数本轮未取**（本轮全程只读禁构建，最强只到 `cl /Zs` 语法+类型检查与两处实跑探针）。

---

## 七 切分与正文骨架

- 分支 `upstream/native-hdr-iamf-followup`（沿用历史命名风格），base `20bcc00`，标题英文 ≤70 字符 `Native: …`（含宿主则 `Native+Host: …`）。
- 本地按**一笔一个可评审单元**做，推 PR 时 squash（#18 就是 1-commit 36-files 被收）；若手写增量 >5k 行或特性互不相干则拆两支（#16→#18 的节奏已被验证可行）。
- 拟定 8 笔：①前置入库（`准备Libiamf.ps1` + vcxproj `Condition` + `BedBinaural` 门控）②IAMF 双喂守卫回补（上游→TP 方向的修，随桶 3 走）③注入语义与诊断 API ④Vivid 门控同水 ⑤呈现链位深/缩放读数 ⑥内核小语义修（步进/字幕/外部音频偏移）⑦宿主（LRC/录制 trailer/版本钉位）⑧`Api.h` 合同文本订正。
- 正文小节：目标 → 逐特性（每节带**实测数字**）→ `Semantics settled by measurement` → `Not verified, deliberately left as claims`（沿用已被收过两次的标题）→ API 契约（导出**追加不插入**、`PlayerApiVersion` 序列、`.rc` 同步）→ 构建前置 → 已知未验。
- **PR 面必须为 0 改动的文件**：根 `README.md`（作者禁改）、`PATCHES.md`（不入上游）、`3fp/**` 与外层 3FCompare 的文档/探针/脚本。

---

## 八 执行编队（第二波，产物落 `tests/output/pr-assembly-20261011/lane-<id>/`）

| lane | 活 | 交付 |
|---|---|---|
| W1 | HDR 注入+诊断桶：从快照抽最小 patch，对 `20bcc00` 建临时 worktree 跑 `git apply --check` | 可应用性判定 + 逐 hunk 冲突清单 |
| W2 | IAMF+双耳：按"上游骨架 + TP 增量"合成 patch（含守卫、`Condition`、`#if`、字段尾部追加、版本序列） | 合成 patch + ABI 对照表 |
| W3 | 语义修复包（第五节 1/2/4/5/6 的实现侧 + 我这 12 刀）逐件出 patch，标"进内核 PR / 进宿主 PR / 不进上游" | 分档 patch 集 |
| W4 | 清洗执行表：每条私货给"原文→替换文"的成对条目（不直接改树），含 C++ 注释英化清单 | 可复核的替换清单 |

**尚未排期的两项**（要人先定）：版本序列与上游的重排（18 被上游占用）、`audio-delay` 链（只在 KF）要不要进这支。

---

## 九 远端动作（**等点头**）

`git push -u origin upstream/native-hdr-iamf-followup` 与开 PR 都属远端可见动作，本文件不代替授权；本机无 `gh`，开 PR 需浏览器登录态（历史上匿名 compare 页填不了表单）。真要提交时我会先给：①最终 diff 的 `--stat` 与手写增量数；②`check_kernel_pairing.py` 全绿读数；③"README 0 改动 / PATCHES.md 不在面内"的自检命令。

---

## 十 2026-10-12 装配轮复核：本文件有 5 条结论被实测推翻或改写

原件一字不改留档，以下为装配台的实测修订。读数出处：`tests/output/pr-assembly-20261012/assembly/FINDINGS.md`、`per-patch-base-readings.txt`、`abi-test/build.log`。

| # | 本文件原结论 | 10-12 实测 | 对 PR 的影响 |
|---|---|---|---|
| 1 | §一「C++ 源码注释 0% 中文」（引 `VideoRenderer.cpp 0/974`、`PlayerSession.cpp 0/561`、`Api.h 0/442`） | **不成立**。基座 `20bcc00` 自身：`IamfAudioDecoder.cpp` 12 行含汉字、`FFF.Native.rc` 7 行、`PlayerSession.cpp` 3 行；只有 `VideoRenderer.cpp` 与 `Api.h` 是 0 | 清洗口径改成**按文件实测**。`.rc` 里的中文注释是上游自己的风格，不该被我们"洗掉" |
| 2 | §四-5「KF 那份约 30 行的 `Program.vb` 会**覆盖上游 5,713 行**的 23 档注册表」 | **方向说反**。`FFF.Player.Tests/Program.vb`：基座 **15 行 / 1 旗标**、TP **5,713 行 / 54 旗标**、KF 62 行 / 4 旗标；且 `--lyrics-kit-regression` 是**基座有、TP 无** | 真实风险是"拿 **TP** 那份当骨会删掉上游已有入口"。合并必须以基座 15 行为骨逐档并入 |
| 3 | §三 桶 4「呈现链：源位深沿调用链传入（12 刀 F11）」当作**我方贡献** | **上游早有**：基座 `VideoRenderer.h:376-377` 即 `CreateSwapChain(w,h,hdr,outputBits,`**`sourceBitDepth`**`)`、`:378-379` `ReconfigureSwapChain(hdr,outputBits,sourceBitDepth)` | F11 从 PR 面撤出（对 TP 而言是补齐自己落后，不是给上游东西） |
| 4 | §三 桶 4「`ActualVideoScalingMode` 初值/复位改 `Shader`，因 VP 写点为 0」（F4） | 前提**只在 TP 成立**。基座 `VideoRenderer.cpp:6379` 有活的 `actualVideoScalingMode_.store(VSR active ? VP : Shader)`；而基座 `:2150` 初值、`:6872` 复位仍写 `D3D11VideoProcessor` | 只带"初值/复位"两半，`:6379` 的三元**不许动**（lane-V 已按此排除） |
| 5 | §四-2「前置脚本 `tools/准备Libiamf.ps1` 未入库 ⇒ 上游无人能生成那套库」 | 只对 **TP** 成立（TP 里是 `??`）。基座里该脚本**已跟踪，110 行**；但它 `BINAURALIZER` 命中 **0**、只 stage iamf+oar | 难点从"把我们的脚本提交进去"改成"**改上游那 110 行**"：加 `-DIAMF_ENABLE_BINAURALIZER=ON` 并补齐 oar/obr 产物，否则照上游取库必链不上 |

另三条装配事实（不是对本文件的推翻，是新增）：

- **§三 缺 VSR 桶**。基座 `ApplySuperResolution`（`:3403..3580`，178 行）函数体内 `viewRotation` 命中 = 0，`:3450` 拿视图轴 target 比未旋转源尺寸 ⇒ **上游今天带着 docs/88 那条 P1**。修复只躺在未提交的 KF 里，已补成独立一桶（lane-V，两支，含 6 条 `static_assert`）。
- **§三 缺 audio-delay 桶**（用户 10-12 并入）。实测 12 处命中只在 KF，TP 与基座均 0 ⇒ 对上游是纯新增导出 + 纯新增快照字段。
- **B4 与 X1 的快照尾部追加互斥**，且两支各自推算的 `sizeof` 都被编译器否掉：合并后实测 `sizeof(FFF3FPSnapshot)=408`、`audioDelay100ns=400`（两支都写的是 400/392）。已由主循环合成单支 `T1-snapshot-tail-and-audio-delay.patch`（104 行、3 hunk、10 条编译器实测守卫、负控 `C2338` 验过）取代 B4 + X1 结构体段 + X3。§四-3 的"尾部追加"因此有了**唯一合法次序**：`384 iamfContentChannels → 388 iamfBinaural → 392 audioBinaural → 400 audioDelay100ns`。
