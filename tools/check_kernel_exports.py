#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""列出 FFF.Native.dll 的导出符号，用于核对"本地专属导出"是否齐全 + API 版本。"""
import ctypes
import struct
import sys


def exports(path):
    data = open(path, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    mach, nsec, _, _, _, optsz, _ = struct.unpack_from("<HHIIIHH", data, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dd_off = opt + (112 if magic == 0x20B else 96)
    rva, size = struct.unpack_from("<II", data, dd_off)
    # 节表：找 .rdata 等所在节做 RVA->FOA
    secs = []
    so = opt + optsz
    for i in range(nsec):
        name = data[so + i * 40: so + i * 40 + 8].rstrip(b"\0")
        vs, va, rs, ra = struct.unpack_from("<IIII", data, so + i * 40 + 8)
        secs.append((va, vs, rs, ra))
    def r2o(r):
        # 用虚拟大小（不是 SizeOfRawData）界定范围，否则节区间重叠会错配
        for va, vs, rs, ra in secs:
            if va <= r < va + max(vs, rs):
                return ra + (r - va)
        return None
    o = r2o(rva)
    # IMAGE_EXPORT_DIRECTORY 是 10 个 DWORD（含 Major/Minor 合并），不是 8 个
    hdr = struct.unpack_from("<10I", data, o)
    _, _, _, _, _, _nfun, nname, _afun, aname, _aord = hdr
    names = []
    for i in range(nname):
        nr = struct.unpack_from("<I", data, r2o(aname) + i * 4)[0]
        s = r2o(nr)
        e = data.index(b"\0", s)
        names.append(data[s:e].decode("ascii", "replace"))
    return sorted(names)


for p in sys.argv[1:]:
    try:
        xs = exports(p)
    except Exception as ex:
        print("%s -> 解析失败: %s" % (p, ex))
        continue
    print("%s -> 导出 %d 个" % (p, len(xs)))
    for want in ("FFF3FP_Redraw", "FFF3FP_SetLogCallback", "FFF3FP_SetPresentConfig",
                 "FFF3FP_ReadVideoPixelRegion", "FFF3FP_GetRenderTargetInfo",
                 "FFF3FP_SetPacingConfig"):
        print("   %-32s %s" % (want, "YES" if want in xs else "no"))
    try:
        d = ctypes.CDLL(p)
        print("   API version = %d" % d.FFF3FP_GetApiVersion())
    except Exception as ex:
        print("   API version 读取失败: %s" % ex)
