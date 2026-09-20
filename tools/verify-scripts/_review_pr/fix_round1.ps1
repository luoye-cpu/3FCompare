# 第 1 轮（harness 退出码未捕获）的事后修正：完全依据被测程序自己打印的日志判读。
# 依据（见 .review_pr/device_vs_thread/logs/r1_*.{out,err}.txt）：
#   A 臂：out 无 "全部通过"；err 有 "失败 ✗ 第 1 路漂移 0.7585345 > 100ms"；
#          err 无 "[ExitSelfTest] 准备退出" ⇒ 断言抛出后死于收尾（DestroyAllSessions），判 CRASH。
#   B 臂：out 有 "全部通过 ✓"；err 有 "[ExitSelfTest] 准备退出 code=0" ⇒ 判 OK，exitCode=0。
$ErrorActionPreference = "Stop"
$outDir = "C:\PLAN\3FCompare\.review_pr\device_vs_thread"
$csv    = Join-Path $outDir "results_ab.csv"

if (Test-Path $csv) { Copy-Item $csv (Join-Path $outDir "results_ab_round1_raw.csv") -Force }

$r1 = @(
    [PSCustomObject]@{
        time="12:05:03"; arm="A"; round=1; pos=1; verdict="CRASH"; exitCode=$null; codeHex=""
        sawPass=$false; sawAssert=$true; sawCleanup=$false; wallSec=18.3
        injected="RTSSHooks64.dll+nvspcap64.dll"; rtssUp=$true; pid=54516
        outFile="r1_A_120445.out.txt"
        note="退出码未捕获(harness bug)；按日志判据：漂移断言失败后死于收尾"
    },
    [PSCustomObject]@{
        time="12:05:26"; arm="B"; round=1; pos=2; verdict="OK"; exitCode=0; codeHex=""
        sawPass=$true; sawAssert=$false; sawCleanup=$true; wallSec=19.3
        injected="RTSSHooks64.dll+nvspcap64.dll"; rtssUp=$true; pid=7312
        outFile="r1_B_120506.out.txt"
        note="退出码取自日志 [ExitSelfTest] 准备退出 code=0"
    }
)

if (Test-Path $csv) { Remove-Item $csv -Force }
$r1 | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Host ("已写入第 1 轮修正行 -> $csv")
Get-Content $csv
