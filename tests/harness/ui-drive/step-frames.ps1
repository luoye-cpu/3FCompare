param([int]$TargetPid)
$wshell = New-Object -ComObject wscript.shell
$wshell.AppActivate($TargetPid) | Out-Null
Start-Sleep -Seconds 1
# 空格暂停
$wshell.SendKeys(' ')
Start-Sleep -Seconds 1
# 右键帧步进5次
for ($i = 0; $i -lt 5; $i++) {
    $wshell.SendKeys('{RIGHT}')
    Start-Sleep -Milliseconds 400
}
Start-Sleep -Seconds 1
$p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if ($p) {
    Write-Output "After-step Responding=$($p.Responding) CPU=$([math]::Round($p.CPU,1))"
} else {
    Write-Output "Process exited"
}