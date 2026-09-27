<#
.SYNOPSIS
    产物新鲜度守卫（docs/49 切片 1）：跑任何 GUI 自测之前，先证明"要跑的这个二进制
    不早于喂给它的源码"；可选顺带执行构建，并且**构建失败绝不允许回退去跑旧产物**。

.DESCRIPTION
    为什么需要它（2026-09-26 两次真实假读数）：
      ① `dotnet build` 报 MSB3021/MSB3027「文件被 3FCompare (PID) 锁定」= 产物没更新，
         当时脚本仍继续跑，于是拿到一份"引用了源码里已删文案"的判红消息；
      ② 拿旧 exe 复跑新写的判据，跑通了也不作数。
    两者只要比较"最新输入写入时间 vs 产物写入时间"就能当场拦住。

    方向性约定：**一切不确定都判陈旧**（宁可多做一次构建，绝不放走陈旧产物）。
    已知取舍：mtime 是保守失效 —— `git checkout` / 复制会刷新 mtime ⇒ 误判为过期，
    代价只是一次重建。跨机器拿来的产物（发布包）没有对应源码树 ⇒ 用 -SkipSourceCheck
    显式降级，但必须把"未经新鲜度校验"写进结论。

.PARAMETER Build
    先执行 Release 构建；构建非零即 exit=4（不回退旧产物）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\验证守卫.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$Exe = '',
    [switch]$Build,
    [switch]$SkipSourceCheck,
    [int]$ToleranceSec = 2,
    # 每个源码类别至少枚举到多少个文件才算"判据成立"。取 20 是因为 src\ 下实有 90+ 个输入：
    # 枚举突然只剩几个 = glob/参数配错了，此时必须拦下而不是放行。
    [int]$MinClassFiles = 20,
    # 输入集合（相对 $Root）。src\ 是托管代码与 XAML；内核 DLL 是被嵌进 exe 的输入。
    # 不纳入 3fp\（独立的上游 PR 工作区）与 publish\（完整 FFmpeg 源）—— 它们不是本程序的构建输入，
    # 纳入会被另一路写者的动作天天误拦。
    [string[]]$InputDirs = @('src'),
    [string[]]$SourceExt = @('.cs', '.axaml', '.csproj', '.slnx'),
    [string[]]$NativeInputs = @('third_party\fff_project\FFF.Native')
)

$ErrorActionPreference = 'Continue'   # 原生命令写 stderr 时，Stop 会把它抛成终止错误

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $Exe)  { $Exe = Join-Path $Root 'src\3FCompare\bin\Release\net11.0-windows\3FCompare.exe' }
$proj = Join-Path $Root 'src\3FCompare\3FCompare.csproj'

# 用 .NET 枚举而不是 Get-ChildItem -Path 'src\**\*.cs' -Recurse：
# 后者在本机 Windows PowerShell 5.1 下**静默枚举不到 .cs**（第一版因此只比了个 09-22 的 .slnx，
# 把"源码比产物新"判成"新鲜"并放行 —— 守卫自己成了假绿的源头）。
function Get-NewestFiles([string]$dir, [string[]]$exts) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $all = [System.IO.Directory]::GetFiles($dir, '*', [System.IO.SearchOption]::AllDirectories)
    $keep = New-Object System.Collections.Generic.List[object]
    foreach ($p in $all) {
        if ($p -match '\\(bin|obj|\.vs|\.idea)\\') { continue }
        if ($p -match '\\tests\\output\\') { continue }
        if ($exts -notcontains [System.IO.Path]::GetExtension($p)) { continue }
        $keep += [pscustomobject]@{ Path = $p; Stamp = [System.IO.File]::GetLastWriteTimeUtc($p) }
    }
    $keep | Sort-Object Stamp -Descending
}

function Format-Stamp([datetime]$utc) { $utc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z' }

Write-Host "  守卫：$Exe" -ForegroundColor Gray

if ($Build) {
    Write-Host "  · 构建 Release（0 error 是硬前提）" -ForegroundColor Gray
    $out  = & dotnet build $proj -c Release --no-restore --nologo 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $reason = ($out | Where-Object { "$_" -match 'error|MSB3027|MSB3021' } | Select-Object -First 3) -join "`n      "
        Write-Host "    ✗ 构建失败 (exit=$code) —— 拒绝回退旧产物，本轮结果不作数" -ForegroundColor Red
        if ($reason) { Write-Host "      $reason" }
        exit 4
    }
    Write-Host "    ✓ 构建通过" -ForegroundColor Green
}

if ($SkipSourceCheck) {
    Write-Host "    ⚠ 已显式跳过源码新鲜度校验：本报告不得写「产物已验证」" -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "    ✗ 找不到产物 $Exe（先 -Build，或显式传 -Exe）" -ForegroundColor Red
    exit 5
}

# ── 收集输入：**按类别分别统计** ──
# 只取"全局最新的那一个文件"会被单一类别代表全部：第一版就是这样 —— 它一枚举不到 .cs，
# 就拿一个 09-22 的 .slnx 比了比，把"源码比产物新"判成"新鲜"并放行。
# 现在每个类别都要凑够最低数量，否则按"判据本身失效"拦下（fail-closed）。
$classes = @()
foreach ($d in $InputDirs) {
    $f = Get-NewestFiles (Join-Path $Root $d) $SourceExt
    $classes += [pscustomobject]@{ Name = "源码 \$d"; Files = $f; Min = $MinClassFiles }
}
foreach ($d in $NativeInputs) {
    $f = Get-NewestFiles (Join-Path $Root $d) @('.dll')
    $classes += [pscustomobject]@{ Name = "内核 $d"; Files = $f; Min = 1 }
}

$thin = $classes | Where-Object { $_.Files.Count -lt $_.Min }
if ($thin) {
    Write-Host "    ✗ 构建输入枚举不足 —— 判据本身失效，不确定即拦下（不判新鲜）" -ForegroundColor Red
    foreach ($t in $thin) {
        Write-Host ("      {0}: 枚举到 {1} 个，要求 ≥ {2}" -f $t.Name, $t.Files.Count, $t.Min) -ForegroundColor Yellow
    }
    Write-Host ("      InputDirs={0}  SourceExt={1}" -f ($InputDirs -join ', '), ($SourceExt -join ', ')) -ForegroundColor DarkGray
    exit 6
}

# ── 产物时间：代码在 DLL 里，apphost 可能单独更新 ⇒ 取二者中较旧者为产物年龄下限 ──
$exeItem = Get-Item -LiteralPath $Exe
$asmPath = Join-Path $exeItem.DirectoryName '3FCompare.dll'
$cands = @([pscustomobject]@{ Path = $Exe; Stamp = $exeItem.LastWriteTimeUtc })
if (Test-Path -LiteralPath $asmPath) {
    $cands += [pscustomobject]@{ Path = $asmPath; Stamp = [System.IO.File]::GetLastWriteTimeUtc($asmPath) }
}
$artifact = $cands | Sort-Object Stamp | Select-Object -First 1

# ── 逐类别对照：两侧都报出文件名与时间，不写文件名的新鲜度结论不可诊断 ──
Write-Host ("    产物 {0}  {1}" -f (Split-Path -Leaf $artifact.Path), (Format-Stamp $artifact.Stamp)) -ForegroundColor DarkGray
$stale = @()
foreach ($c in $classes) {
    $n = $c.Files[0]
    Write-Host ("    输入 {0}  共 {1} 个  最新 {2}  {3}" -f `
        $c.Name, $c.Files.Count, $n.Path.Replace((Join-Path $Root '\'), ''), (Format-Stamp $n.Stamp)) -ForegroundColor DarkGray
    if ($n.Stamp -gt $artifact.Stamp.AddSeconds($ToleranceSec)) { $stale += [pscustomobject]@{ Class = $c.Name; File = $n } }
}

if ($stale.Count -gt 0) {
    foreach ($s in $stale) {
        $lag = [math]::Round(($s.File.Stamp - $artifact.Stamp).TotalSeconds, 0)
        Write-Host ("    ✗ 产物陈旧：{0} 的最新输入比产物晚 {1:N0} s ⇒ 跑它等于跑旧代码" -f $s.Class, $lag) -ForegroundColor Red
        Write-Host ("      {0}" -f $s.File.Path) -ForegroundColor Yellow
    }
    Write-Host "      处置：tools\验证守卫.ps1 -Build（被文件占用时，先关掉正在运行的 3FCompare.exe）" -ForegroundColor Yellow
    exit 3
}

Write-Host "    ✓ 产物不早于任何一类输入" -ForegroundColor Green

# ── 内容指纹（比 mtime 更强：mtime 会漏掉"内容变了但时间没变"，也会因 checkout 误报）──
# 基准由构建期 tools/源码指纹.ps1 落在输出目录；老产物没有它 ⇒ 明说退回 mtime，不伪装成内容级校验。
$fpTxt = Join-Path (Split-Path -Parent $Exe) 'source-fingerprint.txt'
$fpScript = Join-Path $PSScriptRoot '源码指纹.ps1'
if ((Test-Path -LiteralPath $fpTxt) -and (Test-Path -LiteralPath $fpScript)) {
    $rc = & powershell -NoProfile -ExecutionPolicy Bypass -File $fpScript `
            -ProjectDir (Join-Path $Root 'src\3FCompare') -CompareTo $fpTxt
    $fcode = $LASTEXITCODE
    foreach ($l in $rc) { Write-Host "      $l" -ForegroundColor DarkGray }
    if ($fcode -ne 0) {
        Write-Host "    ✗ 内容指纹不一致 ⇒ 这份产物不含当前源码，拒绝（跑它等于跑旧代码）" -ForegroundColor Red
        exit 3
    }
    $fpItemCount = ((Get-Content -LiteralPath $fpTxt -Raw) -split '\|').Count
    Write-Host ("    ✓ 内容指纹一致（App 工程 {0} 项输入逐项核对）" -f $fpItemCount) -ForegroundColor Green
} elseif (-not $SkipSourceCheck) {
    Write-Host "    ⚠ 该产物没有 source-fingerprint.txt（早于指纹机制）⇒ 只做了 mtime 级校验，" -ForegroundColor Yellow
    Write-Host "      不能排除「内容变了但时间戳没变」这类陈旧；重建一次再依赖此判据。" -ForegroundColor Yellow
}

exit 0
