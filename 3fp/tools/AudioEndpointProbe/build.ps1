# AudioEndpointProbe 构建脚本 —— cl 命令行直编（沿用 3fp/tools/rebuild_probes.ps1 的 vcvars 模式）。
# 工具链定位顺序：vswhere → 已知 VS18 Community 路径。无需 MSBuild 工程、无需 vcpkg/FFmpeg 头。
#
# 用法：pwsh -File build.ps1 [-Run [json路径]]

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# ---- 1) 定位 vcvars64.bat ----
$vcvars = $null
$vswhere = Join-Path (${env:ProgramFiles(x86)}) 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $inst = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($inst) {
        $p = Join-Path $inst.Trim() 'VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path $p) { $vcvars = $p }
    }
}
if (-not $vcvars) {
    foreach ($p in @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
    )) { if (Test-Path $p) { $vcvars = $p; break } }
}
if (-not $vcvars) { throw '未找到 vcvars64.bat（需要 MSVC C++ 工具链，含 x64 组件）' }
Write-Host "vcvars64: $vcvars"

# ---- 2) 组装 cl 命令（bat 承载，复用 rebuild_probes.ps1 的做法）----
$exe = Join-Path $here 'AudioEndpointProbe.exe'
$objDir = Join-Path $here '_obj'
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
Remove-Item $exe -Force -ErrorAction SilentlyContinue   # 先删目标：失败残留旧二进制会误判成功
$bat = Join-Path $here '_build_one.bat'
$src = Join-Path $here 'AudioEndpointProbe.cpp'
$lines = @(
    '@echo off',
    "call `"$vcvars`" >nul",
    # 注意：/Fo 不能以 \" 结尾（尾反斜杠会转义引号，吞掉后续源文件参数 → D8003），故给出完整 obj 文件名。
    "cl /nologo /W4 /WX- /EHsc /std:c++20 /O2 /utf-8 /Fe:`"$exe`" /Fo:`"$objDir\AudioEndpointProbe.obj`" `"$src`" /link ole32.lib"
)
Set-Content -Path $bat -Value ($lines -join "`r`n") -Encoding ASCII
try {
    $log = cmd /c "`"$bat`" 2>&1"
    $log | ForEach-Object { Write-Host "  $_" }
} finally {
    Remove-Item $bat -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path $exe)) { throw '编译失败：未生成 AudioEndpointProbe.exe' }
Write-Host "OK: $exe"

# ---- 3) 可选：构建后直接运行 ----
if ($Run) {
    $json = if ($Run -is [string]) { $Run } else { 'C:\PLAN\3FCompare\3fp\samples\passthrough\endpoint-capabilities.json' }
    $dir = Split-Path -Parent $json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    & $exe $json
}
