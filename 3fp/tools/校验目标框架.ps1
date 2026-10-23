#Requires -Version 5.1
<#
.SYNOPSIS
    3FP 目标框架（TFM）一致性门禁。

.DESCRIPTION
    TFM 的默认值分散在四个 vbproj 与若干构建/发布脚本里（这是 MSBuild 求值顺序
    决定的：见下方 NOTES）。分散就必然存在「改了三个忘了第四个」的漂移风险，
    本脚本把这种漂移变成一条明确报错。

    检查三项：
      1. 四个托管工程的 <NetTFM> / <LakeUITfm> 默认值彼此一致；
      2. 各构建/发布脚本的 $NetTFM 默认值与工程一致；
      3. 没有任何工程绕过参数化、把 TargetFramework 写成字面量。

.NOTES
    为什么默认值不放在 Directory.Build.props：
      <Project Sdk="..."> 属性形式会把 SDK props 隐式插到项目文件最顶部，先于
      文件里任何 Import 执行，写在 vbproj 里的 Import 永远晚于 SDK 的 TFM 求值
      （报 NETSDK1241）。实测 Directory.Build.props 在本仓库同样未生效，因此
      改为各工程就地定义，由本脚本兜住一致性。

.EXAMPLE
    pwsh -File 3fp\tools\校验目标框架.ps1
    pwsh -File 3fp\tools\校验目标框架.ps1 -Expected net11.0-windows10.0.26100.0
#>
[CmdletBinding()]
param(
    # 若指定，则要求所有位置都已是该 TFM（GA 当天切到 net11 后用于验收）
    [string]$Expected = '',
    # 缺失条目是"就没配 TFM"还是"有意省略"难以区分，默认只警告不拦截
    [switch]$AllowMissing
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$kernel = Join-Path $root 'kernel'

# ── 采集：vbproj ────────────────────────────────────────────────────────────
$vbprojRel = @(
    'kernel\FFF.Player\FFF.Player.vbproj',
    'kernel\FFF.Recorder\FFF.Recorder.vbproj',
    'kernel\FFF.Player.Tests\FFF.Player.Tests.vbproj',
    'kernel\FFF.Recorder.Tests\FFF.Recorder.Tests.vbproj'
)

function Get-VbprojDefault {
    param([string]$LiteralPath, [string]$Name)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { return $null }
    $text = Get-Content -LiteralPath $LiteralPath -Raw
    $m = [regex]::Match($text, '<' + $Name + ' Condition="[^"]*">([^<]+)</' + $Name + '>')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

$entries = @()
foreach ($rel in $vbprojRel) {
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full)) {
        $entries += [pscustomobject]@{Kind = 'vbproj'; Item = $rel; Tfm = $null; Note = '文件不存在' }
        continue
    }
    $tfm = Get-VbprojDefault -LiteralPath $full -Name 'NetTFM'
    $lake = Get-VbprojDefault -LiteralPath $full -Name 'LakeUITfm'
    $text = Get-Content -LiteralPath $full -Raw

    # 绕过参数化的检测：TargetFramework 直接写成字面量
    $hard = [regex]::Match($text, '<TargetFramework>\s*net')
    if ($hard.Success) {
        $entries += [pscustomobject]@{Kind = 'vbproj'; Item = $rel; Tfm = $tfm; Note = 'TargetFramework 被写成字面量，绕过了 $(NetTFM) 参数化' }
    }
    else {
        $entries += [pscustomobject]@{Kind = 'vbproj'; Item = $rel; Tfm = $tfm; Note = 'LakeUI=' + $(if ($lake) { $lake } else { '（未设，仅 Player/Recorder 需要）' }) }
    }
}

# ── 采集：脚本 ──────────────────────────────────────────────────────────────
$scriptDirs = @((Join-Path $root 'tools'), (Join-Path $kernel 'tools'))
$scriptFiles = @()
foreach ($d in $scriptDirs) {
    if (Test-Path -LiteralPath $d) {
        $scriptFiles += Get-ChildItem -LiteralPath $d -Filter '*.ps1' -File |
            Where-Object { $_.Name -notlike '校验目标框架*' }
    }
}

foreach ($f in $scriptFiles) {
    $text = Get-Content -LiteralPath $f.FullName -Raw
    # 只有真正定义了 $NetTFM 默认值的脚本才参与比对
    $m = [regex]::Match($text, '\$NetTFM\s*=\s*[''""](net[^''""]+)[''""]')
    if ($m.Success) {
        $entries += [pscustomobject]@{Kind = 'script'; Item = $f.Name; Tfm = $m.Groups[1].Value; Note = '' }
    }
}

# ── 判定 ───────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 3FP 目标框架一致性门禁 ==='
Write-Host ''

$width = ($entries | ForEach-Object { $_.Item.Length } | Measure-Object -Maximum).Maximum
if ($width -lt 20) { $width = 20 }

$fmt = '  [{0,-6}] {1,-' + ($width + 2) + '} {2,-34} {3}'
foreach ($e in $entries) {
    $shown = if ($e.Tfm) { $e.Tfm } else { '（未配置）' }
    $flag = ''
    if (-not $e.Tfm) { $flag = '  [缺失]' }
    Write-Host ($fmt -f $e.Kind, $e.Item, $shown, $e.Note) -NoNewline
    if ($flag) { Write-Host $flag -ForegroundColor Yellow } else { Write-Host '' }
}

$problems = @()

$known = @($entries | Where-Object { $_.Tfm } | Select-Object -ExpandProperty Tfm -Unique)
if ($known.Count -gt 1) {
    $problems += ('TFM 默认值出现 {0} 种：{1}。必须收敛为同一个。' -f $known.Count, ($known -join ' / '))
}
$missing = @($entries | Where-Object { -not $_.Tfm })
if ($missing.Count -gt 0 -and -not $AllowMissing) {
    foreach ($m in $missing) { $problems += ('{0} 未配置 TFM 默认值：{1}' -f $m.Kind, $m.Item) }
}
foreach ($e in $entries) {
    if ($e.Note -match '字面量') {
        $problems += ('{0} {1}：{2}' -f $e.Kind, $e.Item, $e.Note)
    }
}
if ($Expected) {
    foreach ($e in $entries) {
        if ($e.Tfm -and $e.Tfm -ne $Expected) {
            $problems += ('{0} {1} 仍为 {2}，期望 {3}' -f $e.Kind, $e.Item, $e.Tfm, $Expected)
        }
    }
}

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Host ('判定：失败（{0} 项）' -f $problems.Count) -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ('  · ' + $p) -ForegroundColor Red }
    Write-Host ''
    exit 1
}

$actual = if ($known.Count -eq 1) { $known[0] } else { '（全部缺失）' }
$count = @($entries | Where-Object { $_.Tfm }).Count
Write-Host ('判定：通过 —— {0} 处 TFM 一致，均为 {1}' -f $count, $actual) -ForegroundColor Green
if ($Expected) { Write-Host ('      且 -Expected 校验成立：' + $Expected) -ForegroundColor Green }
Write-Host ''
exit 0
