# 3FCompare 鼠标交互完整测试
# 测试：滚轮缩放 / 拖拽平移 / 点击选中 / 截图对比
param(
    [string]$VideoPath = "C:\PLAN\3FCompare\testmedia\media\vidA.mp4",
    [string]$ExeDir = "C:\PLAN\3FCompare\src\3FCompare\bin\Debug\net11.0-windows"
)

$ErrorActionPreference = "Continue"

# ── Win32 API ──
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public class MouseTest {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lp);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint WHEEL = 0x0800;
    public const uint LDOWN = 0x0002;
    public const uint LUP   = 0x0004;
}
"@

Add-Type -AssemblyName System.Drawing

function Screenshot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $path = "C:\PLAN\3FCompare\testmedia\media\mouse_test_$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $size = (Get-Item $path).Length
    Write-Host "  📸 ${name}: $path ($size bytes)"
    $g.Dispose(); $bmp.Dispose()
    return $size
}

function WheelAt([int]$x, [int]$y, [int]$delta) {
    [void][MouseTest]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 100
    [MouseTest]::mouse_event([MouseTest]::WHEEL, 0, 0, [uint32]([Math]::Abs($delta)), [UIntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 200
}

function DragFromTo([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    [void][MouseTest]::SetCursorPos($x1, $y1)
    Start-Sleep -Milliseconds 100
    [MouseTest]::mouse_event([MouseTest]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    # 分步移动（模拟平滑拖拽）
    $steps = 10
    for ($i = 1; $i -le $steps; $i++) {
        $cx = $x1 + [int](($x2 - $x1) * $i / $steps)
        $cy = $y1 + [int](($y2 - $y1) * $i / $steps)
        [void][MouseTest]::SetCursorPos($cx, $cy)
        Start-Sleep -Milliseconds 30
    }
    Start-Sleep -Milliseconds 100
    [MouseTest]::mouse_event([MouseTest]::LUP, 0, 0, 0, [UIntPtr]::Zero)
}

# ── 启动应用 ──
$exe = Join-Path $ExeDir "3FCompare.exe"
if (-not (Test-Path $exe)) { Write-Host "❌ exe 不存在"; exit 1 }

Write-Host "`n=== 启动应用 ===" 
$proc = Start-Process $exe -ArgumentList "--autodemo", $VideoPath -PassThru
Start-Sleep -Seconds 8

# 查找窗口位置
$mainHwnd = [IntPtr]::Zero
$cb = [MouseTest+EnumWindowsProc]{
    param($h, $l)
    $winPid = [uint32]0
    [void][MouseTest]::GetWindowThreadProcessId($h, [ref]$winPid)
    if ($winPid -eq $proc.Id -and [MouseTest]::IsWindowVisible($h)) {
        $script:mainHwnd = $h
        return $false
    }
    return $true
}
[void][MouseTest]::EnumWindows($cb, [IntPtr]::Zero)

if ($mainHwnd -eq [IntPtr]::Zero) { Write-Host "❌ 未找到窗口"; Stop-Process -Id $proc.Id; exit 1 }

$rect = New-Object MouseTest+RECT
[void][MouseTest]::GetWindowRect($mainHwnd, [ref]$rect)
[void][MouseTest]::SetForegroundWindow($mainHwnd)
Start-Sleep -Seconds 2

$winX = $rect.Left; $winY = $rect.Top
$winW = $rect.Right - $rect.Left; $winH = $rect.Bottom - $rect.Top
Write-Host "窗口: ($winX,$winY) ${winW}x${winH}"

# 视频区域中心（假设网格在菜单下方、传输栏上方，占窗口主要区域）
$vidCX = $winX + [int]($winW * 0.45)
$vidCY = $winY + [int]($winH * 0.40)

# ── 测试 1：截图初始状态 ──
Write-Host "`n=== [T1] 初始状态截图 ==="
$s1 = Screenshot "initial"

# ── 测试 2：滚轮放大 ──
Write-Host "`n=== [T2] 滚轮放大 ×5 ==="
for ($i = 0; $i -lt 5; $i++) { WheelAt $vidCX $vidCY 120 }
Start-Sleep -Seconds 1
$s2 = Screenshot "zoomed"

# ── 测试 3：拖拽平移（向右下拖 150px）──
Write-Host "`n=== [T3] 拖拽平移 →(150,+100) ==="
DragFromTo ($vidCX) ($vidCY) ($vidCX + 150) ($vidCY + 100)
Start-Sleep -Seconds 1
$s3 = Screenshot "panned"

# ── 测试 4：继续放大到更高倍率 ──
Write-Host "`n=== [T4] 继续放大 ×5 ==="
for ($i = 0; $i -lt 5; $i++) { WheelAt $vidCX ($vidCY + 50) 120; Start-Sleep -m 100 }
Start-Sleep -Seconds 1
$s4 = Screenshot "highzoom"

# ── 测试 5：反向缩小回原尺寸 ──
Write-Host "`n=== [T5] 滚轮缩小 ×10 ==="
for ($i = 0; $i -lt 10; $i++) { WheelAt $vidCY $vidCY -120; Start-Sleep -m 80 }
Start-Sleep -Seconds 1
$s5 = Screenshot "reset"

# ── 结果分析 ──
Write-Host "`n=== 结果分析 ==="
if ($s2 -ne $s1) { Write-Host "✅ 缩放后画面变化 (差=$([Math]::Abs($s2-$s1)) bytes)" }
else { Write-Host "⚠️ 缩放前后画面相同" }
if ($s3 -ne $s2) { Write-Host "✅ 平移后画面变化 (差=$([Math]::Abs($s3-$s2)) bytes)" }
else { Write-Host "⚠️ 平移前后画面相同" }
if ($s5 -ne $s1) { Write-Host "✅ 复位后画面变化" }
else { Write-Host "⚠️ 复位前后画面相同" }

# 清理
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Host "`n✅ 鼠标交互测试完成"
