# 激活窗口并点击
param(
    [uint64]$WindowHandle = 3804566,
    [int]$X = 1643,
    [int]$Y = 458
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ActivateClick {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$target = [System.IntPtr]::new([int64]$WindowHandle)
[void][ActivateClick]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 500
$rect = New-Object ActivateClick+RECT
[void][ActivateClick]::GetWindowRect($target, [ref]$rect)
Write-Host "窗口位置: $($rect.Left),$($rect.Top)-$($rect.Right),$($rect.Bottom)"
[void][ActivateClick]::SetCursorPos($X, $Y)
Start-Sleep -Milliseconds 300
[ActivateClick]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
[ActivateClick]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Write-Host "已激活并点击 ($X, $Y)"
