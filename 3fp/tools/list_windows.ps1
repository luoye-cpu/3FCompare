param([string]$ProcessFilter = 'FFF')
# list_windows.ps1 —— 列出某进程的全部**顶层窗口**（标题/矩形/可见性）
# 用途：LakeUI 自绘窗体的标题不一定是我们预期的中文串，点击后要靠这个确认到底开了哪个窗口。
$ErrorActionPreference = 'Stop'
if (-not ('WinList' -as [type])) {
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinList {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumCb(IntPtr h, IntPtr l);
    public struct RECT { public int Left, Top, Right, Bottom; }
    static List<string> rows = new List<string>();
    static List<uint> pids = new List<uint>();
    static bool Cb(IntPtr h, IntPtr l) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pids.Count > 0 && !pids.Contains(pid)) return true;
        var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
        var c = new StringBuilder(256); GetClassNameW(h, c, 256);
        RECT r; GetWindowRect(h, out r);
        rows.Add(string.Format("pid={0} hwnd={1} vis={2} \"{3}\" class={4} {5}x{6}@{7},{8}",
            pid, h.ToInt64(), IsWindowVisible(h) ? "Y" : "N", t, c,
            r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top));
        return true;
    }
    public static List<string> Run(string[] pidStrings) {
        rows = new List<string>(); pids = new List<uint>();
        foreach (var s in pidStrings) { uint v; if (uint.TryParse(s, out v)) pids.Add(v); }
        EnumWindows(new EnumCb(Cb), IntPtr.Zero);
        return rows;
    }
}
'@
}
$pids = (Get-Process | Where-Object { $_.ProcessName -like "*$ProcessFilter*" } | ForEach-Object { "$($_.Id)" })
if (-not $pids) { Write-Output "没有匹配进程 *$ProcessFilter*"; exit 1 }
Write-Output ("进程 pid: " + ($pids -join ' '))
[WinList]::Run($pids)
