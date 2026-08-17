# 3FCompare（项目代号：ICAT-Like 视频盯帧/画质对比软件）

> 目标：做一款与 NVIDIA ICAT 同类的视频盯帧（逐帧对比）桌面软件，
> 播放/解码后端复用 FFF 帝国（[FFF_Project](https://github.com/Lake1059/FFF_Project)）的 **3FP 播放器内核**。

3FCompare 面向视频编码评测（VCB-Studio 等圈子）的场景：把多个编码版本的视频按帧对齐，
提供**分屏（1~9 路网格）/ 单屏切换 / A-B 滑块 / 双步进（帧&秒）/ 像素探针 / 放大镜**等贴合“盯帧”工作流的操作，
支持**硬件编解码开关与多显卡解码指定**、**窗口/全屏双模式**，
并原生支持 **Windows Advanced Color（广色域 / ACM）** 与 **G-SYNC / FreeSync（VRR）** 显示链路。

---

## 📌 产品定位

| 维度 | 说明 |
| --- | --- |
| 产品形态 | Windows 桌面应用（WinForms，.NET 11）；**NativeAOT 自包含单文件已启用（21MB 原生产物）** |
| 对标产品 | NVIDIA ICAT（最多 4 路视频/图像对比）——本项目**扩展至 1~9 路**，对齐、双步进、硬件解码开关、窗口/全屏、多显卡解码 |
| 后端 | FFF_Project 的 **3FP**（`FFF.Native` fork + 自研补丁，见 docs/03） |
| 业务规模 | **1~9 路对比**（3x3 网格上限），架构按 N 路扩展 | 
| 解码 | 3FP 原生能力：CPU（FFmpeg）/ GPU（CUDA/NVDEC、D3D11VA 优先）+ 自动回退；**硬件开关 + 多 GPU 指定** |
| 理想阶段 | MVP（2 路同步步进）→ 完整版（4 路 + 丰富对比工具） |

## 🗂 文档索引

| 文档 | 内容 |
| --- | --- |
| [docs/01-需求分析.md](docs/01-需求分析.md) | 用户画像、功能清单、非功能需求、验收标准 |
| [docs/02-系统架构.md](docs/02-系统架构.md) | 分层架构、模块划分、进程模型、关键设计 |
| [docs/03-后端接入与能力映射.md](docs/03-后端接入与能力映射.md) | 3FP API 逐个映射到我们的模块与调用方式；**MIT fork 扩展补丁设计（§6）** |
| [docs/04-同步模型设计.md](docs/04-同步模型设计.md) | 多会话时钟、帧基准同步、步进/Seek 一致性方案 |
| [docs/05-里程碑与任务分解.md](docs/05-里程碑与任务分解.md) | M0–M5 里程碑、任务清单、依赖关系 |
| [docs/06-风险与依赖.md](docs/06-风险与依赖.md) | 已知依赖、风险矩阵、外部 FFmpeg 版本策略 |

## 🚀 快速路线（详见里程碑文档）

```text
M0 骨架（2 路，双步进帧/秒）  →  M1 1~9 路网格 + 全屏/窗口  →  M2 播放同步 + 硬件解码开关 + VRR 实测
M3 对比工具（放大/滑动/A-B）+ ACM 广色域  →  M4 二级设置窗口/工程化  →  M5 打磨与发布（ACM/VRR 专项验收）
```

## 🌈 显示链路（ACM / VRR）要点

- **ACM/广色域**：完全遵循 3FP 的 Advanced Color 交换链契约（SDR `BGRA8/RGB10A2`、HDR `R10G10B10A2+PQ/BT.2020`），
  显示侧校色交给 DWM；本项目自行探测显示能力（F24–F26）并透传配置，探针/截屏始终读取「颜色管理前」的原生缓冲，保证跨路对比一致。
- **G-SYNC / FreeSync**：播放窗口为独立窗口，**不破坏桌面 VRR**；是否全时刻生效取决于 3FP 交换链
  （Present 节奏 / `ALLOW_TEARING`，见 [docs/03](docs/03-后端接入与能力映射.md) 待确认项 **A8/A9**），M2 前实测。
- 专项验收清单见 [docs/01-需求分析.md §5.1](docs/01-需求分析.md)。

## ⚖️ 依赖与许可提示

- `FFF.Native` 为 **MIT 许可（已确认）**，本项目**基于其源码二次开发**：以 git submodule 固定 commit，
  在其上追加自研扩展补丁（VRR 交换链 / 视口子区域 / 全帧回读等，见 `docs/03 §6`），保持与上游可合并。
- FFmpeg 公共 API：Shared FFmpeg DLL 组（`avcodec` 等）由 BtbN 构建，**不纳入本仓库**，仅在发布说明中指引获取。
- 本项目的 UI、同步逻辑、对比工具均为独立实现；本仓库不包含任何第三方 DLL 二进制。

> 详细依赖清单、构建步骤与风险见 [docs/06-风险与依赖.md](docs/06-风险与依赖.md)。

## 🛠 工程状态（M0–M2 完成 + 真实内核接入，2026-08-16）

```text
src/
├── 3FCompare.slnx              # 解决方案（.NET 11 新格式）
├── 3FCompare.Core/             # 后端抽象 / 3FP P/Invoke / 演示引擎 / 同步 / 设置 / GPU 枚举
├── 3FCompare.App/              # WinForms 主程序（多路网格 / 双步进 / 时间轴 / 设置 / 全屏 / 对比工具）
tests/
├── 3FCompare.SmokeTests/       # E3 冒烟（控制台，演示引擎全流程验证）
├── 3FCompare.Core.Tests/       # 单元测试（FrameTimeline / SyncController，10 例）
third_party/
└── fff_project/                # FFF_Project submodule（内核，MIT）
    └── FFF.Native → x64/Release/FFF.Native.dll   # 已构建（Release x64）
    └── runtime/                 # Shared FFmpeg DLL 组（BtbN，已准备）
    └── third_party/vcpkg_installed/  # libass（vcpkg，已准备）
```

**里程碑状态**：✅ 真实 3FP 内核全链路已验证（FFmpeg 解码 → D3D 渲染 → App 显示）。
✅ **NativeAOT 已启用**：`dotnet publish -c Release -r win-x64` → 21MB 原生单文件 `3FCompare.App.exe`，
真实视频渲染 + `--selftest` 均验证通过（官方 `_SuppressWinFormsTrimError` 抑制 NETSDK1175）。
`tools/构建全部.ps1` 一键复现内核构建与 DLL 部署。

**验证记录**：
- `dotnet test` 10/10 通过；
- App `--selftest` 真实模式通过（就绪 → 帧步进 41.67ms → 秒步进 → 媒体信息，exit=0）；
- 2 路真实视频同屏渲染确认；
- AOT 单文件 exe 真实渲染 + selftest 通过（21MB）。

### 已实现功能

- **多路对比 1~9 路**（2x2/3x2/3x3 自动网格，点击选中，单屏/多屏切换，数字键 1-9 加路）；
- **双步进**：按帧（←/→）与按秒（Shift+←/→）两组前进/后退，步长可在设置中调整（F12）；
- **同步播放/暂停/停止/Seek/循环**：以第 0 路为 master 的媒体时间同步（SyncController）；
- **二级设置窗口**：硬件解码开关、GPU 选择（多显卡）、步进步长、色彩模式、默认布局、窗口/全屏行为（F25/F26）；
- **全屏模式**（F11）+ 窗口模式，全屏可隐藏工具栏/时间轴；
- **会话保存/加载**（`.3fcs` JSON：文件列表/偏移/布局/位置/循环区间）；
- **快捷键**：Space 播放/暂停、←→ 帧步进、Shift+←→ 秒步进、↑↓ 10 秒、F11 全屏、B A-B滑块、P 探针、O 打开、Esc 退出全屏；
- **对比工具**：像素探针（F19）、A-B 滑块视图（F15）、放大镜（F17）、书签导出与跳转（F22）、
  **截图导出 PNG（F21）**、**差异叠加热力图（F20，可选）**、**媒体信息面板（F3）**、**音频面板（音轨/音量/静音）**、**时间轴 A/B 打点（A/B 键）**；
- **引擎自动探测**：存在 `FFF.Native.dll` 时用真实 3FP 内核，否则回退**演示模式**（合成画面）；
- **真实内核已验证**：FFmpeg + libass + FFF.Native 全链路构建成功，App 真实渲染视频确认（见 README 底部工程状态）。

### 构建与运行

```powershell
# 一键构建内核 + 部署（需 VS 2022+ C++、Git；首次联网下载 FFmpeg/vcpkg）
powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1

# 单元测试
dotnet test  tests/3FCompare.Core.Tests

# 运行应用（有 FFF.Native 时真实模式，无则演示模式）
dotnet run --project src/3FCompare.App

# 演示模式体验（任意文件，合成画面）
dotnet run --project src/3FCompare.App -- --autodemo <文件...>

# E3 冒烟（真实/演示自动切换）
dotnet run --project tests/3FCompare.SmokeTests -- <视频> [更多视频...]
```