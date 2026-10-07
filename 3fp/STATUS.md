# 3fp 工作区 · 交接状态

> **给接手的 agent**：先读本文件，再读 `README.md`（铁律），然后按「下一步」的第一个动作开工。
> 更新本文件时请同步更新「最后更新」一行。

- **最后更新**：2026-09-30（本文件重构为**短状态页**：原头部变更叙事与原「六、工作日志」
  整段已迁入 [docs/archive/status-log-2026-09.md](docs/archive/status-log-2026-09.md)，按日期检索。）

## 当前快照（2026-09-30）

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

## 二、下一步（按优先级，第一个动作即可执行）

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
