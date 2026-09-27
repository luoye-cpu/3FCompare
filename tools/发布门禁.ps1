# 3FCompare 发布门禁（P1-14）
# 用法: powershell -ExecutionPolicy Bypass -File tools/发布门禁.ps1 [-Version 0.2.0]
#       [-WithPack] [-SkipSelfTest] [-SkipPackageSelfTest] [-StrictGit]
#       [-Media <视频路径>] [-LogPath <结果文件>]
#       [-KernelConfiguration <Release|Debug>]（默认 Release，须与构建内核时用的配置一致）
#
#   版本号唯一真源 = src/3FCompare/3FCompare.csproj 的 <Version>；
#   不传 -Version 时自动取 csproj（与 pack.ps1 同一套规则）。
#
# 背景：本次审查发现的 4 个 P0 全部属于"脚本一路绿灯、产物/功能其实是坏的"——
# 会话加载必然失败、导出被固定降采样、完整包缺 FFmpeg、发行包里塞着本机日志。
# 它们没有一个是编译期能发现的，只能靠打包前的固定门禁拦住。
# 本脚本把审查报告 §5 的 checklist 固化成可执行的断言。
#
# ── 步骤编号约定（docs/41 §13.6「门禁编号漂移」已修）──
# 分母 = 本脚本实际编号步骤总数 = **11**（旧版一律写 7 作分母，而实际早已不是 7 项）。
# 编号一律**扁平连续 1..11**，不再用 [1b] / [3b] / [3c] 这类字母后缀——它们不是"某步的子项"，
# 而是各自独立、各自 Add-Result 的门禁项，写成后缀会让分母永远对不上。
# 旧编号（分母 7）→ 新编号（分母 11），供对照 docs/41 的历史记录：
#   [1]→[1/11]   [1b]→[2/11]   [2]→[3/11]   [3]→[4/11]   [3b]→[5/11]
#   [3c]→[6/11]  [4]→[7/11]    [5]→[8/11]   [6]→[9/11]   [7]→[10/11]
#   [11/11] AOT 包内自测（docs/45 P0-2 新增，无旧编号）
# `[仓库卫生]` 是条件项（仅 -StrictGit 或工作区干净时才 Add-Result），沿用原名不参与编号。
# Step-numbering convention (bilingual): denominator = 11 actual numbered steps, flat 1..11.

param(
    [string]$Version = "",  # 留空则取 csproj 的 <Version>（唯一真源）
    [switch]$WithPack,      # 额外执行 pack.ps1（NativeAOT 发布耗时长，默认不跑）
    [switch]$SkipSelfTest,  # 跳过实机自测（无真实素材/无 GPU 环境时用）
    [switch]$SkipPackageSelfTest,
                            # 显式降级：跳过 [11/11] AOT 包内自测。**不是"跳过不判"**——
                            # 该项照样判红、汇总里单独列出、退出码非 0（docs/45 P0-2：
                            # AOT 裁剪/反射类缺陷只有 [11/11] 能覆盖，没验过就不能算通过）。
                            # 仅影响 [11/11]，不影响 [7-9/11]（那是 -SkipSelfTest 的范围）。
    [switch]$StrictGit,     # 工作区不干净即失败（默认只警告）
    [string]$Media = "",    # 指定实机素材；留空则自动取 testmedia/media/real 下前两个
    [string]$KernelConfiguration = "Release",
                            # 内核配置目录名：third_party\...\FFF.Native\x64\<配置>\FFF.Native.dll，
                            # 与 csproj 的 $(KernelConfiguration) 同义。发布走默认 Release；
                            # 覆盖它时本脚本会把它一并传给 [3/11] 的 dotnet build，
                            # 保证"校验的内核"与"内嵌进 exe 的内核"是同一份。
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
$PlatformTest = Join-Path $ProjectRoot "tests\3FCompare.Platform.Tests\3FCompare.Platform.Tests.csproj"
$SmokeProject = Join-Path $ProjectRoot "tests\3FCompare.SmokeTests\3FCompare.SmokeTests.csproj"
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

# ── [1/11] 内核基线：产物内核必须是发布基线（P1-8）──
#   旧流程里 -AllowKernelDrift 与"内核工作区脏"都只 Write-Warning 就继续，
#   构建出的 DLL 照常部署，之后 pack.ps1 与本脚本都不再校验 SHA ——
#   脏内核 / 错误内核可以一路绿灯进发布包。
#   基线值从 tools/构建全部.ps1 正则提取，保持单一来源，避免两处硬编码漂移。
#
#   2026-09-27 起改读 tools/构建全部.ps1 成功路径写出的 .3fc_kernel_build.json，
#   于是这一步能同时拦住三件事（以前一件都拦不住）：
#     ① 编出来的不是基线提交；② 内核工作树脏（未提交的改动混进包）；
#     ③ **构建之后磁盘上那份 DLL 又被换了**——今天真实发生过：Debug bin 里躺着
#        09-21 的旧内核，跑出来的判据全是假红；而只比"常量 == sha 文件"看不见这件事。
Write-Host "`n[1/11] 内核基线（产物内核 == 发布基线，且未被事后替换）" -ForegroundColor Yellow
$KernelBaselineSha = ""
$buildAll = Join-Path $PSScriptRoot "构建全部.ps1"
if (Test-Path $buildAll) {
    $m = [regex]::Match((Get-Content $buildAll -Raw -Encoding UTF8),
                        '\$KernelBaselineSha\s*=\s*"([0-9a-fA-F]{7,40})"')
    if ($m.Success) { $KernelBaselineSha = $m.Groups[1].Value }
}
$manifestPath = Join-Path $ProjectRoot ".3fc_kernel_build.json"
function Short([string]$s) { if ($s.Length -ge 12) { return $s.Substring(0, 12) } return $s }
if (-not $KernelBaselineSha) {
    Add-Result "内核基线" $false "无法从 tools/构建全部.ps1 提取 `$KernelBaselineSha"
} elseif (-not (Test-Path -LiteralPath $manifestPath)) {
    Add-Result "内核基线" $false "未找到 $manifestPath（请先运行 tools/构建全部.ps1；它只在成功路径写清单）"
} else {
    $mf = $null
    try { $mf = (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8) | ConvertFrom-Json }
    catch { Add-Result "内核基线" $false "清单解析失败：$($_.Exception.Message)" }
    if ($mf) {
        $mHead  = [string]$mf.head
        $mDirty = [bool]$mf.dirty
        $mDll   = [string]$mf.kernelDll
        $mHash  = [string]$mf.dllSha256
        $fails  = @()
        if ($mHead -notlike "$KernelBaselineSha*") {
            $fails += "产物内核 head $(Short $mHead) ≠ 基线 $(Short $KernelBaselineSha)"
        }
        if ($mDirty) {
            $n = @($mf.dirtyFiles).Count
            $fails += "内核工作树脏（$n 个文件未提交）：$(($mf.dirtyFiles | Select-Object -First 3) -join ' | ')"
        }
        if (-not $mHash) {
            $fails += "清单里 dllSha256 为空（构建时该路径不存在内核）"
        } elseif (-not (Test-Path -LiteralPath $mDll)) {
            $fails += "清单指向的内核文件已不在：$mDll"
        } else {
            # 用 Get-FileHash（本脚本别处也用它）；它给大写，清单里是小写 ⇒ 统一按小写比。
            $now = (Get-FileHash -Algorithm SHA256 -LiteralPath $mDll).Hash.ToLowerInvariant()
            if ($now -ne $mHash) {
                $fails += "磁盘内核与构建时不是同一份（现 $(Short $now) / 构建时 $(Short $mHash)）：$mDll"
            }
        }
        $tagNote = if ([string]$mf.kernelTag) { "tag=$($mf.kernelTag)" } else { "基线提交无归档 tag" }
        Add-Result "内核基线" ($fails.Count -eq 0) `
            ("head=$(Short $mHead) $tagNote cfg=$($mf.configuration) dll=$(Short $mHash)" +
             $(if ($fails.Count) { " ✗ " + ($fails -join '；') } else { "" }))
    }
}

# ── [2/11] 内核导出面：校验 **真正进包的那份内核**（≥82 导出 / API 15 / 25 个必需导出齐全）──
#   docs/41 P0-3：check_kernel_exports.py 原先**只打印、永远 exit 0**，且全仓无脚本调用
#   ⇒ 导出面被裁 / ABI 错位要到运行时才炸。现已加 --min-exports/--min-api 断言，在此接入。
#
#   ⚠ 判据用 --min-exports（导出数 **≥** 82）而不是旧的 --expect（**恰好** == 82）：
#     内核合法新增导出是正常演进，硬相等会让每次内核升级都假红（docs/41 §13.6）。
#     --expect 在脚本里保留、语义不变，既有调用不受影响。
#   ⚠ 新增：脚本内 REQUIRED 已扩到托管侧 P/Invoke 全量 25 个 —— 只数总数是拦不住
#     "删一个同时改名/新增一个"的（总数仍 82、旧 5 项全在，但运行时 EntryPointNotFoundException）。
#
#   ⚠ 校验对象是 **csproj 里 KernelDllPath 指向的 third_party 产物** —— 那才是进包的内核
#   （src/3FCompare/3FCompare.csproj 的 KernelDllPath 被同文件当作 EmbeddedResource 嵌进 exe）。
#   **不是** bin 下那份副本：bin\Release\net11.0-windows\FFF.Native.dll 只是 构建全部.ps1
#   的 Deploy-To 落盘的**开发运行副本**——dotnet build 不刷新它，NativeRuntime 也
#   "磁盘已有则绝不覆盖"（tools/verify-scripts/_review_pr/run_ab.ps1 还会把实验内核拷进去
#   并故意留着）。拿它当校验对象时，只要 exe 里嵌的是旧内核就照样判绿（假绿）；
#   反过来，上次成功构建是 Debug / 刚跑过 run_ab 时它又陈旧，会判红而 exe 其实是对的（假红）。
#   为了把"同一份内核"钉死，这里再**额外断言 bin 副本与进包内核 SHA256 相同**：
#   bin 副本不存在时只打印提示、不判红（Debug 构建确实不会有它）。
Write-Host "      · 内核导出面（--min-exports 82 --min-api 15，必需导出 25 个）" -ForegroundColor Yellow

# KernelDllPath 从 csproj 正则提取（不硬编码路径），再把 MSBuild 属性展开成本机绝对路径
$kernelDll = ""
if (Test-Path -LiteralPath $AppProject) {
    $km = [regex]::Match((Get-Content -Raw -LiteralPath $AppProject),
                         '<KernelDllPath>\s*([^<]+?)\s*</KernelDllPath>')
    if ($km.Success) {
        $raw = $km.Groups[1].Value.Trim()
        $raw = $raw.Replace('$(MSBuildThisFileDirectory)', (Split-Path $AppProject -Parent) + "\")
        $raw = $raw.Replace('$(KernelConfiguration)', $KernelConfiguration)
        $kernelDll = [IO.Path]::GetFullPath($raw)
    }
}

# python：Get-Command 只做存在性判断是不够的——若命中的是 Windows Store 的
# App Execution Alias 桩，Get-Command 有值，但执行时只打印 "Python was not found"
# 并返回 9009 ⇒ 门禁红且提示误导（看起来像"导出面坏了"）。
# 同名命令可能有多个候选（本机实测：workbuddy 自带 3.13 / WindowsApps 桩 / Python312，
# 且 PATH 顺序在多次调用间会变）。规则：优先取**不在 WindowsApps 下**的第一个候选
# ——门禁只需要"一个能跑的 python"，不该被 PATH 上的占位桩挡住真实解释器；
# 若候选全是 WindowsApps 下的（含从 Microsoft Store 正常安装的可用 Python），
# 仍取第一个并交给**实际执行**判定，绝不按路径硬判红。
# ⚠ 必须带 -All：`Get-Command python` 只返回**首个**匹配（同名 Application 只给一个），
#   下面的"跳过 WindowsApps 桩"循环就成了死代码 —— 实测 Windows PowerShell 5.1 下
#   PATH 里 WindowsApps 排在 Python312 之前，于是恒取到占位桩、执行返回 9009，
#   [2/11] 内核导出面在本机 5.1 恒红（且提示误导成"导出面坏了"）。
#   -All 才会列出全部候选，偏好规则才真正生效。
$pyCands = @(Get-Command python -All -ErrorAction SilentlyContinue)
$pyCmd = $null
foreach ($c in $pyCands) {
    $p = $c.Source
    if (-not $p) { $p = $c.Definition }
    if ($p -and ($p -notmatch '(?i)[\\/]WindowsApps[\\/]')) { $pyCmd = $c; break }
}
if (-not $pyCmd -and $pyCands.Count -gt 0) { $pyCmd = $pyCands[0] }
# 把**解析出的真实路径** $pyExe 传给 Invoke-Cmd，而不是字符串 "python" 让它按 PATH 二次解析
$pyExe = $null
if ($pyCmd) {
    $pyExe = $pyCmd.Source
    if (-not $pyExe) { $pyExe = $pyCmd.Definition }
}
$pyAliasHint = ""
if ($pyExe -and ($pyExe -match '(?i)[\\/]WindowsApps[\\/]')) {
    $pyAliasHint = "；⚠ python 解析到 Windows Store 应用执行别名（$pyExe）——若它只是未安装时的占位桩，执行会返回 9009，请安装真实 Python 3，或在「设置 → 应用 → 高级应用设置 → 应用执行别名」中关闭 python.exe"
}

$binKernel = Join-Path (Split-Path $Exe -Parent) "FFF.Native.dll"
# 结论里打印**实际校验的那条路径**（相对仓库根，便于对照）：若 csproj 的 KernelDllPath
# 被改成非默认位置，标签不会与事实不符。
$kernelRel = $kernelDll
if ($kernelRel -and $kernelRel.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $kernelRel = $kernelRel.Substring($ProjectRoot.Length).TrimStart('\')
}
if (-not $kernelDll) {
    Add-Result "内核导出面" $false "无法从 3FCompare.csproj 提取 KernelDllPath（期望 <KernelDllPath>…</KernelDllPath>）"
} elseif (-not (Test-Path -LiteralPath $kernelDll)) {
    Add-Result "内核导出面" $false "找不到进包内核 $kernelDll（请先运行 tools/构建全部.ps1 -Configuration $KernelConfiguration）"
} elseif (-not $pyCmd) {
    Add-Result "内核导出面" $false "未找到 python，无法校验内核导出面；请安装 Python 3 并加入 PATH 后重跑（本项 fail-closed，不静默跳过）"
} else {
    # ⚠ 必须走 Invoke-Cmd，不能裸调 `& python`：脚本级 $ErrorActionPreference='Stop' 下，
    #    原生命令往 stderr 写内容会被当作终止错误抛出（PowerShell 5.1 实测 RemoteException），
    #    ⇒ 门禁会在第 1 项直接崩掉，[3/11]~[10/11] 全部不跑。
    #    Invoke-Cmd 内部已临时降级 ErrorActionPreference、清空 $LASTEXITCODE，
    #    并把"进程压根没起来"归一成 exit=-1（不会静默判成通过）。
    #    ⚠ 传的是 $pyExe（= $pyCmd.Source，上面解析出的**真实路径**），不是字符串 "python"
    #      ——否则会按 PATH 二次解析，又可能落回 WindowsApps 占位桩（已修过的坑，勿回退）。
    $exp = Invoke-Cmd $pyExe @(
        (Join-Path $PSScriptRoot "check_kernel_exports.py"),
        $kernelDll, "--min-exports", "82", "--min-api", "15")
    $expExit = $exp.Exit
    # 取最后一条**非空**输出：stub/异常路径下末行常是空串，直接取末行会让结论里出现 "路径: ；"
    $expTail = ($exp.Out | Where-Object { $_ -and $_.Trim() -ne "" } | Select-Object -Last 1)
    if (-not $expTail) { $expTail = "无输出（exit=$expExit）" }
    if ($expExit -ne 0) { $exp.Out | Select-Object -Last 4 | ForEach-Object { Write-Host "      $_" } }

    # 额外断言：bin 下那份开发运行副本必须与进包内核同源（同 SHA256）。
    $binNote = "bin 副本不存在，跳过 SHA256 一致性断言（Debug 构建不会有它）"
    $binOk = $true
    if (Test-Path -LiteralPath $binKernel) {
        $srcHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $kernelDll).Hash
        $binHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $binKernel).Hash
        if ($srcHash -eq $binHash) {
            $binNote = "bin 副本 SHA256 一致（$($srcHash.Substring(0,12))）"
        } else {
            $binOk = $false
            $binNote = "bin 副本 SHA256 不一致（进包 $($srcHash.Substring(0,12)) / bin $($binHash.Substring(0,12))）——bin 里是旧内核或 run_ab 留下的实验内核，请重跑 tools/构建全部.ps1 -Configuration $KernelConfiguration"
        }
    }
    Add-Result "内核导出面" (($expExit -eq 0) -and $binOk) `
        (Format-FailDetail $expExit "${kernelRel}: $expTail；$binNote$pyAliasHint")
}

# ── [3/11] Release 编译：0 error，且非白名单告警必须为 0 ──
Write-Host "`n[3/11] Release 编译（0 error / 0 非白名单 warning）" -ForegroundColor Yellow
# -p:KernelConfiguration 显式传入：默认值即 csproj 的默认（Release），行为不变；
# 但若用 -KernelConfiguration Debug 覆盖，必须让"这里编出来的 exe 内嵌的内核"
# 与 [2/11] 校验的那份保持同一配置，否则又变成"校验的和进包的不是同一份"。
$build    = Invoke-Cmd $Dotnet @("build", $AppProject, "-c", "Release",
                                 "-p:KernelConfiguration=$KernelConfiguration", "--no-restore", "--nologo")
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

# ── [4/11] 单元测试全绿 ──
Write-Host "`n[4/11] 单元测试" -ForegroundColor Yellow
$test    = Invoke-Cmd $Dotnet @("test", $TestProject, "-c", "Release", "--no-restore", "--nologo")
$testOut = $test.Out
$testOk  = $test.Exit -eq 0
$summary = ($testOut | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
Add-Result "单元测试" $testOk (Format-FailDetail $test.Exit $summary)

# ── [5/11] Platform 层测试：抓屏路由 / 裁剪 / Win32 区域 / 本地化键完整性 ──
#   本工程是 tests/ 下唯一引用 UI 工程的测试（WindowRegionClipper 等只在 UI 工程里，
#   而 Core 是 net11.0 无 Windows TFM，Core.Tests 够不到）。此前全仓无脚本调用它 ⇒ 该层回归是空的。
#   docs/41 §六#4「本地化键完整性」**不需要再加独立门禁项**：本工程内的
#   LanguageManagerKeysTests.两张语言表键集合一致时返回空列表 已 `Assert.Empty(FindMissingKeys())`
#   （FindMissingKeys 双向比对 zh/en 两张表），随本项一起跑即已覆盖。
Write-Host "`n[5/11] Platform 测试（抓屏路由 / 裁剪 / Win32 区域 / 本地化键）" -ForegroundColor Yellow
$platTest    = Invoke-Cmd $Dotnet @("test", $PlatformTest, "-c", "Release", "--no-restore", "--nologo")
$platOut     = $platTest.Out
$platSummary = ($platOut | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
Add-Result "Platform 测试" ($platTest.Exit -eq 0) (Format-FailDetail $platTest.Exit $platSummary)

# ── [6/11] 控制台 E3 冒烟（tests/3FCompare.SmokeTests 是控制台程序，不是 xunit）──
#   它内部固定用 SimulatedEngine（合成画面），既不加载原生库也不解码文件，无 GUI 也能稳定跑；
#   但入口会 File.Exists 校验路径 ⇒ 必须传一个真实存在的路径，否则直接 exit=2。
#
#   ⚠ 本项 fail-closed（docs/45 P0-3）：素材目录 testmedia/media/real 未纳入版本控制，
#     旧版缺素材时**把 .csproj 当视频路径传进去**"避免恒红"——而 SmokeTests 恒用
#     SimulatedEngine、只做 File.Exists 校验、不读文件内容 ⇒ 该路径永不被打开，
#     这一项**永不失败**，对真实解码零判别力（假绿）。门禁的环境前提本来就是有素材
#     （[7-9/11] 同样依赖它），故缺素材时直接判红并说清该部署什么，不再回退工程文件。
#   素材列表在此统一解析：$realMedia 供本项、[7-9/11]、[11/11] 共用，避免多处 glob 漂移。
Write-Host "`n[6/11] 控制台 E3 冒烟（--no-restore，无需 GUI）" -ForegroundColor Yellow
$realMedia = @(Get-ChildItem (Join-Path $ProjectRoot "testmedia\media\real\*.mp4") -ErrorAction SilentlyContinue |
    Sort-Object Name | ForEach-Object { $_.FullName })
$smokeArg = if ($Media) { $Media } elseif ($realMedia.Count -gt 0) { $realMedia[0] } else { "" }
if (-not $smokeArg) {
    Add-Result "控制台 E3 冒烟" $false "缺真实素材，请先部署 testmedia/media/real/*.mp4（或用 -Media 显式指定）"
} else {
    $smoke     = Invoke-Cmd $Dotnet @("run", "--project", $SmokeProject, "-c", "Release", "--no-restore", "--", $smokeArg)
    $smokeOut  = $smoke.Out
    $smokeLine = ($smokeOut | Where-Object { $_ -match 'E3 冒烟通过|E3 冒烟失败' } | Select-Object -Last 1)
    Add-Result "控制台 E3 冒烟" ($smoke.Exit -eq 0) (Format-FailDetail $smoke.Exit "$smokeLine (exit=$($smoke.Exit))")
}

# ── 显式降级必须先落"失败"记录（docs/45 P1-11）──
# 旧实现里 -SkipSelfTest 会让整个块不执行 ⇒ 汇总中根本没有这三项，门禁照样打印
# "全部通过"，等于**静默降级**。这里与 [11/11] 的口径一致：跳过 = 判红 + 汇总单列。
if ($SkipSelfTest) {
    Add-Result "单路全量" $false "显式降级（-SkipSelfTest）：实机自测未执行，本次结果不得作为发布依据"
    Add-Result "会话往返" $false "显式降级（-SkipSelfTest）：实机自测未执行"
    Add-Result "帧导出"   $false "显式降级（-SkipSelfTest）：实机自测未执行"
}
if (-not $SkipSelfTest) {
    if (-not (Test-Path $Exe)) {
        Add-Result "单路全量" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
        Add-Result "会话往返" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
        Add-Result "帧导出" $false "找不到 $Exe（请先运行 tools/构建全部.ps1 -Configuration Release）"
    } else {
    # 素材列表（与 [6/11] 同一份 $realMedia，取前两个）：[7/11] 用第 1 个，
    # [8/11] 需要 2 个（第二个存在才跑"文件拖入"分支）。
    $mediaList = if ($Media) { @($Media) } else { @($realMedia | Select-Object -First 2) }
    # 用 -join 而不是 Join-String：后者是 pwsh7 专有，本脚本可能被 Windows PowerShell 5.1 调用
    $mediaNames = ($mediaList | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ' + '
    Write-Host "  素材: $mediaNames" -ForegroundColor Gray

    # ── [7/11] 单路全量（覆盖面最广：布局/探针/倍速/消息注入/最大化……）──
    # docs/14 §4.3：它此前**从不被任何脚本调用**，等于这条最宽的回归网一直是空的。
    Write-Host "`n[7/11] 单路全量回归（--selftest）" -ForegroundColor Yellow
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

    # ── [7/11] 追加一行：多路对比回归（--comparemodetest，5 路档）──
    #   为什么挂在本步而不是新起一个编号：文件头的编号约定是"分母 = 11 个带号步骤"，
    #   加一行 Add-Result 不改变步骤数，也就不动 [n/11] 的既有引用（docs 里到处指它）。
    #   此前这一步从不被门禁调用 ⇒ 16 个隔离单元（模式收敛/格表驱动布局/裁剪接线/
    #   分割线命中/路启用态/视图模式列……）整批在门禁之外，"门禁绿"不代表对比模式没坏。
    #   5 路是**故意**选的：≤4 路时"ABCD 只显示前 4 路""多余路停用"这类判据没有对象，
    #   会登记成 SKIP —— 所以下面除了 exit，还额外要求"跳过数为 0"。
    Write-Host "      · 多路对比回归（--comparemodetest 5 路）" -ForegroundColor Yellow
    if ($mediaList.Count -lt 1) {
        Add-Result "对比模式回归" $false "需要至少 1 个真实素材"
    } else {
        $cmRun  = Invoke-Cmd $Exe @("--comparemodetest", $mediaList[0], "5")
        $cmCode = $cmRun.Exit
        $cmSum  = ($cmRun.Out | Where-Object { $_ -match '验收点汇总' } | Select-Object -Last 1)
        $cmSkip = -1
        if ($cmSum -match '跳过\s*(\d+)') { $cmSkip = [int]$Matches[1] }
        $cmOk = ($cmCode -eq 0) -and ($cmSkip -eq 0)
        $cmDetail = if ($cmSkip -lt 0) { "汇总行没找到（判红：无法确认跑了什么）" }
                    elseif ($cmSkip -gt 0) { "有 $cmSkip 项记为 SKIP（没判到东西，不算验过）" }
                    else { "0 项跳过" }
        Add-Result "对比模式回归" $cmOk (Format-FailDetail $cmCode "$cmDetail (exit=$cmCode)")
    }

    # ── [8/11] 会话往返（拦 P0-1 / P0-3）──
    Write-Host "`n[8/11] 会话存取往返回归（--sessiontest）" -ForegroundColor Yellow
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

    # ── [9/11] 抓帧导出（拦 P0-2）──
    Write-Host "`n[9/11] 帧导出回归（--screentest）" -ForegroundColor Yellow
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
    Write-Host "`n[7-9/11] 实机自测已按 -SkipSelfTest 跳过" -ForegroundColor Yellow
}

# ── [10/11] 打包 + 产物自检（可选）──
if ($WithPack) {
    Write-Host "`n[10/11] 打包与产物自检（pack.ps1 内含 Assert-Package）" -ForegroundColor Yellow
    $packArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $ProjectRoot "pack.ps1"), "-Version", $Version, "-Mode", "all")
    # 操作者已声明"跑不了 GUI 自测"时透传给 pack，免得它在打包中途再抛一次（pack 自身会显著标注）；
    # 门禁侧由 [11/11] 记成显式降级并判红 —— 透传不会把"未验证"变成"通过"。
    if ($SkipPackageSelfTest) { $packArgs += "-SkipPackageSelfTest" }
    $pack = Invoke-Cmd "powershell" $packArgs
    Add-Result "打包与产物自检" ($pack.Exit -eq 0) (Format-FailDetail $pack.Exit "pack.ps1 exit=$($pack.Exit)")
} else {
    Write-Host "`n[10/11] 打包已跳过（加 -WithPack 启用）" -ForegroundColor Yellow
}

# ── [11/11] AOT 包内自测（docs/45 P0-2：不可跳过）──
#   [2/11]~[9/11] 自测的是 bin\Release 的 **IL/JIT** 产物（74KB apphost + deps.json + runtimeconfig.json），
#   而发货物是 NativeAOT 单文件（~33MB）：AOT 裁剪掉反射依赖、Marshal 封送差异这类缺陷
#   在门禁里**结构性不可见**。唯一能覆盖它的位置，就是拿 publish 出来的那个 exe 真跑一次
#   --selftest —— 本项把 pack.ps1 的包内自测（pack.ps1:356-377）收编进门禁并设为不可跳过：
#   门禁不再需要 -WithPack 才覆盖 AOT，且唯一出口 -SkipPackageSelfTest 是**显式降级**
#   （判红 + 汇总单列 + 退出码非 0），不是"跳过不判"。
#
#   ⚠ 必须在**副本**里跑：AOT exe 一启动就会在自己身边生成 logs\（含本机绝对路径），
#     并解压 FFF.Native.dll / 3FC.WgcCapture.dll / libSkiaSharp.dll / libHarfBuzzSharp.dll
#     —— 那会让 publish\build 下的待发目录不再等于 SHA256SUMS 描述的发货内容
#     （pack.ps1 为此专门写了 Remove-ExtraneousArtifacts 收敛）。门禁只做只读验证，
#     故整目录复制到 testmedia\tmp 下再跑，跑完即删，绝不污染待发目录。
Write-Host "`n[11/11] AOT 包内自测（publish 出的单文件 exe，不可跳过）" -ForegroundColor Yellow
# 产物目录命名与 pack.ps1 的 Get-PackageName 一致：app = 3FCompare-v<版本>-x64，full = ...-x64-full。
# 两个都在时优先验完整版（含 FFmpeg，覆盖面更大）——只验一个即可：两者是同参数的两次 publish，
# exe 同源；打包时若任一次 publish 失败，pack.ps1 早已中止、这里也就找不到目录。
$aotDirs = @(
    (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64-full"),
    (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64")
)
$aotDir = $aotDirs | Where-Object { Test-Path -LiteralPath (Join-Path $_ "3FCompare.exe") } | Select-Object -First 1
$aotMedia = if ($Media) { $Media } elseif ($realMedia.Count -gt 0) { $realMedia[0] } else { "" }
if ($SkipPackageSelfTest) {
    Add-Result "AOT 包内自测" $false "显式降级（-SkipPackageSelfTest）：AOT 产物未经 --selftest 验证，本次结果不得作为发布依据"
} elseif (-not $aotDir) {
    Add-Result "AOT 包内自测" $false "找不到 AOT 产物 $($aotDirs[1])\3FCompare.exe（未跑 -WithPack / 未执行 pack.ps1）；AOT 裁剪与反射类缺陷只有本项能覆盖，请先运行 pack.ps1 -Mode app|full"
} elseif (-not $aotMedia) {
    Add-Result "AOT 包内自测" $false "缺真实素材，请先部署 testmedia/media/real/*.mp4（或用 -Media 显式指定）"
} else {
    $aotExe  = Join-Path $aotDir "3FCompare.exe"
    $aotCopy = Join-Path $TmpDir "aot_selftest"
    $aotOk   = $true
    $aotDetail = ""
    $sizeMB  = [math]::Round((Get-Item -LiteralPath $aotExe).Length / 1MB, 1)
    Write-Host "  产物: $aotDir（exe ${sizeMB}MB）" -ForegroundColor Gray
    Write-Host "  素材: $([IO.Path]::GetFileName($aotMedia))" -ForegroundColor Gray
    Write-Host "  ⚠ 会弹出程序窗口，请勿操作" -ForegroundColor DarkYellow
    try {
        if (Test-Path -LiteralPath $aotCopy) { Remove-Item -LiteralPath $aotCopy -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $aotCopy | Out-Null
        # 逐项复制（不用 "dir\*" 通配）：PS 5.1 下 -Path 带通配 + -Recurse 的落点有歧义，
        # 显式枚举子项再 Copy-Item 语义确定。
        foreach ($item in @(Get-ChildItem -LiteralPath $aotDir -Force)) {
            Copy-Item -LiteralPath $item.FullName -Destination $aotCopy -Recurse -Force
        }
        $r    = Invoke-Cmd (Join-Path $aotCopy "3FCompare.exe") @("--selftest", $aotMedia)
        $tail = ($r.Out | Where-Object { $_ -match 'selftest\[|全部通过' } | Select-Object -Last 1)
        if ($r.Exit -ne 0) {
            $aotOk = $false
            $r.Out | Select-Object -Last 15 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
            $aotDetail = Format-FailDetail $r.Exit "$tail (exit=$($r.Exit))"
        } else {
            $aotDetail = "AOT 单文件 exe（${sizeMB}MB）--selftest 通过 (exit=0) $tail"
        }
    } catch {
        # 复制/清理失败也要落到 Add-Result 上：直接抛出会让门禁跳过汇总，只剩一个 exit=1
        $aotOk = $false
        $aotDetail = "AOT 自测执行失败：$($_.Exception.Message)"
    } finally {
        # 副本一律清掉：里面还留着 exe 运行时解压出的 DLL 与本机日志
        if (Test-Path -LiteralPath $aotCopy) { Remove-Item -LiteralPath $aotCopy -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Add-Result "AOT 包内自测" $aotOk $aotDetail
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
# 显式降级单独列出：这类项虽然同样判红（exit≠0），但性质是"没验"而不是"验失败"，
# 混在普通失败里很容易被当成偶发问题重跑掉（docs/45 P0-2：AOT 只有 [11/11] 能覆盖）。
if ($SkipPackageSelfTest) {
    Write-Host "  ⚠ 显式降级：-SkipPackageSelfTest —— AOT 包内自测未执行，本次结果不得作为发布依据" -ForegroundColor Yellow
    Write-Log "DEGRADED  -SkipPackageSelfTest：AOT 包内自测未执行，本次结果不得作为发布依据"
}
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
