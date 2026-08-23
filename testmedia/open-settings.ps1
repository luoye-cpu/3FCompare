param([int]$TargetPid)
$wshell = New-Object -ComObject wscript.shell
$wshell.AppActivate($TargetPid) | Out-Null
Start-Sleep -Seconds 1
$wshell.SendKeys('%s')
Start-Sleep -Seconds 2
$wshell.SendKeys('{ENTER}')
Start-Sleep -Seconds 6
$p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
if ($p) {
    Write-Output "Responding=$($p.Responding) CPU=$([math]::Round($p.CPU,1))"
} else {
    Write-Output "Process exited"
}