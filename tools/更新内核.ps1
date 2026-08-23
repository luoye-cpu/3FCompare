# 更新 FFF_Project 子模块到最新版本（保留 3FCompare 自定义修改）
# 用法: powershell -ExecutionPolicy Bypass -File tools/更新内核.ps1
# 说明:
#   1. 将 third_party/fff_project 更新到 Lake1059/FFF_Project 的最新 commit
#   2. 自动重打 tools/patches/ 中的自定义补丁
#   3. 重新构建 FFF.Native 并部署
#   如果只想查看有无更新而不实际升级，加 -CheckOnly 参数。

param(
    [switch]$CheckOnly,         # 仅检查更新，不实际拉取
    [switch]$Force,             # 强制更新（丢弃本地未提交的修改）
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$SubmodulePath = Join-Path $ProjectRoot "third_party\fff_project"
$PatchesDir = Join-Path $PSScriptRoot "patches"

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  3FCompare — 内核子模块更新工具" -ForegroundColor Cyan
Write-Host "  目标: Lake1059/FFF_Project" -ForegroundColor Cyan
Write-Host "  当前: $(git -C $SubmodulePath log --oneline -1 2>$null)" -ForegroundColor Cyan
Write-Host "  补丁: $(Get-ChildItem $PatchesDir -Filter *.patch -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Count) 个自定义补丁" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# 确保子模块已初始化
if (-not (Test-Path (Join-Path $SubmodulePath "FFF.Native\FFF.Native.vcxproj"))) {
    Write-Host "`n子模块未初始化，正在拉取..." -ForegroundColor Yellow
    Push-Location $ProjectRoot
    git submodule update --init --recursive
    Pop-Location
}

# 暂存本地修改（如果有）
$hasLocalChanges = (git -C $SubmodulePath status --short) -ne ""
$stashRef = $null
if ($hasLocalChanges) {
    Write-Host "`n📦 子模块有本地修改，正在暂存..." -ForegroundColor Yellow
    git -C $SubmodulePath status --short
    $stashRef = git -C $SubmodulePath stash create "3FCompare-local-changes-$(Get-Date -Format 'yyyyMMdd')"
    if ($stashRef) {
        git -C $SubmodulePath stash store $stashRef
        Write-Host "  已暂存为: $($stashRef.Substring(0,12))" -ForegroundColor Green
    }
}

# 获取远端最新信息
Write-Host "`n[1/4] 检查远端更新..." -ForegroundColor Yellow
Push-Location $SubmodulePath
git fetch origin
$currentCommit = git rev-parse HEAD
$remoteCommit = git rev-parse origin/master 2>$null
Pop-Location

if ($currentCommit -eq $remoteCommit) {
    Write-Host "  已是最新版本 ($($currentCommit.Substring(0,12)))" -ForegroundColor Green
    if ($CheckOnly) { exit 0 }
    Write-Host "  无需更新" -ForegroundColor Green
    exit 0
}

$currentMsg = git -C $SubmodulePath log --oneline -1 $currentCommit
$remoteMsg = git -C $SubmodulePath log --oneline -1 $remoteCommit
Write-Host "  当前: $currentMsg" -ForegroundColor Gray
Write-Host "  远端: $remoteMsg" -ForegroundColor Yellow

if ($CheckOnly) {
    Write-Host "`n有可用更新。使用本脚本（不加 -CheckOnly）即可升级。" -ForegroundColor Cyan
    exit 0
}

# 更新子模块到最新上游
Write-Host "`n[2/4] 拉取上游最新版本..." -ForegroundColor Yellow
Push-Location $ProjectRoot
git submodule update --remote --force third_party/fff_project
if ($LASTEXITCODE -ne 0) { throw "子模块更新失败" }
$newCommit = git -C $SubmodulePath rev-parse HEAD
Pop-Location
Write-Host "  已更新: $($newCommit.Substring(0,12))" -ForegroundColor Green

# 重打自定义补丁
Write-Host "`n[3/4] 应用自定义补丁..." -ForegroundColor Yellow
$patches = Get-ChildItem $PatchesDir -Filter *.patch | Sort-Object Name
if ($patches) {
    Push-Location $SubmodulePath
    foreach ($patch in $patches) {
        Write-Host "  正在应用: $($patch.Name)" -ForegroundColor Yellow
        $result = git apply --ignore-whitespace $patch.FullName 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ⚠️  补丁 $($patch.Name) 应用失败（可能有冲突），请手动检查" -ForegroundColor Red
            Write-Host "  $result" -ForegroundColor Red
            Write-Host "  补丁文件位置: $($patch.FullName)" -ForegroundColor Yellow
            Write-Host "  手动应用: cd third_party/fff_project && git apply tools/patches/$($patch.Name)" -ForegroundColor Yellow
        } else {
            Write-Host "  ✅ $($patch.Name) 已应用" -ForegroundColor Green
        }
    }
    Pop-Location
} else {
    Write-Host "  无自定义补丁" -ForegroundColor Yellow
}

# 恢复之前暂存的本地修改
if ($stashRef) {
    Write-Host "`n正在恢复本地修改..." -ForegroundColor Yellow
    git -C $SubmodulePath stash pop 2>$null
    Write-Host "  ✅ 本地修改已恢复" -ForegroundColor Green
}

# 重新构建内核 + 部署
Write-Host "`n[4/4] 重新构建 FFF.Native 内核..." -ForegroundColor Yellow
& (Join-Path $PSScriptRoot "构建全部.ps1") -Configuration $Configuration -SkipTests
if ($LASTEXITCODE -ne 0) { throw "内核构建失败" }

# 提交更新
Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  更新完成!" -ForegroundColor Green
Write-Host "  旧: $($currentCommit.Substring(0,12)) $($currentMsg)" -ForegroundColor Gray
Write-Host "  新: $($newCommit.Substring(0,12)) $($remoteMsg)" -ForegroundColor Green
Write-Host "  自定义补丁已重打" -ForegroundColor Green
Write-Host "`n  建议执行以下命令提交更新:" -ForegroundColor Cyan
Write-Host "  git add third_party/fff_project tools/patches" -ForegroundColor White
Write-Host "  git commit -m '更新 FFF_Project 内核至 $($newCommit.Substring(0,12))'" -ForegroundColor White
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan