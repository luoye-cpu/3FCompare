# 3FCompare 构建与部署脚本
# 用法: powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1 [-SkipTests]
# 前置: Visual Studio 2022+ (C++ 桌面负载), Git, （vcpkg 首次联网）

param(
    [switch]$SkipTests,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ForkRoot = Join-Path $ProjectRoot "third_party\fff_project"

function Get-MSBuildPath {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($vs) {
            $candidates = @(
                (Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"),
                (Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe")
            )
            foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
        }
    }
    throw "未找到 MSBuild（需要 Visual Studio 的 MSBuild 组件）"
}

$KernelBaselineTag = "3fcompare-kernel-2026.9.11.1"  # 见 third_party/fff_project/PATCHES.md
if (-not (Test-Path (Join-Path $ForkRoot "FFF.Native\FFF.Native.vcxproj"))) {
    Write-Host "内核目录缺失，按基线 tag 克隆: $KernelBaselineTag"
    Push-Location $ProjectRoot
    try {
        # .gitmodules 已解除跟踪（见 third_party/README.md），submodule 命令不再适用
        git clone https://github.com/Lake1059/FFF_Project.git $ForkRoot 2>$null
        if (Test-Path $ForkRoot) {
            Push-Location $ForkRoot
            git fetch origin tag $KernelBaselineTag 2>$null
            git checkout $KernelBaselineTag 2>$null
            if ($LASTEXITCODE -ne 0) { Write-Warning "基线 tag $KernelBaselineTag 不存在，已保持在默认分支——请核对 PATCHES.md 后手动 checkout" }
            Pop-Location
        }
    } finally { Pop-Location }
}

# 应用自定义补丁（增量策略：只应用比标记更新或未被跟踪的补丁，见 README）
# - 标记文件记录每个已应用补丁: 名称=policy 顺序行；时间为准已被 [时间戳 incr] 替代。
# - 任一补丁第一次出现或修改时间晚于标记时间 → 重新应用该补丁（幂等失败→阻断构建）。
$PatchesDir = Join-Path $PSScriptRoot "patches"
$PatchMarker = Join-Path $ForkRoot ".3fc_patches_applied"
if (Test-Path $PatchesDir) {
    $markerTime = if (Test-Path $PatchMarker) { (Get-Item $PatchMarker).LastWriteTime } else { [DateTime]::MinValue }
    $toApply = @(Get-ChildItem $PatchesDir -Filter *.patch | Where-Object { $_.LastWriteTime -gt $markerTime } | Sort-Object Name)
    if ($toApply.Count -gt 0) {
        Write-Host "应用 3FCompare 自定义补丁（增量 $($toApply.Count) 个）..."
        Push-Location $ForkRoot
        $allApplied = $true
        foreach ($p in $toApply) {
            Write-Host "  正在应用: $($p.Name)"
            git apply --ignore-whitespace $p.FullName 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✅ $($p.Name) 已应用"
            } else {
                # 已应用的补丁（git apply 报 already exists 场景）视为成功
                Write-Host "  ❌ $($p.Name) 应用失败"
                $allApplied = $false
            }
        }
        Pop-Location
        # 仅当全部成功才刷新标记，失败保持旧标记，下次重跑可重试失败的那批
        if ($allApplied) {
            New-Item -ItemType File -Path $PatchMarker -Force | Out-Null
            Write-Host "✅ 增量补丁全部应用，标记已刷新"
        } else {
            Write-Host "⚠️ 存在补丁未能应用，请检查冲突后重新运行本脚本"
        }
    } else {
        Write-Host "补丁均为最新（标记时间 $(Get-Date $markerTime -Format 'yyyy-MM-dd HH:mm')），跳过"
    }
}

Write-Host "=== [1/4] 准备 FFmpeg（若缺失） ==="
$ffmpegMarker = Join-Path $ForkRoot "third_party\ffmpeg\include\libavcodec\avcodec.h"
if (-not (Test-Path $ffmpegMarker)) {
    Push-Location $ForkRoot
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\准备FFmpeg.ps1"
    # 补齐上游脚本偶发遗漏的生成头
    $cache = Join-Path $env:LOCALAPPDATA "fff-ffmpeg-download\extracted\ffmpeg-master-latest-win64-lgpl-shared\include"
    if (Test-Path (Join-Path $cache "libavutil\avconfig.h")) {
        Copy-Item (Join-Path $cache "libavutil\avconfig.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force
        Copy-Item (Join-Path $cache "libavutil\ffversion.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
} else {
    Write-Host "  FFmpeg 已就绪，跳过"
}

Write-Host "=== [2/4] 准备 libass（若缺失） ==="
$assMarker = Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\include\ass\ass.h"
if (-not (Test-Path $assMarker)) {
    Push-Location $ForkRoot
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\准备Libass.ps1"
    Pop-Location
} else {
    Write-Host "  libass 已就绪，跳过"
}

Write-Host "=== [3/4] 构建 FFF.Native ==="
$msbuild = Get-MSBuildPath
Push-Location $ForkRoot
& $msbuild "FFF.Native\FFF.Native.vcxproj" /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "FFF.Native 构建失败" }
Pop-Location

Write-Host "=== [4/4] 部署 DLL 到应用与冒烟目录 ==="
function Deploy-To($targetDir) {
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item (Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll") $targetDir -Force
    Get-ChildItem (Join-Path $ForkRoot "runtime\*.dll") | ForEach-Object { Copy-Item $_.FullName $targetDir -Force }
    $assDll = Get-ChildItem (Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\bin\ass-9.dll") -ErrorAction SilentlyContinue
    if ($assDll) { Copy-Item $assDll.FullName $targetDir -Force }
    Write-Host "  已部署 -> $targetDir"
}

Deploy-To (Join-Path $ProjectRoot "src\3FCompare\bin\$Configuration\net11.0-windows")
Deploy-To (Join-Path $ProjectRoot "tests\3FCompare.SmokeTests\bin\$Configuration\net11.0")

if (-not $SkipTests) {
    Write-Host "=== [可选] 构建 .NET 解决方案 ==="
    Push-Location $ProjectRoot
    dotnet build .\src\3FCompare.slnx -c $Configuration --nologo
    Pop-Location
}

Write-Host "✔ 全部完成。运行冒烟: dotnet run --project tests/3FCompare.SmokeTests -- <视频>"
Write-Host "✔ 运行应用: dotnet run --project src/3FCompare"