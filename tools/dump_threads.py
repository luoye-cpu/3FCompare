#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
列出 minidump 里每个线程的栈内模块构成，用于判断"崩溃那一刻还有谁在 D3D/DXGI 里"。

用途：issue #7 的关键分歧——崩溃到底来自单个渲染器内部的锁竞态（PR #8 已修），
还是来自**多个渲染器并发进入 dxgi**（PR #8 根本没碰）。若崩溃时另有线程正停在
dxgi.dll / d3d11.dll，后者成立。

用法：
    python tools/dump_threads.py <dump.dmp> [--top N]
"""
import os
import struct
import sys

MINIDUMP_THREAD_SIZE = 48          # x64
WANT = ("dxgi.dll", "d3d11.dll", "fff.native.dll", "coreclr.dll")


def streams(data):
    n = struct.unpack_from("<I", data, 8)[0]
    dirrva = struct.unpack_from("<I", data, 12)[0]
    out = {}
    for i in range(n):
        t, sz, rva = struct.unpack_from("<III", data, dirrva + i * 12)
        out[t] = (rva, sz)
    return out


def modules(data, rva):
    cnt = struct.unpack_from("<I", data, rva)[0]
    mods = []
    for k in range(cnt):
        off = rva + 4 + k * 108
        base, size = struct.unpack_from("<QI", data, off)
        name_rva = struct.unpack_from("<I", data, off + 20)[0]
        if not name_rva:
            continue
        ln = struct.unpack_from("<I", data, name_rva)[0]
        name = data[name_rva + 4:name_rva + 4 + ln].decode("utf-16-le", "replace")
        mods.append((base, size, os.path.basename(name).lower()))
    return mods


def module_of(addr, mods):
    for base, size, name in mods:
        if base <= addr < base + size:
            return name
    return None


def main():
    argv = sys.argv[1:]
    want_tid = None
    out_prefix = None
    if "--stack" in argv:
        i = argv.index("--stack")
        want_tid = int(argv[i + 1])
        out_prefix = argv[i + 2] if i + 2 < len(argv) and not argv[i + 2].startswith("--") else f"tid{want_tid}"
        argv = argv[:i] + argv[i + 3:]
    args = [a for a in argv if not a.startswith("--")]
    path = args[0] if args else None
    if not path:
        print(__doc__)
        return 2
    data = open(path, "rb").read()
    st = streams(data)
    if 4 not in st or 3 not in st:
        print("缺少 ModuleList 或 ThreadList 流")
        return 2
    mods = modules(data, st[4][0])

    trva = st[3][0]
    cnt = struct.unpack_from("<I", data, trva)[0]
    print(f"模块 {len(mods)} 个，线程 {cnt} 个")
    print("%-8s %-8s %-6s %-6s %-6s %s" % ("TID", "栈字节", "dxgi", "d3d11", "内核", "栈内模块（去重，自底向上）"))

    hit = {"dxgi.dll": 0, "d3d11.dll": 0, "fff.native.dll": 0}
    rows = []
    for i in range(cnt):
        off = trva + 4 + i * MINIDUMP_THREAD_SIZE
        tid = struct.unpack_from("<I", data, off)[0]
        stack_start = struct.unpack_from("<Q", data, off + 24)[0]
        data_size, rva = struct.unpack_from("<II", data, off + 32)
        names = []
        seen = set()
        for p in range(0, max(0, data_size - 7), 8):
            val = struct.unpack_from("<Q", data, rva + p)[0]
            if val < 0x10000:
                continue
            nm = module_of(val, mods)
            if nm and nm not in seen:
                seen.add(nm)
                names.append(nm)
        flags = {w: (w in seen) for w in WANT}
        for w in hit:
            if flags.get(w):
                hit[w] += 1
        # 该线程的 RIP（ThreadContext 在 MINIDUMP_THREAD +40：DataSize/Rva）
        rip = 0
        rip_mod = None
        rsp = 0
        ctx_size, ctx_rva = struct.unpack_from("<II", data, off + 40)
        if ctx_rva and ctx_size >= 0x100:
            # x64 CONTEXT：Rip @0xF8、Rsp @0x98（本机实测与 MSDN 一致）
            rip = struct.unpack_from("<Q", data, ctx_rva + 0xF8)[0]
            rsp = struct.unpack_from("<Q", data, ctx_rva + 0x98)[0]
            rip_mod = module_of(rip, mods)

        # 导出指定线程的栈，供 stack_resolve.py 符号化（看清它在内核侧调用的是谁）
        if want_tid is not None and tid == want_tid:
            blob = data[rva:rva + data_size]
            with open(out_prefix + ".stack.bin", "wb") as fh:
                fh.write(blob)
            with open(out_prefix + ".meta.txt", "w", encoding="utf-8") as fh:
                fh.write(f"tid={tid}\nrip=0x{rip:X}\nrsp=0x{rsp:X}\nrbp=0x0\n"
                         f"stack_file={os.path.basename(out_prefix)}.stack.bin\n"
                         f"stack_bytes={data_size}\nread_ok=1\n")
            print(f"[已导出] TID={tid} 栈 {data_size} 字节 -> {out_prefix}.stack.bin "
                  f"(RIP=0x{rip:X} RSP=0x{rsp:X})")
        rows.append((tid, data_size, flags, names, rip, rip_mod))

    # 只展示与 D3D/DXGI/内核相关的线程，其余略过（154 个线程全列没有意义）
    for tid, size, flags, names, rip, rip_mod in rows:
        if not (flags["dxgi.dll"] or flags["d3d11.dll"] or flags["fff.native.dll"]):
            continue
        if rip_mod:
            where = f"{rip_mod} +0x{rip - next(b for b, s, n in mods if n == rip_mod):X}"
        else:
            where = f"0x{rip:X}（无模块）"
        print("%-8d %-8d %-6s %-6s %-6s %-40s %s" % (
            tid, size,
            "Y" if flags["dxgi.dll"] else ".",
            "Y" if flags["d3d11.dll"] else ".",
            "Y" if flags["fff.native.dll"] else ".",
            where,
            " ".join(n for n in names if n in WANT)[:30]))

    print()
    print("含 dxgi.dll 的线程数 = %d；含 d3d11.dll = %d；含 FFF.Native.dll = %d" % (
        hit["dxgi.dll"], hit["d3d11.dll"], hit["fff.native.dll"]))
    print("FFF.Native 线程的 RIP（可喂给 pdb_resolve.py 得函数名）:")
    for tid, size, flags, names, rip, rip_mod in rows:
        if rip_mod == "fff.native.dll":
            base = next(b for b, s, n in mods if n == "fff.native.dll")
            print("  TID=%d  RIP=0x%X  RVA=0x%X" % (tid, rip, rip - base))
    return 0


if __name__ == "__main__":
    sys.exit(main())
