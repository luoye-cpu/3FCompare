"""breaktrace 的正对照子进程（无 GUI、无弹窗，仅用于校验判据本身）。

做两件事，顺序很重要：
  1) 软件投递：kernel32!RaiseException  ->  ntdll!RtlRaiseException -> ntdll!NtRaiseException
     用 VEH 吞掉它，让进程活下来。
  2) 真硬件访存异常：读地址 0xFFFFFFFFFFFFFFFF
     由内核直接派发，不经过 RtlRaiseException；VEH 放行，进程就此终止。

判据预期：
  第 1 次异常  ⇒ 应命中 RtlRaiseException + NtRaiseException + KiUserExceptionDispatcher
  第 2 次异常  ⇒ 只应命中 KiUserExceptionDispatcher（无 RtlRaise/NtRaise）
若实际与预期不符，说明断点机制本身没生效，主实验的"未命中"结论不成立。
"""
import ctypes
import sys
import time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)

PROC = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.POINTER(ctypes.c_void_p))
k32.AddVectoredExceptionHandler.restype = ctypes.c_void_p
k32.AddVectoredExceptionHandler.argtypes = [ctypes.c_ulong, PROC]
k32.RaiseException.argtypes = [ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong,
                               ctypes.POINTER(ctypes.c_ulonglong)]

seen = []


def veh(info):
    rec = ctypes.cast(info, ctypes.POINTER(ctypes.c_void_p))[0]
    code = ctypes.c_uint32.from_address(rec).value
    seen.append(code)
    if code == 0xC0000005:
        return 0            # EXCEPTION_CONTINUE_SEARCH：放行，交给默认处理
    return -1               # EXCEPTION_CONTINUE_EXECUTION：吞掉软件 raise


cb = PROC(veh)
h = k32.AddVectoredExceptionHandler(1, cb)
print(f"veh installed h=0x{h:X}", flush=True)

# ---- 1) 软件投递 ---------------------------------------------------------
k32.RaiseException(0x12345678, 0, 0, None)
print("after RaiseException, veh saw: " + str([hex(c) for c in seen]), flush=True)

time.sleep(0.4)

# ---- 2) 真硬件访存异常 ---------------------------------------------------
print("now triggering real hardware AV (read of -1)", flush=True)
time.sleep(0.2)
buf = ctypes.create_string_buffer(1)
k32.RtlMoveMemory(buf, ctypes.c_void_p(0xFFFFFFFFFFFFFFFF), 1)
print("should not reach here", flush=True)
sys.exit(0)
