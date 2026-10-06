param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Runs = 5,
    [string]$ProfileName = 'startup_profile.txt'
)
# 用 FFFPROF=1 启动宿主 N 次，收集 Form1_Load 分段打点，取各段中位数。
# 每次跑完把 startup_profile.txt 改名保存，避免下一次覆盖。
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$dir = Split-Path -Parent $Exe
$profPath = Join-Path $dir $ProfileName

if (-not ('StartupProbe.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace StartupProbe {
    public static class Native {
        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr h);
        public static bool HasVisibleTitledWindow(uint wantedPid) {
            bool found = false;
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != wantedPid) return true;
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLengthW(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 2);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.Length > 0) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@
}

$env:FFFPROF = '1'
$collected = @()
for ($i = 1; $i -le $Runs; $i++) {
    if (Test-Path -LiteralPath $profPath) { Remove-Item -LiteralPath $profPath -Force }
    $p = [System.Diagnostics.Process]::Start($Exe)
    $procId = [uint32]$p.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.HasExited) { break }
        if ([StartupProbe.Native]::HasVisibleTitledWindow($procId)) { break }
        Start-Sleep -Milliseconds 4
    }
    Start-Sleep -Milliseconds 800   # 让 END 打点与写文件完成
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 1200
    if (Test-Path -LiteralPath $profPath) {
        $lines = Get-Content -LiteralPath $profPath
        $collected += , $lines
        Write-Host ("run {0}: {1} 行" -f $i, $lines.Count)
    } else {
        Write-Host ("run {0}: 未生成 profile 文件" -f $i)
    }
}
Remove-Item Env:\FFFPROF -ErrorAction SilentlyContinue

# 聚合：按阶段名取中位数
$byName = @{}
foreach ($run in $collected) {
    foreach ($ln in $run) {
        $parts = $ln -split "`t"
        if ($parts.Count -ne 2) { continue }
        if (-not $byName.ContainsKey($parts[0])) { $byName[$parts[0]] = @() }
        $byName[$parts[0]] += [double]$parts[1]
    }
}
Write-Host "`n===== 各阶段相对 T0 的中位数 (ms) ====="
$names = @('before_SP','after_SP','after_Settings','after_FileAssoc','after_Fonts','after_SPPersonalize','after_CoreProbe','after_IconLoad','ICON_LOAD_2ND','after_Icons','after_ScreenCtl','after_PlayCtl','after_ClipCtl','after_StreamSelCtor','after_StreamSelFont','after_UIProvider','after_Layer1','after_Layer2','after_Layer3','after_Layer4Ctor','after_Layer4','after_AllCtrls','after_AllHandlers','after_UIStart','after_InitialSize','END','CORE_MISSING')
foreach ($n in $names) {
    if (-not $byName.ContainsKey($n)) { continue }
    $vals = @($byName[$n] | Sort-Object)
    $med = if ($vals.Count % 2 -eq 1) { $vals[[int][math]::Floor($vals.Count/2)] }
           else { ($vals[$vals.Count/2 - 1] + $vals[$vals.Count/2]) / 2 }
    Write-Host ("{0,-22} {1,8:F1}" -f $n, $med)
}
Write-Host "`n===== 增量（相邻差）====="
$prev = 0.0; $prevName = 'T0'
foreach ($n in $names) {
    if (-not $byName.ContainsKey($n)) { continue }
    $vals = @($byName[$n] | Sort-Object)
    $med = if ($vals.Count % 2 -eq 1) { $vals[[int][math]::Floor($vals.Count/2)] }
           else { ($vals[$vals.Count/2 - 1] + $vals[$vals.Count/2]) / 2 }
    Write-Host ("{0,-22} -> {1,-22} {2,8:F1}" -f $prevName, $n, ($med - $prev))
    $prev = $med; $prevName = $n
}
