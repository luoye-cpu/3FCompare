# 3FP 发布打包
#
# 目的：把"GUI 与内核版本必须一致"从**人工记忆**变成**流水线步骤**。
#
# 背景：宿主与内核共用一个配置结构体，内核用
#   version == PlayerApiVersion  且  size >= sizeof(FFF3FPConfiguration)
# 双重校验。任一侧漂移，FFF3FP_Create 就返回 InvalidArgument，表现是
# "播放器起不来"。2026-10-07 实际发生过一次：宿主停在 API 16（80 字节），
# 内核已到 18（112 字节），播放器完全无法启动。
#
# 本脚本只做打包，不做修复：不一致就**中止**并列明该改哪里。
# 版本一致性由 FFF.Player.vbproj 里的两个 target 强制执行：
#   VerifyPlayerApiVersion   —— 内核 / 宿主常量 / 宿主发送值 / 工程声明 四者相等
#   VerifyPlayerConfigSize   —— 内核与宿主的配置结构体字段数相等
#
# 用法：
#   pwsh -File 3fp/tools/发布打包.ps1                 # 默认 Release / win-x64
#   pwsh -File 3fp/tools/发布打包.ps1 -Configuration Debug
#   pwsh -File 3fp/tools/发布打包.ps1 -SkipTests      # 跳过回归（不推荐）
#   pwsh -File 3fp/tools/发布打包.ps1 -LogPath logs\发布打包.txt
#       # 逐条落盘三道门禁（版本 / 布局 / 文档引用）的结论；格式与 tools/发布门禁.ps1
#       # 的 Write-Log 一致（`PASS|FAIL  名称  详情`，UTF-8 无 BOM 追加）。总验收 §5
#       # 要求"逐条落盘日志"，控制台输出在子进程/CI 里整段丢过一次，不能只靠它。
#
# 2026-10-08 RC-13（docs/67 第六节）三处自身失效已修，判据方向都是 **fail-closed**：
#   ① §0b/§0c 原先"缺脚本或缺 Python"只打印 `[--] 跳过` 就继续 ⇒ 文档门禁可被静默消音
#      （实测：$root 指到空目录时两条门禁同时消音、脚本照常走到打包）。现在缺依赖一律红。
#   ② 全脚本无日志参数，三门禁结论不落盘。现在 -LogPath 逐条落盘。
#   ③ Resolve-Python 原先可能命中 WindowsApps 的 App Execution Alias 占位桩
#      （本机实测 `python --version` 零输出 exit=9009），桩被当成解释器后 §0c 会**红得误导**
#      ——报"文档行号引用有硬失败"，实际那条引用根本没被检查过。现在按 Get-Command -All
#      跳过 WindowsApps 桩；万一只剩桩，红的是"解释器不可用"而不是"引用坏了"。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$SkipTests,
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # 3fp/
$kernelProject = Join-Path $root 'kernel\FFF.Native\FFF.Native.vcxproj'
$hostProject = Join-Path $root 'kernel\FFF.Player\FFF.Player.vbproj'
$dotnet = 'dotnet'

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Fail([string]$text) { Write-Host "  [!!] $text" -ForegroundColor Red }

# 落盘（照 tools/发布门禁.ps1:91-99 的 Write-Log 写法，不自创格式）：CI / 子进程调用时
# Write-Host 的输出经常整段丢失，只剩一个 exit=1，无法定位是哪一条门禁拦的。
function Write-Log([string]$line) {
    if (-not $LogPath) { return }
    try {
        $dir = Split-Path -Parent $LogPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [IO.File]::AppendAllText($LogPath, $line + "`n", (New-Object Text.UTF8Encoding $false))
    } catch { }
}
# 一条门禁 = 一行控制台（沿用本脚本的 [OK]/[!!] 样式）+ 一行日志（PASS/FAIL + 名称 + 详情）。
# 详情必须带上**两侧读数**（期望值 / 实测值、文件名原样），只写"不一致"等于没写。
function Add-Gate([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Ok $detail } else { Write-Fail $detail }
    Write-Log ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
    if (-not $ok) { Write-Log "ABORT  $name  发布中止" }
}
# 跑一条 python 门禁，并把失败**归因归对**（RC-13③）。三条纪律：
#   ① $ErrorActionPreference 临时降到 Continue：原生工具往 stderr 写进度是常态，
#      脚本级 Stop 会把 stderr 变成终止异常抛出（tools/发布门禁.ps1:108-137 同条经验），
#      那时门禁连结论行都写不出来，只剩一个 exit=1。
#   ② 先清 $LASTEXITCODE：它是"上一次原生调用"的残留值，进程压根没起来时会沿用 0 ⇒ 假绿。
#   ③ 非 0 退出 **且零输出** 是"解释器没跑成"（WindowsApps 占位桩 / 启动失败）的特征，
#      与"脚本判出硬失败"是两回事。把两者混成一条结论，就会把"根本没检查"报成"检查没过"。
function Invoke-PythonGate {
    param(
        [Parameter(Mandatory)] [string]$Exe,
        [Parameter(Mandatory)] [string]$Tool,
        [string]$Pattern = ''
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = $null
    $raw = & $Exe $Tool 2>&1 | Out-String
    $code = if ($null -eq $LASTEXITCODE) { -1 } else { $LASTEXITCODE }
    $ErrorActionPreference = $prev
    $lines = @(($raw -split "`r?`n") | Where-Object { $_ -and $_.Trim() -ne '' })
    $shown = if ($Pattern) { @($lines | Where-Object { $_ -match $Pattern }) } else { @($lines) }
    [pscustomobject]@{ Exit = $code; Lines = $shown; All = $lines; Empty = ($lines.Count -eq 0) }
}
if ($LogPath) {
    [IO.File]::WriteAllText($LogPath,
        "3FP 发布打包  cfg=$Configuration rid=$RuntimeIdentifier  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n",
        (New-Object Text.UTF8Encoding $false))
    Write-Log "LOGPATH  $LogPath"
}


# ── 0. 读取三处版本号，先给出人类可读的对照 ────────────────────────────────
Write-Step '版本一致性'
$apiCpp = Join-Path $root 'kernel\FFF.Native\3FP\Api\PlayerApi.cpp'
$sessionVb = Join-Path $root 'kernel\FFF.Player\Core\播放器会话.vb'
$projXml = Get-Content $hostProject -Raw

$kernelVersion = [regex]::Match((Get-Content $apiCpp -Raw), 'PlayerApiVersion\s*=\s*(\d+)').Groups[1].Value
$hostConst = [regex]::Match((Get-Content $sessionVb -Raw), '期望API版本\s+As\s+UInteger\s*=\s*(\d+)UI').Groups[1].Value
$projectVersion = [regex]::Match($projXml, '<PlayerApiVersion>(\d+)</PlayerApiVersion>').Groups[1].Value

if (-not $kernelVersion) { Add-Gate '版本一致性' $false "无法解析内核 PlayerApiVersion：$apiCpp"; exit 1 }
if (-not $hostConst) { Add-Gate '版本一致性' $false "无法解析宿主 期望API版本：$sessionVb"; exit 1 }
if (-not $projectVersion) { Add-Gate '版本一致性' $false "无法解析工程 <PlayerApiVersion>：$hostProject"; exit 1 }

Write-Host ("  内核={0}  宿主常量={1}  工程声明={2}" -f $kernelVersion, $hostConst, $projectVersion)
Write-Log "读数  kernel=$kernelVersion hostConst=$hostConst project=$projectVersion"

if ($kernelVersion -ne $projectVersion -or $hostConst -ne $projectVersion) {
    Add-Gate '版本一致性' $false ("内核=$kernelVersion / 宿主常量=$hostConst / 工程声明=$projectVersion 不相等，请同步：" +
        'kernel/FFF.Native/3FP/Api/PlayerApi.cpp PlayerApiVersion；' +
        'kernel/FFF.Player/Core/播放器会话.vb 期望API版本；' +
        'kernel/FFF.Player/FFF.Player.vbproj <PlayerApiVersion>')
    exit 1
}
Add-Gate '版本一致性' $true "版本一致（API $projectVersion；内核=$kernelVersion 宿主常量=$hostConst 工程声明=$projectVersion）"

# ── 0b. 快照结构体布局 ──────────────────────────────────────────────────────
# 版本号一致并不代表布局一致：字段顺序错**不会**编译报错，只会静默读出错位的值。
# 2026-10-07 实际发生过 —— 宿主把 IAMF 三字段追加到末尾，而 C 侧它们在第 26-28 位，
# 于是其后约 30 个字段全部偏移。此检查把"顺序错"变成发布阶段的硬失败。
Write-Step '快照结构体布局'
$layoutTool = Join-Path $root 'tools\校验快照布局.py'
# 解析 Python：PATH 上未必有 `python`，且"找得到"不等于"能跑"——同名命令可能有多个候选，
# 而 PATH 上排在前面的往往是 Windows Store 的 App Execution Alias **占位桩**（本机实测
# `Get-Command python` 首个候选 = %LOCALAPPDATA%\Microsoft\WindowsApps\python.exe，
# `--version` 零输出、exit=9009）。桩能被当成解释器交给 &，于是一条**根本没执行**的判据
# 会以"快照布局不一致 / 文档引用有硬失败"的面目报红（RC-13③）。规则照 tools/发布门禁.ps1:264-294：
#   ① 用 `Get-Command -All` 列出**全部**候选——不带 -All 时只给首个，"跳过桩"的循环是死代码；
#   ② 优先取**不在 WindowsApps 下**的第一个（门禁只需要"一个能跑的 python"，不该被占位桩挡住）；
#   ③ 候选全在 WindowsApps 下时仍取第一个并交给**实际执行**判定，绝不按路径硬判红，
#      但置一个 $script:PythonNote 提示位，让失败详情说清到底是哪一类失败。
function Resolve-Python {
    $cands = @(Get-Command python, python3, py -All -ErrorAction SilentlyContinue)
    foreach ($c in $cands) {
        $p = $c.Source; if (-not $p) { $p = $c.Definition }
        if ($p -and ($p -notmatch '(?i)[\\/]WindowsApps[\\/]')) { return $p }
    }
    if ($cands.Count -gt 0) {
        $p = $cands[0].Source; if (-not $p) { $p = $cands[0].Definition }
        return $p
    }
    # PATH 上一个候选都没有，再找已知安装位置（历史行为，保留在最后兜底）
    foreach ($p in @(
        'C:\Users\20210\AppData\Local\Programs\Python\Python312\python.exe')) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

$python = Resolve-Python
$script:PythonNote = ''
if ($python -and ($python -match '(?i)[\\/]WindowsApps[\\/]')) {
    $script:PythonNote = "；⚠ 解析到 Windows Store 应用执行别名（$python）——若它只是未安装 Python 时的占位桩，执行必然失败，请安装真实 Python 3 或在「设置 → 应用 → 高级应用设置 → 应用执行别名」中关闭 python.exe"
}
Write-Log "解释器  python=$python$script:PythonNote"

if (-not (Test-Path -LiteralPath $layoutTool)) {
    # RC-13①：这里原先打印 `[--] 未找到布局校验脚本，跳过` 就继续，与下面"缺 Python 即红"
    # 不对称——删掉/改名一个 .py 就能把整条布局门禁消音。缺依赖 = 本次发布没被检查过。
    Add-Gate '快照布局' $false ("缺少布局校验脚本：$layoutTool（fail-closed：字段顺序错不会编译报错，" +
        '只会静默读到错位的值，没跑等于没守卫）')
    exit 1
} elseif (-not $python) {
    Add-Gate '快照布局' $false ("找不到 Python 解释器，无法执行 $layoutTool$script:PythonNote" +
        '（该项不能跳过：字段顺序错不会编译报错，只会静默读到错位的值）')
    exit 1
} else {
    $layout = Invoke-PythonGate -Exe $python -Tool $layoutTool -Pattern '字段|sizeof|布局'
    foreach ($line in $layout.Lines) { Write-Host "  $line" }
    if ($layout.Empty) {
        Add-Gate '快照布局' $false ("解释器零输出（exit=$($layout.Exit)）：$python ——这是占位桩/未能启动的特征，" +
            "不是布局判据没过；本项实际未执行，不得当作已校验$script:PythonNote")
        exit 1
    } elseif ($layout.Exit -ne 0) {
        Add-Gate '快照布局' $false ("快照布局与 C 侧不一致（exit=$($layout.Exit)，详见上方列表）——" +
            '字段顺序错不会编译报错，只会静默读到错位的值，请按 C 侧顺序重排 原生播放器快照' +
            "$script:PythonNote")
        exit 1
    }
    Add-Gate '快照布局' $true ("快照布局一致（$((@($layout.Lines | Select-Object -First 4)) -join ' / ')）$script:PythonNote")
}

# ── 0c. 文档行号引用 ────────────────────────────────────────────────────────
# 文档里的 `Foo.cpp:245` 在代码改动后**不会报错**，只会静默让读者跳到错误的行、
# 读到无关代码并据此形成错误判断。2026-10-08 实际发生：docs/61 的 20 处引用
# 有 17 处整体漂移 +17 行，而后续实现者把它当成了当前事实。
# 这里只拦"硬失败"（源文件找不到、行号越界）；"指向无语义行"仅告警，
# 因为改一行注释就可能让某个合法引用落在空行上。
Write-Step '文档行号引用'
$refTool = Join-Path $root 'tools\校验文档行号引用.py'
# RC-13①：原来整条门禁挂在 `if ((Test-Path $refTool) -and $python)` 上，else 只打印
# `[--] 跳过（缺脚本或 Python）` 然后继续 ⇒ 与 §0b 的 fail-closed 不对称，删脚本或让
# PATH 里的 python 变成占位桩都能**静默消音**这条文档门禁。实测（改前）：$root 指向空树时
# 两条门禁同时消音、脚本一路走到打包；$python 钉成 WindowsApps 桩时报的是
# "文档行号引用有硬失败"，而那条引用根本没被读过。缺依赖一律红。
if (-not (Test-Path -LiteralPath $refTool)) {
    Add-Gate '文档行号引用' $false ("缺少文档引用校验脚本：$refTool（fail-closed：文档引用错了不会编译报错，" +
        '只会误导读者；脚本不在就等于这条门禁从没跑过）')
    exit 1
} elseif (-not $python) {
    Add-Gate '文档行号引用' $false ("找不到 Python 解释器，无法执行 $refTool$script:PythonNote")
    exit 1
} else {
    $ref = Invoke-PythonGate -Exe $python -Tool $refTool -Pattern '引用总数|可定位|找不到|越界|仓外|无语义'
    foreach ($line in $ref.Lines) { Write-Host "  $line" }
    if ($ref.Empty) {
        Add-Gate '文档行号引用' $false ("解释器零输出（exit=$($ref.Exit)）：$python ——本项实际未执行，" +
            "不能报成「文档行号引用有硬失败」$script:PythonNote")
        exit 1
    } elseif ($ref.Exit -ne 0) {
        Add-Gate '文档行号引用' $false ("文档行号引用有硬失败（源文件找不到或行号越界，exit=$($ref.Exit)，详见上方列表）——" +
            '这些引用不会导致编译失败，只会误导读者；建议改为"符号名 + 行号"形式，重构后仍可定位' +
            "$script:PythonNote")
        exit 1
    }
    Add-Gate '文档行号引用' $true ("文档行号引用无硬失败（$((@($ref.Lines | Select-Object -First 2)) -join ' / ')）$script:PythonNote")
}

# ── 1. 构建内核 ─────────────────────────────────────────────────────────────
Write-Step "构建内核（$Configuration / x64）"
$msbuild = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $msbuild) { Write-Fail '未找到 MSBuild'; exit 1 }

& $msbuild.FullName $kernelProject /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Fail '内核构建失败'; exit $LASTEXITCODE }
Write-Ok '内核构建通过'

$kernelDll = Join-Path $root "kernel\FFF.Native\x64\$Configuration\FFF.Native.dll"
if (-not (Test-Path $kernelDll)) { Write-Fail "未生成内核 DLL：$kernelDll"; exit 1 }

# ── 2. 回归（可选，但默认执行）───────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step '回归测试'
    $tests = Join-Path $root 'kernel\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe'
    if (Test-Path $tests) {
        $output = & $tests 2>&1 | Out-String
        if ($output -match 'Core checks passed') { Write-Ok 'smoke 通过' }
        else { Write-Fail 'smoke 失败'; Write-Host $output; exit 1 }
    } else {
        # 与 §0b/§0c 同形状的 fail-open，本轮改成拦得住：原先这里是
        # `[--] 未找到测试可执行文件，跳过` 然后一路走到打包 ⇒ smoke 从没跑过而发布照走。
        # 真要跳过请用显式的 -SkipTests（那时整步根本不出现），别让"找不到"冒充"跳过了"。
        Write-Fail "未找到回归测试可执行文件：$tests"
        Write-Log ('FAIL  回归测试  未找到可执行文件 {0}（fail-closed：整步从未执行；' +
            '如需跳过请显式使用 -SkipTests）' -f $tests)
        Write-Log 'ABORT  回归测试  发布中止'
        exit 1
    }
}

# ── 3. 发布宿主（两个校验 target 在此触发）──────────────────────────────────
Write-Step "发布宿主（$Configuration / $RuntimeIdentifier）"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "publish\$Configuration-$RuntimeIdentifier" }

& $dotnet publish $hostProject -c $Configuration -r $RuntimeIdentifier --self-contained false `
    -o $OutputDirectory --nologo 2>&1 | ForEach-Object {
        # 把校验 target 的输出显示出来，让"检查确实跑了"可见
        if ($_ -match '3FP API 版本|配置结构体字段数|发布中止') { Write-Host "  $_" }
        elseif ($_ -match 'error') { Write-Fail $_ }
    }
if ($LASTEXITCODE -ne 0) {
    Write-Fail '发布中止 —— 通常是 GUI 与内核版本/结构体不一致（见上方提示）'
    exit $LASTEXITCODE
}
Write-Ok '发布通过（版本与结构体校验均已执行）'

# ── 4. 确认产物里的内核 DLL 就是刚构建的那个 ────────────────────────────────
Write-Step '产物校验'
$published = Join-Path $OutputDirectory 'FFF.Native.dll'
if (-not (Test-Path $published)) {
    Write-Fail "产物里没有 FFF.Native.dll：$published"
    exit 1
}
$h1 = (Get-FileHash $kernelDll -Algorithm SHA256).Hash
$h2 = (Get-FileHash $published -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $published).Length / 1MB, 2)

if ($h1 -ne $h2) {
    Write-Fail '产物中的 FFF.Native.dll 与刚构建的内核不一致（可能用了陈旧副本）'
    Write-Host "  构建: $kernelDll"
    Write-Host "  产物: $published"
    exit 1
}
Write-Ok "FFF.Native.dll 一致（$sizeMb MB, SHA256 $($h1.Substring(0,16))…）"

# ── 5. 运行期依赖完整性 ─────────────────────────────────────────────────────
# 这一项不是形式主义：发布 target 曾经漏拷 @(DevelopmentRuntime)，导致 build 目录能跑、
# publish 出来的包缺少全部 FFmpeg DLL（少 113 MB、6 个文件），启动即失败。
Write-Step '运行期依赖'
$required = @(
    'avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll',
    'swresample-7.dll', 'swscale-10.dll', 'avfilter-12.dll',
    'ass-9.dll', 'bluray-3.dll'
)
$missing = @()
foreach ($name in $required) {
    $p = Join-Path $OutputDirectory $name
    if (Test-Path $p) { Write-Host ("  [OK] {0,-20} {1,7:N1} MB" -f $name, ((Get-Item $p).Length / 1MB)) }
    else { Write-Host ("  [!!] {0,-20} 缺失" -f $name) -ForegroundColor Red; $missing += $name }
}
if ($missing.Count -gt 0) {
    Write-Fail ("交付目录缺少 {0} 个运行期依赖：{1}" -f $missing.Count, ($missing -join ', '))
    Write-Host '  这些 DLL 在 kernel\runtime 与 third_party\vcpkg_installed 下；' -ForegroundColor DarkGray
    Write-Host '  若缺失，请检查 FFF.Player.vbproj 的 CopyProductNativeToPublish 是否拷贝了 @(DevelopmentRuntime)。' -ForegroundColor DarkGray
    exit 1
}
Write-Ok '运行期依赖完整'

# ── 6. 交付目录 ─────────────────────────────────────────────────────────────
Write-Step '交付目录'
$files = Get-ChildItem $OutputDirectory -File | Sort-Object Name
Write-Host ("  共 {0} 个文件，{1} MB" -f $files.Count, [math]::Round((($files | Measure-Object Length -Sum).Sum)/1MB, 1))
foreach ($name in 'FFF.Player.exe', 'FFF.Native.dll') {
    $f = $files | Where-Object Name -eq $name
    if ($f) { Write-Host ("    · {0,-20} {1,8:N2} MB  {2}" -f $name, ($f.Length/1MB), $f.LastWriteTime) }
}

Write-Host "`n发布完成：$OutputDirectory" -ForegroundColor Green
Write-Log "DONE  发布完成  $OutputDirectory"
Write-Host "提示：目标机需具备 VC++ 运行库（MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1）；" -ForegroundColor DarkGray
Write-Host "      d3d11/dxgi/d2d1/DWrite 由 Windows 提供。" -ForegroundColor DarkGray
exit 0
