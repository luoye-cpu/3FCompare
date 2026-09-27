# 3FCompare 打包脚本 — 2 版本发布（全部 NativeAOT 编译）
# 3FCompare Build & Pack Script — 2 variants (all NativeAOT)
# 用法 / Usage:
#   精简版 (无 FFmpeg) / Lite (no FFmpeg):        .\pack.ps1 -Mode app
#   完整版 (含 ffmpeg-full) / Full (with ffmpeg-full): .\pack.ps1 -Mode full
#   一键全部 2 个版本 / Both variants:         .\pack.ps1 -Mode all (default)
#
#   只校验一个已存在的目录（跳过单测/编译/压缩，秒级完成）/ Verify an existing dir only
#   (skips unit tests / publish / compression):
#       .\pack.ps1 -ChecksOnly -CheckDir publish\build\3FCompare-v0.2.5-x64 -Mode app
#       .\pack.ps1 -ChecksOnly -CheckDir <目录> -Mode full
#   同上，但先按当前目录内容重算 SHA256SUMS 再校验 / Same, but regenerate SHA256SUMS first:
#       .\pack.ps1 -ChecksOnly -CheckDir <目录> -Mode full -WriteManifest
#   ⚠ -ChecksOnly 必须显式给 -Mode app|full（不接受 all：否则"完整版缺 FFmpeg"会被静默跳过）。
#     -ChecksOnly requires an explicit -Mode app|full ("all" would silently skip the FFmpeg check).
#
#   跳过包内自测（会弹 GUI 窗口，默认开启）/ Skip in-package self-test (opens a GUI window):
#       .\pack.ps1 -Mode all -SkipPackageSelfTest
#   ⚠ 这是**显式降级**，不是"跳过不判"：发布门禁 tools/发布门禁.ps1 已把包内自测收编为
#     不可跳过项 [11/11]（门禁会自己拿 publish 出的 AOT exe 跑 --selftest，不再依赖 -WithPack）。
#     在门禁里传 -SkipPackageSelfTest 会让该项判红、汇总里单列、退出码非 0。
#     本开关只影响 pack.ps1 **单独运行**时的行为（包仍会打出来，但未经 --selftest 验证）。
#     This is an explicit degradation — the release gate runs this test as a non-skippable
#     item and reports a FAILED item with exit≠0 when it is skipped there.
#   指定自测素材（默认自动取 testmedia\media\real\ 下的 4K 真实素材）/ Override self-test media:
#       .\pack.ps1 -SelfTestMedia <绝对路径>
#
#   版本号唯一真源 = src/3FCompare/3FCompare.csproj 的 <Version>（+<VersionSuffix>）。
#   不传 -Version 时自动取 csproj；显式传入且与 csproj 不一致会告警（防误发旧包）。
param(
    [string]$Version = "",
    [ValidateSet("app", "full", "all")]
    [string]$Mode = "all",
    [switch]$NoCompress,          # 跳过 7z 压缩（调试时快速验证打包逻辑）/ Skip 7z compression (debug)
    [switch]$SkipPackageSelfTest, # 显式降级：跳过包内 --selftest（慢且会弹 GUI）。发布门禁里该项不可跳过
    [string]$SelfTestMedia = "",  # 自测素材；留空自动取 testmedia\media\real\ 的 4K 真实素材
    [switch]$ChecksOnly,          # 只跑产物校验，对 -CheckDir 生效（不单测/不编译/不压缩）
    [string]$CheckDir = "",       # -ChecksOnly 要校验的已存在目录
    [switch]$WriteManifest,       # 配合 -ChecksOnly：先重算 SHA256SUMS 再校验
    [string]$BuildDate = ""       # 写进使用说明的构建日期；留空则不写该项（保证包内文本文件可复现）
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = "$ProjectDir\publish"
$BuildDir = "$ProjectDir\publish\build"
$AppProject = "$ProjectDir\src\3FCompare\3FCompare.csproj"
$PlanSource = "$PublishDir\PLAN"

# ── 版本号：默认从 csproj 读（唯一真源） ──
# 历史问题：0.2.0 硬编码在 pack.ps1 / 发布门禁.ps1 / 两个 csproj 四处，
# 出现过"本地打过 0.2.1 但版本叙事里没有它"的情况，极易误发旧包。
function Read-CsprojVersion([string]$CsprojPath) {
    if (-not (Test-Path -LiteralPath $CsprojPath)) { return $null }
    $text = Get-Content -Raw -LiteralPath $CsprojPath
    $v = [regex]::Match($text, '<Version>\s*([^<]+)\s*</Version>')
    if (-not $v.Success) { return $null }
    $s = [regex]::Match($text, '<VersionSuffix>\s*([^<]+)\s*</VersionSuffix>')
    return [pscustomobject]@{
        Version = $v.Groups[1].Value.Trim()
        Suffix  = if ($s.Success) { $s.Groups[1].Value.Trim() } else { "" }
    }
}

$csprojVer = Read-CsprojVersion $AppProject
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (-not $csprojVer) { throw "无法从 $AppProject 读取 <Version>，请显式传 -Version" }
    $Version = $csprojVer.Version
    Write-Host "版本取自 csproj（唯一真源）: $Version" -NoNewline -ForegroundColor Gray
    if ($csprojVer.Suffix) { Write-Host "  后缀: $($csprojVer.Suffix)" -ForegroundColor Gray } else { Write-Host "" }
} elseif ($csprojVer -and $Version -ne $csprojVer.Version) {
    Write-Warning "传入版本 '$Version' 与 csproj 的 '$($csprojVer.Version)' 不一致——确认是在打旧版本吗？"
}

# 本机 dotnet restore 全域失败：沙箱/部分终端里 APPDATA 为空，
# NuGet 读资产文件时 Path.Combine 拿到 null，报 "Value cannot be null. (Parameter 'path1')"。
# 对策：补 APPDATA + dotnet 调用一律 --no-restore（依赖需提前 restore 好）。
if (-not $env:APPDATA) {
    Write-Host "环境缺少 APPDATA，兜底设置为默认用户目录 / APPDATA missing, fallback to default" -ForegroundColor Yellow
    $env:APPDATA = Join-Path $env:USERPROFILE "AppData\Roaming"
}
# dotnet 未必在 PowerShell 的 PATH 上；用裸命令时 $LASTEXITCODE 可能沿用旧值而误判成功。
$Dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($Dotnet) { $Dotnet = $Dotnet.Source }
elseif (Test-Path "C:\Program Files\dotnet\dotnet.exe") { $Dotnet = "C:\Program Files\dotnet\dotnet.exe" }
else { throw "未找到 dotnet / dotnet not found. 请安装 .NET SDK 或将其加入 PATH。" }

# 架构
$Arch = "x64"
$Rid = "win-x64"

# ── 原生命令包装 ──
# 两个必须解决的问题（tools\发布门禁.ps1:93 是正确样板）：
#  1) $ErrorActionPreference='Stop' 下，原生命令往 stderr 写内容会被 PowerShell 5.1 当成
#     终止错误抛出——而 dotnet / python / 7z 往 stderr 写进度是常态 ⇒ 脚本会在无关的地方崩。
#  2) $LASTEXITCODE 是"上一次原生调用"的残留值。若本次进程压根没起来（PATH 缺失/沙箱禁止起
#     进程），它会沿用上一次成功调用的 0 ⇒ 假绿。所以必须先清空，并把"没起来"归一成 -1。
function Invoke-Cmd {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        # 长耗时构建（NativeAOT publish）用：实时透传到控制台，同时收集输出供失败时打印尾部
        [switch]$Stream
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = $null
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Stream) {
        & $FilePath @Arguments 2>&1 | ForEach-Object {
            $s = $_.ToString()
            $lines.Add($s)
            Write-Host $s
        }
    } else {
        & $FilePath @Arguments 2>&1 | ForEach-Object { $lines.Add($_.ToString()) }
    }
    $code = if ($null -eq $LASTEXITCODE) { -1 } else { $LASTEXITCODE }
    $ErrorActionPreference = $prev
    return [pscustomobject]@{ Exit = $code; Out = @($lines) }
}

# ── Windows 命令行引用（仅 PS 5.1 兜底路径用）──
# .NET Core / pwsh7 的 ProcessStartInfo 有 ArgumentList：逐项传参，天然无引号注入。
# 而 Windows PowerShell 5.1 跑在 .NET Framework 上**没有**该属性，只能给 Arguments 字符串。
# 此时若像旧代码那样裸拼（'... -mmt=1 "' + $path + '" *'），$path 里的引号会把后面的内容
# 顶出引号范围、被当成额外参数（$Version 未做字符集校验，是真实注入面）。
# 本函数按 Windows 的 argv 解析规则引用：含空格/制表/引号才加引号；反斜杠**仅在其后紧跟
# 引号时**才加倍（无条件加倍会让 7z 收到的路径多出反斜杠）。
function ConvertTo-CommandLineArg([string]$Arg) {
    if ($Arg -ne "" -and $Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $bs = 0
    foreach ($ch in $Arg.ToCharArray()) {
        if ($ch -eq '\') { $bs++; continue }
        if ($ch -eq '"') {
            [void]$sb.Append('\', $bs * 2 + 1)   # 引号前：反斜杠加倍 + 1 个用于转义引号本身
            [void]$sb.Append('"')
        } else {
            if ($bs -gt 0) { [void]$sb.Append('\', $bs) }
            [void]$sb.Append($ch)
        }
        $bs = 0
    }
    if ($bs -gt 0) { [void]$sb.Append('\', $bs * 2) }   # 结尾反斜杠必须加倍，否则会转义收尾引号
    [void]$sb.Append('"')
    return $sb.ToString()
}

# ── 包命名 ──
function Get-PackageName([string]$mode) {
    switch ($mode) {
        "app"  { return "3FCompare-v$Version-$Arch" }
        "full" { return "3FCompare-v$Version-$Arch-full" }
    }
    throw "未知模式: $mode / Unknown mode: $mode"
}

# ── 哈希清单（SHA256SUMS）──
# 目的：包内每个文件的 sha256 落到一个清单里，用户解压后可自行核对"拿到的包没被改过/没坏"。
# 格式与 GNU coreutils 的 `sha256sum` 一致：`<64位小写hex><两空格><相对路径>`（便于直接
# `sha256sum -c SHA256SUMS` 复核）。清单**先于压缩生成**，因此它本身也在压缩包内。
# ⚠ 下面三个函数都用 "$f.FullName.Substring($Dir.Length)" 算相对路径。
#   若 $Dir 是**相对路径**而 FullName 是绝对的，Substring 会切在错误位置 ——
#   实测后果是 Remove-ExtraneousArtifacts 把整个产物目录清空（2026-09-21 亲手踩到）。
#   故统一先规范化成绝对路径；末尾分隔符也会让长度对不上，一并 Trim。
function Resolve-Dir([string]$Dir) {
    return [IO.Path]::GetFullPath($Dir).TrimEnd('\')
}

# 清单里的相对路径一律用**正斜杠**（GNU `sha256sum -c` 的标准格式）。
# 用反斜杠时 GNU coreutils 会把 `\` 当转义/文件名的一部分 ⇒ `sha256sum -c` 复核必失败，
# 而清单存在的唯一目的就是"用户能自行复核"。脚本内部比对时再转回本机分隔符。
# ⚠ 只写正斜杠却忘了在读侧转回来，收敛/校验就会把所有条目判成"多余"——两侧必须成对改。
function Convert-ManifestPathToLocal([string]$Rel) {
    return ($Rel -replace '/', '\')
}

function New-Sha256Manifest([string]$Dir) {
    $Dir = Resolve-Dir $Dir
    $manifest = Join-Path $Dir "SHA256SUMS"
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
    # 先枚举再写：清单自身不参与枚举 ⇒ 不会出现"清单里有自己"的循环
    $files = @(Get-ChildItem $Dir -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $files) {
        $rel = ($f.FullName.Substring($Dir.Length).TrimStart('\')).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $f.FullName).Hash.ToLower()
        [void]$sb.AppendLine("$hash  $rel")
    }
    [IO.File]::WriteAllText($manifest, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
    return $files.Count
}

# 把产物目录收敛回"发货内容"——即 SHA256SUMS 里列的那几项。
#
# 为什么需要：包内自测（Invoke-PackageSelfTest）会真跑一次 exe，而 exe 运行必然产生
# 三类**不属于发货内容**的副产物：
#   · logs\           —— 内核/组件日志落盘在 exe 同目录，且**含本机绝对路径**（信息泄露）；
#   · FFF.Native.dll   —— 内核 DLL 是 EmbeddedResource，首次运行才解压到磁盘；
#   · 3FC.WgcCapture.dll / libSkiaSharp.dll / libHarfBuzzSharp.dll —— 同为运行时解压。
# 实测（2026-09-21，-Mode full）：清单 16 项，自测后目录变成 22 项，多出的 6 项正是
# 上述内容 ⇒ 目录不再等于发货内容，再用 -ChecksOnly 复检会判红，日志还可能被误收进包。
#
# ⚠ 不要用"手工列举要删哪几个文件名"来实现：运行时解压清单随 Avalonia/内核版本变化，
#   列举必然漏，届时又是一轮静默污染。以 SHA256SUMS 为**唯一真源**做差集才不会漂。
function Remove-ExtraneousArtifacts([string]$Dir) {
    $Dir = Resolve-Dir $Dir
    $manifest = Join-Path $Dir "SHA256SUMS"
    if (-not (Test-Path -LiteralPath $manifest)) { return @() }
    $keep = @{}
    foreach ($l in @(Get-Content -LiteralPath $manifest)) {
        $m = [regex]::Match($l, '^[0-9a-fA-F]{64}\s\s?(.+)$')
        if ($m.Success) { $keep[(Convert-ManifestPathToLocal $m.Groups[1].Value.Trim())] = $true }
    }
    $removed = @()
    foreach ($f in @(Get-ChildItem $Dir -File -Recurse -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($Dir.Length).TrimStart('\')
        if ($rel -eq "SHA256SUMS") { continue }        # 清单自身不参与收敛
        if (-not $keep.ContainsKey($rel)) {
            Remove-Item -LiteralPath $f.FullName -Force
            $removed += $rel
        }
    }
    # 清掉因上述删除而变空的目录（典型是 logs\）。按路径长度倒序 ⇒ 先子后父。
    foreach ($d in @(Get-ChildItem $Dir -Directory -Recurse -ErrorAction SilentlyContinue |
                     Sort-Object { $_.FullName.Length } -Descending)) {
        if (@(Get-ChildItem -LiteralPath $d.FullName -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $d.FullName -Recurse -Force
            $removed += ($d.FullName.Substring($Dir.Length).TrimStart('\') + "\")
        }
    }
    return $removed
}

# 校验 SHA256SUMS：行数 == 包内文件数（不含清单自身），且每行哈希与磁盘实际值一致。
# 只判行数是不够的——伪造一份行数正确、哈希全错的清单照样能过（反向验证时正是这个用例）。
function Test-Sha256Manifest([string]$Dir) {
    $Dir = Resolve-Dir $Dir
    $errs = @()
    $manifest = Join-Path $Dir "SHA256SUMS"
    if (-not (Test-Path -LiteralPath $manifest)) {
        return @("缺少哈希清单 SHA256SUMS（-ChecksOnly 下请加 -WriteManifest 初始化）")
    }
    $lines = @(Get-Content -LiteralPath $manifest | Where-Object { $_ -and $_.Trim() -ne "" })
    $files = @(Get-ChildItem $Dir -File -Recurse -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -ne $manifest })
    if ($lines.Count -ne $files.Count) {
        $errs += "SHA256SUMS 行数($($lines.Count)) != 包内文件数($($files.Count))"
    }
    $map = @{}
    foreach ($l in $lines) {
        $m = [regex]::Match($l, '^([0-9a-fA-F]{64})\s\s?(.+)$')
        if (-not $m.Success) { $errs += "SHA256SUMS 行格式非法（期望 '<64位hex>  <相对路径>'）: $l"; continue }
        $map[(Convert-ManifestPathToLocal $m.Groups[2].Value.Trim())] = $m.Groups[1].Value.ToLower()
    }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($Dir.Length).TrimStart('\')
        if (-not $map.ContainsKey($rel)) { $errs += "SHA256SUMS 缺少条目: $rel"; continue }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $f.FullName).Hash.ToLower()
        if ($actual -ne $map[$rel]) {
            $errs += "SHA256 不匹配: $rel（清单 $($map[$rel].Substring(0,12))… / 实际 $($actual.Substring(0,12))…）"
        }
    }
    return $errs
}

# 列出压缩包内的**文件**条目（相对路径）。返回 $null 表示"无从校验"（7z 不可用）。
#
# 为什么需要：Assert-Package 原先只判"7z 存在且 >1MB"，那是没牙齿的判据——
# 7z 参数写错、只压进一个文件、把调试产物一起压进去，都能"通过"。
# 真正的不变式是「目录 == SHA256SUMS == 压缩包内容」（记忆条目 22），三者都要验。
#
# 用 -slt（结构化输出）+ -sccUTF-8：默认输出走 OEM 代码页，中文文件名
# （使用说明.txt）会变乱码 ⇒ 比对必然假红。-ba 去掉横幅与统计行。
function Get-ArchiveFileEntries([string]$ArchivePath) {
    $sevenZip = Get-Command "7z" -ErrorAction SilentlyContinue
    if (-not $sevenZip) {
        if (Test-Path "C:\Program Files\7-Zip\7z.exe") {
            $sevenZip = [pscustomobject]@{ Source = "C:\Program Files\7-Zip\7z.exe" }
        } else { return $null }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $sevenZip.Source
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
    $listArgs = @("l", "-ba", "-slt", "-sccUTF-8", $ArchivePath)
    if ($psi.PSObject.Properties.Name -contains "ArgumentList") {
        foreach ($a in $listArgs) { $psi.ArgumentList.Add($a) }
    } else {
        $psi.Arguments = (($listArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join " ")
    }
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "7z l 失败 (exit=$($p.ExitCode)): $err" }

    # -slt 每条目形如：Path=… / Size=… / … / Attributes=…（含 D 表示目录）
    # 目录不比对：清单只登记文件，包内空目录不属于"发货内容"的判定范围。
    $files = @()
    $curPath = $null
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^Path\s*=\s*(.*)$') { $curPath = $Matches[1] }
        elseif ($line -match '^Attributes\s*=\s*(.*)$') {
            if ($curPath -ne $null -and $Matches[1] -notmatch 'D') { $files += $curPath }
            $curPath = $null
        }
    }
    return $files
}

# ── 产物自检 ──
# 目的：打包流程历史上出现过"脚本一路绿灯、产物其实是坏的"——
# 缺 FFmpeg 的完整版、版本号没传到位、把本机 logs/（含开发者绝对路径）打进包。
# 这些都只在用户解压后才会暴露。这里在打包结束时做一次硬校验，失败即中止并以非 0 退出。
function Assert-Package([string]$mode, [string]$OutputDir, [string]$ArchivePath) {
    Write-Host "`n[自检] 校验产物完整性 / Verifying artifacts..." -ForegroundColor Yellow
    $errors = @()

    # 1. 主程序必须存在
    $exe = Join-Path $OutputDir "3FCompare.exe"
    if (-not (Test-Path $exe)) { $errors += "缺少主程序 3FCompare.exe" }

    # 2. 完整版必须自带 FFmpeg，且 avcodec 必须在（引擎探测以它为判定依据）
    if ($mode -eq "full") {
        $av = @(Get-ChildItem "$OutputDir\ffmpeg-full\avcodec-*.dll" -ErrorAction SilentlyContinue)
        if ($av.Count -eq 0) { $errors += "完整版缺少 FFmpeg 核心库 ffmpeg-full\avcodec-*.dll" }
    }

    # 3. 版本号必须真的落到 exe 上
    #    （曾出现 csproj 硬编码 <VersionSuffix>BETA 导致正式包仍显示 -BETA）
    if (Test-Path $exe) {
        $vi = (Get-Item $exe).VersionInfo
        $pv = if ($vi.ProductVersion) { $vi.ProductVersion } else { $vi.FileVersion }
        if ([string]::IsNullOrWhiteSpace($pv)) {
            $errors += "无法读取 3FCompare.exe 的版本信息（ProductVersion/FileVersion 均为空）"
        }
        elseif (-not $pv.StartsWith($Version)) {
            $errors += "版本号不符：期望以 '$Version' 开头，实际 '$pv'"
        }
        elseif ($pv -match "-") {
            # 预发布后缀仅在 -Version 本身带后缀（如 0.3.0-beta）时合法；正式包仍禁止
            if ($Version -notmatch "-") {
                $errors += "正式包不应带预发布后缀，实际 '$pv'（请确认 VersionSuffix 已清空）"
            }
        }
        # exe 的 SHA256 打印出来，便于与发布说明 / 外部渠道比对人工核对（§5.4 清单第 1 条）
        $exeItem = Get-Item $exe
        Write-Host "   · 3FCompare.exe  $([math]::Round($exeItem.Length / 1MB, 1)) MB" -ForegroundColor Gray
        Write-Host "     SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash)" -ForegroundColor Gray
    }

    # 4. 不应把本机调试日志打进包（含开发者绝对路径，属于信息泄露）
    if (Test-Path (Join-Path $OutputDir "logs")) { $errors += "发行包内残留 logs/ 目录（含本机路径）" }

    # 5. 递归不得残留任何 *.pdb（§5.4 清单第 6 条）
    #    原先只在打包流程里"发现就删"：删漏 / 删失败（文件被占用、权限不足）不会让脚本失败，
    #    坏包照发。这里改成硬断言——发布流程删完之后仍能发现 pdb，说明删除环节失效了。
    $pdbLeft = @(Get-ChildItem $OutputDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue)
    if ($pdbLeft.Count -gt 0) {
        $errors += "发行包内残留 $($pdbLeft.Count) 个 *.pdb（调试符号不应分发）：" +
                   (($pdbLeft | Select-Object -First 3 | ForEach-Object {
                        $_.FullName.Substring($OutputDir.Length).TrimStart('\') }) -join ', ')
    }

    # 6. 哈希清单：存在、行数 == 包内文件数、且逐条哈希一致
    $errors += @(Test-Sha256Manifest $OutputDir)

    # 7. 压缩包必须真实生成且体积合理（空包/半包往往体积异常小）
    #    -ChecksOnly 时 ArchivePath 为空字符串 ⇒ 本项不适用（没有压缩步骤可验）
    if (-not $NoCompress -and $ArchivePath) {
        if (-not (Test-Path $ArchivePath)) {
            $errors += "压缩包未生成: $ArchivePath"
        } elseif ((Get-Item $ArchivePath).Length -lt 1MB) {
            $errors += "压缩包体积异常小（<1MB），疑似空包"
        }

        # 8. 压缩包内容必须与 SHA256SUMS **逐条一致**（不变式：目录 == 清单 == 包内容）。
        #    体积/存在性判据拦不住"压错了内容"：7z 参数漂移只压进一个文件照样 >1MB。
        $entries = Get-ArchiveFileEntries $ArchivePath
        if ($null -eq $entries) {
            $errors += "无法核对压缩包内容：未找到 7z（PATH 与 C:\Program Files\7-Zip\7z.exe 均不可用）——列不出条目就没有资格说产物完整"
        } else {
            $manifestPath = Join-Path (Resolve-Dir $OutputDir) "SHA256SUMS"
            $manifestRel = @()
            if (Test-Path -LiteralPath $manifestPath) {
                foreach ($l in @(Get-Content -LiteralPath $manifestPath)) {
                    $m = [regex]::Match($l, '^[0-9a-fA-F]{64}\s\s?(.+)$')
                    if ($m.Success) { $manifestRel += (Convert-ManifestPathToLocal $m.Groups[1].Value.Trim()) }
                }
            }
            $entrySet = @{}; foreach ($e in @($entries)) { $entrySet[$e] = $true }
            $manSet   = @{}; foreach ($r in $manifestRel) { $manSet[$r] = $true }
            # 清单按设计不列自己（"清单自身不参与枚举"），但清单本身会被压进包内
            # ⇒ 比对时排除 SHA256SUMS 自身，否则与生成规则自相矛盾、必判红
            $inManifestNotInArc = @($manifestRel | Where-Object { -not $entrySet.ContainsKey($_) })
            $inArcNotInManifest = @(@($entries) | Where-Object { $_ -ne "SHA256SUMS" -and -not $manSet.ContainsKey($_) })
            if ($inManifestNotInArc.Count -gt 0) {
                $errors += "压缩包比清单少 $($inManifestNotInArc.Count) 项（压缩环节漏压）: " +
                           (($inManifestNotInArc | Select-Object -First 3) -join ', ')
            }
            if ($inArcNotInManifest.Count -gt 0) {
                $errors += "压缩包比清单多 $($inArcNotInManifest.Count) 项（压进了不该发的产物）: " +
                           (($inArcNotInManifest | Select-Object -First 3) -join ', ')
            }
        }
    }

    if ($errors.Count -gt 0) {
        Write-Host "   ❌ 产物自检未通过 / Artifact verification FAILED:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "      • $_" -ForegroundColor Red }
        throw "[$mode] 产物自检失败，中止发布 / Artifact verification failed"
    }
    Write-Host "   ✅ 产物自检通过 / Artifacts verified" -ForegroundColor Green
}

# ── 自测素材解析 ──
# ⚠ 必须用 testmedia\media\real\ 下的真实素材（如 real_4k_h264_60m.mp4）。
#   ffmpeg 合成的 testmedia\media\small.mp4 只有 320×180，探针映射断言必红，
#   用它会把"环境里没有真素材"误判成"程序回归"。故此处硬性排除。
function Resolve-SelfTestMedia {
    if ($SelfTestMedia) {
        if (-not (Test-Path -LiteralPath $SelfTestMedia)) {
            throw "[自测] 指定的素材不存在: $SelfTestMedia"
        }
        if ([IO.Path]::GetFileName($SelfTestMedia) -match '(?i)^small') {
            throw "[自测] 禁止使用合成小素材 small.mp4（320×180，探针断言必红）——请用 testmedia\media\real\ 下的真实 4K 素材"
        }
        return (Resolve-Path -LiteralPath $SelfTestMedia).Path
    }
    $dir = Join-Path $ProjectDir "testmedia\media\real"
    $cands = @(Get-ChildItem "$dir\*.mp4" -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -notmatch '(?i)small' })
    if ($cands.Count -eq 0) {
        throw "[自测] 找不到真实素材（$dir\*.mp4）。请放入 4K 真实视频，或用 -SelfTestMedia 指定；确实要跳过请加 -SkipPackageSelfTest"
    }
    $pick = $cands | Where-Object { $_.Name -eq 'real_4k_h264_60m.mp4' } | Select-Object -First 1
    if (-not $pick) { $pick = $cands | Where-Object { $_.Name -match '(?i)real_4k' } | Select-Object -First 1 }
    if (-not $pick) { $pick = $cands | Select-Object -First 1 }
    return $pick.FullName
}

# ── 包内自测 ──
# 从**产物目录**里的 exe 跑一次 --selftest：验的是"解压后用户手上那个 exe 真能干活"，
# 而不是开发目录 bin 下那份（两者内嵌的内核/资源可能不同，§5.4 清单第 5 条）。
# ⚠ 会弹 GUI 窗口且耗时较长 ⇒ 默认开启；-SkipPackageSelfTest 是**显式降级**（不是"跳过不判"）：
#   发布门禁 tools/发布门禁.ps1 已把包内自测收编为不可跳过项 [11/11]（门禁自己拿 publish 出的
#   AOT exe 跑 --selftest，不再依赖 -WithPack），在那里跳过会判红并让门禁退出码非 0。
function Invoke-PackageSelfTest([string]$OutputDir) {
    if ($SkipPackageSelfTest) {
        Write-Host "`n[自测] ⏭️ 已跳过包内自测（-SkipPackageSelfTest，显式降级）/ In-package self-test SKIPPED (degraded)" -ForegroundColor Yellow
        Write-Host "      ⚠ 产物**未经** --selftest 验证，不可据此判定可发布 / NOT verified by --selftest" -ForegroundColor Yellow
        Write-Host "        发布门禁 [11/11] 已把该项收编为不可跳过项；在那里跳过会判红并以非 0 退出。" -ForegroundColor Yellow
        return
    }
    Write-Host "`n[自测] 从产物目录运行 --selftest / In-package self-test..." -ForegroundColor Yellow
    $exe = Join-Path $OutputDir "3FCompare.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "[自测] 产物目录内找不到 3FCompare.exe: $exe（无法自测，请先修好发布步骤）"
    }
    $media = Resolve-SelfTestMedia
    Write-Host "   素材 / Media: $media" -ForegroundColor Gray
    Write-Host "   ⚠ 会弹出程序窗口，请勿操作 / A GUI window will appear, do not interact" -ForegroundColor DarkYellow
    $r = Invoke-Cmd $exe @("--selftest", $media)
    $tail = ($r.Out | Where-Object { $_ -match 'selftest\[|全部通过|通过' } | Select-Object -Last 1)
    if ($r.Exit -ne 0) {
        $r.Out | Select-Object -Last 20 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
        throw "[自测] 包内自测失败 / In-package self-test failed (exit=$($r.Exit)) $tail"
    }
    Write-Host "   ✅ 包内自测通过 / Self-test passed (exit=0) $tail" -ForegroundColor Green
}

# ── 打包前置：单元测试 ──
# 打包前跑两个测试工程，任一失败即中止（§六 #5）。
# 理由：NativeAOT 打包 10~20 分钟，等打完才发现测试红是纯浪费；且发布包一旦发出去，
# "带回归的包"的代价远高于打包前多花 40 秒。
# ⚠ 必须走 Invoke-Cmd：dotnet test 会把用例失败/警告写到 stderr，裸调用在 Stop 下会崩。
function Invoke-UnitTests {
    Write-Host "`n[前置] 单元测试（Release）/ Pre-pack unit tests..." -ForegroundColor Yellow
    $projects = @(
        @{ Name = "Core.Tests";     Path = "$ProjectDir\tests\3FCompare.Core.Tests\3FCompare.Core.Tests.csproj" },
        @{ Name = "Platform.Tests"; Path = "$ProjectDir\tests\3FCompare.Platform.Tests\3FCompare.Platform.Tests.csproj" }
    )
    foreach ($p in $projects) {
        if (-not (Test-Path -LiteralPath $p.Path)) { throw "[前置] 测试工程不存在: $($p.Path)" }
        $r = Invoke-Cmd $Dotnet @("test", $p.Path, "-c", "Release", "--no-restore", "--nologo")
        $summary = ($r.Out | Where-Object { $_ -match '已通过!|失败!|Passed!|Failed!' } | Select-Object -Last 1)
        if ($r.Exit -ne 0) {
            $r.Out | Select-Object -Last 20 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
            throw "[前置] 单元测试失败：$($p.Name) (exit=$($r.Exit)) $summary"
        }
        if (-not $summary) { $summary = "(无摘要行)" }
        Write-Host "   ✅ $($p.Name): $summary" -ForegroundColor Green
    }
}

# ── 单个版本打包 ──
function Invoke-Pack([string]$mode) {
    $PackageName = Get-PackageName $mode
    $OutputDir = "$BuildDir\$PackageName"
    $ArchivePath = "$PublishDir\$PackageName.7z"

    $modeLabel = switch ($mode) {
        "app"  { "精简版 (NativeAOT, 无 FFmpeg) / Lite (NativeAOT, no FFmpeg)" }
        "full" { "完整版 (NativeAOT + ffmpeg-full) / Full (NativeAOT + ffmpeg-full)" }
    }
    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  $modeLabel" -ForegroundColor Cyan
    Write-Host "  v$Version | $Arch | $PackageName" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

    # 清理旧产物
    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Step 1: NativeAOT 发布（FFF.Native 已内嵌于 exe）/ NativeAOT publish (FFF.Native embedded in exe)
    # -p:WgcCaptureRequired=true（§六 #6）：WGC 抓屏原生库缺失时**构建直接 Error**。
    #   默认该缺失只降级成 Warning（源码里解释了原因：CI/新克隆没有 VS C++ 工具链，
    #   而运行时 FrameCapture.GetProbe() 探测到缺库有 GDI 兜底、本就不会崩）。
    #   但发布包不一样——发布包缺 WGC ⇒ 用户拿到的是"抓 D3D flip-model 不可靠、窗口被遮挡
    #   就抓到遮挡物"的降级版，且完全无声。故发布路径强制严格。
    #   （已核实该 MSBuild 属性真实存在：src/3FCompare/3FCompare.csproj:106/114 定义了
    #     <WgcCaptureRequired> 并据此 <Error>，不是无效果的空开关。）
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    $pub = Invoke-Cmd -FilePath $Dotnet -Arguments @(
        "publish", $AppProject,
        "-c", "Release", "-r", $Rid,
        "-p:PublishAot=true",
        "-p:SelfContained=true",
        "-p:WgcCaptureRequired=true",
        "-p:Version=$Version",
        "-p:VersionSuffix=",
        "--no-restore",
        "-o", $OutputDir
    ) -Stream
    # Invoke-Cmd 已把"命令根本没启动"归一成 exit=-1，不会沿用上一次成功调用的 0（旧代码的坑）
    if ($pub.Exit -ne 0) {
        $pub.Out | Select-Object -Last 15 | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkRed }
        throw "[$mode] 发布失败 / Publish failed (exit=$($pub.Exit))"
    }
    Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

    # 清理本机调试日志：内含开发者绝对路径（如 C:\PLAN\...\test_8k_av1_200M.mp4），不应随包分发
    $logDir = Join-Path $OutputDir "logs"
    if (Test-Path $logDir) {
        Remove-Item -Recurse -Force $logDir
        Write-Host "   ✅ 已清理发行包内的调试日志 / Debug logs removed" -ForegroundColor Green
    }

    # 清理调试符号（.pdb 对用户无意义）/ Remove debug symbols (.pdb)
    # ⚠ 必须 -Recurse：原先只匹配顶层 "$OutputDir\*.pdb"，子目录里的 pdb 会随包分发
    # （docs/14 §P2-10）。
    $pdbFiles = @(Get-ChildItem $OutputDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue)
    if ($pdbFiles.Count -gt 0) {
        # ⚠ 不能写 `$pdbFiles | Remove-Item`：当结果为单个 FileInfo 时管道绑定会失败
        # （"输入对象无法绑定到该命令的任何参数"），导致打包中断。显式循环更可靠。
        foreach ($f in $pdbFiles) { Remove-Item -LiteralPath $f.FullName -Force }
        $savedMB = [math]::Round(($pdbFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host "   ✅ 已删除调试符号（递归），共 $($pdbFiles.Count) 个，节省 ${savedMB}MB / Debug symbols removed, saved ${savedMB}MB" -ForegroundColor Green
    }
    # 删完再验一次：删漏/删失败（文件被占用、权限不足）以前不会让脚本失败，坏包照发。
    # 这里提前判红，让失败点靠近根因（Assert-Package 里还会再断言一次，属于兜底）。
    $pdbStill = @(Get-ChildItem $OutputDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue)
    if ($pdbStill.Count -gt 0) {
        throw "[$mode] 调试符号删除失败，仍有 $($pdbStill.Count) 个 *.pdb 残留在产物目录（可能被进程占用）"
    }

    # 单文件发布后不该再出现这些中间产物；出现说明发布配置被改过或有残留。
    # 只警告不失败：某些合法的发布形态会保留 deps.json，硬拦会误伤打包。
    $leftovers = @()
    $leftovers += Get-ChildItem $OutputDir -Filter *.deps.json -Recurse -File -ErrorAction SilentlyContinue
    $leftovers += Get-ChildItem $OutputDir -Filter *.runtimeconfig.json -Recurse -File -ErrorAction SilentlyContinue
    $leftovers += Get-ChildItem $OutputDir -Directory -Recurse -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -in @('bin', 'obj') }
    if ($leftovers.Count -gt 0) {
        Write-Host "   ⚠ 发行包内发现 $($leftovers.Count) 个中间产物（通常不应随包分发）:" -ForegroundColor Yellow
        $leftovers | Select-Object -First 10 | ForEach-Object {
            Write-Host "     - $($_.FullName.Substring($OutputDir.Length).TrimStart('\'))" -ForegroundColor DarkYellow
        }
    }

    # Step 2/3: 复制 FFmpeg 运行时 + 生成使用说明 (仅完整版)
    if ($mode -eq "full") {
        Write-Host "`n[2/4] 复制 FFmpeg 组件包 / Copy FFmpeg bundle..." -ForegroundColor Yellow
        # 单份 FFmpeg DLL 复制到 ffmpeg-full/ 子目录（运行时 NativeRuntime 自动探测并注册此目录）
        $FfmpegDest = "$OutputDir\ffmpeg-full"
        New-Item -ItemType Directory -Force -Path $FfmpegDest | Out-Null
        $FfmpegSrc = "$PublishDir\PLAN\ffmpeg-full"
        # P0-4 修复：以下三种情况过去都只 warning 就继续，会打出"完整版却没有 FFmpeg"的坏包
        # （用户解压后和精简版一样只能跑演示模式），而脚本末尾照样打印"打包完成"。
        # 完整版的价值就在于自带 FFmpeg，缺了就必须失败中止。
        if (-not (Test-Path $FfmpegSrc)) {
            throw "[$mode] FFmpeg 源目录不存在: $FfmpegSrc`n请先将 FFmpeg DLL 放入 publish\PLAN\ffmpeg-full\ 目录再打包完整版。"
        }
        $ffDlls = @(Get-ChildItem "$FfmpegSrc\*.dll" -ErrorAction SilentlyContinue)
        if ($ffDlls.Count -eq 0) {
            throw "[$mode] FFmpeg 源目录存在但没有 DLL: $FfmpegSrc —— 完整版必须包含 FFmpeg，否则与精简版无异。"
        }
        $ffDlls | ForEach-Object { Copy-Item $_.FullName "$FfmpegDest" -Force }
        # 复制后按数量校验，防止 Copy-Item 静默失败
        $copied = @(Get-ChildItem "$FfmpegDest\*.dll" -ErrorAction SilentlyContinue)
        if ($copied.Count -ne $ffDlls.Count) {
            throw "[$mode] FFmpeg DLL 复制数量不符：源 $($ffDlls.Count) 个，目标 $($copied.Count) 个"
        }
        Write-Host "   ✅ FFmpeg DLL 已复制到 ffmpeg-full/ 子目录（$($copied.Count) 个）" -ForegroundColor Green

        Write-Host "`n[3/4] 生成使用说明 / Generate usage guide..." -ForegroundColor Yellow
        $ReadmePath = "$OutputDir\使用说明.txt"
        # ⚠ 使用说明.txt 是**包内文件**（在 SHA256SUMS 里），内容必须只由 Version/Arch 决定。
        #   原先这里嵌了 (Get-Date)，同一份代码打两次包内容就不同 ⇒ 无法用"两次产物一致"
        #   来验证打包逻辑没漂移（可复现构建的判据）。改用 -BuildDate 显式传入，
        #   未传就不写这一项——宁可少一行信息，也不引入不可复现的输入。
        #   注：exe 自身的 PE 时间戳仍可能变化，故"全包字节级一致"尚不成立；
        #   这里保证的是**脚本生成的文本文件**可复现。
        $buildDateLine = if ($BuildDate) { " | 构建日期 Build: ${BuildDate}" } else { "" }
        $content = @"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  3FCompare v${Version} — 使用说明 / Usage Guide
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求 / Requirements
  • Windows 10/11 或更高版本 / or later
  • 无需安装任何运行环境（NativeAOT 独立编译）/ No runtime required (NativeAOT standalone)

🚀 快速开始 / Quick Start
  1. 解压所有文件到任意目录（保持文件夹结构完整）/ Extract to any directory
  2. 双击运行 3FCompare.exe / Double-click 3FCompare.exe
  3. 拖入视频文件即可开始对比 / Drag video files to start comparison

📁 文件结构 / File Structure
  3FCompare.exe    — 主程序（NativeAOT 单文件，内嵌播放器内核）
                         Main executable (NativeAOT, embedded player kernel)
  av*.dll / sw*.dll    — FFmpeg 编解码引擎（在 ffmpeg-full/ 子目录）/ FFmpeg decoding engine (in ffmpeg-full/ subdirectory)
  ass-9.dll            — 字幕渲染引擎 / Subtitle rendering engine

⌨️ 快捷键 / Shortcuts
  ※ 前三行的键位与步进量都能在「设置 → 快捷键 / 步进」里改；下面按**出厂默认**列出。
    The first three rows are rebindable in Settings → Shortcuts / Stepping; defaults shown.
  空格 Space       播放/暂停 Play/Pause
  A / D            逐帧后退/前进（默认 1 帧）Frame step back/forward (1 frame by default)
  ← / →            按秒快退/快进（默认 1 秒）Second step back/forward (1 s by default)
  ↑ / ↓            ±10 秒步进 10s step
  G / V / S        视图模式：标准 / A-B 可拖动 / 左右拉动  View modes: standard / A-B split / wipe
  C                对比布局循环（AB / AB 上下 / ABC / ABC 三列 / ABCD）Cycle compare layout
  P                像素探针 Pixel probe
  R                重置视图 Reset view
  O 或 Ctrl+O      打开视频 Open videos
  Ctrl+S           导出当前帧 PNG Export frame as PNG
  Ctrl+H           侧栏三态（展开 / 图标栏 / 完全隐藏）Sidebar tri-state
  T / Shift+T      折叠时间轴 / 状态栏 Toggle timeline / status bar
  F11              全屏切换 Fullscreen
  Esc              退出全屏 Exit fullscreen
  A / B            时间轴 A/B 打点（时间轴获得焦点时）Timeline A/B markers (when focused)
  设置 / Settings  菜单「设置 → 设置对话框」（无默认快捷键）Settings menu item, no default key

❓ 常见问题 / FAQ
  Q: 提示"FFmpeg 不可用"？/ "FFmpeg unavailable"?
  A: 完整版已内置 FFmpeg（DLL 在 ffmpeg-full/ 子目录），程序会自动探测，通常不会出现此提示。
     Full version includes FFmpeg DLLs in the ffmpeg-full/ subdirectory, auto-detected at startup.
     若出现，请打开「设置 → 设置对话框」→ FFmpeg 路径 → 指向 ffmpeg-full/ 或程序目录，
     点击"测试探测"验证后保存。
     Otherwise, open Settings (menu 设置 → Settings dialog) → FFmpeg Path → point to ffmpeg-full/
     or the program dir, click "Test" to verify, then save.

  Q: 精简版如何播放视频？/ How to play video in the lite version?
  A: 精简版不含 FFmpeg，需要自行获取 FFmpeg DLL，放到程序目录下 ffmpeg-full/ 子目录
     （程序自动探测），或在设置中指定包含 avcodec-*.dll 的目录。
     The lite version does not include FFmpeg; obtain FFmpeg DLLs yourself and place them
     in an ffmpeg-full/ subdirectory next to the exe (auto-detected), or set the path in Settings.

  Q: 迁移到其他电脑？/ Migrate to another PC?
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。
     Copy the entire folder (portable, no .NET runtime required).

  Q: 怎么确认包没坏 / 没被改过？/ How to verify the package integrity?
  A: 包内 SHA256SUMS 列出每个文件的 sha256。在包根目录执行：
     sha256sum -c SHA256SUMS        （Git Bash / WSL 自带；Windows 也可用 certutil 逐个核对）
     Every file's sha256 is listed in SHA256SUMS; run the command above from the package root.

📞 反馈与交流 / Feedback
  GitHub: https://github.com/luoye-cpu/3FCompare

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本 Version: v${Version} | 架构 Arch: ${Arch}$buildDateLine
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
"@
        # 不能写 -Encoding UTF8：PS 5.1 会写 BOM、pwsh 7 不写，同一份使用说明.txt
        # 在另一版本下打开就乱码。显式指定带 BOM 的 UTF8，两个版本行为一致。
        # （Set-Content 在 5.1 下 -Encoding UTF8 也是 BOM，但 pwsh 7 默认无 BOM，故统一走 .NET API）
        [IO.File]::WriteAllText($ReadmePath, $content, (New-Object Text.UTF8Encoding $true))
        Write-Host "   ✅ 使用说明已生成 → $ReadmePath" -ForegroundColor Green
    } else {
        Write-Host "`n[2/4] 跳过 (精简版不含 PLAN) / Skipped (lite, no PLAN)" -ForegroundColor Yellow
        Write-Host "`n[3/4] 跳过 (精简版不生成使用说明) / Skipped (lite, no usage guide)" -ForegroundColor Yellow
    }

    # Step 4: 压缩 / Archive
    Write-Host "`n[4/4] 压缩打包 / Archiving..." -ForegroundColor Yellow

    # 哈希清单必须在**所有文件就位之后、压缩之前**生成：
    #  · 在压缩之前 ⇒ 清单本身也被收进 .7z，用户解压后即可 `sha256sum -c` 自证；
    #  · 在 ffmpeg 复制/使用说明生成之后 ⇒ 清单覆盖的是最终内容，不是半成品。
    $manifestCount = New-Sha256Manifest -Dir $OutputDir
    Write-Host "   ✅ 已生成 SHA256SUMS（覆盖 $manifestCount 个文件）/ Hash manifest written" -ForegroundColor Green

    if ($NoCompress) {
        # P1-6 修复：这里原来是 `return`，而断言写在 return 之后 ——
        # -NoCompress 本是用来快速验证打包逻辑的调试开关，结果恰恰把
        # exe / 版本号 / FFmpeg / logs 残留这四项最该验的校验全跳过了。
        # 改为「只跳过压缩，不跳过自检」（Assert-Package 内部已按 $NoCompress 跳过压缩包体积检查）。
        Write-Host "   ⏭️ 已跳过压缩 (-NoCompress) / Compression skipped" -ForegroundColor Yellow
        Write-Host "   产物目录 / Output directory: $OutputDir" -ForegroundColor Yellow
    } else {
        $sevenZip = Get-Command "7z" -ErrorAction SilentlyContinue
        if (-not $sevenZip -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) {
            $sevenZip = [pscustomobject]@{ Source = "C:\Program Files\7-Zip\7z.exe" }
        }
        if ($sevenZip) {
            Remove-Item $ArchivePath -Force -ErrorAction SilentlyContinue
            # P2 修复：7z 参数一律以**数组**传递，杜绝字符串拼接的引号注入。
            #   旧写法 $zipArgs = 'a -t7z ... -mmt=1 "' + $ArchivePath + '" *' 把路径裸拼进命令行，
            #   而 $ArchivePath 由 $Version 拼成（$Version 无字符集校验）：版本号里带一个 `"`
            #   就能把后续内容顶出引号范围，被 7z 当成额外参数。
            #   pwsh7 / .NET Core 用 ArgumentList 逐项传参；PS 5.1 没有该属性，退回
            #   ConvertTo-CommandLineArg 逐项引用后 join —— 两者语义一致。
            $zipArgs = @("a", "-t7z", "-mx9", "-md=3840m", "-mfb=273", "-ms=on", "-mmt=1", $ArchivePath, "*")
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $sevenZip.Source
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $false
            $psi.WorkingDirectory = $OutputDir
            if ($psi.PSObject.Properties.Name -contains "ArgumentList") {
                foreach ($a in $zipArgs) { $psi.ArgumentList.Add($a) }
            } else {
                $psi.Arguments = (($zipArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join " ")
            }
            $proc = [System.Diagnostics.Process]::Start($psi)
            try { $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }
            $proc.WaitForExit()
            if ($proc.ExitCode -ne 0) { throw "压缩失败 (7z exit code: $($proc.ExitCode)) / Archive failed" }
            Write-Host "   ✅ 压缩完成 → $ArchivePath / Archive created" -ForegroundColor Green
        } else {
            # P0-4 修复：过去这里只 warning 且退出码仍为 0，CI 会误判打包成功，
            # 实际上根本没有产出可分发的压缩包。
            throw "未找到 7z.exe，无法压缩。请安装 7-Zip 或将其加入 PATH。/ 7z not found — install 7-Zip or add it to PATH."
        }
    }

    # 产物自检：确认"打出来的包真的是能用的包"，任一项不满足即中止。
    # 必须在上面 -NoCompress 分支之外，否则调试打包时整段失效。
    Assert-Package -mode $mode -OutputDir $OutputDir -ArchivePath $ArchivePath

    # 包内自测：拿产物目录里的 exe 真跑一次（默认开启；-SkipPackageSelfTest 是显式降级，
    # 且在发布门禁里该项 [11/11] 不可跳过）
    Invoke-PackageSelfTest -OutputDir $OutputDir

    # ⚠ 自测跑在 Assert-Package **之后**，而它真跑一次 exe 必然产生三类副产物：
    #   logs\（含本机绝对路径）、以及运行时解压的 FFF.Native.dll / 3FC.WgcCapture.dll /
    #   libSkiaSharp.dll / libHarfBuzzSharp.dll —— 全都**不属于发货内容**。
    #   结果：产物目录不再等于压缩包内容，再用 -ChecksOnly 复检会判红，日志还可能被误收进包。
    #   （压缩包在自测之前生成，故 7z 本身是干净的；要修的是产物目录。）
    #   ⇒ 自测后把目录收敛回 SHA256SUMS 列出的发货清单，再校验"目录 == 清单 == 压缩包内容"。
    $removed = @(Remove-ExtraneousArtifacts -Dir $OutputDir)
    if ($removed.Count -gt 0) {
        Write-Host "   ✅ 已清除自测产生的 $($removed.Count) 项非发货内容（不进包，运行时会按需重新解压）:" -ForegroundColor Green
        $removed | Select-Object -First 8 | ForEach-Object { Write-Host "      · $_" -ForegroundColor DarkGray }
    }
    $mfErrs = @(Test-Sha256Manifest -Dir $OutputDir)
    if ($mfErrs.Count -gt 0) {
        Write-Host "   ❌ 自测后哈希清单校验失败:" -ForegroundColor Red
        $mfErrs | ForEach-Object { Write-Host "      • $_" -ForegroundColor Red }
        throw "[$mode] 自测后产物目录与 SHA256SUMS 不一致，中止发布"
    }

    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  [$mode] 打包完成 / Pack complete!" -ForegroundColor Green
    Write-Host "  产物目录 / Output: $OutputDir" -ForegroundColor Green
    if (Test-Path $ArchivePath) {
        $size = (Get-Item $ArchivePath).Length / 1MB
        Write-Host "  压缩包 / Archive: $ArchivePath ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    }
    Write-Host "  哈希清单 / Manifest: $(Join-Path $OutputDir 'SHA256SUMS')" -ForegroundColor Green
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
}

# ── -ChecksOnly：只校验一个已存在的目录（不单测 / 不编译 / 不压缩）──
# 用途：让"新增断言的反向验证"变得可行——手工构造失败条件后秒级判红，不必等 10~20 分钟的 AOT。
# 也用于复检已发出的包（例如从归档解压出来再跑一次）。
if ($ChecksOnly) {
    if (-not $CheckDir) { throw "-ChecksOnly 需要 -CheckDir <目录> / -ChecksOnly requires -CheckDir <dir>" }
    if (-not (Test-Path -LiteralPath $CheckDir)) { throw "-CheckDir 不存在 / not found: $CheckDir" }
    # ⚠ 必须显式给 app|full：默认的 "all" 无法确定要不要断言 ffmpeg-full\avcodec-*.dll，
    #    若按"目录里没有 ffmpeg-full 就当成精简版"来自动判，则"完整版漏拷 FFmpeg"会被
    #    静默放过 —— 正是本项目最忌讳的假绿。
    if ($Mode -eq "all") {
        throw "-ChecksOnly 必须显式指定 -Mode app 或 -Mode full（不接受 all：否则完整版的 FFmpeg 断言会被静默跳过）"
    }
    $CheckDir = [IO.Path]::GetFullPath($CheckDir)
    Write-Host "`n[仅校验] 目录 / Directory: $CheckDir" -ForegroundColor Cyan
    Write-Host "[仅校验] 模式 / Mode: $Mode | 期望版本 / Version: $Version" -ForegroundColor Cyan

    if ($WriteManifest) {
        $n = New-Sha256Manifest -Dir $CheckDir
        Write-Host "   · 已重算 SHA256SUMS（$n 个文件）/ Manifest regenerated" -ForegroundColor Gray
    }

    Assert-Package -mode $Mode -OutputDir $CheckDir -ArchivePath ""

    # ⚠ 自测必须放在 Assert-Package **之后**（与主流程一致），且之后要"收敛 + 复验"。
    #   旧版 -ChecksOnly 缺了后面两步：自测真跑 exe 会生成 logs\ 与运行时解压的
    #   FFF.Native.dll / 3FC.WgcCapture.dll / libSkiaSharp.dll / libHarfBuzzSharp.dll
    #   ⇒ 目录 ≠ 清单（16 行 vs 22 文件）⇒ **第二次 -ChecksOnly 必判红**；
    #   而 -WriteManifest 更糟：它会把这份污染**写进**清单，清单从此描述的不是发货内容。
    #   这里补齐与 Invoke-Pack 完全一致的"自测 → 收敛 → 复验"，保证 -ChecksOnly 可反复执行。
    Invoke-PackageSelfTest -OutputDir $CheckDir

    $removed = @(Remove-ExtraneousArtifacts -Dir $CheckDir)
    if ($removed.Count -gt 0) {
        Write-Host "   ✅ 已清除自测产生的 $($removed.Count) 项非发货内容（不进包，运行时会按需重新解压）:" -ForegroundColor Green
        $removed | Select-Object -First 8 | ForEach-Object { Write-Host "      · $_" -ForegroundColor DarkGray }
    }
    $mfErrs = @(Test-Sha256Manifest -Dir $CheckDir)
    if ($mfErrs.Count -gt 0) {
        Write-Host "   ❌ 自测后哈希清单校验失败:" -ForegroundColor Red
        $mfErrs | ForEach-Object { Write-Host "      • $_" -ForegroundColor Red }
        throw "[$Mode] 自测后目录与 SHA256SUMS 不一致，中止校验"
    }

    Write-Host "`n✅ 仅校验模式全部通过 / Checks-only PASSED" -ForegroundColor Green
    exit 0
}

# ── 主入口 ──
# 前置单测：任一测试工程红即中止（§六 #5）。放在 switch 之前 ⇒ app/full/all 都会先过这一关。
Invoke-UnitTests

switch ($Mode) {
    "app"  { Invoke-Pack "app" }
    "full" { Invoke-Pack "full" }
    # full 先、app 后。原先是 app 先：精简版按设计不含 FFmpeg ⇒ 包内 --selftest 落进演示模式、
    # 按 docs/41 #17 故意返回 2（fail-closed，正确的判定，不去放宽），而 Invoke-Pack 遇非零即 throw
    # ⇒ `-Mode all` 永远走不到 full，`tools/发布门禁.ps1 -WithPack` 因此产不出完整包的 AOT 自测物。
    # 换序只改"哪一条先被打"，不改任何判决：app 变体照样打包、照样在包内自测处判红，
    # 门禁仍把"打包与产物自检"记为失败并让退出码非 0。
    "all"  { Invoke-Pack "full"; Invoke-Pack "app" }
}

Write-Host "`n✅ 全部打包完成! / All packs complete!" -ForegroundColor Green
