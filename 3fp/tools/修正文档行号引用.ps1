# 把文档里的裸行号引用改写成"符号名 + 行号"形式
#
# 为什么：`HdrProcessor.cpp:245` 这种引用在代码一改后就静默失效 ——
# 不报错，只让读者跳到错误的行，读到无关代码并据此形成错误判断。
# 2026-10-08 实际发生：docs/61 的 20 处引用中 17 处已漂移（整体 +17 行）。
#
# 改写策略：在行号后**补上符号名**，而不是删掉行号。
#   `HdrProcessor.cpp:245`                →  `HdrProcessor.cpp:276（ProcessFrame 的 maximum_maxrgb 派生）`
# 行号仍有导航价值，符号名则保证重构后读者仍能定位。
#
# 本脚本只处理**明确列出的**已知错误引用，不做全局盲替换 ——
# 盲目改写会把本来正确的引用改坏。

$ErrorActionPreference = 'Stop'
$docs = 'C:\PLAN\3FCompare\3fp\docs'

# 旧引用 -> 新引用（含符号名，行号已按当前源码校正）
$map = @{
  'HdrProcessor.cpp:245' = 'HdrProcessor.cpp:276（`ProcessFrame` 内 `maximum_maxrgb * 10000.0`）'
  'HdrProcessor.cpp:227' = 'HdrProcessor.cpp:227（`ProcessFrame` 的 HLG 分支 `sourcePeakNits = 1000.0f`）'
  'HdrProcessor.cpp:222' = 'HdrProcessor.cpp:222（`ProcessFrame` 起点 `if (next.format == Sdr)`）'
  'HdrProcessor.cpp:244' = 'HdrProcessor.cpp:265 附近（`vividBaselineIsHlg` 判据的注释）'
  'HdrProcessor.cpp:248' = 'HdrProcessor.cpp:265 附近（`vividBaselineIsHlg` 判据的注释）'
  'HdrProcessor.cpp:236' = 'HdrProcessor.cpp:236（`ProcessFrame` 关于 Vivid 无自身传递函数的注释）'
  'HdrProcessor.cpp:282' = 'HdrProcessor.cpp:329 附近（Vivid 分支收尾）'
  'VideoRenderer.cpp:4121' = 'VideoRenderer.cpp:5345（`Render` 内 `settings.transfer` 判定）'
  'VideoRenderer.cpp:4124' = 'VideoRenderer.cpp:5348（`Render` 内 `settings.transfer` 判定收尾）'
  '3FP__Render__VideoRenderer.cpp:4313' = '3FP__Render__VideoRenderer.cpp:4313（历史副本，行号仅对该副本有效）'
}

$changed = 0
foreach ($file in Get-ChildItem $docs -Filter '*.md') {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $before = $text
    foreach ($old in $map.Keys) {
        # 只替换反引号包裹的引用，避免误伤正文里的普通数字
        $text = $text.Replace("``$old``", "``$($map[$old])``")
    }
    if ($text -ne $before) {
        [System.IO.File]::WriteAllText($file.FullName, $text)
        $n = ($map.Keys | Where-Object { $before.Contains("``$_``") }).Count
        Write-Host ("  {0,-56} 改写 {1} 类引用" -f $file.Name.Substring(0, [Math]::Min(54, $file.Name.Length)), $n)
        $changed++
    }
}
Write-Host ""
Write-Host "共修改 $changed 篇文档" -ForegroundColor Green
