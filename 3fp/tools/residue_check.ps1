param(
    [Parameter(Mandatory=$true)][string]$Ext,
    [switch]$DeleteIfEmpty
)
# residue_check.ps1 —— 查一个扩展名在 HKCU 的键是否只剩"我们创建过的空壳"，以及它会不会遮蔽 HKLM。
# 背景：注销文件类型 会删值但不删它创建过的键 ⇒ 基线里本不存在的扩展名（如 .apng）会留下空键。
# 空键的 default 为空 ⇒ HKCU 优先于 HKLM，可能把机器级的同名注册遮掉。
$ErrorActionPreference = 'Stop'
$hkcu = "HKCU:\Software\Classes\$Ext"
$hklm = "HKLM:\SOFTWARE\Classes\$Ext"
$机器 = if (Test-Path $hklm) {
    $d = [string](Get-Item $hklm).GetValue('')
    "HKLM 存在 default='$d'"
} else { "HKLM 无此类注册" }
if (-not (Test-Path $hkcu)) { Write-Output "$Ext : HKCU 无键（干净） / $机器"; exit 0 }
$i = Get-Item $hkcu
$值 = @($i.GetValueNames()) | Where-Object { $_ -ne '(default)' }
$子 = @($i.GetSubKeyNames())
$默认 = [string]$i.GetValue('')
$空壳 = ($默认 -eq '' -and $值.Count -eq 0 -and $子.Count -eq 0)
Write-Output ("{0} : default='{1}' 值=[{2}] 子键=[{3}] 空壳={4} / {5}" -f `
    $Ext, $默认, ($值 -join ','), ($子 -join ','), $空壳, $机器)
if ($DeleteIfEmpty) {
    if (-not $空壳) { Write-Output "拒绝删除：$Ext 不是空壳"; exit 1 }
    Remove-Item -Path $hkcu -Force
    Write-Output "已删除空壳键 $hkcu（恢复到基线'无键'状态）"
}
