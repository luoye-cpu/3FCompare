# 3FP 发布打包
#
# 目的：把"GUI 与内核版本必须一致"从**人工记忆**变成**流水线步骤**。
#
# 背景：宿主与内核共用一个配置结构体，内核用
#   version == PlayerApiVersion  且  size >= sizeof(FFF3FPConfiguration)
# 双重校验。任一侧漂移，FFF3FP_Create 就返回 InvalidArgument，表现是
# "播放器起不来"。2026-10-07 实际发生过一次：宿主停在 API 16（80 字节），
# 内核已到 18（112 字节），播放器完全无法启动。
#
# 本脚本只做打包，不做修复：不一致就**中止**并列明该改哪里。
# 版本一致性由 FFF.Player.vbproj 里的两个 target 强制执行：
#   VerifyPlayerApiVersion   —— 内核 / 宿主常量 / 宿主发送值 / 工程声明 四者相等
#   VerifyPlayerConfigSize   —— 内核与宿主的配置结构体字段数相等
#
# 用法：
#   pwsh -File 3fp/tools/发布打包.ps1                 # 默认 Release / win-x64
#   pwsh -File 3fp/tools/发布打包.ps1 -Configuration Debug
#   pwsh -File 3fp/tools/发布打包.ps1 -SkipTests      # 跳过回归（不推荐）

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # 3fp/
$kernelProject = Join-Path $root 'kernel\FFF.Native\FFF.Native.vcxproj'
$hostProject = Join-Path $root 'kernel\FFF.Player\FFF.Player.vbproj'
$dotnet = 'dotnet'

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Fail([string]$text) { Write-Host "  [!!] $text" -ForegroundColor Red }

# ── 0. 读取三处版本号，先给出人类可读的对照 ────────────────────────────────
Write-Step '版本一致性'
$apiCpp = Join-Path $root 'kernel\FFF.Native\3FP\Api\PlayerApi.cpp'
$sessionVb = Join-Path $root 'kernel\FFF.Player\Core\播放器会话.vb'
$projXml = Get-Content $hostProject -Raw

$kernelVersion = [regex]::Match((Get-Content $apiCpp -Raw), 'PlayerApiVersion\s*=\s*(\d+)').Groups[1].Value
$hostConst = [regex]::Match((Get-Content $sessionVb -Raw), '期望API版本\s+As\s+UInteger\s*=\s*(\d+)UI').Groups[1].Value
$projectVersion = [regex]::Match($projXml, '<PlayerApiVersion>(\d+)</PlayerApiVersion>').Groups[1].Value

if (-not $kernelVersion) { Write-Fail '无法解析内核 PlayerApiVersion'; exit 1 }
if (-not $hostConst) { Write-Fail '无法解析宿主 期望API版本'; exit 1 }
if (-not $projectVersion) { Write-Fail '无法解析工程 <PlayerApiVersion>'; exit 1 }

Write-Host ("  内核={0}  宿主常量={1}  工程声明={2}" -f $kernelVersion, $hostConst, $projectVersion)

if ($kernelVersion -ne $projectVersion -or $hostConst -ne $projectVersion) {
    Write-Fail '版本不一致，发布中止。请同步：'
    Write-Host '    · kernel/FFF.Native/3FP/Api/PlayerApi.cpp  PlayerApiVersion'
    Write-Host '    · kernel/FFF.Player/Core/播放器会话.vb      期望API版本'
    Write-Host '    · kernel/FFF.Player/FFF.Player.vbproj      <PlayerApiVersion>'
    exit 1
}
Write-Ok "版本一致（API $projectVersion）"

# ── 0b. 快照结构体布局 ──────────────────────────────────────────────────────
# 版本号一致并不代表布局一致：字段顺序错**不会**编译报错，只会静默读出错位的值。
# 2026-10-07 实际发生过 —— 宿主把 IAMF 三字段追加到末尾，而 C 侧它们在第 26-28 位，
# 于是其后约 30 个字段全部偏移。此检查把"顺序错"变成发布阶段的硬失败。
Write-Step '快照结构体布局'
$layoutTool = Join-Path $root 'tools\校验快照布局.py'
if (Test-Path $layoutTool) {
    $layoutOutput = & python $layoutTool 2>&1 | Out-String
    foreach ($line in ($layoutOutput -split "`r?`n")) {
        if ($line -match '字段|sizeof|布局') { Write-Host "  $line" }
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Fail '快照布局与 C 侧不一致，发布中止（详见上方列表）'
        Write-Host '  字段顺序错不会编译报错，只会静默读到错位的值。' -ForegroundColor DarkGray
        Write-Host '  请按 C 侧顺序重排 原生播放器快照。' -ForegroundColor DarkGray
        exit 1
    }
    Write-Ok '快照布局一致'
} else {
    Write-Host '  [--] 未找到布局校验脚本，跳过'
}

# ── 1. 构建内核 ─────────────────────────────────────────────────────────────
Write-Step "构建内核（$Configuration / x64）"
$msbuild = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $msbuild) { Write-Fail '未找到 MSBuild'; exit 1 }

& $msbuild.FullName $kernelProject /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Fail '内核构建失败'; exit $LASTEXITCODE }
Write-Ok '内核构建通过'

$kernelDll = Join-Path $root "kernel\FFF.Native\x64\$Configuration\FFF.Native.dll"
if (-not (Test-Path $kernelDll)) { Write-Fail "未生成内核 DLL：$kernelDll"; exit 1 }

# ── 2. 回归（可选，但默认执行）───────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step '回归测试'
    $tests = Join-Path $root 'kernel\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe'
    if (Test-Path $tests) {
        $output = & $tests 2>&1 | Out-String
        if ($output -match 'Core checks passed') { Write-Ok 'smoke 通过' }
        else { Write-Fail 'smoke 失败'; Write-Host $output; exit 1 }
    } else {
        Write-Host '  [--] 未找到测试可执行文件，跳过'
    }
}

# ── 3. 发布宿主（两个校验 target 在此触发）──────────────────────────────────
Write-Step "发布宿主（$Configuration / $RuntimeIdentifier）"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "publish\$Configuration-$RuntimeIdentifier" }

& $dotnet publish $hostProject -c $Configuration -r $RuntimeIdentifier --self-contained false `
    -o $OutputDirectory --nologo 2>&1 | ForEach-Object {
        # 把校验 target 的输出显示出来，让"检查确实跑了"可见
        if ($_ -match '3FP API 版本|配置结构体字段数|发布中止') { Write-Host "  $_" }
        elseif ($_ -match 'error') { Write-Fail $_ }
    }
if ($LASTEXITCODE -ne 0) {
    Write-Fail '发布中止 —— 通常是 GUI 与内核版本/结构体不一致（见上方提示）'
    exit $LASTEXITCODE
}
Write-Ok '发布通过（版本与结构体校验均已执行）'

# ── 4. 确认产物里的内核 DLL 就是刚构建的那个 ────────────────────────────────
Write-Step '产物校验'
$published = Join-Path $OutputDirectory 'FFF.Native.dll'
if (-not (Test-Path $published)) {
    Write-Fail "产物里没有 FFF.Native.dll：$published"
    exit 1
}
$h1 = (Get-FileHash $kernelDll -Algorithm SHA256).Hash
$h2 = (Get-FileHash $published -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $published).Length / 1MB, 2)

if ($h1 -ne $h2) {
    Write-Fail '产物中的 FFF.Native.dll 与刚构建的内核不一致（可能用了陈旧副本）'
    Write-Host "  构建: $kernelDll"
    Write-Host "  产物: $published"
    exit 1
}
Write-Ok "FFF.Native.dll 一致（$sizeMb MB, SHA256 $($h1.Substring(0,16))…）"

# ── 5. 运行期依赖完整性 ─────────────────────────────────────────────────────
# 这一项不是形式主义：发布 target 曾经漏拷 @(DevelopmentRuntime)，导致 build 目录能跑、
# publish 出来的包缺少全部 FFmpeg DLL（少 113 MB、6 个文件），启动即失败。
Write-Step '运行期依赖'
$required = @(
    'avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll',
    'swresample-7.dll', 'swscale-10.dll', 'avfilter-12.dll',
    'ass-9.dll', 'bluray-3.dll'
)
$missing = @()
foreach ($name in $required) {
    $p = Join-Path $OutputDirectory $name
    if (Test-Path $p) { Write-Host ("  [OK] {0,-20} {1,7:N1} MB" -f $name, ((Get-Item $p).Length / 1MB)) }
    else { Write-Host ("  [!!] {0,-20} 缺失" -f $name) -ForegroundColor Red; $missing += $name }
}
if ($missing.Count -gt 0) {
    Write-Fail ("交付目录缺少 {0} 个运行期依赖：{1}" -f $missing.Count, ($missing -join ', '))
    Write-Host '  这些 DLL 在 kernel\runtime 与 third_party\vcpkg_installed 下；' -ForegroundColor DarkGray
    Write-Host '  若缺失，请检查 FFF.Player.vbproj 的 CopyProductNativeToPublish 是否拷贝了 @(DevelopmentRuntime)。' -ForegroundColor DarkGray
    exit 1
}
Write-Ok '运行期依赖完整'

# ── 6. 交付目录 ─────────────────────────────────────────────────────────────
Write-Step '交付目录'
$files = Get-ChildItem $OutputDirectory -File | Sort-Object Name
Write-Host ("  共 {0} 个文件，{1} MB" -f $files.Count, [math]::Round((($files | Measure-Object Length -Sum).Sum)/1MB, 1))
foreach ($name in 'FFF.Player.exe', 'FFF.Native.dll') {
    $f = $files | Where-Object Name -eq $name
    if ($f) { Write-Host ("    · {0,-20} {1,8:N2} MB  {2}" -f $name, ($f.Length/1MB), $f.LastWriteTime) }
}

Write-Host "`n发布完成：$OutputDirectory" -ForegroundColor Green
Write-Host "提示：目标机需具备 VC++ 运行库（MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1）；" -ForegroundColor DarkGray
Write-Host "      d3d11/dxgi/d2d1/DWrite 由 Windows 提供。" -ForegroundColor DarkGray
exit 0
