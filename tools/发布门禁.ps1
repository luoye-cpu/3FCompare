# 3FCompare 发布门禁（P1-14）
# 用法: powershell -ExecutionPolicy Bypass -File tools/发布门禁.ps1 [-Version 0.2.0]
#       [-WithPack] [-SkipSelfTest] [-StrictGit] [-Media <视频路径>] [-LogPath <结果文件>]
#
#   版本号唯一真源 = src/3FCompare/3FCompare.csproj 的 <Version>；
#   不传 -Version 时自动取 csproj（与 pack.ps1 同一套规则）。
#
# 背景：本次审查发现的 4 个 P0 全部属于"脚本一路绿灯、产物/功能其实是坏的"——
# 会话加载必然失败、导出被固定降采样、完整包缺 FFmpeg、发行包里塞着本机日志。
# 它们没有一个是编译期能发现的，只能靠打包前的固定门禁拦住。
# 本脚本把审查报告 §5 的 checklist 固化成可执行的断言。

param(
    [string]$Version = "",  # 留空则取 csproj 的 <Version>（唯一真源）
    [switch]$WithPack,      # 额外执行 pack.ps1（NativeAOT 发布耗时长，默认不跑）
    [switch]$SkipSelfTest,  # 跳过实机自测（无真实素材/无 GPU 环境时用）
    [switch]$StrictGit,     # 工作区不干净即失败（默认只警告）
    [string]$Media = "",    # 指定实机素材；留空则自动取 testmedia/media/real 下前两个
    [string]$LogPath = ""   # 结果同时写入文件（CI 归档；非交互/子进程调用时控制台输出易丢失）
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$AppProject  = Join-Path $ProjectRoot "src\3FCompare\3FCompare.csproj"

# 版本号：默认从 csproj 读，避免门禁打印的版本与程序集/产物版本对不上
if ([string]::IsNullOrWhiteSpace($Version) -and (Test-Path -LiteralPath $AppProject)) {
    $text = Get-Content -Raw -LiteralPath $AppProject
    $m = [regex]::Match($text, '<Version>\s*([^<]+)\s*</Version>')
    if ($m.Success) { $Version = $m.Groups[1].Value.Trim() }
    else { $Version = "未知" }
}
$TestProject = Join-Path $ProjectRoot "tests\3FCompare.Core.Tests\3FCompare.Core.Tests.csproj"
$Exe         = Join-Path $ProjectRoot "src\3FCompare\bin\Release\net11.0-windows\3FCompare.exe"
$TmpDir      = Join-Path $ProjectRoot "testmedia\tmp"

# 已知无害、暂不阻断的告警（Avalonia XAML 加载器提示，MainWindow 由代码直接 new）
$AllowedWarnings = @("AVLN3001")

# 本机 dotnet restore 全域失败（沙箱 APPDATA 为空导致 Path.Combine(null)），
# 所有 build/test 必须前置 APPDATA 并加 --no-restore。
if (-not $env:APPDATA) {
    Write-Host "⚠ 环境缺少 APPDATA，兜底设置为默认用户目录" -ForegroundColor Yellow
    $env:APPDATA = Join-Path $env:USERPROFILE "AppData\Roaming"
}

# dotnet / git 不一定在 PowerShell 的 PATH 上（Git Bash 里有、PowerShell 会话里可能没有），显式解析
function Resolve-Tool([string]$name, [string[]]$fallbacks) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    foreach ($p in $fallbacks) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}
$Dotnet = Resolve-Tool "dotnet" @("$env:ProgramFiles\dotnet\dotnet.exe", "C:\Program Files\dotnet\dotnet.exe")
if (-not $Dotnet) { throw "未找到 dotnet，请确认已安装 .NET SDK 并将其加入 PATH" }
$Git = Resolve-Tool "git" @("$env:ProgramFiles\Git\cmd\git.exe", "C:\Program Files\Git\cmd\git.exe",
                             "C:\Program Files\Git\bin\git.exe")
Write-Host "  dotnet: $Dotnet" -ForegroundColor Gray
if ($Git) { Write-Host "  git   : $Git" -ForegroundColor Gray }
else { Write-Host "  git   : 未找到（工作区检查将跳过）" -ForegroundColor Yellow }

$results = New-Object System.Collections.Generic.List[object]

# 日志落盘：CI / 子进程调用时 Write-Host 的输出经常整段丢失，只剩一个 exit=1，
# 无法定位是哪一步失败（本次排查就卡在这里）。落盘后至少能拿到逐项结论。
function Write-Log([string]$line) {
    if (-not $LogPath) { return }
    try { [IO.File]::AppendAllText($LogPath, $line + "`n", (New-Object Text.UTF8Encoding $false)) } catch { }
}
if ($LogPath) {
    [IO.File]::WriteAllText($LogPath,
        "3FCompare 发布门禁  v$Version  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n",
        (New-Object Text.UTF8Encoding $false))
}

function Add-Result([string]$name, [bool]$ok, [string]$detail = "") {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    if ($ok) { Write-Host "  ✅ $name" -ForegroundColor Green }
    else { Write-Host "  ❌ $name  $detail" -ForegroundColor Red }
    Write-Log ("{0}  {1}  {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail)
}

# 原生命令（dotnet / 3FCompare.exe）的 stderr 在 $ErrorActionPreference='Stop' 下
# 会被当作终止错误抛出——构建工具往 stderr 写进度是常态，必须临时降级为 Continue。
function Invoke-Cmd {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # 必须先清空：$LASTEXITCODE 是"上一次原生调用"的残留值。若本次进程压根没起来
    # （沙箱 / PATH 缺失），它会沿用上一次成功调用的 0 → 门禁假绿。
    $global:LASTEXITCODE = $null
    $raw = & $FilePath @Arguments 2>&1
    # 某些受限环境（如自动化沙箱）根本起不了进程：此时 $LASTEXITCODE 为 null。
    # 不显式兜底的话，门禁只会显示"exit="空值，看不出到底是测试失败还是命令没跑起来。
    $code = if ($null -eq $LASTEXITCODE) { -1 } else { $LASTEXITCODE }
    $ErrorActionPreference = $prev
    return [pscustomobject]@{
        Exit = $code
        Out  = @($raw | ForEach-Object { $_.ToString() })
    }
}

# exit=-1 是"命令根本没启动"（受限环境 / dotnet 不在 PATH / 沙箱禁止起进程），
# 与"跑完了但失败"是两回事。若不区分，编译项会显示成"错误 0 / 告警 0"却判失败，
# 排查时极易被误导去查编译警告逻辑，实际该看的是"为什么命令没起来"。
# 注意：注释必须用 # —— 写成 C# 的 /// 时 ParseFile 语法检查仍会通过，只有实际运行才报错。
function Format-FailDetail([int]$code, [string]$detail) {
    if ($code -eq -1) {
        return "命令未能启动（exit=-1）：本会话无法启动原生进程，请在真实终端运行门禁"
    }
    return $detail
}

function Get-BuildCounters([string[]]$output) {
    $text = $output -join "`n"
    $err = 0; $warn = 0
    if ($text -match '(\d+)\s*个错误') { $err = [int]$Matches[1] }
    if ($text -match '(\d+)\s*个警告') { $warn = [int]$Matches[1] }
    if ($text -match '(\d+)\s*Error\(s\)') { $err = [int]$Matches[1] }
    if ($text -match '(\d+)\s*Warning\(s\)') { $warn = [int]$Matches[1] }
    return [pscustomobject]@{ Error = $err; Warning = $warn; Text = $text }
}

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  3FCompare 发布门禁  v$Version" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# ── [1/7] 内核基线：产物内核必须是发布基线（P1-8）──
#   旧流程里 -AllowKernelDrift 与"内核工作区脏"都只 Write-Warning 就继续，
#   构建出的 DLL 照常部署，之后 pack.ps1 与本脚本都不再校验 SHA ——
#   脏内核 / 错误内核可以一路绿灯进发布包。
#   基线值从 tools/构建全部.ps1 正则提取，保持单一来源，避免两处硬编码漂移。
Write-Host "`n[1/7] 内核基线（产物内核 SHA == 发布基线）" -ForegroundColor Yellow
$KernelBaselineSha = ""
$buildAll = Join-Path $PSScriptRoot "构建全部.ps1"
if (Test-Path $buildAll) {
    $m = [regex]::Match((Get-Content $buildAll -Raw -Encoding UTF8),
                        '\$KernelBaselineSha\s*=\s*"([0-9a-fA-F]{7,40})"')
    if ($m.Success) { $KernelBaselineSha = $m.Groups[1].Value }
}
$shaFile = Join-Path $ProjectRoot ".3fc_kernel_sha"
if (-not $KernelBaselineSha) {
    Add-Result "内核基线" $false "无法从 tools/构建全部.ps1 提取 `$KernelBaselineSha"
} elseif (-not (Test-Path $shaFile)) {
    Add-Result "内核基线" $false "未找到 $shaFile（请先运行 tools/构建全部.ps1）"
} else {
    $actual = (Get-Content $shaFile -Raw).Trim()
    $short  = if ($actual.Length -ge 12) { $actual.Substring(0, 12) } else { $actual }
    Add-Result "内核基线" ($actual -eq $KernelBaselineSha) `
        "实际 $short / 基线 $($KernelBaselineSha.Substring(0,12))"
}

# ── [2/7] Release 编译：0 error，且非白名单告警必须为 0 ──
Write-Host "`n[2/7] Release 编译（0 error / 0 非白名单 warning）" -ForegroundColor Yellow
$build    = Invoke-Cmd $Dotnet @("build", $AppProject, "-c", "Release", "--no-restore", "--nologo")
$buildOut = $build.Out
$buildOk  = $build.Exit -eq 0
$c = Get-BuildCounters $buildOut
if ($c.Error -gt 0) { $buildOk = $false }
# 先排除 MSBuild 的计数摘要行：英文 SDK 输出 "    1 Warning(s)" / "    0 Error(s)"，
# 这些行不含告警 code，若被 -match 'warning' 命中就会落进"白名单外告警"→ 英文 CI 恒定失败。
# 中文摘要写作"1 个警告"，不含 warning，所以本机一直不暴露这个坑。
$realWarnings = @($buildOut |
    Where-Object { $_ -match 'warning' -and $_ -notmatch 'Warning\(s\)' -and $_ -notmatch 'Error\(s\)' } |
    Where-Object { $allowed = $false; foreach ($a in $AllowedWarnings) { if ($_ -match $a) { $allowed = $true } }; -not $allowed })
if ($realWarnings.Count -gt 0) {
    $buildOk = $false
    $realWarnings | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
}
Add-Result "Release 编译" $buildOk (Format-FailDetail $build.Exit "错误 $($c.Error) / 告警 $($c.Warning)（白名单外 $($realWarnings.Count)）")

# ── [3/7] 单元测试全绿 ──
Write-Host "`n[3/7] 单元测试" -ForegroundColor Yellow
$test    = Invoke-Cmd $Dotnet @("test", $TestProject, "-c", "Release", "--no-restore", "--nologo")
$testOut = $test.Out
$testOk  = $test.Exit -eq 0
$summary = ($testOut | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
Add-Result "单元测试" $testOk (Format-FailDetail $test.Exit $summary)

if (-not $SkipSelfTest) {
    # 素材列表供 [4-6/7] 三步共用，先统一解析（原先只在会话往返那步内定义，
    # 新增的 selftest 排在它前面会拿不到）。
    if (-not (Test-Path $Exe)) {
        Add-Result "单路全量" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
        Add-Result "会话往返" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
        Add-Result "帧导出" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
    } else {
    $mediaList = @()
    if ($Media) { $mediaList = @($Media) }
    else {
        $mediaList = @(Get-ChildItem (Join-Path $ProjectRoot "testmedia\media\real\*.mp4") -ErrorAction SilentlyContinue |
            Sort-Object Name | Select-Object -First 2 | ForEach-Object { $_.FullName })
    }
    # 用 -join 而不是 Join-String：后者是 pwsh7 专有，本脚本可能被 Windows PowerShell 5.1 调用
    $mediaNames = ($mediaList | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ' + '
    Write-Host "  素材: $mediaNames" -ForegroundColor Gray

    # ── [4/7] 单路全量（覆盖面最广：布局/探针/倍速/消息注入/最大化……）──
    # docs/14 §4.3：它此前**从不被任何脚本调用**，等于这条最宽的回归网一直是空的。
    Write-Host "`n[4/7] 单路全量回归（--selftest）" -ForegroundColor Yellow
    if ($mediaList.Count -lt 1) {
        Add-Result "单路全量" $false "需要至少 1 个真实素材（testmedia/media/real/*.mp4），实际 $($mediaList.Count) 个"
    } else {
        $stArgs = @("--selftest", $mediaList[0])
        if ($mediaList.Count -ge 2) { $stArgs += $mediaList[1] } # 第二个给了才跑"文件拖入"分支
        $run  = Invoke-Cmd $Exe $stArgs
        $code = $run.Exit
        $line = ($run.Out | Where-Object { $_ -match 'selftest\[|全部通过' } | Select-Object -Last 1)
        Add-Result "单路全量" ($code -eq 0) (Format-FailDetail $code "$line (exit=$code)")
    }

    # ── [5/7] 会话往返（拦 P0-1 / P0-3）──
    Write-Host "`n[5/7] 会话存取往返回归（--sessiontest）" -ForegroundColor Yellow
        if ($mediaList.Count -lt 2) {
            Add-Result "会话往返" $false "需要 2 个真实素材（testmedia/media/real/*.mp4），实际 $($mediaList.Count) 个"
        } else {
            Write-Host "  素材: $([IO.Path]::GetFileName($mediaList[0])) + $([IO.Path]::GetFileName($mediaList[1]))" -ForegroundColor Gray
            $run  = Invoke-Cmd $Exe @("--sessiontest", $mediaList[0], $mediaList[1])
            $out  = $run.Out
            $code = $run.Exit
            $line = ($out | Where-Object { $_ -match 'sessiontest:' } | Select-Object -Last 1)
            $detail = Format-FailDetail $code "$line (exit=$code)"
            Add-Result "会话往返" ($code -eq 0) $detail
        }

    # ── [6/7] 抓帧导出（拦 P0-2）──
    Write-Host "`n[6/7] 帧导出回归（--screentest）" -ForegroundColor Yellow
    if ($mediaList.Count -ge 1) {
        New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null
        $png = Join-Path $TmpDir "gate_frame.png"
        if (Test-Path $png) { Remove-Item $png -Force }
        $run  = Invoke-Cmd $Exe @("--screentest", $mediaList[0], $png)
        $out  = $run.Out
        $code = $run.Exit
        $size = if (Test-Path $png) { (Get-Item $png).Length } else { 0 }
        $dim = ($out | Where-Object { $_ -match 'screentest: 导出' } | Select-Object -Last 1)
        Add-Result "帧导出" (($code -eq 0) -and ($size -gt 1000)) (Format-FailDetail $code "$dim, $size bytes (exit=$code)")
    } else {
        Add-Result "帧导出" $false "缺少 exe 或素材，跳过前置条件不足"
    }
    }   # 关闭上面的 else（exe 存在分支）
} else {
    Write-Host "`n[4-6/7] 实机自测已按 -SkipSelfTest 跳过" -ForegroundColor Yellow
}

# ── [7/7] 打包 + 产物自检（可选）与仓库卫生 ──
if ($WithPack) {
    Write-Host "`n[7/7] 打包与产物自检（pack.ps1 内含 Assert-Package）" -ForegroundColor Yellow
    $pack = Invoke-Cmd "powershell" @("-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $ProjectRoot "pack.ps1"), "-Version", $Version, "-Mode", "all")
    Add-Result "打包与产物自检" ($pack.Exit -eq 0) (Format-FailDetail $pack.Exit "pack.ps1 exit=$($pack.Exit)")
} else {
    Write-Host "`n[7/7] 打包已跳过（加 -WithPack 启用）" -ForegroundColor Yellow
}

Write-Host "`n[仓库卫生] git 工作区" -ForegroundColor Yellow
$dirty = if ($Git) { (Invoke-Cmd $Git @("-C", $ProjectRoot, "status", "--porcelain")).Out } else { @() }
$dirtyCount = @($dirty | Where-Object { $_ -and $_.Trim() -ne "" }).Count
if ($dirtyCount -gt 0) {
    $msg = "$dirtyCount 个未提交项（发布前易漏提交）"
    if ($StrictGit) { Add-Result "工作区干净" $false $msg }
    else { Write-Host "  ⚠ $msg —— 加 -StrictGit 可将其升级为失败" -ForegroundColor Yellow }
    $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor Gray }
} elseif ($Git) {
    Add-Result "工作区干净" $true ""
}

# ── 汇总 ──
Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
$failed = @($results | Where-Object { -not $_.Ok })
foreach ($r in $results) {
    $mark = if ($r.Ok) { "✅" } else { "❌" }
    Write-Host "  $mark $($r.Name)  $($r.Detail)"
}
if ($failed.Count -gt 0) {
    Write-Host "`n发布门禁未通过：$($failed.Count) 项失败" -ForegroundColor Red
    Write-Log "发布门禁未通过：$($failed.Count) 项失败"
    exit 1
}
Write-Host "`n发布门禁全部通过 ✅" -ForegroundColor Green
Write-Log "发布门禁全部通过"
exit 0
