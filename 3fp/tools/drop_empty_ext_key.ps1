param([Parameter(Mandatory=$true)][string]$Ext)
# drop_empty_ext_key.ps1 —— 只有当 HKCU\Software\Classes\<ext> 及其全部子键都确实为空时才删，
# 用于清掉本轮实机测试自己留下的空壳键（基线里该键本不存在）。
$ErrorActionPreference = 'Stop'
$path = "HKCU:\Software\Classes\$Ext"
if (-not (Test-Path $path)) { Write-Output "$Ext : 无键，无需清理"; exit 0 }
$key = Get-Item $path
$非空值 = @($key.GetValueNames()) | Where-Object { $_ -ne '(default)' -or [string]$key.GetValue('') -ne '' }
foreach ($子 in $key.GetSubKeyNames()) {
    $sk = Get-Item "$path\$子"
    $非空值 += @($sk.GetValueNames()) | Where-Object { $_ -ne '(default)' -or [string]$sk.GetValue('') -ne '' }
    if (@($sk.GetSubKeyNames()).Count -gt 0) { $非空值 += "$($子) 还有子键" }
}
if ($非空值.Count -gt 0) {
    Write-Output "$Ext : 拒绝删除，仍有内容：[$($非空值 -join ' | ')]"
    exit 1
}
Remove-Item -Path $path -Recurse -Force
Write-Output "$Ext : 已删除空壳键（含空的子键），恢复到基线的'无键'状态"
