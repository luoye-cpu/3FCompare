# 3FCompare 内核升级体检工具
# 用法: powershell -ExecutionPolicy Bypass -File tools/更新内核.ps1 [-CheckOnly]
#
# 重要：本脚本**不会**自动把内核切到上游最新。
# third_party/fff_project 是 3FCompare 的本地重移植分支（3fcompare/zoom-viewport-cover），
# 上游 Lake1059/FFF_Project 不含这些扩展；直接切上游会静默丢掉全部自研能力
# （旧版本正是这么做的：checkout 失败只 Write-Warning，然后拿默认分支构建出"成功"的假象）。
# 真正的升级 = 按 third_party/fff_project/PATCHES.md 人工重移植 → 打新归档 tag → 更新
# tools/构建全部.ps1 里的 $KernelBaselineSha。本脚本负责把前置信息一次查清。

param(
    [switch]$CheckOnly   # 只体检，不联网 fetch（默认会 fetch）
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ForkRoot    = Join-Path $ProjectRoot "third_party\fff_project"
$PatchesDir  = Join-Path $PSScriptRoot "patches"

$KernelBaselineTag = "3fcompare-kernel-2026.9.11.1"
$KernelBaselineSha = "6bc8d61c7fd0a2053e806a627c6db1b4f112e2d9"

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

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  3FCompare — 内核升级体检" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $ForkRoot "FFF.Native\FFF.Native.vcxproj"))) {
    Write-Host "内核目录缺失，请先运行: tools/构建全部.ps1" -ForegroundColor Red
    exit 1
}

# ---- 1. 当前状态 ----
$head   = (Invoke-Git $ForkRoot @("rev-parse", "HEAD")).Out
$branch = (Invoke-Git $ForkRoot @("rev-parse", "--abbrev-ref", "HEAD")).Out
$dirty  = (Invoke-Git $ForkRoot @("status", "--porcelain")).Out

Write-Host "`n[1/4] 当前内核" -ForegroundColor Yellow
Write-Host "  分支: $branch"
Write-Host "  HEAD : $head"
if ($head -eq $KernelBaselineSha) {
    Write-Host "  状态 : 与钉死基线一致（$KernelBaselineTag）" -ForegroundColor Green
} else {
    Write-Host "  状态 : ⚠ 与钉死基线 $KernelBaselineSha 不一致，构建会被 tools/构建全部.ps1 拦下" -ForegroundColor Red
}
if ($dirty) {
    Write-Host "  ⚠ 工作区有未提交改动（重移植产物请先提交或备份）：" -ForegroundColor Yellow
    Write-Host $dirty
}

# ---- 2. 远端可达性 ----
Write-Host "`n[2/4] 远端信息" -ForegroundColor Yellow
if (-not $CheckOnly) {
    $fetch = Invoke-Git $ForkRoot @("fetch", "origin", "--tags", "--prune")
    if ($fetch.Exit -ne 0) {
        Write-Host "  ⚠ fetch 失败（离线？）: $($fetch.Out)" -ForegroundColor Yellow
    }
}
$upstream = (Invoke-Git $ForkRoot @("rev-parse", "origin/master")).Out
if ($upstream) {
    Write-Host "  origin/master: $upstream"
    $behind = (Invoke-Git $ForkRoot @("rev-list", "--count", "HEAD..origin/master")).Out
    $ahead  = (Invoke-Git $ForkRoot @("rev-list", "--count", "origin/master..HEAD")).Out
    Write-Host "  落后上游 $behind 个提交，领先 $ahead 个提交（领先的即为 3FCompare 自研扩展）"
} else {
    Write-Host "  ⚠ 无法解析 origin/master（离线或未配置远端）" -ForegroundColor Yellow
}

# ---- 3. 归档 tag 是否已推到远端（决定新机器能否复现）----
Write-Host "`n[3/4] 归档可复现性" -ForegroundColor Yellow
$remoteTags = (Invoke-Git $ForkRoot @("ls-remote", "--tags", "origin")).Out
if (-not $remoteTags) {
    Write-Host "  ⚠ 无法查询远端 tag（离线？）" -ForegroundColor Yellow
} elseif ($remoteTags -match [regex]::Escape($KernelBaselineTag)) {
    Write-Host "  ✅ 归档 tag $KernelBaselineTag 已在远端" -ForegroundColor Green
} else {
    Write-Host "  ⚠ 归档 tag $KernelBaselineTag **不在远端** —— 新机器裸克隆拿不到基线，" -ForegroundColor Red
    Write-Host "    tools/构建全部.ps1 会直接报错而不是静默用错内核。建议推送：" -ForegroundColor Red
    Write-Host "      git -C third_party/fff_project push origin 3fcompare/zoom-viewport-cover" -ForegroundColor White
    Write-Host "      git -C third_party/fff_project push origin $KernelBaselineTag" -ForegroundColor White
}

# ---- 4. 补丁归档 ----
Write-Host "`n[4/4] 补丁归档" -ForegroundColor Yellow
if (Test-Path $PatchesDir) {
    $patches = @(Get-ChildItem $PatchesDir -Filter *.patch | Sort-Object Name)
    Write-Host "  tools/patches: $($patches.Count) 个（历史留档，上下文已漂移，不自动重放）"
    foreach ($p in $patches) { Write-Host "    · $($p.Name)" -ForegroundColor Gray }
}

Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  升级内核的人工流程（见 third_party/fff_project/PATCHES.md）:" -ForegroundColor Cyan
Write-Host "   1. 在 $branch 上合并/重放上游 origin/master" -ForegroundColor White
Write-Host "   2. 按『必须重放』4 项重移植，以 PlayerApi 导出面为完成判据" -ForegroundColor White
Write-Host "   3. MSBuild Release x64 构建 + dotnet build + 3FCompare.Core.Tests 全绿" -ForegroundColor White
Write-Host "   4. 打新归档 tag：3fcompare-kernel-<上游版本>.<序号>" -ForegroundColor White
Write-Host "   5. 同步更新 tools/构建全部.ps1 的 `$KernelBaselineSha 与 `$KernelBaselineTag" -ForegroundColor White
Write-Host "   6. 重建并部署：tools/构建全部.ps1" -ForegroundColor White
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
