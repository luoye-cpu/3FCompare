"""对指定进程枚举全部顶层窗口（含隐藏）并投递 WM_CLOSE —— 仍属优雅退出，不使用 TerminateProcess。

用法: python graceful_close.py RTSS MSIAfterburner "NVIDIA Overlay"
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

WM_CLOSE = 0x0010

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [WNDENUMPROC, wt.LPARAM]
user32.EnumWindows.restype = wt.BOOL
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.GetWindowThreadProcessId.restype = wt.DWORD
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostMessageW.restype = wt.BOOL
user32.SendMessageTimeoutW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM,
                                       wt.UINT, wt.UINT, ctypes.POINTER(ctypes.c_void_p)]
user32.SendMessageTimeoutW.restype = wt.LPARAM
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindowVisible.restype = wt.BOOL

targets = set(sys.argv[1:])
if not targets:
    print("need process names")
    sys.exit(2)

# 用 CreateToolhelp32Snapshot 取 pid<->name
TH32CS_SNAPPROCESS = 0x2


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD),
                ("th32ProcessID", wt.DWORD), ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wt.DWORD), ("szExeFile", wt.WCHAR * 260)]


snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
pe = PROCESSENTRY32W()
pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
pids = {}
if kernel32.Process32FirstW(snap, ctypes.byref(pe)):
    while True:
        name = pe.szExeFile[:-4] if pe.szExeFile.lower().endswith(".exe") else pe.szExeFile
        if name in targets:
            pids.setdefault(name, []).append(pe.th32ProcessID)
        if not kernel32.Process32NextW(snap, ctypes.byref(pe)):
            break
kernel32.CloseHandle(snap)

print("目标进程:", {k: v for k, v in pids.items()})

found = {name: [] for name in pids}
owner = {pid: name for name, lst in pids.items() for pid in lst}


def cb(hwnd, _l):
    pid = wt.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value in owner:
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        txt = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, txt, 256)
        found[owner[pid.value]].append(
            (hwnd, cls.value, txt.value, bool(user32.IsWindowVisible(hwnd))))
    return True


user32.EnumWindows(WNDENUMPROC(cb), 0)

posted = 0
for name, wins in found.items():
    print(f"\n[{name}] 顶层窗口 {len(wins)} 个")
    for hwnd, cls, txt, vis in wins:
        print(f"   hwnd={hwnd:#x} class={cls!r} title={txt!r} visible={vis}")
    for hwnd, cls, txt, vis in wins:
        ok = user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        if ok:
            posted += 1
        print(f"   -> PostMessage WM_CLOSE hwnd={hwnd:#x} ok={bool(ok)}")

print(f"\n共投递 WM_CLOSE {posted} 次（未使用 TerminateProcess）")
time.sleep(8)
