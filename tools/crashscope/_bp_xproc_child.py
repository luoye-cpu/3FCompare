"""跨进程断点探针的子进程：循环调 kernel32!Sleep（保证目标函数必然被执行）。"""
import ctypes
import sys

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.Sleep.argtypes = [ctypes.c_ulong]

print("child up, pid=%d" % k32.GetCurrentProcessId(), flush=True)
for i in range(60):
    k32.Sleep(50)
print("child done", flush=True)
sys.exit(0)
