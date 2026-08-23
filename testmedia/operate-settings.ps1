param([int]$TargetPid)
$wshell = New-Object -ComObject wscript.shell
$wshell.AppActivate($TargetPid) | Out-Null
Start-Sleep -Seconds 1
# 遍历所有 Tab 页（Ctrl+Tab 切换）
for ($i = 0; $i -lt 6; $i++) {
    $wshell.SendKeys("^{TAB}")
    Start-Sleep -Seconds 1
}
# 点确定按钮
$wshell.SendKeys('{ENTER}')
Start-Sleep -Seconds 4
$p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if ($p) {
    Write-Output "Tab遍历+确定后 Responding=$($p.Responding) CPU=$([math]::Round($p.CPU,1))"
} else {
    Write-Output "Process exited"
}