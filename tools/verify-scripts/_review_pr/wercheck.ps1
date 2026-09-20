# 读取 3FCompare 最新的 WER AppCrash 报告，输出一行：
# count|EventTimeFileTime|sig3(故障模块)|sig6(异常偏移)|sig7(异常代码)|sig8(异常数据)|mtimeIso
# 仅只读查询，不修改任何东西。
$ErrorActionPreference = 'SilentlyContinue'
$root = 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive'
$dirs = @(Get-ChildItem $root -Directory -Filter 'AppCrash_3FCompare*')
$count = $dirs.Count
$newest = $dirs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $newest) { Write-Output "$count|0|||||"; exit 0 }
$lines = Get-Content (Join-Path $newest.FullName 'Report.wer') -Encoding Unicode
function Field($name) {
  $m = $lines | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
  if ($m) { return $m.Substring($name.Length + 1) } else { return '' }
}
$et = Field 'EventTime'
$sig3 = Field 'Sig[3].Value'
$sig6 = Field 'Sig[6].Value'
$sig7 = Field 'Sig[7].Value'
$sig8 = Field 'Sig[8].Value'
$mt = $newest.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
Write-Output "$count|$et|$sig3|$sig6|$sig7|$sig8|$mt"
