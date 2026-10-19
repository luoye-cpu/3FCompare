# 3FCompare 构建与部署脚本
# 用法: powershell -ExecutionPolicy Bypass -File tools/构建全部.ps1 [-SkipTests] [-NoRunDirDeploy]
# 前置: Visual Studio 2022+ (C++ 桌面负载), Git, （vcpkg 首次联网）

param(
    [switch]$SkipTests,
    # 默认会把内核与原生依赖刷进本机运行目录 publish\3FCompare（见 §部署运行目录 的注释）。
    # 想只构建不碰那份正在跑的东西时加这个开关。
    [switch]$NoRunDirDeploy,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ForkRoot = Join-Path $ProjectRoot "third_party\fff_project"

# ---- 内核基线（改基线 = 改这里；必须同时是 PATCHES.md 里归档 tag 指向的提交）----
# 单一真源：tools/发布门禁.ps1 的 [1/11] 用正则从这里提取 $KernelBaselineSha，
# 不要在门禁里另抄一份字面量（S6 的收敛结论）。
#
# 2026-09-27 基线迁移到 b661365：= 上游 a87046e 的合并提交 a8e437e
#   + 方案A（RenderTargetInfo v2，destX/destY 改有符号；负原点对应放大平移时
#     被推到 back buffer 之外的目的框，v1 的 uint32 会静默吃掉一半平移量程）
#   + PATCHES.md 记账。归档 tag `3fcompare-kernel-2026.9.27.1`。
#   ⚠ 方案A **不改** PlayerApiVersion（仍是 15）：改的是输出结构的语义而非导出签名。
#     因此"版本号相等"拦不住 ABI 变化 —— PATCHES.md 〇 节记着上游自己也干过这种事。
#
# 2026-09-30 基线迁移到 3940990：= 上一基线 b661365 + 分支 `3fcompare/api16-kernel` 上的
#   两笔提交（f344c3c 落地 API 16 内核语义、3940990 把 D10 成员/探测/标识符记进 PATCHES.md §十三）。
#   来源是待上游合并的内核 PR，用 `git apply --3way` 移植；8 处冲突与 5 处本树特有对账逐条裁决，
#   重放件换为 tools/patches/0014-baseline-vs-a87046e-api16.patch（0013 退役入 history/）。
#   ⚠ 这一笔**确实升版本**：`sdrScRgbMode` 是 FFF3FPConfiguration 的字段新增 ⇒
#     PlayerApiVersion 15→16、结构体 80→84 B、导出面 86→87。上一基线那条"改布局不升版本"的教训
#     正是这里要防的反面，所以 `check_kernel_pairing.py` 现在按"恰好相等"拦，不再只查下限。
#   ⚠ 本脚本只**记录**内核实况（HEAD/脏文件/DLL 哈希写进 .3fc_kernel_build.json），
#     拦截由门禁 [1/11] 负责：脏内核可以照常构建（开发要能跑），但不能进发布包。
#
# 2026-10-08 基线推进到 API 17（HDR10+ 动态元数据）：
#   PlayerApiVersion 16→17 的原因与上面那条不同 —— 这次**没有**改 FFF3FPConfiguration，
#   而是给 FFF3FPSnapshot **尾部追加**了 5 个 ST 2094 诊断字段（+动态元数据注入 API 的 3 个导出）。
#   GetSnapshot 的判据是 `size < sizeof(FFF3FPSnapshot)` 就拒绝，所以托管侧必须同批加长；
#   两侧实测均为 368 B（见 docs/58 §11.5）。导出面 87→90。
#   回滚点：3fcompare-kernel-2026.9.30.1 / 394099019aaff37c4b6ce482c2e835527e5b124a
#
# 2026-10-20 基线 = 上游 578f80b（PR #31）本身。出货树此前那 2 笔本地提交不再保留：逐行对撞后
#   其 187 行新增有 181 行上游已有，剩 6 行是注释措辞与换行。归档 tag 3fcompare-kernel-2026.10.20.1
#   就指这个提交；两棵树与上游同一 SHA，构建/被测的就是上游内容，没有任何本地分叉。
#
# 2026-10-17 0014 退役入 tools/patches/history/：
#   工作树已换到新谱系（upstream-sync，HEAD 不含钉死基线 d2f5826），0014 描述的是旧谱系
#   （3fcompare/api16-kernel vs a87046e）的分歧，正向、反向 --check 都过不去 ⇒ 每次构建都在
#   补丁段中止。按下方 throw 文案自身的处置指引移入 history/（非递归枚举，不再参与重放）。
#   现行树构建不依赖任何顶层补丁；下一条分歧出现时按 patches/README.md 的规范顺延编号。
$KernelRepo        = "https://github.com/Lake1059/FFF_Project.git"
$KernelBaselineTag = "3fcompare-kernel-2026.10.20.1"
$KernelBaselineSha = "578f80b6ce8603aa64b746c3e394146cf011f0d2"
# 上一基线（回滚点）：3fcompare-kernel-2026.9.30.1 / 394099019aaff37c4b6ce482c2e835527e5b124a
# 3FCompare 扩展的云端归档：上游 Lake1059/FFF_Project **不含**这些扩展，
# 且本机账号对它只有读权限，所以基线归档只能落在主仓库自己的仓库里。
$KernelArchiveRepo   = "https://github.com/luoye-cpu/3FCompare.git"
$KernelArchiveBranch = "kernel/3fcompare-zoom-viewport-cover"

function Invoke-Git {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$GitArgs
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = & git -C $Path @GitArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{
        Exit = $code
        Out  = (($raw | ForEach-Object { $_.ToString() }) -join "`n").Trim()
    }
}

function Get-FileSha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "" }
    $stream = [IO.File]::OpenRead($path)
    try {
        $hash = [Security.Cryptography.SHA256]::Create().ComputeHash($stream)
        return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    } finally { $stream.Dispose() }
}

# 内核此刻的实况：HEAD、是否脏、HEAD 上有没有归档 tag。
# 刻意**不 checkout**：本脚本走"增量补丁应用到工作树"的路子（见上方补丁段），
# 自动检出基线会把用户未提交的内核工作直接掀掉——HEAD 版本可以这么做是因为它
# 那时内核总是干净的。核对与拦截交给门禁，构建侧只负责如实记录。
function Get-KernelState {
    $head = ""
    $r = Invoke-Git $ForkRoot @("rev-parse", "HEAD")
    if ($r.Exit -eq 0) { $head = $r.Out }
    $dirty = ""
    $s = Invoke-Git $ForkRoot @("status", "--porcelain")
    if ($s.Exit -eq 0) { $dirty = $s.Out }
    $tag = ""
    $t = Invoke-Git $ForkRoot @("tag", "--points-at", "HEAD")
    if ($t.Exit -eq 0 -and $t.Out) { $tag = ($t.Out -split "`n")[0].Trim() }
    return [pscustomobject]@{
        Head    = $head
        Dirty   = ($dirty -ne "")
        DirtyList = @($dirty -split "`n" | Where-Object { $_ -ne "" })
        Tag     = $tag
    }
}

function Get-MSBuildPath {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($vs) {
            $candidates = @(
                (Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"),
                (Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe")
            )
            foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
        }
    }
    throw "未找到 MSBuild（需要 Visual Studio 的 MSBuild 组件）"
}

if (-not (Test-Path (Join-Path $ForkRoot "FFF.Native\FFF.Native.vcxproj"))) {
    Write-Host "内核 submodule 未初始化，正在拉取..."
    Push-Location $ProjectRoot
    git submodule update --init --recursive
    Pop-Location
}

# 应用自定义补丁（增量策略：只应用比标记更新或未被跟踪的补丁，见 patches/README.md）
#
# ⚠ 这里原来有个真 bug：注释写着"已应用的补丁视为成功"，代码里**却没有这条判断**——
#   直接 `git apply` 后看退出码。于是当补丁描述的分歧已经被合进内核（今天的 0013 就是），
#   正向 apply 失败 ⇒ 标记不刷新 ⇒ 每次构建都重演一次失败；更糟的是 $ErrorActionPreference=Stop
#   下 git 往 stderr 写字就抛，整个构建在第一步就中止（2026-09-27 实跑撞上的就是这个）。
#   现在按三态判：正向 --check 能过 ⇒ 应用；反向 --check 能过 ⇒ **已在树中**，算满足不动它；
#   两边都过不去 ⇒ 真冲突，判红停下（半补丁的内核构建出来比不构建更坏）。
#   原生命令一律走 Invoke-Git，避免 stderr 把 Stop 变成异常。
$PatchesDir = Join-Path $PSScriptRoot "patches"
$PatchMarker = Join-Path $ForkRoot ".3fc_patches_applied"
if (Test-Path $PatchesDir) {
    $markerTime = if (Test-Path $PatchMarker) { (Get-Item $PatchMarker).LastWriteTime } else { [DateTime]::MinValue }
    $toApply = @(Get-ChildItem $PatchesDir -Filter *.patch | Where-Object { $_.LastWriteTime -gt $markerTime } | Sort-Object Name)
    if ($toApply.Count -gt 0) {
        Write-Host "处理 3FCompare 自定义补丁（增量 $($toApply.Count) 个）..."
        $pending = @()
        foreach ($p in $toApply) {
            $fwd = Invoke-Git $ForkRoot @("apply", "--check", "--ignore-whitespace", $p.FullName)
            if ($fwd.Exit -ne 0) {
                # 反向能应用 == 这份补丁的效果已经在树里（已合并/已应用），不是冲突
                $rev = Invoke-Git $ForkRoot @("apply", "--check", "-R", "--ignore-whitespace", $p.FullName)
                if ($rev.Exit -eq 0) {
                    Write-Host "  ✓ $($p.Name) 已在树中（反向 --check 通过），不重复应用" -ForegroundColor Green
                    continue
                }
                Write-Host "  ✗ $($p.Name) 既不正向可应用、也不已在树中 ⇒ 真冲突" -ForegroundColor Red
                Write-Host "    正向：$($fwd.Out)" -ForegroundColor DarkGray
                $pending += $p.Name
                continue
            }
            $ap = Invoke-Git $ForkRoot @("apply", "--ignore-whitespace", $p.FullName)
            if ($ap.Exit -eq 0) { Write-Host "  ✅ $($p.Name) 已应用" }
            else { Write-Host "  ✗ $($p.Name) --check 通过但应用失败：$($ap.Out)" -ForegroundColor Red; $pending += $p.Name }
        }
        if ($pending.Count -gt 0) {
            # 不刷新标记：下次重跑还会尝试同一批。宁可停下，也不要拿半补丁的内核去构建。
            throw @"
以下内核补丁无法应用、也不在树中：$($pending -join ', ')
构建已中止（内核未改动）。请人工核对 third_party/fff_project 的 HEAD 与 tools/patches/ 的假设是否对得上：
  - 补丁描述的分歧若已被合并 ⇒ 把该补丁移进 tools/patches/history/（它只是记录，不再是待应用项）
  - 若确实还没应用 ⇒ 逐个人工 git apply --3way 解冲突，并在 PATCHES.md 记一笔
"@
        }
        # 全部处理完（应用了或确认已在树中）才刷新标记
        New-Item -ItemType File -Path $PatchMarker -Force | Out-Null
        Write-Host "✅ 补丁全部处理完毕，标记已刷新"
    } else {
        Write-Host "补丁均为最新（标记时间 $(Get-Date $markerTime -Format 'yyyy-MM-dd HH:mm')），跳过"
    }
}

Write-Host "=== [1/4] 准备 FFmpeg（若缺失） ==="
$ffmpegMarker = Join-Path $ForkRoot "third_party\ffmpeg\include\libavcodec\avcodec.h"
if (-not (Test-Path $ffmpegMarker)) {
    Push-Location $ForkRoot
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\准备FFmpeg.ps1"
    # 补齐上游脚本偶发遗漏的生成头
    $cache = Join-Path $env:LOCALAPPDATA "fff-ffmpeg-download\extracted\ffmpeg-master-latest-win64-lgpl-shared\include"
    if (Test-Path (Join-Path $cache "libavutil\avconfig.h")) {
        Copy-Item (Join-Path $cache "libavutil\avconfig.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force
        Copy-Item (Join-Path $cache "libavutil\ffversion.h") (Join-Path $ForkRoot "third_party\ffmpeg\include\libavutil\") -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
} else {
    Write-Host "  FFmpeg 已就绪，跳过"
}

Write-Host "=== [2/4] 准备 libass（若缺失） ==="
$assMarker = Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\include\ass\ass.h"
if (-not (Test-Path $assMarker)) {
    Push-Location $ForkRoot
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\准备Libass.ps1"
    Pop-Location
} else {
    Write-Host "  libass 已就绪，跳过"
}

Write-Host "=== [3/4] 构建 FFF.Native ==="
# 在动手编译**之前**取内核实况：编译过程不改 git 状态，但脚本尾部再取会漏掉
# "构建期间有人往内核目录写东西"这类情况——越早取越接近"这份 DLL 是从哪个树编出来的"。
$KernelState = Get-KernelState
if ($KernelState.Dirty) {
    Write-Host "⚠ 内核工作树脏（$($KernelState.DirtyList.Count) 个文件），构建出的 DLL 不是基线提交本身：" -ForegroundColor Yellow
    foreach ($d in $KernelState.DirtyList) { Write-Host "    $d" -ForegroundColor Yellow }
    Write-Host "  本脚本照常构建（开发要能跑），但发布门禁 [1/11] 会因此判红，直到这些改动被提交。" -ForegroundColor Yellow
}
$msbuild = Get-MSBuildPath
Push-Location $ForkRoot
& $msbuild "FFF.Native\FFF.Native.vcxproj" /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "FFF.Native 构建失败" }
Pop-Location

Write-Host "=== [4/4] 部署 DLL 到应用与冒烟目录 ==="
function Deploy-To($targetDir) {
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item (Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll") $targetDir -Force
    Get-ChildItem (Join-Path $ForkRoot "runtime\*.dll") | ForEach-Object { Copy-Item $_.FullName $targetDir -Force }
    $assDll = Get-ChildItem (Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\bin\ass-9.dll") -ErrorAction SilentlyContinue
    if ($assDll) { Copy-Item $assDll.FullName $targetDir -Force }
    Write-Host "  已部署 -> $targetDir"
}

Deploy-To (Join-Path $ProjectRoot "src\3FCompare\bin\$Configuration\net11.0-windows")
Deploy-To (Join-Path $ProjectRoot "tests\3FCompare.SmokeTests\bin\$Configuration\net11.0")

# ── 部署运行目录（2026-10-13 补，此前这一步根本不存在）──────────────────────
# 为什么非做不可：`publish\3FCompare` 是本机**真正跑起来**的那份（同目录有 settings.json、
# logs、debug.log），而 `NativeRuntime.ExtractEmbeddedDll` 的既定策略是"磁盘上已有 FFF.Native.dll
# 就绝不覆盖"（该方法注释记着 2026-09-16 实测：按"哈希不同就覆盖"把用户磁盘上刚构建的 API 15
# 降级成内嵌的 API 14，而托管 ConfigVersion=15、内核对版本严格相等 ⇒ 所有会话创建失败）。
# ⇒ **运行期加载的是这个目录里散落的 FFF.Native.dll，exe 内嵌那份只是缺失兜底、且通常更旧**。
# 所以"改动有没有到货"的闸口是这个散 DLL；本脚本过去只部署两个 bin 目录，这里从来没人写，
# 实测它停在 10-11 的 6,180,352 B / API 17，而新构建是 5,398,016 B / API 20。
# 边界：只写内核与原生依赖，**绝不碰** settings.json / logs / debug.log / selftest_output.txt；
# 目录不存在时**不新建**（那里是运行时状态，凭空造一份没有意义）；进程在跑时**跳过并说明**，
# 不是静默跳过——"内核没刷进去"必须留下能归因的一行。
$RunDir = Join-Path $ProjectRoot "publish\3FCompare"
$KernelDllSrc = Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll"
function Get-ShaShort([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "缺失" }
    try { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant() }
    catch { "读不到" }
}
if ($NoRunDirDeploy) {
    Write-Host "  [跳过] 运行目录部署（-NoRunDirDeploy）：publish\3FCompare 里的内核仍是旧版，属已知状态"
} elseif (-not (Test-Path -LiteralPath $RunDir -PathType Container)) {
    Write-Host "  [跳过] 运行目录不存在：$RunDir（不新建——那里是运行时状态目录）" -ForegroundColor Yellow
} elseif (-not (Test-Path -LiteralPath $KernelDllSrc)) {
    throw "内核 DLL 不存在：$KernelDllSrc"
} else {
    $running = @(Get-Process -Name "3FCompare" -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        # 覆盖一个正在被加载的 DLL 要么失败要么让进程读到混合状态；宁可红着提醒。
        Write-Host ("  [跳过] 3FCompare 正在运行（PID {0}），不刷运行目录 —— 关掉它再重跑本脚本，" -f ($running.Id -join ',')) -ForegroundColor Yellow
        Write-Host "         否则本轮改动不会生效，而这里会看起来成功了。" -ForegroundColor Yellow
    } else {
        # ⚠ 版本配对闸：内核对 PlayerApiVersion 做**严格相等**判断（FFF3FP_Create 不等即拒），
        # 而运行目录里的 3FCompare.exe 是**上一次发布**的产物、带着那一版的托管镜像。
        # 只把新内核放进去 = "新内核 + 旧托管镜像"，会话全部创建失败。
        # `NativeRuntime.ExtractEmbeddedDll` 的注释记着反方向的同一件事（2026-09-16：
        # 按"哈希不同就覆盖"把磁盘上的 API 15 降级成内嵌的 API 14，表现是"未就绪/找不到入口点"）。
        # 所以这里要求**成对到货**：exe 必须不早于本轮内核，否则跳过并说明要去重跑发布。
        $exePath = Join-Path $RunDir "3FCompare.exe"
        if (-not (Test-Path -LiteralPath $exePath)) {
            Write-Host "  [跳过] 运行目录里没有 3FCompare.exe，无法判断托管镜像版本，不单独放内核" -ForegroundColor Yellow
        } elseif ((Get-Item $exePath).LastWriteTimeUtc -lt (Get-Item $KernelDllSrc).LastWriteTimeUtc) {
            Write-Host ("  [跳过] 运行目录的 exe（{0}）早于本轮内核（{1}）：只刷内核会造出" -f `
                (Get-Item $exePath).LastWriteTime.ToString('MM-dd HH:mm'), `
                (Get-Item $KernelDllSrc).LastWriteTime.ToString('MM-dd HH:mm')) -ForegroundColor Yellow
            Write-Host "         『新内核 + 旧托管镜像』，而内核对 API 版本严格相等 ⇒ 会话全建不起来。" -ForegroundColor Yellow
            Write-Host "         要成对到货：先重新发布 exe（pack.ps1 的 dotnet publish），再跑本脚本。"
            Write-Host ("  现状：运行目录 FFF.Native.dll {0} B / exe {1} B" -f `
                (Get-Item (Join-Path $RunDir "FFF.Native.dll")).Length, (Get-Item $exePath).Length)
        } else {
        Write-Host "=== 部署运行目录 $RunDir ==="
        $before = Get-ShaShort (Join-Path $RunDir "FFF.Native.dll")
        $beforeBytes = (Get-Item (Join-Path $RunDir "FFF.Native.dll")).Length
        Copy-Item -LiteralPath $KernelDllSrc (Join-Path $RunDir "FFF.Native.dll") -Force
        foreach ($rt in (Get-ChildItem (Join-Path $ForkRoot "runtime\*.dll") -ErrorAction SilentlyContinue)) {
            Copy-Item -LiteralPath $rt.FullName -Destination $RunDir -Force
        }
        $runAss = Join-Path $ForkRoot "third_party\vcpkg_installed\x64-windows\bin\ass-9.dll"
        if (Test-Path -LiteralPath $runAss) { Copy-Item -LiteralPath $runAss -Destination $RunDir -Force }
        $after = Get-ShaShort (Join-Path $RunDir "FFF.Native.dll")
        $built = Get-ShaShort $KernelDllSrc
        # 部署后自证：运行目录那份必须与刚构建的那份同哈希，否则这行就是红的来源。
        if ($after -ne $built) { throw "运行目录内核哈希 $after ≠ 构建产物 $built —— 部署没真正落地" }
        Write-Host ("  FFF.Native.dll sha256 {0} -> {1}（构建产物 {2}）" -f $before, $after, $built)
        Write-Host ("  字节 {0} -> {1}" -f $beforeBytes, (Get-Item $KernelDllSrc).Length)
        # 内嵌件与散件的差异是**设计如此**（散件优先、内嵌只兜底），所以这里只报不拦。
        Write-Host ("  注：{0} 内嵌的仍是发布那批的兜底内核；运行期用的是上面这份散 DLL。" -f (Split-Path $exePath -Leaf))
        Write-Host "      要让内嵌件也变新，得重跑 pack.ps1 重新发布 exe——那是发布动作，不在本脚本职责内。"
        }
    }
}

if (-not $SkipTests) {
    Write-Host "=== [可选] 构建 .NET 解决方案 ==="
    Push-Location $ProjectRoot
    dotnet build .\src\3FCompare.slnx -c $Configuration --nologo
    Pop-Location
}

Write-Host "=== 写内核构建清单（发布门禁 [1/11] 的输入）==="
# 只在**成功路径**写：写在前面会让"构建失败但清单已更新"变成假绿（HEAD 版本踩过这个坑，
# 见其注释"项目根的 .3fc_kernel_sha 不能在这里写"）。
# 清单里记的是"这份 DLL 从哪个树编出来、哈希是多少"——门禁据此复核磁盘上那份有没有被换过
# （今天真实发生过：Debug bin 里躺着 09-21 的旧内核，跑出来的判据全是假红）。
$KernelDllPath = Join-Path $ForkRoot "FFF.Native\x64\$Configuration\FFF.Native.dll"
$manifest = [ordered]@{
    head          = $KernelState.Head
    baseline      = $KernelBaselineSha
    baselineTag   = $KernelBaselineTag
    kernelTag     = $KernelState.Tag
    dirty         = [bool]$KernelState.Dirty
    dirtyFiles    = @($KernelState.DirtyList)
    configuration = $Configuration
    kernelDll     = $KernelDllPath
    dllSha256     = (Get-FileSha256 $KernelDllPath)
    builtAt       = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
}
$manifestPath = Join-Path $ProjectRoot ".3fc_kernel_build.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
# 兼容字段：门禁与老脚本仍会读 .3fc_kernel_sha
Set-Content -Path (Join-Path $ProjectRoot ".3fc_kernel_sha") -Value $KernelState.Head -Encoding ASCII
if (-not $manifest.dllSha256) {
    Write-Host "⚠ 清单里 dllSha256 为空：$KernelDllPath 不存在，门禁 [1/11] 会判红" -ForegroundColor Yellow
}
Write-Host "  清单 -> $manifestPath（head $($KernelState.Head.Substring(0,[Math]::Min(12,$KernelState.Head.Length))) dirty=$($manifest.dirty)）"

Write-Host "✔ 全部完成。运行冒烟: dotnet run --project tests/3FCompare.SmokeTests -- <视频>"
Write-Host "✔ 运行应用: dotnet run --project src/3FCompare"