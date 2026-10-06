# dump_associations.ps1 —— 快照 HKCU 里图片/视频扩展名的关联现状（只读）
# 用法: powershell -File dump_associations.ps1 <输出文件> <扩展名列表,逗号分隔>
param(
  [Parameter(Mandatory=$true)][string]$Out,
  [Parameter(Mandatory=$true)][string]$Exts
)
$rows = @()
foreach ($e in ($Exts -split ',')) {
  $p = "HKCU:\Software\Classes\$e"
  $k = Get-Item -Path $p -ErrorAction SilentlyContinue
  if ($null -eq $k) {
    $rows += ("{0}|(键缺失)|-|-|owp=(键缺失)|FFFProgId=False" -f $e)
    continue
  }
  $def = [string]$k.GetValue('')
  $ct  = [string]$k.GetValue('Content Type')
  $pt  = [string]$k.GetValue('PerceivedType')
  $owp = @()
  $owpPath = "$p\OpenWithProgids"
  if (Test-Path $owpPath) { $owp = (Get-Item $owpPath).GetValueNames() }
  $progId = "FFF.Player$e"
  $hasProgId = Test-Path "HKCU:\Software\Classes\$progId"
  $rows += ("{0}|default={1}|ct={2}|pt={3}|owp={4}|FFFProgId={5}" -f `
            $e, $def, $ct, $pt, ($owp -join '+'), $hasProgId)
}
$rows += "--- 应用级注册 ---"
$appPath = 'HKCU:\Software\Classes\Applications\FFF.Player.exe'
if (Test-Path $appPath) {
  $st = "$appPath\SupportedTypes"
  $rows += "Applications\FFF.Player.exe 存在; SupportedTypes=" + `
           $(if (Test-Path $st) { ((Get-Item $st).GetValueNames() -join ' ') } else { '(无)' })
} else { $rows += "Applications\FFF.Player.exe (缺失)" }
$cap = 'HKCU:\Software\1059 Studio\FFF.Player'
$rows += "CapabilitiesRoot 存在=" + (Test-Path "$cap")
$bak = "$cap\FileAssociationBackups"
$rows += "FileAssociationBackups 存在=" + (Test-Path $bak) + `
         $(if (Test-Path $bak) { ' 子项=' + ((Get-Item $bak).GetSubKeyNames() -join ' ') } else { '' })
$reg = 'HKCU:\Software\RegisteredApplications'
$rows += "RegisteredApplications\FFF.Player=" + `
         $(if ((Get-Item $reg -ErrorAction SilentlyContinue)) { [bool]((Get-Item $reg).GetValue('FFF.Player')) } else { '键缺失' })
$rows | Set-Content -Path $Out -Encoding UTF8
Write-Output "写入 $Out ($(($rows).Count) 行)"
