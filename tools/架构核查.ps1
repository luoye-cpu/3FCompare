# 架构核查探针（ARM 前瞻准备，docs/53 §二）
#
# 用途：读出仓库内**所有原生二进制**的 PE 机器类型，并报告"与给定架构是否一致"。
#
# 为什么要有它（而不是把它塞进 发布门禁.ps1）：
#   出 ARM64 包那天最可能的翻车形状是"内嵌 Dll 的 LogicalName 是架构中立的（FFF.Native.dll /
#   libSkiaSharp.dll / …），于是 x64 的产物被静默嵌进 arm64 的包"，而**现有守卫只查 Exists()**。
#   本脚本把这件事变成一条可复核的命令行判据，且**默认不参与任何既有流程**——
#   不跑就绝不可能弄坏现在的 x64 构建（这是"前瞻准备"的底线）。
#
# 用法：
#   pwsh -File tools/架构核查.ps1                     # 默认按当前进程架构核查
#   pwsh -File tools/架构核查.ps1 -Expected x64        # 显式指定期望架构
#   pwsh -File tools/架构核查.ps1 -Expected arm64 -Quiet
#
# 退出码：0 = 全部一致（或读不出机器类型的项为 0 且无错配）；1 = 存在错配。
#   读不出机器类型的文件**只警告不判红**——加壳/非常规产出不应阻断发布。

[CmdletBinding()]
param(
    # 期望架构：x64 / arm64 / x86。留空则取当前 PowerShell 进程的架构。
    [ValidateSet("", "x64", "arm64", "x86")]
    [string]$Expected = "",

    # 只输出结论行（供脚本/CI 取用）。
    [switch]$Quiet,

    # 额外要核查的路径（默认已含仓库内已知的原生件所在目录）。
    [string[]]$ExtraPath = @(),

    # 不递归扫描 third_party（那里有 4 万+ 文件，其中绝大多数是 vcpkg 源码树里的
    # 第三方 .dll/.exe，与"我们要发布的产物"无关；默认只查真正会被打包的位置）。
    [switch]$IncludeThirdParty
)

$ErrorActionPreference = "Stop"

$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Get-PeMachine([string]$Path) {
    <#
      读 PE 头机器类型。
        布局：DOS 头 0x3C 处 e_lfanew(4B) → "PE\0\0"(4B) → COFF 头 Machine(2B)。
      返回机器类型码（uint16），读不出返回 0。**任何异常都返回 0**，不向上抛：
      本探针是"诊断工具"，它自己崩掉就失去意义了。
    #>
    try {
        # 只读前 4 KiB：这些 DLL 有几十 MB，整文件读会白白吃 I/O 与内存。
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $head = New-Object byte[] 4096
            $read = $fs.Read($head, 0, $head.Length)
            if ($read -lt 0x40) { return 0 }
            $peOffset = [BitConverter]::ToInt32($head, 0x3C)
            if ($peOffset -lt 0 -or $peOffset -gt ($read - 24)) { return 0 }
            if ($head[$peOffset] -ne 0x50 -or $head[$peOffset + 1] -ne 0x45 -or
                $head[$peOffset + 2] -ne 0 -or $head[$peOffset + 3] -ne 0) { return 0 }
            return [BitConverter]::ToUInt16($head, $peOffset + 4)
        }
        finally { $fs.Dispose() }
    }
    catch { return 0 }
}

function Get-MachineName([uint16]$Machine) {
    switch ($Machine) {
        0x8664  { "x64" }        # 亦为 Arm64EC（EC 的 PE 头就是 x64）
        0xAA64  { "arm64" }
        0x014C  { "x86" }
        0x01C4  { "arm" }        # ARM (32-bit thumb-2)
        0x0200  { "ia64" }
        0       { "未知(读不出 PE 头)" }
        default { "未知(0x{0:X4})" -f $Machine }
    }
}

# 由架构名推出该架构下"可被加载"的机器类型集合。
function Get-AcceptableMachines([string]$Arch) {
    switch ($Arch) {
        # x64 进程可加载 x64 与 Arm64EC（PE 头同为 0x8664）。
        "x64"   { @(0x8664) }
        "arm64" { @(0xAA64) }
        "x86"   { @(0x014C) }
        default { @() }
    }
}

if (-not $Expected) {
    $Expected = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        "X64"   { "x64" }
        "Arm64" { "arm64" }
        "X86"   { "x86" }
        default { "x64" }
    }
}

$acceptable = Get-AcceptableMachines $Expected

# 要扫描的位置：只列**会进发布包**或**会被加载**的目录。
#
# ⚠ 设计取舍（第一版踩过的坑）：**不要扫 `bin/`**。原因有三，且都不是真缺陷：
#   ① 托管程序集（Avalonia.*.dll / 3FCompare.dll…）的 PE 头是 I386(0x014C) ——
#      它们是 IL，由 JIT/AOT 决定实际架构，**与"原生库架构"不是一回事**；
#   ② `runtimes\win-arm64\` / `runtimes\win-x86\` 这类**别的架构的资产**本来就该躺在包里
#      （Avalonia 的多架构原生资产），在 x64 视角下"错配"是预期的；
#   ③ `bin\Debug|Release` 是构建中间产物，不是发布物。
# 扫它们只会淹没有效信号（第一版实测：478 个文件里 277 个"错配"，全是上面三类）。
# 因此默认只扫**真正被加载的那几处**：仓库根的 runtime/、自研 WGC 库、
# 以及**发布目录**（publish/，迁移时才是重点）。
$roots = New-Object System.Collections.Generic.List[string]
foreach ($rel in @(
        "runtime",                    # 共享 FFmpeg DLL 组 + 6 个 clang 运行期 DLL（构建全部.ps1 会拷进产物）
        "native\wgc_capture",         # 自研抓屏库（3FC.WgcCapture.dll → 内嵌）
        "third_party\fff_project\runtime",
        "third_party\fff_project\FFF.Native\x64\Release",
        "third_party\fff_project\FFF.Native\x64\Debug",
        "third_party\fff_project\FFF.Native\Win32\Release",
        "publish"                     # 出包产物（存在时才有意义）
    )) {
    $full = Join-Path $ProjectRoot $rel
    if (Test-Path -LiteralPath $full) { $roots.Add($full) | Out-Null }
}
if ($IncludeThirdParty) {
    $tp = Join-Path $ProjectRoot "third_party"
    if (Test-Path -LiteralPath $tp) { $roots.Add($tp) | Out-Null }
}
foreach ($p in $ExtraPath) {
    if (Test-Path -LiteralPath $p) { $roots.Add((Resolve-Path -LiteralPath $p).Path) | Out-Null }
}

if ($roots.Count -eq 0) {
    Write-Host "未找到任何待核查目录（仓库尚未构建过？）" -ForegroundColor Yellow
    exit 0
}

# 只核查**原生**候选：托管程序集会被排除（见上方 ①）。
# 判据用"是否被 CLR 头标记为 IL"——即 PE 可选头里的 COR20 (COM descriptor) 目录非零。
# 这不是必需的精确判定，只是把明显的托管程序集滤掉；滤错也只是少报，不影响"错配必报"。
function Test-IsManagedAssembly([string]$Path) {
    try {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $head = New-Object byte[] 8192
            $read = $fs.Read($head, 0, $head.Length)
            if ($read -lt 0x40) { return $false }
            $peOffset = [BitConverter]::ToInt32($head, 0x3C)
            if ($peOffset -lt 0 -or $peOffset -gt ($read - 24)) { return $false }

            # COFF 头(20B) 之后是可选头。OptionalHeader 的大小在 COFF 头偏移 +16 处（2B）。
            $optSize = [BitConverter]::ToUInt16($head, $peOffset + 4 + 16)
            if ($optSize -le 0) { return $false }
            $optStart = $peOffset + 4 + 20
            # 可选头 Magic：0x10B = PE32(32 位)，0x20B = PE32+(64 位)。数据目录起点不同。
            $magic = [BitConverter]::ToUInt16($head, $optStart)
            $dataDirStart = if ($magic -eq 0x20B) { $optStart + 112 } elseif ($magic -eq 0x10B) { $optStart + 96 } else { return $false }
            # 数据目录第 15 项（索引 14）= CLR Runtime Header (COM20)。每项 8 字节，取 RVA(4B)。
            $cor20 = $dataDirStart + (14 * 8)
            if ($cor20 + 4 -gt $read) { return $true }   # 读不到就当托管，宁可少报
            return ([BitConverter]::ToUInt32($head, $cor20) -ne 0)
        }
        finally { $fs.Dispose() }
    }
    catch { return $false }
}

$files = foreach ($root in $roots) {
    Get-ChildItem -LiteralPath $root -Recurse -File -Include *.dll, *.exe -ErrorAction SilentlyContinue
}
$files = $files | Sort-Object FullName -Unique

$mismatched = New-Object System.Collections.Generic.List[object]
$unreadable = New-Object System.Collections.Generic.List[object]
$skippedManaged = 0
$okCount = 0

foreach ($f in $files) {
    if (Test-IsManagedAssembly $f.FullName) { $skippedManaged++; continue }
    $machine = Get-PeMachine $f.FullName
    if ($machine -eq 0) {
        $unreadable.Add($f) | Out-Null
        continue
    }
    # I386 机器类型在**原生**文件里就是真 x86；在托管里才是"AnyCPU"，而托管已在上面滤掉。
    if ($acceptable -contains $machine) { $okCount++; continue }
    $mismatched.Add([pscustomobject]@{
        Path    = $f.FullName
        Machine = (Get-MachineName $machine)
    }) | Out-Null
}

if (-not $Quiet) {
    Write-Host ""
    Write-Host "架构核查探针（docs/53 §二）" -ForegroundColor Cyan
    Write-Host "  期望架构 : $Expected  （可接受的机器类型: $(($acceptable | ForEach-Object { '0x{0:X4}' -f $_ }) -join ', ')）"
    Write-Host "  扫描根   : $($roots.Count) 个目录"
    Write-Host "  原生文件 : $($files.Count) 个（一致 $okCount / 错配 $($mismatched.Count) / 读不出 $($unreadable.Count) / 跳过托管 $skippedManaged）"
    Write-Host ""
}

if ($mismatched.Count -gt 0) {
    Write-Host "❌ 以下原生二进制与期望架构 **$Expected** 不一致：" -ForegroundColor Red
    foreach ($m in $mismatched) {
        Write-Host ("   {0,-8} {1}" -f $m.Machine, $m.Path) -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "   典型成因：内嵌资源的 LogicalName 是架构中立的，于是**另一个架构**的产物被嵌了进来；" -ForegroundColor Yellow
    Write-Host "   或同一目录残留了上一个架构的包（见 docs/53 §二「静默错载」）。" -ForegroundColor Yellow
    if ($unreadable.Count -gt 0) {
        Write-Host "   （另有 $($unreadable.Count) 个文件读不出 PE 头，未参与判定）" -ForegroundColor DarkGray
    }
    exit 1
}

if (-not $Quiet) {
    Write-Host "✅ 未发现与期望架构 $Expected 错配的原生二进制。" -ForegroundColor Green
    if ($unreadable.Count -gt 0) {
        Write-Host "   （$($unreadable.Count) 个文件读不出 PE 头，已跳过——不判红，见脚本头注释）" -ForegroundColor DarkGray
        if ($VerbosePreference -ne 'SilentlyContinue') {
            foreach ($u in $unreadable) { Write-Host "      $($u.FullName)" -ForegroundColor DarkGray }
        }
    }
    Write-Host ""
}
exit 0
