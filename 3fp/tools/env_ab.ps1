# env_ab.ps1 - 同一个二进制、同一窗，只换环境变量做交替 A/B
#
# 为什么用换 env 而不是换臂：两臂若是同一份 exe+同一份 DLL，只差一个开关，
# 那么"编译差异/发布差异/加载差异"全部被消掉，配对只剩被测那一件事。
# 本轮被测的是内核自带的硬件试解门开关 FFF3FP_HWCAP_TRIAL=0（PlayerSession.cpp:414-418）。
#
# 轮内还要换序：奇数轮 A→B、偶数轮 B→A，否则"B 总排在 A 之后"会把
# "第二次开图更便宜"这类一阶效应记成开关的收益。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Images,     # "label=path;label=path"
    [Parameter(Mandatory = $true)][string]$EnvA,       # "K=V;K2=V2" 或 'none'
    [Parameter(Mandatory = $true)][string]$EnvB,
    [int]$Rounds = 20,
    [int]$TimeoutSec = 30,
    # 第二把分叉尺：不改代码、不改环境变量，改**臂旁的设置文件**。
    # 本轮被测的是 `解码方式`（1=CPU / 2=GPU，设置.vb:17 默认 GPU，Form1.vb:133 原样进会话配置），
    # 它对应内核 PlayerSession.cpp:3555 的 `tryHardware = decodeMode_ == D3D11 && …`。
    # 用它做分叉的规矩：SwapA/SwapB 必须是**只差那一个键**的两份文件，其余字节相同；
    # 两份都由脚本从同一份基线生成，禁止手改（手改会连带把别的键也改掉，分叉就不唯一了）。
    [string]$OverrideA = '',
    [string]$OverrideB = '',
    [string]$SwapTarget = 'FFF.Player.Settings.json',
    [string]$CsvOut = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CsvOut)) {
    $CsvOut = Join-Path (Split-Path $Exe -Parent) ('env_ab_' + (Get-Date -Format 'HHmmss') + '.csv')
}
if (Test-Path -LiteralPath $CsvOut) { Remove-Item -LiteralPath $CsvOut -Force }

function Parse-Env([string]$spec) {
    $h = @{}
    if ($spec -eq 'none' -or [string]::IsNullOrWhiteSpace($spec)) { return $h }
    foreach ($kv in ($spec -split ';' | Where-Object { $_ })) {
        $pair = $kv -split '=', 2
        if ($pair.Count -ne 2 -or [string]::IsNullOrWhiteSpace($pair[0])) { throw "环境规格非法：'$kv'" }
        $h[$pair[0].Trim()] = $pair[1].Trim()
    }
    return $h
}
$specA = Parse-Env $EnvA
$specB = Parse-Env $EnvB
# 参数块给 $EnvA 定了 [string]，PowerShell 的类型约束跟着变量走：
# 把哈希表赋回**同名**变量会被静默 ToString() ⇒ 后面每一处判断都在猜。换个名字就是了。
if ($specA -isnot [hashtable] -or $specB -isnot [hashtable]) { throw "Parse-Env 返回的不是哈希表：A=[$($specA.GetType().Name)] B=[$($specB.GetType().Name)]" }
if ($specA.Count -eq 0 -and $specB.Count -eq 0 -and -not ($OverrideA -or $OverrideB)) {
    throw 'A 与 B 既没设变量也没设覆盖 ⇒ 两臂同条件，这场 A/B 没有对照'
}
Write-Host ("A = {0}" -f (($specA.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ';'))
Write-Host ("B = {0}" -f (($specB.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ';'))
Write-Host ("覆盖：A'{0}'  B'{1}'  目标={2}" -f $OverrideA, $OverrideB, $SwapTarget)
Write-Host ("  变量条数：A={0} B={1}" -f $specA.Count, $specB.Count)
foreach ($k in $specA.Keys) {
    if ($specB.ContainsKey($k) -and [string]$specB[$k] -eq [string]$specA[$k]) {
        Write-Host ("  注意：{0} 在两臂取值相同（{1}）⇒ 该变量不构成分叉" -f $k, $specA[$k]) -ForegroundColor Yellow
    }
}

# ── 第二把分叉尺：改臂旁的设置文件，用**最小字节替换**（只动那一个键的数值，其余字节原样）
# 为什么不用 ConvertFrom/ConvertTo-Json 往返：81 个键会被重排、深度截断、非 ASCII 被转义，
# 于是"只差一个键"变成"整份文件都不同"，分叉就不唯一了。
$baseSettings = $null
$baselineText = ''
$hadBom = $false
if ($OverrideA -or $OverrideB) {
    $target = Join-Path (Split-Path $Exe -Parent) $SwapTarget
    if (-not (Test-Path -LiteralPath $target)) { throw "找不到 $target ⇒ 无法用设置文件分叉" }
    $baseSettings = $target
    $raw = [System.IO.File]::ReadAllBytes($target)
    $hadBom = ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF)
    $skip = if ($hadBom) { 3 } else { 0 }
    $baselineText = [System.Text.Encoding]::UTF8.GetString($raw, $skip, $raw.Length - $skip)
    if ($baselineText.Length -lt 50) { throw '基线设置文件小得可疑 ⇒ 不像真的设置' }
}
function New-SettingsText([string]$base, [string]$spec, [string]$who) {
    if ([string]::IsNullOrWhiteSpace($spec)) { return $base }
    $pair = $spec -split '=', 2
    if ($pair.Count -ne 2) { throw "覆盖规格非法：'$spec'" }
    $pat = '("' + [regex]::Escape($pair[0].Trim()) + '"\s*:\s*)-?\d+'
    $hits = @([regex]::Matches($base, $pat))
    if ($hits.Count -ne 1) { throw "键 '$($pair[0])' 在基线里命中 $($hits.Count) 次（必须恰好 1）⇒ 不敢改，怕改到别处" }
    $before = $hits[0].Value
    # 替换串必须写 ${1}，不能写 '$1'+值：.NET 在 $ 后面会贪婪读数字，
    # 值以数字开头时 '$1'+'1' 被当成第 11 组 ⇒ 写出字面量 $11 ⇒ JSON 非法。
    # 上一版就栽在这：两臂都被应用判成 broken 并重置 ⇒ 实际测的是"两个相同臂"的零差异。
    $out = [regex]::Replace($base, $pat, ('${1}' + $pair[1].Trim()))
    # 与基线相同**不是错误**：对照臂通常就是基线值（本轮 B=2 正是出货默认）。
    # 真正要拦的是"两臂相同"，那一步在下面统一判。
    if ($out -eq $base) { Write-Host ("  {0}: 覆盖值与基线相同（{1}）⇒ 这一臂就是基线" -f $who, $pair[1]) }
    $chk = $null
    try { $chk = ConvertFrom-Json $out } catch {
        throw "$who 覆盖后的文本不是合法 JSON（$($_.Exception.Message)）⇒ 会被应用判 broken 并重置全部设置"
    }
    $got = $chk.$($pair[0].Trim())
    if ([string]$got -ne [string]$pair[1].Trim()) { throw "$who 覆盖后读回的值是 '$got'，不是 '$($pair[1])' ⇒ 改错了地方" }
    Write-Host ("  {0}: {1} → {2}（JSON 回读校验通过）" -f $who, $before, ($pair[0].Trim() + '=' + $pair[1].Trim()))
    return $out
}
$txtA = New-SettingsText $baselineText $OverrideA 'A'
$txtB = New-SettingsText $baselineText $OverrideB 'B'
if ($OverrideA -or $OverrideB) {
    if ($txtA -eq $txtB) { throw '两臂设置文本完全相同 ⇒ 覆盖没落地（键名写错？），这场 A/B 没有对照' }
    Write-Host ("设置分叉就位：文本长度 A={0} B={1}（同长度才说明只换了数值）" -f $txtA.Length, $txtB.Length)
}

$list = @($Images -split ';' | ForEach-Object {
    $kv = $_ -split '=', 2
    [pscustomobject]@{ Label = $kv[0]; Path = ($kv[1] -replace '\^', ':') }
})
foreach ($i in $list) { if (-not (Test-Path -LiteralPath $i.Path)) { throw "夹具不存在：$($i.Path)" } }

if (-not ('EnvAb.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace EnvAb {
    public static class Native {
        [DllImport("kernel32.dll", SetLastError=true)]
        public static extern bool SetEnvironmentVariable(string name, string value);
    }
}
'@ | Out-Null
}

function Get-TotalCpuPct {
    try { return [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime }
    catch { return -1.0 }
}

$rows = New-Object System.Collections.Generic.List[object]
for ($r = 1; $r -le $Rounds; $r++) {
    $armOrder = if ($r % 2 -eq 1) { @('A', 'B') } else { @('B', 'A') }
    foreach ($fx in $list) {
        foreach ($armName in $armOrder) {
            $vars = if ($armName -eq 'A') { $specA } else { $specB }
            Get-Process -Name FFF.Player -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
            Start-Sleep -Milliseconds 1200
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $Exe; $psi.Arguments = '"' + $fx.Path + '"'
            $psi.UseShellExecute = $false; $psi.RedirectStandardError = $true
            # 关键：ProcessStartInfo 的环境表在 UseShellExecute=false 时才生效（所以必须先设它），
            # 且它是在继承来的环境上做"覆盖/追加"——上一臂留下的键必须**显式 Remove**，
            # 否则 B 的 =0 会渗进 A，两臂变成同一条件；这种坏法不报错，只给出骗人的零差异。
            # （不要改成"赋空串"：空串与未设在 GetEnvironmentVariableW 返回值上不可区分，
            #   但内核各开关对"未设"与"空"的处理并不都一致，显式删除才是无歧义的。）
            foreach ($k in (@($specA.Keys) + @($specB.Keys) | Sort-Object -Unique)) {
                if ($vars.ContainsKey($k)) { $psi.EnvironmentVariables[$k] = [string]$vars[$k] }
                else { [void]$psi.EnvironmentVariables.Remove($k) }
            }
            if ($null -ne $baseSettings) {
                $txt = if ($armName -eq 'A') { $txtA } else { $txtB }
                [System.IO.File]::WriteAllText($baseSettings, $txt, (New-Object System.Text.UTF8Encoding($hadBom)))
            }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $spawnQpc = [Diagnostics.Stopwatch]::GetTimestamp()
            $p = [System.Diagnostics.Process]::Start($psi)
            $reader = $p.StandardError
            $pp = -1.0; $note = ''
            # 分叉证据：`[hwcap] trial …` 只有**试解真的跑了**才会打印（内核自带 trace，受
            # FFF3FP_HWCAP_TRACE 门控 ⇒ 两臂都开 trace）。所以 A 臂每轮应有 trial 行、B 臂应为 0 行。
            # 没有这一列，"开关没生效"和"开关生效但没差别"在结果上长得一模一样。
            $hwcap = 0
            $rdTask = $reader.ReadLineAsync()
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
            while ([DateTime]::UtcNow -lt $deadline) {
                if ($p.HasExited) { $note = "early-exit exit=$($p.ExitCode)"; break }
                if (-not $rdTask.Wait(150)) { continue }
                $line = $rdTask.Result; $rdTask = $null
                if ($null -eq $line) { $note = 'stderr-EOF'; break }
                if ($line -match '\[hwcap\] trial') { $hwcap++ }
                if ($line -match '\[pp\] still_first_present qpc=(\d+)') {
                    $pp = ([int64]$Matches[1] - $spawnQpc) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                    break
                }
                $rdTask = $reader.ReadLineAsync()
            }
            if ($pp -lt 0 -and $note -eq '') { $note = 'NO-[pp]' }
            try { if (-not $p.HasExited) { $p.Kill() } } catch { }
            try { $p.WaitForExit(5000) | Out-Null } catch { }
            $rows.Add([pscustomobject]@{
                Round = $r; Fmt = $fx.Label; Arm = $armName; Order = ($armOrder -join '')
                LoadPct = (Get-TotalCpuPct); SpawnMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
                PPms = [math]::Round($pp, 1); HwcapTrialLines = $hwcap; Note = $note
            })
        }
    }
    if ($r % 2 -eq 0) { Write-Host ("round {0}/{1} 完成（负载 {2}%）" -f $r, $Rounds, (Get-TotalCpuPct)) }
}
$rows | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
Write-Host ("明细：{0}" -f $CsvOut)
$bad = @($rows | Where-Object { $_.PPms -lt 0 })
Write-Host ("总样本 {0} | 未判 {1}（{2}）" -f $rows.Count, $bad.Count,
    (($bad | Group-Object { $_.Fmt + '/' + $_.Arm } | ForEach-Object { $_.Name + '×' + $_.Count }) -join ', '))
# 分叉判据先于效果判据：两臂的 [hwcap] trial 行数若相同 ⇒ 开关根本没落地，本次 A/B 作废
Write-Host '分叉证据（[hwcap] trial 行数中位）：'
foreach ($f in ($rows | Group-Object Fmt)) {
    foreach ($a in 'A', 'B') {
        $v = @($f.Group | Where-Object { $_.Arm -eq $a } | ForEach-Object { [double]$_.HwcapTrialLines })
        $med = if ($v.Count) { ($v | Sort-Object)[[int][Math]::Floor($v.Count / 2)] } else { 'n/a' }
        Write-Host ("  {0}/{1}: {2}（n={3}）" -f $f.Name, $a, $med, $v.Count)
    }
}
# 分叉判据必须是"两臂分布不同"，不能预设"哪一侧该有行"：
# env 那把尺是 A 有 trial 行 / B 没有；解码模式那把尺正好反过来（A=CPU 没有，B=GPU 有）。
# 预设方向的写法会把"开关反了/两臂相同"这两种坏法都读成正常。
$forkBad = @()
foreach ($f in ($rows | Group-Object Fmt)) {
    $ca = @($f.Group | Where-Object { $_.Arm -eq 'A' } | ForEach-Object { [double]$_.HwcapTrialLines })
    $cb = @($f.Group | Where-Object { $_.Arm -eq 'B' } | ForEach-Object { [double]$_.HwcapTrialLines })
    if (-not $ca -or -not $cb) { continue }
    $ma = ($ca | Measure-Object -Average).Average
    $mb = ($cb | Measure-Object -Average).Average
    if ([math]::Abs($ma - $mb) -lt 0.5) { $forkBad += ('{0}(A={1:N1} B={2:N1})' -f $f.Name, $ma, $mb) }
}
if ($OverrideA -or $OverrideB) {
    if ($forkBad.Count -eq ($rows | Group-Object Fmt).Count) {
        Write-Host '⛔ 所有格式的两臂 trial 行均值都一样 ⇒ 覆盖没落地或该格式本来不过门；本次 Δ 不可解释' -ForegroundColor Red
    } else {
        Write-Host ('分叉成立，未分叉的格式：' + ($forkBad -join ' '))
    }
} elseif ($forkBad.Count -gt 0) {
    Write-Host ("⚠ 这些格式两臂 trial 行无差别（可能本来就不试解，属阴性对照而非失效）：{0}" -f ($forkBad -join ' ')) -ForegroundColor Yellow
}
