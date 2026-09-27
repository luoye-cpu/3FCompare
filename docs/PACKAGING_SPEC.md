# 📦 3FCompare 打包规范 / Packaging Specification

> 版本 / Version: 1.1 | 最后更新 / Last updated: 2026-08-19 | 适用于 / Applies to: v0.1.0+
>
> **当前项目版本 / Current project version: `v0.3.1-beta`**（2026-09-27 核对：两个 csproj 均为
> `<Version>0.3.1-beta</Version>`，`<VersionSuffix>` 已按 docs/45 P1-12 移除；git tag `v0.3.1-beta`
> 随本次发布创建。⚠ `v0.3.0-beta` **从未打过 tag**——0.3.0 的包与文档都在，但没有可回溯的标签，
> 本次只补 0.3.1，不追溯给旧提交打标签（那会让"tag 指向发布时的树"这条约定失真）。上一有 tag 的版本为 `v0.2.5`。

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
3FCompare-v0.3.0-beta-x64-full/
├── 3FCompare.exe                ← 主程序（NativeAOT 单文件，内嵌播放器内核）
├── SHA256SUMS                   ← 包内每个文件的 sha256（用户可 `sha256sum -c` 自证）
├── 使用说明.txt                  ← 用户使用指南（打包时自动生成，位于**包根**，不在子目录里）
└── ffmpeg-full/                 ← FFmpeg 预编译 DLL 包（内核配套版本，14 个）
    ├── avcodec-63.dll           ← FFmpeg 编解码库（引擎以它为探测依据）
    ├── avformat-63.dll          ← FFmpeg 封装格式库
    ├── avfilter-12.dll          ← FFmpeg 滤镜库
    ├── avutil-61.dll            ← FFmpeg 工具库
    ├── avdevice-63.dll          ← FFmpeg 设备库
    ├── swresample-7.dll         ← FFmpeg 音频重采样库
    ├── swscale-10.dll           ← FFmpeg 图像缩放/色彩转换库
    ├── ass-9.dll                ← libass 字幕渲染库
    └── libc++.dll / libomp.dll / libunwind.dll / libwinpthread-1.dll
        / libSPIRV-Tools-shared.dll / libshaderc_shared.dll
                                 ← 上述 FFmpeg/字幕库的间接依赖（缺一即加载失败）
```

> ⚠ **2026-09-22 修订（docs/45 P1-12）**：旧的这一节画的是 `PLAN/ffmpeg-full/` 与包内 `README.md`，
> 与实际产物不符——`PLAN\` 只是 `pack.ps1` 的**中间暂存目录**（`publish\PLAN`），
> 不会出现在发行包里；`README.md` 也从不分发。照旧文档排查"包里缺东西"必然找错位置。
> 唯一真源是 `SHA256SUMS`：包内容 == 目录内容 == 清单（三条不变式，见 §5）。

### 2.2 FFmpeg 目录识别规则 / FFmpeg Directory Detection

程序按以下方式识别 / The app detects the FFmpeg directory as follows:

| 目录 / Directory | 识别条件 / Detection Condition | 使用方式 / Usage |
|--------|---------|---------|
| `ffmpeg-full/` | 目录存在且含 `avcodec-*.dll` / Exists and contains `avcodec-*.dll` | FFmpeg 路径指向此目录 / Point FFmpeg path to this directory |

> **重要 / Important**: 用户手动设置的 FFmpeg 路径优先级高于自动检测。 / User-configured FFmpeg path takes priority over auto-detection.

---

## 三、打包流程 / Build & Packaging Flow

### 3.1 前置准备 / Prerequisites

```powershell
# 1. 版本号唯一真源 = src/3FCompare/3FCompare.csproj 的 <Version>（不传 -Version 时自动取它）。
#    pack.ps1 把它以 -p:Version 传给 dotnet publish，全链路生效；<VersionSuffix> **已移除**
#    （docs/45 P1-12）——带 -BETA 会让门禁/开发的 plain build 与发布包出现两个版本叙事。
#    Version source of truth = csproj <Version>; pack.ps1 passes it via -p:Version.
#    <VersionSuffix> was removed: "-BETA" gave dev builds and release packages different versions.

# 2. 确保播放器内核已构建（FFF.Native.dll）并放到 third_party 的约定位置；
#    csproj 的 KernelDllPath 指向的那份会被嵌进 exe（发布门禁 [1b/11] 校验这一点）。
#    Ensure the player kernel is built and placed where csproj KernelDllPath points.

# 3. 准备 FFmpeg 组件包：DLL 放入 publish\PLAN\ffmpeg-full\（pack.ps1 的中间暂存目录，
#    不是发行包内的路径——见 §2.1）。完整版缺了它会在打包时**直接失败**，不静默降级。
#    Prepare the FFmpeg bundle under publish\PLAN\ffmpeg-full\ (staging dir, NOT the
#    in-package layout). A missing bundle fails the full-mode build instead of degrading.
```

### 3.2 发布命令 / Publish Commands

推荐使用 pack.ps1，一键 2 版本，全部 NativeAOT / Recommended: use pack.ps1 for both variants in one command:

```powershell
# ⚠ 一律**不传** -Version：脚本从 csproj 真源派生（§5.2 第 3 条）。
#    显式传入且与 csproj 不一致只会得到一条告警——历史上 0.2.1 误发就是这么来的。

# 精简版（NativeAOT，不含 PLAN）/ Lite (NativeAOT, no PLAN)
.\pack.ps1 -Mode app

# 完整版（NativeAOT + PLAN 组件包）/ Full (NativeAOT + PLAN bundle)
.\pack.ps1 -Mode full

# 一键全部 2 个版本（默认）/ Both variants (default)
.\pack.ps1 -Mode all
```

手动发布命令 / Manual publish (equivalent to pack.ps1):

```powershell
# Windows x64 精简版（NativeAOT）/ Lite (NativeAOT)
dotnet publish src/3FCompare/3FCompare.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:WgcCaptureRequired=true `
    -p:Version=0.3.0-beta -p:VersionSuffix= `
    --no-restore `
    -o publish/build/3FCompare-v0.3.0-beta-x64/

# Windows x64 完整版：publish 命令**完全相同**，差别只在随后把 ffmpeg-full\ 拷进产物目录
# Full: identical publish command; the only difference is copying ffmpeg-full\ afterwards (§3.3)
dotnet publish src/3FCompare/3FCompare.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:WgcCaptureRequired=true `
    -p:Version=0.3.0-beta -p:VersionSuffix= `
    --no-restore `
    -o publish/build/3FCompare-v0.3.0-beta-x64-full/

> ⚠ **2026-09-22 修订**：旧文档写的 `-p:EmbedFffNative=true` 在**全仓无任何引用**
> （`grep -rn EmbedFffNative` 只剩历史文档里的一句迁移记录），是无效开关，照抄不会报错、
> 也不会生效——典型的"看起来配置了其实没有"。
> `-p:WgcCaptureRequired=true` 才是真实存在且发布路径**必须**带的：缺 WGC 抓屏库时
> 它让构建直接 Error，否则用户拿到的是"窗口被遮挡就抓到遮挡物"的静默降级版。
```

### 3.3 组装完整包 / Assemble Full Package

```powershell
# 把 ffmpeg-full 拷到产物目录（**包内是 ffmpeg-full\，不是 PLAN\ffmpeg-full\**）
# Copy ffmpeg-full into the output dir (in-package path is ffmpeg-full\, NOT PLAN\ffmpeg-full\)
robocopy publish\PLAN\ffmpeg-full publish\build\3FCompare-v0.3.0-beta-x64-full\ffmpeg-full /E

# 生成使用说明文档（见第四章）/ Generate usage guide (see §4)
# → 输出到**包根** / Output to the package ROOT:
#     publish\build\3FCompare-v0.3.0-beta-x64-full\使用说明.txt
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

`src/3FCompare/3FCompare.csproj`（**唯一真源** / single source of truth）:
```xml
<Version>0.3.0-beta</Version>
```

> **2026-09-23 校正**：`<VersionSuffix>` 自 0.2.5 那轮起已移除（docs/45 P1-12）——带独立后缀会让
> 门禁/开发的 plain build 与发布包产出两个版本叙事。需要预发布时把后缀写进 `<Version>` 本身
> （`0.3.0-beta`），`pack.ps1` 的 `-p:VersionSuffix=` 清空动作因此不再影响版本字符串。
> **2026-09-16 校正**：本节此前仍写着 `0.2.0`，与 csproj 真源严重脱节 —— 正是本节 §5.2 想防止的
> "README/规范与 csproj 不一致"问题本身。改版本时**本处必须与 csproj 同步**。

`pack.ps1` 与 `tools/发布门禁.ps1` **不再硬编码版本号**：不传 `-Version` 时自动读取上面这一行；
显式传入且与 csproj 不一致会告警（防止误发旧包——历史上出现过 `publish/` 里躺着 0.2.1 产物、
而 README 与 csproj 仍是 0.2.0 的情况）。

### 5.2 更新流程 / Update Workflow

1. 修改 `src/3FCompare/3FCompare.csproj` 的 `<Version>`（及 `3FCompare.Core.csproj`，两者保持一致）
2. 更新 `README.md` 中的版本号与更新日志 / Update version + changelog in `README.md`
3. 打包时**不要**手动传 `-Version`，让它从 csproj 派生 / Do not pass `-Version` manually
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