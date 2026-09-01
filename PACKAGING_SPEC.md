# 📦 3FCompare 打包规范 / Packaging Specification

> 版本 / Version: 1.1 | 最后更新 / Last updated: 2026-08-19 | 适用于 / Applies to: v0.1.0+

---

## 一、发布产物命名规则 / Release Artifact Naming Convention

### 命名格式 / Naming Format

```
3FCompare-v{Version}-{Arch}[-{Variant}].{Ext}
```

| 字段 / Field | 说明 / Description | 示例 / Example |
|------|------|------|
| `Version` | 三位语义化版本号 / Semantic version | 0.1.0 |
| `Arch` | CPU 架构标识 / Architecture | x64 |
| `Variant` | 可选。`full`=含 PLAN/ffmpeg-full；省略=精简版（不含 PLAN） / Optional, `full` for PLAN bundle, omit for lite | full |
| `Ext` | 压缩格式 / Archive format | 7z |

> **注意 / Note**: 所有版本统一使用 **NativeAOT 编译**（无 .NET Runtime 依赖），两版本区别仅是否包含 `PLAN/` 文件夹。 / All variants use **NativeAOT compilation** (no .NET runtime dependency); the only difference is the presence of the `PLAN/` folder.

### 命名示例 / Naming Examples

| 产物 / Artifact | 名称 / Name |
|------|------|
| Windows x64 精简版（NativeAOT，不含 PLAN） / Lite (NativeAOT, no PLAN) | `3FCompare-v0.1.0-x64.7z` |
| Windows x64 完整版（NativeAOT + PLAN/ffmpeg-full） / Full (NativeAOT + PLAN/ffmpeg-full) | `3FCompare-v0.1.0-x64-full.7z` |

---

## 二、PLAN 文件夹结构规范 / PLAN Folder Structure

外部工具组件统一放入 `PLAN/` 子目录，程序启动后在设置中指向对应目录。
External tool components are placed in the `PLAN/` subdirectory; the app points to it in Settings.

### 2.1 目录结构 / Directory Structure

```
3FCompare-v0.1.0-x64-full/
├── 3FCompare.exe                    ← 主程序（NativeAOT 单文件，内嵌 FFF.Native）
├── PLAN/                                ← 外部组件根目录
│   ├── ffmpeg-full/                     ← FFmpeg 预编译 DLL 包（内核配套版本）
│   │   ├── avcodec-63.dll               ← FFmpeg 编解码库
│   │   ├── avformat-63.dll              ← FFmpeg 封装格式库
│   │   ├── avfilter-12.dll              ← FFmpeg 滤镜库
│   │   ├── avutil-61.dll                ← FFmpeg 工具库
│   │   ├── swresample-7.dll             ← FFmpeg 音频重采样库
│   │   ├── swscale-10.dll               ← FFmpeg 图像缩放/色彩转换库
│   │   └── ass-9.dll                    ← libass 字幕渲染库
│   └── 使用说明.txt                      ← 用户使用指南（打包时自动生成）
└── README.md
```

### 2.2 PLAN 子目录识别规则 / PLAN Subdirectory Detection

程序按以下方式识别 / The app detects PLAN subdirectories as follows:

| 子目录 / Subdirectory | 识别条件 / Detection Condition | 使用方式 / Usage |
|--------|---------|---------|
| `PLAN/ffmpeg-full/` | 目录存在且含 `avcodec-*.dll` / Exists and contains `avcodec-*.dll` | 设置 F25 → FFmpeg 路径指向此目录 / Point F25 → FFmpeg Path to this directory |

> **重要 / Important**: 用户手动设置的 FFmpeg 路径优先级高于 PLAN 自动检测。 / User-configured FFmpeg path takes priority over PLAN auto-detection.

---

## 三、打包流程 / Build & Packaging Flow

### 3.1 前置准备 / Prerequisites

```powershell
# 1. 版本号由 pack.ps1 -p:Version 统一控制（自动覆盖 csproj 中的 <Version>），
#    无需手动修改 csproj；其中 <Version>/<VersionSuffix> 仅为未传参时的默认值
#    （SDK 会从 Version+Suffix 自动派生 AssemblyVersion/FileVersion/InformationalVersion）
#    Version is controlled by pack.ps1 -p:Version (overrides csproj <Version>);
#    csproj defaults are only fallbacks.

# 2. 确保 FFF.Native 内核已构建（third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll）
#    合并使用 tools/构建全部.ps1 一键构建
#    Ensure FFF.Native is built (use tools/构建全部.ps1 for one-click)

# 3. 准备 PLAN 组件包（third_party/fff_project/runtime/*.dll + ass-9.dll）
#    Prepare PLAN component package
```

### 3.2 发布命令 / Publish Commands

推荐使用 pack.ps1，一键 2 版本，全部 NativeAOT / Recommended: use pack.ps1 for both variants in one command:

```powershell
# 精简版（NativeAOT，不含 PLAN）/ Lite (NativeAOT, no PLAN)
.\pack.ps1 -Version "0.1.0" -Mode app

# 完整版（NativeAOT + PLAN 组件包）/ Full (NativeAOT + PLAN bundle)
.\pack.ps1 -Version "0.1.0" -Mode full

# 一键全部 2 个版本（默认）/ Both variants (default)
.\pack.ps1 -Version "0.1.0" -Mode all
```

手动发布命令 / Manual publish (equivalent to pack.ps1):

```powershell
# Windows x64 精简版（NativeAOT，内嵌 FFF.Native）/ Lite (NativeAOT, embedded FFF.Native)
dotnet publish src/3FCompare/3FCompare.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:EmbedFffNative=true `
    -o publish/build/3FCompare-v0.1.0-x64/

# Windows x64 完整版（NativeAOT，另需复制 PLAN/ 目录）/ Full (NativeAOT, plus PLAN/ copy)
dotnet publish src/3FCompare/3FCompare.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:EmbedFffNative=true `
    -o publish/build/3FCompare-v0.1.0-x64-full/
```

### 3.3 组装完整包 / Assemble Full Package

```powershell
# 将 PLAN 文件夹复制到完整包目录 / Copy PLAN folder into full package directory
robocopy publish\PLAN publish\build\3FCompare-v0.1.0-x64-full\PLAN /E

# 生成使用说明文档（见第四章）/ Generate usage guide (see §4)
# → 输出到 / Output to publish\build\3FCompare-v0.1.0-x64-full\PLAN\使用说明.txt
```

### 3.4 压缩打包 / Archive

```powershell
# 7-Zip 极限压缩（实测最优配置）/ Maximum compression (tested optimal)
#   -mx9       极限等级 / Maximum compression level
#   -md=3840m  字典 3840 MiB（LZMA2 上限）/ Dictionary size (LZMA2 max)
#   -mfb=273   单词大小上限 / Word size max
#   -ms=on     固实压缩 / Solid archive
#   -mmt=1     单线程（压缩率最高）/ Single thread (best ratio)
7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\3FCompare-v0.1.0-x64.7z `
    publish\build\3FCompare-v0.1.0-x64\*

7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\3FCompare-v0.1.0-x64-full.7z `
    publish\build\3FCompare-v0.1.0-x64-full\*
```

**进程优先级 / Process Priority (auto-maximized)**

```powershell
function Invoke-7zMax {
    param([string]$Arguments)
    $exe = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $exe -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) { $exe = "C:\Program Files\7-Zip\7z.exe" }
    if (-not $exe) { throw "未找到 7z.exe / 7z not found" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }
    $p.WaitForExit(); return $p.ExitCode
}
Invoke-7zMax 'a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 "out.7z" *'
```

---

## 四、使用说明文档自动生成 / Auto-generated Usage Guide

每次打包时，自动生成 `PLAN/使用说明.txt`，包含以下内容 / Generated automatically during each pack:

### 4.1 模板 / Template

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v{VERSION} — 使用说明 / Usage Guide
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求 / Requirements
  • Windows 10/11 或更高版本 / or later
  • 无需安装任何运行环境（NativeAOT 独立编译）/ No runtime required (NativeAOT standalone)

🚀 快速开始 / Quick Start
  1. 解压所有文件到任意目录 / Extract all files to any directory
  2. 双击运行 3FCompare.exe / Double-click 3FCompare.exe
  3. 首次使用：打开设置（F25）→ FFmpeg 路径 → 指向 PLAN/ffmpeg-full/
     First run: Settings (F25) → FFmpeg Path → point to PLAN/ffmpeg-full/
  4. 拖入视频文件即可开始对比 / Drag video files to start comparison

📁 文件结构 / File Structure
  3FCompare.exe  — 主程序（NativeAOT 单文件，内嵌播放器内核）
                       Main executable (NativeAOT, embedded player kernel)
  PLAN/              — 外部组件包（完整版含）/ External component bundle (full version)
    └── ffmpeg-full/ — FFmpeg 编解码引擎（必需）/ FFmpeg decoding engine (required)

⌨️ 快捷键 / Shortcuts
  Space            播放/暂停 Play/Pause
  ←→               帧步进 Frame step
  Shift+←→         秒步进 Second step
  ↑↓               10 秒步进 10s step
  F11              全屏切换 Fullscreen
  F25              设置 Settings
  B                设置 A/B 循环打点 A/B Loop markers
  P                像素探针 Pixel probe
  Ctrl+S           导出当前帧 PNG Export frame as PNG

🖼️ 支持的格式 / Supported Formats
  输入 Input: 所有 FFmpeg 支持的视频格式（MP4, MKV, AVI, MOV, WebM, FLV 等）
  All FFmpeg-supported video formats
  输出 Output: 当前帧截图 PNG（Ctrl+S）/ Frame screenshot PNG

❓ 常见问题 / FAQ
  Q: 提示"FFmpeg 不可用"？/ "FFmpeg unavailable"?
  A: 完整版已内置 FFmpeg（DLL 在程序目录），通常不会出现此提示。
     Full version includes FFmpeg DLLs in the program directory.
     若出现，请打开设置（F25）→ FFmpeg 路径 → 选择程序目录或 PLAN/ffmpeg-full/，
     点击"测试探测"验证后保存。
     Otherwise, open Settings (F25) → FFmpeg Path → select program dir or PLAN/ffmpeg-full/,
     click "Test" to verify, then save.

  Q: 如何更新 FFmpeg？/ How to update FFmpeg?
  A: 替换 PLAN/ffmpeg-full/ 下的 DLL 文件即可，
     注意版本号应与内核匹配（avcodec-63 / avformat-63 / avutil-61）。
     Replace the DLLs in PLAN/ffmpeg-full/; ensure version matches the kernel.

  Q: 迁移到其他电脑？/ Migrate to another PC?
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。
     Copy the entire folder (portable, no .NET runtime required).

📞 反馈与交流 / Feedback
  GitHub: https://github.com/luoye-cpu/3FCompare

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本 Version: v{VERSION} | 架构 Arch: {ARCH} | 构建日期 Build: {DATE}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### 4.2 生成时机 / Generation Timing

- 每次执行打包脚本时自动生成 / Generated automatically during each pack
- 占位符 `{VERSION}`, `{ARCH}`, `{DATE}` 从 `.csproj` 和系统获取 / Placeholders from `.csproj` and system

---

## 五、版本号管理 / Version Management

### 5.1 版本号位置 / Version Location

`src/3FCompare/3FCompare.csproj`:
```xml
<Version>0.2.0</Version>
<VersionSuffix>BETA</VersionSuffix>
```

### 5.2 更新流程 / Update Workflow

1. 修改 `.csproj` 中的 `<Version>` 标签 / Update `<Version>` in `.csproj`
2. 更新 `README.md` 中的版本号 / Update version in `README.md`
3. 执行打包流程 / Run packaging (`pack.ps1`)
4. 在 GitHub Releases 中创建对应 tag: `vX.Y.Z` / Create a GitHub Release with the tag

---

## 六、精简包 vs 完整包 / Lite vs Full Variant

| 特性 / Feature | 精简包 Lite (`-x64.7z`) | 完整包 Full (`-x64-full.7z`) |
|------|:--:|:--:|
| 主程序（内嵌 FFF.Native）/ Main executable (embedded FFF.Native) | ✅ | ✅ |
| PLAN 文件夹 / PLAN folder | ❌ | ✅ |
| FFmpeg 解码库 / FFmpeg decoding libraries | ❌（需用户自行配置 / User must provide） | ✅（开箱即用 / Ready to use） |
| 压缩后体积 / Compressed size | ~10 MB | ~60 MB |
| 适用场景 / Use case | 已有 FFmpeg 环境的用户 / Users with existing FFmpeg | 新用户 / 便携使用 / New users / Portable use |

---

> 📅 本规范自 v0.1.0 起生效 / This spec is effective from v0.1.0.