# 临时探针：真跑 tools/构建全部.ps1 里"内核状态采集 + 构建清单写出"那两段。
# 为什么单独跑它：那两段是这轮新写的、发布门禁 [1/11] 的唯一输入来源，
# 而整脚本跑一遍 = 重建内核 + 重放补丁队列（风险不对等）。所以把**原文**切出来，
# 把 $ProjectRoot 指到临时目录（清单落临时区，绝不碰仓库根那份真清单），
# $ForkRoot 指真内核（只读 git 查询）——测的是真代码、真输入，不测副本。
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$src = Get-Content -LiteralPath (Join-Path $PSScriptRoot "构建全部.ps1") -Raw -Encoding UTF8

$fnStart = $src.IndexOf('$KernelRepo ')
$fnEnd = $src.IndexOf('if (-not (Test-Path (Join-Path $ForkRoot "FFF.Native')
if ($fnStart -lt 0 -or $fnEnd -le $fnStart) { throw "切不出常量+函数段" }
$fnBlock = $src.Substring($fnStart, $fnEnd - $fnStart)

$mfStart = $src.IndexOf('$KernelDllPath = Join-Path $ForkRoot')
if ($mfStart -lt 0) { throw "切不出清单写出段" }
# 清单段跑到"Write-Host "  清单 -> " 这一行结束（含），后面的收尾提示不参与
$mfEnd = $src.IndexOf('Write-Host "  清单 ->')
$mfEnd = $src.IndexOf("`n", $mfEnd)
if ($mfEnd -le $mfStart) { throw "清单写出段边界找不到" }
$mfBlock = $src.Substring($mfStart, $mfEnd - $mfStart)

$tmp = Join-Path $env:TEMP ("3fc_manifest_probe_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try
{
  # 被测代码用的三个变量在这里给值：ProjectRoot 指向临时目录，ForkRoot 指向真内核
  $ProjectRoot = $tmp
  $ForkRoot = Join-Path $repo "third_party\fff_project"
  $Configuration = "Release"

  Invoke-Expression $fnBlock      # 定义常量与 Invoke-Git / Get-FileSha256 / Get-KernelState
  $KernelState = Get-KernelState
  Invoke-Expression $mfBlock      # 写 $tmp\.3fc_kernel_build.json + $tmp\.3fc_kernel_sha

  $mfPath = Join-Path $tmp ".3fc_kernel_build.json"
  if (-not (Test-Path -LiteralPath $mfPath)) { throw "清单没写出来：$mfPath" }
  $mf = Get-Content -LiteralPath $mfPath -Raw -Encoding UTF8 | ConvertFrom-Json

  # 逐项核：字段齐、值对得上现场，任何一项空/缺都判失败
  $realDll = Join-Path $ForkRoot "FFF.Native\x64\Release\FFF.Native.dll"
  $wantHead = (git -C $ForkRoot rev-parse HEAD).Trim()
  $wantDirty = ((git -C $ForkRoot status --porcelain) | Where-Object { $_ -ne "" }).Count -gt 0
  $wantHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $realDll).Hash.ToLowerInvariant()
  $bad = @()
  if ($mf.head -ne $wantHead) { $bad += "head 不符：$($mf.head) ≠ $wantHead" }
  if ([bool]$mf.dirty -ne $wantDirty) { $bad += "dirty 不符：$($mf.dirty) ≠ $wantDirty" }
  if ($mf.dllSha256 -ne $wantHash) { $bad += "dllSha256 不符：$($mf.dllSha256) ≠ $wantHash" }
  if ($mf.configuration -ne "Release") { $bad += "configuration 丢了" }
  if (-not $mf.kernelDll) { $bad += "kernelDll 为空" }
  if (-not $mf.builtAt) { $bad += "builtAt 为空" }
  $shaLine = (Get-Content -LiteralPath (Join-Path $tmp ".3fc_kernel_sha") -Raw).Trim()
  if ($shaLine -ne $wantHead) { $bad += ".3fc_kernel_sha 与 head 不一致：$shaLine" }

  Write-Host "清单内容："
  Get-Content -LiteralPath $mfPath -Raw -Encoding UTF8
  Write-Host "内核实况：head=$wantHead dirty=$wantDirty tag=$($KernelState.Tag)"
  if ($bad.Count) { Write-Host "`n✗ 不符项："; $bad | ForEach-Object { Write-Host "   $_" }; exit 1 }
  Write-Host "`n✓ 清单字段与内核现场逐项一致（head/dirty/dllSha256/configuration/kernelDll/builtAt + sha 文件）"

  # 反向自检：门禁那边能不能把这份**真清单**判对？脏内核必须判红（今天的实况就是脏）
  # 门禁那段用 $PSScriptRoot 定位 构建全部.ps1，而 IEX 的作用域里它是空的
  # ——所以把原文写进 tools\ 下的临时文件再点源（与 _probe_gate_step1.ps1 同一个办法）。
  $gate = Get-Content -LiteralPath (Join-Path $PSScriptRoot "发布门禁.ps1") -Raw -Encoding UTF8
  $g1 = $gate.IndexOf("# ── [1/11]"); $g2 = $gate.IndexOf("# ── [2/11]")
  if ($g1 -lt 0 -or $g2 -le $g1) { throw "切不出门禁 [1/11]" }
  $blockPath = Join-Path $PSScriptRoot "_probe_build_manifest_gate_block.ps1"
  $gate.Substring($g1, $g2 - $g1) | Set-Content -LiteralPath $blockPath -Encoding UTF8
  try
  {
    $results = @()
    function Add-Result([string]$n, [bool]$ok, [string]$d = "") { $script:results += [pscustomobject]@{Ok=$ok;Detail=$d} }
    # 门禁那段读的是**仓库根**的清单；探针把真清单原文复制过去判，判完还原
    $realManifest = Join-Path $repo ".3fc_kernel_build.json"
    $hadReal = Test-Path -LiteralPath $realManifest
    $backup = $null
    if ($hadReal) { $backup = Join-Path $env:TEMP ("mf_" + [Guid]::NewGuid().ToString("N")); Copy-Item $realManifest $backup -Force }
    Copy-Item -LiteralPath $mfPath -Destination $realManifest -Force
    try { . $blockPath }
    finally
    {
      if ($hadReal) { Copy-Item $backup $realManifest -Force; Remove-Item $backup -Force }
      else { Remove-Item $realManifest -Force -ErrorAction SilentlyContinue }
    }
  }
  finally { Remove-Item -LiteralPath $blockPath -Force -ErrorAction SilentlyContinue }
  $r = $results | Select-Object -First 1
  if (-not $r) { Write-Host "✗ 门禁那段没产生判定结果"; exit 1 }
  Write-Host "门禁 [1/11] 对这份真清单的判定：ok=$($r.Ok) :: $($r.Detail)"
  if ($wantDirty -and $r.Ok) { Write-Host "✗ 内核是脏的，门禁却判绿 ⇒ 拦截失效"; exit 1 }
  if (-not $wantDirty -and -not $r.Ok) { Write-Host "✗ 内核是干净的，门禁却判红"; exit 1 }
  Write-Host "✓ 脏/干净与门禁判定方向一致"
  exit 0
}
finally {
  Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $tmp) { Write-Host "✗ 临时目录没清掉：$tmp" }
}
