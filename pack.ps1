# 3FCompare 打包脚本 — 2 版本发布（全部 NativeAOT 编译）
# 3FCompare Build & Pack Script — 2 variants (all NativeAOT)
# 用法 / Usage:
#   精简版 (无 FFmpeg) / Lite (no FFmpeg):        .\pack.ps1 -Mode app
#   完整版 (含 ffmpeg-full) / Full (with ffmpeg-full): .\pack.ps1 -Mode full
#   一键全部 2 个版本 / Both variants:         .\pack.ps1 -Mode all (default)
#
#   版本号唯一真源 = src/3FCompare/3FCompare.csproj 的 <Version>（+<VersionSuffix>）。
#   不传 -Version 时自动取 csproj；显式传入且与 csproj 不一致会告警（防误发旧包）。
param(
    [string]$Version = "",
    [ValidateSet("app", "full", "all")]
    [string]$Mode = "all",
    [switch]$NoCompress   # 跳过 7z 压缩（调试时快速验证打包逻辑）/ Skip 7z compression (debug)
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = "$ProjectDir\publish"
$BuildDir = "$ProjectDir\publish\build"
$AppProject = "$ProjectDir\src\3FCompare\3FCompare.csproj"
$PlanSource = "$PublishDir\PLAN"

# ── 版本号：默认从 csproj 读（唯一真源） ──
# 历史问题：0.2.0 硬编码在 pack.ps1 / 发布门禁.ps1 / 两个 csproj 四处，
# 出现过"本地打过 0.2.1 但版本叙事里没有它"的情况，极易误发旧包。
function Read-CsprojVersion([string]$CsprojPath) {
    if (-not (Test-Path -LiteralPath $CsprojPath)) { return $null }
    $text = Get-Content -Raw -LiteralPath $CsprojPath
    $v = [regex]::Match($text, '<Version>\s*([^<]+)\s*</Version>')
    if (-not $v.Success) { return $null }
    $s = [regex]::Match($text, '<VersionSuffix>\s*([^<]+)\s*</VersionSuffix>')
    return [pscustomobject]@{
        Version = $v.Groups[1].Value.Trim()
        Suffix  = if ($s.Success) { $s.Groups[1].Value.Trim() } else { "" }
    }
}

$csprojVer = Read-CsprojVersion $AppProject
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (-not $csprojVer) { throw "无法从 $AppProject 读取 <Version>，请显式传 -Version" }
    $Version = $csprojVer.Version
    Write-Host "版本取自 csproj（唯一真源）: $Version" -NoNewline -ForegroundColor Gray
    if ($csprojVer.Suffix) { Write-Host "  后缀: $($csprojVer.Suffix)" -ForegroundColor Gray } else { Write-Host "" }
} elseif ($csprojVer -and $Version -ne $csprojVer.Version) {
    Write-Warning "传入版本 '$Version' 与 csproj 的 '$($csprojVer.Version)' 不一致——确认是在打旧版本吗？"
}

# 本机 dotnet restore 全域失败：沙箱/部分终端里 APPDATA 为空，
# NuGet 读资产文件时 Path.Combine 拿到 null，报 "Value cannot be null. (Parameter 'path1')"。
# 对策：补 APPDATA + dotnet 调用一律 --no-restore（依赖需提前 restore 好）。
if (-not $env:APPDATA) {
    Write-Host "环境缺少 APPDATA，兜底设置为默认用户目录 / APPDATA missing, fallback to default" -ForegroundColor Yellow
    $env:APPDATA = Join-Path $env:USERPROFILE "AppData\Roaming"
}
# dotnet 未必在 PowerShell 的 PATH 上；用裸命令时 $LASTEXITCODE 可能沿用旧值而误判成功。
$Dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($Dotnet) { $Dotnet = $Dotnet.Source }
elseif (Test-Path "C:\Program Files\dotnet\dotnet.exe") { $Dotnet = "C:\Program Files\dotnet\dotnet.exe" }
else { throw "未找到 dotnet / dotnet not found. 请安装 .NET SDK 或将其加入 PATH。" }

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

# ── 产物自检 ──
# 目的：打包流程历史上出现过"脚本一路绿灯、产物其实是坏的"——
# 缺 FFmpeg 的完整版、版本号没传到位、把本机 logs/（含开发者绝对路径）打进包。
# 这些都只在用户解压后才会暴露。这里在打包结束时做一次硬校验，失败即中止并以非 0 退出。
function Assert-Package([string]$mode, [string]$OutputDir, [string]$ArchivePath) {
    Write-Host "`n[自检] 校验产物完整性 / Verifying artifacts..." -ForegroundColor Yellow
    $errors = @()

    # 1. 主程序必须存在
    $exe = Join-Path $OutputDir "3FCompare.exe"
    if (-not (Test-Path $exe)) { $errors += "缺少主程序 3FCompare.exe" }

    # 2. 完整版必须自带 FFmpeg，且 avcodec 必须在（引擎探测以它为判定依据）
    if ($mode -eq "full") {
        $av = @(Get-ChildItem "$OutputDir\ffmpeg-full\avcodec-*.dll" -ErrorAction SilentlyContinue)
        if ($av.Count -eq 0) { $errors += "完整版缺少 FFmpeg 核心库 ffmpeg-full\avcodec-*.dll" }
    }

    # 3. 版本号必须真的落到 exe 上
    #    （曾出现 csproj 硬编码 <VersionSuffix>BETA 导致正式包仍显示 -BETA）
    if (Test-Path $exe) {
        $vi = (Get-Item $exe).VersionInfo
        $pv = if ($vi.ProductVersion) { $vi.ProductVersion } else { $vi.FileVersion }
        if ([string]::IsNullOrWhiteSpace($pv)) {
            $errors += "无法读取 3FCompare.exe 的版本信息（ProductVersion/FileVersion 均为空）"
        }
        elseif (-not $pv.StartsWith($Version)) {
            $errors += "版本号不符：期望以 '$Version' 开头，实际 '$pv'"
        }
        elseif ($pv -match "-") {
            $errors += "正式包不应带预发布后缀，实际 '$pv'（请确认 VersionSuffix 已清空）"
        }
    }

    # 4. 不应把本机调试日志打进包（含开发者绝对路径，属于信息泄露）
    if (Test-Path (Join-Path $OutputDir "logs")) { $errors += "发行包内残留 logs/ 目录（含本机路径）" }

    # 5. 压缩包必须真实生成且体积合理（空包/半包往往体积异常小）
    if (-not $NoCompress) {
        if (-not (Test-Path $ArchivePath)) {
            $errors += "压缩包未生成: $ArchivePath"
        } elseif ((Get-Item $ArchivePath).Length -lt 1MB) {
            $errors += "压缩包体积异常小（<1MB），疑似空包"
        }
    }

    if ($errors.Count -gt 0) {
        Write-Host "   ❌ 产物自检未通过 / Artifact verification FAILED:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "      • $_" -ForegroundColor Red }
        throw "[$mode] 产物自检失败，中止发布 / Artifact verification failed"
    }
    Write-Host "   ✅ 产物自检通过 / Artifacts verified" -ForegroundColor Green
}

# ── 单个版本打包 ──
function Invoke-Pack([string]$mode) {
    $PackageName = Get-PackageName $mode
    $OutputDir = "$BuildDir\$PackageName"
    $ArchivePath = "$PublishDir\$PackageName.7z"

    $modeLabel = switch ($mode) {
        "app"  { "精简版 (NativeAOT, 无 FFmpeg) / Lite (NativeAOT, no FFmpeg)" }
        "full" { "完整版 (NativeAOT + ffmpeg-full) / Full (NativeAOT + ffmpeg-full)" }
    }
    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  $modeLabel" -ForegroundColor Cyan
    Write-Host "  v$Version | $Arch | $PackageName" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

    # 清理旧产物
    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Step 1: NativeAOT 发布（FFF.Native 已内嵌于 exe）/ NativeAOT publish (FFF.Native embedded in exe)
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    & $Dotnet publish $AppProject `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:Version=$Version `
        -p:VersionSuffix='' `
        --no-restore `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "[$mode] 发布失败 / Publish failed (exit=$LASTEXITCODE)" }
    Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

    # 清理本机调试日志：内含开发者绝对路径（如 C:\PLAN\...\test_8k_av1_200M.mp4），不应随包分发
    $logDir = Join-Path $OutputDir "logs"
    if (Test-Path $logDir) {
        Remove-Item -Recurse -Force $logDir
        Write-Host "   ✅ 已清理发行包内的调试日志 / Debug logs removed" -ForegroundColor Green
    }

    # 清理调试符号（.pdb 对用户无意义）/ Remove debug symbols (.pdb)
    # ⚠ 必须 -Recurse：原先只匹配顶层 "$OutputDir\*.pdb"，子目录里的 pdb 会随包分发
    # （docs/14 §P2-10）。
    $pdbFiles = Get-ChildItem $OutputDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue
    if ($pdbFiles) {
        $pdbFiles | Remove-Item -Force
        $savedMB = [math]::Round(($pdbFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host "   ✅ 已删除调试符号（递归），共 $($pdbFiles.Count) 个，节省 ${savedMB}MB / Debug symbols removed, saved ${savedMB}MB" -ForegroundColor Green
    }

    # 单文件发布后不该再出现这些中间产物；出现说明发布配置被改过或有残留。
    # 只警告不失败：某些合法的发布形态会保留 deps.json，硬拦会误伤打包。
    $leftovers = @()
    $leftovers += Get-ChildItem $OutputDir -Filter *.deps.json -Recurse -File -ErrorAction SilentlyContinue
    $leftovers += Get-ChildItem $OutputDir -Filter *.runtimeconfig.json -Recurse -File -ErrorAction SilentlyContinue
    $leftovers += Get-ChildItem $OutputDir -Directory -Recurse -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -in @('bin', 'obj') }
    if ($leftovers.Count -gt 0) {
        Write-Host "   ⚠ 发行包内发现 $($leftovers.Count) 个中间产物（通常不应随包分发）:" -ForegroundColor Yellow
        $leftovers | Select-Object -First 10 | ForEach-Object {
            Write-Host "     - $($_.FullName.Substring($OutputDir.Length).TrimStart('\'))" -ForegroundColor DarkYellow
        }
    }

    # Step 2/3: 复制 FFmpeg 运行时 + 生成使用说明 (仅完整版)
    if ($mode -eq "full") {
        Write-Host "`n[2/4] 复制 FFmpeg 组件包 / Copy FFmpeg bundle..." -ForegroundColor Yellow
        # 单份 FFmpeg DLL 复制到 ffmpeg-full/ 子目录（运行时 NativeRuntime 自动探测并注册此目录）
        $FfmpegDest = "$OutputDir\ffmpeg-full"
        New-Item -ItemType Directory -Force -Path $FfmpegDest | Out-Null
        $FfmpegSrc = "$PublishDir\PLAN\ffmpeg-full"
        # P0-4 修复：以下三种情况过去都只 warning 就继续，会打出"完整版却没有 FFmpeg"的坏包
        # （用户解压后和精简版一样只能跑演示模式），而脚本末尾照样打印"打包完成"。
        # 完整版的价值就在于自带 FFmpeg，缺了就必须失败中止。
        if (-not (Test-Path $FfmpegSrc)) {
            throw "[$mode] FFmpeg 源目录不存在: $FfmpegSrc`n请先将 FFmpeg DLL 放入 publish\PLAN\ffmpeg-full\ 目录再打包完整版。"
        }
        $ffDlls = @(Get-ChildItem "$FfmpegSrc\*.dll" -ErrorAction SilentlyContinue)
        if ($ffDlls.Count -eq 0) {
            throw "[$mode] FFmpeg 源目录存在但没有 DLL: $FfmpegSrc —— 完整版必须包含 FFmpeg，否则与精简版无异。"
        }
        $ffDlls | ForEach-Object { Copy-Item $_.FullName "$FfmpegDest" -Force }
        # 复制后按数量校验，防止 Copy-Item 静默失败
        $copied = @(Get-ChildItem "$FfmpegDest\*.dll" -ErrorAction SilentlyContinue)
        if ($copied.Count -ne $ffDlls.Count) {
            throw "[$mode] FFmpeg DLL 复制数量不符：源 $($ffDlls.Count) 个，目标 $($copied.Count) 个"
        }
        Write-Host "   ✅ FFmpeg DLL 已复制到 ffmpeg-full/ 子目录（$($copied.Count) 个）" -ForegroundColor Green

        Write-Host "`n[3/4] 生成使用说明 / Generate usage guide..." -ForegroundColor Yellow
        $ReadmePath = "$OutputDir\使用说明.txt"
        $content = @"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v${Version} — 使用说明 / Usage Guide
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求 / Requirements
  • Windows 10/11 或更高版本 / or later
  • 无需安装任何运行环境（NativeAOT 独立编译）/ No runtime required (NativeAOT standalone)

🚀 快速开始 / Quick Start
  1. 解压所有文件到任意目录（保持文件夹结构完整）/ Extract to any directory
  2. 双击运行 3FCompare.exe / Double-click 3FCompare.exe
  3. 拖入视频文件即可开始对比 / Drag video files to start comparison

📁 文件结构 / File Structure
  3FCompare.exe    — 主程序（NativeAOT 单文件，内嵌播放器内核）
                         Main executable (NativeAOT, embedded player kernel)
  av*.dll / sw*.dll    — FFmpeg 编解码引擎（在 ffmpeg-full/ 子目录）/ FFmpeg decoding engine (in ffmpeg-full/ subdirectory)
  ass-9.dll            — 字幕渲染引擎 / Subtitle rendering engine

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
  A: 完整版已内置 FFmpeg（DLL 在 ffmpeg-full/ 子目录），程序会自动探测，通常不会出现此提示。
     Full version includes FFmpeg DLLs in the ffmpeg-full/ subdirectory, auto-detected at startup.
     若出现，请打开设置（F25）→ FFmpeg 路径 → 指向 ffmpeg-full/ 或程序目录，
     点击"测试探测"验证后保存。
     Otherwise, open Settings (F25) → FFmpeg Path → point to ffmpeg-full/ or the program dir,
     click "Test" to verify, then save.

  Q: 精简版如何播放视频？/ How to play video in the lite version?
  A: 精简版不含 FFmpeg，需要自行获取 FFmpeg DLL，放到程序目录下 ffmpeg-full/ 子目录
     （程序自动探测），或在设置中指定包含 avcodec-*.dll 的目录。
     The lite version does not include FFmpeg; obtain FFmpeg DLLs yourself and place them
     in an ffmpeg-full/ subdirectory next to the exe (auto-detected), or set the path in Settings.

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
        # 不能写 -Encoding UTF8：PS 5.1 会写 BOM、pwsh 7 不写，同一份使用说明.txt
        # 在另一版本下打开就乱码。显式指定带 BOM 的 UTF8，两个版本行为一致。
        # （Set-Content 在 5.1 下 -Encoding UTF8 也是 BOM，但 pwsh 7 默认无 BOM，故统一走 .NET API）
        [IO.File]::WriteAllText($ReadmePath, $content, (New-Object Text.UTF8Encoding $true))
        Write-Host "   ✅ 使用说明已生成 → $ReadmePath" -ForegroundColor Green
    } else {
        Write-Host "`n[2/4] 跳过 (精简版不含 PLAN) / Skipped (lite, no PLAN)" -ForegroundColor Yellow
        Write-Host "`n[3/4] 跳过 (精简版不生成使用说明) / Skipped (lite, no usage guide)" -ForegroundColor Yellow
    }

    # Step 4: 压缩 / Archive
    Write-Host "`n[4/4] 压缩打包 / Archiving..." -ForegroundColor Yellow
    if ($NoCompress) {
        # P1-6 修复：这里原来是 `return`，而断言写在 return 之后 ——
        # -NoCompress 本是用来快速验证打包逻辑的调试开关，结果恰恰把
        # exe / 版本号 / FFmpeg / logs 残留这四项最该验的校验全跳过了。
        # 改为「只跳过压缩，不跳过自检」（Assert-Package 内部已按 $NoCompress 跳过压缩包体积检查）。
        Write-Host "   ⏭️ 已跳过压缩 (-NoCompress) / Compression skipped" -ForegroundColor Yellow
        Write-Host "   产物目录 / Output directory: $OutputDir" -ForegroundColor Yellow
    } else {
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
            # P0-4 修复：过去这里只 warning 且退出码仍为 0，CI 会误判打包成功，
            # 实际上根本没有产出可分发的压缩包。
            throw "未找到 7z.exe，无法压缩。请安装 7-Zip 或将其加入 PATH。/ 7z not found — install 7-Zip or add it to PATH."
        }
    }

    # 产物自检：确认"打出来的包真的是能用的包"，任一项不满足即中止。
    # 必须在上面 -NoCompress 分支之外，否则调试打包时整段失效。
    Assert-Package -mode $mode -OutputDir $OutputDir -ArchivePath $ArchivePath

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