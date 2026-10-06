param(
    [Parameter(Mandatory=$true)][string]$OutDir
)
# rht_bisect.ps1 —— 复刻 run_host_tests.sh 的行为（先构建、TEMP 覆盖、**中途不还原**），
# 但每跑完一个开关就检查一次现场，用来定位「污染从哪一个开关开始出现」。
#
# 为什么要复刻"不还原"：逐套件探测（每套件前还原）得到 21/21 CLEAN，
#   但 run_host_tests.sh 一次跑完会污染 ⇒ 差异必在脚本自身行为或"不还原"这个条件上。
#   本脚本保留条件、只加观测。
#
# ⚠ check 必须在 PowerShell 内做：bash 里调 powershell.exe 会被安全策略拦，
#   实测导致状态列全空（得到一个"看不出问题"的假结果）。
$ErrorActionPreference = 'Stop'
$ROOT = 'C:\PLAN\3FCompare\3fp'
$PROJ = "$ROOT\kernel\FFF.Player.Tests"
$BIN  = "$PROJ\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe"
$EXTS = '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif'
$基线 = Get-Content "$ROOT\build\guard_ab\baseline_before.txt" -Encoding UTF8

function 状态 {
    $tmp = "$OutDir\_now.txt"
    & "$ROOT\tools\dump_associations.ps1" -Out $tmp -Exts $EXTS | Out-Null
    $n = Get-Content $tmp -Encoding UTF8
    if ($null -eq (Compare-Object $基线 $n -SyncWindow 0)) { 'CLEAN' } else { 'DIRTY' }
}

# 复刻脚本：先杀掉宿主、再构建
foreach ($i in 1..5) {
    Get-Process -Name 'FFF.Player' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (-not (Get-Process -Name 'FFF.Player' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 2
}
$log = "$OutDir\bisect_build.log"
Push-Location $PROJ
& 'C:\Program Files\dotnet\dotnet.exe' build FFF.Player.Tests.vbproj -c Release --nologo *> $log
Pop-Location
$errs = (Select-String -Path $log -Pattern 'error [A-Z]+\d+' -AllMatches).Count
if ($errs -ne 0) { Write-Output "构建失败（$errs）"; exit 1 }

$env:TEMP = 'C:\Users\20210\AppData\Local\Temp'
$env:TMP  = $env:TEMP

$行 = @()
$行 += "跑前: " + (状态)
Push-Location (Split-Path $BIN)
foreach ($flag in (Get-Content "$ROOT\build\flags1.txt" | Where-Object { $_ -and $_.Trim() })) {
    & $BIN $flag *> "$OutDir\_one.log"
    $code = $LASTEXITCODE
    $行 += ("{0}  {1}  (EXIT={2})" -f (状态), $flag, $code)
}
Pop-Location
$行 | Set-Content "$OutDir\rht_bisect.txt" -Encoding UTF8
$行 | ForEach-Object { Write-Output $_ }
