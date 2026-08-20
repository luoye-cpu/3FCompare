# 3FCompare 打包脚本 — 2 版本发布（全部 NativeAOT 编译）
# 3FCompare Build & Pack Script — 2 variants (all NativeAOT)
# 用法 / Usage:
#   精简版 (不含 PLAN) / Lite (no PLAN):        .\pack.ps1 -Version "0.1.0" -Mode app
#   完整版 (含 PLAN/ffmpeg-full) / Full (with PLAN): .\pack.ps1 -Version "0.1.0" -Mode full
#   一键全部 2 个版本 / Both variants:         .\pack.ps1 -Version "0.1.0" -Mode all (default)
param(
    [string]$Version = "0.1.0",
    [ValidateSet("app", "full", "all")]
    [string]$Mode = "all",
    [switch]$NoCompress   # 跳过 7z 压缩（调试时快速验证打包逻辑）/ Skip 7z compression (debug)
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = "$ProjectDir\publish"
$BuildDir = "$ProjectDir\publish\build"
$AppProject = "$ProjectDir\src\3FCompare.App\3FCompare.App.csproj"
$PlanSource = "$PublishDir\PLAN"

# 架构
$Arch = "x64"
$Rid = "win-x64"

# ── 包命名 ──
function Get-PackageName([string]$mode) {
    switch ($mode) {
        "app"  { return "3FCompare-v$Version-$Arch" }
        "full" { return "3FCompare-v$Version-$Arch-full" }
    }
    throw "未知模式: $mode / Unknown mode: $mode"
}

# ── 单个版本打包 ──
function Invoke-Pack([string]$mode) {
    $PackageName = Get-PackageName $mode
    $OutputDir = "$BuildDir\$PackageName"
    $ArchivePath = "$PublishDir\$PackageName.7z"

    $modeLabel = switch ($mode) {
        "app"  { "精简版 (NativeAOT, 不含 PLAN) / Lite (NativeAOT, no PLAN)" }
        "full" { "完整版 (NativeAOT + PLAN/ffmpeg-full) / Full (NativeAOT + PLAN)" }
    }
    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  $modeLabel" -ForegroundColor Cyan
    Write-Host "  v$Version | $Arch | $PackageName" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

    # 清理旧产物
    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Step 1: NativeAOT 发布（内嵌 FFF.Native.dll）/ NativeAOT publish (embedded FFF.Native.dll)
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    dotnet publish $AppProject `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:EmbedFffNative=true `
        -p:Version=$Version `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "[$mode] 发布失败 / Publish failed" }
    Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

    # 清理调试符号（.pdb 对用户无意义）/ Remove debug symbols (.pdb)
    $pdbFiles = Get-ChildItem "$OutputDir\*.pdb" -ErrorAction SilentlyContinue
    if ($pdbFiles) {
        $pdbFiles | Remove-Item -Force
        $savedMB = [math]::Round(($pdbFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host "   ✅ 已删除调试符号，节省 ${savedMB}MB / Debug symbols removed, saved ${savedMB}MB" -ForegroundColor Green
    }

    # Step 2/3: 复制 PLAN + FFmpeg 运行时 + 生成使用说明 (仅完整版)
    if ($mode -eq "full") {
        Write-Host "`n[2/4] 复制 PLAN 组件包 / Copy PLAN bundle..." -ForegroundColor Yellow
        # 1) 将 FFmpeg DLL 复制到 exe 同级目录（FFF.Native Delay-Load 必需）
        $FfmpegSrc = "$PublishDir\PLAN\ffmpeg-full"
        if (Test-Path $FfmpegSrc) {
            $ffDlls = @(Get-ChildItem "$FfmpegSrc\*.dll" -ErrorAction SilentlyContinue)
            if ($ffDlls.Count -gt 0) {
                $ffDlls | ForEach-Object { Copy-Item $_.FullName $OutputDir -Force }
                Write-Host "   ✅ FFmpeg DLL 已复制到程序目录（$($ffDlls.Count) 个）" -ForegroundColor Green
            }
        } else {
            Write-Host "   ⚠️ FFmpeg 源目录不存在: $FfmpegSrc / FFmpeg source not found" -ForegroundColor Yellow
        }

        # 2) 复制 PLAN 组件（FFmpeg-full 与使用说明）到 PLAN 子目录
        $PlanDest = "$OutputDir\PLAN"
        if (Test-Path $PlanSource) {
            if (Test-Path $PlanDest) {
                Remove-Item -Recurse -Force $PlanDest -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
            }
            robocopy $PlanSource $PlanDest /E /NFL /NDL /NJH /NJS /NC /NS
            if ($LASTEXITCODE -ge 8) { throw "复制 PLAN 失败 (robocopy exit code: $LASTEXITCODE) / PLAN copy failed" }
            Write-Host "   ✅ PLAN 组件已复制" -ForegroundColor Green
        } else {
        Write-Host "   ⚠️ PLAN 源目录不存在: $PlanSource / PLAN source not found" -ForegroundColor Yellow
        Write-Host "   请先将 FFmpeg DLL 放入 publish\PLAN\ffmpeg-full\ 目录 / Place FFmpeg DLLs in publish\PLAN\ffmpeg-full\" -ForegroundColor Yellow
        }

        Write-Host "`n[3/4] 生成使用说明 / Generate usage guide..." -ForegroundColor Yellow
        $ReadmePath = "$PlanDest\使用说明.txt"
        $content = @"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v${Version} — 使用说明 / Usage Guide
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求 / Requirements
  • Windows 10/11 或更高版本 / or later
  • 无需安装任何运行环境（NativeAOT 独立编译）/ No runtime required (NativeAOT standalone)

🚀 快速开始 / Quick Start
  1. 解压所有文件到任意目录（保持文件夹结构完整）/ Extract to any directory
  2. 双击运行 3FCompare.App.exe / Double-click 3FCompare.App.exe
  3. 拖入视频文件即可开始对比 / Drag video files to start comparison

📁 文件结构 / File Structure
  3FCompare.App.exe    — 主程序（NativeAOT 单文件，内嵌播放器内核）
                         Main executable (NativeAOT, embedded player kernel)
  av*.dll / sw*.dll    — FFmpeg 编解码引擎（已在程序目录，无需配置）/ FFmpeg decoding engine (in program directory, no configuration needed)
  ass-9.dll            — 字幕渲染引擎 / Subtitle rendering engine
  PLAN/ffmpeg-full/    — FFmpeg 归档副本（备份，通常无需使用）/ FFmpeg archive copy (backup, usually not needed)

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

❓ 常见问题 / FAQ
  Q: 提示"FFmpeg 不可用"？/ "FFmpeg unavailable"?
  A: 完整版已内置 FFmpeg（DLL 在程序目录），通常不会出现此提示。
     Full version includes FFmpeg DLLs in the program directory.
     若出现，请打开设置（F25）→ FFmpeg 路径 → 选择程序目录或 PLAN/ffmpeg-full/，
     点击"测试探测"验证后保存。
     Otherwise, open Settings (F25) → FFmpeg Path → select program dir or PLAN/ffmpeg-full/,
     click "Test" to verify, then save.

  Q: 精简版（不含 PLAN/ffmpeg-full）如何播放视频？/ How to play video in the lite version?
  A: 精简版不含 FFmpeg，需要自行获取 FFmpeg DLL 放到程序目录，
     或在设置中指定包含 avcodec-*.dll 的目录。
     The lite version does not include FFmpeg; obtain FFmpeg DLLs yourself and place them
     in the program directory, or configure the path in Settings.

  Q: 迁移到其他电脑？/ Migrate to another PC?
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。
     Copy the entire folder (portable, no .NET runtime required).

📞 反馈与交流 / Feedback
  GitHub: https://github.com/luoye-cpu/3FCompare

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本 Version: v${Version} | 架构 Arch: ${Arch} | 构建日期 Build: $((Get-Date -Format "yyyy-MM-dd"))
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
"@
        Set-Content -Path $ReadmePath -Value $content -Encoding UTF8
        Write-Host "   ✅ 使用说明已生成 → $ReadmePath" -ForegroundColor Green
    } else {
        Write-Host "`n[2/4] 跳过 (精简版不含 PLAN) / Skipped (lite, no PLAN)" -ForegroundColor Yellow
        Write-Host "`n[3/4] 跳过 (精简版不生成使用说明) / Skipped (lite, no usage guide)" -ForegroundColor Yellow
    }

    # Step 4: 压缩 / Archive
    Write-Host "`n[4/4] 压缩打包 / Archiving..." -ForegroundColor Yellow
    if ($NoCompress) {
        Write-Host "   ⏭️ 已跳过压缩 (-NoCompress) / Compression skipped" -ForegroundColor Yellow
        Write-Host "   产物目录 / Output directory: $OutputDir" -ForegroundColor Yellow
        return
    }
    $sevenZip = Get-Command "7z" -ErrorAction SilentlyContinue
    if (-not $sevenZip -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) {
        $sevenZip = [pscustomobject]@{ Source = "C:\Program Files\7-Zip\7z.exe" }
    }
    if ($sevenZip) {
        Remove-Item $ArchivePath -Force -ErrorAction SilentlyContinue
        $zipArgs = 'a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 "' + $ArchivePath + '" *'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $sevenZip.Source
        $psi.Arguments = $zipArgs
        $psi.WorkingDirectory = $OutputDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $false
        $proc = [System.Diagnostics.Process]::Start($psi)
        try { $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { throw "压缩失败 (7z exit code: $($proc.ExitCode)) / Archive failed" }
        Write-Host "   ✅ 压缩完成 → $ArchivePath / Archive created" -ForegroundColor Green
    } else {
        Write-Host "   ⚠️ 未找到 7z 命令，跳过压缩 / 7z not found, skipping compression" -ForegroundColor Yellow
        Write-Host "   手动压缩目录 / Manual archive: $OutputDir" -ForegroundColor Yellow
    }

    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  [$mode] 打包完成 / Pack complete!" -ForegroundColor Green
    Write-Host "  产物目录 / Output: $OutputDir" -ForegroundColor Green
    if (Test-Path $ArchivePath) {
        $size = (Get-Item $ArchivePath).Length / 1MB
        Write-Host "  压缩包 / Archive: $ArchivePath ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    }
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
}

# ── 主入口 ──
switch ($Mode) {
    "app"  { Invoke-Pack "app" }
    "full" { Invoke-Pack "full" }
    "all"  { Invoke-Pack "app"; Invoke-Pack "full" }
}

Write-Host "`n✅ 全部打包完成! / All packs complete!" -ForegroundColor Green