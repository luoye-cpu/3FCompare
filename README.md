# 3FCompare – ICAT-like video frame-by-frame comparison tool

> English | [简体中文](#chinese)
>
> Compare multiple encoded video streams side-by-side, frame by frame.
> Powered by the **3FP player kernel** from [FFF_Project](https://github.com/Lake1059/FFF_Project).

---

## <span id="chinese">3FCompare（项目代号：ICAT-Like 视频盯帧/画质对比软件）</span>

> 目标：做一款与 NVIDIA ICAT 同类的视频盯帧（逐帧对比）桌面软件，
> 播放/解码后端复用 FFF 帝国（[FFF_Project](https://github.com/Lake1059/FFF_Project)）的 **3FP 播放器内核**。

3FCompare 面向视频编码评测（VCB-Studio 等圈子）的场景：把多个编码版本的视频按帧对齐，
提供**两种视图模式（网格对比：一路一格、分割线可拖 / 左右拉动揭示，视图菜单顶部一列互斥切换）**、
**单屏切换 / 双步进（帧&秒）/ 像素探针 / 放大镜**等贴合“盯帧”工作流的操作，
支持**硬件编解码开关与多显卡解码指定**与**窗口/全屏双模式**，
（**多显卡指定 2026-09-16 真正接线生效**：此前该选项只保存不生效——内核配置无 adapter 字段；
现新增内核扩展 `preferredAdapterIndex`（PlayerApiVersion 14→15），索引与 DXGI `EnumAdapters1` 一致，
越界自动回落默认策略。详见 `docs/06` §3 A11）
并原生支持 **Windows Advanced Color（广色域 / ACM）** 与 **G-SYNC / FreeSync（VRR）** 显示链路。

---

## 📌 产品定位 / Product Positioning

| 维度 / Dimension | 说明 / Description |
| --- | --- |
| 产品形态 / Type | Windows 桌面应用（**Avalonia 12**，.NET 11）；**NativeAOT 自包含（精简版 7z 分发约 9.3MB）** / Windows desktop app (**Avalonia 12**, .NET 11); **NativeAOT self-contained (lite 7z ~9.3MB)** |
| 对标产品 / Reference | NVIDIA ICAT（最多 4 路视频/图像对比）——本项目**扩展至 1~9 路**，对齐、双步进、硬件解码开关、窗口/全屏、多显卡解码 / NVIDIA ICAT (up to 4-way) — **extended to 1–9 ways** with alignment, dual stepping, HW decode toggle, window/fullscreen, multi-GPU |
| 后端 / Backend | FFF_Project 的 **3FP**（`FFF.Native` fork + 自研补丁，见 docs/03-后端接入与能力映射.zh.md） / **3FP** from FFF_Project (forked `FFF.Native` + custom patches, see docs/03-后端接入与能力映射.en.md) |
| 业务规模 / Scale | **1~9 路对比**（3x3 网格上限），架构按 N 路扩展 / **1–9 way comparison** (3×3 grid max), architecture scales to N-way |
| 解码 / Decode | 3FP 原生能力：CPU（FFmpeg）/ GPU（CUDA/NVDEC、D3D11VA 优先）+ 自动回退；**硬件开关 + 多 GPU 指定（2026-09-16 起生效）** / 3FP native: CPU (FFmpeg) / GPU (CUDA/NVDEC, D3D11VA preferred) + auto fallback; **HW toggle + multi-GPU selection** |


| [docs/PACKAGING_SPEC.md](docs/PACKAGING_SPEC.md) | 打包规范：NativeAOT 双版本发布流程、命名规则、压缩配置 |

## 🌈 显示链路（ACM / VRR）要点 / Display Chain Highlights

- **ACM/广色域**：完全遵循 3FP 的 Advanced Color 交换链契约（SDR `BGRA8/RGB10A2`、HDR `R10G10B10A2+PQ/BT.2020`），
  显示侧校色交给 DWM；本项目自行探测显示能力（DXGI 亮度读取）并计算智能色调映射参数，探针/截屏始终读取「颜色管理前」的原生缓冲，保证跨路对比一致。
- **G-SYNC / FreeSync**：播放窗口为独立窗口，**不破坏桌面 VRR**。
  ~~是否全时刻生效取决于 3FP 交换链（Present 节奏 / `ALLOW_TEARING`），待专项实测~~
  → **⚠ 2026-10-03 更正**：这里原来写的两条"已实测支持"，一条是**假绿形状**、一条**根本没那个 API**。
  ① `SetPresentConfig` 存在，但它返回 true 只说明**交换链接受了撕裂请求**，不说明每次 Present 真没等
  （旧 selftest 那句"显示器链支持 ✓"就只是打这个返回值）。现在自测改成量内核的两个计数：
  `PresentWait100ns / SwapChainPresents` ⇒ 单次 Present 平均阻塞；本机 2026-10-03 实测
  **19 次 Present 平均 1.06 ms**（VSync 锁定应是 ≈ 一个刷新周期），样本不足时明写"未验证"不打火。
  ② `SetPacingConfig`（媒体率节奏）**内核未导出、托管也没有对应属性**
  （`AppSettings.cs:203-205`、`IPlayerEngine.cs:53` 的注释都记着"从未实现"）⇒ 之前那行"已启用 ✓"是错的，已删。
  是否彻底生效仍需在带 VRR 的显示器上复测。

- **2026-09-30 复核（上游 PR 树 `3fp/kernel`，内核 API 16）**〔行号为当时读数；**2026-10-08 复算**：实验树 `3fp/build/FFF.Native.dll` 现为 **API 19 / 96 导出**，出货树 `third_party/fff_project` 为 **API 17 / 90 导出**，在包件 `publish/build/3FCompare-v0.3.2-beta-x64-full/FFF.Native.dll` 仍是 **API 16 / 87 导出** ⇒ 三棵树不是一个版本号〕：该树的 `FFF3FP_SetPresentConfig` 是本轮新建入口，
  第一版把「交换链是否带 `ALLOW_TEARING`」与「用户是否要撕裂」合用一个变量 ⇒ 关它会连带改掉 `ResizeBuffers` 的
  flag、开启时正常播放分支仍走 `Present(1, 0)`（等于 no-op）、设备重建后偏好丢失。已拆成
  `swapAllowTearing_`（能力）+ `tearingRequested_`（偏好）并在正常呈现分支生效。
  ⇒ 上面 09-16 那两行读数来自**另一棵树**（`third_party` 的自研 VRR 补丁），不能迁移成「这条也已验过」；
  真实撕裂节奏仍需在带 VRR 的显示器上复测。
  The 09-16 readings come from a different tree (the `third_party` fork's own VRR patch); on the API 16
  upstream-PR tree the toggle was inert until 2026-09-30, and real tearing cadence is still unmeasured.
- 专项验收清单见 [docs/01-需求分析.zh.md §5.1](docs/01-需求分析.zh.md)。

## ⚖️ 依赖与许可提示 / License & Dependencies

- `FFF.Native` 上游**自述**为 MIT，但**本仓内无可读回的凭据**（其 `LICENSE.txt:3` 是未填写的模板
  `Copyright (c) [year] [fullname]`），故此处按实测登记为 **许可未经确认**；内核 251 个被跟踪文件里
  212 个（84.5%）与上游根提交逐字节相同，归属见 `THIRD-PARTY-NOTICES.md` §7。本项目**基于其源码二次开发**：内核以**独立 git 仓库**形式位于
  `third_party/fff_project/`——**不是 submodule**（`.gitmodules` 已无条目，整目录被 `.gitignore` 忽略），
  基线由 `tools/构建全部.ps1` 的 SHA/tag 钉死。在其上追加自研扩展补丁（VRR 交换链 / 视口子区域 /
  全帧回读等，见 `docs/03 §6`），保持与上游可合并。
- FFmpeg 公共 API：Shared FFmpeg DLL 组（`avcodec` 等）由 BtbN 构建，**不纳入本仓库**，仅在发布说明中指引获取。
- 本项目的 UI、同步逻辑、对比工具均为独立实现；本仓库的 git 集合里**不含第三方 DLL/EXE**（按 `git ls-tree -r HEAD` 的扩展名口径实测为 0）。
  ⚠ **口径限定**：仓根 `.3fc_kernel_baseline.bundle`（实测 2 371 867 B，在 `HEAD` 与 `origin/main` 的树里）是**随仓分发的二进制 git 包**，内含整套内核仓库历史（含 `refs/stash`）；`3fp/patches/history/…/ShaderBytecode.h`（1 157 815 B）内嵌自产 DXBC 字节码。"零二进制入仓"这句话只对源码集合成立，别按磁盘口径读。逐件第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

> 详细依赖清单、构建步骤与风险见 [docs/06-风险与依赖.zh.md](docs/06-风险与依赖.zh.md)。

## 🛠 工程状态（v0.3.2-beta，2026-09-30）

> ⚠ 本节标题此前长期停留在 `0.2.0-BETA，2026-08-25`，与实际版本严重脱节，现已更正。
> 版本唯一真源是 csproj 的 `<Version>`，改版本时记得同步此处。

> **版本号唯一真源**：`src/3FCompare/3FCompare.csproj:23` 的 `<Version>`（预发布后缀直接写进
> `<Version>`，如 `0.3.2-beta`；独立的 `<VersionSuffix>` 已移除，见 docs/45 P1-12）。
> `pack.ps1` 与 `tools/发布门禁.ps1` 不硬编码版本——不传 `-Version` 时自动从 csproj 派生，
> 传入不一致会告警。详见 [PACKAGING_SPEC.md §5](docs/PACKAGING_SPEC.md)。
> ⚠ **两条 2026-10-08 实测边界，别把这段读成"版本已经收口"**：
> ① 真源不是"集中式"的——本仓**不存在** `Directory.Build.props`（`ls` 零命中），
> `src/3FCompare.Core/3FCompare.Core.csproj:15` 是**手抄的同值副本**，
> **没有任何门禁校验两份相等**（`.github/workflows/` 目录不存在，`tools/发布门禁.ps1` 只读 App 侧那一份）；
> ② **版本 ≠ tag**：csproj 已是 `0.3.2-beta` 且 10-07 已用该号出包，但 `git describe --tags --abbrev=0`
> 仍是 `v0.3.1-beta` ⇒ "版本号、tag、csproj 三者同步"这条本文件开头的纪律**当前没成立**，缺的是 tag。

```text
src/
├── 3FCompare.slnx              # 解决方案（.NET 11 新格式；**在 src/ 下**，不在仓库根）
├── 3FCompare.Core/             # 后端抽象 / 3FP P/Invoke / 演示引擎 / 同步 / 设置 / GPU 枚举 / DXGI 显示器能力
├── 3FCompare/                  # Avalonia 主程序（多路网格 / 双步进 / 时间轴 / 设置 / 全屏 / 对比工具 / 主题；
│                               #   2026-08-22 由 WinForms 迁移而来，WinForms 版归档于 tag `winforms-final`）
tests/
├── 3FCompare.Core.Tests/       # 单元测试（FrameTimeline / SyncController / GridLayout / ToneMapping / PixelReadback / RenderStallWatchdog 等）
├── 3FCompare.Platform.Tests/   # 平台层单元测试（视图变换 / 放大镜 / 播放编排 / 抓帧路由 / 崩溃守卫 / 本地化键）
├── 3FCompare.SmokeTests/       # E3 冒烟（控制台，**始终走演示引擎**，合成画面全流程验证）
third_party/
└── fff_project/                # 3FP 内核独立 git 仓库（许可未经确认，见 THIRD-PARTY-NOTICES.md §7；**非 submodule**，被 .gitignore 忽略）
    └── FFF.Native → x64/Release/FFF.Native.dll   # 已构建（Release x64）
    └── runtime/                 # Shared FFmpeg DLL 组（BtbN，已准备）
    └── third_party/vcpkg_installed/  # libass（vcpkg，已准备）
```

> 测试数量**不在此处写死**（会随每次提交漂移，写死必然过期）；以 `dotnet test` 的实际输出为准。
> 另：`3fp/` 是「图片模式」独立工作区（上游 3FP 的 PR 工作树），**不由 3FC 的 csproj 引用**，
> 与 `third_party/fff_project/` 互不干扰，详见 `3fp/README.md`。

**里程碑状态 / Milestone**: ✅ 真实 3FP 内核全链路已验证（FFmpeg 解码 → D3D 渲染 → App 显示） / Real 3FP kernel pipeline verified (FFmpeg decode → D3D render → App display).
✅ **NativeAOT 已启用 / Enabled**: `dotnet publish -c Release -r win-x64` → 原生单文件 `3FCompare.exe`
（含 Skia/ANGLE 原生栈约 21MB；完整版内嵌 FFF.Native），
真实视频渲染 + `--selftest` / `--screentest` 均验证通过；精简版 7z 分发 9.3MB。
迁移纪要见 [docs/07-Avalonia迁移规划.zh.md](docs/07-Avalonia迁移规划.zh.md)。
`tools/构建全部.ps1` 一键复现内核构建与 DLL 部署。


### 已实现功能 / Implemented Features

- **多路对比 1~9 路**（2x2/3x2/3x3 自动网格，点击选中，单屏/多屏切换，数字键 1-9 加路） / **1–9 way comparison** (2×2/3×2/3×3 automatic grid, click selection, single/multi view toggle, number keys 1-9)
- **双步进**：按帧（默认 `A`/`D`）与按秒（默认 `←`/`→`）两组前进/后退，**步长与键位都能在设置里改**（设置 → 快捷键，逐项捕获 / 清除 / 恢复默认） / **Dual stepping**: frame-stepping (default `A`/`D`) and second-stepping (default `←`/`→`), both the step size and the key are editable in Settings → Shortcuts
- **同步播放/暂停/停止/Seek/循环**：以第 0 路为 master 的媒体时间同步（SyncController）/ **Sync play/pause/stop/seek/loop**: SyncController with slot 0 as master
- **二级设置窗口**：硬件解码开关、GPU 选择（多显卡，按 DXGI 适配器列出，2026-09-16 起生效）、步进步长、色彩模式、默认布局、窗口/全屏行为（F25/F26） / **Settings dialog**: HW decode toggle, multi-GPU selection (DXGI adapters, effective since 2026-09-16), step sizes, color mode, layout, window/fullscreen behavior (F25/F26)
- **全屏模式**（F11）+ 窗口模式，全屏可隐藏工具栏/时间轴 / **Fullscreen mode** (F11) + window mode, hide chrome in fullscreen
- **会话保存/加载**（`.3fcs` JSON：文件列表/偏移/布局/位置/循环区间） / **Session save/load** (`.3fcs` JSON: file list, offsets, layout, position, loop range)
- **快捷键** / **Keyboard shortcuts**: `空格` 播放/暂停 · `A`/`D` 逐帧 · `←`/`→` 逐秒 · `↑`/`↓` ±10 秒 · `G`/`S` 视图模式两态（`V` 与 `G` 同态）· `C` 换分格朝向 · `F11` 全屏 · `R` 重置视图 · `O` 打开 · `P` 探针 · `Ctrl+H` 侧栏三态 · `Esc` 退出全屏 / Space play/pause, A/D frame step, ←/→ second step, ↑/↓ 10s step, G/S view modes (V aliases G), C cycles the split orientation, F11 fullscreen, R reset view, O open, P probe, Ctrl+H sidebar tri-state, Esc exit fullscreen（六项传输键全部可在设置里改 / all six transport keys rebindable in Settings）
- **对比工具** / **Comparison tools**: 像素探针 pixel probe (F19)、放大镜 magnifier (F17)、书签 bookmarks (F22)、截图导出 screenshot export PNG (F21)、差异叠加 diff overlay heatmap (F20)、媒体信息 media info (F3)、音频面板 audio panel、时间轴 A/B 打点 timeline A/B markers (A/B keys)
- **同步视图变换**：鼠标滚轮**缩放**（1~32×，以光标为锚点：光标下那个视频像素钉在光标下）+ 拖拽**平移**（画面 1:1 跟手，与倍率无关；多路同步），R 键重置。注意 ×1 = "恰好铺满窗口"的适配框，**不等于屏幕:视频 1:1**，真比值看状态栏（0.1.1 新增；2026-09-29 修正跟手增益与光标锚点） / **Sync view transform**: mouse wheel **zoom** (1–32×, cursor-anchored — the video pixel under the pointer stays under it) + drag **pan** (picture tracks the pointer 1:1 at any zoom; multi-way sync), R reset. Note ×1 is the fit box ("fills the window"), not 1:1 screen:video — the real ratio is in the status bar (added 0.1.1; tracking gain and cursor anchor corrected 2026-09-29)
- **网格布局预设菜单**：视图 → 网格布局一键切换 2×1 / 2×2 / 3×3 预设或自动布局（0.1.2 新增） / **Grid layout presets**: View → Grid Layout 2×1/2×2/3×3 or auto (0.1.2)
- **可停靠工具侧栏**：右侧 Dock 标签页（探针 / 书签 / 偏移 / 媒体 / 音频）+ Pin 置顶固定（0.1.2 新增） / **Dockable tool sidebar**: right dock tabs (probe/bookmarks/offset/media/audio) + Pin (0.1.2)
- **时间轴拖动缩略图预览**：拖动进度条时悬浮显示当前帧缩略图（节流 >10ms），松手快速定位关键帧（0.1.2 新增） / **Timeline scrub preview**: thumbnail popup on drag, throttle >10ms (0.1.2)
- **自适应轮询**：播放中 16ms 精跟、空闲 250ms 省电，动态切换（0.1.2 新增） / **Adaptive polling**: 16ms during playback, 250ms idle (0.1.2)
- **高 DPI 支持**：PerMonitorV2 多显示器自适应缩放，4K 250% 缩放下控件不挤压/不重叠/不溢出（Dpi 工具类 + 全控件树 AutoScale，0.1.2 新增） / **High-DPI support**: PerMonitorV2, Dpi utility class, full control tree AutoScale (0.1.2)
- **深色主题系统**：统一 AppTheme + 布局常量，设置窗口重构为分页标签布局（0.1.1 新增） / **Dark theme system**: unified AppTheme + LayoutConstants, tabbed settings (0.1.1)
- **智能色调映射**：基于真实显示器能力（DXGI 亮度读取）与源内容 HDR 状态，自动计算 BT.2390 映射参数，替代固定 100 nits（0.1.1 新增） / **Smart tone mapping**: DXGI-based display luminance detection, BT.2390 auto calculation (0.1.1)
- **引擎自动探测**：同时存在 `FFF.Native.dll` 与 FFmpeg 核心 DLL（`avcodec-*.dll`）在程序目录时用真实 3FP 内核，否则回退**演示模式**（合成画面）；`--selftest` 两模式均通过 / **Engine auto-detection**: real 3FP kernel when both `FFF.Native.dll` and FFmpeg DLLs present, otherwise fallback to **demo mode** (synthetic frames); `--selftest` passes both modes
- **真实内核已验证**：FFmpeg + libass + FFF.Native 全链路构建成功，App 真实渲染视频确认 / **Real kernel verified**: FFmpeg + libass + FFF.Native pipeline built, real video rendering confirmed.
- **拖拽平移稳定性修复**：滚轮缩放后按住拖动跨画面边界不中断（鼠标捕获 + 不再由 MouseLeave 提前结束拖拽），多路同步平移连贯（0.1.4 新增） / **Drag-pan stability fix**: cross-surface drag without interruption via mouse capture, continuous multi-way sync pan (0.1.4)
- **双语界面**：完整中英双语，启动应用已保存语言、语言切换即时刷新全部界面（菜单/工具栏/面板/状态栏/消息框/文件过滤器），此前英文模式仅设置对话框生效、主界面残留全中文（0.1.4 完善） / **Bilingual UI**: full zh/en support, applies saved language on startup and refreshes the entire UI on switch (menus/toolbars/panels/status/message dialogs/file filters); previously only the settings dialog honored English (0.1.4)
- **播放期漂移检测与周期校正**：各路时钟必然发散，以 master 为基准，偏差超半帧才校正、1s 冷却防抖（0.2.5 新增） / **Playback drift detection & periodic correction**: master-referenced, corrects only beyond half a frame with 1s cooldown (0.2.5)
- **帧步进先全路暂停**：避免 master 被内核置 Paused 而从路继续播导致的错帧（0.2.5） / **Frame-step pauses all routes first**: prevents master stalling while followers keep playing (0.2.5)
- **像素回读坐标域修正**：内核 `ReadVideoPixel` 的坐标域是后台缓冲而非片源分辨率，展示仍用片源坐标、仅读取走换算（0.2.5） / **Pixel readback coordinate-domain fix**: kernel reads in backbuffer space; source coords kept for display, converted only for reads (0.2.5)
- **滚轮缩放最终值不再被节流吞掉**：16ms 节流原为直接丢弃，现改为挂一次性 UI 线程补发（0.2.5） / **Wheel zoom final value no longer swallowed by throttle**: throttled updates are now re-flushed once on the UI thread (0.2.5)
- **配置原子写 + 会话/FFmpeg 路径校验**：写失败不再损坏配置；拒绝相对路径与 UNC（后者会外泄 NTLM 凭据）（0.2.5） / **Atomic config writes + path validation**: no more corrupted settings; rejects relative and UNC paths (the latter leaks NTLM credentials) (0.2.5)
- **内核基线 SHA 绑定**：`构建全部.ps1` 钉死内核 SHA，并把实况（HEAD / 是否 dirty / DLL 哈希）写进
  `.3fc_kernel_build.json`；**脏树仍会照常构建**（不会拦你），由 `tools/发布门禁.ps1` 据此判红、阻止出包
  （0.2.0 新增） / **Kernel baseline SHA pinning**: the build script pins the kernel SHA and records the actual
  state (HEAD / dirty / DLL hash) into `.3fc_kernel_build.json`; a **dirty tree still builds** — it is the
  release gate that turns it red and blocks packaging (0.2.0)

### 🧭 对比呈现的两族：传统模式 与 现代模式 / Two families of comparison: classic vs. seamless

两族的**语义区别**在"每格显示什么"，不在格子数量：**传统模式**把每一路**完整**显示在它自己的格子里（各自适应各自
的格子）；**现代模式**把所有路**同步放大到同一段源画面**，再从这段共同区域里各裁一格出来比对（0.3.0-beta 新增）。
The distinction is **what each cell shows**, not how many cells there are: **classic modes** fit each route
**whole** into its own cell; **seamless modes** zoom all routes **together onto the same source region** and then
crop one cell out of that shared region for side-by-side inspection (new in 0.3.0-beta).

- **传统模式 / Classic modes**（v0.3.1 合并后由 `GridPartition` 表达：**列数 × 行数**的网格 + 每条内部线的可拖权重，
  形状候选 2×1 / 1×2 / 3×1 / 1×3 / 2×2 / 4×1 / 1×4 / 3×2 / 2×3 / 3×3；另有视图 → 网格布局的 2×1 / 2×2 / 3×3 / 自动 预设。
  旧 `CompareMode`/`CompareLayout`/`SplitParams`（五个固定形状那套）已随合并**整模块删除**，
  连同 `CompareLayoutTests`/`ModeVariantTests`/`ModeSystemTests` 三份单测；其中 4 条测的其实是活代码
  `GridLayout`，已迁进 `GridLayoutTests`，其余不变量（无缝铺满、格数≡格表长度、手柄互不重合、
  首项＝自动布局、容量 ≥ 路数、5% 下界、末格逐位贴边）在 `GridPartitionTests` 里有等价或更强的对应条）
  / **Classic modes** (after the v0.3.1 merge these are expressed by `GridPartition`: a **cols × rows** grid whose interior
  lines each carry a draggable weight — candidates 2×1 / 1×2 / 3×1 / 1×3 / 2×2 / 4×1 / 1×4 / 3×2 / 2×3 / 3×3, plus
  View → Grid Layout presets 2×1 / 2×2 / 3×3 / auto). The old `CompareMode` / `CompareLayout` / `SplitParams` trio —
  the five fixed shapes — has now been **deleted as a module**, together with `CompareLayoutTests`, `ModeVariantTests`
  and `ModeSystemTests`; four of those cases actually tested live code (`GridLayout`) and moved into `GridLayoutTests`,
  and the remaining invariants (seamless tiling, cell count ≡ cell table, non-coincident handles, first candidate =
  auto layout, capacity ≥ routes, the 5% floor, last cell exactly flush with the container) have equivalents —
  or stronger forms — in `GridPartitionTests`)
- **现代模式 / Seamless modes**：滚轮**以光标为锚点**同步缩放（1× 起，缩小到底退出）、放大态可拖动平移且
  **松手时只提交一次**裁剪，避免逐帧 `SetWindowRgn` / **Wheel zoom anchored at the cursor** (from 1×; zooming back
  out to 1× exits), drag-to-pan while zoomed with the crop **committed once on release** instead of per frame
- **视图模式：菜单顶部一列，两态互斥 / View modes: one mutually exclusive column at the top of the View menu**
  入口 **视图 → 视图模式 → 网格对比（分割线可拖）/ 左右拉动对比**，右侧直接标着各自的快捷键；
  两项做成单选（圆点）而不是复选，勾在哪一项就是当前处于哪一 mode；**都没勾 = 均匀网格**（未进入对比）。
  - **网格对比（`G` 开关 / `V` 进入）**：合并了旧的「标准网格」与「A/B 可拖动分格」——基准取**网格的渲染覆盖**
    （一路一格，2~9 路**全部显示、全部启用**，5~9 路不再被挤到格表之外），再挂上 A/B 的**可拖分割线**：
    内部竖线 `列数-1` 条 + 横线 `行数-1` 条（3×3 即 4 条），每条**整条可抓**。拖动只改这条线**两侧**那两格的大小，
    其余线一格都不位移（权重模型，见 `Core/Display/GridPartition`）；`C` 键换分格朝向（2 路左右↔上下、
    3 路三列↔三行↔2×2、4 路 2×2↔四列↔四行、5~6 路 3×2↔2×3）。形状仍由「视图 → 网格布局」预设与路数共同解析，
    换预设会改形状并把列宽退回均匀。
  - **合并撤掉了什么**：旧 `CompareMode` 的五种固定形状里，AB 左右 / AB 上下 / ABC 三列 / ABCD 十字都变成了网格的
    朝向候选；只有 **ABC「左大图 + 右侧上下两小图」**是 T 形交界的非整线形状，整线网格表达不了，随合并取消。
  - **左右拉动对比（`S`）**：**只对比两路**（≥3 路时取前两路，状态栏写明"显示前 2 / 共 N 路"，其余路在此模式下隐藏）：两路**都铺满同一区域**，各由 `SetWindowRgn` 裁成互补且不重叠的两半，于是**左右拖动分割线 = 在两路之间揭示 / 擦除**（对标 NVIDIA ICAT Single Screen）。**整条分割线从上到下都能抓**（不必瞄准线中间的圆点），光标移到线上即变成左右箭头。键是开关语义（再按一次回到网格对比），菜单项是"进入"语义。此模式下无缝放大与左右互换被禁（三者都要改子窗口矩形或区域，语义互斥）。
  - **没占格的路是"不启用"，不是"藏起来"**：当前视图放不下的那几路（合并后只有**左右拉动**会出现这种路数>格数的形态）会被 Pause 并从播放 / 步进 / Seek /
    漂移校正里整体移出（实测 5 路用 A/B 可拖动：未占格的第 5 路 1.2 秒 presented **+0**，改动前是 **+41**，
    与看得见的四路同速白烧解码与呈现线程）；重新占格时先 Seek 回规范时间、再按"要播放"的意图恢复，
    所以切回去不会停在旧帧。第 0 路例外，恒启用——它是规范时间轴的基准
    / routes that don't fit the current view are **deactivated, not merely hidden**: they get paused and dropped from
    play / step / seek / drift-correction (measured with 5 routes on A/B split: the off-screen route presented
    **+0** frames in 1.2 s, versus **+41** before the fix — the same rate as the visible ones); when a route comes back
    it is seeked to the master position first, so it never shows a stale frame. Route 0 is always active — it *is* the timebase
  - 勾选状态在菜单展开时按真实状态刷新：`G`/`V`/`S`/`C` 这些快捷键改过模式后，菜单里不会留下陈旧圆点
  / **View → View mode → Grid compare (draggable dividers) / Left-right wipe**, each labelled with its shortcut and rendered as a radio column (the bullet sits on the mode actually in effect; **no bullet = the plain uniform grid**). **Grid compare (`G` toggles, `V` enters)** merges the old *Standard grid* and *A/B draggable split*: the base is the grid's rendering coverage — one cell per route, **all 1–9 routes visible and active** (routes 5–9 are no longer pushed outside the cell table) — with A/B's draggable dividers on top (`cols-1` vertical + `rows-1` horizontal interior lines, 3×3 ⇒ 4 lines, **each grabbable along its whole length**). A drag resizes only the two cells on either side of that line; every other line stays put (weights model, see `Core/Display/GridPartition`). `C` cycles the orientation (2-way side-by-side ↔ stacked, 3-way columns ↔ rows ↔ 2×2, 4-way 2×2 ↔ four columns/rows, 5–6 way 3×2 ↔ 2×3); the shape still resolves from View → Grid layout presets against the route count, and changing the preset evens the widths back out. What the merge drops: AB / AB stacked / ABC three-column / ABCD quadrant all became grid orientations, but **ABC "big left + two stacked right"** is a T-junction shape no full-line grid can express, so it goes away. **Left-right wipe (`S`)** = exactly **two** routes (the first two when more are open, the rest hidden) covering the same area, `SetWindowRgn` trimming each to a complementary, non-overlapping half, so dragging the line wipes between them (NVIDIA ICAT's Single Screen) — **the whole line is grabbable top to bottom**, no need to aim for the grip dot, and the cursor turns into a resize arrow over it; the key toggles while the menu item enters, and seamless zoom / swap are disabled there by design. Checkmarks are re-read from real state on menu open, so keyboard-driven switches never leave a stale bullet
- **左右互换（AB）/ 轮换各格画面 / Swap left-right (AB) / Rotate sources across cells**：只换"格 ↔ 路"的映射，
  **不改变格数**；排版、命中测试、裁剪下发、放大源区间四处共用同一份映射，轮换按**路号**循环
  4 路用 AB 也能轮到第 3、4 路 / These remap cell↔route without changing the cell count; layout, hit-testing,
  crop dispatch and magnify sources all share one mapping, and rotation cycles by **route index** so 4 routes on a
  2-cell AB layout can still reach routes 3 and 4
- **分辨率对齐模式 / Resolution alignment**：归一化与像素级两态，决定"各路露出源画面的哪一块"，
  与上面两族正交 / normalized vs. pixel-aligned, deciding which source region each route exposes; orthogonal to both families
- **打开后自动进入对比模式 / Enter compare mode automatically after opening**：偏好项 `AutoEnterCompare`，
  **默认关闭**，挂在"全部打开完成"回调上（此刻路数才准确），且不会覆盖用户已拖好的分割位置
  / setting `AutoEnterCompare`, **off by default**, applied on the all-opened callback (when the route count is finally
  correct) and it preserves a split position the user already dragged
- **已撤下的两个重复入口 / Two duplicated entries retired**：「A-B 滑块视图」（含裸 `B` 键与 `AbSliderView`）
  只画两块渐变占位、真机模式下会把真实画面整块遮住，与上面的网格对比 / 左右拉动是同一概念的假版本；
  「显示 对比网格」的实际动作是折叠侧栏，与「视图 → 侧栏」三态重复
  / **A-B Slider View** (with its bare `B` key and `AbSliderView`) painted two gradient placeholders and covered the real picture in real mode — a fake twin of the two modes above; **Show Comparison Grid** actually collapsed the sidebar, duplicating View → Sidebar's tri-state
- **双语 / Bilingual**：上述入口的中文与英文串都在 `Localization/LanguageManager.cs` 的两张表里
  （`Menu_ViewMode`、`Menu_Mode_Standard|Wipe`、`Menu_CompareOptions`、`Menu_Mode_Swap|Rotate`、`Menu_AutoCompare`），切换语言即时生效
  / every entry above has both zh and en strings in the two tables of `Localization/LanguageManager.cs`
  (`Menu_ViewMode`, `Menu_Mode_Standard|Wipe`, `Menu_CompareOptions`, `Menu_Mode_Swap|Rotate`, `Menu_AutoCompare`); switching language applies live
- **怎么试 / How to try**：打开 2 个以上文件 → **视图 → 视图模式 → 网格对比（分割线可拖）**（或按 `G`，`V` 是同义入口）
  → 拖任意一条分割线换相邻两格宽窄、按 `C` 换朝向、在画面上滚动滚轮放大、按住拖动平移、
  菜单 **对比选项 → 左右互换（AB）** 换源 → 再切 **左右拉动对比**（或按 `S`）左右拉动揭示 →
  再按 `S` 回到网格、滚回 1× 退出放大、再按 `G` 回到无分割线的均匀网格
  / open 2+ files → **View → View mode → Grid compare (draggable dividers)** (or press `G`; `V` is an alias) →
  drag any divider to resize the two cells it separates, press `C` to cycle the orientation, wheel-zoom on the picture,
  drag to pan, **Compare options → Swap left/right (AB)** to exchange sources → press `S` for the **left-right wipe** →
  `S` again returns to the grid, wheel back to 1× exits the zoom, and `G` again drops to the plain divider-free grid

### 🚀 快速开始（仅需下载包） / Quick Start (prebuilt package)

> 面向只想试用、不打算从源码构建的用户。 / For users who just want to try it, without building from source.

**1. 下载与解压** / **Download & extract**

从 Releases 下载 `3FCompare-v<版本>-x64-full.7z`（自包含，推荐）或 `3FCompare-v<版本>-x64.7z`（精简版），解压到**任意可写目录**（不要解压到 `C:\Program Files`，程序会在 exe 同目录写入 `settings.json`）。

_Download `3FCompare-v<version>-x64-full.7z` (self-contained, recommended) or `3FCompare-v<version>-x64.7z` (lite) from Releases and extract to **any writable folder** (avoid `C:\Program Files` — the app writes `settings.json` next to the exe)._

> **完整版 / full**：已内置 `ffmpeg-full/`，解压即用，无需额外准备。
> **精简版 / lite**：不含 FFmpeg，需按下面第 2 步自行放置 DLL。

**2. 准备 FFmpeg（决定真实模式/演示模式）** / **Prepare FFmpeg (decides real vs. demo mode)**

程序需要一组 Shared FFmpeg DLL（`avcodec-*.dll`、`avformat-*.dll`、`avutil-*.dll`、`swscale-*.dll` 等）。**完整版已内置，可跳过本步**；精简版从下面三种方式任选其一：

_The app needs a set of shared FFmpeg DLLs. **The full package already bundles them — skip this step.** For the lite package, pick any one of:_

| 方式 / Option | 做法 / How |
| --- | --- |
| ① 同目录 `ffmpeg-full/`（推荐） | 把 DLL 组放进 exe 同目录的 `ffmpeg-full/` 子文件夹 / Put the DLLs in an `ffmpeg-full/` subfolder next to the exe |
| ② 同目录平铺 | DLL 与 exe 直接放同一目录 / DLLs directly beside the exe |
| ③ 设置里手动指定 | 设置 → FFmpeg DLL 目录 → 浏览，选中 DLL 所在目录 / Settings → FFmpeg DLL folder → Browse |

DLL 组来源：BtbN 的 FFmpeg-Builds（取出 `bin/*.dll`；需含 `avcodec-63` / `avformat-63` / `avutil-61` / `swscale-10` / `swresample-7` / `avfilter-12` 及 `ass-9.dll`）。⚠ **变体口径实测有三处不一致**：本文原写 `ffmpeg-n8.x-*-win64-gpl-shared-*.7z`；`3fp/kernel/tools/准备FFmpeg.ps1:9` 的默认 URL 却是 `ffmpeg-master-latest-win64-lgpl-shared.zip`；而盘上真正发货的那套（`publish/PLAN/ffmpeg-full`，`git-2026-10-11-edadf711a9`）实测构建串含 `--enable-gpl --enable-nonfree --enable-version3 --enable-libx264 --enable-libx265 --enable-libfdk-aac` ⇒ 属 **nonfree** 变体，照 ps1 默认 URL 重新下载会**静默丢掉** x264/x265/fdk-aac/libplacebo 承接线。逐件许可与分发条件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) §1。**本仓库的 git 集合不含第三方 DLL**（磁盘与发布载荷里有，别按磁盘口径读这句）。

_DLLs come from BtbN's FFmpeg-Builds (take `bin/*.dll`; needs `avcodec-63` / `avformat-63` / `avutil-61` / `swscale-10` / `swresample-7` / `avfilter-12` plus `ass-9.dll`). ⚠ **The variant is stated three different ways, measured**: this file used to say `...-gpl-shared-...`; `3fp/kernel/tools/准备FFmpeg.ps1:9` defaults to `...-lgpl-shared.zip`; and the set actually shipped (`publish/PLAN/ffmpeg-full`, `git-2026-10-11-edadf711a9`) measures `--enable-gpl --enable-nonfree --enable-version3 --enable-libx264 --enable-libx265 --enable-libfdk-aac` — i.e. **nonfree**, so re-downloading via the script's default URL silently drops the x264/x265/fdk-aac/libplacebo chain. Per-component licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) §1. **The git set of this repo contains no third-party DLLs** (the disk and the release payload do — do not read this sentence as a disk claim)._

> **找不到 FFmpeg 会怎样？** 程序**不会崩**，而是回退到**演示模式**（合成画面，可完整走通打开/播放/步进/分格等操作），并在启动时弹窗说明原因、状态栏常驻显示降级原因。
> _If FFmpeg is missing the app does **not** crash — it falls back to **demo mode** (synthetic frames) and tells you why in a startup dialog and the status bar._

**3. 运行** / **Run**

双击 `3FCompare.exe`。把视频文件**拖进窗口**（或「文件 → 打开视频」），最多可加到 9 路。

_Double-click `3FCompare.exe`, then **drag video files into the window** (or File → Open Video), up to 9 ways._

**4. 上手三招** / **Three things to try first**

- **分格盯帧**：拖入 2 个同源不同编码的文件 → 按 `G` 进 **网格对比（分割线可拖）** → `A`/`D` 逐帧、`←`/`→` 按秒，拖中间分割线调两格宽窄
- **左右拉动揭示**：按 `S` 切到 **左右拉动对比**，两路铺满同一区域，左右拉动分割线即在同一帧上揭示两个版本
- **像素探针**：按 `P`，鼠标悬停读像素值（颜色管理前的原生缓冲，跨路可直接比）

常用键：`空格` 播放/暂停 · `A`/`D` 逐帧 · `←`/`→` 逐秒 · `G`/`S` 视图模式（`V` 与 `G` 同态）· `C` 换分格朝向 · `F11` 全屏 · `R` 重置视图 · `Esc` 退出全屏（前四项的键位在设置 → 快捷键里可改）。

_Key keys: `Space` play/pause · `A`/`D` frame step · `←`/`→` second step · `G`/`S` view modes (`V` aliases `G`) · `C` cycles the split orientation · `F11` fullscreen · `R` reset view · `Esc` exit fullscreen (the first four are rebindable in Settings → Shortcuts)._

**常见问题** / **Troubleshooting**

| 现象 / Symptom | 处理 / Fix |
| --- | --- |
| 启动提示「未找到 FFmpeg 核心库」 | 按上面第 2 步放置 DLL，或在弹窗里点「打开设置」指定目录后**重启** / Place the DLLs (step 2) or point to the folder in Settings, then **restart** |
| 一直是演示模式（画面是彩条/测试图） | 状态栏看引擎名与原因；确认 DLL 位数是 **x64**、文件名匹配 `avcodec-*.dll` / Check the engine name and reason in the status bar; make sure the DLLs are **x64** and named `avcodec-*.dll` |
| HDR 画面偏灰/过曝 | 设置 → 色彩：自动即可；也可手动切 SDR/HDR，详见「显示链路」一节 / Settings → Color: leave on Auto, or switch SDR/HDR manually |
| 窗口跑到看不见的地方了 | 窗口几何自动记忆；若上次坐标已不在任何屏幕内（显示器拔掉/分辨率变化），程序会跳过坐标恢复、改用系统默认位置，并在下次关闭时重新记录当前坐标自愈 / Geometry is remembered; if the last position is off every screen, position restore is skipped in favour of the system default and the next close re-records a valid one |

### 构建与运行 / Build & Run

```powershell
# 0. 裸克隆后先恢复内核目录（整目录不入库，随仓带的是基线归档 bundle）
#    / On a fresh clone, restore the kernel tree first (it is gitignored; the repo ships a baseline bundle)
#    目标目录必须不存在；重复执行会报 "destination path already exists"。
git clone .3fc_kernel_baseline.bundle third_party/fff_project

# 1. 一键构建内核 + 部署（需 VS 2022+ C++、Git；首次联网下载 FFmpeg/vcpkg） / One-click kernel build + deploy (requires VS 2022+ C++, Git; first-run downloads FFmpeg/vcpkg)
powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1

# 单元测试 / Unit tests
dotnet test  tests/3FCompare.Core.Tests
dotnet test  tests/3FCompare.Platform.Tests

# 运行应用（有 FFF.Native 时真实模式，无则演示模式） / Run (real mode with FFF.Native, demo mode otherwise)
dotnet run --project src/3FCompare

# 演示模式体验（任意文件，合成画面） / Demo mode: any file, synthetic frames
dotnet run --project src/3FCompare -- --autodemo &lt;文件...&gt;

# E3 冒烟（**始终走演示引擎**，合成画面；检测到真实 DLL 也只是提示，不会切换）
# / E3 smoke test (**always the demo engine**; a real DLL only prints a notice, it is never used)
dotnet run --project tests/3FCompare.SmokeTests -- &lt;视频&gt; [更多视频...]
```

---

## 📝 更新日志（Changelog）

> ⚠ **本节只保留到 v0.2.0-BETA，是历史留档，已停止更新。**
> 完整、最新的变更记录一律看 [`CHANGELOG.md`](CHANGELOG.md)（最新带号段是 `[0.3.2-beta] — 2026-09-30`，
> 其上还有 `[未发布] — 2026-10-08` 与 `2026-10-07` 两段；**git tag 才仍停在 `v0.3.1-beta`**，0.3.2 的包已出但没打 tag）——
> 曾经因为这里单独维护一份，结果长期停在 0.2.0 而与实际版本严重脱节。

### v0.2.0-BETA（2026-08-25）

高码率 HDR 播放稳定性专项版：修复 4K/8K 高码率视频下滚轮缩放导致的渲染卡死，新增两级停滞自愈，并完成帧率解析修正与 FFmpeg 探测修复。

**🔧 高码率缩放卡死修复（内核补丁 0002）**
- **缩放链缓存量化**：`PrepareScaledVideo` 缓存键按 64px 档位取整，连续滚轮缩放命中同一缓存，消除每次缩放重建 8K FP16 纹理链导致的解码线程阻塞（此前在 206Mbps HEVC HDR 素材下 50% 复现渲染完全停滞）
- **叠加层呈现快速路径**：`PresentTimedText` 在交换链尺寸匹配时跳过锁内 `EnsureSwapChain`（含 GetClientRect 与 HDR 能力探测），缩短与解码线程的互斥窗口

**✨ 两级停滞自愈（应用侧）**
- 轮询检测 presented 计数停滞（≈1.25s）：第一级 `Pause→Play` 轻量恢复呈现管线；无效则自动升级完整会话重建，恢复增长后重置升级标志

**🔧 其他修复**
- **帧率解析修正**：`EstimateFps` 不再把流时间基（如 MP4 的 1/15360）当帧率（曾致 4K H.264 显示 15360fps）；改用媒体信息 nominalFrameRate 并加合理范围钳制
- **FFmpeg 子目录探测**：检出 `ffmpeg/` 子目录时同步注册 DLL 搜索路径，修复 Delay-Load 原生崩溃 0xC0005FFE
- **最大化恢复加固**：会话重建后轮询确认 presented 连续增长才算稳定，未稳定自动重试

### v0.1.4-BETA（2026-08-20）

高 DPI 布局与交互稳定性专项版，修复设置界面挤压/网格子菜单遮挡/同步拖拽失效三类问题，并完成**完整双语界面**与**全管线审查整改**，发布前构建 0 错误、40 例单测全过。

**✨ 新增（双语界面）**
- **完整中英双语**：此前仅设置对话框实现双语，主菜单/工具栏/工具面板/状态栏/消息框/文件过滤器全为中文硬编码；现全部接入 `LanguageManager` 资源（约 100+ 中英键），添加 `LanguageChanged` 事件在切换语言时即时刷新整个界面
- **启动应用已保存语言**：修复«保存英文后重启仍是中文»的关键 Bug —— `MainForm` 启动时应用 `settings.Language` 并刷新全部控件

**🔧 管线审查整改**
- 新增 `Core/Display/GridLayout.cs` 纯逻辑（自动网格/预设解析），`CompareGridView` 委托复用；新增 `GridLayoutTests`（单测 **24 → 40 例**）
- `Core.csproj` 移除硬编码 `AssemblyVersion/FileVersion`，改由 SDK 从 `Version`+`VersionSuffix` 派生
- `EngineSnapshot.State` 由 `int` 强化为 `PlayerState` 枚举（值与原生命令一一对应）
- 构建脚本补丁标记修复：仅当全部补丁成功才标记，失败下次可重试

**🐛 修复**
- **设置界面高 DPI 挤压/冲突**：设置窗口在 150%/200% 缩放下 `TabControl` 未随 AutoScale 同步缩放、控件部分放大而按钮不缩放，导致布局错乱挤压。重构为 `TableLayoutPanel` 自适应布局（内容行 AutoSize 紧凑 + 弹性空行吸收余量），标签页控件随窗体等比缩放，不再重叠、越界
- **网格布局预设菜单被遮挡**：视图 →「网格布局」子菜单默认向右侧弹出，窗口靠近屏幕/窗口右缘时子项（2×1/2×2/3×3/自动）超出可视区被剪裁。显式 `DropDownDirection = Left` 向左弹出，子菜单完整落在可视区域内
- **缩放后同步拖拽失效**：多路网格下按住拖动平移时，指针一跨出当前画面边界即触发 `MouseLeave` 清空共享拖拽状态，即使左键仍按住后续拖动也失效。改为 `MouseDown` 设置鼠标捕获（`Capture=true`）+ `MouseLeave` 不再清理 `_dragging`，拖拽结束完全由 `MouseUp` 决定，跨路同步平移连续不中断

### v0.1.2-BETA（2026-08-19）

完整发布双版本（精简 / 完整含 FFmpeg），发布前全管线审查通过（构建 0 错误、24 例单测全过、真实/演示两模式 selftest 验证）。

**✨ 新增**
- **网格布局预设菜单**：视图 → 网格布局 2×1 / 2×2 / 3×3 预设或自动布局一键切换
- **可停靠工具侧栏**：右侧 Dock 标签页（探针 / 书签 / 偏移 / 媒体 / 音频）+ Pin 固定，替代常驻面板，释放画面空间
- **时间轴拖动缩略图预览**：ScrubPreview + 悬浮缩略图窗（ThumbnailPopup），拖动时间轴实时预览关键帧
- **自适应轮询**：播放 16ms 精跟 / 空闲 250ms 省电
- **高 DPI 支持**：PerMonitorV2 + `Dpi` 工具类，4K 250% 缩放下控件树正确缩放
- FFmpeg 目录可手动设置（设置 UI + FFMPEG_DIR → PATH → 程序目录三级探测链）

**🐛 修复**
- 发布版本号错位：移除 csproj 硬编码 `AssemblyVersion/FileVersion/InformationalVersion`，改由 SDK 从 `Version`+`VersionSuffix` 自动派生，`pack.ps1 -p:Version` 现在真正生效（此前 0.1.1 发布 exe 实际显示 0.1.0）
- 编译警告清零：移除 VerticalDockHost 未用变量、网格布局 lambda 捕获顺序告警（CS0219/CS8602），Release 构建 0 错误

### v0.1.1-BETA（2026-08-18）

支持 NativeAOT 双版本发布（精简版 / 完整版含 FFmpeg）。

**✨ 新增**
- **同步视图变换**：鼠标滚轮缩放（1~32×，多路同步）、拖拽平移、R 键重置
- **深色主题系统**：`AppTheme` 统一配色 + `LayoutConstants` 布局常量，设置窗口重构为分页标签布局
- **智能色调映射**：通过 DXGI 读取真实显示器亮度（HDR 峰值/纸白），结合源内容 HDR 状态自动计算 BT.2390 映射参数，替代固定 100 nits
- 单元测试从 10 例扩至 24 例（新增 ToneMappingParameters 测试）

**🐛 修复**
- **精简版打开视频崩溃**（`0xC0005FFE`）：引擎探测改为同时检测 FFmpeg（`avcodec-*.dll`），精简版缺 FFmpeg 时安全回退演示模式
- **滚轮缩放失效**：`PlayerSurface` 为无焦点控件导致 `MouseWheel` 收不到事件，改为全局 `IMessageFilter` 拦截，鼠标悬停任意画面即缩放
- **字母快捷键（R/B/P/O/S 等）失效**：中文输入法激活时截获字母键为 `VK_PROCESSKEY`，设置 `ImeMode.Disable` 后快捷键恢复
- 打包脚本 FFmpeg 复制计数显示错误

### v0.1.0-BETA（2026-08-17）

首个可分发版本，支持 NativeAOT 双版本发布。

- 多路对比 1~9 路、双步进（帧/秒）、同步播放/步进/循环
- 完整对比工具：像素探针、A-B 滑块、放大镜、书签、截图导出、差异叠加、媒体信息、音频面板
- 二级设置窗口（硬件解码、多 GPU、步长、色彩模式等）、全屏/窗口模式、会话保存/加载
- 真实 3FP 内核全链路（FFmpeg 解码 → D3D 渲染 → App 显示）+ 演示模式自动回退
- NativeAOT 自包含单文件发布（约 23MB）+ 精简/完整双版本打包规范