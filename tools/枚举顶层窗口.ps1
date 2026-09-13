# 枚举进程的所有顶层窗口
param(
    [int]$ProcessId = 87332
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Top {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$target = [uint32]$ProcessId
$cb = [Win32Top+EnumProc]{
    param($h, $l)
    $winPid = [uint32]0
    [void][Win32Top]::GetWindowThreadProcessId($h, [ref]$winPid)
    if ($winPid -eq $target) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32Top]::GetWindowText($h, $sb, 256)
        $cb2 = New-Object System.Text.StringBuilder 256
        [void][Win32Top]::GetClassName($h, $cb2, 256)
        $rect = New-Object Win32Top+RECT
        [void][Win32Top]::GetWindowRect($h, [ref]$rect)
        $vis = [Win32Top]::IsWindowVisible($h)
        Write-Host ("hwnd={0} title='{1}' class={2} visible={3} rect={4},{5}-{6},{7}" -f $h, $sb.ToString(), $cb2.ToString(), $vis, $rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    }
    return $true
}
Write-Host "进程 $ProcessId 的顶层窗口："
[void][Win32Top]::EnumWindows($cb, [IntPtr]::Zero)
