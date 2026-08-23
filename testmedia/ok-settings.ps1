param([int]$TargetPid)
$wshell = New-Object -ComObject wscript.shell
$wshell.AppActivate($TargetPid) | Out-Null
Start-Sleep -Seconds 1
# 点确定按钮（设置对话框默认 AcceptButton）
$wshell.SendKeys('{ENTER}')
Start-Sleep -Seconds 5
$p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if ($p) {
    Write-Output "After-OK Responding=$($p.Responding) CPU=$([math]::Round($p.CPU,1))"
} else {
    Write-Output "Process exited"
}