param(
    [Parameter(Mandatory=$true)][string]$OutDir
)
# ab_mutation.ps1 —— 变异复验：把 guard 的**重定向那一行**删掉（只清内存、不重定向），
# 预期应退回「污染」，从而证明「重定向设置文件路径」是闸生效的必要条件，
# 而不是"闸恰好没跑到"或"污染本来就偶发"。
#
# 为什么选这一行做变异：闸由两步组成——
#   ① 清内存（若只做①，Form1_Load:79 的 启动时加载设置() 会把 实例对象 整体替换成磁盘那份，
#      清除结果被丢弃 ⇒ 无效）
#   ② 重定向设置文件路径（使任何时点重新加载读到的都是全 False）
# 摘① 无法区分"哪一步起作用"；摘② 则精确检验"重定向是必要条件"这一主张。
$ErrorActionPreference = 'Stop'
$ROOT = 'C:\PLAN\3FCompare\3fp'
$SRC  = "$ROOT\kernel\FFF.Player.Tests\Program.vb"
$VAR  = "$ROOT\build\guard_ab\variants"

$调用行 = '        引导测试进程的文件关联(参数)'
$重定向行 = '            设置.重定向设置文件路径(临时路径)'
$变异行  = "            ' " + [char]0x2605 + '变异：摘掉重定向（只清内存）'

$原版 = [IO.File]::ReadAllText("$VAR\Program.vb.guard", [Text.Encoding]::UTF8)
if (-not $原版.Contains($重定向行)) { Write-Output "⚠ 找不到重定向行 ⇒ 素材不对"; exit 1 }
$新 = $原版 -replace [regex]::Escape($重定向行), ($变异行)

if (Test-Path $SRC) { Remove-Item $SRC -Force }
[IO.File]::WriteAllText($SRC, $新, (New-Object Text.UTF8Encoding($false)))
Write-Output "已写入变异源码（摘掉重定向）"

$DLL = "$ROOT\kernel\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.dll"
$BIN = "$ROOT\kernel\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe"
if (Test-Path $DLL) { Remove-Item $DLL -Force }
$log = "$ROOT\build\guard_ab\ab_build_mutation.log"
Push-Location "$ROOT\kernel\FFF.Player.Tests"
& 'C:\Program Files\dotnet\dotnet.exe' build FFF.Player.Tests.vbproj -c Release --nologo *> $log
Pop-Location
$errs = (Select-String -Path $log -Pattern 'error [A-Z]+\d+' -AllMatches).Count
if ($errs -ne 0) { Write-Output "构建失败（$errs 错误）⇒ 中止"; exit 1 }
if ((Get-Item $DLL).LastWriteTime -le (Get-Item $SRC).LastWriteTime) { Write-Output "⚠ 产物未刷新 ⇒ 中止"; exit 1 }
Write-Output "构建通过（0 错误），dll mtime=$((Get-Item $DLL).LastWriteTime) > src mtime=$((Get-Item $SRC).LastWriteTime)"

# 现场还原 + 跑 + 快照
& "$ROOT\tools\restore_associations_baseline.ps1" -BaselineHuman "$ROOT\build\guard_ab\baseline_before.txt" -Apply | Out-Null
$base = 'HKCU:\Software\1059 Studio\FFF.Player\FileAssociationBackups'
if (Test-Path $base) {
    @((Get-Item $base).GetSubKeyNames()) | Where-Object { $_ -ne 'vob' } |
        ForEach-Object { Remove-Item "$base\$_" -Recurse -Force }
}
& "$ROOT\tools\dump_associations.ps1" -Out "$OutDir\mut_before.txt" `
    -Exts '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif' | Out-Null
$基线 = Get-Content "$ROOT\build\guard_ab\baseline_before.txt" -Encoding UTF8
$现在 = Get-Content "$OutDir\mut_before.txt" -Encoding UTF8
if (Compare-Object $基线 $现在 -SyncWindow 0) { Write-Output "⚠ 清现场未达基线 ⇒ 中止"; exit 1 }

$env:TEMP = 'C:\Users\20210\AppData\Local\Temp'
$env:TMP  = $env:TEMP
Push-Location (Split-Path $BIN)
& $BIN '--clip-focus-regression' *> "$OutDir\mut_run.log"
$code = $LASTEXITCODE
Pop-Location
Write-Output "运行 ⇒ EXIT=$code"
& "$ROOT\tools\dump_associations.ps1" -Out "$OutDir\mut_after.txt" `
    -Exts '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif' | Out-Null
Write-Output "已快照"
