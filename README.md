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
提供**三种视图模式（标准网格 / A/B 可拖动分格 / 左右拉动揭示，视图菜单顶部一列互斥切换）**、
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
| 产品形态 / Type | Windows 桌面应用（**Avalonia 11**，.NET 11）；**NativeAOT 自包含（精简版 7z 分发约 9.3MB）** / Windows desktop app (**Avalonia 11**, .NET 11); **NativeAOT self-contained (lite 7z ~9.3MB)** |
| 对标产品 / Reference | NVIDIA ICAT（最多 4 路视频/图像对比）——本项目**扩展至 1~9 路**，对齐、双步进、硬件解码开关、窗口/全屏、多显卡解码 / NVIDIA ICAT (up to 4-way) — **extended to 1–9 ways** with alignment, dual stepping, HW decode toggle, window/fullscreen, multi-GPU |
| 后端 / Backend | FFF_Project 的 **3FP**（`FFF.Native` fork + 自研补丁，见 docs/03-后端接入与能力映射.zh.md） / **3FP** from FFF_Project (forked `FFF.Native` + custom patches, see docs/03-后端接入与能力映射.en.md) |
| 业务规模 / Scale | **1~9 路对比**（3x3 网格上限），架构按 N 路扩展 / **1–9 way comparison** (3×3 grid max), architecture scales to N-way |
| 解码 / Decode | 3FP 原生能力：CPU（FFmpeg）/ GPU（CUDA/NVDEC、D3D11VA 优先）+ 自动回退；**硬件开关 + 多 GPU 指定（2026-09-16 起生效）** / 3FP native: CPU (FFmpeg) / GPU (CUDA/NVDEC, D3D11VA preferred) + auto fallback; **HW toggle + multi-GPU selection** |


| [PACKAGING_SPEC.md](PACKAGING_SPEC.md) | 打包规范：NativeAOT 双版本发布流程、命名规则、压缩配置 |

## 🌈 显示链路（ACM / VRR）要点 / Display Chain Highlights

- **ACM/广色域**：完全遵循 3FP 的 Advanced Color 交换链契约（SDR `BGRA8/RGB10A2`、HDR `R10G10B10A2+PQ/BT.2020`），
  显示侧校色交给 DWM；本项目自行探测显示能力（DXGI 亮度读取）并计算智能色调映射参数，探针/截屏始终读取「颜色管理前」的原生缓冲，保证跨路对比一致。
- **G-SYNC / FreeSync**：播放窗口为独立窗口，**不破坏桌面 VRR**。
  ~~是否全时刻生效取决于 3FP 交换链（Present 节奏 / `ALLOW_TEARING`），待专项实测~~
  → **2026-09-16 已实测支持**：`SetPresentConfig`（撕裂呈现）「显示器链支持 ✓」、
  `SetPacingConfig`（媒体率节奏）「已启用 ✓」，见 [docs/03](docs/03-后端接入与能力映射.zh.md) 的 **A8/A9**。
- 专项验收清单见 [docs/01-需求分析.zh.md §5.1](docs/01-需求分析.zh.md)。

## ⚖️ 依赖与许可提示 / License & Dependencies

- `FFF.Native` 为 **MIT 许可（已确认）**，本项目**基于其源码二次开发**：以 git submodule 固定 commit，
  在其上追加自研扩展补丁（VRR 交换链 / 视口子区域 / 全帧回读等，见 `docs/03 §6`），保持与上游可合并。
- FFmpeg 公共 API：Shared FFmpeg DLL 组（`avcodec` 等）由 BtbN 构建，**不纳入本仓库**，仅在发布说明中指引获取。
- 本项目的 UI、同步逻辑、对比工具均为独立实现；本仓库不包含任何第三方 DLL 二进制。

> 详细依赖清单、构建步骤与风险见 [docs/06-风险与依赖.zh.md](docs/06-风险与依赖.zh.md)。

## 🛠 工程状态（v0.3.0-beta，2026-09-23）

> ⚠ 本节标题此前长期停留在 `0.2.0-BETA，2026-08-25`，与实际版本严重脱节，现已更正。
> 版本唯一真源是 csproj 的 `<Version>`，改版本时记得同步此处。

> **版本号唯一真源**：`src/3FCompare/3FCompare.csproj` 的 `<Version>`（预发布后缀直接写进
> `<Version>`，如 `0.3.0-beta`；独立的 `<VersionSuffix>` 已移除，见 docs/45 P1-12）。
> `pack.ps1` 与 `tools/发布门禁.ps1` 不硬编码版本——不传 `-Version` 时自动从 csproj 派生，
> 传入不一致会告警。详见 [PACKAGING_SPEC.md §5](PACKAGING_SPEC.md)。

```text
src/
├── 3FCompare.slnx              # 解决方案（.NET 11 新格式）
├── 3FCompare.Core/             # 后端抽象 / 3FP P/Invoke / 演示引擎 / 同步 / 设置 / GPU 枚举 / DXGI 显示器能力
├── 3FCompare/         # Avalonia 主程序（多路网格 / 双步进 / 时间轴 / 设置 / 全屏 / 对比工具 / 主题；
│                               #   2026-08-22 由 WinForms 迁移而来，WinForms 版归档于 tag `winforms-final`）
tests/
├── 3FCompare.SmokeTests/       # E3 冒烟（控制台，演示引擎全流程验证）
├── 3FCompare.Core.Tests/       # 单元测试（FrameTimeline / SyncController / GridLayout / ToneMapping / PixelReadback / RenderStallWatchdog 等，175 例）
third_party/
└── fff_project/                # FFF_Project submodule（内核，MIT）
    └── FFF.Native → x64/Release/FFF.Native.dll   # 已构建（Release x64）
    └── runtime/                 # Shared FFmpeg DLL 组（BtbN，已准备）
    └── third_party/vcpkg_installed/  # libass（vcpkg，已准备）
```

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
- **快捷键** / **Keyboard shortcuts**: `空格` 播放/暂停 · `A`/`D` 逐帧 · `←`/`→` 逐秒 · `↑`/`↓` ±10 秒 · `G`/`V`/`S` 视图模式三态 · `C` 对比布局循环 · `F11` 全屏 · `R` 重置视图 · `O` 打开 · `P` 探针 · `Ctrl+H` 侧栏三态 · `Esc` 退出全屏 / Space play/pause, A/D frame step, ←/→ second step, ↑/↓ 10s step, G/V/S view modes, C cycle compare layouts, F11 fullscreen, R reset view, O open, P probe, Ctrl+H sidebar tri-state, Esc exit fullscreen（六项传输键全部可在设置里改 / all six transport keys rebindable in Settings）
- **对比工具** / **Comparison tools**: 像素探针 pixel probe (F19)、放大镜 magnifier (F17)、书签 bookmarks (F22)、截图导出 screenshot export PNG (F21)、差异叠加 diff overlay heatmap (F20)、媒体信息 media info (F3)、音频面板 audio panel、时间轴 A/B 打点 timeline A/B markers (A/B keys)
- **同步视图变换**：鼠标滚轮**缩放**（1~32×）+ 拖拽**平移**（多路同步），R 键重置（0.1.1 新增） / **Sync view transform**: mouse wheel **zoom** (1–32×) + drag **pan** (multi-way sync), R reset (0.1.1)
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
- **内核基线 SHA 绑定**：`构建全部.ps1` 钉死内核 SHA，DLL 与 HEAD 不一致即强制重建（0.2.0 新增） / **Kernel baseline SHA pinning**: build script pins the kernel SHA and forces rebuild on mismatch (0.2.0)

### 🧭 对比呈现的两族：传统模式 与 现代模式 / Two families of comparison: classic vs. seamless

两族的**语义区别**在"每格显示什么"，不在格子数量：**传统模式**把每一路**完整**显示在它自己的格子里（各自适应各自
的格子）；**现代模式**把所有路**同步放大到同一段源画面**，再从这段共同区域里各裁一格出来比对（0.3.0-beta 新增）。
The distinction is **what each cell shows**, not how many cells there are: **classic modes** fit each route
**whole** into its own cell; **seamless modes** zoom all routes **together onto the same source region** and then
crop one cell out of that shared region for side-by-side inspection (new in 0.3.0-beta).

- **传统模式 / Classic modes**（`CompareMode`：Ab 2 路左右、Abc 3 路左大+右上下、Abcd 4 路十字、AbVertical 2 路上下、
  AbcColumns 3 路三列；另有视图 → 网格布局的 2×1 / 2×2 / 3×3 / 自动 预设）
  / **Classic modes** (`CompareMode`: Ab 2-way side-by-side, Abc 3-way picture-in-picture, Abcd 4-way quadrant,
  AbVertical 2-way stacked, AbcColumns 3-column; plus View → Grid Layout presets 2×1 / 2×2 / 3×3 / auto)
- **现代模式 / Seamless modes**：滚轮**以光标为锚点**同步缩放（1× 起，缩小到底退出）、放大态可拖动平移且
  **松手时只提交一次**裁剪，避免逐帧 `SetWindowRgn` / **Wheel zoom anchored at the cursor** (from 1×; zooming back
  out to 1× exits), drag-to-pan while zoomed with the crop **committed once on release** instead of per frame
- **视图模式：菜单顶部一列，三态互斥 / View modes: one mutually exclusive column at the top of the View menu**
  入口 **视图 → 视图模式 → 标准模式 / A/B 可拖动对比 / 左右拉动对比**，右侧直接标着各自的快捷键；
  三项做成单选（圆点）而不是复选，勾在哪一项就是当前处于哪一 mode。
  - **标准模式（`G`）**：均匀网格，1~9 路全部显示（每格完整装下各路），可用「网格布局」预设 2×1 / 2×2 / 3×3 / 自动 与「单屏/多屏切换」细调。
  - **A/B 可拖动对比（`V`）**：各路分格显示，**格与格之间的分割线可拖**（手柄在 `LayoutOverlayWindow` 上，鼠标穿透只让手柄可点）。按路数收敛：2 路→AB 左右、3 路→ABC、≥4 路→ABCD 且只显示前 4 路；细分排布（AB 上下、ABC 三列）用 `C` 键循环，循环到末项即退出对比模式。
  - **左右拉动对比（`S`）**：**只对比两路**（≥3 路时取前两路，状态栏写明"显示前 2 / 共 N 路"，其余路在此模式下隐藏）：两路**都铺满同一区域**，各由 `SetWindowRgn` 裁成互补且不重叠的两半，于是**左右拖动分割线 = 在两路之间揭示 / 擦除**（对标 NVIDIA ICAT Single Screen）。**整条分割线从上到下都能抓**（不必瞄准线中间的圆点），光标移到线上即变成左右箭头。键是开关语义（再按一次回到 A/B 可拖动），菜单项是"进入"语义。此模式下无缝放大与左右互换被禁（三者都要改子窗口矩形或区域，语义互斥）。
  - **没占格的路是"不启用"，不是"藏起来"**：任何模式下当前视图放不下的那几路会被 Pause 并从播放 / 步进 / Seek /
    漂移校正里整体移出（实测 5 路用 A/B 可拖动：未占格的第 5 路 1.2 秒 presented **+0**，改动前是 **+41**，
    与看得见的四路同速白烧解码与呈现线程）；重新占格时先 Seek 回规范时间、再按"要播放"的意图恢复，
    所以切回去不会停在旧帧。第 0 路例外，恒启用——它是规范时间轴的基准
    / routes that don't fit the current view are **deactivated, not merely hidden**: they get paused and dropped from
    play / step / seek / drift-correction (measured with 5 routes on A/B split: the off-screen route presented
    **+0** frames in 1.2 s, versus **+41** before the fix — the same rate as the visible ones); when a route comes back
    it is seeked to the master position first, so it never shows a stale frame. Route 0 is always active — it *is* the timebase
  - 勾选状态在菜单展开时按真实状态刷新：`G`/`V`/`S`/`C` 这些快捷键改过模式后，菜单里不会留下陈旧圆点
  / **View → View mode → Standard view / A/B draggable split / Left-right wipe**, each labelled with its shortcut and rendered as a radio column (the bullet sits on the mode actually in effect). **Standard (G)** = uniform grid, all 1–9 routes whole in their cells, with grid presets and single/multi toggle underneath. **A/B draggable (V)** = routes in separate cells whose shared divider you drag (2→AB, 3→ABC, ≥4→ABCD showing the first four; `C` cycles the sub-layouts). **Left-right wipe (S)** = exactly **two** routes (the first two when more are open, the rest hidden) covering the same area, `SetWindowRgn` trimming each to a complementary, non-overlapping half, so dragging the line wipes between them (NVIDIA ICAT's Single Screen) — **the whole line is grabbable top to bottom**, no need to aim for the grip dot, and the cursor turns into a resize arrow over it; the key toggles while the menu item enters, and seamless zoom / swap are disabled there by design. Checkmarks are re-read from real state on menu open, so keyboard-driven switches never leave a stale bullet
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
  只画两块渐变占位、真机模式下会把真实画面整块遮住，与上面的 A/B 可拖动 / 左右拉动是同一概念的假版本；
  「显示 对比网格」的实际动作是折叠侧栏，与「视图 → 侧栏」三态重复
  / **A-B Slider View** (with its bare `B` key and `AbSliderView`) painted two gradient placeholders and covered the real picture in real mode — a fake twin of the two modes above; **Show Comparison Grid** actually collapsed the sidebar, duplicating View → Sidebar's tri-state
- **双语 / Bilingual**：上述入口的中文与英文串都在 `Localization/LanguageManager.cs` 的两张表里
  （`Menu_ViewMode`、`Menu_Mode_Standard|AbDrag|Wipe`、`Menu_CompareOptions`、`Menu_Mode_Swap|Rotate`、`Menu_AutoCompare`），切换语言即时生效
  / every entry above has both zh and en strings in the two tables of `Localization/LanguageManager.cs`
  (`Menu_ViewMode`, `Menu_Mode_Standard|AbDrag|Wipe`, `Menu_CompareOptions`, `Menu_Mode_Swap|Rotate`, `Menu_AutoCompare`); switching language applies live
- **怎么试 / How to try**：打开 2 个以上文件 → **视图 → 视图模式 → A/B 可拖动对比**（或按 `V`）→ 拖分割线换两路宽窄、
  在画面上滚动滚轮放大、按住拖动平移、菜单 **对比选项 → 左右互换（AB）** 换源 →
  再切 **左右拉动对比**（或按 `S`）左右拉动揭示 → 滚回 1× 退出放大、按 `G` 回标准模式
  / open 2+ files → **View → View mode → A/B draggable split** (or press `V`) → drag the divider, wheel-zoom on the
  picture, drag to pan, **Compare options → Swap left/right (AB)** to exchange sources → switch to **Left-right wipe**
  (or press `S`) to reveal between the two → wheel back to 1× and press `G` for the standard view

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

DLL 组来源：BtbN 的 FFmpeg-Builds（`ffmpeg-n8.x-*-win64-gpl-shared-*.7z`，取出 `bin/*.dll`；需含 `avcodec-63` / `avformat-63` / `avutil-61` / `swscale-10` / `swresample-7` / `avfilter-12` 及 `ass-9.dll`）。**本仓库不包含任何第三方 DLL。**

_DLLs come from BtbN's FFmpeg-Builds (`ffmpeg-n8.x-*-win64-gpl-shared-*.7z`, take `bin/*.dll`; needs `avcodec-63` / `avformat-63` / `avutil-61` / `swscale-10` / `swresample-7` / `avfilter-12` plus `ass-9.dll`). This repo ships no third-party DLLs._

> **找不到 FFmpeg 会怎样？** 程序**不会崩**，而是回退到**演示模式**（合成画面，可完整走通打开/播放/步进/分屏等操作），并在启动时弹窗说明原因、状态栏常驻显示降级原因。
> _If FFmpeg is missing the app does **not** crash — it falls back to **demo mode** (synthetic frames) and tells you why in a startup dialog and the status bar._

**3. 运行** / **Run**

双击 `3FCompare.exe`。把视频文件**拖进窗口**（或「文件 → 打开视频」），最多可加到 9 路。

_Double-click `3FCompare.exe`, then **drag video files into the window** (or File → Open Video), up to 9 ways._

**4. 上手三招** / **Three things to try first**

- **分屏盯帧**：拖入 2 个同源不同编码的文件 → 按 `V` 进 **A/B 可拖动对比** → `A`/`D` 逐帧、`←`/`→` 按秒，拖中间分割线调两路宽窄
- **左右拉动揭示**：按 `S` 切到 **左右拉动对比**，两路铺满同一区域，左右拉动分割线即在同一帧上揭示两个版本
- **像素探针**：按 `P`，鼠标悬停读像素值（颜色管理前的原生缓冲，跨路可直接比）

常用键：`空格` 播放/暂停 · `A`/`D` 逐帧 · `←`/`→` 逐秒 · `G`/`V`/`S` 视图模式 · `C` 对比布局循环 · `F11` 全屏 · `R` 重置视图 · `Esc` 退出全屏（前四项的键位在设置 → 快捷键里可改）。

_Key keys: `Space` play/pause · `A`/`D` frame step · `←`/`→` second step · `G`/`V`/`S` view modes · `C` cycle compare layout · `F11` fullscreen · `R` reset view · `Esc` exit fullscreen (the first four are rebindable in Settings → Shortcuts)._

**常见问题** / **Troubleshooting**

| 现象 / Symptom | 处理 / Fix |
| --- | --- |
| 启动提示「未找到 FFmpeg 核心库」 | 按上面第 2 步放置 DLL，或在弹窗里点「打开设置」指定目录后**重启** / Place the DLLs (step 2) or point to the folder in Settings, then **restart** |
| 一直是演示模式（画面是彩条/测试图） | 状态栏看引擎名与原因；确认 DLL 位数是 **x64**、文件名匹配 `avcodec-*.dll` / Check the engine name and reason in the status bar; make sure the DLLs are **x64** and named `avcodec-*.dll` |
| HDR 画面偏灰/过曝 | 设置 → 色彩：自动即可；也可手动切 SDR/HDR，详见「显示链路」一节 / Settings → Color: leave on Auto, or switch SDR/HDR manually |
| 窗口跑到看不见的地方了 | 窗口几何自动记忆；若上次坐标已不在任何屏幕内（显示器拔掉/分辨率变化），程序会跳过坐标恢复、改用系统默认位置，并在下次关闭时重新记录当前坐标自愈 / Geometry is remembered; if the last position is off every screen, position restore is skipped in favour of the system default and the next close re-records a valid one |

### 构建与运行 / Build & Run

```powershell
# 一键构建内核 + 部署（需 VS 2022+ C++、Git；首次联网下载 FFmpeg/vcpkg） / One-click kernel build + deploy (requires VS 2022+ C++, Git; first-run downloads FFmpeg/vcpkg)
powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1

# 单元测试 / Unit tests
dotnet test  tests/3FCompare.Core.Tests

# 运行应用（有 FFF.Native 时真实模式，无则演示模式） / Run (real mode with FFF.Native, demo mode otherwise)
dotnet run --project src/3FCompare

# 演示模式体验（任意文件，合成画面） / Demo mode: any file, synthetic frames
dotnet run --project src/3FCompare -- --autodemo &lt;文件...&gt;

# E3 冒烟（真实/演示自动切换） / E3 smoke test (auto real/demo mode)
dotnet run --project tests/3FCompare.SmokeTests -- &lt;视频&gt; [更多视频...]
```

---

## 📝 更新日志（Changelog）

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