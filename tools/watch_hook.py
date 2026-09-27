r"""
实时监控：第三方 inline hook 是何时被写进 dxgi.dll 的
=====================================================
崩溃转储取证已确定：dxgi.dll +0x33AF0 的函数头 5 字节被改成 E9 跳转，
崩溃点固定落在 +0x33AF3。本工具在**活进程**里高频轮询该地址，
把"改写发生的时刻"与"进程启动/播放开始/崩溃"对齐，从而判断：
  · 钩子是在进程启动后多久装上的
  · 是装一次就稳定，还是反复装卸（反复装卸才构成竞态窗口）

用法:
  python tools/watch_hook.py [--routes 4] [--seconds 40] [--interval 30]
                             [--exe <3FCompare.exe>] [--media <视频>] [--strict]
  python tools/watch_hook.py --help
不需要 WinDbg，只用 ctypes 调 ReadProcessMemory。

退出码语义:
  默认       恒为 0 —— 本工具是"观测器"，被测进程崩不崩不是它自己的失败。
  --strict   观测到崩溃（退出码不属于 {0=OK, 1=DRIFT, 2=参数错误}）时返回 3，
             供脚本/门禁把"钩子竞态崩溃"当红处理；默认关闭以免破坏既有用法。
"""

import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys
import time
from datetime import datetime

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
LIST_MODULES_ALL = 0x03

k32.OpenProcess.restype = wt.HANDLE
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, wt.LPCVOID, wt.LPVOID, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
psapi.EnumProcessModulesEx.restype = wt.BOOL
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(wt.HANDLE), wt.DWORD,
                                       ctypes.POINTER(wt.DWORD), wt.DWORD]
psapi.GetModuleFileNameExW.restype = wt.DWORD
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, wt.HANDLE, wt.LPWSTR, wt.DWORD]
psapi.GetModuleInformation.restype = wt.BOOL
psapi.GetModuleInformation.argtypes = [wt.HANDLE, wt.HANDLE, ctypes.c_void_p, wt.DWORD]

# 关注的补丁点：RVA → 说明
PATCH_SITES = [
    (0x33AF0, "崩溃点所在函数（两份转储都命中这里）"),
    (0x10C408, "转储全量扫描发现的另一处 E9 改写"),
]

# 默认路径由本脚本位置反推仓库根，不再硬编码 C:\PLAN\...（换盘/换机器即失效）。
# 两者都可用 --exe / --media 覆盖。
_REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(_REPO, r"src\3FCompare\bin\Release\net11.0-windows\3FCompare.exe")
MEDIA = os.path.join(_REPO, r"testmedia\media\real\real_4k_hevc10_60m.mp4")


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p),
                ("SizeOfImage", wt.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


def list_modules(hproc):
    buf = (wt.HANDLE * 4096)()
    needed = wt.DWORD(0)
    if not psapi.EnumProcessModulesEx(hproc, buf, ctypes.sizeof(buf),
                                      ctypes.byref(needed), LIST_MODULES_ALL):
        return []
    n = min(needed.value // ctypes.sizeof(wt.HANDLE), 4096)
    out = []
    for i in range(n):
        hmod = buf[i]
        name = ctypes.create_unicode_buffer(4096)
        if psapi.GetModuleFileNameExW(hproc, hmod, name, 4096):
            mi = MODULEINFO()
            if psapi.GetModuleInformation(hproc, hmod, ctypes.byref(mi), ctypes.sizeof(mi)):
                out.append((mi.lpBaseOfDll or 0, mi.SizeOfImage, name.value))
    return out


def read_bytes(hproc, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    if k32.ReadProcessMemory(hproc, wt.LPCVOID(addr), buf, n, ctypes.byref(got)):
        return buf.raw[:got.value]
    return None


USAGE = """用法 / Usage:
  python tools/watch_hook.py [--routes N] [--seconds N] [--interval N]
                             [--exe PATH] [--media PATH] [--strict]

选项 / Options:
  --routes N    并行路数（默认 4）
  --seconds N   每路播放时长秒（默认 40）
  --interval N  轮询间隔毫秒（默认 30）
  --exe PATH    被测 exe（默认 <仓库>/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe）
  --media PATH  测试素材（默认 <仓库>/testmedia/media/real/real_4k_hevc10_60m.mp4）
  --strict      观测到崩溃时返回 3（默认恒返回 0，见模块 docstring）
  -h, --help    显示本帮助
"""


def main():
    routes = 4
    seconds = 40
    interval = 30          # ms
    exe = EXE
    media = MEDIA
    strict = False
    args = list(sys.argv[1:])
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a in ("-h", "--help"):
            print(USAGE)
            return 0
        elif a == "--routes" and nxt is not None:
            routes = int(nxt)
        elif a == "--seconds" and nxt is not None:
            seconds = int(nxt)
        elif a == "--interval" and nxt is not None:
            interval = int(nxt)
        elif a == "--exe" and nxt is not None:
            exe = nxt
        elif a == "--media" and nxt is not None:
            media = nxt
        elif a == "--strict":
            strict = True
        elif a.startswith("-"):
            print("未知参数 / unknown option: %s\n" % a, file=sys.stderr)
            print(USAGE, file=sys.stderr)
            return 2

    # 路径写错时报"崩溃 0x..."会把人带偏，故先显式拦掉。
    if not os.path.exists(exe):
        print("找不到被测 exe / exe not found: %s（可用 --exe 指定）" % exe, file=sys.stderr)
        return 2
    if not os.path.exists(media):
        print("找不到测试素材 / media not found: %s（可用 --media 指定）" % media, file=sys.stderr)
        return 2

    env = dict(os.environ)
    env.pop("_3FC_CRASH_TRACE", None)   # 追踪会写文件，这里不需要
    t0 = time.time()
    proc = subprocess.Popen([exe, "--multitest", media, str(routes), str(seconds)],
                            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print(f"启动 PID={proc.pid}  路数={routes} 时长={seconds}s 采样间隔={interval}ms")
    print(f"{'t(ms)':>8}  {'dxgi+0x33AF0 前8字节':<26} {'dxgi+0x10C408':<18} 事件")

    hproc = None
    dxgi_base = 0
    rtss_seen = None
    last = {}          # site -> bytes
    events = []
    interval_s = interval / 1000.0

    while True:
        alive = proc.poll() is None
        if alive and hproc is None:
            hproc = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, proc.pid)
        if alive and hproc and not dxgi_base:
            for base, size, name in list_modules(hproc):
                low = name.lower()
                if low.endswith("system32\\dxgi.dll") and not dxgi_base:
                    dxgi_base = base
                if "rtsshooks64.dll" in low and rtss_seen is None:
                    rtss_seen = int((time.time() - t0) * 1000)
                    print(f"{rtss_seen:>8}  {'':<26} {'':<18} ★ RTSSHooks64.dll 已注入")
        if alive and hproc and dxgi_base:
            for rva, desc in PATCH_SITES:
                b = read_bytes(hproc, dxgi_base + rva, 8)
                if b is None:
                    continue
                prev = last.get(rva)
                if prev != b:
                    ms = int((time.time() - t0) * 1000)
                    tag = "（首次采样）" if prev is None else "★★ 内容改变"
                    print(f"{ms:>8}  {b.hex(' '):<26} "
                          f"{(read_bytes(hproc, dxgi_base + PATCH_SITES[1][0], 8) or b'').hex(' '):<18} "
                          f"{tag} @ {desc}")
                    events.append((ms, rva, b.hex(" "), prev.hex(" ") if prev else None))
                    last[rva] = b
        if not alive:
            break
        time.sleep(interval_s)

    rc = proc.returncode
    ms = int((time.time() - t0) * 1000)
    kind = {0: "OK", 1: "DRIFT(漂移断言失败)", 2: "参数错误"}.get(rc, None)
    crashed = kind is None
    if crashed:
        kind = f"崩溃 0x{rc & 0xFFFFFFFF:08X}"
    print(f"{ms:>8}  进程退出 exit={rc} → {kind}")

    print("\n=== 时间线汇总 ===")
    if rtss_seen is not None:
        print(f"  RTSS 钩子 DLL 注入        : +{rtss_seen} ms")
    for ms_, rva, new, old in events:
        print(f"  +{ms_:>6} ms  dxgi+0x{rva:X}: {old} → {new}")
    print(f"  进程结束                  : +{ms} ms  ({kind})")
    print("\n提示：若改写只发生一次且远早于崩溃 → 不是竞态，是陈旧钩子；")
    print("      若改写反复来回（装上/还原）→ 竞态窗口，3FC 侧才可能规避。")
    # 默认仍返回 0（保持既有用法：本工具是观测器，不是判据）；
    # 加 --strict 后，观测到崩溃即返回非 0，供脚本/门禁直接当红处理。
    if crashed and strict:
        print("\n[--strict] 观测到崩溃 → 返回 3 / crash observed under --strict, exiting 3")
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
