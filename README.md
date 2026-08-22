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
提供**分屏（1~9 路网格）/ 单屏切换 / A-B 滑块 / 双步进（帧&秒）/ 像素探针 / 放大镜**等贴合“盯帧”工作流的操作，
支持**硬件编解码开关与多显卡解码指定**、**窗口/全屏双模式**，
并原生支持 **Windows Advanced Color（广色域 / ACM）** 与 **G-SYNC / FreeSync（VRR）** 显示链路。

---

## 📌 产品定位 / Product Positioning

| 维度 / Dimension | 说明 / Description |
| --- | --- |
| 产品形态 / Type | Windows 桌面应用（**Avalonia 11**，.NET 11）；**NativeAOT 自包含（精简版 7z 分发约 9.3MB）** / Windows desktop app (**Avalonia 11**, .NET 11); **NativeAOT self-contained (lite 7z ~9.3MB)** |
| 对标产品 / Reference | NVIDIA ICAT（最多 4 路视频/图像对比）——本项目**扩展至 1~9 路**，对齐、双步进、硬件解码开关、窗口/全屏、多显卡解码 / NVIDIA ICAT (up to 4-way) — **extended to 1–9 ways** with alignment, dual stepping, HW decode toggle, window/fullscreen, multi-GPU |
| 后端 / Backend | FFF_Project 的 **3FP**（`FFF.Native` fork + 自研补丁，见 docs/03） / **3FP** from FFF_Project (forked `FFF.Native` + custom patches, see docs/03) |
| 业务规模 / Scale | **1~9 路对比**（3x3 网格上限），架构按 N 路扩展 / **1–9 way comparison** (3×3 grid max), architecture scales to N-way |
| 解码 / Decode | 3FP 原生能力：CPU（FFmpeg）/ GPU（CUDA/NVDEC、D3D11VA 优先）+ 自动回退；**硬件开关 + 多 GPU 指定** / 3FP native: CPU (FFmpeg) / GPU (CUDA/NVDEC, D3D11VA preferred) + auto fallback; **HW toggle + multi-GPU selection** |


| [PACKAGING_SPEC.md](PACKAGING_SPEC.md) | 打包规范：NativeAOT 双版本发布流程、命名规则、压缩配置 |

## 🌈 显示链路（ACM / VRR）要点 / Display Chain Highlights

- **ACM/广色域**：完全遵循 3FP 的 Advanced Color 交换链契约（SDR `BGRA8/RGB10A2`、HDR `R10G10B10A2+PQ/BT.2020`），
  显示侧校色交给 DWM；本项目自行探测显示能力（DXGI 亮度读取）并计算智能色调映射参数，探针/截屏始终读取「颜色管理前」的原生缓冲，保证跨路对比一致。
- **G-SYNC / FreeSync**：播放窗口为独立窗口，**不破坏桌面 VRR**；是否全时刻生效取决于 3FP 交换链
  （Present 节奏 / `ALLOW_TEARING`，见 [docs/03](docs/03-后端接入与能力映射.md) 待确认项 **A8/A9**），待专项实测。
- 专项验收清单见 [docs/01-需求分析.md §5.1](docs/01-需求分析.md)。

## ⚖️ 依赖与许可提示 / License & Dependencies

- `FFF.Native` 为 **MIT 许可（已确认）**，本项目**基于其源码二次开发**：以 git submodule 固定 commit，
  在其上追加自研扩展补丁（VRR 交换链 / 视口子区域 / 全帧回读等，见 `docs/03 §6`），保持与上游可合并。
- FFmpeg 公共 API：Shared FFmpeg DLL 组（`avcodec` 等）由 BtbN 构建，**不纳入本仓库**，仅在发布说明中指引获取。
- 本项目的 UI、同步逻辑、对比工具均为独立实现；本仓库不包含任何第三方 DLL 二进制。

> 详细依赖清单、构建步骤与风险见 [docs/06-风险与依赖.md](docs/06-风险与依赖.md)。

## 🛠 工程状态（0.1.4-BETA，2026-08-20）

```text
src/
├── 3FCompare.slnx              # 解决方案（.NET 11 新格式）
├── 3FCompare.Core/             # 后端抽象 / 3FP P/Invoke / 演示引擎 / 同步 / 设置 / GPU 枚举 / DXGI 显示器能力
├── 3FCompare/         # Avalonia 主程序（多路网格 / 双步进 / 时间轴 / 设置 / 全屏 / 对比工具 / 主题；
│                               #   2026-08-22 由 WinForms 迁移而来，WinForms 版归档于 tag `winforms-final`）
tests/
├── 3FCompare.SmokeTests/       # E3 冒烟（控制台，演示引擎全流程验证）
├── 3FCompare.Core.Tests/       # 单元测试（FrameTimeline / SyncController / GridLayout / ToneMapping 等，40 例）
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
迁移纪要见 [docs/07-Avalonia迁移规划.md](docs/07-Avalonia迁移规划.md)。
`tools/构建全部.ps1` 一键复现内核构建与 DLL 部署。


### 已实现功能 / Implemented Features

- **多路对比 1~9 路**（2x2/3x2/3x3 自动网格，点击选中，单屏/多屏切换，数字键 1-9 加路） / **1–9 way comparison** (2×2/3×2/3×3 automatic grid, click selection, single/multi view toggle, number keys 1-9)
- **双步进**：按帧（←/→）与按秒（Shift+←/→）两组前进/后退，步长可在设置中调整 / **Dual stepping**: frame-stepping (←/→) and second-stepping (Shift+←/→), configurable step sizes
- **同步播放/暂停/停止/Seek/循环**：以第 0 路为 master 的媒体时间同步（SyncController）/ **Sync play/pause/stop/seek/loop**: SyncController with slot 0 as master
- **二级设置窗口**：硬件解码开关、GPU 选择（多显卡）、步进步长、色彩模式、默认布局、窗口/全屏行为（F25/F26） / **Settings dialog**: HW decode toggle, multi-GPU selection, step sizes, color mode, layout, window/fullscreen behavior (F25/F26)
- **全屏模式**（F11）+ 窗口模式，全屏可隐藏工具栏/时间轴 / **Fullscreen mode** (F11) + window mode, hide chrome in fullscreen
- **会话保存/加载**（`.3fcs` JSON：文件列表/偏移/布局/位置/循环区间） / **Session save/load** (`.3fcs` JSON: file list, offsets, layout, position, loop range)
- **快捷键** / **Keyboard shortcuts**: Space play/pause, ←→ frame step, Shift+←→ second step, ↑↓ 10s step, F11 fullscreen, B A-B marker, P probe, O open, R reset view, Esc exit fullscreen
- **对比工具** / **Comparison tools**: 像素探针 pixel probe (F19)、A-B 滑块滑块 A-B slider view (F15)、放大镜 magnifier (F17)、书签 bookmarks (F22)、截图导出 screenshot export PNG (F21)、差异叠加 diff overlay heatmap (F20)、媒体信息 media info (F3)、音频面板 audio panel、时间轴 A/B 打点 timeline A/B markers (A/B keys)
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