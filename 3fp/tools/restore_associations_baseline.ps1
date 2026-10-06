param(
    [Parameter(Mandatory=$true)][string]$BaselineHuman,
    [switch]$Apply
)
# restore_associations_baseline.ps1 —— 把 HKCU 图片关联现场还原到一份人工记录的基线文本。
#
# 为什么要这个脚本：实机测试（宿主自测开关）会把 HKCU 写脏，测试自身只还原它认识的那几个字段，
# 而 OpenWithProgids 的增项、ProgID 子键、Applications 项、RegisteredApplications、备份目录都需要
# 单独收回。手工逐项删既慢又易漏，且**必须走 PowerShell**——Git Bash 里 `reg delete ... /f` 的 `/f`
# 会被 MSYS 当成路径转换，命令**静默失败**（本会话踩过：据此得出过一串反向假结论）。
#
# 基线文本格式（dump_associations.ps1 的输出，一个扩展名一行）：
#   .png|default=WPS.PIC.png|ct=image/png|pt=|owp=A+B+C|FFFProgId=False
# 说明：owp 字段记的是 **基线当时 OpenWithProgids 的全部值名**，还原时把「现值里基线没有的」删掉。
#       因此要收集**完整**的 OpenWithProgids 现值再比对——dump 脚本为了可读性只列全部值名，
#       这里沿用同一份 dump 输出即可（它列的就是全量值名）。
$ErrorActionPreference = 'Stop'

$rows = Get-Content -Path $BaselineHuman -Encoding UTF8
$当前 = @{}          # ext -> @{Default;CT;PT;OWP=@();}
$顺序 = @()
foreach ($line in $rows) {
    if ($line -notmatch '^\s*\.') { continue }
    $f = $line -split '\|'
    if ($f.Count -lt 6) { continue }
    $ext = $f[0].Trim()
    $顺序 += $ext
    $当前[$ext] = @{
        Default = ($f[1] -replace '^default=', '')
        CT      = ($f[2] -replace '^ct=', '')
        PT      = ($f[3] -replace '^pt=', '')
        OWP     = (($f[4] -replace '^owp=', '') -split '\+' | Where-Object { $_ -and $_ -ne '(键缺失)' })
    }
}

$改动 = @()
foreach ($ext in $顺序) {
    $want = $当前[$ext]
    $p    = "HKCU:\Software\Classes\$ext"

    # ── 若基线里该键「缺失」，就要求它现在也不存在 ──
    if ($want.Default -eq '(键缺失)') {
        if (Test-Path $p) {
            $改动 += "DELKEY  $p  (基线为「键缺失」)"
            if ($Apply) {
                Remove-Item -Path $p -Recurse -Force
            }
        }
        continue
    }

    # ── default / Content Type / PerceivedType ──
    if (-not (Test-Path $p)) {
        $改动 += "MKDIR   $p  (基线存在，当前缺失)"
        if ($Apply) { New-Item -Path $p -Force | Out-Null }
    }
    $k = Get-Item $p
    foreach ($pair in @(@('', $want.Default), @('Content Type', $want.CT), @('PerceivedType', $want.PT))) {
        $名 = $pair[0]; $值 = [string]$pair[1]
        $现 = [string]$k.GetValue($名)
        if ($现 -ne $值) {
            $改动 += "SETVAL  $p[$名]  '$现' -> '$值'"
            if ($Apply) {
                # ⚠ 默认值不能走 Remove-ItemProperty -Name ''（参数绑定拒绝空串，实测报
                #   "无法将自变量绑定到参数 'Name'，因为它是空字符串"）。
                #   默认值的名字在 .NET 里就是 '(default)'，用 Set-ItemProperty 写成空串即可。
                $注册名 = if ($名 -eq '') { '(default)' } else { $名 }
                Set-ItemProperty -Path $p -Name $注册名 -Value $值
            }
        }
    }

    # ── OpenWithProgids：只删「现值里基线没有的」，绝不删基线里有的 ──
    $owpPath = "$p\OpenWithProgids"
    if (Test-Path $owpPath) {
        $现名 = @((Get-Item $owpPath).GetValueNames())
        $多余 = $现名 | Where-Object { $_ -and ($want.OWP -notcontains $_) }
        foreach ($n in $多余) {
            $改动 += "DELVAL  $owpPath[$n]"
            if ($Apply) { Remove-ItemProperty -Path $owpPath -Name $n -Force }
        }
        # 若基线为「键缺失」则整键也该消失（ext 键本身已在上面的 MKDIR 分支处理）
        if ($want.OWP.Count -eq 0 -and @($现名 -notcontains '(default)').Count -eq 0) {
            $改动 += "DELKEY  $owpPath  (基线 OpenWithProgids 为空且无值)"
            if ($Apply) { Remove-Item -Path $owpPath -Recurse -Force }
        }
    }
}

# ── ProgID 子键 FFF.Player.<ext>：基线一律不存在的，全部删 ──
foreach ($ext in $顺序) {
    $prog = "HKCU:\Software\Classes\FFF.Player$ext"
    if (Test-Path $prog) {
        $改动 += "DELKEY  $prog  (基线无此 ProgID)"
        if ($Apply) { Remove-Item -Path $prog -Recurse -Force }
    }
}

# ── 应用级键 ──
foreach ($appKey in @(
    'HKCU:\Software\Classes\Applications\FFF.Player.exe',
    'HKCU:\Software\Classes\Applications\FFF.Player.exe\SupportedTypes')) {
    if (Test-Path $appKey) {
        $改动 += "DELKEY  $appKey  (基线为「缺失」)"
        if ($Apply) { Remove-Item -Path $appKey -Recurse -Force }
    }
}

# ── RegisteredApplications\FFF.Player 值：基线为 False ⇒ 删该值（不删 RegisteredApplications 键本身） ──
$ra = 'HKCU:\Software\RegisteredApplications'
if (Test-Path $ra) {
    $k = Get-Item $ra
    if ($null -ne $k.GetValue('FFF.Player')) {
        $改动 += "DELVAL  $ra[FFF.Player]  -> 基线为不存在"
        if ($Apply) { Remove-ItemProperty -Path $ra -Name 'FFF.Player' -Force }
    }
}

Write-Output ("=== 差集（{0} 项） ===" -f $改动.Count)
$改动 | ForEach-Object { Write-Output $_ }
if (-not $Apply) { Write-Output "`n（未实际改动；加 -Apply 才执行）" }
else { Write-Output "`n已执行还原。" }
