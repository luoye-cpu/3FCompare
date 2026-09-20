"""就地探针：这台机器上"用 DR0-DR3 下执行断点"到底生不生效？

背景：breaktrace 的正对照里，子进程明明走了 RaiseException（VEH 看到了 0x02345678），
但 RtlRaiseException / NtRaiseException / KiUserExceptionDispatcher 一个断点都没命中。
必须先排除"硬件断点根本没生效"这一可能，否则主实验的"未命中"结论无效。

做法：在当前进程里给自己下 DR0 = kernel32!Sleep 的执行断点，然后调一次 Sleep。
  * VEH 看到 STATUS_SINGLE_STEP ⇒ 硬件断点有效
  * VEH 什么都没看到          ⇒ 硬件断点无效（需退回 INT3 方案）
零弹窗、亚秒级、不改任何代码字节。
"""
import ctypes
import ctypes.wintypes as wt
import struct

k32 = ctypes.WinDLL("kernel32", use_last_error=True)

CTX_FLAGS = 0x30
CTX_DR0 = 0x48
CTX_DR6 = 0x68
CTX_DR7 = 0x70
CONTEXT_ALL = 0x00100000 | 0x01 | 0x02 | 0x08 | 0x10

PROC = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.POINTER(ctypes.c_void_p))
k32.AddVectoredExceptionHandler.restype = ctypes.c_void_p
k32.AddVectoredExceptionHandler.argtypes = [ctypes.c_ulong, PROC]
k32.GetCurrentThread.restype = wt.HANDLE
k32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
k32.SetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
k32.Sleep.argtypes = [wt.DWORD]

seen = []


def veh(info):
    rec = ctypes.cast(info, ctypes.POINTER(ctypes.c_void_p))[0]
    code = ctypes.c_uint32.from_address(rec).value
    addr = ctypes.c_void_p.from_address(rec + 0x10).value
    seen.append((hex(code), hex(addr)))
    return -1            # EXCEPTION_CONTINUE_EXECUTION


cb = PROC(veh)
hveh = k32.AddVectoredExceptionHandler(1, cb)
print(f"VEH 已装 h=0x{hveh:X}", flush=True)

h = k32.GetCurrentThread()
buf = ctypes.create_string_buffer(0x4D0 + 32)
base = ctypes.addressof(buf)
aligned = (base + 15) & ~15
off = aligned - base
ctypes.memset(aligned, 0, 0x4D0)

target = ctypes.cast(k32.Sleep, ctypes.c_void_p).value
print(f"目标 kernel32!Sleep = 0x{target:X}", flush=True)


def get_ctx():
    struct.pack_into("<I", buf, off + CTX_FLAGS, CONTEXT_ALL)
    return bool(k32.GetThreadContext(h, ctypes.c_void_p(aligned)))


def set_ctx():
    return bool(k32.SetThreadContext(h, ctypes.c_void_p(aligned)))


assert get_ctx(), "GetThreadContext 失败"
struct.pack_into("<Q", buf, off + CTX_DR0, target)
struct.pack_into("<Q", buf, off + CTX_DR7, 0x1)      # L0=1, RW=LEN=0（执行断点）
struct.pack_into("<Q", buf, off + CTX_DR6, 0)
print(f"SetThreadContext = {set_ctx()}", flush=True)

assert get_ctx()
dr0 = struct.unpack_from("<Q", buf, off + CTX_DR0)[0]
dr7 = struct.unpack_from("<Q", buf, off + CTX_DR7)[0]
print(f"回读 DR0=0x{dr0:X} DR7=0x{dr7:X}", flush=True)

k32.Sleep(1)
print(f"调用 Sleep 之后，VEH 看到: {seen}", flush=True)

assert get_ctx()
dr6 = struct.unpack_from("<Q", buf, off + CTX_DR6)[0]
print(f"DR6=0x{dr6:X}", flush=True)

# 收尾：清掉断点
struct.pack_into("<Q", buf, off + CTX_DR0, 0)
struct.pack_into("<Q", buf, off + CTX_DR7, 0)
set_ctx()
print("结论: " + ("硬件断点【有效】" if seen else "硬件断点【无效】—— 必须退回 INT3 方案"),
      flush=True)
