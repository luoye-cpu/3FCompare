param(
    [Parameter(Mandatory=$true)][ValidateSet('control','guard')][string]$Variant,
    [Parameter(Mandatory=$true)][string]$Flag,
    [Parameter(Mandatory=$true)][string]$OutDir
)
# ab_run_one.ps1 —— A/B 对照的「单次运行」单元：换源码 → 重建 → 校验 → 清现场 → 跑 → 快照。
#
# 设计要点（都是踩过的坑）：
#  ① 换源码**绝不能用 Copy-Item -Force**：它保留源文件时间戳，若变体文件比上次产物更旧，
#     MSBuild 判定"源码未变更"⇒ 跳过编译、复用旧 DLL ⇒ 两组实际跑同一份二进制（假对照）。
#     实测：两组 dll mtime 完全相同（23:10:25）。这里改为「读原版 → 字符串替换 → 重写」，
#     写入时间必然是"现在"，一定晚于产物。
#  ② 变异方式选「注释掉 Main 里的一行调用」，而不是删函数体：删函数体时
#     `index("End Sub")` 会被函数体内 lambda 的 `End Sub` 误匹配，截出残余语法（BC30025/BC30689）。
#     注释一行的改动最小、语法风险为零。
#  ③ 校验不靠"构建说成功"，而靠两条独立判据：源码里变异标记一致 + dll mtime 晚于源码。
#  ④ 清现场必须走 PowerShell 并**回读确认**：Git Bash 的 `reg delete ... /f` 会因 MSYS 路径转换
#     静默失败（`/f` 被当路径），据此会得出"污染自行重现"等反向假结论。
$ErrorActionPreference = 'Stop'
$ROOT = 'C:\PLAN\3FCompare\3fp'
$PROJ = "$ROOT\kernel\FFF.Player.Tests"
$SRC  = "$PROJ\Program.vb"
$VAR  = "$ROOT\build\guard_ab\variants"
$DLL  = "$PROJ\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.dll"
$BIN  = "$PROJ\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe"

$调用行 = '        引导测试进程的文件关联(参数)'
$变异行 = "        ' " + [char]0x2605 + '变异（control 组）：注释掉闸调用'

# ── ① 换源码：以 guard 原版为基准，按 Variant 决定是否注释掉闸调用 ──
$原版 = [IO.File]::ReadAllText("$VAR\Program.vb.guard", [Text.Encoding]::UTF8)
if ($原版.Contains($调用行) -eq $false) { Write-Output "⚠ guard 原版里找不到调用行 ⇒ 素材不对"; exit 1 }
$新 = $原版 -replace [regex]::Escape($调用行), ("$变异行`r`n        ' " + $调用行.Trim())
$新内容 = if ($Variant -eq 'control') { $新 } else { $原版 }

if (Test-Path $SRC) { Remove-Item $SRC -Force }
[IO.File]::WriteAllText($SRC, $新内容, (New-Object Text.UTF8Encoding($false)))
Write-Output "[$Variant] 已写入源码（$((Get-Item $SRC).LastWriteTime)）"

# ── ② 重建 ──
if (Test-Path $DLL) { Remove-Item $DLL -Force }
$log = "$ROOT\build\guard_ab\ab_build_$Variant.log"
Push-Location $PROJ
& 'C:\Program Files\dotnet\dotnet.exe' build FFF.Player.Tests.vbproj -c Release --nologo *> $log
Pop-Location
$errs = (Select-String -Path $log -Pattern 'error [A-Z]+\d+' -AllMatches).Count
if ($errs -ne 0) { Write-Output "构建失败（$errs 错误）⇒ 中止。见 $log"; exit 1 }
if (-not (Test-Path $BIN)) { Write-Output "构建声称成功但 exe 不存在 ⇒ 中止"; exit 1 }

# ── ②b 硬校验 ──
$srcText = [IO.File]::ReadAllText($SRC, [Text.Encoding]::UTF8)
$isControlSrc = $srcText.Contains('变异（control 组）')
if ($Variant -eq 'control' -and -not $isControlSrc) { Write-Output "⚠ 源码无 control 标记"; exit 1 }
if ($Variant -eq 'guard'   -and      $isControlSrc) { Write-Output "⚠ 源码仍带 control 标记"; exit 1 }
if ((Get-Item $DLL).LastWriteTime -le (Get-Item $SRC).LastWriteTime) {
    Write-Output "⚠ 产物未刷新：dll $((Get-Item $DLL).LastWriteTime) 不晚于源码 $((Get-Item $SRC).LastWriteTime)"; exit 1
}
Write-Output "[$Variant] 校验通过：源码标记一致，dll mtime=$((Get-Item $DLL).LastWriteTime) > 源码 mtime=$((Get-Item $SRC).LastWriteTime)"

# ── ③ 清现场（PowerShell；回读确认）──
# ⚠⚠ 这里必须清成**完整基线**，不能只删 ProgID/Applications。
#   曾踩过：只删那几项 ⇒ 9 个扩展名的 default 仍是上一次跑留下的 `FFF.Player.<ext>`，
#   于是"跑完后看起来污染了"其实一半是清现场没清干净（虚假污染）——
#   而对照组与实验组的差异会被这类残留掩盖。
#   改为直接调用 restore 脚本（以基线文本为真源，逐项差集还原），再回读确认。
& "$ROOT\tools\restore_associations_baseline.ps1" `
    -BaselineHuman "$ROOT\build\guard_ab\baseline_before.txt" -Apply | Out-Null
# 备份目录也要收回基线（只保留基线里就有的 vob）
$base = 'HKCU:\Software\1059 Studio\FFF.Player\FileAssociationBackups'
if (Test-Path $base) {
    @((Get-Item $base).GetSubKeyNames()) | Where-Object { $_ -ne 'vob' } |
        ForEach-Object { Remove-Item "$base\$_" -Recurse -Force }
}
# 回读确认：与基线逐行比对
& "$ROOT\tools\dump_associations.ps1" -Out "$OutDir\ab_before_$Variant.txt" `
    -Exts '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif' | Out-Null
$基线 = Get-Content "$ROOT\build\guard_ab\baseline_before.txt" -Encoding UTF8
$现在 = Get-Content "$OutDir\ab_before_$Variant.txt" -Encoding UTF8
if (Compare-Object $基线 $现在 -SyncWindow 0) {
    Write-Output "⚠ 清现场后仍与基线有差异 ⇒ 中止（避免拿脏前提做对照）"
    Compare-Object $基线 $现在 -SyncWindow 0 | Format-Table -AutoSize | Out-String | Write-Output
    exit 1
}
Write-Output "[$Variant] 现场已还原到基线（逐行确认一致）"

# ── ④ 跑一个套件 ──
$env:TEMP = 'C:\Users\20210\AppData\Local\Temp'
$env:TMP  = $env:TEMP
$out = "$OutDir\ab_run_$Variant.log"
Push-Location (Split-Path $BIN)
& $BIN $Flag *> $out
$code = $LASTEXITCODE
Pop-Location
Write-Output "[$Variant] 运行 $Flag ⇒ EXIT=$code"

# ── ⑤ 快照 ──
& "$ROOT\tools\dump_associations.ps1" -Out "$OutDir\ab_after_$Variant.txt" `
    -Exts '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif' | Out-Null
Write-Output "[$Variant] 已快照"
