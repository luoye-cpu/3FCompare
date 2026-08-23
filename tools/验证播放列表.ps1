# 3FP 播放列表左右分区验证脚本
# 1. 枚举 3FP 窗口，找到播放列表按钮并点击
# 2. 打开播放列表窗口后验证 DragSelectZoneWidth 生效
param(
    [int]$ProcessId = 87332
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Enum {
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
}
"@

$proc = Get-Process -Id $ProcessId -ErrorAction Stop
$main = $proc.MainWindowHandle
Write-Host "主窗口 hwnd=$main 标题=$($proc.MainWindowTitle)"

# 枚举所有子窗口
$all = New-Object System.Collections.ArrayList
$cb = [Win32Enum+EnumProc]{
    param($h, $l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][Win32Enum]::GetClassName($h, $sb, 256)
    $tb = New-Object System.Text.StringBuilder 256
    [void][Win32Enum]::GetWindowText($h, $tb, 256)
    $rect = New-Object Win32Enum+RECT
    [void][Win32Enum]::GetWindowRect($h, [ref]$rect)
    [void]$all.Add([PSCustomObject]@{
        Hwnd = $h; Class = $sb.ToString(); Text = $tb.ToString()
        Rect = "$($rect.Left),$($rect.Top)-$($rect.Right),$($rect.Bottom)"
    })
    return $true
}
[void][Win32Enum]::EnumChildWindows($main, $cb, [IntPtr]::Zero)

Write-Host "`n=== 主窗口子控件 ($($all.Count) 个) ==="
$all | Where-Object { $_.Class -ne "" } | ForEach-Object {
    Write-Host ("hwnd={0} class={1} text='{2}' rect={3}" -f $_.Hwnd, $_.Class, $_.Text, $_.Rect)
}
