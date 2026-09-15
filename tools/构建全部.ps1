# 3FCompare 构建与部署脚本
# 用法: powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1 [-SkipTests] [-Configuration Release]
#       [-SkipPatches] [-ForcePatches] [-AllowKernelDrift]
# 前置: Visual Studio 2022+ (C++ 桌面负载), Git, （vcpkg 首次联网）
#
# 内核基线策略（P1-13）：
#   third_party/fff_project 是 **本地归档分支** 3fcompare/zoom-viewport-cover，
#   含上游没有的 3FCompare 扩展（见 third_party/fff_project/PATCHES.md）。
#   tag 名是可变的，只有 SHA 不可变 —— 因此本脚本钉死完整 SHA，
#   并在 HEAD 与钉死值不一致时**直接报错**，绝不静默退回上游默认分支。

param(
    [switch]$SkipTests,             # 仅跳过单元测试；主程序永远会构建
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipPatches,       # 完全跳过 tools/patches 重放
    [switch]$ForcePatches,      # 即使 HEAD 等于钉死基线也强制重放
    [switch]$AllowKernelDrift   # 允许内核 HEAD 与钉死 SHA 不一致（仅本地试验，禁止用于发布）
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ForkRoot    = Join-Path $ProjectRoot "third_party\fff_project"

# ---------------------------------------------------------------------------
# 环境兜底：本机 dotnet restore 全域失败
#   根因：部分终端/沙箱里 APPDATA 为空，NuGet 读资产文件时 Path.Combine 拿到 null，
#         报 "Value cannot be null. (Parameter 'path1')"（NuGet.targets）。
#   表现：任何不带 --no-restore 的 dotnet build/test 都直接崩，且崩得与代码无关。
#   对策：① 补 APPDATA；② 本脚本所有 dotnet 调用一律 --no-restore（依赖需提前 restore 好）。
# ---------------------------------------------------------------------------
if (-not $env:APPDATA) {
    Write-Host "环境缺少 APPDATA，兜底设置为默认用户目录" -ForegroundColor Yellow
    $env:APPDATA = Join-Path $env:USERPROFILE "AppData\Roaming"
}
# 同一个沙箱根因的另一半：LOCALAPPDATA 为空时 Join-Path 会抛参数绑定终止错误。
# 只在"需要准备 FFmpeg"这条路径上触发，所以长期没暴露。
if (-not $env:LOCALAPPDATA) {
    Write-Host "环境缺少 LOCALAPPDATA，兜底设置为默认用户目录" -ForegroundColor Yellow
    $env:LOCALAPPDATA = Join-Path $env:USERPROFILE "AppData\Local"
}

# ---------------------------------------------------------------------------
# 工具绝对路径解析
#   git / dotnet 未必在 PowerShell 的 PATH 里。用裸命令有两个坑：
#     ① 命令不存在时 PowerShell 抛 CommandNotFoundException 而不是返回非零退出码；
#     ② $LASTEXITCODE 会沿用上一次原生命令的旧值 —— 拿到 0 就误判成功。
#   因此统一解析为绝对路径，缺失即 throw。
# ---------------------------------------------------------------------------
function Resolve-Tool {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [string[]]$Fallbacks = @()
    )
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($f in $Fallbacks) { if ($f -and (Test-Path $f)) { return $f } }
    throw "未找到 $Name。请安装后重试，或加入 PATH。已尝试: $($Fallbacks -join ', ')"
}

# 安全截断 SHA 用于日志：戳记文件可能被人改坏或写了一半，
# 直接 Substring(0,12) 会抛"索引和长度必须引用该字符串内的位置"这种与真实原因
# （DLL 来源不明/需重建）无关的异常，把 S5 的判断逻辑整个盖掉（docs/14 §P2-9）。
function Short-Sha {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '<空>' }
    if ($Value.Length -le 12) { return $Value }
    return $Value.Substring(0, 12)
}

$Dotnet = Resolve-Tool "dotnet" @("$env:ProgramFiles\dotnet\dotnet.exe", "C:\Program Files\dotnet\dotnet.exe")
$Git    = Resolve-Tool "git"    @("$env:ProgramFiles\Git\cmd\git.exe", "C:\Program Files\Git\cmd\git.exe",
                                  "C:\Program Files\Git\bin\git.exe")
# 原先直接裸写 `powershell`：pwsh7 环境下它未必在 PATH，报出的却是"找不到文件"这类
# 误导性错误。这里同样走 Resolve-Tool，并把子脚本的调用统一收敛到 Invoke-SubScript。
# 注意：pwsh7 会话里 Get-Command powershell 常返回 NOT FOUND（已在 Windows 11 实测），
# 因此 fallback 必须给全；这里额外硬编码一份，避免 $env:SystemRoot 为空时解析失败。
$Pwsh   = Resolve-Tool "powershell" @("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe",
                                      "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")

# ---------------------------------------------------------------------------
# 调用子脚本：除了退出码，还要拦子脚本里**非终止**的 Write-Error。
# 只判 $LASTEXITCODE 会漏——Write-Error 默认不终止脚本，退出码仍是 0，
# 于是"子脚本报错了但构建继续"这种假绿会一路带到打包（docs/14 §P2-7）。
# ---------------------------------------------------------------------------
function Invoke-SubScript {
    param(
        [Parameter(Mandatory)] [string]$ScriptPath,
        [Parameter(Mandatory)] [string]$What
    )
    if (-not (Test-Path $ScriptPath)) { throw "$What：找不到子脚本 $ScriptPath" }

    $out = & $Pwsh -NoProfile -ExecutionPolicy Bypass -File $ScriptPath 2>&1
    $code = $LASTEXITCODE
    if ($out) { $out | ForEach-Object { Write-Host "    $_" } }

    # ErrorRecord 是明确的错误信号，不受 $ErrorActionPreference 影响
    $errs = @($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    if ($code -ne 0) { throw "$What 失败（退出码 $code）" }
    if ($errs.Count -gt 0) {
        throw "$What 输出了 $($errs.Count) 条错误（退出码却是 0）：$($errs[0].Exception.Message)"
    }
}

# ---- 内核基线（改基线 = 改这里，必须同时是 PATCHES.md 里的归档 tag 指向的提交）----
$KernelRepo        = "https://github.com/Lake1059/FFF_Project.git"
$KernelBaselineTag = "3fcompare-kernel-2026.9.14.1"
$KernelBaselineSha = "025198f36f5735248b087a050afbe88b3801382a"
# 上一基线（回滚点）：3fcompare-kernel-2026.9.11.1 / 6bc8d61c7fd0a2053e806a627c6db1b4f112e2d9
# 2026-09-15 升级至上游 2026.9.14（d8b2c038）。上游该区间只改了 2 个 vbproj（版本号 +
# Vortice.DirectComposition 包引用），FFF.Native 源码零改动，故 4 项 API 扩展无需重移植。
# ⚠ 该基线在打包时**尚未做内核构建验证**（本机无 MSBuild）。首次在装有 Visual Studio
# 的机器上构建时，S5 的戳记校验会检测到 FFF.Native.dll 仍属旧基线并自动 /t:Rebuild。

# 3FCompare 扩展的云端归档：主仓库自带的 kernel/* 分支 + 同名 tag。
# 上游 Lake1059/FFF_Project **不含**这些扩展，且本机账号对它只有读权限（push=false），
# 所以基线归档只能落在主仓库自己的仓库里。bundle 缺失时用它兜底。
$KernelArchiveRepo   = "https://github.com/luoye-cpu/3FCompare.git"
$KernelArchiveBranch = "kernel/3fcompare-zoom-viewport-cover"

# ---------------------------------------------------------------------------
# 统一 git 调用：显式捕获退出码，不再用 2>$null 吞掉错误（P1-13）
# ---------------------------------------------------------------------------
function Invoke-Git {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$GitArgs
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = & $Git -C $Path @GitArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{
        Exit = $code
        Out  = (($raw | ForEach-Object { $_.ToString() }) -join "`n").Trim()
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

# ---------------------------------------------------------------------------
# [0/5] 确保内核存在且停在钉死基线上
# ---------------------------------------------------------------------------
function Ensure-KernelBaseline {
    $vcxproj = Join-Path $ForkRoot "FFF.Native\FFF.Native.vcxproj"
    if (-not (Test-Path $vcxproj)) {
        # 优先用随仓库携带的基线归档（bundle）恢复。
        # 原因：上游 Lake1059/FFF_Project **不含** 3FCompare 扩展，从它克隆出来的内核是错的；
        # 而归档分支此前只存在于本机，换机器/重装即永久丢失。bundle 让基线可随主仓库分发。
        $bundle = Join-Path $ProjectRoot ".3fc_kernel_baseline.bundle"
        if (Test-Path $bundle) {
            Write-Host "内核目录缺失，从基线归档恢复: $bundle" -ForegroundColor Yellow
            $clone = Invoke-Git $ProjectRoot @("clone", $bundle, $ForkRoot)
            if ($clone.Exit -ne 0) { throw "从基线归档恢复失败（退出码 $($clone.Exit)）: $($clone.Out)" }
        } else {
            # bundle 也不在：退回主仓库自带的云端归档分支。
            # 注意：不能直接克隆上游 —— Lake1059/FFF_Project 不含 3FCompare 扩展，
            # 克隆出来的是错的内核，后面的 Assert-KernelExtensions 才会报错（晚且难查）。
            Write-Host "内核目录缺失，从云端归档恢复: $KernelArchiveRepo" -ForegroundColor Yellow
            Write-Host "  分支 $KernelArchiveBranch" -ForegroundColor Yellow
            $clone = Invoke-Git $ProjectRoot @("clone", "--branch", $KernelArchiveBranch, $KernelArchiveRepo, $ForkRoot)
            if ($clone.Exit -ne 0) {
                Write-Host "  云端归档不可用（网络/权限问题），回退上游: $KernelRepo" -ForegroundColor Yellow
                Write-Host "  警告：上游不含 3FCompare 扩展，检出基线几乎必然失败。" -ForegroundColor Yellow
                $clone = Invoke-Git $ProjectRoot @("clone", $KernelRepo, $ForkRoot)
                if ($clone.Exit -ne 0) { throw "克隆内核仓库失败（退出码 $($clone.Exit)）: $($clone.Out)" }
            }
        }
    }

    $head = (Invoke-Git $ForkRoot @("rev-parse", "HEAD")).Out
    if ($head -ne $KernelBaselineSha) {
        Write-Host "  检出基线提交 $KernelBaselineSha ..." -ForegroundColor Yellow
        $co = Invoke-Git $ForkRoot @("checkout", $KernelBaselineSha)
        if ($co.Exit -ne 0) {
            # 远端可能允许按 SHA 拉取
            $null = Invoke-Git $ForkRoot @("fetch", "origin", $KernelBaselineSha)
            $co = Invoke-Git $ForkRoot @("checkout", $KernelBaselineSha)
        }
        if ($co.Exit -ne 0) {
            $coTag = Invoke-Git $ForkRoot @("checkout", $KernelBaselineTag)
            if ($coTag.Exit -ne 0) {
                throw @"
无法检出内核基线 $KernelBaselineSha（tag: $KernelBaselineTag）。

原因：该提交是 3FCompare 的本地归档（分支 3fcompare/zoom-viewport-cover），
上游 Lake1059/FFF_Project **不包含**这些扩展，且裸克隆也拿不到这个 tag。
恢复方式（任选一）：
  1) 用随仓库携带的基线归档重建（推荐，无需网络与远端权限）：
       git clone .3fc_kernel_baseline.bundle third_party/fff_project
  2) 从主仓库的云端归档分支重建（需网络，无需上游写权限）：
       git clone --branch $KernelArchiveBranch $KernelArchiveRepo third_party/fff_project
  3) 从其它机器的备份恢复 third_party/fff_project（该目录被 .gitignore 忽略，不入库）

注：不要再尝试 push 到上游 $KernelRepo ——
本机账号对它只有读权限（admin=false, push=false），推不上去。
"@
            }
        }
        $head = (Invoke-Git $ForkRoot @("rev-parse", "HEAD")).Out
    }

    if ($head -ne $KernelBaselineSha) {
        $msg = "内核 HEAD ($($head.Substring(0,12))) 与钉死基线 ($($KernelBaselineSha.Substring(0,12))) 不一致。"
        if (-not $AllowKernelDrift) {
            throw @"
$msg
若确需升级内核：先按 third_party/fff_project/PATCHES.md 完成重移植并打新归档 tag，
再同步更新本脚本的 `$KernelBaselineSha。
仅做本地试验可加 -AllowKernelDrift 绕过（产物与基线不同，禁止用于发布）。
"@
        }
        Write-Warning "$msg 已用 -AllowKernelDrift 放行：构建产物与发布基线不同，请勿打包发布。"
    } else {
        Write-Host "  内核基线已锁定 $($KernelBaselineSha.Substring(0,12)) ($KernelBaselineTag)" -ForegroundColor Green
        $dirty = (Invoke-Git $ForkRoot @("status", "--porcelain")).Out
        if ($dirty) {
            Write-Warning "内核工作区有未提交改动，构建结果不可复现："
            Write-Warning $dirty
        }
    }

    # 内核目录内留一份检出记录（该目录不入 git，仅本机可见）。
    # ⚠️ 项目根的 .3fc_kernel_sha **不能在这里写**：它位于成功路径之前，
    # 一旦 Assert-KernelExtensions / msbuild / dotnet build 任一步失败退出，
    # 文件已被刷成基线值，之后单独跑发布门禁就会"内核基线通过 + 增量构建通过"
    # → 用上一次的陈旧产物全绿。故只在脚本末尾成功路径写入（见文件底部）。
    Set-Content -Path (Join-Path $ForkRoot ".3fc_kernel_sha") -Value $head -Encoding ASCII
    return $head
}

# ---------------------------------------------------------------------------
# 内核扩展存在性校验（P1-7）
#   为什么不用「逐 patch 跑 git apply -R --check」来判定扩展是否内置：
#   tools/patches 是**历史归档**，其上下文相对当前基线已漂移 —— 实测 9 个补丁
#   正向与反向 apply 全部失败，据此判断会 100% 误报"扩展缺失"。
#
#   权威判据来自 third_party/fff_project/PATCHES.md：
#     「类别一 4 项重移植，以 PlayerApi 导出面为完成判据」。
#
#   不做这层校验的后果：基线被重置/克隆到同名 SHA 而实际不含扩展时，
#   内核照常编译通过、脚本照常打印成功，托管侧直到运行到对应功能才崩 ——
#   这正是 P1-13 要根治的"用错内核却显示成功"。
# ---------------------------------------------------------------------------
function Assert-KernelExtensions {
    $apiHeader = Join-Path $ForkRoot "FFF.Native\3FP\Api\FFF.Player.Api.h"
    if (-not (Test-Path $apiHeader)) {
        throw "未找到内核 API 头文件：$apiHeader`n内核树结构异常，无法确认扩展是否内置。"
    }
    $text = Get-Content $apiHeader -Raw -Encoding UTF8
    $required = @(
        'FFF3FP_SetPresentConfig',
        'FFF3FP_SetPacingConfig',
        'FFF3FP_GetRenderTargetInfo',
        'FFF3FP_ReadVideoPixelRegion'
    )
    $missing = @()
    foreach ($fn in $required) {
        if ($text -notmatch [regex]::Escape($fn)) { $missing += $fn }
    }
    if ($missing.Count -gt 0) {
        throw @"
内核缺少 3FCompare 硬依赖的扩展 API（$($missing.Count)/$($required.Count) 缺失）：
  $($missing -join ', ')

判定依据：PATCHES.md「类别一」的完成判据是 PlayerApi 导出面。
缺少这些 API 时内核照样能编译，但托管侧会在运行到对应功能时才失败。
处理：
  · 确认内核停在正确的归档分支 3fcompare/zoom-viewport-cover；
  · 若内核已升级，按 third_party/fff_project/PATCHES.md 完成重移植后再构建。
"@
    }
    Write-Host "  内核扩展校验通过（$($required.Count)/$($required.Count) 项 API 齐全）" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 补丁重放（P1-12：幂等）
#   旧实现用「补丁 mtime > 标记文件 mtime」判定，且失败只标记不阻断：
#   一旦任一补丁失败 → 标记不刷新 → 下次重跑全部 → 已应用的必然再失败 → 永久卡死。
#   新实现：先 git apply -R --check 反向探测，能反向应用即视为已应用 → 跳过；
#           真失败直接 throw（可用 -SkipPatches 明确放行）。
# ---------------------------------------------------------------------------
function Invoke-KernelPatches {
    param([string]$HeadSha)

    $PatchesDir = Join-Path $PSScriptRoot "patches"
    if (-not (Test-Path $PatchesDir)) { return }
    $patches = @(Get-ChildItem $PatchesDir -Filter *.patch | Sort-Object Name)
    if ($patches.Count -eq 0) { return }

    if ($SkipPatches) {
        Write-Host "已按 -SkipPatches 跳过 $($patches.Count) 个补丁" -ForegroundColor Yellow
        return
    }

    # 注：这里跳过重放**不再**是"无条件信任"。扩展是否内置已由 Assert-KernelExtensions
    # 按 PlayerApi 导出面独立校验过；tools/patches 只是历史归档（正反向均 apply 不上），
    # 无法也不应作为判据。
    if ((-not $ForcePatches) -and ($HeadSha -eq $KernelBaselineSha)) {
        Write-Host "内核已是钉死基线，跳过 tools/patches 重放。" -ForegroundColor Green
        Write-Host "  （扩展存在性已按 PlayerApi 导出面校验；tools/patches 是历史归档，"
        Write-Host "   新机器重移植流程见 third_party/fff_project/PATCHES.md。）强制重放请加 -ForcePatches。"
        return
    }

    Write-Host "重放 3FCompare 自定义补丁（$($patches.Count) 个）..."
    $applied = @()
    foreach ($p in $patches) {
        # 1) 反向探测：已应用则跳过（幂等的关键）
        $rev = Invoke-Git $ForkRoot @("apply", "-R", "--check", "--ignore-whitespace", $p.FullName)
        if ($rev.Exit -eq 0) {
            Write-Host "  ⏭  $($p.Name) 已应用，跳过" -ForegroundColor Gray
            continue
        }
        # 2) 正向应用：先常规，失败再 --3way（容忍上下文漂移）
        $fwd = Invoke-Git $ForkRoot @("apply", "--ignore-whitespace", $p.FullName)
        if ($fwd.Exit -ne 0) {
            $fwd = Invoke-Git $ForkRoot @("apply", "--3way", "--ignore-whitespace", $p.FullName)
        }
        if ($fwd.Exit -ne 0) {
            throw @"
补丁应用失败: $($p.Name)
  $($fwd.Out)
处理建议：
  · 若内核树已自带该改动（基线分支常如此），用 -SkipPatches 跳过；
  · 若确实需要，请按 third_party/fff_project/PATCHES.md 手工重移植后更新补丁文件。
"@
        }
        Write-Host "  ✅ $($p.Name) 已应用" -ForegroundColor Green
        $applied += $p.Name
    }

    # 仅作追溯记录；幂等性由反向探测保证，不再依赖时间戳
    $marker = Join-Path $ForkRoot ".3fc_patches_applied"
    ($applied -join "`n") | Set-Content -Path $marker -Encoding UTF8
    Write-Host "补丁处理完成（本次新应用 $($applied.Count) 个）" -ForegroundColor Green
}

$kernelSha = Ensure-KernelBaseline
Assert-KernelExtensions
Invoke-KernelPatches -HeadSha $kernelSha

Write-Host "=== [1/5] 准备 FFmpeg（若缺失） ==="
$ffmpegMarker = Join-Path $ForkRoot "third_party\ffmpeg\include\libavcodec\avcodec.h"
if (-not (Test-Path $ffmpegMarker)) {
    Push-Location $ForkRoot
    try {
        Invoke-SubScript -ScriptPath ".\tools\准备FFmpeg.ps1" -What "准备 FFmpeg"
        # 补齐上游脚本偶发遗漏的生成头
        $cache = Join-Path $env:LOCALAPPDATA "fff-ffmpeg-download\extracted\ffmpeg-master-latest-win64-lgpl-shared\include"
        if (Test-Path (Join-Path $cache "libavutil\avconfig.h")) {
            Copy-Item (Join-Path $cache "libavutil\avconfig.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force
            Copy-Item (Join-Path $cache "libavutil\ffversion.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force -ErrorAction SilentlyContinue
        }
    } finally { Pop-Location }
} else {
    Write-Host "  FFmpeg 已就绪，跳过"
}

Write-Host "=== [2/5] 准备 libass（若缺失） ==="
$assMarker = Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\include\ass\ass.h"
if (-not (Test-Path $assMarker)) {
    Push-Location $ForkRoot
    try {
        Invoke-SubScript -ScriptPath ".\tools\准备Libass.ps1" -What "准备 libass"
    } finally { Pop-Location }
} else {
    Write-Host "  libass 已就绪，跳过"
}

Write-Host "=== [3/5] 构建 FFF.Native ==="
$msbuild = Get-MSBuildPath

# ---- S5：把「实际部署的 DLL」与「内核 HEAD」绑定 ----
# Assert-KernelExtensions 只校验**头文件文本**，证明不了 x64/<配置> 下的 DLL 就是这个 HEAD 编出来的。
# 而 x64/ 被 .gitignore 且从不清空：一旦内核切过分支或回退过，陈旧 DLL 会被继续复用并部署出去，
# 现象是"构建全绿，但运行行为与基线对不上"，极难归因。
# 做法：内核构建成功后，在 DLL 旁写戳记（第 1 行=内核 HEAD，第 2 行=DLL 的 SHA256）；
#       下次构建前比对戳记里的 HEAD —— 不一致、或压根没有戳记，就强制 /t:Rebuild。
$kernelDll = Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll"
$stampPath = "$kernelDll.3fcbuild"

function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$needRebuild = $false
if (Test-Path $kernelDll) {
    if (-not (Test-Path $stampPath)) {
        $needRebuild = $true
        Write-Host "  DLL 存在但没有构建戳记，来源不明 → 强制重建（S5）" -ForegroundColor Yellow
    } else {
        $stampHead = (Get-Content $stampPath -TotalCount 1).Trim()
        if ($stampHead -ne $kernelSha) {
            $needRebuild = $true
            Write-Host "  DLL 由旧内核 HEAD 编出（戳记 $(Short-Sha $stampHead) ≠ 当前 $(Short-Sha $kernelSha)）→ 强制重建（S5）" -ForegroundColor Yellow
        }
    }
}

Push-Location $ForkRoot
try {
    $msbuildArgs = @("FFF.Native\FFF.Native.vcxproj", "/p:Configuration=$Configuration", "/p:Platform=x64", "/m", "/v:minimal")
    if ($needRebuild) { $msbuildArgs += "/t:Rebuild" }
    & $msbuild @msbuildArgs
    if ($LASTEXITCODE -ne 0) { throw "FFF.Native 构建失败" }
    if (-not (Test-Path $kernelDll)) { throw "构建后未找到产物：$kernelDll" }
    # 构建成功 → 刷新戳记，供下次比对（源 DLL 自此与该 HEAD 绑定）
    Set-Content -Path $stampPath -Value "$kernelSha`n$(Get-FileSha256 $kernelDll)" -Encoding ASCII
} finally { Pop-Location }

Write-Host "=== [4/5] 部署 DLL 到应用与冒烟目录 ==="
function Deploy-To($targetDir) {
    $src = Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll"
    if (-not (Test-Path $src)) { throw "未找到构建产物: $src" }
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item $src $targetDir -Force

    # S5：部署后校验落地副本与源一致。源 DLL 已由 $stampPath 绑定到内核 HEAD，
    # 这条再把"源 → 目标"这一段钉死，堵住复制被占用/跳过导致的"部署了旧 DLL"。
    $dst = Join-Path $targetDir "FFF.Native.dll"
    $srcHash = Get-FileSha256 $src
    $dstHash = Get-FileSha256 $dst
    if ($srcHash -ne $dstHash) {
        throw "部署校验失败：$dst 与源不一致（$dstHash ≠ $srcHash）——可能复制被占用或跳过"
    }

    # FFmpeg 共享库：数量为 0 必须报错。此前 Get-ChildItem 不校验数量，
    # 缺件时脚本照样打印"已部署"，应用起来后静默回退演示模式，排查成本很高。
    $ffDlls = @(Get-ChildItem (Join-Path $ForkRoot "runtime\*.dll"))
    if ($ffDlls.Count -eq 0) {
        throw "未找到 FFmpeg 运行库：$ForkRoot\runtime\*.dll（请先在内核目录准备 FFmpeg）"
    }
    $ffDlls | ForEach-Object { Copy-Item $_.FullName $targetDir -Force }

    # libass：只影响字幕渲染，缺失不致命，故告警而非中断。
    $assDll = Get-ChildItem (Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\bin\ass-9.dll") -ErrorAction SilentlyContinue
    $hasAss = $false
    if ($assDll) { Copy-Item $assDll.FullName $targetDir -Force; $hasAss = $true }
    else { Write-Warning "未找到 ass-9.dll，字幕渲染将不可用" }

    Write-Host "  已部署 -> $targetDir （FFmpeg $($ffDlls.Count) 个 DLL / ass-9: $(if ($hasAss) { '有' } else { '无' })）"
}

$appBin = Join-Path $ProjectRoot "src\3FCompare\bin\$Configuration\net11.0-windows"
$smokeBin = Join-Path $ProjectRoot "tests\3FCompare.SmokeTests\bin\$Configuration\net11.0"
Deploy-To $appBin
Deploy-To $smokeBin

# P0-2 修复：主程序构建原先被塞在 `if (-not $SkipTests)` 里，
# 结果 -SkipTests 会把 3FCompare.exe 本身一起跳过，脚本末尾却照样打印"✔ 全部完成"
# ——典型的假成功。主程序是产物本体，永远不能被"跳过测试"连带跳过。
Write-Host "=== [5/5] 构建 .NET 解决方案 ==="
Push-Location $ProjectRoot
try {
    # KernelConfiguration 必须随 -Configuration 透传：
    # csproj 用它拼内核 DLL 路径（默认 Release），若不同步，
    # Debug 构建会去找 x64\Release\FFF.Native.dll 然后被 CheckKernelDll 硬失败拦下。
    & $Dotnet build .\src\3FCompare.slnx -c $Configuration -p:KernelConfiguration=$Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET 解决方案构建失败（退出码 $LASTEXITCODE）" }
} finally { Pop-Location }

if (-not $SkipTests) {
    Write-Host "=== [可选] 运行单元测试 ==="
    Push-Location $ProjectRoot
    try {
        & $Dotnet test .\tests\3FCompare.Core.Tests\3FCompare.Core.Tests.csproj -c $Configuration --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw "单元测试失败（退出码 $LASTEXITCODE）" }
    } finally { Pop-Location }
} else {
    Write-Host "已按 -SkipTests 跳过单元测试（主程序仍已构建）" -ForegroundColor Yellow
}

# 项目根的 .3fc_kernel_sha 只在**成功路径**写入：发布门禁 [1/6] 靠它判断
# "产物内核 == 发布基线"。若提前写在 Ensure-KernelBaseline 里，一旦
# Assert-KernelExtensions / msbuild / dotnet build 任一步失败退出，
# 文件已被刷成基线值，之后单独跑门禁就会用上一次的陈旧产物全绿。
Set-Content -Path (Join-Path $ProjectRoot ".3fc_kernel_sha") -Value $kernelSha -Encoding ASCII

Write-Host "✔ 全部完成。内核 SHA: $kernelSha"
Write-Host "✔ 运行冒烟: dotnet run --project tests/3FCompare.SmokeTests -- <视频>"
Write-Host "✔ 运行应用: dotnet run --project src/3FCompare"
