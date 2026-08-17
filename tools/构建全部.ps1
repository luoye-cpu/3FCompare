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

if (-not (Test-Path (Join-Path $ForkRoot "FFF.Native\FFF.Native.vcxproj"))) {
    Write-Host "内核 submodule 未初始化，正在拉取..."
    Push-Location $ProjectRoot
    git submodule update --init --recursive
    Pop-Location
}

# 应用自定义补丁（如果尚未应用）
$PatchesDir = Join-Path $PSScriptRoot "patches"
$PatchMarker = Join-Path $ForkRoot ".3fc_patches_applied"
if ((Test-Path $PatchesDir) -and -not (Test-Path $PatchMarker)) {
    Write-Host "应用 3FCompare 自定义补丁..."
    Push-Location $ForkRoot
    Get-ChildItem $PatchesDir -Filter *.patch | Sort-Object Name | ForEach-Object {
        Write-Host "  正在应用: $($_.Name)"
        git apply --ignore-whitespace $_.FullName 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✅ $($_.Name) 已应用"
        } else {
            Write-Host "  ⚠️  $($_.Name) 应用失败（可能已应用过），继续..."
        }
    }
    Pop-Location
    # 创建标记文件，避免重复打补丁
    New-Item -ItemType File -Path $PatchMarker -Force | Out-Null
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

Deploy-To (Join-Path $ProjectRoot "src\3FCompare.App\bin\$Configuration\net11.0-windows")
Deploy-To (Join-Path $ProjectRoot "tests\3FCompare.SmokeTests\bin\$Configuration\net11.0")

if (-not $SkipTests) {
    Write-Host "=== [可选] 构建 .NET 解决方案 ==="
    Push-Location $ProjectRoot
    dotnet build .\src\3FCompare.slnx -c $Configuration --nologo
    Pop-Location
}

Write-Host "✔ 全部完成。运行冒烟: dotnet run --project tests/3FCompare.SmokeTests -- <视频>"
Write-Host "✔ 运行应用: dotnet run --project src/3FCompare.App"