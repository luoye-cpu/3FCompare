#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
按 **DXGI 顺序**枚举适配器——与内核 A11 的 preferredAdapterIndex 语义完全一致
（内核用的是 IDXGIFactory1::EnumAdapters1 的索引）。

为什么需要它：A11 指定的是 DXGI 枚举序号，而"设备管理器里的顺序/型号名"
可能与 DXGI 索引不一致；有这张表才能准确地把某一路分配到某一张卡。

用法：
    python tools/enum_adapters.py
"""
import ctypes
import ctypes.wintypes as wt
import struct

DXGI_ADAPTER_DESC_SIZE = 8 + 4 + 4 + 4 + 4 + 8 + 256 * 2   # 结构内嵌 WCHAR Description[128]


class LUID(ctypes.Structure):
    _fields_ = [("LowPart", wt.DWORD), ("HighPart", ctypes.c_int32)]


class DXGI_ADAPTER_DESC1(ctypes.Structure):
    _fields_ = [
        ("Description", ctypes.c_wchar * 128),
        ("VendorId", ctypes.c_uint),
        ("DeviceId", ctypes.c_uint),
        ("SubSysId", ctypes.c_uint),
        ("Revision", ctypes.c_uint),
        ("DedicatedVideoMemory", ctypes.c_size_t),
        ("DedicatedSystemMemory", ctypes.c_size_t),
        ("SharedSystemMemory", ctypes.c_size_t),
        ("AdapterLuid", LUID),
        ("Flags", ctypes.c_uint),
    ]


IID_IDXGIFactory1 = (ctypes.c_ubyte * 16)(*bytes.fromhex(
    "770AAE78F26F4DBA829AFDCA96EB2AEB"))  # 77 0a ae 78 f2 6f 4d ba 82 9a fd ca 96 eb 2a eb
IID_IDXGIAdapter1 = (ctypes.c_ubyte * 16)(*bytes.fromhex(
    "29038F613D434BFD8F7B9E7B2B5B1E9A"))
# 上面的 GUID 字节序易错，直接改用字符串构造，见下方 create_factory()


def create_factory():
    # CreateDXGIFactory1(REFIID riid, void **ppFactory)
    dxgi = ctypes.WinDLL("dxgi")
    dxgi.CreateDXGIFactory1.restype = ctypes.c_long
    dxgi.CreateDXGIFactory1.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    # IID_IDXGIFactory1 = {770AAE78-F26F-4DBA-829A-FDCA96EB2AEB}
    iid = struct.pack("<IHH8B", 0x770AAE78, 0xF26F, 0x4DBA,
                      0x82, 0x9A, 0xFD, 0xCA, 0x96, 0xEB, 0x2A, 0xEB)
    factory = ctypes.c_void_p()
    # ⚠ 必须把 buffer 存成变量：若直接 cast 临时对象，调用期间它已被回收，
    #   IID 指针指向垃圾内存，表现为 CreateDXGIFactory1 返回 E_NOINTERFACE(0x80004002)。
    buf = ctypes.create_string_buffer(iid)
    hr = dxgi.CreateDXGIFactory1(ctypes.cast(buf, ctypes.c_void_p), ctypes.byref(factory))
    return hr, factory    # buf 需活到调用结束；这里 hr/factory 已拿到值，安全


def main():
    hr, factory = create_factory()
    if hr != 0 or not factory:
        print(f"CreateDXGIFactory1 失败: 0x{hr & 0xFFFFFFFF:08X}")
        return 2

    # IDXGIFactory1 的 vtable：IUnknown(3) + ... EnumAdapters1 在第 12 个槽（0-based）
    # IDXGIObject: SetPrivateData(3) SetPrivateDataInterface(4) GetPrivateData(5) GetParent(6)
    # IDXGIFactory: EnumAdapters(7) MakeWindowAssociation(8) GetWindowAssociation(9)
    #               CreateSwapChain(10) CreateSoftwareAdapter(11)
    # IDXGIFactory1: EnumAdapters1(12)
    vtable = ctypes.cast(factory, ctypes.POINTER(ctypes.c_void_p))[0]
    enum_fn = ctypes.cast(ctypes.c_void_p(
        ctypes.cast(vtable, ctypes.POINTER(ctypes.c_void_p))[12]),
        ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint,
                         ctypes.POINTER(ctypes.c_void_p)))

    print("DXGI 适配器（索引 = A11 preferredAdapterIndex 的取值）")
    print("-" * 92)
    print("%-4s %-42s %-8s %-10s %s" % ("索引", "描述", "vendor", "专用显存", "LUID"))
    idx = 0
    while True:
        adapter = ctypes.c_void_p()
        hr = enum_fn(factory, idx, ctypes.byref(adapter))
        if hr != 0 or not adapter:
            break
        av = ctypes.cast(adapter, ctypes.POINTER(ctypes.c_void_p))[0]
        get_desc = ctypes.cast(ctypes.c_void_p(
            ctypes.cast(av, ctypes.POINTER(ctypes.c_void_p))[10]),
            ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                             ctypes.POINTER(DXGI_ADAPTER_DESC1)))
        desc = DXGI_ADAPTER_DESC1()
        if get_desc(adapter, ctypes.byref(desc)) == 0:
            mem = desc.DedicatedVideoMemory / (1024 ** 3)
            print("%-4d %-42s 0x%04X     %6.1f GB   %08X:%08X" % (
                idx, desc.Description[:40], desc.VendorId, mem,
                desc.AdapterLuid.HighPart & 0xFFFFFFFF, desc.AdapterLuid.LowPart))
        idx += 1
    print("-" * 92)
    print(f"共 {idx} 个适配器。Microsoft Basic Render Driver 是纯软件（无硬解），不要分配业务路。")
    return 0


if __name__ == "__main__":
    sys_exit = __import__("sys").exit
    sys_exit(main())
