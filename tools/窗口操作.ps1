# 向指定窗口发送消息或键盘按键
param(
    [string]$Action = "close",
    [uint64]$HwndValue = 0
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinMsg {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
if ($Action -eq "close" -and $HwndValue -ne 0) {
    $target = [System.IntPtr]::new([int64]$HwndValue)
    [void][WinMsg]::PostMessage($target, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
    Write-Host "已发送 WM_CLOSE 到 $target"
} elseif ($Action -eq "esc") {
    [WinMsg]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [WinMsg]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    Write-Host "已发送 Esc"
}
