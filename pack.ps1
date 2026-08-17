# 3FCompare 打包脚本 — 2 版本发布（全部 NativeAOT 编译）
# 用法:
#   精简版 (不含 PLAN):        .\pack.ps1 -Version "0.1.0" -Mode app
#   完整版 (含 PLAN/ffmpeg-full): .\pack.ps1 -Version "0.1.0" -Mode full
#   一键全部 2 个版本:         .\pack.ps1 -Version "0.1.0" -Mode all (默认)
param(
    [string]$Version = "0.1.0",
    [ValidateSet("app", "full", "all")]
    [string]$Mode = "all",
    [switch]$NoCompress   # 跳过 7z 压缩（调试时快速验证打包逻辑）
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
    throw "未知模式: $mode"
}

# ── 单个版本打包 ──
function Invoke-Pack([string]$mode) {
    $PackageName = Get-PackageName $mode
    $OutputDir = "$BuildDir\$PackageName"
    $ArchivePath = "$PublishDir\$PackageName.7z"

    $modeLabel = switch ($mode) {
        "app"  { "精简版 (NativeAOT, 不含 PLAN)" }
        "full" { "完整版 (NativeAOT + PLAN/ffmpeg-full)" }
    }
    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  $modeLabel" -ForegroundColor Cyan
    Write-Host "  v$Version | $Arch | $PackageName" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

    # 清理旧产物
    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Step 1: NativeAOT 发布（内嵌 FFF.Native.dll）
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    dotnet publish $AppProject `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:EmbedFffNative=true `
        -p:Version=$Version `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "[$mode] 发布失败" }
    Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

    # 清理调试符号（.pdb 对用户无意义）
    $pdbFiles = Get-ChildItem "$OutputDir\*.pdb" -ErrorAction SilentlyContinue
    if ($pdbFiles) {
        $pdbFiles | Remove-Item -Force
        $savedMB = [math]::Round(($pdbFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host "   ✅ 已删除调试符号，节省 ${savedMB}MB" -ForegroundColor Green
    }

    # Step 2/3: 复制 PLAN + FFmpeg 运行时 + 生成使用说明 (仅完整版)
    if ($mode -eq "full") {
        Write-Host "`n[2/4] 复制 PLAN 组件包..." -ForegroundColor Yellow
        # 1) 将 FFmpeg DLL 复制到 exe 同级目录（FFF.Native Delay-Load 必需）
        $FfmpegSrc = "$PublishDir\PLAN\ffmpeg-full"
        if (Test-Path $FfmpegSrc) {
            $ffDlls = Get-ChildItem "$FfmpegSrc\*.dll" -ErrorAction SilentlyContinue
            if ($ffDlls) {
                $ffDlls | ForEach-Object { Copy-Item $_.FullName $OutputDir -Force }
                Write-Host "   ✅ FFmpeg DLL 已复制到程序目录（${count} 个）" -ForegroundColor Green
            }
        } else {
            Write-Host "   ⚠️ FFmpeg 源目录不存在: $FfmpegSrc" -ForegroundColor Yellow
        }

        # 2) 复制 PLAN 组件（FFmpeg-full 与使用说明）到 PLAN 子目录
        $PlanDest = "$OutputDir\PLAN"
        if (Test-Path $PlanSource) {
            if (Test-Path $PlanDest) {
                Remove-Item -Recurse -Force $PlanDest -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
            }
            robocopy $PlanSource $PlanDest /E /NFL /NDL /NJH /NJS /NC /NS
            if ($LASTEXITCODE -ge 8) { throw "复制 PLAN 失败 (robocopy exit code: $LASTEXITCODE)" }
            Write-Host "   ✅ PLAN 组件已复制" -ForegroundColor Green
        } else {
            Write-Host "   ⚠️ PLAN 源目录不存在: $PlanSource" -ForegroundColor Yellow
            Write-Host "   请先将 FFmpeg DLL 放入 publish\PLAN\ffmpeg-full\ 目录" -ForegroundColor Yellow
        }

        Write-Host "`n[3/4] 生成使用说明..." -ForegroundColor Yellow
        $ReadmePath = "$PlanDest\使用说明.txt"
        $content = @"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v${Version} — 使用说明
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求
  • Windows 10/11 或更高版本
  • 无需安装任何运行环境（NativeAOT 独立编译）

🚀 快速开始
  1. 解压所有文件到任意目录（保持文件夹结构完整）
  2. 双击运行 3FCompare.App.exe
  3. 拖入视频文件即可开始对比

📁 文件结构
  3FCompare.App.exe    — 主程序（NativeAOT 单文件，内嵌播放器内核）
  av*.dll / sw*.dll    — FFmpeg 编解码引擎（已在程序目录，无需配置）
  ass-9.dll            — 字幕渲染引擎
  PLAN/ffmpeg-full/    — FFmpeg 归档副本（备份，通常无需使用）

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

❓ 常见问题
  Q: 提示"FFmpeg 不可用"？
  A: 完整版已内置 FFmpeg（DLL 在程序目录），通常不会出现此提示。
     若出现，请打开设置（F25）→ FFmpeg 路径 → 选择程序目录或 PLAN/ffmpeg-full/，
     点击"测试探测"验证后保存。

  Q: 精简版（不含 PLAN/ffmpeg-full）如何播放视频？
  A: 精简版不含 FFmpeg，需要自行获取 FFmpeg DLL 放到程序目录，
     或在设置中指定包含 avcodec-*.dll 的目录。

  Q: 迁移到其他电脑？
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。

📞 反馈与交流
  GitHub: https://github.com/luoye-cpu/3FCompare

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本: v${Version} | 架构: ${Arch} | 构建日期: $((Get-Date -Format "yyyy-MM-dd"))
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
"@
        Set-Content -Path $ReadmePath -Value $content -Encoding UTF8
        Write-Host "   ✅ 使用说明已生成 → $ReadmePath" -ForegroundColor Green
    } else {
        Write-Host "`n[2/4] 跳过 (精简版不含 PLAN)" -ForegroundColor Yellow
        Write-Host "`n[3/4] 跳过 (精简版不生成使用说明)" -ForegroundColor Yellow
    }

    # Step 4: 压缩
    Write-Host "`n[4/4] 压缩打包..." -ForegroundColor Yellow
    if ($NoCompress) {
        Write-Host "   ⏭️ 已跳过压缩 (-NoCompress)" -ForegroundColor Yellow
        Write-Host "   产物目录: $OutputDir" -ForegroundColor Yellow
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
        if ($proc.ExitCode -ne 0) { throw "压缩失败 (7z exit code: $($proc.ExitCode))" }
        Write-Host "   ✅ 压缩完成 → $ArchivePath" -ForegroundColor Green
    } else {
        Write-Host "   ⚠️ 未找到 7z 命令，跳过压缩" -ForegroundColor Yellow
        Write-Host "   手动压缩目录: $OutputDir" -ForegroundColor Yellow
    }

    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  [$mode] 打包完成!" -ForegroundColor Green
    Write-Host "  产物目录: $OutputDir" -ForegroundColor Green
    if (Test-Path $ArchivePath) {
        $size = (Get-Item $ArchivePath).Length / 1MB
        Write-Host "  压缩包: $ArchivePath ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    }
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
}

# ── 主入口 ──
switch ($Mode) {
    "app"  { Invoke-Pack "app" }
    "full" { Invoke-Pack "full" }
    "all"  { Invoke-Pack "app"; Invoke-Pack "full" }
}

Write-Host "`n✅ 全部打包完成!" -ForegroundColor Green