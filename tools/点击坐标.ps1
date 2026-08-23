# 点击指定屏幕坐标
param(
    [int]$X = 1685,
    [int]$Y = 458
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseSim {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@
[void][MouseSim]::SetCursorPos($X, $Y)
Start-Sleep -Milliseconds 200
[MouseSim]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)  # LEFTDOWN
Start-Sleep -Milliseconds 100
[MouseSim]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)  # LEFTUP
Write-Host "已点击 ($X, $Y)"
