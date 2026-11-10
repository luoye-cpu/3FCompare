# 3fp 工作区 · 交接状态

> **给接手的 agent**：先读本文件，再读 `README.md`（铁律），然后按「下一步」的第一个动作开工。
> 更新本文件时请同步更新「最后更新」一行。

- **最后更新**：2026-10-11（RTX VSR P1/P2：实验树 `3fp/kernel` 修 P1 旋转几何 + 三条 P2 清理，
  记录见 [docs/89](docs/89-RTX-VSR自动模式P1旋转几何与P2清理修复记录（2026-10-11）.zh.md)；
  `cl /Zs` PASS + `static_assert` 牙齿实测，未提交、未实机运行。下文「最后更新」叙事为 2026-10-09 交接轮收口内容。）
  2026-10-09（交接轮收口：注入语义三缺陷 + 像素级闭环已修并复测，JXR/Vivid 两块补了正式账本。
  本文件重构为**短状态页**：原头部变更叙事与原「六、工作日志」
  整段已迁入 [docs/archive/status-log-2026-09.md](docs/archive/status-log-2026-09.md)，按日期检索。）

## 当前快照（2026-10-09）

| 项 | 状态（本轮实测） |
|---|---|
| 内核实验树 `3fp/kernel` | 分支 `feature/vsr-and-rotation` @`782736e`，**4 个未提交文件**（3 个队友 + 本轮注释与计时修补 + layout-0 裁切修复）；构建镜像 `3fp/build` 与 `x64/Release` 同哈希 **`6F0DA5D303D40DDB`**（1,319,936 B；改前臂 `1ECD070915A7716C`/1,319,424 B 留存在 `_3fp-fix/laneM/run_bef/`） |
| 出货内核源 `third_party/fff_project` | 分支 `3fcompare/hdr10plus` @`d2f5826`，**7 个未提交文件**（注入语义修复 6 + API 契约注释 1）；`x64/Release/FFF.Native.dll` **已还原为基线字节** `E5A7ADCB53B8DDB9`（803,328 B），与 `.3fc_kernel_build.json` 的 `dllSha256` 逐字符相符 |
| 3FC 出货基线 | `3fcompare-kernel-2026.10.8.1` / `d2f5826`（API 17、**90 导出含注入三函数**）⇒ **third_party 就是出货源**，`3fp/kernel` 的成果默认**不进出货** |
| 本轮修完 | D1 注入匹配键（`pts`→`frameIndex`）、D2 暂停/EOF 装表**画面级**生效（走呈现路径 `RefreshHdrShaderInputs`）、D5 峰值归属（注入项优先，否则流级基线）、夹具守卫（FATE 单帧件判红 exit 8）、`ShaderBytecode.h` 排版噪声 49,035→24,357 行（−50.3%） |
| 回归（修复版出货内核 `CF6785F9B7684DE4`，全程同哈希） | SDR `fmt=0 peak=100 outBits=8 presented=78`；HDR10+ `fmt=2 path=2 dyn=1 peak=300 presented=77`；JXR `fh2_fp16` 可呈现；注入套件真夹具 `=== PASS (0 failures) ===`；FATE 守卫判红 |
| **已修的产品缺陷（本轮收口）** | 出货内核曾把 HLG 基线的 HDR Vivid 按 PQ 绝对域读：`peak=10000`（锚点 4000）。门控 `vividBaselineIsHlg` 已从实验树**移植进 TP** 并实测：HLG 规范夹具 `10000 → 4000`、HLG 无静态 `=1000`、**PQ + `maximum_maxrgb=2048/4095` 仍 `=5001`**（×10000 那半没被切掉）、HDR10+ `peak=314` / SDR `peak=100` 未动 |
| PR 包 | **一个 PR、一笔提交，base 直接是上游 master**：`3fp/patches/hdr10plus-pr-all-2026-10-09.md` —— 分支 `upstream/hdr10plus-on-master` @ **`fbd98fc`（父 = `12d8afc`，上游 master 本体）**，**18 文件** / +3,694 −731（15 内核 + `FFF.Native.rc` + 两个 VB 宿主件；标题后另 amend 过一次**纯提交说明**，树哈希未动 `2ebaa09…`），建在一次性克隆 `_3fp-fix/pr1/tp-clone`（工作树干净；**分支已于 2026-10-09 push 到 fork**：
`ls-remote refs/heads/upstream/hdr10plus-on-master` ⇒ `fbd98fc5eb991aaac0c7e42690350cadb9b770ce` 与本地逐字符相同，
GitHub compare 页自渲染 **1 commit / 18 changed files / 3,694 additions / 731 deletions** 与本地读数对得上；
**PR 未提** —— 本机浏览器未登录 GitHub，匿名 compare 页 `textbox` 命中 0，表单填不了，那一下归用户）。**移植已真做**：`merge-tree` 报 12 文件，真打上去是 **5 文件 / 9 hunk**，逐处裁决 + 本轮复核见 `docs/77`；干净重建 **0 error / 0 warning**；三个部署位守卫在两修复臂上各跑一次全绿（PR 臂 exports **92** / 出货臂 90、kernel=17/managed=17、dest abi 2、config ABI 80 B/15 字段，`judged-pass=6/fail=0/not-judged=0`），跑完部署位还原成记录件并复算断言（`E5A7ADCB…` 803,328 B MATCHES record）；**15 条电池"退出码 + 结构字段"三臂逐字相同**（随取样时刻走的 `presented/peak/frameIndex` 单列，不作跨臂判据），带牙探针 J1 三区域 0.00000、J2 近黑 0/5、J3 SDR 装表 0/10 点移动、单帧守卫仍 **exit 8**。**收尾趟新修 D9**：两窗口表下未用槽 2 没停车 ⇒ 退化框仍带权重 1 与全零曲线行做加权平均，**整幅在 PQ 码域被砍一半**；同一支计数探针只换 DLL 的 A/B = `758DC466` **FAIL 0.77126/0.79863 exit 1** → 两修复臂 **PASS 0.00000/0.00000 exit 0**。**⚠ 我先前那句"当前版探针 12/12 PASS"是错的**：lane F 现版把该断言降级成不计数打印，它在带缺陷的臂上同样 12/12 PASS ⇒ 无牙，不能当证据。**lane G 的第二个发现登记不修**：多窗口归一化分母恒取 `windows[0]`（只翻倍窗口 1 自己的 maxScl ⇒ 区域移动 0.00000），改它要先动 b2 布局且必须先把同源钉的参照换掉（否则对更合规实现判红 0.46945–0.48663）、ST 2094-40 的分母逐字未拿到 ⇒ 写进 PR 正文 Not claimed 交上游裁。补丁路径重验：`format-patch` ⇒ `git am --keep-cr` 在 `12d8afc` 上 **exit 0**，落地树哈希 `2ebaa09…` **与本笔树相同**；私有引用对 `12d8afc..HEAD` 新增行重扫 **0 处**、FATE 引用保留 **6 处**；上游代码丢失审计 = **21 行、条条对应本笔改写点，无无关丢失**；`SdrShaderBytecode.h` 重生成后与上游**三个数组逐字节全同** ⇒ PR 只带 `ShaderBytecode.h` 一个生成物（D9 只动 C++，不涉字节码）。~~备选 `upstream/hdr10plus-all` @ `1db27cf`~~ **已退役不作 PR**（TP 生成物头本轮归位成生成器版式，该支会带约 2.5 万行纯版式）。**未覆盖**：宿主层（`--selftest` / 发布门禁）没跑过这支新二进制（904,192 B，不是出货件）；`FFF.Player` 在克隆里缺 LakeUI 依赖编不过，与移植无关；D9 的混合带内接缝未逐像素取样。旧三分支包 **作废留痕**。**layout-0 裁切修复仍不入这批**：自适应政策 `cedb7bb` 在任何远端都不存在（`git branch -r --contains` 空）⇒ 无上游承接对象，提案 `feature/vsr-and-rotation` 时合进那一笔；出货树全树搜 `adaptive` 零命中、无可达性（政策若进 TP 必须同带四刀，锚点见 `docs/76 §六`） |
| 第二支 PR（其余全部本地工作，已 push） | **`3fp/patches/kernel-followup-pr-2026-10-09.md`** —— 分支 `upstream/kernel-all` @ **`72c0aec`**（父 = `12d8afc` 上游 master 本体，`ls-remote` 复核远端与本地同 SHA），**34 文件 / +27,626 −21,931**（89.6% 是两份生成物字节码重排，手写面 `VideoRenderer.cpp` +1,100/−80 等约 2,200 行；`72c0aec` 上重跑的值。先前记的 31/+27,517 是 `4878da0` 时的数）。内容 = `#16` 之外全部本地工作：IAMF 沉浸式音频（含新 `tools/准备Libiamf.ps1` 与 README 前置说明）、解码/上传性能、自适应降尺度 + layout-0 四刀几何修复、VSR/旋转/平移缩放/内嵌歌词/离屏回读、JPEG XR 浮点输入（原为**未提交**工作树内容，已随本支落定）、宿主 VB + 测试。**为什么是一支而不是三支**：实测 B 组 `b5db0a4` 写 C 组 `f1e3019` 引入的 `viewRotation` 字段（lane S1 六种排列全测，C→B→A=8 冲突为下限，与整条重放同），且政策与四刀不能拆、IAMF 缺 `libiamf` 上游根本链不动。验收：`-t:Rebuild` **0 error / 0 warning**（修掉 2 个 C4267 与 `.rc` 停在 16 的版本漂移后重跑）、`check_kernel_exports` API=19 OK、`format-patch`→`git am --keep-cr` 在 `12d8afc` 上 **exit 0 且落地树 `aea1f9bb…` 与分支树相同**、私货引用 12→17 处替换→**0**。**未验如实写进提交说明**：宿主 `FFF.Player` 本机编不过（LakeUI 缺，910 BC30002）；VSR 未量（本机 NVIDIA App 全 `isEnabled:false`）；IAMF 置换只对照解码器自身次序；准备脚本未端到端跑；`0 / 3,686,400` 那类无件数字**没有写进去**。**与 `#16` 的关系**：两支都动 `VideoRenderer`/`PlayerSession`/`PlayerApi`/`FFF.Player.Api.h`/快照尾部，`#16`=17 而本支=19 ⇒ 先合 `#16` 则本支必须 rebase 重编号 |
| 正式账本（本轮新建，读数全在里面） | `docs/73`（JXR 格式实测与出货性判定）、`docs/74`（注入语义与像素级闭环）、`docs/75`（Vivid 峰值域分支与两树分叉账）、`docs/76`（自适应缩放下 layout-0 上传几何与真裁切）、`docs/77`（PR 审查轮：冲突裁决复核、缺陷闭环 D1–D9 与带牙判据）、**`docs/78`（两支已提交 PR 的全面审查台账：11 路 lane + 主循环逐条回源，含我驳回的 lane 断言清单）**；PR 交付件 `patches/hdr10plus-pr-all-2026-10-09.md`（同日的 `patches/hdr10plus-pr-{pack,squash}-2026-10-09.md` 已作废留痕） |
| 阻塞 | 无技术阻塞；待用户点头 4 件（见 §二′ 末） |

## 增量快照（2026-10-16，可变帧率内容周期与帧率模式）

上一张表是 **2026-10-09 的日期戳快照，本轮没有改它**（改带日期戳的旧读数等于伪造历史）。这九行是本轮事实：

| 项 | 状态（本轮实测） |
|---|---|
| 内核提交 | `3fp/kernel` 分支 `pr/all-local-extra` → **`7832e74`**，4 files **+126/−11**（`PlayerSession.cpp/.h`、`VideoRenderer.cpp/.h`）。只暂存本轮 hunk：提交后工作树余量正好是别人那句 `:4010` 注释改写（4 行）＋队友 4 个文件，一行没被动 |
| 外层提交 | `main` → **`e5938f1`**，12 files（两支探针、跑批脚本、夹具生成、`docs/03` §A12 与补充节、快照目录） |
| 内核树状态 | 8 个 `M`（本轮 4 个已提交，余 4 个是队友的 `Api/FFF.Player.Api.h`、`Audio/BedBinauralRenderer.h`、`Audio/IamfAudioDecoder.cpp`、`README.md`）；**`3fp/kernel/` 整树被外层 `.gitignore:63` 忽略**，所以在外层仓 `git diff` 存补丁会得到 0 字节文件 |
| 构建 | `py tools/build_kernel_manual.py` rc=0，16.4s；`x64/Release/FFF.Native.dll` 与 `3fp/build/FFF.Native.dll` **同 md5**、**5,500,928 B**、mtime 新于源码（本轮取数一律同看这三件，见下"假信号"） |
| 修了什么 | ① 内容周期改为四级取数（下一帧 PTS 差 → `frame->duration` → `avg`→`r` → 0＝未知）；② `frameRateMode` 改由**实测逐帧间距散布**判定并分两阶段发布；③ 屏幕停留判带改用**上一次**瞄准的 ratio |
| 判据 | 无窗口 `tools/content_period_probe.cpp` 四臂 **PASS**（12b 两容器各两条平台段 20→40 ＋ ⑥ variable ＋ 丢弃 0；12c 两容器各一条 ＋ ⑥ constant ＋ **ⓔ 呈现 91 对 20fps×4.51s=90.2，差 0.9%**），复跑一致 ±0.01s；夹具自证 **7/7**；带窗口 ⑥ 四条全绿 |
| 消融（证明判据有牙） | 退回旧两支 ⇒ 12b.**ts** 塌成一条 `[4.50s @20.0Hz]`（后段红），12b.**mp4** 仍全绿 ⇒ 红来自容器没有逐帧时长，不是探针自己坏。旧形状实测：4 秒丢 **68** 帧／呈现 86 → 修复后丢 **11**／呈现 **148** |
| 仍未收口 | §A9 三条 CFR 臂的**吞吐**读数——本轮下午 `DeltaForceClient` 长期占 **300%+ CPU**，同一文件三次跑出 19／132／156 帧。已用同负载消融 A/B 证明塌陷两个构建同样（上限读数逐字相同：60.00／24.00／144.0x ⇒ CFR 无回归的证据是那三个上限数），并另立负载无关的 **ⓔ** 顶住风险面；**安静机器上重跑那三臂仍欠着**。`瞄格误差`／`屏幕停留带内比例` 在同一构建内部就红绿翻转 ⇒ 记未定（与 §A9 ④ 的不可重复性同向） |
| 新发现未修 | **Annex-B 裸 H.264 在本播放器里不出帧**：VFR／CFR 裸流 × 带窗口／无窗口四种组合全部 `呈现帧=0｜媒体时钟推进=0.00s｜丢弃=0`，`Open` 却返回成功。推断它在本轮改动的上游（`PresentVideoFrame` 在释放闸之后，而这里连时钟都没走），但**没做 HEAD~1 对照** ⇒ 记"代码推理排除、未实测排除"。详见 `docs/03` 补充节 |

## 二′、下一步（2026-10-09 重排，按优先级）

1. **P2 路线**：实验树的 Vivid HLG 门控 + JXR 浮点通路要不要移植进出货树。移植成本实测：10 个已存在文件里 8 个 `git apply --check` 直接失败、`ShaderBytecode.h` 需全量重生成、API 17→19 十个触点 + 63 个快照字段重排、两仓不共享 object（`--3way` 要先 fetch）。**判据就是那条 `peak=10000`。**
2. **PR-1 切分**：方案已落 `3fp/patches/hdr10plus-pr-squash-2026-10-09.md`（1A 特性五笔 / 1B 本轮语义修复，各压一笔）。**push 与开 PR 属远端动作，逐次要点头**；本机无 `gh`。
3. **未定量的间歇性 Open 超时**：真 fp16 夹具 `fh2_fp16.jxr` 单次独立进程复跑 6/6 通过，此前观测到 1 次 `Open timed out` ⇒ n=7 不足定量；升格为验收件前必须多次复跑（详见 `docs/73` 勘误表第 5 行）。
4. ~~**layout-0 臂的真裁切**~~ **已修（实验树闭环，2026-10-09）**：机制 = `convertedRgb_` 按整帧尺寸转换、上传 pitch 用解码宽，而
   `EnsurePipeline` 已按目标尺寸建纹理 ⇒ `UpdateSubresource` 只吃每行前 `texture.Width` 字节，左上子矩形被着色器放大。
   修法是把缩小**折进那次已有的 sws 转换**（转换/缓冲/行距/纹理/上传五处口径同源）。改前 engaged↔control 行扫描
   **34/41 不同、平均 0.8143、max 1.00000**；改后 **4/41、平均 0.0035、max 0.08235**（残差是 CPU 双线性 vs GPU 滤波的边缘差）。
   **control 改前↔改后 0/41 逐点全等**、planar 臂（layout 1，jpg 夹具）engaged 改前↔改后 **0/41 全等** ⇒ 未 engaged 路径与队友那条臂零回归；
   **10-bit layout-0 臂同判**（改前 0.8199 → 改后 0.0044，control 逐点全等）。
   附带口径：layout-0 的 `videoUpload100ns` 改前恒不记时，改后 control **24.44 ms** → engaged **0.64 ms**（上传带宽 1/38）。
   证据 `\_3fp-fix/laneM/`（README + 四份 LAYOUT0 + 四份 LAYOUT1 原始读数 + 可逆补丁），判决件 `compare_geom.py` 由主循环复算过。
5. ~~**待点头的清理**~~ **本轮已按 lane N 的账执行完毕**：`3fp/build/` 17 份零引用变体 DLL（21.16 MB）、laneA 的 4 支
   `D1A92286` 片 FFmpeg（109.83 MB）与 `real_hdr10plus_rolloff.mp4` 副本（6.53 MB）、`run-after-tmp/FFF.Native.dll`
   （0.77 MB）、`.obj` 中间件 13 件 ⇒ **合计 36 件 / 145,649,359 B（138.9 MB）**，逐件台账
   `_3fp-fix/laneN-apply/deleted.json`（A–D 组的哈希取自 lane N 删除前自己的 census，E 组现场测量后 unlink；
   4 件 `.obj` 因「同名 `.cpp` 不在同目录」这条更严前置而留下）。**清理脚本第二轮才落盘**，
   第一轮在 `deleted.json` 写出前崩溃 ⇒ 前 23 件是**事后重建**（已逐条确认不在盘上、且各有同哈希留存件位置）。
   **未动**：`hdr10plus-run/`（111.85 MB，交接文档与 docs/74 判词互斥）、`audit_tmp/`（含唯一哈希）、
   `audit-baseline/`（git 恢复不了）、`_3fc_tmp/abtest/`（1110.9 MB，属 3FC 侧）。
   另：`3fp/build/FFF.Native.{bak,bak2,dll.bak,keep,prem.bak}` 不在 lane N 的 `FFF.Native*.dll` census 口径内
   ⇒ 重复性未证，本轮不删。**跑 `3fp/tools/bench_ab.ps1` 会覆写 `3fp/build/FFF.Native.dll`**（现 `6F0DA5D3…`），跑前先记哈希。
   `3fp/samples/media/*` 是空目录属正常（本体不入库，跑样张回归前先 `samples/tools/generate_samples.sh`）。

> **另一条已量到的红（不属本轮引入，也不属 PR 内容）**：`tools/check_kernel_ledger.py --upstream 2acd96d` 判 **3 项不一致**
> ——A1 有 8 个成员未登记（`dynamicConstants_` `dynamicCurveBuilt_` `dynamicCurveTexture_` `dynamicCurveView_`
> `hdrMetadataValid_` `injectedActive_` `injected_` `lastHdrMetadata_`）、B 两条探测计数对不上
> （`EffectivePaperWhiteNits` 实得 7 / 台账写 6；`PlayerApiVersion = 16` 实得 0 / 台账写 1）、D 有 141 个长标识符没点名。
> 归因已查：**这 8 个成员全部在 `d2f5826`（10-08 的 HDR10+ 五笔）里就有、在 `3940990` 里一个都没有**
> ⇒ 欠账来自那批已提交的特性历史，**不是 10-09 这轮修复引入的**。订正要动的是内核树里的 `PATCHES.md`（§十一/§十三），
> 且这条会拦 3FCompare 打包门禁 ⇒ 属 3FC 侧账，按"3FC 先搁置"未动。哨兵自证对工作树只读（F 项 PASS，前后指纹 `a5e99a77aef7`）。

> 下面 2026-09-30 那份「二、下一步」是**历史清单**（图片模式/MKS/文件关联那一串），保留不删，但已不是当前优先级；与之冲突处以本节为准。

## 当前快照（2026-09-30，历史快照）

| 项 | 状态 |
|---|---|
| `3fp/kernel` 检出分支 | `upstream/mks-subtitle-pr` @ `33ac900`（MKS PR #12 已 rebase 到上游 `235f8bc` 并 force-push） |
| 3FC 出货基线 | `3fcompare-kernel-2026.9.30.1` / `3940990` —— **API 16 已重移植进 `third_party/fff_project`**（真源：`tools/构建全部.ps1` 的 `$KernelBaselineSha`） |
| 上游 PR | #10（图片模式）、#11（HDR）已合并；#12（MKS）在评；api16-scrgb-viewport 提交 `ff0f17a` 已推送待开 PR（基于上游 tip `5d2cb21`） |
| 回归基线 | 内核 785408 B 0 失败；宿主测试 729/0；`--mks-subtitle-regression` EXIT=0 |
| 阻塞 | 无新增；见下文「二、下一步」 |

> ⚠ 历史叙事（09-29 及之前）曾说「应用真正跑的内核是 `3fp/kernel`」——**已过时**：
> 09-30 起 API 16 已按 `third_party/fff_project/PATCHES.md` 流程重移植进 fork 树，
> 出货树恢复正常，别再按旧叙事选构建源。

## 一、已完成

| 项 | 状态 | 产出 |
|---|---|---|
| 可行性分析 | ✅ | `docs/01-可行性分析.zh.md` |
| 色彩专项审查（广色域/HDR/GainMap） | ✅ | 同上 §4.6（**推翻了 3 处原判断**） |
| FFmpeg 能力实测 | ✅ | 同上 §5；工具 `tools/探测FFmpeg能力.ps1` |
| 实现规划 | ✅ | `docs/02-实现规划.zh.md` |
| 模式隔离设计规格 | ✅ | `docs/00-图片模式设计规格.zh.md` |
| 独立工作区 + 内核副本 | ✅ | 本区域；分支 `feature/image-mode` |
| 与上游沟通 | ✅ 已初步沟通 | 色彩三条结论已据此修正 |
| 上游仓库分析与 PR 策略 | ✅ | `docs/03-上游仓库与PR策略.zh.md` |
| **fork 同步到最新上游** | ✅ 当时 | `fork/master` = `2acd96d`（原落后 54 提交）—— ⚠ 上游 09-24 又推进 3 个提交到 `a87046e`，fork **再次落后**，见 §开头「fork 落后」 |
| **PR-A：K1+K2+K4 实现** | ✅ 已提交 | `942ab9c`，**已编译通过**（MSVC，EXIT=0） |
| **PR-B：K3 判定改 nb_frames** | ✅ 已提交 | `2465862`，已编译通过 |
| **PR-C 内核：`GetImageInfo`** | ✅ 已提交 | `c0534ce`，已编译通过（字段：帧数/动画/EXIF旋转/格式位深/ICC/alpha） |
| **PR-D 外壳图片模式（M1 骨架）** | ✅ 已提交 | `0e076fe`，6 文件 +263 行，**宿主已编译通过**（0 错误 0 警告） |
| **广色域保留（图片模式）** | ✅ 已实现 | `4a5774a` + `88526b5`（见 §2.5）；primaries 字段 + P3→BT.2020 + 放宽 scRGB 门禁 + 平移钳制修复 |
| **实机验证程序** | ✅ 已写 | `3fp/tools/verify_image_mode.cpp`，已编出 exe（见 §七） |
| **动图循环播放（用户硬要求）** | ✅ 已重编+实测 | `FlushAtEnd` 的 `animatedImage_` 分支 `DoSeek(0)` 回绕；A 类 GIF + B 类 HEIC 探针双 `LOOP RESULT: PASS`（见 `build/loop_07{b,e}.log`） |
| **动图循环「接缝卡顿」定量** | ✅ 已定案（2026-09-26） | **默认循环成立**（5 格式都无 `PlaybackEnded`）；**只有 APNG 每轮丢 1 帧**且**根因是 FFmpeg `apng` demuxer/decoder**（CLI 独立复现，非 3FC）。见 `docs/14` |
| **APNG 循环丢帧修复** | ✅ **已修+实测**（2026-09-26） | APNG 关 demuxer 回绕（`ignore_loop=1`）改走会话 `FlushAtEnd -> DoSeek(0)`。**长停 7 → 0 次**；反向验证有效；全格式/静图/宿主回归**零回归**。见 `docs/14` §7 |
| **JXR 解码可行性** | ❌ **仍不能跑通**（2026-09-26） | 新 FFmpeg 已编入 `libjxr`，但 GUID 表**只认 8bpp Gray**；真实样本（扩展码 11/12）与全部彩色 GUID 均失败。见 `docs/15` |
| **JXR 白名单放开（前置）** | ✅ **已完成**（2026-09-26） | `.jxr/.wdp/.hdp` 加入**图片模式**清单（文件关联**有意不加**）；新增 `--image-extension-whitelist-regression`（反向验证判红已做）；`gray` JXR 端到端**首帧呈现成功**。见 `docs/16` |
| **JXR 满血启用方案** | ✅ **已产出**（2026-09-26） | `docs/16`：阻断点定位 + 上游修复三路线 + **上游修好后收尾清单**（换 DLL 即满血）+ `tools/verify_jxr.sh` 三层判据 |
| **新 FFmpeg 正式启用** | ✅ **已换入 7 处**（2026-09-26） | 三件套（`2026-09-24 c0e8b139fd`，含 `libjxr`）+ **6 个 clang 依赖**；8 处 `avcodec-63.dll` md5 全等 `bff388…`、`libjxr` 计数全 = 3；**净 ≈ −1.9 MB**；回归零新退化。备份 `/tmp/ffmpeg_swap_backup_1790369759/` |
| **图片模式启动速度分析** | ✅ **已产出**（2026-09-26） | `docs/17`：冷启动 **1205.9 ms**；根因 **首触 JIT**（图标首次 74 ms vs 二次 2.4 ms）。⭐ **`PublishReadyToRun` 已落地**（`FFF.Player.vbproj` 一处，未动 .vb）：发布产物 **−141.4 ms（−13.0%）/ 11 轮全胜**；单文件交付 **−74.1 ms（−8.9%）/ 9 胜 11**、**+7.5 MB** |
| **启动优化 · 其余候选** | ❌ **实测后否决**（2026-09-26） | FFmpeg 探测窄化（差 −23 ms 在 ±60 ms 噪声内 + 有功能风险）｜图标懒加载（首触 JIT 且下一行立即用到，只是挪后）｜去掉 `UseWPF`（deps.json 里 WPF 库 = 0，零收益）——详见 `docs/17` §5 |

---

## 二、下一步（2026-09-30 版，**已被 §二′ 取代**，保留作历史）

### ✅ 阻塞项 1 · 样张集 —— **已解除（2026-09-24）**

**17 项已生成并核验**，脚本入库：`samples/tools/generate_samples.sh` + `samples/tools/inject_metadata.py`。

- [x] 1.1 脚本可生成的：sRGB PNG(±alpha)、序列图 `img%03d.png`、多页 TIFF、>16384 大图、16bit PNG / EXR、动画 WebP/GIF/APNG、ICO
- [x] 1.2 PQ AVIF（从 HDR10 视频抽帧）、HLG JXL（zscale PQ→HLG）
- [x] 1.3 带 EXIF Orientation=6 的 JPEG
- [ ] 1.4 **仍需真实素材**：PQ HEIC（本机 ffmpeg **无 heif muxer**）、iPhone gain map HEIC / Ultra HDR JPEG（Q8）、动画 JXL、多尺寸 ICO

> ⚠ **推翻 `samples/README.md` §3.1 的旧结论**：本机**有**完整 ffmpeg CLI
> （`C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\`，含 libjxl/libaom/libwebp/libzimg/lcms2）。
> 另：ffmpeg **写不出** PNG 的 cICP、JPEG 的 EXIF、多页 TIFF ⇒ 由 `inject_metadata.py` 后处理注入（三处均已实测确认）。

### 🟡 阻塞项 2 · Q1 / Q5 / Q8 / Q9 —— **2 项定案、1 项部分、1 项未做**

> 完整读数与证据见 `docs/04-实机验证报告（2026-09-24）.zh.md`。

| 编号 | 问题 | 状态 | 结论 |
|---|---|---|---|
| Q5 | 各格式解码后 `color_trc` 是否被填充 | ✅ **定案（正向）** | PQ AVIF→`smpte2084`、HLG JXL→`arib-std-b67`、P3 PNG(cICP)→`iec61966-2-1`、EXR→`linear`，**帧级**已填充 ⇒ **HDR 卖点成立** |
| Q9 | RGB/float 输入的色彩与精度 | 🟡 **部分定案** | 色彩：RGB→RGB 不过 YUV 矩阵 ⇒ 硬编码 `SWS_CS_ITU709` 对 RGB 源**无影响**。精度：**EXR 实测 `bitDepth=32` ⇒ 触发 `(1u<<32)` UB 与 `FullRangeChromaOffset(32)` 除零**（实测未崩，属潜在缺陷） |
| Q8 | gain map 是否被 ffmpeg 暴露 | ✅ **定案（不支持，读取侧）** | 本机二进制串扫描 0 命中；`mov` demuxer 的派生项 switch **只处理 `grid`/`iovl`，不认 `tmap`**；上游仅有一个**未合并**的**写侧** PR #24264 ⇒ **D4 维持，但依据需修订**（见 `docs/04` §7.1） |
| Q1 | 基线打开 PNG 的实际行为 | ✅ **定案（A/B 实测）** | 基线：PNG/JPG/TIFF 打开即 `PlaybackEnded=yes`、seek 落点被静默夹到时长；**AVIF 被判 `staticImage=false`**；**ICO 的 seek 直接报错**；17000px 大图报模糊的"纹理创建失败" ⇒ **K1–K4 的必要性全部由实测支撑**（详见 `docs/04` §2.1） |

- [x] 2.2 Q5（已定案）
- [x] 2.4 Q9（部分定案，产出 K7 的两条加固点）
- [x] 2.1 Q1（**已完成**：基线 A/B 实测，见 `docs/04` §2.1）
- [x] 2.3 Q8（已定案：不支持，读取侧）

> ✅ **顺带定案**：Q3（EXIF 方向）→ **ffmpeg mjpeg 解码器确实导出帧级 `DISPLAYMATRIX`** ⇒ M2 自动旋转可实施；
> Q4（多页 TIFF）→ 确认只解首页。

### 🟡 3 · 提第一个上游 PR（PR-A）

- [x] 3.1 实现 K1（`FlushAtEnd` 对静态图不置 Ended，改置 Paused）
- [x] 3.2 实现 K2（`DoSeek` 静态图短路为 no-op）
- [x] 3.3 实现 K4（大图预检，>16384 拒绝并给出明确文案）
- [x] 3.4 编译通过（MSVC Release x64，`FFF.Native.dll` 产出，EXIT=0）
- [x] 3.5 **实机自测**：静态图不再报错/跳 Ended（K1/K2 实测通过）、K3/K4 通过、`GetImageInfo` 字段正确
  - ✅ **K1–K4 均已有「基线 vs 改动」同批次交替的 A/B 对照**（`tools/ab_baseline_vs_patched.sh`，见 `docs/04` §2.1）
  - ⏳ 仍缺：**视频模式零回归**需在真实宿主（`FFF.Player`）上人工跑一遍（见下方「待决策」）
- [ ] 3.6 推送 `git push -u fork upstream/image-mode` 并开 PR ← **推送与开 PR 须用户确认**

> 选 PR-A 打头的原因：纯行为修正、不改 ABI、对上游也有独立价值，最容易被接受。
>
> **3.5 现状**：内核侧行为已用 `verify_image_mode.exe` 在真实窗口 + 真实 FFmpeg DLL 下验证通过。
> 剩余缺口是**宿主侧交互**（滚轮缩放/平移/上下张/列表）与**视频模式回归**，那需要一个能跑的 `FFF.Player`。

> 🚦 **提交纪律（D10/D11）**：最终给上游的 PR **只有一个提交**，且不急于提。
> 现在仍按"一个改动一个提交"在本地推进（便于回溯），最后 `merge --squash` 压成单提交。
> 已推送到 fork 的 `upstream/image-mode` 分支**只是备份**，上游侧没有任何 PR。

### ✅ M1 + M2地基 + 广色域 当前进度（2026-09-24 交接打包）

分支 `upstream/image-mode`，**7 个提交**（rebase 到 `a87046e` 后 SHA 全部改变），内核与宿主**均已编译通过**：

```
83a775c  Player: match the kernel pan scale                      (外壳 pan 对齐)
0502340  Native: keep wide gamut instead of clipping it to Rec.709  (图片广色域)
3d2f4ee  Native: report image details (frame count, EXIF rotation, format, ICC)
81befc7  Player: build the picture list from its folder and hide the timeline
7409560  Player: add a picture mode without touching video behaviour
1cc3480  Native: decide still images by frame count instead of demuxer name
127cb4a  Native: keep still images interactive instead of treating them as finished playback
a87046e  2026/9/24-3  （上游最新；旧 tip 88526b5 在 backup/pre-upstream-a87046e）
```

**已可用（代码层，未实机跑）**：打开图片自动进图片模式、滚轮缩放、左键平移、
←/→ 上一张/下一张、同目录建列表、隐藏进度条/播放按钮/时间码、菜单可手动开关、
`GetImageInfo` 查询、广色域 P3 静态图经 zscale 转 BT.2020 后走 scRGB。

### ✅ 文件关联仿图片（**已收尾**，2026-09-24 夜，`docs/11`）

用户要求「设置里的文件关联仿照视频，制作所有能解码的图片」。已做完并验证：

- **补完**：`Form设置_文件关联.Designer.vb` 三个图片 CheckBox（+66/−0）、`关联_CheckedChanged` 三条分支。
  ⇒ **宿主恢复可编译（0 警告 0 错误）**；`--file-association-regression` **EXIT=0**。
- **另修 3 处交接之外的缺陷**：① 图片数组中间的注释行截断 VB 隐式续行（74 个编译错误，
  这才是"补完声明仍编不过"的第二因）；② `启动后台同步` 的"任一启用"判据硬编码 6 个视频/音频选项
  ⇒ 只勾图片档时重启后关联静默失效，改为从类别枚举派生 `有已启用的类别()` 并被测试钉死；
  ③ 设置页几何判据量的是"独立页面设计尺寸"（永不出货的构型），改量宿主实形。
- **覆盖度换成实测分母**：图片档 35 → **39 条**（常见 14 / 不常见 13 / 老旧 12）。
  判据 = 随包内核 `Open=0` **且** `presented≥1`（`tools/cover_probe.sh`）。
  据此新增 `.apng .jp2 .j2k .j2c .xbm .dpx`，**剔除 `.dib`（无 dib demuxer、强制 bmp 报 bad magic）
  与 `.pcd`（本机无法构造能解出像素的样本）**。见 D13。
- **真机端到端已跑完**（2026-09-25，`docs/11` §9）：`.png/.jpg/.gif/.bmp/.tiff` 勾选→接管→取消→恢复
  **逐字段回到基线**；启动同步在"只勾图片档"的重启场景下确实注册 14 项（即我修的判据在真机路径生效）。
- ⚠ **两项未修**（交接给下一位，见 `docs/12` §4/§5）：
  ① **测试进程能把真机 HKCU 写掉**——`Form1:80` 无条件调 `启动后台同步`，该路径无沙箱，
  实测扫描期间把 14 项注册到了用户机器（我已手工复原，**缓解措施未实现**）；
  ② `Finally` 里"设置还原"排在 `Dispose` **之后**（我上一轮说要挪前面，实际未改）。
- ❗ **产品口径待定**：注册表写对 **≠** 双击走我们。Windows 看 `FileExts\<ext>\UserChoice`（哈希保护）。
  本机 `.png/.jpg/...` 有 UserChoice ⇒ 实测弹「选取应用」；无 UserChoice 的 `.apng` ⇒ 实测双击直接进 FFF.Player。
  ⇒ 本功能实际是"登记成可用打开方式"，不是静默夺走扩展名；提 PR 前要定调。

> 停手点快照（60%、当时宿主不可编译）见 `docs/10` §4，过程与对照数字见 `docs/11`，**当前交接文档 = `docs/12`**。

### §2.5 广色域保留实现细节（图片模式，已提交 `4a5774a`+`88526b5`）

> ✅ **2026-09-26 更新**：本节的「⚠ 未做 / 零实机验证 / 显示器不支持回落未实测」**已基本结清**，详见 `docs/13`。
> - **scRGB 输出已实机验证跑通**（`outBits=16`）：PQ AVIF / HLG JXL / Display P3 PNG / HDR10 视频（4K+8K）。
>   ⇒ §A 的 ①②（`color_trc` 填充、P3 转换后 shader 处理）与 ③（显示器不支持时回落）**均已实测**。
> - 🔴 **本轮发现并修复新缺陷**：上述改动清单第 4/5 条**只改了 `SetColorMode` 一处**，
>   而 `VideoRenderer::SetWindow`、`VideoRenderer::EnsureSwapChain` **仍只判 `IsHdrSource()`**；
>   且 `PlayerSession.cpp` 公开 `SetColorMode` 在**会话层**先按 `isHdrSource` 改写用户意图（**元凶**，短路在渲染器之前）。
>   ⇒ **P3 图被错误落到 SDR 交换链上**（没能走 scRGB），超出 Rec.709 的颜色**被静默裁掉**。三处门禁已统一为 `sourceHdr || wideGamut`，
>   会话层不再裁定（详见 `docs/13` §4）。
> - **仍未做**：视频 P3 的**逐帧** P3→BT.2020 转换（转换逻辑缺失，shader 仍把 P3 当 709）—— 见下方原文。

**需求变更**：用户要求"保留广色域"而非"压缩到 BT.709"（D12）。方案：
P3 → **BT.2020**（无损载体，P3 ⊂ 2020）→ shader 按 `source2020` 处理 → **scRGB FP16 输出**。
SDR 管道物理上放不下 P3，所以必须走 HDR/scRGB。

**改动清单**：
1. `FFF.Player.Api.h`：`FFF3FPImageInfo` 加 `colorPrimaries`/`colorSpace`/`colorTransfer` + `#define FFF3FP_IMAGE_FLAG_WIDE_GAMUT`
2. `PlayerSession.cpp::GetImageInfo`：填充上述字段（帧级优先、容器参数兜底）+ WIDE_GAMUT 标志
3. `PlayerSession.cpp`：新增 `NeedsPrimariesCarrier` / `ConvertPrimariesToBt2020`（用 `zscale` avfilter，仅静态图、Render 前一次转换；`stillImageFrame_` 也存转换后帧）
4. `VideoRenderer.cpp`：
   - `ConfigureHdrStream` 读 `codecpar->color_primaries` → `sourceWideGamut_`（新增成员）
   - `SetColorMode`：`!IsHdrSource() && !IsWideGamutSource()` 才回落 SDR（原只判 `!IsHdrSource()`）
   - `IsWideGamutSource()` 新方法
5. `PlayerSession.cpp::DoOpen`：快照检查加 `!videoRenderer_.IsWideGamutSource()`
6. 平移两侧钳制（`DrawCachedVideo`）；外壳 pan 换算对齐内核

**⚠ 未做（请接手者评估）**：
- **视频的 P3 广色域未做逐帧转换**。门禁已放开（视频 P3 也会"尝试"scRGB），但缺乏每帧 P3→BT.2020 转换，
  shader 仍把 P3 当 709 ⇒ 视频 P3 **实际仍被裁**（只是不再报错）。用户提过"视频也应该尝试 scRGB 失败回落"，
  路径已通，但**转换逻辑缺失**——每帧 zscale 太慢，需要 GPU pass 或接受裁切。这是交接后第一个待定项。
- `zscale` 在 BtbN 构建里已确认存在（avfilter 枚举 533 个，含 zscale/colorspace/libplacebo）。
- 全部**零实机验证**：zscale 输出的帧 `color_primaries` 是否真被设为 BT.2020、shader 是否真按 BT.2020 处理、scRGB 失败回落是否生效——全未见运行。

⚠ **全局零实机验证**（样张缺失）。已知待验：
① ffmpeg 是否为静态图填 `color_trc`（决定 HDR 图能否亮）—— ✅ **已实测**（`docs/04` §7 Q5：PQ/HLG/sRGB/linear 帧级均已填充）
② P3 图经 zscale 转换后是否仍被 shader 正确按 BT.2020 处理 —— ✅ **已实测**（`docs/04` §4：`primaries=BT.2020(9)` + `outBits=16`）
③ 显示器不支持 scRGB 时应自动回落 SDR（机制已存在，未实测）—— ✅ **已实测**（`docs/13` §3.2：sRGB 源回落 `MapToSdr`/`8`）
④ 验证程序本机跑 HDR 视频**输出为空** —— ✅ **已解决**（根因是缺 `FFF.Native.dll` ⇒ `EXIT=127`，非 headless 限制；见 `docs/04` §1）

⚠ **本轮（09-26）新暴露的判据坑**：`verify_image_mode.cpp:286` 在 `[open]` 快照之后**显式自调** `SetColorMode(MapToHdr)`
⇒ `[hdr]`/`[after-play]` 两段**恒为** `requested=MapToHdr`，**不能**用来判断"默认模式"行为（只看 `[open]` 段）；
视频的 `[open]` 段 `outBits=8` 也**不是**缺陷（`presented=0`，`Play()` 后才成立，判视频看 `[after-play]`）。详见 `docs/13` §3 注。

### 🟢 4 · M2 待办（按依赖顺序）

- [x] 4.1 **PR-C 内核 API**：`GetImageInfo` 已实现并提交（`c0534ce`）；`OpenWithOptions`/`SetViewRotation` **未做**
- [ ] 4.2 外壳接 `GetImageInfo`：EXIF 自动旋转、动画图默认停首帧（`loop=0`）
- [ ] 4.3 幻灯片（Timer + 设置项）
- [ ] 4.4 设置页 `Form设置_图片.vb`（缩放步进 / 默认适应 / 幻灯片间隔）
- [x] 4.5 **文件关联加图片扩展名**（`文件关联管理器.vb`，HKCU 无需管理员）—— 图片三档 39 条、
      设置页三个 CheckBox、启动同步判据改为枚举派生；入选清单按"随包内核实测能呈现"取，见 `docs/11` 与 D13
- [ ] 4.6 媒体信息面板增补图片字段
- [ ] 4.7 K7：RGB/float 输入色彩路径加固（需先做 Q9 实测）
- [ ] 4.8 **视频 P3 广色域**：门禁已放开，缺逐帧 P3→BT.2020 转换（需 GPU pass 或接受裁切），见 §2.5
- [ ] 4.9 **实机验证**：跑 `3fp/tools/verify_image_mode.exe`（先修其空输出 bug）+ 补齐样张后验 Q1/Q5/Q8/Q9

---

## 七、实机验证程序（交接关键产物）

`3fp/tools/verify_image_mode.cpp` —— 用**真实窗口 + 真实 FFmpeg DLL** 跑内核，
检查纸面推演无法确认的行为。作者已编译出 `3fp/build/verify_image_mode.exe`
（复制 `runtime/*.dll` 到 `3fp/build/` 后运行）。

**用法**：
```powershell
cd 3fp/build
.\verify_image_mode.exe "<媒体路径>" [--hdr]   # --hdr 初始即请求 MapToHdr
```

**它检查什么**：
- 静态图打开后是否跳 `Ended`（K1）、`Seek` 是否报错（K2）
- `GetImageInfo` 报的 primaries/transfer/flags
- 请求 `MapToHdr` 后交换链位深 `videoOutputBitDepth`：**16 = scRGB 生效，8/10 = 已回落 SDR**
- `isHdrSource` / `actualColorMode`

**✅ 已解决（2026-09-24）**：所谓"stdout 为空"的根因是 **exe 同目录缺 `FFF.Native.dll`**
（`EXIT=127`，延迟加载失败；当时把 127 误读成了"无头/时序问题"）。补上 DLL 后：
真实窗口正常创建、`presented` / `swapPresents` 递增、全部读数完整输出。

```bash
cp 3fp/kernel/FFF.Native/x64/Release/FFF.Native.dll 3fp/build/   # 每次重建内核后都要补
```

**批量跑法**：`bash 3fp/tools/verify_all_samples.sh` ⇒ 输出 `3fp/build/verify_all.txt`（17 项）。
**基线 A/B 对照**：`bash 3fp/tools/ab_baseline_vs_patched.sh` ⇒ `3fp/build/ab/ab_result.txt`（8 用例 × 2 版内核，同批次交替）。
> ⚠ 该脚本依赖 `3fp/build/ab/{baseline,patched}/` 两个目录各放一份 `FFF.Native.dll` + FFmpeg DLL + 同一份探针。
> 探针**必须**用基线 import lib 链接（这样才不静态导入 `GetImageInfo`），否则跑基线内核会直接加载失败。

**编译命令**（已验证可用）：
```powershell
$vc = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
$env:PATH = "$vc\bin\Hostx64\x64;" + $env:PATH
$env:INCLUDE = "$vc\include;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\ucrt;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared"
$env:LIB = "$vc\lib\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\um\x64"
cl /nologo /EHsc /std:c++20 /I "c:\PLAN\3FCompare\3fp\kernel\FFF.Native" /Fe:"c:\PLAN\3FCompare\3fp\build\verify_image_mode.exe" "c:\PLAN\3FCompare\3fp\tools\verify_image_mode.cpp" /link "c:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.lib" user32.lib gdi32.lib
Copy-Item C:\PLAN\3FCompare\runtime\*.dll C:\PLAN\3FCompare\3fp\build\ -Force
```

---

### 📌 P1 的问题描述（已修，保留作背景）

> 来源：`docs/06-图片模式接线审查（2026-09-24）.zh.md`。**已实证**，不是纸面推演。

- `播放器360视角控制器.vb:103-104` 的 `是360视频` 第三条判据是**纯启发式**：`宽 == 高×2` ⇒
  **任何 2:1 的普通图片**（1920×960 等）都会被自动判为 360。
- `播放器图片浏览控制器.vb:70` 对同一张图也会启用图片模式，且**没有排除 360**。
- `播放器画面控件.vb` 的滚轮分流是「图片 `:171` → 音量 `:175` / 全景视场角 `:176`」⇒ **滚轮被图片模式抢走**，与 D8「360 优先」相反。
  ⚠ 原文此处写"拖拽也被抢走"**不成立**：`MouseDown` 的顺序是 光盘 → **全景** → 图片，360 分支在前且 `Return`，拖拽仍归 360。详见 `docs/06` §6.3。
- 方向键：360 的 `PreFilterMessage`（`:164-201`）在自身启用时**返回 True 吃掉消息** ⇒
  `Form1` 的 `方向键快捷键已请求` 不触发 ⇒ **图片模式的 ←/→ 静默失效**。
- **实证**：1920×960 普通 PNG（无 360 元数据）在改动内核下 `staticImage=true`，同时满足 `1920 = 960×2`。
- **最小修法（未实施）**：`播放器图片浏览控制器.媒体已打开` 里前置判 `视角360控制器.是360视频(信息, 快照)`（该函数已是 `Friend Shared`），为真则不进入图片模式。

### ✅ 已修并验证 · P1/P2/P3（2026-09-24）

> 来源：`docs/06-图片模式接线审查（2026-09-24）.zh.md` §五、`docs/07-宿主实机验证（2026-09-24）.zh.md`。
> ⚠ 修复由**另一路**写入工作区（07:24 起），**已审读 + 主 Agent 亲自反向验证 + 宿主端到端验证**。**未提交**。

| 项 | 改动 | 验证 |
|---|---|---|
| **P1** 2:1 图片被 360 与图片模式同时认领 | `播放器图片浏览控制器.vb` 新增 `Friend Shared 自动进入图片模式(信息, 快照)`，判定为「静态图 **且** `Not 是360视频(...)`」 | **变异复验**：摘掉修复 ⇒ `测试失败：2:1 静态图片被 360° 与图片模式同时认领`（**EXIT=1**）；恢复 ⇒ 判绿（EXIT=0）。**宿主端到端**：2:1 图按 → 标题**不变**且画面呈 360 球面投影；4:3 图按 → 标题**变为下一张** |
| **P2** 剪辑区间抢键不尊重 `Handled` | `播放器剪辑区间控制器.vb`：`If e.Handled OrElse Not 当前模式已启用 Then Return` | 编译通过（未单独构造双模式共存的用例） |
| **P3** 缩放分支冗余 | `If(刻度 > 0, X, X)` → `X` | 编译通过 |
| 回归断言 | `FFF.Player.Tests/Program.vb` 在 `测试360识别与交互数学`（`--360-interaction-regression`）加 **3 条互斥性断言** | 判绿 EXIT=0；变异判红 EXIT=1 |

⚠ **验证过程中踩到的两个陷阱（都造成过误判）**：
1. **构建失败被 `| grep` 掩盖 ⇒ 测试跑陈旧二进制 ⇒ 假绿**。构建因 `FFF.Player.exe` 被占用报 `MSB3021/MSB3027`，
   当时只 grep 错误行、随后照常跑测试得到"通过" —— 实际跑的是变异**前**的旧二进制。**必须先杀掉宿主实例并确认构建真的成功**。
2. **`cmd | tail` 会掩盖退出码**：`./tests.exe ... | tail -6; echo $?` 拿到的是 `tail` 的状态（恒 0）。
   正确做法是重定向到文件再取 `$?`（实测判红确为 **EXIT=1**）。

### 🔴 仍未定案 · 图片适配尺寸不对（与图片模式无关）

- 实测：640×480 图在 857×591 窗口下画面为 **525×394**，**锚定右下**（左留黑 312、上留黑 120），宽高比正确（1.3325）；
  1920×1080 图则填满可绘区（宽高比 1.6284 ≠ 源 1.7778）。
- **反解**：`CalculateVideoDestination(源 640×480, 输出 851×394, limitToNative=true)` → **525×394**，与实测**完全吻合**
  ⇒ 内核按 **输出高度 394** 适配，而客户区高度约 **534**（**差 140 px**）。
- 候选根因：输出区域（交换链）按"扣掉控制条"的尺寸建立，图片模式隐藏控制条后**未让输出区域随之变大**。
- **未能定案**：想用基线内核做对照，但 **3FC 基线内核与 3FP 宿主不兼容**（换上去后宿主根本不打开媒体）；
  定案需内核侧插桩 ⇒ 需重编译内核，而**本会话内核重编译不可用**（`MSB6001` + `dotnet build` 不支持 `.vcxproj`）。
- 详见 `docs/07` §4。

### 🟡 待定 · 图片态左上角的小标签归属

`设置图片模式` 会置 `时间标签.Visible = False` 并把文本重置为零，但实测该位置仍有一个小号
"时间码 + 数字"标签（`00:00:00.100` + `1`，**非零**）⇒ 它**可能不是** `时间标签`，
而是信息图层（`播放器信息图层呈现器:230` 会格式化播放位置）的另一个元素。**未定案**。

### 🟢 记录即可（原 P4/P5/P6）

> ⚠ 原 P1/P2/P3 三项**已修并验证**，见上方「已修并验证」表；此处只保留记录项。

- **P4**：与 `docs/00` 的两处偏离（§6.5 播放按钮改为"隐藏"而非"换成上一张/下一张"；§6.4 双击适应窗口↔1:1 **未实现**，且视频模式本就没有双击全屏）⇒ 非回归。
- **P5**：文件关联**未注册图片扩展名**（`文件关联管理器.vb:399-443` 只有 video/audio）⇒ **双击 `.png` 时 Windows 不会把 3FP 列为候选**；属 M2 待办 4.5，不在本 PR 范围。
- **P6**：UNC 被内核拒是**设计**（禁用网络路径）；但 **OneDrive 占位文件 / 符号链接**因 `FILE_ATTRIBUTE_REPARSE_POINT` 被拒是**副作用**，且报英文错误。

---

## 三·补 · 当前待决策（阻塞 3.5）

| 选项 | 做法 | 代价 | 风险 |
|---|---|---|---|
| A | 构建 3FP 宿主（`3fp/kernel/FFF.Player`）并用它自测 | 需 .NET 10 + 编译 VB.NET | 低（最正统） |
| B | 用新 DLL 在**副本目录**跑 3FC 验证（不动 3FC 原环境） | 复制运行时目录 | 中（3FC 依赖部分本地扩展，行为可能有差异） |
| C | 写最小 C API 测试程序（P/Invoke 调 Create/Open/Play/Seek/GetSnapshot） | 需自建窗口句柄 | 低-中（只验 API 语义，不验渲染） |
| D | 先补样张再自测 | 与 A/B/C 组合 | — |

---

## 三、关键决策记录（改之前先看这里，别重复讨论）

| # | 决策 | 理由 |
|---|---|---|
| D1 | 图片模式**只加不改**，视频模式分支零改动 | 用户明确要求两模式相互独立；回归风险最低 |
| D2 | 静态图判定从"猜 demuxer 名"改为 **`nb_frames <= 1`**（K3） | 名字枚举永远追不上 ffmpeg；实测证明 AVIF/HEIC（走 `mov`）已被漏掉 |
| D3 | 广色域（Display P3）**剥离为 M3 独立提案** | 改动落在全局渲染管线，会影响所有视频；与看图功能不是一个量级。**但 2026-09-24 已落地「图片模式的广色域保存」**（仅静态图、CPU 端 zscale P3→BT.2020、仅改门禁与新增 filter graph，未碰视频路径），见 §2.5。视频 P3 的逐帧转换**仍未做**（门禁已放开但缺转换逻辑，实际仍被裁）——这是 M3 的剩余部分 |
| D4 | GainMap **明确不做**（2026-09-24 **复核维持**，**依据修订**） | ⚠ 原依据"ffmpeg 无 gain map 侧数据通道（头文件全目录 grep 零命中 + 组件枚举零命中）"**过于笼统**。2026-09-24 重新取证：本机二进制串扫描（`gainmap`/`gain_map`/`hdrgm`/`21496`/`urn:iso`）**0 命中**；`mov` demuxer 的派生项 switch **只处理 `grid`/`iovl`，不认 `tmap`** ⇒ 准确表述为「ffmpeg 当前**无 gain map 读取通道**；上游仅有一个**未合并**的**写侧** PR #24264」。要做得先改 ffmpeg 本身 ⇒ 维持"不做" |
| D5 | 版本 `15→16`、快照 `8→9` **一次性**加完字段 | 内核对 version 严格相等校验，加字段就必须递增，反复递增会撕裂兼容性 |
| D6 | 内核改动**不写入** `third_party/fff_project/` | 会污染 3FC 构建基线（SHA 钉死 + dirty 拒绝 + 发布门禁戳记） |
| D7 | 旋转**新增** `SetViewRotation`，不扩展 `SetViewTransform` | 扩展现有签名会破坏已有调用点；新增可让旧 DLL 返回 `NotSupported` 安全降级 |
| D8 | 输入优先级：光盘 > 360° > 图片 > 视频默认 | 360° **静态全景图**是真实场景，图片模式必须让位 |
| D9 | **PR-C 改为"只新增函数"，不动 `FFF3FPConfiguration` / `FFF3FPSnapshot`、不递增 `PlayerApiVersion`** | 内核对 version 严格相等校验，一旦递增就必须内外同步；而图片模式需要的字段（帧数/动画/EXIF/位深）**都能由新增的 `GetImageInfo` 提供**，没必要动结构体。改为纯新增后：ABI 完全不破坏，旧 DLL 返回 `NotSupported` 自动降级 |
| D10 | **给上游的 PR 最终只有一个提交** | 仓库所有者明确要求（2026-09-24）。偏离 PR #9 的"Native:/Player: 两提交"范式，但好处是**结构上不可能出现"合了内核没合外壳"的半截状态**，且上游日更时 rebase 最省事。本地保留 `upstream/image-mode` 多提交开发分支，最终另建 `upstream/image-mode-pr` 单提交分支。详见 `docs/03-上游仓库与PR策略.zh.md` §6.2–6.5 |
| D12 | **广色域要"保留"，不压缩**（2026-09-24 需求澄清） | 早先的"P3 → BT.709"是压缩，超出 709 的颜色会被裁掉。正确做法：P3 → **BT.2020**（无损载体，P3 ⊂ 2020）→ shader 按 `source2020` 处理 → **scRGB FP16 输出**（FP16 允许超范围值，才能装下广色域）。SDR 管道物理上放不下 P3，所以必须走 HDR/scRGB。详见 `docs/00-图片模式设计规格.zh.md` §9 |
| D11 | **不急于开 PR** | 功能做完 + 实机验证后再提。当前 6 个提交已推送到 fork 的 `upstream/image-mode`（**仅作备份，不是 PR**），上游侧看不到任何 PR |
| D13 | **文件关联的图片档按"随包内核实测能呈现"入选**（2026-09-24 夜） | 设置页一旦勾选就把扩展名从别的播放器手里接管，承诺是"点开就能看"。所以判据取 `Open=0` **且** `presented≥1`（`tools/cover_probe.sh`，生成器用本机 ffmpeg、判据用随包 `FFF.Native.dll`），而不是"FFmpeg 名义上有解码器"——按此判据剔掉 `.dib`（无 `dib` demuxer，强制 `-c:v bmp` 报 `bad magic number`）与 `.pcd`（解码器实名 `photocd`，本机无法构造能解出像素的样本）。⇒ 恢复任何被剔项的前提是先跑出 `presented≥1`，不是"文档里说支持" |

---

## 四、已知陷阱（踩过的坑，别再踩）

| 陷阱 | 说明 |
|---|---|
| demuxer 复合名 | `mov` 的注册名是 `mov,mp4,m4a,3gp,3g2,mj2`，按精确匹配会误判"不支持"（探测脚本第一版就栽在这） |
| 解码器名不是格式名 | JPEG XL 解码器真名是 **`libjxl`**（不是 `jpegxl`）；解复用器才是 `jpegxl_pipe`/`jpegxl_anim` |
| 静态图不触发播放结束 | `PlaybackEnded` 对图片不生效 ⇒ 播放列表自动续播不能用于看图，导航必须显式驱动 |
| 播放列表"扫描相似文件"不可用 | 按系列签名只收文件名末尾数字不同的同系列文件，`IMG_0001.jpg` 与 `风景.jpg` 会退化成只含自己 |
| 宿主无 `SetViewTransform` 封装 | `播放器控制器.vb` 只有 `设置360视角`；3FC 是自己 P/Invoke 的 ⇒ 图片缩放前必须先补封装 |
| `forceHdrOutput` 不能绕过 HDR 门禁 | 它只跳过显示器能力探测，`IsHdrSource()` 判定绕不过 ⇒ SDR 图永远回落 SDR |
| 版本错配即全废 | `version != PlayerApiVersion` 立刻 `InvalidArgument` ⇒ 内核与外壳必须同一 PR |
| VB 注释会截断隐式续行 | 多行数组初始化器里"上一项以逗号结尾、下一行放 `' 注释`"会断掉续行 ⇒ `BC30201` + 一片 `BC30035`。分组说明只能写在函数上方（`文件关联管理器.vb::创建文件类型` 头已留警告） |
| `Dock=Top` 后加入者靠上 | 面板子控件的**加入顺序**决定上下，`Location` 只是序列化残留；要让新档位排在末尾必须**最先加入**。且设置页运行时被 `Dock=Fill` 进设置窗内容面板 ⇒ **独立 `New Form设置_xx()` 量到的是设计尺寸，不是出货尺寸**（本例行高 75 vs 宿主 62，判据必须量宿主） |
| 报"格式不支持"前先疑夹具 | 本轮 `.xpm`/`.dds` 各被自己的手工样本坑了一次（XPM 行宽、DDS 漏 `reserved1[11]` 与 pixel-format 字段序），补齐即 `presented=1`；且 grep `-decoders` 会漏掉解码器实名与扩展名不同（`photocd`/`jpeg2000`）、被无关子串假命中（`dib`∈`Audible`） |
| ⚠ **Git Bash 里 `reg delete ... /f` 静默失败** | MSYS 把 `/f` 当路径转换 ⇒ 命令返回 `ERROR: Invalid syntax.` 直接不执行；若再套 `>/dev/null 2>&1` 则**完全无声**。后果是"所有清空都是假的"：所有"污染"都是从未删掉的旧键、"已复原"也是假的（2026-09-25 晚据此误判过一轮）。⇒ 清/写注册表**一律走 PowerShell**（`Remove-Item -Recurse -Force` / `Set-ItemProperty`），且**每次改完必须回读确认** |
| ⚠ **`Copy-Item -Force` 保留源时间戳 ⇒ MSBuild 跳过编译** | 用 `Copy-Item` 换掉源码做 A/B 时，目标文件 mtime 与源相同 ⇒ MSBuild 判定"未变更"⇒ **复用旧 DLL**，两组跑的其实是同一份二进制（假对照）。判据：两组的 dll mtime 完全一致。⇒ 改写源码要用「读原版 → 字符串替换 → `[IO.File]::WriteAllText`」；并**硬校验 `dll mtime > 源码 mtime`** 才允许开跑 |
| ✅ **内核 DLL「两份镜像」问题已根除**（2026-10-07） | 历史上 `tools/build_kernel_manual.py` 是 tlog 复放器（因 MSBuild 被判为 LOLBin），**按设计只输出到 `x64/Release_anim/`**，再人工拷到 `x64/Release/` 与 `build/`；09-26 曾因漏拷一处造成"假绿"。**现已统一**：MSBuild 恢复正常调用，复放器改为**薄封装**；探针（`rebuild_probes.ps1`）与出货（`tools/发布打包.ps1`）**都使用 `x64/Release`** —— 不存在第二份镜像，也就无从漏拷。同时删除 `Release_anim` 残留（212 MB）与**早已无法构建的 Win32 配置**。⚠ 教训仍有效：**`mtime` 只能证明"某一份是新的"，证明不了"两份是同一份"**；若将来又引入旁路产物，必须恢复逐字节比对 |
| ⚠ **默认值不能用 `Remove-ItemProperty -Name ''`** | Win32 注册表默认值在 PowerShell 里叫 `(default)`，传空串会报"参数 'Name' 是空字符串"。⇒ `$注册名 = if ($名 -eq '') { '(default)' } else { $名 }`，再用 `Set-ItemProperty` 写 |
| ⚠ **VB 源码是 ANSI/无 BOM** | 用脚本写含非 ASCII（如 `★`）的 `.vb` 文件易引入非法字符 ⇒ `BC30037` 及成片错误。⇒ 改源码优先用编辑工具做**最小行改动**，别用脚本拼字符串重建函数体 |

---

## 五、接手检查清单

- [ ] 读了 `README.md` 的三条铁律
- [ ] 确认 `git -C 3fp/kernel log --oneline -1` 输出 `83a775c`（我们 7 个提交，base `a87046e`）
- [ ] `git -C 3fp/kernel status --porcelain` **当前预期 13 个文件脏**（全部未提交，勿当脏数据清掉）：
      内核 `PlayerSession.cpp/.h`（动图 + 循环播放）；宿主动图接线 `Core/播放器会话.vb`、`Core/播放器控制器.vb`、
      `Interop/播放器原生接口.vb`、`UI/播放器裁剪区域控制器.vb`、`UI/播放器图片浏览控制器.vb`、`Form1.vb`；
      文件关联 `文件关联管理器.vb`、`设置.vb`、`Form设置_文件关联.vb` + `.Designer.vb`；测试 `FFF.Player.Tests/Program.vb`
- [ ] ⚠ **本仓有另一路 agent 并行写**：它会 checkout/rebase/reset 分支。读 git 状态时要把
      `rev-parse HEAD` 与 `log` 放在**同一条命令**里取，否则会读到它切到 `backup/*` 分支那一瞬间的旧 SHA
      （2026-09-24 18:13 我就据此误判过一次"rebase 被回滚"）
- [ ] 确认没有在 `third_party/fff_project/` 里留下任何改动（`git -C third_party/fff_project status --porcelain`）
- [ ] 确认两个 junction 在：`3fp/LakeUI` → `third_party/LakeUI`、`3fp/kernel/third_party` → `third_party/fff_project/third_party`
- [ ] 读了 `docs/00-图片模式设计规格.zh.md` 的模式隔离原则
- [ ] 读了 §2.5（广色域实现细节）与 §七（验证程序）
- [ ] 读了 **`docs/04-实机验证报告（2026-09-24）.zh.md`**（首次实机验证的全部读数与 6 项缺陷/风险）
- [ ] 开工前先在下方「工作日志」追加一行

> **2026-09-24 复核补充（这三条与本清单原描述不一致，以后者为准）**：
> 1. ⚠ `third_party/fff_project` **不是干净的**：有 1 处**纯注释**改动
>    （`FFF.Native/3FP/Render/VideoRenderer.cpp` +6 行，记录 2026-09-21 已回滚的 HDR 探测实验，属 **3FC 侧遗留**，与 3fp 无关）。
>    ⇒ **铁律 1 未被违反**（3fp 改动只落在 `3fp/kernel/`），但那处注释会让 `构建全部.ps1` 视基线为 dirty，需要时再定夺。
> 2. ✅ **本机有完整 ffmpeg CLI**（`C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\`）—— 样张可全脚本生成。
> 3. ⛔ **（已过期，2026-09-26 更正）** 原文："本机显示器 HDR 当前关闭（`\\.\DISPLAY1`/RTX 4060 Laptop）⇒ scRGB 输出无法在本机验证"。
>    **现况**：本机 HDR **已开启** —— 活动/Primary 输出 = **`\\.\DISPLAY5`（RTX 5080）`bits=10` + `PQ`**
>    ⇒ **scRGB 输出已验证跑通**（`outBits=16`），详见 `docs/13`。⚠ `DISPLAY1`（RTX 4060 Laptop，`bits=8`/sRGB）
>    仍在 DXGI 输出列表里，但 **HDR 判据读 Primary 输出**，不要被它的 `bits=8` 误导。
> 4. ⚠ **验证探针本身有坑**：`verify_image_mode.cpp:286` 在 `[open]` 快照后**显式自调** `SetColorMode(MapToHdr)`
>    ⇒ `[hdr]`/`[after-play]` 两段**恒为** `requested=MapToHdr`，**判断"默认模式"只能看 `[open]` 段**；
>    视频 `[open]` 段 `outBits=8` 也**不是**缺陷（判视频看 `[after-play]`）。详见 `docs/13` §3。

---


## 2026-11-10 追加（当日多条线的收口；逐轮原始记录见 `.workbuddy/memory/2026-11-10.md`）

> ⚠ **留痕**：本文件当日版本曾被一次脚本误操作清空（`open(..., 'wb')` 先截断、随后的 `write` 因类型错误失败），已从 Git 基线恢复（本文件原有 471 行），本节为摘要重建。当天每一轮的完整过程以 `.workbuddy/memory/2026-11-10.md`（224 行）为准，那里没有丢。

### 预加载（第一步 + 第二步）
- 第一步：跨进程 POD 协议、可释放缓存（静图 CPU 帧 / 容器 POD / 文件页）、`FFF.PrefetchHost.exe` 小助手（WARM 只读字节、PROBE 只 `avformat_open_input` 读 `codecpar`）、三档内存守门员（吃紧 = `dwMemoryLoad ≥ 95%`）。
- 第二步：静图命中分支接进 `DecodeInitialFrame`（`TakeStill` → 呈现 → seek 回绕），`TakeProbe` 仍只有写入端。
- 🔴 修掉的真缺陷：缓存键必须是 `CacheKey()`（`/`→`\`），否则宿主下发的列表路径与 `mediaPath_` 的 `GetFullPathNameW` 归一化结果不一致 ⇒ 缓存填满、`hits` 恒 0。
- E1（`--chain` 连续 Open、`FFF3FP_PREFETCH=0` 作零基线、8 轮×2 rep、Mann–Whitney）：108 MP JPEG **680.8 → 101.1 ms（−85 %，p=6.7e-6）**；0.9 MB PNG **26.0 → 8.0 ms（−69 %）**；16 配对行×18 字段 0 差异、媒体信息 JSON 逐字节相同。
- 🔴 口径纠正：`FFF3FP_Close()` 销毁整个 D3D 渲染器（≈270–350 ms），「Close→Open 循环」的绝对值都被它污染；`FFF3FP_PREFETCH_HIT=0` 不等于没有预加载（worker 仍在解码邻居抢 CPU）。

### 360 全景识别收敛为「只认元数据」
- 删掉 `宽 == 高×2` 几何兜底；只剩 `AV_PKT_DATA_SPHERICAL` 与容器/流 tag 白名单两路（side data 非空即权威短路）。
- 键归一化（去 `_`/`-` + 小写）+ 键值配对（投影类型键只认 `equirectangular`；布尔声明键只认 `1`/`true`）；删 `stitching_software` 与 `360` 子串匹配。
- `av_spherical_projection_name` 实测：2 = tiled、3 = half equirectangular ⇒ 原 `Contains("equirectangular")` 会误认，已修。
- 回归进仓：`FFF.Player.Tests/播放器360识别回归.vb`（29 例，`--360-identification-regression`），变异复验能判红。

### HRTF 按端点形态自动激活 + 底部徽章
- 病灶：`SelectBedRoute` 只看「插槽装了 + 端点 2 声道」⇒ 任何 2 声道端点都双耳化，立体声音箱上 5.1 被误开。
- 判据取 `EndpointFormFactor ∈ {3 耳机, 5 头戴}`；USB DAC 报 9（数字输出）⇒ 手动强制是必需项。
- 新增 `FFF3FP_SetAudioBinauralPolicy` / `FFF3FP_GetAudioBinauralStatus`（附加导出，API 仍 20）；热插拔靠渲染线程既有的 500 ms 端点比对，不加通知。
- 探针 20/20 PASS（内核 formFactor 与独立 MMDevice 查询互证）。仍 U：本机 11 个耳机端点全 NOTPRESENT，无法端到端造出活动耳机。

### HDR→SDR 参考白统一到 203（BT.2408，用户定则）
- 🔴 口径纠正：真正默认值是 `设置.vb` 的 **250**（`播放器控制器.vb` 的 100 只是构造初值，`Form1.vb` 启动即覆盖）。
- 五处改 203：`设置.vb`、`播放器控制器.vb` 的 `默认SDR峰值尼特`、`播放模型.vb`、`Form设置_HDR.Designer.vb` 滑块初值、`VideoRenderer.cpp/h` 的 `sdrPeakNits_` / `CachedVideoSettings::sdrPeak`。
- 迁移只搬「恰好 = 旧默认 250」那一档（E1 三例：250 无标记→203、203 带标记幂等、250 带标记不覆盖）。
- 顺带修好 `ReadSdrWhiteLevelDisplayConfig`：原本只用 `QDC_DATABASE_CURRENT`，本机返回 87 ⇒ 恒读 0 nits；改为回退 `QDC_ALL_PATHS` 且只认 ACTIVE path。
- 码值影响（1000 nit 源、8bit、18 % 灰 26 nit）：203→100.2、180→106.1(+5.9)、100→139.4(+39.2)、80→153.0(+52.7) ⇒ 180 与 203 只差 3–8 码值，80/100 差 30–50。

### ⛔ 已撤销 · SDR 参考白「自动读取显示器白点」（同日落地后按批准移除）
> 用户问「有意义吗」，复核结论是**净负面**，当场按批准撤销。

**为什么撤**——四条独立成立：

1. **语义错位**：`sdrPeak` 是「源内容里多少 nits 当作 SDR 满白」，是内容侧常量（BT.2408 = 203）；`GET_SDR_WHITE_LEVEL` 描述的是「DWM 把 SDR 信号抬到多亮」，是合成器增益。拿增益当内容常量，等于把映射链的一端塞进另一端。
2. **HDR 桌面下是双重补偿**：DWM 本来就把该窗口 SDR 码值 1.0 显示成系统白点（本机 100 nits）。按 203 归一化时 203 nits → 码值 1.0 → 100 nits，与同屏其它 SDR 窗口天然一致；再按 100 归一化等于把该增益预乘进内容，DWM 又乘一次 ⇒ 203 以上全被 clamp 成死白、暗部整体抬亮 30–50 码值。
3. **命中率几乎为零**：SDR 桌面 `GET_SDR_WHITE_LEVEL` 回 80 单位锚（被 >82 门挡），本机 ICC 是 stock sRGB（lumi=80，被门禁拒）⇒ 自动档等价于常量 203，零收益；只有 HDR 桌面取到值，而 HDR 桌面默认走 `MapToHdr`，根本不经过 `sdrPeak`。
4. **两处可观测缺陷**：设置页滑块显示 203（锁死）与副标题「当前 100 尼特」自相矛盾；`当前SDR参考白说明()` 内部调 `刷新SDR参考白()`，**打开设置页这个只读动作会静默改写渲染参数并重建缓存**。

**撤销范围（E2）**：删 `3FP/Render/SdrReferenceWhite.h/.cpp`、`FFF3FP_QuerySdrReferenceWhite` 导出及 `FFF3FPSdrWhiteSource` / `FFF3FPSdrReferenceWhite`（`FFF.Player.Api.h` 尾部整块 + `PlayerApi.cpp` 包装）、vcxproj/filters 四处引用；VB 侧删 `原生SDR参考白`、`查询SDR参考白`、`SDR参考白来源`、`SDR参考白解析结果`、`刷新SDR参考白`、`当前SDR参考白说明`、`HDR映射SDR参考亮度自动`、`SDR参考白已迁移203` 与一次性迁移、设置页复选框及事件；`应用SDR亮度设置`、`打开媒体Async` 回到单行固定值路径；三个取证探针一并删除（源码备份在 `_3fc_tmp/sdr_white_removed/`）。全局 grep 上述 10 个符号**零残留**。

**保留**：203 五处不动；`ReadSdrWhiteLevelDisplayConfig` 的 `QDC_ALL_PATHS` 回退修复保留——它服务既有的 `sdrWhiteLevelNits_` 路径。

**验证**：内核重建 **0 警 0 错**，`FFF.Native.dll` sha16 **`1067bfa7a4f379eb`**（6517760 B，比带功能版少 5632 B），`x64/Release` 与宿主 `bin/Release/net10.0-windows/` 两份一致；DLL 内 `FFF3FP_QuerySdrReferenceWhite` **0 命中**（对照 `FFF3FP_GetPrefetchStatus` 1 命中）；宿主 `dotnet build` **0 警 0 错**、`PlayerApiVersion` 仍 **20**；宿主实机启动、主窗口正常渲染；`--360-identification-regression` **29/29 PASS**；`Settings.json` 逐字节还原为 3095 B / sha16 `b13cbf64ee033f72`，计划任务已删。

**仍 U**：设置页在本沙箱里点不开（`SetCursorPos`+`mouse_event` 与 `PostMessage` 都被自绘控件吞掉），「HDR 页无复选框」只有 E2（Designer 已删、编译通过、grep 零残留），无新截图。

---

## 2026-11-11 · 快进/快退卡顿：seek 期间跳过非参考帧

### 定位（E1）
方向键 → `相对跳转` → `FFF3FP_Seek`（精确）→ `DoSeek(value,-1,exact=true)` → `av_seek_frame(BACKWARD)` 落到目标前最近的关键帧 → `DecodeUntilSeekTarget()` 在 worker 里逐包同步解码到目标。单次代价 ≈ `(目标 − 前一关键帧) 的帧数 × 单帧解码成本`，中间帧不上屏（`PresentVideoFrame:7071` 提前 return）却必须解，因为它们是后续帧的参考链。

`skip_frame` 阶梯（`EvaluateVideoSkipRung`）原本**刻意**在 seek 期间降回 `AVDISCARD_DEFAULT`，理由是"暂停/单步要把解码器产出的每一帧都交出来"。对精确 seek 这条理由不成立：目标之外的参考帧之外，没有一帧会被用到。

### 改动（3 处，均在 `PlayerSession`）
1. `DecodeUntilSeekTarget`：精确 seek 期间按包位置分两段——远段 `AVDISCARD_NONREF`（非参考 B 帧不重建），距目标 `(has_b_frames+4)` 帧内恢复 `DEFAULT`（目标帧自己可能就是非参考帧）。只对 H.264/HEVC 的渐进流启用（AV1/VP9 的"一包=一帧"不成立）。退出时按 `appliedSkipRung_` 还原，避免恢复播放的前几帧被静默丢掉。
2. `PumpPlayback`：播放态的 seek 原本是主泵每 tick 推一个包（61 帧 = 61 tick）。目标达成前画面本来就不变，逐 tick 只延长黑屏，改为一次解到位。`FFF_SEEK_BATCH=0` 可退回。
3. 新增 `IndexVideoPacketTimestamp()`：被跳过的帧不走 `PresentVideoFrame` 的索引维护，按包补 PTS，让帧号保持连续（不补时 9.000 s 的帧号从 121 掉到 67）。

开关：`FFF_SEEK_SKIP=0` / `FFF_SEEK_BATCH=0` 可分别回退；默认都开。

### 实测（同一进程内 A/B，`FFF_SEEK_TRACE` 同时给出包数与每包成本）
4K HEVC 10bit 硬解（`seek4k_hevc.mp4`，GOP 120、has_b_frames=2）：

| 目标 | 全解 | 跳参考帧 | 降幅 |
|---|---|---|---|
| 7.6 s | 169.6 ms | 75.5 ms | −55% |
| 9.6 s | 172.2 ms | 63.4 ms | −63% |
| 11.6 s | 155.8 ms | 62.8 ms | −60% |

4K H.264 High 硬解（`seek4k_bf.mp4`）：暂停态 217–297 → 94–172 ms；播放态 358–422 → 170–203 ms；`FFF_SEEK_BATCH=0` 立刻退回 390 ms。

连点 6 次（间隔 40 ms，照宿主"读快照 + step"写法）：**1677 → 905 ms**。

纯 FFmpeg 侧对照（`seek_floor_probe --skip nonref`）：4K HEVC 输出帧 115 → 58、解码 11.2 s → 7.2 s；4K H.264 61 → 31 帧、1354 → 575 ms。

### 正确性
- 落点逐帧一致：两臂 `framePts` 相同（9.000/11.000/13.000 s），误差为 0。
- 像素一致：`ffmpeg -skip_frame nonref` 与 `none` 在 9.0 s 的 `framemd5` 同为 `985597d190343914a121637d9a820b72`。
- 两臂日志均无 decode error / corrupt / concealing。
- `--360-identification-regression` 29/29 PASS，内核 0 警 0 错。

### 边界（必须记住）
- **无 B 帧的素材零收益**：`pr49_1080p60.mp4` 与最早的 `seek4k.mp4` 都是 Constrained Baseline（`has_b_frames=0`），输出帧数一帧不减，只有 ~14% 噪声级差异。收益完全取决于素材有没有非参考帧。
- **精确 seek 的物理下限**是「GOP 内从关键帧到目标的**参考帧**数 × 单包成本」。要再快只能牺牲精确性：落关键帧（实测 10–30 ms）或"先出关键帧画面、后台精确到位"的两段式。两者都改行为，未实施。
- 帧号仍有 2–6（H.264）/ 10–22（HEVC）的偏差：补索引按包 PTS 插，而 `PresentVideoFrame` 用 `best_effort_timestamp`，两者对个别帧不等。宿主不调用 `FFF3FP_SeekFrame`（`跳转到帧` 无调用者），可见行为不受影响。
- **`decodedVideoFrames` 仍然看不见这段工作量**：它只在 `QueueVideoFrame` 累加，而 seek 期间的帧走 `PresentVideoFrame` 后提前返回。这是既有盲区，本次未改。

### 产物
`FFF.Native.dll` sha16 **`3cc923551c6831d7`**（6519296 B），`x64/Release`、`3fp/build`、测试目录三份一致；宿主 `bin/Release` 仍是改动前的 `1067bfa7a4f379eb`（生产路径未动）。零 commit / 零 push。


## seek 预览帧（渐进呈现）——完整修复（2026-11-11，已实现、零出仓）

### 动机
`AVDISCARD_NONREF` 跳过修复把 seek 的**总耗时**降了 45–62%，但感知延迟仍在：`PresentVideoFrame` 对「position < 目标」的帧直接 return，整段参考链解码期间屏幕停在 seek 前那一帧。用户按键后没有任何视觉反馈——这就是「卡顿」的本体。

### 实现（`PlayerSession.cpp/.h`，四处）
1. **真 bug 修复**：`ClearVideoQueue()`（flush 统一入口）不清 `framePtsIndex_/framePtsIndexBase_`，回跳后新 PTS 被 `lower_bound` 插进旧表前部而 base 不变 → 快照 frameIndex 虚高、按帧 seek 的到位判定被提前满足。修法：flush 时清表并归零 base；按帧精确 seek 到位时把 base 校正到 `seekTargetFrame_`。
2. **seek 预览帧**：`PresentVideoFrame` 增加 `seekPreview` 形参。仅离目标仍远时把解出的第一帧上屏，但不结案（不清 seek 目标、不写 position/frameIndex、不解除音频门、不缓存 side data）；每次 seek 只预览一帧，近目标一律走原路径。`FFF_SEEK_PREVIEW=0` 可关。
3. **连点冷却**：预览的渲染+一次 vblank 会落在下一次 seek 的关键路径上（连点 6 次实测 1542–1704 ms vs 对照 605–1201 ms）。DoSeek 记录 `lastSeekStartedMs_`，与前一次 DoSeek 间隔 <400 ms 视为连击，整段连击不做预览。400 ms 的依据：连击时相邻 DoSeek 间隔 = 上一次 seek 的耗时（4K 实测 100–200 ms），150 ms 分不开两者；>400 ms 静默后的按键是新的意图，保留预览。
4. **解码量补账**：新增 `seekDecodedFrames_` 独立计数（不进 ABI 快照，避免污染 skip 阶梯的 decodedDelta 判定），进 `FFF_SEEK_TRACE` 输出；trace 的目标值在成功呈现清零前快照（原来每次都打 0）。

### 实测（硬解，同进程 A/B，`pv3_*` 4 rep×6 轮 + `pp_*` 3 rep×5 轮）
| 场景 | 对照首画面 | 开预览首画面 | 降幅 | 目标帧到位 |
|---|---|---|---|---|
| 4K HEVC 10bit 暂停态 | 101.7 ms | 27.4 ms | −73% | 101.7 → 109.2 ms（持平） |
| 4K H.264 暂停态 | 110.8 ms | 31.2 ms | −72% | 110.8 → 125.9 ms（持平） |
| 4K HEVC 播放态 | 62.0 ms | 15.7 ms | −75% | 62.0 → 48.8 ms（略优） |
| 连点 6 次（冷却生效） | 631/1037 ms 中位 | 646/1014 ms 中位 | 持平 | — |

### 正确性与回归
- 落点逐帧一致：两臂落点 pts 序列完全相同。
- 探针判据修正：新增「首画面ms」列，只认 `presentedVideoFrames` 增量（旧判据「位置≈目标且 frameIndex≥0」看不见预览帧）。
- `--360-identification-regression` **29/29 PASS**（`FFF.Player.Tests.exe`，本次重跑）；probe360 变异表 24/24。
- 内核重建 0 警 0 错；宿主 `dotnet build` 0 警 0 错。
- 误启 GUI 探索的 `FFF.Player.Settings.json` 确认逐字节未变（sha16 `b13cbf64ee033f72`）；23 个历次计划任务全部删除。

### 产物
`FFF.Native.dll` sha16 **`b0c02cf5f9296fac`**（6520320 B），`x64/Release`、`3fp/build`、宿主 `bin/Release/net10.0-windows/` 三份一致。零 commit / 零 push，内核 HEAD 仍 `72af915`。


## HLG→scRGB 过曝：OOTF 亮度域修复（2026-11-11，两树已实现、零出仓）

### 🔴 缺陷定位
`HlgToNits`（shader + C++ 参考实现两份）把 BT.2100 OOTF 的 `pow(·,1.2)` **逐通道**施加，正确做法是在场景亮度 `Yc=dot(E,(0.2627,0.6780,0.0593))` 上取 `1000·Yc^0.2` 同乘三通道。逐通道与亮度域仅对灰阶等效；对饱和像素等效每通道乘 `Yc^(-0.2)`（纯蓝 ×1.76）。YouTube HLG 素材（`Qs2-klYtq5Y` 两个最小 HDR 交付，已存 `_3fc_tmp/hlg_audit/`）逐像素实测：日落帧 P99 过亮 ×1.19、最亮太阳像素 scRGB 19.29→应 14.96（+29%）——中性灰全对（0.75→203nits），饱和高光系统性偏亮 20%~76% 且过饱和，DWM rolloff 下呈"高光滚白"过曝。审查全记录与修复见 **docs/141**。

### 改动（KF 与 TP 逐字同步，各四处 + 字节码）
1. HLSL：`HlgOne` 拆成 `HlgInvOetf` + 亮度域 `HlgToNits(float3)`（灰阶逐位不变）。
2. C++：标量改 `HlgInvOetf`，`Float3` 后新增 `Float3 HlgToNits(Float3)`，`EvaluateVideoColorTransform` 消费点改单调用。
3. `BuildDxgiHdr10Metadata`：`MinMasteringLuminance` 无 side data 时 0 → 0.0001（1 个 DXGI 单位）。**更正**：审查中口头报过的「HLG 元数据全零色度」系误读，`HdrStaticMetadata` 默认值本就是 BT.2020/D65，不成立。
4. `ProcessFrame`：帧级 `color_trc` 双向重分类（原只允许 Sdr 升级）——容器错标不再把 HdrProcessor 与 shader（已跟随帧级）永久撕裂；Vivid/HDR10+ 属主状态不动（真实流帧级 trc 为 UNSPECIFIED，FATE 已验证）。
5. 两树 `ShaderBytecode.h`/`SdrShaderBytecode.h` 重生成（fxc 10.0.28000.0），两树哈希一致；SDR 特化逐字节未变 = HLG 分支被死码消除（反证 SDR 链不受伤）；`check_shader_cbuffer_layout.py` 两树全绿。

### 实测与回归
- 新探针 `3fp/tools/hlg_ootf_probe.cpp`（挂 `rebuild_probes.ps1`）：五锚点（灰 0.75 / 红绿蓝 1.0 / 晚霞）容差 ±0.02，KF DLL 与 TP 源码独立构建均 **5/5 PASS**。
- `rebuild_probes.ps1` 34 探针 OK；内核两树构建 0 错。

### 产物
`FFF.Native.dll`（KF 构建，含 IAMF）sha16 **`983451b73d370877`**（6520832 B），四份一致：KF `x64/Release`、`3fp/build`、宿主 `bin/Release/net10.0-windows/`、TP `x64/Release`（出货镜像位）。**注意**：TP vcxproj 的 IAMF 链接段缺 `fdk-aac.lib`（KF 已补、TP 未跟，既有缺口），TP 本地强制 IAMF 链接会失败；出货二进制按惯例取 KF 构建镜像，TP 源码级验证用其 `HaveLibiamf=false` 构建（`54f876f9f262c63e`，探针同过）。零 commit / 零 push。

**补记（同日）**：TP vcxproj 缺 `fdk-aac.lib` 的准确表述——该链接行**已在上游 main（72af915 之前）**，TP 只是落后 53 提交才没有；TP 追平基线后自然解决，非独立缺口。上游基线事实：最近 PR = **#55**（CUDA 解码直拷，merge `0d336b7`），main 顶端另有直推 `72af915`（非 PR），KF 检出恰好=该基线。


## 综合审查与全量修复（2026-11-11，PR55+上游基线全部本地更改）

对「最近一个 PR + 上游基线上全部本地额外更改」的完整审查（PR55 全量、KF 工作树 39 文件 9 条工作线、TP 4 文件、根仓 3 未推送提交 + 107 条脏/未跟踪）发现的问题逐项修复。审查明细与修复清单见 **docs/143**；VRR 判决矩阵落仓见 **docs/142**。

### 修复要点
- **PR55 跟进（P1）**：CUDA 互操作 Probe 换 context 先按旧 context Reset + Reset 补 per-plane unregister trace（防御缺口：跨 context 注销静默失败曾致 Import 永久落回退臂）。
- **KF P2×3**：`AudioPassthroughLane::Format()` 改值返回（带锁逃逸引用）；`FFF_SEEK_BATCH` 加 250ms 时间片（暂停态「一次解到位」语义不变，`timeSliced` 仅播放态批量臂为 true）；prefetch 命中回卷失败分支补 flush + trace。
- **KF P3**：四处环境开关 0 值解析边界（"0000"/"0 " 不再被当开启，CUDA 侧三开关收敛共用 `CudaEnvStartsWith`）；CPU `HlgInvOetf` 去 Clamp01 与 HLSL 对齐；`/r` 笔误与签名挤行；PrefetchHost still 表补 AV1/HEVC；`.gitignore` 补 build/obj。
- **TP**：**移植 QDC SDR 白电平回退**（QDC_ALL_PATHS 重试，本机实测 87 错误场景）——此前只在 KF 脏改动里，不带即随包带病；`.gitattributes` 固定生成头 LF。
- **根仓**：中性化门禁 REM 大小写（带词边界）+ `SOURCE_FATAL_HOST_VB`（`24UI`/`29UI` 字面判据仅拼宿主 .vb）；**VRR 判决矩阵落仓 docs/142**（探针三处「文档 §2」改指落仓版）；VRR 探针 DXGIOutput 泄漏 + inconclusive 措辞；`r2r_a2_exclude` 删 FFF.Native.dll 死条目（余托管 10 条）；`.gitignore` 补 6 条大目录（未跟踪 111→101）；三个散落根目录 md 归档进 docs。
- **决策记录**：双耳自动门控对非耳机 2ch 端点关闭床渲染——接受，一次性 UI 提示列为产品后续。

### 验证与产物
KF `FFF.Native` + `FFF.PrefetchHost` 重建 0 错；TP 源码构建（含 QDC）0 错；`hlg_ootf_probe` 新 DLL 5/5、TP 构建 5/5；门禁脚本 `py_compile`+`--self-test` 38/38+构造样本实测（三种 Rem 注释跳过、`removehandler` 不误吞、`.cpp` 行为不变、真实宿主树 93 文件 0 假红）；VRR 探针编译链接通过。`FFF.Native.dll` sha16 **`02acb77bbe900cdf`**，四镜像一致。零 commit / 零 push。

### 出仓（同日）
KF 工作树全部待提交工作已按用户指令提交进开放 PR **#57**（`pr/passthrough-color-metadata-20261108`，fork luoye-cpu/FFF_Project）：以临时索引落树（排除 `_pr48_review/`、`LakeUI性能分析笔记`），在独立 worktree 于 `85af3fa` 之上落增量并恢复两个 `.user` 文件，**`fad4487`** 快进推送（无 force）；PR-tip 全量编译 Native+PrefetchHost 0 错、`hlg_ootf_probe` 对推送字节 5/5，上游 `refs/pull/57/head` 已同步 `fad4487`。KF 主工作树 `feature/cuda-d3d11-interop` 的工作区状态不变（仅 index 曾用临时文件，未动）；根仓与 TP 的文档/探针改动仍未提交。

### TP 收口（同日，用户裁决：禁止分叉、就地解决）
TP 原停在 `pr/hdr-peak-display-20261102` @`0db9f7e`（落后 53 提交）+ HLG/QDC 未提交改动——构成平行状态。收口：fetch 后逐文件验证 TP 脏改动**已全部被 `fad4487` 包含**（`ShaderBytecode.h` 工作区与 fad4487 逐字节相同；`SdrShaderBytecode.h` 三方一致；`VideoRenderer/HdrProcessor` 经特征行核验为子集；`.gitattributes` 为 fad4487 超集），随后**丢弃脏改动、TP 直接落到 PR 分支** `pr/passthrough-color-metadata-20261108` @`fad4487`（跟踪 fork，工作区 0 脏；临时 pr57 worktree 已拆除不再有平行检出）。TP 自树重建：**IAMF 自动链接成功**（fad4487 的 vcxproj 已含 fdk-aac.lib，旧链接缺口随收口消失），出货产物 `FFF.Native.dll` sha16 **`7a8339d42c5533a7`**（6521344 B，含 IAMF），`hlg_ootf_probe` 对出货字节 **5/5**。

### 四视角审计（同日：本地/fork/上游PR/上游基线）
KF 工作区临时索引落树 = `9ec14896`（与 fad4487 的提交树相同），树级 diff 仅差两个有意保留的 `.vcxproj.user`；上游基线 `72af915` 为 fad4487 祖先（merge-base 核验）；上游 `refs/pull/57/head` = `fad4487`。**结论：FFF_Project 范围内无任何未进 PR 的内容**。不进 PR 的三项（非内核内容）：① KF 草稿 `_pr48_review/`、`LakeUI性能分析笔记_20261106.md`（有意排除）；② fork `main` 滞后 `30b2a7e`（缺 #52/#54/#55/#56 合并，属 fork 维护非 PR 内容）；③ 根仓 3FCompare 的 3 个未推送提交与 docs/tools/tests 改动（另一 GitHub 仓，无开放 PR，无法进 #57）。


## 根仓整理出仓（2026-11-11）

四视角审计第③项收口：根仓 3FCompare（luoye-cpu/3FCompare，另一 GitHub 仓）main 落地全部积压内容。此前状态：3 个未推送提交 + 11 个脏跟踪文件 + 92 条未跟踪。

### 提交（main，就地收口无分叉）
- `ae26a45` 仓库：gitignore 补全一次性工作区与本地产物；清空壳目录与编译垃圾
- `a9a278c` 文档：101–143 台账出仓与账本同步——HLG 修复、VRR 判决矩阵、综合审查全量修复、四视角审计（56 篇 docs + 本账本 + 2 份 cleanup 记录归位 docs/）
- `6c62873` 探针：HLG OOTF 五锚点等 24 项探针入库——sdr_white QDC 回退、vrr 引用泄漏、zoom 快照 v23、注册表同步
- `9b8861d` 工具：r2r 排除表清死条目；发布打包补建预加载小助手并 fail-closed 校验
- `f9b0e46` 门禁：宿主 .vb 违规本体判据（24UI/29UI）与 Rem 注释大小写不敏感
- `dcf8640` 测试：PR/AB 与 UI 驱动 harness、enumtest 源入库；附 WGC 抓帧与 llama CUDA 探测脚本

### 清理裁决
- **删除**：空壳目录 `3fp/kernel_pr49/`、`3fp/qwen-main-merge/`、`third_party/_pr52_c1wt/`（find 证实 0 文件，worktree 残骸）；空文件 `覆盖`；编译垃圾 `pixfmt_name*.obj`。
- **gitignore 新增 20 条**（本机保留不入库）：`/test/`（124MB prefetch 实测工作区）、`/third_party/build/`、`/_3fc_tmp*` 系 5 条、`/.arts/`、`/.resultverify/`、`/endpoint-capabilities.json`（含本机设备 GUID）、`/debug.log`、`/.3fc_kernel_*` 3 条、`/FFF.HDRdp.Test.dll`、AudioEndpointProbe `_obj/`+`*.exe`。
- **保留本机**：`.resultverify/` 验证报告、`_gui_test/` 截图与 PR 草稿、kernel baseline bundle。

### 终态
`git status --porcelain` = **0 条**（工作区全净）；连同既有 3 提交（`1d907ef`/`db47cd2`/`ecec92e`）共 10 个提交推送 `origin main`。
