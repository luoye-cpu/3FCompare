param(
    [Parameter(Mandatory=$true)][string]$OutDir
)
# ab_sweep_probe.ps1 —— 逐套件探测「哪个开关会污染真机 HKCU」。
#
# ⚠ 这个脚本存在的原因：早先用 bash + `reg delete ... /f` 做过同样的事，得到
#   「15 个开关全部污染」的结论，事后证明**全部是假的**——MSYS 把 `/f` 当路径转换，
#   删除静默失败，于是每个套件之后看到的都是那个从未被删掉的老键。
#   这里改为：每套件前**用 restore 脚本还原到基线**，跑完**逐行比对基线**，
#   只有"跑后 ≠ 跑前"才记为该套件的污染。
#   清现场与比对都走 PowerShell，且清现场后强制回读确认，杜绝上述假象。
$ErrorActionPreference = 'Stop'
$ROOT = 'C:\PLAN\3FCompare\3fp'
$BIN  = "$ROOT\kernel\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe"
$EXTS = '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif'
$基线 = Get-Content "$ROOT\build\guard_ab\baseline_before.txt" -Encoding UTF8

function 还原现场 {
    & "$ROOT\tools\restore_associations_baseline.ps1" `
        -BaselineHuman "$ROOT\build\guard_ab\baseline_before.txt" -Apply | Out-Null
    $base = 'HKCU:\Software\1059 Studio\FFF.Player\FileAssociationBackups'
    if (Test-Path $base) {
        @((Get-Item $base).GetSubKeyNames()) | Where-Object { $_ -ne 'vob' } |
            ForEach-Object { Remove-Item "$base\$_" -Recurse -Force }
    }
}

$flags = Get-Content "$ROOT\build\flags1.txt" | Where-Object { $_ -and $_.Trim() }
$结果 = @()
foreach ($flag in $flags) {
    还原现场
    $tmp = "$OutDir\_probe_before.txt"
    & "$ROOT\tools\dump_associations.ps1" -Out $tmp -Exts $EXTS | Out-Null
    $b = Get-Content $tmp -Encoding UTF8
    if (Compare-Object $基线 $b -SyncWindow 0) {
        $结果 += "SKIP  $flag  （清现场未达基线，跳过）"
        continue
    }

    $env:TEMP = 'C:\Users\20210\AppData\Local\Temp'
    $env:TMP  = $env:TEMP
    Push-Location (Split-Path $BIN)
    & $BIN $flag *> "$OutDir\_probe_run.log"
    $code = $LASTEXITCODE
    Pop-Location

    $tmp2 = "$OutDir\_probe_after.txt"
    & "$ROOT\tools\dump_associations.ps1" -Out $tmp2 -Exts $EXTS | Out-Null
    $a = Get-Content $tmp2 -Encoding UTF8
    $diff = Compare-Object $b $a -SyncWindow 0
    if ($null -eq $diff) {
        $结果 += ("CLEAN {0}  (EXIT={1})" -f $flag, $code)
    } else {
        $结果 += ("DIRTY {0}  (EXIT={1})  diff={2} 行" -f $flag, $code, @($diff).Count)
    }
}
$结果 | Set-Content -Path "$OutDir\sweep_probe.txt" -Encoding UTF8
$结果 | ForEach-Object { Write-Output $_ }
