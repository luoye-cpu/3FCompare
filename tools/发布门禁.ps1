# 3FCompare 发布门禁（P1-14）
# 用法: powershell -ExecutionPolicy Bypass -File tools/发布门禁.ps1 [-Version 0.2.0]
#       [-WithPack] [-SkipSelfTest] [-SkipPackageSelfTest] [-StrictGit]
#       [-SkipProbeSetGate] [-ProbeGateFull]
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
# 另有六条**不占编号的独立门禁项**（各自 Add-Result、各自决定退出码，只是不给新号，理由写在各处注释里）：
#   `[许可卫生]` 第三方清单与自述（2026-10-15 IP 普查；清单缺席/空壳/未登记实测 nonfree 构建串 ⇒ 判红）
#   `[2/11]` 追加 · 本体中性（docs/93 T3；判据 tools/check_kernel_neutrality.py）
#   `[2/11]` 追加 · 着色器 cbuffer 布局（docs/93 T1e；判据 tools/check_shader_cbuffer_layout.py）
#   `[2/11]` 追加 · 探针集扫编（J3；判据 tools/check_probe_set.py + tools/probe_baseline.txt）
#   `[2/11]` 追加 · 内核配置 ABI（J5；判据 tools/check_kernel_config_abi.py）
#   `[2/11]` 追加 · 输出档位阶梯一致性（2026-10-19 接线；判据 tools/check_output_tier_ladder_parity.py，
#            该判据 10-18 就写好了但零调用者 —— 本次把它接进门禁，并跑变异臂自证有牙）
#   `[3/11]` 追加 · 内嵌内核一致性（J4；验"嵌进产物的那份内核"是不是 csproj 指向的那一份）
#   `[7/11]` 追加 · 对比模式回归（--comparemodetest 5 路）
#   （条数说明：文件头此前写"另有三条"，J3 落地时按 +1 改成"四条"；J4 同批又落一条，
#     故此处如实记 5 条；J5 又落一条 ⇒ 6 条。带号步骤仍是 1..11，分母没动。）
# Step-numbering convention (bilingual): denominator = 11 actual numbered steps, flat 1..11.
#   Six extra independent gate lines carry no step number (added under [2/11] and [3/11] and [7/11] on
#   purpose, so the 70+ `[n/11]` citations in docs/ and CHANGELOG keep resolving to the same steps).

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
    [switch]$SkipProbeSetGate,
                            # 显式降级：跳过 [2/11] 追加行「探针集扫编」（J3）。
                            # 与 -SkipSelfTest / -SkipPackageSelfTest 同一口径：**跳过=判红并单列**，
                            # 不是"跳过不判"。全量扫编 82 个探针要几分钟，首次落地或赶时间时用；
                            # 平时不必跳——默认走增量模式（只重编源码/输入指纹变了的探针）。
    [switch]$ProbeGateFull, # 「探针集扫编」忽略缓存做全量重扫（第一次建缓存、或怀疑缓存失真时用）
    [switch]$NoConfigAbi,
                            # 显式降级：跳过 [2/11] 追加行「内核配置 ABI」（J5，2026-10-14）。
                            # 与 -SkipProbeSetGate / -SkipSelfTest 同一口径：**跳过=判红并单列**，
                            # 不是"跳过不判"。这条判据要真起一次会话（跑 FFF3FP_Create），
                            # 无 GPU/无显示环境跑不了；那种环境下用这个开关，代价是本项判红、
                            # 退出码非 0，**不得**把当轮结果当作发布依据。
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
    # ⚠ 输出必须**管道流式**收，不能写成 `$raw = & $FilePath @Arguments 2>&1`。
    # 本仓要量的 3FCompare.exe 是 GUI 子系统程序（PE Subsystem=2，实测三个 bin 都是 2）。
    # PowerShell 对"赋值给变量"的 GUI 子进程**不等退出**：实测同一份包内 exe 跑
    # `--selftest C:\nope.mp4`，赋值式 433ms 返回 / 0 行 / $LASTEXITCODE 为空，
    # 而管道式 2538ms 返回 / 7 行 / exit=2 —— 差的那 2.1 秒正是程序真正跑完的时间。
    # 后果不是"少几行日志"而是整条判据失配：exit 为空被兜成 -1，详情写"命令未能启动"，
    # 而门禁里所有 *-test 步骤（selftest / comparemodetest / sessiontest / multitest）都走这里。
    $lines = New-Object System.Collections.Generic.List[string]
    & $FilePath @Arguments 2>&1 | ForEach-Object { $lines.Add($_.ToString()) }
    # 某些受限环境（如自动化沙箱）根本起不了进程：此时 $LASTEXITCODE 仍为 null。
    # 不显式兜底的话，门禁只会显示"exit="空值，看不出到底是测试失败还是命令没跑起来。
    $code = if ($null -eq $LASTEXITCODE) { -1 } else { $LASTEXITCODE }
    $ErrorActionPreference = $prev
    return [pscustomobject]@{
        Exit = $code
        Out  = @($lines)
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

# ── RE-10（docs/67 第七节 / laneR-E 判词）：内核工作树**实测**，不信清单自述 ──────────
#   `.3fc_kernel_build.json` 里的 dirty/dirtyFiles 是 tools/构建全部.ps1 在**构建那一刻**
#   写下的自我陈述；写完之后工作树照样会被改。本轮就撞上了这个形状：清单写
#   `dirty:false` / `dirtyFiles:[]`，而 `git -C third_party/fff_project status --porcelain`
#   当场实测 **6 个未提交文件**（本轮 P0 修复尚未提交）⇒ [1/11] 的子判据（head==基线 /
#   配置一致 / DLL SHA256 相符）逐条**都成立且都该绿**，可它们判的是不含这些改动的基线
#   DLL。"自述没撒谎"与"结论有效"是两回事：自述只能证明构建那会儿干净，不能证明现在还干净。
#   故本函数不看清单，直接对 kernelDll 所属的那棵 git 树跑一次实时 status。
#
#   ⚠ **不带路径参数**：本仓有中文文件名，`git status --porcelain -- <中文路径>` 实测返回
#     **空**（同一文件不带 `--` 时能列出 ` M docs/10-UI结构审查….zh.md`）⇒ 按路径过滤 = 静默漏判。
#     内核树（third_party/fff_project）内的文件名当前全是 ASCII，但判据不能依赖这件事。
#   ⚠ **fail-closed**：git 不可用 / 路径解析不了 / 不在任何 git 树内 / status 非 0
#     ⇒ 一律返回 Error 让调用方判红，绝不退回"那就只信清单"。
#   ⚠ 边界（不是本函数能覆盖的）：构建产物目录在内核树里是 git-ignored，所以**换掉 DLL 字节
#     不会**让这里变红——那一半由下面的 DLL SHA256 比对兜着，两条合起来才闭合。
function Get-LiveKernelWorktree {
    param(
        [string]$GitExe,
        [string]$DllPath,
        [string]$Root
    )
    $state = [pscustomobject]@{ Ok = $false; Tree = ''; Dirty = @(); Error = '' }
    if (-not $GitExe) { $state.Error = '未找到 git，无法实测内核工作树（本项 fail-closed，不改回只信清单自述）'; return $state }
    if (-not $DllPath) { $state.Error = '清单未给出 kernelDll，无法定位它所属的工作树'; return $state }
    $target = $DllPath
    try {
        if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $Root $target }
        $target = [IO.Path]::GetFullPath($target)
    } catch {
        $state.Error = "kernelDll 路径解析失败：$($_.Exception.Message)"
        return $state
    }
    # DLL 本身可能已被改名/删除（那也属于"清单与盘上不符"，红由调用方的存在性判据给）；
    # 这里从其父目录起步找树，保证那种情况下实测仍然能跑。
    $start = if (Test-Path -LiteralPath $target -PathType Container) { $target } else { Split-Path -Parent $target }
    if (-not (Test-Path -LiteralPath $start)) { $state.Error = "kernelDll 及其父目录都不在盘上：$start"; return $state }

    $tp = Invoke-Cmd $GitExe @('-C', $start, 'rev-parse', '--show-toplevel')
    $treeLine = ($tp.Out | Where-Object { $_ -and $_.Trim() -ne '' } | Select-Object -First 1)
    if ($tp.Exit -ne 0 -or -not $treeLine) {
        $state.Error = "kernelDll 不在任何 git 工作树内（从 $start 起 rev-parse --show-toplevel exit=$($tp.Exit)：$treeLine）"
        return $state
    }
    $state.Tree = $treeLine.Trim()

    $st = Invoke-Cmd $GitExe @('-C', $state.Tree, 'status', '--porcelain')
    if ($st.Exit -ne 0) {
        $sTail = ($st.Out | Where-Object { $_ -and $_.Trim() -ne '' } | Select-Object -First 1)
        $state.Error = "git status --porcelain 在 $($state.Tree) 执行失败（exit=$($st.Exit)）：$sTail"
        return $state
    }
    $state.Dirty = @($st.Out | Where-Object { $_ -and $_.Trim() -ne '' })
    $state.Ok = $true
    return $state
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
#
#   2026-10-08 RE-10（docs/67 第七节）：② 原先读的是清单里的**自述** `[bool]$mf.dirty`——
#   清单只描述写它的那一刻，之后工作树再被改就查不到。本轮实测过这个假绿：清单
#   `dirty:false / dirtyFiles:[]`、`head==baseline==d2f5826`、DLL SHA256 逐字符相符 ⇒
#   [1/11] 判 ✅，而同刻 `git -C third_party/fff_project status --porcelain` 有 **6 个未提交
#   文件**（本轮 P0 修复）。现在 ② 改为对 kernelDll 所属那棵树跑实时 status（见
#   Get-LiveKernelWorktree），**脏文件清单原样打进结论行**；清单自述仍然打印并且仍然能
#   判红（自述脏 = 那次构建本身脏），但它不再充当"工作树干净"的证据。
Write-Host "`n[1/11] 内核基线（产物内核 == 发布基线、未被事后替换、**工作树实测**干净）" -ForegroundColor Yellow
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
        # 自述（保留、但只作为"那次构建"的陈述）：清单说脏 ⇒ 构建那会儿就脏，照旧判红。
        $mfDirtyN = @($mf.dirtyFiles).Count
        if ($mDirty) {
            $fails += "清单自述构建时内核工作树脏（$mfDirtyN 个文件未提交）：$(($mf.dirtyFiles | Select-Object -First 3) -join ' | ')"
        }
        # 实测（RE-10：这一条才是"工作树干净"的判据）。两侧计数都打出来，
        # 这样"清单说 0 / 盘上 6"这种自述失效当场可见，而不是只报一句"不一致"。
        $live     = Get-LiveKernelWorktree -GitExe $Git -DllPath $mDll -Root $ProjectRoot
        $liveDirty = @($live.Dirty)
        if (-not $live.Ok) {
            $fails += "内核工作树无法实测：$($live.Error)——清单自述 dirty=$mDirty（列 $mfDirtyN 个）不构成放行依据"
        } elseif ($liveDirty.Count -gt 0) {
            $shown = ($liveDirty | Select-Object -First 12) -join ' | '
            if ($liveDirty.Count -gt 12) { $shown += " （另有 $($liveDirty.Count - 12) 个未列出，共 $($liveDirty.Count) 个）" }
            $fails += "内核工作树实测脏：实测 $($liveDirty.Count) 个未提交 / 清单自述 dirty=$mDirty 列 $mfDirtyN 个 ⇒ $shown"
        }
        # 清单必须描述**本次要校验的那个配置**的内核。2026-09-30 实测：Release 一轮里
        # [1/11] 打着 `cfg=Debug dll=50C86906…` 仍判 ✅，而 [2/11] 校验的是 x64\Release 那份
        # ⇒ 基线项的溯源陈述与被验对象无关，等于白说。配置不一致就直接红，不靠人记得先跑哪个构建。
        if ([string]$mf.configuration -ne $KernelConfiguration) {
            $fails += "清单配置 $($mf.configuration) ≠ 本次 -KernelConfiguration $KernelConfiguration（先跑 tools/构建全部.ps1 -Configuration $KernelConfiguration）"
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
        # 结论行带上"实测的是哪棵树、实测几个、清单自述几个"：换成别的内核树、或清单与盘上
        # 再次分叉时，读数本身就能自证，不需要再去手工跑 git status 对账。
        $treeRel = $live.Tree
        if ($treeRel) {
            $treeRel = $treeRel.Replace('/', '\')
            if ($treeRel.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $treeRel = $treeRel.Substring($ProjectRoot.Length).TrimStart('\')
            }
        } else { $treeRel = '未解析出' }
        Add-Result "内核基线" ($fails.Count -eq 0) `
            (("head=$(Short $mHead) $tagNote cfg=$($mf.configuration) dll=$(Short $mHash)" +
              " tree=$treeRel 实测脏=$($liveDirty.Count)/自述 dirty=$mDirty($mfDirtyN)") +
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
Write-Host "      · 内核导出面（--min-exports 82 --min-api 15 --known-missing-tree auto：必需导出=REQUIRED 全量，出货树分叉按声明表**对账**而非放宽；条数由脚本自己打印）" -ForegroundColor Yellow

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

# bin 副本要比的是"这份内核部署到它自己那套应用配置里之后有没有被换过"。$Exe 固定指 Release
# （门禁跑的是 Release 产物），但 -KernelConfiguration Debug 时 tools/构建全部.ps1 部署的是
# bin\Debug ⇒ 沿用 $Exe 所在目录会拿 Debug 内核去比 Release 副本，恒判红（2026-09-30 实测：
# 进包 50C86906B847 / bin E9A18DDF492A）。按内核配置取对应 bin，两条路径都仍然可比。
$binFlavor  = if ($KernelConfiguration -eq 'Debug') { 'Debug' } else { 'Release' }
$binKernel = Join-Path $ProjectRoot "src\3FCompare\bin\$binFlavor\net11.0-windows\FFF.Native.dll"
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
        $kernelDll, "--min-exports", "82", "--min-api", "15",
        # J2（2026-10-14）：出货树 TP 与内核树 KF 本来就分叉（VSR 两条入口只有 KF 有实现，
        # TP 导出 94 / KF 导出 104），本步因此**恒红**——而恒红的门禁只教会读日志的人忽略红。
        # 现在按"对账"而不是"放宽"接：判据里 KNOWN_MISSING_BY_TREE 写死了"哪棵树、缺哪几条、
        # 为什么"，实测缺失集合必须与声明集合**完全相等**才放行：
        #   多缺一条 ⇒ 红（真回归）；声明里多挂着一条 ⇒ 红（提示删条目）；
        #   传 auto 但按路径反查不到已声明的树 ⇒ 不豁免，仍按严格 35 条判（fail-closed）。
        # ⚠ 判据**不带这个参数时行为不变**（严格 35 条），所以主仓那条链路的严格度没被稀释——
        #   稀释只在"这一棵树、这几条符号、写明理由"的范围里发生。
        "--known-missing-tree", "auto")
    $expExit = $exp.Exit
    # 取最后一条**非空**输出：stub/异常路径下末行常是空串，直接取末行会让结论里出现 "路径: ；"
    $expTail = ($exp.Out | Where-Object { $_ -and $_.Trim() -ne "" } | Select-Object -Last 1)
    if (-not $expTail) { $expTail = "无输出（exit=$expExit）" }
    if ($expExit -ne 0) { $exp.Out | Select-Object -Last 4 | ForEach-Object { Write-Host "      $_" } }
    # 豁免对账读数（J2）：判据启用豁免时会打一行"豁免对账·树=… 声明 n / 实测缺 m"，
    # 门禁把它原样搬进结论——红/绿都必须看得见"是按哪张声明表对的账"，不许只留一个 exit 码。
    $expExempt = ($exp.Out | Where-Object { $_ -match '豁免对账·' } | Select-Object -Last 1)
    $expExemptNote = if ($expExempt) { "；$($expExempt.Trim())" } else { "" }

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
    # 配对守卫：进包内核必须满足托管侧的**实际**要求 —— API 版本恰好相等 + 25 个必需入口齐。
    # 上面的 --min-api 15 只是下限，"托管要 16 / 进包是 15"这种**错配**它是放行的；
    # 而错配的真实后果就是 2026-09-29 撞到的那个形状：构建全绿 → 会话创建被内核拒 →
    # 整轮 --selftest 在演示模式空跑（真机判据全 SKIP）而退出码仍为 0。
    $pair     = Invoke-Cmd $pyExe @((Join-Path $PSScriptRoot "check_kernel_pairing.py"), $kernelDll)
    $pairExit = $pair.Exit
    $pairTail = ($pair.Out | Where-Object { $_ -and $_.Trim() -ne "" } | Select-Object -Last 1)
    if (-not $pairTail) { $pairTail = "配对检查无输出（exit=$pairExit）——守卫没跑等于没守卫" }
    if ($pairExit -ne 0) { $pair.Out | Select-Object -Last 4 | ForEach-Object { Write-Host "      $_" } }
    # ── J2 的残余红源对账（2026-10-14）──────────────────────────────────────────
    # check_kernel_pairing.py 自己也要 kx.REQUIRED 全量（tools/check_kernel_pairing.py:740 那句
    # `missing = [e for e in kx.REQUIRED if e not in names]`），所以出货树那 2 条 VSR 分叉
    # **同时**让本步的两条臂判红：只给导出面接上声明表，`[2/11]` 仍然恒红，等于没修好。
    # 而 pairing 属禁改文件 ⇒ 对账落在门禁侧（**待定夺**：更干净的做法是让 pairing 自己读同一张表）。
    # 放行条件三条同时成立，缺一不放（这不是"缺了就警告"，是**逐名相等**的对账）：
    #   ① 导出面那条臂按声明表对完账且判绿（$expExit -eq 0）；
    #   ② 配对守卫实测的 missing 集合与声明集合**逐名相等**（多一条、少一条、换成别的符号都不放）；
    #   ③ 配对守卫的 FAIL 行**只有一条**、且就是"required entry point(s) absent"那一条
    #      ⇒ API 版本错配 / dest ABI / 版本常量矩阵任何一条其它红都照旧判红。
    $pairDeclared = @($exp.Out | Where-Object { $_ -match '声明且实测缺失[：:]' } |
        ForEach-Object { ("" + $_).Trim() -replace '^.*声明且实测缺失[：:]\s*', '' })
    $pairMissingRaw = ($pair.Out | Where-Object { $_ -match '^\s*missing\s*[：:]' } | Select-Object -Last 1)
    $pairMissing = @()
    if ($pairMissingRaw) {
        $pairMissing = @((("" + $pairMissingRaw) -replace '^\s*missing\s*[：:]\s*', '' -split ',') |
            ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    $pairSameAsDeclared = $false
    if ($pairMissing.Count -gt 0 -and $pairDeclared.Count -gt 0) {
        $pairDiff = @(Compare-Object ($pairMissing | Sort-Object) ($pairDeclared | Sort-Object))
        $pairSameAsDeclared = ($pairDiff.Count -eq 0)
    }
    $pairFailLines = @($pair.Out | Where-Object { $_ -match '^\s*FAIL' })
    $pairOk = ($pairExit -eq 0)
    $pairExemptNote = ""
    if ((-not $pairOk) -and $expExit -eq 0 -and $pairSameAsDeclared -and
            $pairFailLines.Count -eq 1 -and ($pairFailLines[0] -match 'required entry point')) {
        $pairOk = $true
        $pairExemptNote = "；配对臂按**同一张声明表**放行：它唯一的红就是这 $($pairMissing.Count) 条分叉、且与声明集合逐名相等（$(($pairMissing | Sort-Object) -join ',')）。不是跳过——其它任何一条红、或集合多/少一条都照旧判红；pairing 自身未接表属待定夺"
    } elseif (-not $pairOk -and $pairSameAsDeclared) {
        $pairExemptNote = "；配对臂的红与声明表同源，但不满足放行三条件（FAIL 行 $($pairFailLines.Count) 条 / 导出面 exit=$expExit）⇒ 仍判红"
    } elseif (-not $pairOk -and $pairMissing.Count -gt 0) {
        $pairExemptNote = "；配对臂报的是**另外的**缺失（实测 missing $($pairMissing.Count) 条 vs 声明 $($pairDeclared.Count) 条，集合不同⇒新红要查）：$(($pairMissing | Sort-Object) -join ',')"
    }
    # 详情里的退出码取"坏的那个"：只报 expExit 会在"导出面 OK、配对红"时打出一个误导性的 0。
    $worstExit = if ($expExit -ne 0) { $expExit } elseif (-not $pairOk) { $pairExit } else { 0 }
    Add-Result "内核导出面" (($expExit -eq 0) -and $pairOk -and $binOk) `
        (Format-FailDetail $worstExit "${kernelRel}: $expTail；$binNote；配对: $pairTail$expExemptNote$pairExemptNote$pyAliasHint")
}

# ── [2/11] 追加一行：本体中性（判据 tools/check_kernel_neutrality.py；口径与分层见 3fp/docs/93）──
#   "要授权才写得出来/用得了的实现"（Dolby Vision / FDK-AAC / MPEG-H / AC-4 / DTS:X / AACS…）不得在**本体**里；
#   免版税标准的实现（libiamf/OAR/OBR/pffft/absl/opus/FLAC）留在本体、能力不降级。分层写死在判据脚本头部，
#   本步不重述也不改动那个分层，只负责"每次发布都真跑一遍、把点名读数原样落进日志"。
#   docs/93 T3.3 的原话是"接入时机：与 T1/T2 同批，早接会恒红"——T1 已完成（二进制 0 命中、源码层 0 命中），
#   所以现在接的不是恒红灯；三层判据现在都必须为 0 命中（工程层自 docs/94 并入硬闸，见下面第 ⑤ 点）。
#
#   为什么挂在本步而不新起编号：与 [7/11] 的「对比模式回归」同理——文件头的约定是"分母 = 实际带号步骤数"，
#   加一行独立 Add-Result 不改变带号步骤数，也就不动 [n/11] 的既有引用（docs/ 与 CHANGELOG 里 70+ 处按
#   [n/11] 指它）。它照样独立进汇总、独立决定退出码。
#
#   对象 = [2/11] 已解析出的那份 $kernelDll（csproj 的 KernelDllPath = 真正进包、并被当 EmbeddedResource
#   嵌进 exe 的内核）。--project / --sources **从这份 DLL 的位置往上反查**它所属的那棵树：找含
#   FFF.Native.vcxproj 的目录，源码层扫它的同级 3FP/3FR。不硬编码 third_party\fff_project——内核换检出目录
#   或换配置（Debug）时判据跟着走，否则又退回"校验的内核与进包的内核不是同一份"那个老形状（见 [1/11] 注释）。
#
#   ⚠ **三层都是硬闸**（docs/94 起）：二进制层、源码层、工程层的判红命中数都必须为 0。
#     工程层此前唯一的红是 nonfree 的 `fdk-aac.lib`（`FFF.Native.vcxproj` 两行），当时按
#     "docs/93 T2 待摘、且前提未实测"显式豁免为**只读数不拦**——那盏灯当时确实是恒红的。
#     T2 已实测并落地：`iamf.lib` 对 `aacDecoder_*` 的 7 条未定义引用是靠 `dumpbin /symbols` 量出来的，
#     换成 FFmpeg 原生 AAC 后端后该计数为 0，两棵树的 vcxproj 都不再引用 `fdk-aac.lib`，
#     两棵树干净重建 `MSBUILD_RC=0`、导出面不变、DLL 各小 783,728 / 783,360 字节。
#     ⇒ 豁免的对象消失了，所以本步把它并入硬闸；**免版税依赖（libiamf/OAR/OBR/pffft/absl/opus/FLAC）
#     仍不判红**（判据 `BINARY_ALLOWED` / `PROJECT_ALLOWED_NOTED` 写死），能力不降级。
#
#   ⚠ 2026-10-14 补第四条（J1）：**反向存在性判据**——`BINARY_ALLOWED` 此前只有定义、
#     0 个使用者（docs/94 §未修 第 9 条），于是这三层只证明"不该有的不在"，从没证明
#     "该有的还在"。历史事故形状就是某配置缺 `FFF_WITH_LIBIAMF` ⇒ 出货版完全没有 IAMF，
#     而验证跑在另一配置上所以全绿。判据现在要求 8 项**逐项在工程层
#     `AdditionalDependencies` 里被证实链入**（实测 `*.lib` 这类链接期文件名不出现在链好的
#     DLL 字节里：iamf.lib/oar.lib/obr.lib/obr_capi.lib/pffft.lib 在进包内核里全 0 命中，
#     所以拿 DLL 字节判存在性会造出一条永假的断言；absl/opus/FLAC 有串也只作佐证打印）。
#     缺项的红标成**【能力被摘除】**而不是"安全违规"——两件事，标签不许混。
#     判据没对象（未传 --project）时判红，不静默跳过。
Write-Host "      · 本体中性（check_kernel_neutrality.py：先 --self-test，再二进制/源码/工程三层硬闸 + 免版税存在性反向判据）" -ForegroundColor Yellow
$neutPy   = Join-Path $PSScriptRoot "check_kernel_neutrality.py"
$neutFail = @()   # 判红原因，逐条点名 ⇒ 只有 .Count -eq 0 才放行
$neutWarn = @()   # 非拦截级提示：必须打出来并落日志，不许只留在变量里
if (-not $pyExe) {
    Add-Result "本体中性" $false "未找到 python，本体中性判据无法执行（fail-closed，不静默跳过）"
} elseif (-not (Test-Path -LiteralPath $neutPy)) {
    Add-Result "本体中性" $false "判据脚本不存在：$neutPy（fail-closed）"
} elseif (-not $kernelDll -or -not (Test-Path -LiteralPath $kernelDll)) {
    Add-Result "本体中性" $false "进包内核不可用（$kernelDll），判据定位不到它所属的那棵树；与 [2/11] 同一对象，先修那一处"
} else {
    # 从 DLL 往上最多 6 层找 FFF.Native.vcxproj：…\FFF.Native\x64\<配置>\FFF.Native.dll → …\FFF.Native
    $neutWalk    = Split-Path -Parent $kernelDll
    $neutProjDir = ""
    for ($i = 0; $i -lt 6; $i++) {
        if (Test-Path -LiteralPath (Join-Path $neutWalk "FFF.Native.vcxproj")) { $neutProjDir = $neutWalk; break }
        $neutUp = Split-Path -Parent $neutWalk
        if (-not $neutUp -or $neutUp -eq $neutWalk) { break }
        $neutWalk = $neutUp
    }
    $neutProj = if ($neutProjDir) { Join-Path $neutProjDir "FFF.Native.vcxproj" } else { "" }
    if (-not $neutProj) { $neutFail += "从 $kernelDll 往上 6 层没找到 FFF.Native.vcxproj——工程层判据没有对象，不能读成'工程层干净'" }
    $neutSrcOk = @()
    foreach ($neutSrcName in @("3FP", "3FR")) {
        if (-not $neutProjDir) { break }
        $neutSrcDir = Join-Path $neutProjDir $neutSrcName
        if (Test-Path -LiteralPath $neutSrcDir -PathType Container) { $neutSrcOk += $neutSrcDir }
        else { $neutFail += "源码层目录不存在：$neutSrcDir（二进制看不见 include 与被解释的结构类型，这一层缺对象=根本没扫，不能读成 0 命中）" }
    }

    # ① 自检先行：扫描器死了之后的"全 0"不能读成干净——这是判据脚本自己列的第一号自欺形状。
    $nst    = Invoke-Cmd $pyExe @($neutPy, "--self-test")
    $nstOut = @($nst.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    # 输出用 -f 格式化而不是 "…$l…"：这里打的是被扫描文件的原文行，里面可能含 $ 与反引号。
    foreach ($neutLine in $nstOut) { Write-Host ("      {0}" -f $neutLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $neutLine) }
    $stRow  = ($nstOut | Where-Object { $_ -match '抓到' } | Select-Object -Last 1)
    $stCtrl = ($nstOut | Where-Object { $_ -match '正控制' } | Select-Object -Last 1)
    $stInj = -1; $stRule = -1; $stGot = -1; $stHit = -1; $stAll = -1
    if ($stRow -and $stRow -match '注入\s*(\d+)\s*个词[（(]去重后\s*(\d+)\s*条判据[）)][，,]\s*抓到\s*(\d+)\s*条') {
        $stInj = [int]$Matches[1]; $stRule = [int]$Matches[2]; $stGot = [int]$Matches[3]
    }
    if ($stCtrl -and $stCtrl -match '正控制\s*(\d+)/(\d+)') { $stHit = [int]$Matches[1]; $stAll = [int]$Matches[2] }
    $neutBefore = $neutFail.Count
    if ($nst.Exit -eq -1) { $neutFail += "判据自检进程未能启动（exit=-1）：本会话无法启动原生进程，请在真实终端运行门禁" }
    elseif ($nst.Exit -ne 0) { $neutFail += "判据 --self-test 判红（exit=$($nst.Exit)）：词表或读取路径已坏，此后所有'全 0'读数都不成立" }
    elseif ($stGot -lt 0) { $neutFail += "自检读数行解析不到（原始输出已逐行打印在上）——没有读数=没跑，不能当成通过" }
    elseif ($stGot -ne $stRule) { $neutFail += "自检点名与合计不等：注入 $stInj 词 / 去重后 $stRule 条判据 / 抓到 $stGot 条" }
    elseif ($stHit -ne $stAll) { $neutFail += "自检正控制 $stHit/$stAll 不齐（读数行：$stCtrl）：连必然存在的串都读不到，等于对这个字节流没读到应有内容" }
    $neutScannerAlive = ($neutFail.Count -eq $neutBefore)

    # ② 三层判据跑在进包内核上。输出**逐行照打并同步写进日志**：三层现在都拦，读数自证。
    $neutArgs = @($neutPy, $kernelDll)
    if ($neutProj) { $neutArgs += @("--project", $neutProj) }
    foreach ($neutSrcDir in $neutSrcOk) { $neutArgs += @("--sources", $neutSrcDir) }
    $nsc      = Invoke-Cmd $pyExe $neutArgs
    $nscOut   = @($nsc.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($neutLine in $nscOut) { Write-Host ("      {0}" -f $neutLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $neutLine) }

    # ③ 读数提取（只认判据脚本自己打印的那几行；解析不到的分支一律走"没有读数=没跑"判红）
    $bLines = 0; $bWords = 0; $bTimes = 0; $bCtlHit = -1; $bCtlAll = -1
    $sLines = 0; $sHits = 0; $sFiles = 0; $sNamed = 0; $sMore = 0
    $pLines = 0; $pRows = 0; $pNamed = 0; $pMore = 0; $pTotal = -1
    # J1 反向判据读数：免版税依赖在**工程层**被证实链入的条数/应链条数（缺项=能力被摘除）
    $xLines = 0; $xHit = 0; $xAll = 0; $xMiss = 0
    $pPats = @(); $pOther = @(); $nFail = @()
    foreach ($neutLine in $nscOut) {
        if ($neutLine -match '\[FAIL\]') { $nFail += $neutLine.Trim(); continue }
        # J1 反向判据（2026-10-14）：判据新起的两行读数，前缀固定、逐工程各打一次。
        # 放在 [FAIL] 之后、其余行之前：它的文本不含"需授权"等字样，不会被下面任何
        # 既有正则误吃（既有行的格式判据一个都没改）。
        if ($neutLine -match '免版税存在性·工程层[：:]\s*(\d+)/(\d+)') {
            $xLines++; $xHit += [int]$Matches[1]; $xAll += [int]$Matches[2]
            $xMiss += ([int]$Matches[2] - [int]$Matches[1]); continue
        }
        if ($neutLine -match '免版税存在性·按块') { continue }
        if ($neutLine -match '免版税存在性·二进制佐证') { continue }
        if ($neutLine -match '正控制命中\s*(\d+)/(\d+)') { $bCtlHit = [int]$Matches[1]; $bCtlAll = [int]$Matches[2]; continue }
        if ($neutLine -match '判红级痕迹[：:]\s*(\d+) 个词命中[，,]共 (\d+) 次') { $bLines++; $bWords += [int]$Matches[1]; $bTimes += [int]$Matches[2]; continue }
        if ($neutLine -match '判红级痕迹[：:]\s*(\d+) 命中') { $bLines++; $bWords += [int]$Matches[1]; continue }
        if ($neutLine -match '需授权实现[：:]\s*(\d+) 处[，,]涉及 (\d+) 个文件[（(]点名条数=(\d+)') {
            $sLines++; $sHits += [int]$Matches[1]; $sFiles += [int]$Matches[2]; $sNamed += [int]$Matches[3]
            if ([int]$Matches[3] -ne [int]$Matches[1]) { $nFail += "源码层读数自相矛盾：命中 $($Matches[1]) 处 / 点名条数 $($Matches[3])" }
            continue
        }
        if ($neutLine -match '需授权实现[：:]\s*(\d+) 命中') { $sLines++; $sHits += [int]$Matches[1]; continue }
        if ($neutLine -match '[（(]另有\s*(\d+)\s*处[）)]') { $sMore = [int]$Matches[1]; continue }
        if ($neutLine -match '需授权库[：:]\s*(\d+) 行命中') { $pLines++; $pRows += [int]$Matches[1]; continue }
        if ($neutLine -match '合计\s*(\d+)\s*行') { $pTotal = [int]$Matches[1]; continue }
        if ($neutLine -match '[（(]另有\s*(\d+)\s*行同类命中[）)]') { $pMore = [int]$Matches[1]; continue }
        # 工程层点名行形如 `     :138   /fdk-aac\.lib/  <AdditionalDependencies>…`（源码层点名行以文件名开头，不会误匹配）
        if ($neutLine -match '^\s+:(\d+)\s+/([^/]+)/') {
            $pNamed++; $neutPat = $Matches[2]; $pPats += $neutPat
            if ($neutPat -notmatch '(?i)fdk|aacplus') { $pOther += ":$($Matches[1]) /$neutPat/" }
            continue
        }
    }

    # ④ 读数对账：传进去几个对象就该读到几条该层读数，少一条=那一层压根没被扫过（静默跳过是本仓的老坑）。
    if ($bLines -ne 1) { $neutFail += "二进制层读数 $bLines 条 ≠ 传入目标 1 个（正控制全灭时判据不打印判红读数，等于这个件没被扫）" }
    if ($sLines -ne $neutSrcOk.Count) { $neutFail += "源码层读数 $sLines 条 ≠ 传入目录 $($neutSrcOk.Count) 个" }
    if ($neutProj -and $pLines -ne 1) { $neutFail += "工程层读数 $pLines 条 ≠ 传入工程 1 个" }
    # J1 反向判据：传了工程就必须读到存在性读数。读不到=那条判据没跑过，
    # 而"没跑过"绝不能被读成"免版税库都在"（这正是它此前作为死代码的形状）。
    if ($neutProj -and $xLines -ne 1) { $neutFail += "免版税存在性读数 $xLines 条 ≠ 传入工程 1 个——反向判据没跑，'能力齐全'属未判定，不能读成通过" }
    if ($xLines -gt 0 -and $xAll -le 0) { $neutFail += "免版税存在性读数分母为 0（读到 $xHit/$xAll）——判据自己没词表了，不信这个读数" }
    if ($pRows -gt 0 -and $pTotal -ge 0 -and $pTotal -ne $pRows) { $neutFail += "工程层命中 $pRows 行 ≠ 判据自报合计 $pTotal 行" }
    if ($pRows -gt 0 -and ($pNamed + $pMore) -ne $pRows) { $neutWarn += "工程层点名 $pNamed + 未点名 $pMore ≠ 命中 $pRows 行（判据单次最多列 12 行）：本步整层拦截，未点名的那些行同样计入 $pRows，逐行清单要回判据脚本看完整输出" }
    if ($sHits -gt 0 -and ($sNamed + $sMore) -ne $sHits) { $neutWarn += "源码层点名 $sNamed + 未点名 $sMore ≠ 命中 $sHits 处（判据单次最多列 20 处）" }
    foreach ($neutF in $nFail) { $neutFail += "判据自身报错：$neutF" }

    # ⑤ 退出码与读数必须互相解释得通；硬闸 = 二进制层 + 源码层 + **工程层**命中数全为 0。
    #    工程层从 docs/94 起并入硬闸：它此前唯一的红是 `fdk-aac.lib`（当时按"待摘"显式豁免），
    #    T2 已把 libiamf 的 AAC 后端换成随包 FFmpeg 原生解码器、两棵树的 vcxproj 都不再引用它，
    #    豁免因此**失去对象**——留着就是一盏永远不会亮的灯，还让读者以为工程层被拦过了。
    if ($nsc.Exit -eq -1) { $neutFail += "判据进程未能启动（exit=-1）：本会话无法启动原生进程" }
    elseif ($nsc.Exit -eq 0 -and ($bWords -gt 0 -or $sHits -gt 0 -or $pRows -gt 0 -or $xMiss -gt 0)) { $neutFail += "判据 exit=0 却读出判红命中（二进制 $bWords / 源码 $sHits / 工程 $pRows / 存在性缺项 $xMiss）：退出码与读数矛盾，本步不放行" }
    elseif ($nsc.Exit -eq 1 -and $bWords -eq 0 -and $sHits -eq 0 -and $pRows -eq 0 -and $xMiss -eq 0 -and $nFail.Count -eq 0) { $neutFail += "判据 exit=1 但三层读数全为 0：红在哪一层说不清，不放行" }
    elseif ($nsc.Exit -lt -1 -or $nsc.Exit -gt 1) { $neutFail += "判据 exit=$($nsc.Exit)（2=用法错/文件读不到，fail-closed）" }
    if ($bWords -gt 0) { $neutFail += "【硬闸】二进制层判红 $bWords 个词 / $bTimes 次命中——本体含需授权实现" }
    if ($sHits -gt 0) { $neutFail += "【硬闸】源码层判红 $sHits 处（涉及 $sFiles 个文件）——本体含需授权实现" }
    if ($pRows -gt 0) { $neutFail += "【硬闸】工程层判红 $pRows 行（模式 $(($pPats | Sort-Object -Unique) -join ',')）——需授权库仍被静态链进本体；免版税依赖不在此列（见判据 BINARY_ALLOWED）" }
    # J1 反向判据的红是**另一种红**：不是"本体含不该有的东西"，而是"本体该有的免版税能力被摘掉了"。
    # 标签必须分开写——混成"安全违规"会让人以为 IAMF/OAR 整块消失也是一次合规事件。
    if ($xMiss -gt 0) { $neutFail += "【能力被摘除】免版税依赖未证实链入 $xMiss 项（工程层读数 $xHit/$xAll）——出货版的空间音频能力被降级，这是功能缺失、不是安全违规；判据 tools/check_kernel_neutrality.py 反向判据（docs/92 T3.2）" }
    if (@($pOther).Count -gt 0) { $neutWarn += "工程层命中里含**非 fdk-aac 族**的模式（$(($pOther | Sort-Object -Unique) -join ', ')）：这些是新增的需授权依赖，与当年那条 fdk-aac 不同源，要单独定夺" }

    # ⑥ 读数摘要：点名条数与合计都要在里面。
    #    ⚠ 变量名后面紧跟汉字时必须用 $(…)：PowerShell 把 `$stInj词` 整串当变量名（汉字算名字字符），
    #      实测会打印成空白——读数打不出来，等于没有读数。
    $neutPats = if (@($pPats).Count) { ($pPats | Sort-Object -Unique) -join ',' } else { "无" }
    if ($pRows -gt 0) {
        $neutProjNote = "工程层判红 $pRows 行（模式 $neutPats / 判据合计 $pTotal）——需授权库被静态链进本体，本步拦截"
    } elseif ($pLines -eq 1) {
        $neutProjNote = "工程层 0 行（免版税依赖允许链入，判据不据其判红）"
    } else {
        # 没有这条读数就不能宣称"工程层 0 行"——扫描器死了的时候最容易在这里说谎。
        $neutProjNote = "工程层读数缺失（读到 $pLines 条）——不能说成 0 行，需授权库是否在链入属未判定"
    }
    # 判据末行的"结论: 本体含受限制实现内容（判红）"现在与本工程层硬闸一致：任何一层有判红命中，
    # 本步就拦。旧版这里有一段"红只来自工程层、本步不拦"的免责声明，随 T2 一起删掉了。
    # J1 反向判据的读数单独进摘要：三层全 0 而存在性缺项时，旧版会打印"本体干净"，
    # 现在这一格会直接写"能力被摘除 x/y"，日志里看得见"该有的东西不在了"。
    $neutExistNote = if ($xLines -eq 0) { "免版税存在性 读数缺失（读到 $xLines 条）——不能读成'能力齐全'" }
                     elseif ($xMiss -gt 0) { "免版税存在性 $xHit/$xAll ——【能力被摘除】缺 $xMiss 项" }
                     else { "免版税存在性 $xHit/$xAll 证实链入（反向判据通过）" }
    $neutRead = "对象 $kernelRel ‖ 自检 注入$($stInj)词/判据$($stRule)条/抓到$($stGot)条，正控制 $($stHit)/$($stAll)" +
                " ‖ 二进制 $bLines/1 件：判红 $bWords 词/$bTimes 次，正控制 $bCtlHit/$bCtlAll" +
                " ‖ 源码 $sLines/$($neutSrcOk.Count) 目录：判红 $sHits 处（涉及 $sFiles 文件，点名 $sNamed+另有 $sMore）" +
                " ‖ $neutProjNote ‖ $neutExistNote ‖ 判据 exit=$($nsc.Exit)"
    if (-not $neutScannerAlive) { $neutRead = "（自检未过，以下所有读数不作数）" + $neutRead }
    foreach ($neutW in $neutWarn) { Write-Host ("      ⚠ {0}" -f $neutW) -ForegroundColor Yellow; Write-Log ("      WARN {0}" -f $neutW) }
    Add-Result "本体中性" ($neutFail.Count -eq 0) ($neutRead + $(if ($neutFail.Count) { " ✗ " + ($neutFail -join '；') } else { "" }))
}

# ── [2/11] 追加一行：着色器 cbuffer 布局逐元素对账（判据 tools/check_shader_cbuffer_layout.py；docs/93 T1e）──
#   DynamicShaderSettings(VideoRenderer.h) 与内嵌 HLSL 的 cbuffer DynamicSettings 是两份手写声明，
#   编译器不替我对账：cbuffer 按字节读，字段名/顺序/填充错了都是"编译通过、每帧像素错"。
#   本轮实测就是靠这条抓到真缺陷：行主序 3x3 输出矩阵写成扁平 float[9] 时，m3 落进着色器
#   从不读的 TransformMatrix0.w，第 1、2 行整体错一位——字节数仍 144、行数仍 9，所以"只看
#   尺寸/行数"的对账（含本轮之前那条 cbuffer 行数检查）看不见它。判据把两侧都交给工具链裁决：
#   内嵌 HLSL 喂 fxc 读每个成员 Offset，struct 声明原样喂 cl 读 offsetof，逐格比 9 个矩阵元素
#   + 17 个标量/数组成员。正控制（缺一不可，否则读数不作数）：b0 'Settings' 必须先被 fxc 报出来
#   + ST 2094 那批成员必须逐个对上，才证明这台机器是活的（改前它就是红的，改后 9/9）。
#   对象 = 进包内核所属的那棵树（从 $kernelDll 反查，与本体中性同一口径，不硬编码 third_party）。
$layPy = Join-Path $PSScriptRoot "check_shader_cbuffer_layout.py"
$layNative = ""
if ($kernelDll -and (Test-Path -LiteralPath $kernelDll)) {
    $layWalk = Split-Path -Parent $kernelDll
    for ($i = 0; $i -lt 6; $i++) {
        if (Test-Path -LiteralPath (Join-Path $layWalk "3FP\Render\VideoRenderer.h")) { $layNative = $layWalk; break }
        $layUp = Split-Path -Parent $layWalk
        if (-not $layUp -or $layUp -eq $layWalk) { break }
        $layWalk = $layUp
    }
}
if (-not $pyExe) {
    Add-Result "着色器 cbuffer 布局" $false "未找到 python，布局判据无法执行（fail-closed，不静默跳过）"
} elseif (-not (Test-Path -LiteralPath $layPy)) {
    Add-Result "着色器 cbuffer 布局" $false "判据脚本不存在：$layPy（fail-closed）"
} elseif (-not $layNative) {
    Add-Result "着色器 cbuffer 布局" $false "从 $kernelRel 往上 6 层没找到 3FP\Render\VideoRenderer.h——布局判据没有对象，不能读成'布局一致'"
} else {
    $lay    = Invoke-Cmd $pyExe @($layPy, "--native-dir", $layNative)
    $layOut = @($lay.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($layLine in $layOut) { Write-Host ("      {0}" -f $layLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $layLine) }

    # 读数提取（只认判据自己打印的那几行；解析不到=没跑，走 fail-closed）
    $layFail = @()
    $laySize = -1; $layMatHit = -1; $layScalarHit = -1; $layScalarAll = -1; $layB0Ctl = -1
    foreach ($layLine in $layOut) {
        if ($layLine -match 'C\+\+ sizeof=(\d+)')              { $laySize = [int]$Matches[1] }
        if ($layLine -match 'b0 Settings 成员 (\d+)')           { $layB0Ctl = [int]$Matches[1] }
        if ($layLine -match '元素落点：(\d+)/9 对上')            { $layMatHit = [int]$Matches[1] }
        if ($layLine -match '标量/数组成员对账：(\d+)/(\d+)')     { $layScalarHit = [int]$Matches[1]; $layScalarAll = [int]$Matches[2] }
        if ($layLine -match '\[RED\]|\[FAIL\]')                { $layFail += $layLine.Trim() }
    }
    if ($lay.Exit -eq -1) { $layFail += "判据进程未能启动（exit=-1）：本会话无法启动原生进程，请在真实终端运行门禁" }
    if ($laySize -lt 0) { $layFail += "没读到 C++ sizeof——判据输出里没有那条读数，等于没跑" }
    elseif ($laySize -ne 144) { $layFail += "C++ sizeof=$laySize，不是钉死的 144（struct 与 static_assert 漂移了）" }
    if ($layB0Ctl -le 0) { $layFail += "正控制塌了：b0 'Settings' 成员数读到 $layB0Ctl，fxc 清单解析器可能是死的" }
    if ($layScalarAll -le 0) { $layFail += "没读到标量/数组对账那行（读数缺失≠布局正确）" }
    elseif ($layScalarHit -ne $layScalarAll) { $layFail += "标量/数组成员 $layScalarHit/$layScalarAll 对上，有成员没对上" }
    if ($layMatHit -lt 0) { $layFail += "没读到'元素落点：N/9'那行（矩阵判据没产出读数）" }
    elseif ($layMatHit -ne 9) { $layFail += "输出矩阵只有 $layMatHit/9 个元素落点对上——着色器读点与 C++ 摆放不一致（就是本轮抓到的那类缺陷）" }
    if ($lay.Exit -ne 0 -and $layFail.Count -eq 0) { $layFail += "判据 exit=$($lay.Exit) 但没打出 [RED]/[FAIL] 行：红在哪一格说不清，不放行" }

    $layRead = "对象 $layNative ‖ C++ sizeof=$laySize ‖ 正控制 b0 Settings 成员 $layB0Ctl ‖ 标量/数组 $layScalarHit/$layScalarAll ‖ 矩阵 $layMatHit/9 ‖ 判据 exit=$($lay.Exit)"
    Add-Result "着色器 cbuffer 布局" ($layFail.Count -eq 0) ($layRead + $(if ($layFail.Count) { " ✗ " + ($layFail -join '；') } else { "" }))
}

# ── [2/11] 追加一行：探针集全量扫编（判据 tools/check_probe_set.py + 基线 tools/probe_baseline.txt；J3）──
#   3fp/tools 下 82 个 C++ 探针此前**没有任何门禁编译它们** ⇒ 相对当前 ABI 无声烂掉：
#   内核头改一个字段、导入库少一个符号，探针一个字不动就先坏，而门禁全绿。
#   2026-10-13 全量扫编实测：55 成 / 27 坏（20 编译错 + 7 链接 LNK2019）。
#
#   ⚠ 判据不是"要全绿"——那从今天起恒红，与 [2/11] 曾经的 VSR 恒红是同一个病
#     （恒红的门禁只教会读日志的人忽略红）。判的是**坏名单 == 基线名单**：
#       新坏一个 ⇒ 红（红名单不许变长，这才是"本批把探针弄烂了"的信号）
#       修好一个 ⇒ 也红，提示"基线可缩短"（防只增不减，也防修好了还挂着不删）
#       失败层漂移 ⇒ 也红（条目含义变了，要重新对账）
#     ⇒ 想让它绿只有两条路：修好新坏的，或删掉已修的。没有第三条（判据 --self-test 五臂证明）。
#
#   ⚠ 成本与"比上次更快"的做法（**没有**把判据降级成"只数文件个数"）：
#     判据默认走**增量模式**——每个探针的结论按（探针源码 sha256 + 输入指纹）缓存于
#     tools/probe_baseline_cache.txt，指纹没变就复用上次的真编结论，变了才重编；
#     输入指纹覆盖 build_probe.py / 内核 ABI 头 / 内核导入库 / FFmpeg SDK 清单 / tlog。
#     缓存被手改、超龄（默认 14 天）、或输入指纹不符 ⇒ **整份作废全部重编**（判据的
#     --self-test 里这三臂都验过），所以缓存不可能把"坏"缓存成"好"。
#     加 -ProbeGateFull 强制全量重扫；-SkipProbeSetGate 是**显式降级=判红**（同 -SkipSelfTest 口径）。
Write-Host "      · 探针集扫编（check_probe_set.py：坏名单==基线；增量模式，只重编指纹变了的探针）" -ForegroundColor Yellow
$probePy    = Join-Path $PSScriptRoot "check_probe_set.py"
$probeBase  = Join-Path $PSScriptRoot "probe_baseline.txt"
$probeFail  = @()
if ($SkipProbeSetGate) {
    Add-Result "探针集扫编" $false "显式降级（-SkipProbeSetGate）：82 个探针未经扫编，本批有没有把它们弄坏属**未判定**——本次结果不得作为发布依据"
} elseif (-not $pyExe) {
    Add-Result "探针集扫编" $false "未找到 python，探针扫编判据无法执行（fail-closed，不静默跳过）"
} elseif (-not (Test-Path -LiteralPath $probePy)) {
    Add-Result "探针集扫编" $false "判据脚本不存在：$probePy（fail-closed）"
} elseif (-not (Test-Path -LiteralPath $probeBase)) {
    Add-Result "探针集扫编" $false "基线文件不存在：$probeBase——没有对账对象时判据一律判红，不能读成'探针都健康'"
} else {
    $probeArgs = @($probePy)
    if ($ProbeGateFull) { $probeArgs += "--full" }
    $pr     = Invoke-Cmd $pyExe $probeArgs
    $prOut  = @($pr.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($prLine in $prOut) { Write-Host ("      {0}" -f $prLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $prLine) }
    # 读数提取：只认判据自己打的那五行前缀；解析不到=没跑（本仓的老规矩）
    $pjTotal = -1; $pjScan = -1; $pjCompile = -1; $pjReuse = -1; $pjSec = -1
    $pjBase = -1; $pjBad = -1; $pjGood = -1; $pjNew = -1; $pjFixed = -1; $pjDrift = -1; $pjVerdict = ""
    foreach ($prLine in $prOut) {
        if ($prLine -match '探针集·对象[：:]\s*(\d+)') { $pjTotal = [int]$Matches[1] }
        if ($prLine -match '探针集·扫编[：:].*?对象\s*(\d+)\s*个.*?实编\s*(\d+).*?复用上次结论\s*(\d+).*?用时\s*([\d.]+)s') {
            $pjScan = [int]$Matches[1]; $pjCompile = [int]$Matches[2]; $pjReuse = [int]$Matches[3]; $pjSec = [double]$Matches[4]
        }
        if ($prLine -match '探针集·基线[：:]\s*(\d+)') { $pjBase = [int]$Matches[1] }
        if ($prLine -match '探针集·实测[：:]\s*(\d+)\s*条坏\s*/\s*(\d+)\s*条好') { $pjBad = [int]$Matches[1]; $pjGood = [int]$Matches[2] }
        if ($prLine -match '探针集·对账[：:]\s*新坏\s*(\d+)\s*/\s*已修\s*(\d+)\s*/\s*层漂移\s*(\d+)\s*/\s*判定=(\S+)') {
            $pjNew = [int]$Matches[1]; $pjFixed = [int]$Matches[2]; $pjDrift = [int]$Matches[3]; $pjVerdict = $Matches[4]
        }
        if ($prLine -match '\[RED\]') { $probeFail += "判据点名：$($prLine.Trim())" }
    }
    if ($pr.Exit -eq -1) { $probeFail += "判据进程未能启动（exit=-1）：本会话无法启动原生进程，请在真实终端运行门禁" }
    if ($pjTotal -lt 0) { $probeFail += "没读到「探针集·对象」那行（读数缺失=没跑，不能当成通过）" }
    if ($pjScan -lt 0) { $probeFail += "没读到「探针集·扫编」那行（没有实编/复用读数就无法确认这次真编过探针）" }
    if ($pjVerdict -eq "") { $probeFail += "没读到「探针集·对账」那行（判据没产出对账结论=没判，不是判过）" }
    if ($pjBase -lt 0 -or $pjBad -lt 0) { $probeFail += "没读到基线/实测两条计数（判据自身没报数，属未判定）" }
    # 覆盖对账：门禁要的是**全量**结论。判据在子集模式（--only）下本来就判红，这里再上一道：
    # 扫编对象数必须等于对象总数，否则"扫了 6 个都过"会被读成"82 个都过"。
    if ($pjTotal -ge 0 -and $pjScan -ge 0 -and $pjScan -ne $pjTotal) { $probeFail += "扫编覆盖 $pjScan ≠ 对象总数 $pjTotal（子集结论不作绿灯依据）" }
    if ($pjScan -ge 0 -and $pjCompile -ge 0 -and ($pjCompile + $pjReuse) -ne $pjScan) { $probeFail += "实编 $pjCompile + 复用 $pjReuse ≠ 扫编 $pjScan（计数自相矛盾，缓存读数不可信）" }
    if ($pjBad -ge 0 -and $pjGood -ge 0 -and $pjScan -ge 0 -and ($pjBad + $pjGood) -ne $pjScan) { $probeFail += "实测坏 $pjBad + 好 $pjGood ≠ 扫编 $pjScan（有探针没被计入结论）" }
    if ($pjNew -gt 0 -or $pjFixed -gt 0 -or $pjDrift -gt 0) {
        $probeFail += "坏名单与基线不等：新坏 $pjNew / 已修没删 $pjFixed / 层漂移 $pjDrift"
    }
    if ($pr.Exit -eq 0 -and $pjVerdict -ne "" -and $pjVerdict -ne "通过") { $probeFail += "判据 exit=0 却报判定=$pjVerdict（退出码与读数矛盾）" }
    # 判据 exit≠0 而"对账三项都是 0"：只有**对账行在**时才是矛盾（红从哪来说不清）；
    # 对账行压根没打=前置件/基线缺失那类 fail-closed 分支，上面那条已经点明了，别在这里重复误导。
    if ($pr.Exit -ne 0 -and $pr.Exit -ne -1 -and $pjVerdict -ne "" -and $pjNew -eq 0 -and $pjFixed -eq 0 -and $pjDrift -eq 0) { $probeFail += "判据 exit=$($pr.Exit) 但对账三项都是 0：红在哪一格说不清，不放行" }
    $probeRead = "对象 $pjTotal ‖ 扫编 $pjScan（实编 $pjCompile / 复用 $pjReuse，${pjSec}s） ‖ 基线 $pjBase ‖ 实测坏 $pjBad/好 $pjGood ‖ 对账 新坏 $pjNew/已修 $pjFixed/漂移 $pjDrift ‖ 判定 $pjVerdict ‖ exit=$($pr.Exit)"
    if ($pjReuse -eq 0 -and $pjScan -gt 50) { Write-Host "      ⚠ 本次全量重扫（复用 0 条）：首次建缓存、-ProbeGateFull、或缓存刚失效" -ForegroundColor Yellow }
    Add-Result "探针集扫编" ($probeFail.Count -eq 0) ($probeRead + $(if ($probeFail.Count) { " ✗ " + ($probeFail -join '；') } else { "" }))
}

# ── [2/11] 追加一行：内核配置 ABI（判据 tools/check_kernel_config_abi.py；J5，2026-10-14）──
#   `FFF3FP_Create` 对"配置结构体太短"和"sdrScRgbMode 越界"返回**同一个** InvalidArgument(-1)，
#   所以单看一次"mode 2 -> -1"什么都证不了：镜像比目标树短时 0/1 也会 -1，mode 2 反而"像通过"。
#   判据因此分五步，缺任一步就报 NOT JUDGED（exit=3，**不是绿**）：
#     ① 从 DLL 自己二分量出 sizeof(FFF3FPConfiguration)（不信任任何转录稿）；
#     ② ctypes 镜像的 sizeof 与字段数必须同时等于 ① 与该树头文件里的声明条数；
#     ③ 尺寸门签名：size=native-1 时 mode 0 也被拒 ⇒ -1 确实有两条成因；
#     ④ 阳性对照：正确 size 下 mode 0/1 必须 Success（没有这步，后面的 -1 不值钱）；
#     ⑤ 只有 ①-④ 全成立，mode 2 -> -1 才算"守卫在这份二进制里是活的"。
#
#   ⚠ 这条判据此前是**孤儿**：全仓 0 个 .ps1/.py 调用它，且镜像表只有 api 17/19，而两棵树现在
#     都是 api 20（`Api/PlayerApi.cpp` 里 `PlayerApiVersion = 20`）⇒ 实测两棵树都 FAIL
#     「mirror requested for api 17/19 but the DLL reports api 20」（docs/95 §N）。
#     本次补上两棵树各自的 api 20 镜像（出货 15 字段/80 B、内核 22 字段/112 B），
#     并在这里接进门禁 ——**不接的判据等于没有判据**。
#
#   ⚠ 镜像按树选，不由 api 号猜：两棵树同为 api 20，判据要求传 `--tree`；本步从 $kernelDll
#     往上找 `FFF.Native.vcxproj` 反查它属于哪棵树（与上面「本体中性」同一套反查思路，
#     不硬编码 third_party），反查不到就不传 --tree，由判据按实测 sizeof 自己挑并在结论里
#     打出挑了哪棵（80 B vs 112 B 足以区分），绝不让"猜错的镜像"静默判绿。
#
#   ⚠ fail-closed：判据进程没起来（-1）、读数行没打全、exit=0 但 VERDICT 不是 PASS、
#     或 exit=3（NOT JUDGED）一律判红。NOT JUDGED 是"环境没能给出结论"，**永远不是通过**。
Write-Host "      · 内核配置 ABI（check_kernel_config_abi.py：镜像==头文件字段数==DLL 自量 sizeof；尺寸门与 sdrScRgbMode 守卫分开证）" -ForegroundColor Yellow
$abiPy   = Join-Path $PSScriptRoot "check_kernel_config_abi.py"
$abiFail = @()
if ($NoConfigAbi) {
    Add-Result "内核配置 ABI" $false "显式降级（-NoConfigAbi）：FFF3FPConfiguration 的布局/尺寸门/守卫未经校验，本批有没有把配置结构体改歪属**未判定**——本次结果不得作为发布依据"
} elseif (-not $pyExe) {
    Add-Result "内核配置 ABI" $false "未找到 python，配置 ABI 判据无法执行（fail-closed，不静默跳过）"
} elseif (-not (Test-Path -LiteralPath $abiPy)) {
    Add-Result "内核配置 ABI" $false "判据脚本不存在：$abiPy（fail-closed）"
} elseif (-not $kernelDll -or -not (Test-Path -LiteralPath $kernelDll)) {
    Add-Result "内核配置 ABI" $false "进包内核不可用（$kernelDll），配置 ABI 判据没有对象；与 [2/11] 同一对象，先修那一处"
} else {
    # 反查进包内核属于哪棵树：从 DLL 往上最多 6 层找 FFF.Native.vcxproj 所在目录
    $abiWalk = Split-Path -Parent $kernelDll
    $abiTree = ""
    for ($i = 0; $i -lt 6; $i++) {
        if (-not $abiWalk) { break }
        if (Test-Path -LiteralPath (Join-Path $abiWalk "FFF.Native.vcxproj")) {
            if ($abiWalk -match '(?i)[\\/]third_party[\\/]') { $abiTree = "third_party" }
            elseif ($abiWalk -match '(?i)[\\/]3fp[\\/]kernel[\\/]') { $abiTree = "kernel" }
            break
        }
        $abiWalk = Split-Path -Parent $abiWalk
    }
    $abiArgs = @($abiPy, "--dll", $kernelDll)
    if ($abiTree) { $abiArgs += @("--tree", $abiTree) }
    $ab     = Invoke-Cmd $pyExe $abiArgs
    $abOut  = @($ab.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($abLine in $abOut) { Write-Host ("      {0}" -f $abLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $abLine) }
    # 读数提取：只认判据自己打的那几行；解析不到=没跑（本仓的老规矩）
    $abMirror = ""; $abFields = -1; $abCtypes = -1; $abNative = -1
    $abPass = -1; $abFailN = -1; $abUndec = -1; $abVerdict = ""
    foreach ($abLine in $abOut) {
        if ($abLine -match '^\s*mirror\s*:\s*api\s+(\d+)\s*/\s*(\S+)\s+tree\s*/\s*(\d+)\s+fields') {
            $abMirror = $Matches[2]; $abFields = [int]$Matches[3]
        }
        if ($abLine -match '\[ctypes mirror\]\s*=\s*(\d+)\s*bytes') { $abCtypes = [int]$Matches[1] }
        if ($abLine -match '\[measured dll\]\s*=\s*(\d+)\s*bytes') { $abNative = [int]$Matches[1] }
        if ($abLine -match 'judged-pass=(\d+)\s+judged-fail=(\d+)\s+not-judged=(\d+)') {
            $abPass = [int]$Matches[1]; $abFailN = [int]$Matches[2]; $abUndec = [int]$Matches[3]
        }
        if ($abLine -match '^VERDICT:\s*(\S+)' -and $abVerdict -eq "") { $abVerdict = $Matches[1] }
    }
    if ($ab.Exit -eq -1) { $abiFail += "判据进程未能启动（exit=-1）：本会话无法启动原生进程，请在真实终端运行门禁" }
    if ($abMirror -eq "" -or $abFields -lt 0) { $abiFail += "没读到「mirror」那行（不知道按哪棵树的镜像判的=没判，不能当成通过）" }
    if ($abCtypes -lt 0 -or $abNative -lt 0) { $abiFail += "没读到 ctypes/实测两条 sizeof 读数（判据自身没报数，属未判定）" }
    if ($abPass -lt 0) { $abiFail += "没读到 counts 那行（judged-pass/fail/not-judged 三计数是判据的账本，缺了就是没判）" }
    if ($abVerdict -eq "") { $abiFail += "没读到 VERDICT 那行（判据没产出结论=没判，不是判过）" }
    # exit=3 是 NOT JUDGED：环境没能给出结论，按本仓口径它**必须红**，不能读成"没报错就算过"
    if ($ab.Exit -eq 3) { $abiFail += "判据 NOT JUDGED（exit=3）：环境没能给出 ABI 结论，这不是通过（见判据头 5 步说明）" }
    if ($ab.Exit -ne 0 -and $ab.Exit -ne -1 -and $ab.Exit -ne 3) { $abiFail += "判据 exit=$($ab.Exit)：$abVerdict" }
    if ($ab.Exit -eq 0 -and $abVerdict -ne "PASS") { $abiFail += "判据 exit=0 却报 VERDICT=$abVerdict（退出码与结论矛盾）" }
    if ($abCtypes -ge 0 -and $abNative -ge 0 -and $abCtypes -ne $abNative) {
        $abiFail += "镜像 $abCtypes B ≠ DLL 实测 $abNative B：这个尺寸下内核一律按 size 拒，任何 -1 都是尺寸拒绝、不是 sdrScRgbMode 拒绝"
    }
    $abiRead = "树 $(if ($abiTree) { $abiTree } else { '自动' }) ‖ 镜像 $abMirror/$abFields 字段 ‖ sizeof 镜像 $abCtypes B / 实测 $abNative B ‖ 账本 绿 $abPass/红 $abFailN/未判 $abUndec ‖ 判定 $abVerdict ‖ exit=$($ab.Exit)"
    Add-Result "内核配置 ABI" ($abiFail.Count -eq 0) ($abiRead + $(if ($abiFail.Count) { " ✗ " + ($abiFail -join '；') } else { "" }))
}

# ── [2/11] 追加一行：输出档位阶梯一致性（判据 tools/check_output_tier_ladder_parity.py；2026-10-19 接线）──
#   这条判据 10-18 22:13 就写好了，但**全仓零调用者**——10-19 早上那笔欠账
#   （docs/交接文档-2026-10-19-自动HDR判词与VSR像素闭环.md §5.5：「宿主 Form媒体信息.vb:497
#   那份阶梯与内核 SwapChainFormatForOutputBits 是两份实现 ⇒ 需要一个托管判据钉住」）
#   要的从来不是"再写一个脚本"，而是"让它每次发布都跑"。与上面 J5 那条同一句教训：
#   **不接的判据等于没有判据**。
#
#   口径（别读反）：判据自带一把独立 python 参考式（`档位 = 16 若 hdr 否则 (10 若源位深>8 否则 8)`），
#   拿它去量四处：内核两棵树各一份、宿主回退式两棵树各一份，再加两张文案表的界值
#   （宿主 >=16/>=10、呈现器 Case 8/10/16）。正常臂 exit=0 且打「结论: 四处阶梯同源」才算过。
#
#   ⚠ 变异臂的退出码是**反的**，接线时最容易读错，这里写死语义：
#     `--mutate` 把实验树内核阈值改成 >10（写到 _3fc_tmp\mutated_VideoRenderer.cpp 的副本，
#     不碰真源），**判红才是对的** ⇒ 判据 exit=0 表示"有牙"，exit=1 表示"判据是死的"。
#     所以本步两条臂都要求 exit=0，但读的是不同的结论行。
#   ⚠ fail-closed：python 找不到、脚本不存在、结论行没打全、"一致"计数 ≠ 6，一律判红。
Write-Host "      · 输出档位阶梯一致性（check_output_tier_ladder_parity.py：四处实现 + 两张文案表对撞独立参考式，另跑变异臂证牙）" -ForegroundColor Yellow
$tierPy   = Join-Path $PSScriptRoot "check_output_tier_ladder_parity.py"
$tierFail = @()
if (-not $pyExe) {
    Add-Result "输出档位阶梯一致性" $false "未找到 python，档位阶梯判据无法执行（fail-closed，不静默跳过）"
} elseif (-not (Test-Path -LiteralPath $tierPy)) {
    Add-Result "输出档位阶梯一致性" $false "判据脚本不存在：$tierPy（fail-closed）"
} else {
    $tierNorm = Invoke-Cmd $pyExe @($tierPy)
    $tierOut  = @($tierNorm.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($tLine in $tierOut) { Write-Host ("      {0}" -f $tLine) -ForegroundColor Gray; Write-Log ("      {0}" -f $tLine) }
    $tierAgree = @($tierOut | Where-Object { $_ -match '\s一致\s' }).Count
    $tierVerdict = @($tierOut | Where-Object { $_ -match '^结论:' } | Select-Object -Last 1)
    if ($tierNorm.Exit -ne 0) { $tierFail += "正常臂 exit=$($tierNorm.Exit)：档位阶梯在四处中至少一处不同水" }
    if (-not $tierVerdict -or $tierVerdict -notmatch '四处阶梯同源') {
        $tierFail += "没读到「结论: 四处阶梯同源」这行（判据没产出结论=没判，不是判过）"
    }
    # 计数必须与点名条数相等：内核×2 + 宿主回退×2 + 宿主文案 + 呈现器文案 = 6
    if ($tierAgree -ne 6) { $tierFail += "「一致」计数 $tierAgree ≠ 6 处（少一处就是有一处没被量到，恒真的可能是解析锚点漂了）" }

    $tierMut = Invoke-Cmd $pyExe @($tierPy, "--mutate")
    $tierMutOut = @($tierMut.Out | Where-Object { $_ -and $_.Trim() -ne "" })
    foreach ($mLine in $tierMutOut) { Write-Host ("      {0}" -f $mLine) -ForegroundColor DarkGray; Write-Log ("      {0}" -f $mLine) }
    $tierMutVerdict = @($tierMutOut | Where-Object { $_ -match '^结论:' } | Select-Object -Last 1)
    if ($tierMut.Exit -ne 0) { $tierFail += "变异臂 exit=$($tierMut.Exit)：改了内核阈值判据却不判红 ⇒ 这条判据是死的（锚点漂了或参考式与被测同源）" }
    if (-not $tierMutVerdict -or $tierMutVerdict -notmatch '变异臂判红了') {
        $tierFail += "变异臂没打「变异臂判红了」（exit=0 但结论行缺失，读作没判）"
    }
    Add-Result "输出档位阶梯一致性" ($tierFail.Count -eq 0) (
        "一致 $tierAgree/6 ‖ 正常臂 $($tierNorm.Exit) ‖ 变异臂 $($tierMut.Exit)（0=有牙）" +
        $(if ($tierFail.Count) { " ✗ " + ($tierFail -join '；') } else { "" }))
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

# ── [3/11] 追加一行：内嵌内核一致性（不占编号；J4，2026-10-14）─────────────────
#   csproj 把 `KernelDllPath` 那份 FFF.Native.dll 当 `<EmbeddedResource LogicalName="FFF.Native.dll">`
#   嵌进宿主（src/3FCompare/3FCompare.csproj:55/:59），运行时再解压出来用。
#   但**没有任何判据校验"嵌进去的那份 == 我以为的那份"**：
#   [2/11] 验的是磁盘上那份 DLL 的导出面/中性/配对，[3/11] 验的是"编译成功"，两者都不看产物里的字节。
#   ⚠ 更正（2026-10-14 复核）：本段早先那句「`GetManifestResourceStream` 0 命中」是**假陈述**，
#     实测 `src/3FCompare/Program.cs:68` 与 `:324` 都在用它（`ExtractEmbedded` 的解压路径）。
#     宿主**有**内嵌件读取代码，缺的是"校验读到的是不是我以为的那一份"。
#     本判据按字节对账、不依赖任何字符串命中，故这条更正不改判据逻辑，只改读者判断。
#   实测形状就是今天的状态：`publish\3FCompare\3FCompare.exe` 偏移 11,888,041 起那 6,180,352 字节
#   == 上一版内核，而磁盘上的进包内核已经是 5,398,016 字节 ⇒ 发出去的 exe 里嵌着**旧内核**，
#   而门禁此前一路绿灯。部署侧（tools/构建全部.ps1 / pack.ps1）由主循环并行改，本步只负责**验**。
#
#   ⚠ 语义（别读反）：**运行目录里那份松 DLL 才是权威，内嵌件只是"松 DLL 缺失时"的回退**。
#     `Program.ExtractEmbedded`（src/3FCompare/Program.cs:317-322）是**有意**不覆盖已存在的
#     松 DLL（`if (File.Exists(target)) { AssertArchMatches(...); return; }`，
#     2026-09-16 那次 API 15→14 降级事故后定的规矩）。
#     所以"exe 里嵌着旧内核"**不等于**"当前运行目录坏了"——它意味着
#     **一次全新安装 / 松 DLL 被删掉之后，会解出那个旧内核**。
#     本判据拦的是后者，不要读成前者；也不要据此判定"到货失败=部署问题"。
#
#   判法（要能排除"长度碰巧相同"）：拿内核**头 4096 字节**在载体字节流里定位，
#   再把"从命中偏移起、长度正好等于内核文件大小"的那一整段取出来算 SHA256 与内核文件比——
#   整段内容对上才算数，长度相同而内容不同的东西在这种校验下必判不等。
#   ⚠ 搜索针不能短于节表：实测 16 字节的 PE 头会在**宿主自身的 PE 头**命中（偏移 0），
#     那是所有 PE 都相同的部分，拿它定位等于没定位。
#   ⚠ 版本串是 UTF-16LE（实测按 ASCII 搜 `api 17` 得 0 命中、换 UTF-16 才得 1），
#     所以这里**不靠任何字符串**判存在，只按字节流对账。
#   fail-closed：内核不可用 / 一个宿主产物都没有 / 产物里找不到内嵌件 ⇒ 一律判红，不许"那就跳过"。
Write-Host "      · 内嵌内核一致性（KernelDllPath 那份字节流是否原样躺在宿主产物里）" -ForegroundColor Yellow

# 载体清单：
#   · IL/JIT 构建：EmbeddedResource 落在**托管程序集** 3FCompare.dll 里
#     （同目录的 3FCompare.exe 是 74KB apphost，不承载资源——别拿它判"没嵌"）
#   · AOT 单文件发货物（存在才验，不存在不判红：那是 -WithPack/pack.ps1 的产物）
#     与部署目录 publish\3FCompare\3FCompare.exe
function Find-KernelSpan {
    param(
        [Parameter(Mandatory)] [string]$Carrier,
        [Parameter(Mandatory)] [string]$DllPath
    )
    # 字节↔字符用代码页 28591（Latin-1）做 1:1 映射后再按 Ordinal 找子串：
    # 实测 42MB 载体 + 5.4MB 针，PS 手写循环扫不动，而 Latin-1 单字节编码下逐字符==逐字节，
    # Ordinal 比较不做任何规范化/大小写折叠 ⇒ 与字节搜索等价（Python 侧同一份数据 0.02s 对齐）。
    # 必须先把中间结果**落成变量**再调用：`$a.IndexOf($b.Substring(0,4096),[StringComparison]::Ordinal)`
    # 这种链式写法在 5.1 下会把参数解析错（实测恒返回 -1）。
    $db  = [IO.File]::ReadAllBytes($DllPath)
    $hb  = [IO.File]::ReadAllBytes($Carrier)
    $enc = [Text.Encoding]::GetEncoding(28591)
    $hayStr   = $enc.GetString($hb)
    $needleAll = $enc.GetString($db)
    $headLen  = [Math]::Min(4096, $db.Length)
    $headStr  = $needleAll.Substring(0, $headLen)
    $needSha  = (Get-FileHash -Algorithm SHA256 -LiteralPath $DllPath).Hash
    $from = 0
    for ($try = 0; $try -lt 8; $try++) {
        $off = $hayStr.IndexOf($headStr, $from, [StringComparison]::Ordinal)
        if ($off -lt 0) { break }
        if (($off + $db.Length) -le $hb.Length) {
            $span = New-Object byte[] $db.Length
            [Array]::Copy($hb, $off, $span, 0, $db.Length)
            $ms = New-Object IO.MemoryStream(,$span)
            $got = (Get-FileHash -Algorithm SHA256 -InputStream $ms).Hash
            $ms.Dispose()
            if ($got -eq $needSha) {
                return [pscustomobject]@{ Found = $true; Offset = $off; SpanSha = $got; NeedSha = $needSha; Size = $db.Length }
            }
        }
        $from = $off + 1
    }
    return [pscustomobject]@{ Found = $false; Offset = -1; SpanSha = ""; NeedSha = $needSha; Size = $db.Length }
}

$embHostDll = Join-Path $ProjectRoot "src\3FCompare\bin\$binFlavor\net11.0-windows\3FCompare.dll"
$embCarriers = @()
if (Test-Path -LiteralPath $embHostDll) { $embCarriers += $embHostDll }
foreach ($cand in @(
        (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64-full\3FCompare.exe"),
        (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64\3FCompare.exe"),
        (Join-Path $ProjectRoot "publish\3FCompare\3FCompare.exe"))) {
    if (Test-Path -LiteralPath $cand) { $embCarriers += $cand }
}
# 找不到时用来回答"那产物里嵌的到底是哪一份"：盘上其它 FFF.Native.dll 副本（去重、排除基准本身）
$embHunt = @()
foreach ($cand in @(
        (Join-Path $ProjectRoot "src\3FCompare\bin\$binFlavor\net11.0-windows\FFF.Native.dll"),
        (Join-Path $ProjectRoot "publish\3FCompare\FFF.Native.dll"),
        (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64-full\FFF.Native.dll"),
        (Join-Path $ProjectRoot "publish\build\3FCompare-v$Version-x64\FFF.Native.dll"),
        (Join-Path $ProjectRoot "3fp\build\FFF.Native.dll"))) {
    if (Test-Path -LiteralPath $cand) { $embHunt += $cand }
}
if (-not $kernelDll -or -not (Test-Path -LiteralPath $kernelDll)) {
    Add-Result "内嵌内核一致性" $false "进包内核不可用（$kernelDll）——没有基准字节流可比，判红不放行（与 [2/11] 同一对象，先修那一处）"
} elseif ($embCarriers.Count -eq 0) {
    Add-Result "内嵌内核一致性" $false "一个宿主载体都没找到（$embHostDll 以及 publish 下的 AOT exe 都不在）——[3/11] 没编出宿主？fail-closed，不跳过"
} else {
    $embFail = @()
    $embNote = @()
    $kernelShaShort = ""
    foreach ($carrier in $embCarriers) {
        $carrierRel = $carrier
        if ($carrierRel.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $carrierRel = $carrierRel.Substring($ProjectRoot.Length).TrimStart('\')
        }
        $hit = Find-KernelSpan -Carrier $carrier -DllPath $kernelDll
        $kernelShaShort = $hit.NeedSha
        if ($hit.Found) {
            $embNote += ("{0}：内嵌件命中@{1}，整段 {2} 字节 SHA256={3} == 基准" -f `
                $carrierRel, $hit.Offset, $hit.Size, $hit.SpanSha.Substring(0, 12))
            Write-Host ("      ✅ {0} 内嵌内核一致（偏移 {1}，{2} 字节）" -f $carrierRel, $hit.Offset, $hit.Size) -ForegroundColor Green
        } else {
            $what = ""
            foreach ($alt in $embHunt) {
                if ($alt -eq $kernelDll) { continue }
                $altRel = $alt
                if ($altRel.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    $altRel = $altRel.Substring($ProjectRoot.Length).TrimStart('\')
                }
                $probe = Find-KernelSpan -Carrier $carrier -DllPath $alt
                if ($probe.Found) {
                    $what = ("产物里那份是**另一个件**：{0}（偏移 {1}，整段 {2} 字节 SHA256={3}）" -f `
                        $altRel, $probe.Offset, $probe.Size, $probe.SpanSha.Substring(0, 12))
                    break
                }
            }
            if (-not $what) { $what = "产物里找不到任何已知内核的完整字节特征（内嵌件不属已知副本，未定位偏移）" }
            $embFail += ("{0}：内嵌内核 ≠ 进包内核；{1}" -f $carrierRel, $what)
            Write-Host ("      ❌ {0}：{1}" -f $carrierRel, $what) -ForegroundColor Red
        }
        Write-Log ("      EMB {0} -> {1}" -f $carrierRel, $(if ($hit.Found) { "命中@$($hit.Offset) sha=$($hit.SpanSha.Substring(0,12))" } else { "未命中 基准=$($hit.NeedSha.Substring(0,12))" }))
    }
    # 摘要：基准（sha/大小/路径）+ 每个载体一行结论；失败项跟在后面，别把"全对"读成"没查"。
    # ⚠ 这里**不用**三元 `? :`——那是 PowerShell 7 语法，本门禁要在 Windows PowerShell 5.1 下跑。
    $embOkNote = if (@($embNote).Count -gt 0) { ($embNote -join '；') } else { "（$($embCarriers.Count) 个载体全部不一致）" }
    $embRead = "基准 $(Short $kernelShaShort)（$kernelRel，$((Get-Item -LiteralPath $kernelDll).Length) 字节） ‖ 载体 $($embCarriers.Count) 个 ‖ $embOkNote"
    if ($embFail.Count -gt 0) { $embRead += "；✗ " + ($embFail -join '；') }
    Add-Result "内嵌内核一致性" ($embFail.Count -eq 0) $embRead
}

# ── [4/11] 单元测试全绿 ──
#   失败必须点名到用例：只报"失败: 1"的红无法归因（实测踩过——门禁报 1 失败，
#   隔离重跑 877 全过，而 trx 没开，连是哪个用例都拿不到）。
#   trx 归 logs\TestResults\（.gitignore 已忽略 logs/），取不到名字时显式说"没取到"，
#   绝不把"读不到"印成"没有失败"。
function Get-FailingTestNames([string]$TrxPath) {
    if (-not (Test-Path -LiteralPath $TrxPath)) { return @("<trx 缺失：$TrxPath>") }
    try {
        [xml]$x = Get-Content -LiteralPath $TrxPath -Raw -Encoding UTF8
        $bad = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
        if ($bad.Count -eq 0) { return @("<trx 里没有 Failed 用例：退出码与用例结果不一致，查运行器>") }
        return @($bad | ForEach-Object {
            $m = ""
            if ($_.Output -and $_.Output.ErrorInfo -and $_.Output.ErrorInfo.Message) {
                $m = " —— " + (($_.Output.ErrorInfo.Message -replace '\s+', ' ').Trim())
            }
            "{0}{1}" -f $_.testName, $m
        })
    } catch {
        return @("<trx 解析失败：$($_.Exception.Message)>")
    }
}

Write-Host "`n[4/11] 单元测试" -ForegroundColor Yellow
$TestResultsDirectory = Join-Path $ProjectRoot "logs\TestResults"
if (-not (Test-Path -LiteralPath $TestResultsDirectory)) { New-Item -ItemType Directory -Path $TestResultsDirectory -Force | Out-Null }
$coreTrx = Join-Path $TestResultsDirectory "gate-core.trx"
$test    = Invoke-Cmd $Dotnet @("test", $TestProject, "-c", "Release", "--no-restore", "--nologo",
                                "--results-directory", $TestResultsDirectory, "-l", "trx;LogFileName=gate-core.trx")
$testOut = $test.Out
$testOk  = $test.Exit -eq 0
$summary = ($testOut | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
if (-not $testOk) { $summary = "$summary ⇒ 失败用例：" + ((Get-FailingTestNames $coreTrx) -join ' ／ ') }
Add-Result "单元测试" $testOk (Format-FailDetail $test.Exit $summary)

# ── [5/11] Platform 层测试：抓屏路由 / 裁剪 / Win32 区域 / 本地化键完整性 ──
#   本工程是 tests/ 下唯一引用 UI 工程的测试（WindowRegionClipper 等只在 UI 工程里，
#   而 Core 是 net11.0 无 Windows TFM，Core.Tests 够不到）。此前全仓无脚本调用它 ⇒ 该层回归是空的。
#   docs/41 §六#4「本地化键完整性」**不需要再加独立门禁项**：本工程内的
#   LanguageManagerKeysTests.两张语言表键集合一致时返回空列表 已 `Assert.Empty(FindMissingKeys())`
#   （FindMissingKeys 双向比对 zh/en 两张表），随本项一起跑即已覆盖。
Write-Host "`n[5/11] Platform 测试（抓屏路由 / 裁剪 / Win32 区域 / 本地化键）" -ForegroundColor Yellow
$platTrx   = Join-Path $TestResultsDirectory "gate-platform.trx"
$platTest  = Invoke-Cmd $Dotnet @("test", $PlatformTest, "-c", "Release", "--no-restore", "--nologo",
                                  "--results-directory", $TestResultsDirectory, "-l", "trx;LogFileName=gate-platform.trx")
$platOut     = $platTest.Out
$platSummary = ($platOut | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
if ($platTest.Exit -ne 0) { $platSummary = "$platSummary ⇒ 失败用例：" + ((Get-FailingTestNames $platTrx) -join ' ／ ') }
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
        # ⚠ --selftest 的退出码**不反映 SKIP**（有跳过也照样 exit=0，只在末尾打一行
        # "本次自测有 N 项跳过（未验证，不等于通过）"）。只判 exit 就会把"没验"报成 PASS。
        # 这里把跳过项拉进判决：本步没跑完 ⇒ 不得作为发布依据，原因逐条进详情。
        $skipBullets = @($run.Out | Where-Object { $_ -match '• \[' })
        $banner = ($run.Out | Where-Object { $_ -match '本次自测有 (\d+) 项跳过' } | Select-Object -Last 1)
        # 显式再匹配一次，不抄 ambient $Matches：中间那条 -match '\[\S.*\]' 也写过它，
        # 组号对不上时 [int]$Matches[1] 会静默变 0 ⇒ "有跳过"被判成"没跳过"。
        $skipN = if ($banner) { [int][regex]::Match($banner, '本次自测有 (\d+) 项跳过').Groups[1].Value } else { 0 }
        # 两个来源互相核对：横幅声明的 N 与实际数到的条数不等 ⇒ 自测输出格式漂移了，
        # 此时宁可判红，也不能因为"正则失配 ⇒ 数到 0"而报全绿。
        $ok = ($code -eq 0) -and ($skipBullets.Count -eq 0) -and ($skipN -eq 0)
        $detail = if ($skipBullets.Count -gt 0 -or $skipN -gt 0) {
            "$line (exit=$code) — 有跳过项（未验证 ≠ 通过），横幅=$skipN 条 / 实到=$($skipBullets.Count) 条：" +
            (($skipBullets | ForEach-Object { ($_ -replace '^.*?• ', '') }) -join ' | ')
        } else {
            Format-FailDetail $code "$line (exit=$code)"
        }
        Add-Result "单路全量" $ok $detail
    }

    # ── [7/11] 追加一行：多路对比回归（--comparemodetest，5 路档）──
    #   为什么挂在本步而不是新起一个编号：文件头的编号约定是"分母 = 11 个带号步骤"，
    #   加一行 Add-Result 不改变步骤数，也就不动 [n/11] 的既有引用（docs 里到处指它）。
    #   此前这一步从不被门禁调用 ⇒ 全部隔离单元（朝向扫掠/格表驱动布局/裁剪接线/
    #   分割线命中/路启用态/视图模式列……）整批在门禁之外，"门禁绿"不代表对比模式没坏。
    #   5 路是**故意**选的：合并后的网格一路一格、不会把路挤出去，"多余路停用 → 再启用"
    #   只有左右拉动（恒两格铺满）才造得出来；5 路同时还能把 3×2 与 2×3 两种朝向都扫到。
    #   ⇒ 除了 exit，还额外要求"跳过数为 0"（2026-10-01 实测：5 路 26 项、9 路 24 项，均失败 0 跳过 0。
    #   两档项数不同只是**朝向扫掠的条数**随路数变（5 路扫 3×2 与 2×3，9 路只有 3×3），
    #   不是有条目被跳过——跳过数两档都是 0。
    #   项数含 2026-10-01 新增的 `拖动热路径`（覆盖层合并提交/结算/回灌四臂）——在此之前这一段没有测量。）
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

# ── 独立门禁项（不占编号）：许可卫生 ──
# 2026-10-15 IP 普查（.3fc_dumps/ip-audit-20261015/）查出的形状：
#   ① 盘上发货的 FFmpeg 是 `--enable-gpl --enable-nonfree --enable-version3` 组合（实测 `-version`），
#      而仓里只有 LGPL 文本、全仓无第三方许可致谢件；
#   ② README/third_party README 都写过"零二进制入仓"，而 `.3fc_kernel_baseline.bundle`（2 371 867 B）
#      就在 `HEAD` 与 `origin/main` 的树里；
#   ③ `3fp/kernel/LICENSE.txt:3` 是未填的 MIT 模板（`Copyright (c) [year] [fullname]`），
#      所以"内核 MIT 已确认"那句当时是不可证伪的假陈述。
# 本步只干一件事：**把"发布时必须带着一份可核对的第三方清单"变成有牙的门**，
# 并让清单本身必须是**实测过**的（要求它含 nonfree 构建串），否则一份通用模板也能骗过"文件存在"这种判据。
# 占位符 LICENSE 只警告不拦：署名是上游作者的，我们代填等于伪造版权人。
Write-Host "`n[许可卫生] 第三方清单与自述" -ForegroundColor Yellow
$licFail = @()
$licWarn = @()
$notices = Join-Path $ProjectRoot "THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $notices)) {
    $licFail += "缺 THIRD-PARTY-NOTICES.md（随包分发第三方件却没清单 = 义务没落）"
} else {
    $ntxt = ""
    try { $ntxt = Get-Content -LiteralPath $notices -Raw -Encoding UTF8 } catch { $licFail += "THIRD-PARTY-NOTICES.md 读不到：$($_.Exception.Message)" }
    if ($ntxt.Length -lt 400) { $licFail += "THIRD-PARTY-NOTICES.md 只有 $($ntxt.Length) 字符，不像清单" }
    # 具名组件计数：枚举为空必须拦（一个具名组件都没有 = 清单是空壳）
    $named = @('FFmpeg', 'LakeUI', 'libass', 'Vortice') | Where-Object { $ntxt.Contains($_) }
    $namedCount = @($named).Count
    Write-Host "      具名组件 $namedCount/4：$(($named | ForEach-Object { $_ }) -join ', ')" -ForegroundColor Gray
    if ($namedCount -lt 3) { $licFail += "清单具名组件只有 $namedCount 项（<3），空壳嫌疑" }
    # 清单必须是实测的，不是通用模板：发货 FFmpeg 的构建串里有 nonfree，清单就必须提到它
    if (-not $ntxt.Contains('--enable-nonfree')) {
        $licFail += "清单未登记 `--enable-nonfree`（实测发货件构建串含之）——写成通用模板即判红"
    }
}
# LICENSE 占位符：只警告，并点名到行
$klic = Join-Path $ProjectRoot "3fp/kernel/LICENSE.txt"
if (-not (Test-Path -LiteralPath $klic)) {
    $licWarn += "找不到 3fp/kernel/LICENSE.txt（内核是独立 git 仓库，未检出时无从判断）"
} else {
    $klicLines = @(Get-Content -LiteralPath $klic -Encoding UTF8)
    $ph = @(); for ($i = 0; $i -lt $klicLines.Count; $i++) {
        if ($klicLines[$i] -match '\[year\]|\[fullname\]') { $ph += "第 $($i + 1) 行" }
    }
    if ($ph.Count -gt 0) {
        $licWarn += "3fp/kernel/LICENSE.txt 仍是未填模板（$($ph -join '、')）⇒ " +
                    "'内核按 MIT 授权'在本仓无可读回的凭据；README 已改为'许可未经确认'"
    }
}
$rootLic = @(Get-ChildItem -LiteralPath $ProjectRoot -Filter "LICENSE*" -File -ErrorAction SilentlyContinue)
if ($rootLic.Count -eq 0) { $licWarn += "主仓根目录无 LICENSE 文件（选哪种许可属对外承诺，待你定）" }
foreach ($w in $licWarn) { Write-Host "  ⚠ $w" -ForegroundColor Yellow; Write-Log "WARN  许可卫生  $w" }
Add-Result "许可卫生" ($licFail.Count -eq 0) `
    $(if ($licFail.Count -gt 0) { ($licFail -join '；') } else { "清单在位且登记了实测构建串；警告 $($licWarn.Count) 条（不拦）" })

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
if ($SkipProbeSetGate) {
    Write-Host "  ⚠ 显式降级：-SkipProbeSetGate —— 82 个探针未经扫编（本批有没有把探针弄坏属未判定）" -ForegroundColor Yellow
    Write-Log "DEGRADED  -SkipProbeSetGate：探针集未经扫编，本批是否弄坏探针属未判定"
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
