# 📦 3FCompare 打包规范

> 版本: 1.1 | 最后更新: 2026-08-19 | 适用于 v0.1.0+

---

## 一、发布产物命名规则

### 命名格式

```
3FCompare-v{Version}-{Arch}[-{Variant}].{Ext}
```

| 字段 | 说明 | 示例 |
|------|------|------|
| `Version` | 三位语义化版本号 | 0.1.0 |
| `Arch` | CPU 架构标识 | x64 |
| `Variant` | 可选。`full`=含 PLAN/ffmpeg-full；省略=精简版（不含 PLAN） | full |
| `Ext` | 压缩格式 | 7z |

> **注意**: 所有版本统一使用 **NativeAOT 编译**（无 .NET Runtime 依赖），两版本区别仅是否包含 `PLAN/` 文件夹。

### 命名示例

| 产物 | 名称 |
|------|------|
| Windows x64 精简版（NativeAOT，不含 PLAN） | `3FCompare-v0.1.0-x64.7z` |
| Windows x64 完整版（NativeAOT + PLAN/ffmpeg-full） | `3FCompare-v0.1.0-x64-full.7z` |

---

## 二、PLAN 文件夹结构规范

外部工具组件统一放入 `PLAN/` 子目录，程序启动后在设置中指向对应目录。

### 2.1 目录结构

```
3FCompare-v0.1.0-x64-full/
├── 3FCompare.App.exe                    ← 主程序（NativeAOT 单文件，内嵌 FFF.Native）
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

### 2.2 PLAN 子目录识别规则

程序按以下方式识别：

| 子目录 | 识别条件 | 使用方式 |
|--------|---------|---------|
| `PLAN/ffmpeg-full/` | 目录存在且含 `avcodec-*.dll` | 设置 F25 → FFmpeg 路径指向此目录 |

> **重要**: 用户手动设置的 FFmpeg 路径优先级高于 PLAN 自动检测。

---

## 三、打包流程

### 3.1 前置准备

```powershell
# 1. 版本号由 pack.ps1 -p:Version 统一控制（自动覆盖 csproj 中的 <Version>），
#    无需手动修改 csproj；其中 <Version>/<VersionSuffix> 仅为未传参时的默认值
#    （SDK 会从 Version+Suffix 自动派生 AssemblyVersion/FileVersion/InformationalVersion）

# 2. 确保 FFF.Native 内核已构建（third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll）
# 合并使用 tools/构建全部.ps1 一键构建

# 3. 准备 PLAN 组件包（third_party/fff_project/runtime/*.dll + ass-9.dll）
```

### 3.2 发布命令（推荐使用 pack.ps1，一键 2 版本，全部 NativeAOT）

```powershell
# 精简版（NativeAOT，不含 PLAN）
.\pack.ps1 -Version "0.1.0" -Mode app

# 完整版（NativeAOT + PLAN 组件包）
.\pack.ps1 -Version "0.1.0" -Mode full

# 一键全部 2 个版本（默认）
.\pack.ps1 -Version "0.1.0" -Mode all
```

手动发布命令（与 pack.ps1 等价）：

```powershell
# Windows x64 精简版（NativeAOT，内嵌 FFF.Native）
dotnet publish src/3FCompare.App/3FCompare.App.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:EmbedFffNative=true `
    -o publish/build/3FCompare-v0.1.0-x64/

# Windows x64 完整版（NativeAOT，另需复制 PLAN/ 目录）
dotnet publish src/3FCompare.App/3FCompare.App.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:EmbedFffNative=true `
    -o publish/build/3FCompare-v0.1.0-x64-full/
```

### 3.3 组装完整包

```powershell
# 将 PLAN 文件夹复制到完整包目录
robocopy publish\PLAN publish\build\3FCompare-v0.1.0-x64-full\PLAN /E

# 生成使用说明文档（见第四章）
# → 输出到 publish\build\3FCompare-v0.1.0-x64-full\PLAN\使用说明.txt
```

### 3.4 压缩打包

```powershell
# 7-Zip 极限压缩（实测最优配置）
#   -mx9       极限等级；7z 按文件架构自动加 BCJ/BCJ2 过滤器
#   -md=3840m  字典 3840 MiB（LZMA2 上限）
#   -mfb=273   单词大小上限
#   -ms=on     固实压缩
#   -mmt=1     单线程（压缩率最高）
7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\3FCompare-v0.1.0-x64.7z `
    publish\build\3FCompare-v0.1.0-x64\*

7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\3FCompare-v0.1.0-x64-full.7z `
    publish\build\3FCompare-v0.1.0-x64-full\*
```

**进程优先级（自动最高）**

```powershell
function Invoke-7zMax {
    param([string]$Arguments)
    $exe = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $exe -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) { $exe = "C:\Program Files\7-Zip\7z.exe" }
    if (-not $exe) { throw "未找到 7z.exe" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }
    $p.WaitForExit(); return $p.ExitCode
}
Invoke-7zMax 'a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 "out.7z" *'
```

---

## 四、使用说明文档自动生成

每次打包时，自动生成 `PLAN/使用说明.txt`，包含以下内容：

### 4.1 模板

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v{VERSION} — 使用说明
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求
  • Windows 10/11 或更高版本
  • 无需安装任何运行环境（NativeAOT 独立编译）

🚀 快速开始
  1. 解压所有文件到任意目录
  2. 双击运行 3FCompare.App.exe
  3. 首次使用：打开设置（F25）→ FFmpeg 路径 → 指向 PLAN/ffmpeg-full/
  4. 拖入视频文件即可开始对比
  5. 完整版：设置已预填 PLAN/ffmpeg-full，确认保存即可

📁 文件结构
  3FCompare.App.exe  — 主程序（NativeAOT 单文件，内嵌播放器内核）
  PLAN/              — 外部组件包（完整版含）
    └── ffmpeg-full/ — FFmpeg 编解码引擎（必需）

⌨️ 快捷键
  Space            播放/暂停
  ←→               帧步进
  Shift+←→         秒步进
  ↑↓               10 秒步进
  F11              全屏切换
  F25              设置
  B                设置 A/B 循环打点
  P                像素探针
  Ctrl+S           导出当前帧 PNG

🖼️ 支持的格式
  输入: 所有 FFmpeg 支持的视频格式（MP4, MKV, AVI, MOV, WebM, FLV 等）
  输出: 当前帧截图 PNG（Ctrl+S）

❓ 常见问题
  Q: 提示"未检测到 FFF.Native"？
  A: 确保 3FCompare.App.exe 所在目录有 FFF.Native.dll。
     完整版中 exe 已内嵌 FFF.Native，首次运行自动释放。

  Q: 提示"FFmpeg 不可用"？
  A: 打开设置（F25）→ FFmpeg 路径 → 浏览选择 PLAN/ffmpeg-full/ 目录，
     点击"测试探测"验证可用性后保存。

  Q: 如何更新 FFmpeg？
  A: 替换 PLAN/ffmpeg-full/ 下的 DLL 文件即可，
     注意版本号应与内核匹配（avcodec-63 / avformat-63 / avutil-61）。

  Q: 迁移到其他电脑？
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。

📞 反馈与交流
  GitHub: https://github.com/luoye-cpu/3FCompare

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本: v{VERSION} | 架构: {ARCH} | 构建日期: {DATE}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### 4.2 生成时机

- 每次执行打包脚本时自动生成
- 占位符 `{VERSION}`, `{ARCH}`, `{DATE}` 从 `.csproj` 和系统获取

---

## 五、版本号管理

### 5.1 版本号位置

`src/3FCompare.App/3FCompare.App.csproj`:
```xml
<Version>0.1.0</Version>
<AssemblyVersion>0.1.0.0</AssemblyVersion>
<FileVersion>0.1.0.0</FileVersion>
<InformationalVersion>0.1.0-BETA</InformationalVersion>
```

### 5.2 更新流程

1. 修改 `.csproj` 中的 `<Version>` 标签
2. 更新 `README.md` 中的版本号
3. 执行打包流程
4. 在 GitHub Releases 中创建对应 tag: `v0.1.0`

---

## 六、精简包 vs 完整包

| 特性 | 精简包 (`-x64.7z`) | 完整包 (`-x64-full.7z`) |
|------|:--:|:--:|
| 主程序（内嵌 FFF.Native） | ✅ | ✅ |
| PLAN 文件夹 | ❌ | ✅ |
| FFmpeg 解码库 | ❌（需用户自行配置） | ✅（开箱即用） |
| 压缩后体积 | ~10 MB | ~60 MB |
| 适用场景 | 已有 FFmpeg 环境的用户 | 新用户 / 便携使用 |

---

> 📅 本规范自 v0.1.0 起生效。