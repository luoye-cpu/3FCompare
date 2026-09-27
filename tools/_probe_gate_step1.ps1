# 临时探针：把 tools/发布门禁.ps1 的 [1/11] 整段切出来，用四份合成清单真跑一遍。
# 为什么切而不是抄一份进探针：抄的那份会漂移，"探针绿了"就不再等于"门禁那段对了"
# （本仓反复踩的"同一功能两份实现"）。放在 tools\ 下是为了让 $PSScriptRoot 与门禁一致。
$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$raw = Get-Content -LiteralPath (Join-Path $PSScriptRoot "发布门禁.ps1") -Raw -Encoding UTF8

$mStart = $raw.IndexOf("# ── [1/11]")
$mEnd = $raw.IndexOf("# ── [2/11]")
if ($mStart -lt 0 -or $mEnd -le $mStart) { throw "切不出 [1/11] 段" }
$block = $raw.Substring($mStart, $mEnd - $mStart)

$results = @()
function Add-Result([string]$name, [bool]$ok, [string]$detail = "") {
  $script:results += [pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail }
}
function Hash-Lower($p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }

$realDll = Join-Path $ProjectRoot "third_party\fff_project\FFF.Native\x64\Release\FFF.Native.dll"
if (-not (Test-Path -LiteralPath $realDll)) { throw "没有真内核：$realDll" }
$realHash = Hash-Lower $realDll
$baseSha = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot "构建全部.ps1") -Raw -Encoding UTF8),
                          '\$KernelBaselineSha\s*=\s*"([0-9a-f]{40})"').Groups[1].Value
if (-not $baseSha) { throw "构建全部.ps1 里没有 40 位基线 SHA" }

# 探针要往项目根写合成清单 ⇒ 先备份真清单，跑完还原并核指纹（探针自己不能成为错误源）。
$mfReal = Join-Path $ProjectRoot ".3fc_kernel_build.json"
$backupPath = Join-Path $env:TEMP ("3fc_manifest_backup_" + [Guid]::NewGuid().ToString("N") + ".json")
$backupHash = $null
if (Test-Path -LiteralPath $mfReal) {
  Copy-Item -LiteralPath $mfReal -Destination $backupPath -Force
  $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupPath).Hash
}

Write-Host "基线（从 构建全部.ps1 提取）= $baseSha"
Write-Host "真内核 sha256 = $realHash"
Write-Host ""

# 案例必须在脚本作用域跑：被切出的那段用 $PSScriptRoot 定位 构建全部.ps1，
# 放进函数 + Invoke-Expression 的组合里就解析不到了（第一次尝试正栽在那儿）。
$cases = @(
  @{ Label = "A 脏内核";       Head = $baseSha;   Dirty = $true;  Hash = $realHash;   Tag = "" },
  @{ Label = "B 干净正对照";   Head = $baseSha;   Dirty = $false; Hash = $realHash;   Tag = "3fcompare-kernel-2026.9.27.1" },
  @{ Label = "C 事后被换内核"; Head = $baseSha;   Dirty = $false; Hash = ("0" * 64);  Tag = "" },
  @{ Label = "D 非基线提交";   Head = ("1" * 40); Dirty = $false; Hash = $realHash;   Tag = "" }
)
$out = @()
# 被切出的那段用 $PSScriptRoot 定位 构建全部.ps1，而 Invoke-Expression 的新作用域里
# $PSScriptRoot 是空的（前两次尝试都栽在这儿）——改成把这段**原样**写进 tools\ 下的临时
# .ps1 再点源：点源运行在调用方作用域（看得见 Add-Result 与 $results），而 $PSScriptRoot
# 取该临时文件所在目录 = tools\，与门禁自己的解析一致。
$blockPath = Join-Path $PSScriptRoot "_probe_gate_step1_block.ps1"
$block | Set-Content -LiteralPath $blockPath -Encoding UTF8
try {
  foreach ($cse in $cases) {
    $mf = [ordered]@{ head = $cse.Head; baseline = ""; baselineTag = ""; kernelTag = $cse.Tag
                     dirty = $cse.Dirty; dirtyFiles = @(" M FFF.Native/x.cpp")
                     configuration = "Release"; kernelDll = $realDll; dllSha256 = $cse.Hash
                     builtAt = "2026-09-27 02:10:00" }
    $mf | ConvertTo-Json | Set-Content -LiteralPath $mfReal -Encoding UTF8
    $results = @()
    . $blockPath
    $r = $results | Select-Object -First 1
    if (-not $r) { Write-Host "[$($cse.Label)] ✗ 没有产生任何判定结果（那段代码没跑到 Add-Result）"; exit 2 }
    $out += $r
    Write-Host ("[{0}] ok={1} :: {2}" -f $cse.Label, $r.Ok, $r.Detail)
    Write-Host ""
  }
} finally {
  Remove-Item -LiteralPath $blockPath -Force -ErrorAction SilentlyContinue
}

# A/C/D 必须红、B 必须绿。B 是正对照：缺了它，三条红可能只是"代码写崩了"。
$ok = ((-not $out[0].Ok) -and $out[1].Ok -and (-not $out[2].Ok) -and (-not $out[3].Ok))
Write-Host ("判据自检：A红={0} B绿={1} C红={2} D红={3} => {4}" -f `
  (-not $out[0].Ok), $out[1].Ok, (-not $out[2].Ok), (-not $out[3].Ok),
  $(if ($ok) { "全部符合预期" } else { "不符合预期" }))

if ($null -ne $backupHash) {
  Copy-Item -LiteralPath $backupPath -Destination $mfReal -Force
  $nowHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $mfReal).Hash
  if ($nowHash -ne $backupHash) { Write-Host "✗ 清单还原后指纹不一致！" -ForegroundColor Red; exit 2 }
  Write-Host "清单已还原（指纹一致 $($backupHash.Substring(0,12))）"
  Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
} else {
  Remove-Item -LiteralPath $mfReal -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $mfReal) { Write-Host "✗ 探针合成的清单没能删掉！" -ForegroundColor Red; exit 2 }
  Write-Host "探针合成的清单已删除（跑探针前本就没有真清单）"
}
exit $(if ($ok) { 0 } else { 1 })
