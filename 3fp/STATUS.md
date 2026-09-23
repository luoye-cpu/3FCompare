# 3fp 工作区 · 交接状态

> **给接手的 agent**：先读本文件，再读 `README.md`（铁律），然后按「下一步」的第一个动作开工。
> 更新本文件时请同步更新「最后更新」一行。

- **最后更新**：2026-09-24（交接打包版）
- **当前阶段**：**M1 已闭环 + M2 地基(GetImageInfo)已提交 + 广色域保留(图片模式)已实现**；全部**零实机验证**（样张缺失）
- **内核副本**：`3fp/kernel/`（嵌套 git 仓，`origin`=fork，`upstream`=上游）
  - `feature/image-mode` @ `b765a1f`（3FC 基线，实验用）
  - **`upstream/image-mode` @ `88526b5`** ← **当前工作分支**，基于上游最新 `2acd96d`，共 **8 个提交**
  - 宽色域 2 个提交（`4a5774a`/`88526b5`）**仅在本地，未推 fork 备份**（见末尾打包步骤）
- **fork 已同步**：`luoye-cpu/FFF_Project:master` = `2acd96d`（与上游 0 差异）
- **阻塞中**：是 —— **①样张集为空（最重要）**；②实机验证环境虽有（FFF.Player 可编译），但还没人跑过一次
- **内核侧（C++）**：✅ 可编译（MSVC Release x64 通过）
- **宿主侧（VB.NET）**：✅ 可编译（需 LakeUI 新版，见下）
- **实机验证程序**：`3fp/tools/verify_image_mode.cpp`（已编译出 `3fp/build/verify_image_mode.exe`），见 §七

> ⚠ **宿主构建阻塞（2026-09-24 发现）**
> `3fp/kernel/FFF.Player` 依赖第三方 UI 库 **LakeUI**（`vbproj` 的 HintPath 指向
> `..\..\LakeUI\LakeUI\bin\Debug\net10.0-windows10.0.17763.0\LakeUI.dll`）。
> 本机 `third_party/LakeUI` 是 **2026-09-18 的旧快照**，而**上游最新**的 FFF.Player 用了更新的
> LakeUI 成员（`ExcellentTrackBar.ChapterMarkers` / `AddChapterMarker` / `ClearChapterMarkers` /
> `ChapterMarkerCornerStrength` / `ChapterMarkerEdgeDistance` / `ChapterMarkerToolTipPadding`），
> 构建报 **9 个 BC30456**。
> 已验证：LakeUI **源码里也没有**这些成员 ⇒ **重新编译本地 LakeUI 无效**，必须拿到更新版 LakeUI。
> 处理方式：已在 `3fp/` 建目录联接 `3fp/LakeUI → third_party/LakeUI`（满足 HintPath 相对路径），
> 换上新 DLL 后即可构建。
> ✅ **已解决（2026-09-24）**：`third_party/LakeUI` 就是原作者公开仓库 `Lake1059/LakeUI` 的克隆
> （提交 PR 无需付费授权）。已 fast-forward 到 `826c70f`（2026/9/24-1，v5.108.0）并用
> `dotnet build LakeUI/LakeUI.vbproj -c Debug` 重建 DLL。
> **宿主现已可编译**：`FFF.Player.dll` 构建成功（net10.0-windows10.0.26100.0，0 错误）。
>
> 环境准备备忘（两台机器/新 agent 复用）：
> ```powershell
> # LakeUI（宿主唯一外部依赖；HintPath 指向 Debug 产物，必须构建 Debug 配置）
> git -C third_party/LakeUI pull --ff-only origin master
> dotnet build third_party/LakeUI/LakeUI/LakeUI.vbproj -c Debug
> # 独立工作区的两个联接（满足相对路径 HintPath）
> New-Item -ItemType Junction -Path 3fp\LakeUI        -Target third_party\LakeUI
> New-Item -ItemType Junction -Path 3fp\kernel\third_party -Target third_party\fff_project\third_party
> ```

---

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
| **fork 同步到最新上游** | ✅ | `fork/master` = `2acd96d`（原落后 54 提交） |
| **PR-A：K1+K2+K4 实现** | ✅ 已提交 | `942ab9c`，**已编译通过**（MSVC，EXIT=0） |
| **PR-B：K3 判定改 nb_frames** | ✅ 已提交 | `2465862`，已编译通过 |
| **PR-C 内核：`GetImageInfo`** | ✅ 已提交 | `c0534ce`，已编译通过（字段：帧数/动画/EXIF旋转/格式位深/ICC/alpha） |
| **PR-D 外壳图片模式（M1 骨架）** | ✅ 已提交 | `0e076fe`，6 文件 +263 行，**宿主已编译通过**（0 错误 0 警告） |
| **广色域保留（图片模式）** | ✅ 已实现 | `4a5774a` + `88526b5`（见 §2.5）；primaries 字段 + P3→BT.2020 + 放宽 scRGB 门禁 + 平移钳制修复 |
| **实机验证程序** | ✅ 已写 | `3fp/tools/verify_image_mode.cpp`，已编出 exe（见 §七） |

---

## 二、下一步（按优先级，第一个动作即可执行）

### 🔴 阻塞项 1 · 补齐样张集（M0 的第一件事，其它都依赖它）

`testmedia/media/` 里现有的 10 个 PNG **全是 UI 截图，不能用作图片素材**。
需要按 `samples/README.md` 的 11 项清单生成/收集样张，放到 `3fp/samples/media/`。

- [ ] 1.1 先做能用脚本生成的：sRGB PNG、序列图 `img%03d.png`、多页 TIFF、>16384 大图、16bit PNG / EXR
- [ ] 1.2 再做需要素材的：PQ AVIF、PQ HEIC、HLG JXL、iPhone gain map HEIC
- [ ] 1.3 带 EXIF Orientation=6 的 JPEG

### 🔴 阻塞项 2 · 跑掉 Q1 / Q5 / Q8 / Q9 四个实测

样张齐了以后，这四项**各十分钟**就能定案，但它们决定了规划里的三件事：

| 编号 | 问题 | 决定什么 |
|---|---|---|
| Q1 | 基线打开 PNG 的实际行为（显示？seek 报错？跳 Ended？） | M1 基线对照 |
| Q5 | 各格式解码后 `color_trc` 是否被填充 | **HDR 卖点能否成立**（R7/R9） |
| Q8 | gain map 是否被 ffmpeg 暴露（哪怕第二路流） | GainMap 是否彻底定案为"不支持" |
| Q9 | RGB/float 输入（`rgb24`/`rgb48le`/`gbrpf32`）的色彩与精度 | K7 的改法 |

- [ ] 2.1 Q1：拖 PNG 进基线构建，记录实际行为
- [ ] 2.2 Q5：PQ AVIF / PQ HEIC / HLG JXL 分别 dump `isHdrSource`、输出位深、`ActualColorMode`
- [ ] 2.3 Q8：iPhone HEIC / Ultra HDR JPEG 用 ffprobe 看流数量与 side data
- [ ] 2.4 Q9：观察 swscale 目标格式与 `VideoRenderer.cpp:4304` 位深推导

### 🟡 3 · 提第一个上游 PR（PR-A）

- [x] 3.1 实现 K1（`FlushAtEnd` 对静态图不置 Ended，改置 Paused）
- [x] 3.2 实现 K2（`DoSeek` 静态图短路为 no-op）
- [x] 3.3 实现 K4（大图预检，>16384 拒绝并给出明确文案）
- [x] 3.4 编译通过（MSVC Release x64，`FFF.Native.dll` 产出，EXIT=0）
- [ ] 3.5 **实机自测**：视频模式零回归 + 图片不再报错/跳 Ended ← **当前卡点**
- [ ] 3.6 推送 `git push -u fork upstream/image-mode` 并开 PR

> 选 PR-A 打头的原因：纯行为修正、不改 ABI、对上游也有独立价值，最容易被接受。
>
> **3.5 为什么卡住**：验证需要一个能跑的宿主。3FC 是现成宿主，但它用的是
> `third_party/fff_project` 编出的 DLL；本分支基于上游 master（不含 3FC 的 31 个本地提交），
> 直接替换 3FC 的 DLL 会污染其运行环境。可选路径见下方「待决策」。

> 🚦 **提交纪律（D10/D11）**：最终给上游的 PR **只有一个提交**，且不急于提。
> 现在仍按"一个改动一个提交"在本地推进（便于回溯），最后 `merge --squash` 压成单提交。
> 已推送到 fork 的 `upstream/image-mode` 分支**只是备份**，上游侧没有任何 PR。

### ✅ M1 + M2地基 + 广色域 当前进度（2026-09-24 交接打包）

分支 `upstream/image-mode`，**8 个提交**，内核与宿主**均已编译通过**：

```
88526b5  Player: match the kernel pan scale                      (外壳 pan 对齐)
4a5774a  Native: keep wide gamut instead of clipping it to Rec.709  (图片广色域)
c0534ce  Native: report image details (frame count, EXIF rotation, format, ICC)
a6cab13  Player: build the picture list from its folder and hide the timeline
0e076fe  Player: add a picture mode without touching video behaviour
2465862  Native: decide still images by frame count instead of demuxer name
942ab9c  Native: keep still images interactive instead of treating them as finished playback
2acd96d  2026/9/23-4  （上游最新，fork 已同步）
```

**已可用（代码层，未实机跑）**：打开图片自动进图片模式、滚轮缩放、左键平移、
←/→ 上一张/下一张、同目录建列表、隐藏进度条/播放按钮/时间码、菜单可手动开关、
`GetImageInfo` 查询、广色域 P3 静态图经 zscale 转 BT.2020 后走 scRGB。

### §2.5 广色域保留实现细节（图片模式，已提交 `4a5774a`+`88526b5`）

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
① ffmpeg 是否为静态图填 `color_trc`（决定 HDR 图能否亮）
② P3 图经 zscale 转换后是否仍被 shader 正确按 BT.2020 处理
③ 显示器不支持 scRGB 时应自动回落 SDR（机制已存在，未实测）
④ 验证程序本机跑 HDR 视频**输出为空**（见 §七，程序可能有 bug 或 headless 限制）

### 🟢 4 · M2 待办（按依赖顺序）

- [x] 4.1 **PR-C 内核 API**：`GetImageInfo` 已实现并提交（`c0534ce`）；`OpenWithOptions`/`SetViewRotation` **未做**
- [ ] 4.2 外壳接 `GetImageInfo`：EXIF 自动旋转、动画图默认停首帧（`loop=0`）
- [ ] 4.3 幻灯片（Timer + 设置项）
- [ ] 4.4 设置页 `Form设置_图片.vb`（缩放步进 / 默认适应 / 幻灯片间隔）
- [ ] 4.5 文件关联加图片扩展名（`文件关联管理器.vb`，HKCU 无需管理员）
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

**⚠ 已知问题（交接给下一位）**：本机对 `test_4k_hdr_80M.mp4` 运行时**stdout 为空**
（`EXIT=0`）。可能原因：窗口在无头/后台会话里没真正 present、或程序在 Pump 期间已退出、
或 DLL 加载路径问题。**这个程序本身还需要调试**才能作为验证工具用。
建议下一位先拿一张普通 PNG 跑通（验证基础流程），再跑 HDR/广色域样张。

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
| D4 | GainMap **明确不做** | ffmpeg 无 gain map 侧数据通道（头文件全目录 grep 零命中 + 组件枚举零命中） |
| D5 | 版本 `15→16`、快照 `8→9` **一次性**加完字段 | 内核对 version 严格相等校验，加字段就必须递增，反复递增会撕裂兼容性 |
| D6 | 内核改动**不写入** `third_party/fff_project/` | 会污染 3FC 构建基线（SHA 钉死 + dirty 拒绝 + 发布门禁戳记） |
| D7 | 旋转**新增** `SetViewRotation`，不扩展 `SetViewTransform` | 扩展现有签名会破坏已有调用点；新增可让旧 DLL 返回 `NotSupported` 安全降级 |
| D8 | 输入优先级：光盘 > 360° > 图片 > 视频默认 | 360° **静态全景图**是真实场景，图片模式必须让位 |
| D9 | **PR-C 改为"只新增函数"，不动 `FFF3FPConfiguration` / `FFF3FPSnapshot`、不递增 `PlayerApiVersion`** | 内核对 version 严格相等校验，一旦递增就必须内外同步；而图片模式需要的字段（帧数/动画/EXIF/位深）**都能由新增的 `GetImageInfo` 提供**，没必要动结构体。改为纯新增后：ABI 完全不破坏，旧 DLL 返回 `NotSupported` 自动降级 |
| D10 | **给上游的 PR 最终只有一个提交** | 仓库所有者明确要求（2026-09-24）。偏离 PR #9 的"Native:/Player: 两提交"范式，但好处是**结构上不可能出现"合了内核没合外壳"的半截状态**，且上游日更时 rebase 最省事。本地保留 `upstream/image-mode` 多提交开发分支，最终另建 `upstream/image-mode-pr` 单提交分支。详见 `docs/03-上游仓库与PR策略.zh.md` §6.2–6.5 |
| D12 | **广色域要"保留"，不压缩**（2026-09-24 需求澄清） | 早先的"P3 → BT.709"是压缩，超出 709 的颜色会被裁掉。正确做法：P3 → **BT.2020**（无损载体，P3 ⊂ 2020）→ shader 按 `source2020` 处理 → **scRGB FP16 输出**（FP16 允许超范围值，才能装下广色域）。SDR 管道物理上放不下 P3，所以必须走 HDR/scRGB。详见 `docs/00-图片模式设计规格.zh.md` §9 |
| D11 | **不急于开 PR** | 功能做完 + 实机验证后再提。当前 6 个提交已推送到 fork 的 `upstream/image-mode`（**仅作备份，不是 PR**），上游侧看不到任何 PR |

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

---

## 五、接手检查清单

- [ ] 读了 `README.md` 的三条铁律
- [ ] 确认 `git -C 3fp/kernel log --oneline -1` 输出 `88526b5`（8 个提交，含广色域）
- [ ] 确认 `git -C 3fp/kernel status --porcelain` 为空（宽色域改动已提交，未推 fork 备份）
- [ ] 确认没有在 `third_party/fff_project/` 里留下任何改动（`git -C third_party/fff_project status --porcelain`）
- [ ] 确认两个 junction 在：`3fp/LakeUI` → `third_party/LakeUI`、`3fp/kernel/third_party` → `third_party/fff_project/third_party`
- [ ] 读了 `docs/00-图片模式设计规格.zh.md` 的模式隔离原则
- [ ] 读了 §2.5（广色域实现细节）与 §七（验证程序）
- [ ] 开工前先在下方「工作日志」追加一行

---

## 六、工作日志

| 日期 | 变更 |
|---|---|
| 2026-09-22 | 完成可行性分析；与上游沟通后复审，推翻广色域/HDR/GainMap 三处判断 |
| 2026-09-23 | 用 `tools/探测FFmpeg能力.ps1` 实测 FFmpeg 能力面；发现 AVIF/HEIC 走 `mov` 不被判为静态图（R12） |
| 2026-09-24 | 建立独立工作区 `3fp/`；克隆内核副本至 `feature/image-mode`；完成模式隔离设计规格与实现规划 |
| 2026-09-24 | 分析 PR 仓库 `luoye-cpu/FFF_Project`：PR #9 已合并、上游无 open PR、无图片相关改动；**fork/master 落后上游 54 提交**。详见 `docs/03-上游仓库与PR策略.zh.md` |
| 2026-09-24 | fork 快进到 `2acd96d`；本地副本改为直接跟踪 fork（`origin`=fork，`upstream`=上游，`bundle`=离线兜底） |
| 2026-09-24 | 解决 LakeUI 阻塞（拉取 `Lake1059/LakeUI` 至 `826c70f` 并重建）⇒ **宿主可编译** |
| 2026-09-24 | 基于最新上游实现并提交 4 个提交：`942ab9c`(K1K2K4) `2465862`(K3) `0e076fe`(外壳图片模式) |
| 2026-09-24 | `c0534ce` 内核 `GetImageInfo`（帧数/动画/EXIF旋转/格式位深/ICC/alpha）；`4a5774a`+`88526b5` 图片模式广色域保留（primaries 字段 + P3→BT.2020 zscale + 放宽 scRGB 门禁 + 平移钳制修复 + 外壳 pan 对齐） |
| 2026-09-24 | 写 `tools/verify_image_mode.cpp` 实机验证程序并编译出 exe（⚠ 跑 HDR 视频时 stdout 为空，需调试）；用户要求**停止实现、打包进度交接** |
