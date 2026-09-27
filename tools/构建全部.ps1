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

# ---- 内核基线（改基线 = 改这里；必须同时是 PATCHES.md 里归档 tag 指向的提交）----
# 单一真源：tools/发布门禁.ps1 的 [1/11] 用正则从这里提取 $KernelBaselineSha，
# 不要在门禁里另抄一份字面量（S6 的收敛结论）。
#
# 2026-09-27 基线迁移到 b661365：= 上游 a87046e 的合并提交 a8e437e
#   + 方案A（RenderTargetInfo v2，destX/destY 改有符号；负原点对应放大平移时
#     被推到 back buffer 之外的目的框，v1 的 uint32 会静默吃掉一半平移量程）
#   + PATCHES.md 记账。归档 tag `3fcompare-kernel-2026.9.27.1`，
#   随仓分发的 .3fc_kernel_baseline.bundle 已重建并 `bundle verify` 通过（含该 tag）。
#   ⚠ 方案A **不改** PlayerApiVersion（仍是 15）：改的是输出结构的语义而非导出签名。
#     因此"版本号相等"拦不住 ABI 变化 —— PATCHES.md 〇 节记着上游自己也干过这种事。
#   ⚠ 本脚本只**记录**内核实况（HEAD/脏文件/DLL 哈希写进 .3fc_kernel_build.json），
#     拦截由门禁 [1/11] 负责：脏内核可以照常构建（开发要能跑），但不能进发布包。
$KernelRepo        = "https://github.com/Lake1059/FFF_Project.git"
$KernelBaselineTag = "3fcompare-kernel-2026.9.27.1"
$KernelBaselineSha = "b66136550a99f06c42c47e272c9eb21d01e09887"
# 上一基线（回滚点）：3fcompare-kernel-2026.9.18.3 / b765a1f8d76619da8583f7ac512621fbfb55dfa5
# 3FCompare 扩展的云端归档：上游 Lake1059/FFF_Project **不含**这些扩展，
# 且本机账号对它只有读权限，所以基线归档只能落在主仓库自己的仓库里。
$KernelArchiveRepo   = "https://github.com/luoye-cpu/3FCompare.git"
$KernelArchiveBranch = "kernel/3fcompare-zoom-viewport-cover"

function Invoke-Git {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$GitArgs
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = & git -C $Path @GitArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{
        Exit = $code
        Out  = (($raw | ForEach-Object { $_.ToString() }) -join "`n").Trim()
    }
}

function Get-FileSha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "" }
    $stream = [IO.File]::OpenRead($path)
    try {
        $hash = [Security.Cryptography.SHA256]::Create().ComputeHash($stream)
        return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    } finally { $stream.Dispose() }
}

# 内核此刻的实况：HEAD、是否脏、HEAD 上有没有归档 tag。
# 刻意**不 checkout**：本脚本走"增量补丁应用到工作树"的路子（见上方补丁段），
# 自动检出基线会把用户未提交的内核工作直接掀掉——HEAD 版本可以这么做是因为它
# 那时内核总是干净的。核对与拦截交给门禁，构建侧只负责如实记录。
function Get-KernelState {
    $head = ""
    $r = Invoke-Git $ForkRoot @("rev-parse", "HEAD")
    if ($r.Exit -eq 0) { $head = $r.Out }
    $dirty = ""
    $s = Invoke-Git $ForkRoot @("status", "--porcelain")
    if ($s.Exit -eq 0) { $dirty = $s.Out }
    $tag = ""
    $t = Invoke-Git $ForkRoot @("tag", "--points-at", "HEAD")
    if ($t.Exit -eq 0 -and $t.Out) { $tag = ($t.Out -split "`n")[0].Trim() }
    return [pscustomobject]@{
        Head    = $head
        Dirty   = ($dirty -ne "")
        DirtyList = @($dirty -split "`n" | Where-Object { $_ -ne "" })
        Tag     = $tag
    }
}

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

# 应用自定义补丁（增量策略：只应用比标记更新或未被跟踪的补丁，见 patches/README.md）
#
# ⚠ 这里原来有个真 bug：注释写着"已应用的补丁视为成功"，代码里**却没有这条判断**——
#   直接 `git apply` 后看退出码。于是当补丁描述的分歧已经被合进内核（今天的 0013 就是），
#   正向 apply 失败 ⇒ 标记不刷新 ⇒ 每次构建都重演一次失败；更糟的是 $ErrorActionPreference=Stop
#   下 git 往 stderr 写字就抛，整个构建在第一步就中止（2026-09-27 实跑撞上的就是这个）。
#   现在按三态判：正向 --check 能过 ⇒ 应用；反向 --check 能过 ⇒ **已在树中**，算满足不动它；
#   两边都过不去 ⇒ 真冲突，判红停下（半补丁的内核构建出来比不构建更坏）。
#   原生命令一律走 Invoke-Git，避免 stderr 把 Stop 变成异常。
$PatchesDir = Join-Path $PSScriptRoot "patches"
$PatchMarker = Join-Path $ForkRoot ".3fc_patches_applied"
if (Test-Path $PatchesDir) {
    $markerTime = if (Test-Path $PatchMarker) { (Get-Item $PatchMarker).LastWriteTime } else { [DateTime]::MinValue }
    $toApply = @(Get-ChildItem $PatchesDir -Filter *.patch | Where-Object { $_.LastWriteTime -gt $markerTime } | Sort-Object Name)
    if ($toApply.Count -gt 0) {
        Write-Host "处理 3FCompare 自定义补丁（增量 $($toApply.Count) 个）..."
        $pending = @()
        foreach ($p in $toApply) {
            $fwd = Invoke-Git $ForkRoot @("apply", "--check", "--ignore-whitespace", $p.FullName)
            if ($fwd.Exit -ne 0) {
                # 反向能应用 == 这份补丁的效果已经在树里（已合并/已应用），不是冲突
                $rev = Invoke-Git $ForkRoot @("apply", "--check", "-R", "--ignore-whitespace", $p.FullName)
                if ($rev.Exit -eq 0) {
                    Write-Host "  ✓ $($p.Name) 已在树中（反向 --check 通过），不重复应用" -ForegroundColor Green
                    continue
                }
                Write-Host "  ✗ $($p.Name) 既不正向可应用、也不已在树中 ⇒ 真冲突" -ForegroundColor Red
                Write-Host "    正向：$($fwd.Out)" -ForegroundColor DarkGray
                $pending += $p.Name
                continue
            }
            $ap = Invoke-Git $ForkRoot @("apply", "--ignore-whitespace", $p.FullName)
            if ($ap.Exit -eq 0) { Write-Host "  ✅ $($p.Name) 已应用" }
            else { Write-Host "  ✗ $($p.Name) --check 通过但应用失败：$($ap.Out)" -ForegroundColor Red; $pending += $p.Name }
        }
        if ($pending.Count -gt 0) {
            # 不刷新标记：下次重跑还会尝试同一批。宁可停下，也不要拿半补丁的内核去构建。
            throw @"
以下内核补丁无法应用、也不在树中：$($pending -join ', ')
构建已中止（内核未改动）。请人工核对 third_party/fff_project 的 HEAD 与 tools/patches/ 的假设是否对得上：
  - 补丁描述的分歧若已被合并 ⇒ 把该补丁移进 tools/patches/history/（它只是记录，不再是待应用项）
  - 若确实还没应用 ⇒ 逐个人工 git apply --3way 解冲突，并在 PATCHES.md 记一笔
"@
        }
        # 全部处理完（应用了或确认已在树中）才刷新标记
        New-Item -ItemType File -Path $PatchMarker -Force | Out-Null
        Write-Host "✅ 补丁全部处理完毕，标记已刷新"
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
# 在动手编译**之前**取内核实况：编译过程不改 git 状态，但脚本尾部再取会漏掉
# "构建期间有人往内核目录写东西"这类情况——越早取越接近"这份 DLL 是从哪个树编出来的"。
$KernelState = Get-KernelState
if ($KernelState.Dirty) {
    Write-Host "⚠ 内核工作树脏（$($KernelState.DirtyList.Count) 个文件），构建出的 DLL 不是基线提交本身：" -ForegroundColor Yellow
    foreach ($d in $KernelState.DirtyList) { Write-Host "    $d" -ForegroundColor Yellow }
    Write-Host "  本脚本照常构建（开发要能跑），但发布门禁 [1/11] 会因此判红，直到这些改动被提交。" -ForegroundColor Yellow
}
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

Write-Host "=== 写内核构建清单（发布门禁 [1/11] 的输入）==="
# 只在**成功路径**写：写在前面会让"构建失败但清单已更新"变成假绿（HEAD 版本踩过这个坑，
# 见其注释"项目根的 .3fc_kernel_sha 不能在这里写"）。
# 清单里记的是"这份 DLL 从哪个树编出来、哈希是多少"——门禁据此复核磁盘上那份有没有被换过
# （今天真实发生过：Debug bin 里躺着 09-21 的旧内核，跑出来的判据全是假红）。
$KernelDllPath = Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll"
$manifest = [ordered]@{
    head          = $KernelState.Head
    baseline      = $KernelBaselineSha
    baselineTag   = $KernelBaselineTag
    kernelTag     = $KernelState.Tag
    dirty         = [bool]$KernelState.Dirty
    dirtyFiles    = @($KernelState.DirtyList)
    configuration = $Configuration
    kernelDll     = $KernelDllPath
    dllSha256     = (Get-FileSha256 $KernelDllPath)
    builtAt       = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
}
$manifestPath = Join-Path $ProjectRoot ".3fc_kernel_build.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
# 兼容字段：门禁与老脚本仍会读 .3fc_kernel_sha
Set-Content -Path (Join-Path $ProjectRoot ".3fc_kernel_sha") -Value $KernelState.Head -Encoding ASCII
if (-not $manifest.dllSha256) {
    Write-Host "⚠ 清单里 dllSha256 为空：$KernelDllPath 不存在，门禁 [1/11] 会判红" -ForegroundColor Yellow
}
Write-Host "  清单 -> $manifestPath（head $($KernelState.Head.Substring(0,[Math]::Min(12,$KernelState.Head.Length))) dirty=$($manifest.dirty)）"

Write-Host "✔ 全部完成。运行冒烟: dotnet run --project tests/3FCompare.SmokeTests -- <视频>"
Write-Host "✔ 运行应用: dotnet run --project src/3FCompare"