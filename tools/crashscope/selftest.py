"""minidump 解析器自检：构造一个合成 MDMP，往返验证结构体偏移是否正确。

真 dump 到手前先把偏移算对——Thread/Exception/Module 三处结构体偏移写错时，
解析结果会"看起来正常"但全是错的，必须用合成样本卡死。

用法： python selftest.py
"""
from __future__ import annotations

import os
import struct
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import attribute as at  # noqa: E402
import minidump as md  # noqa: E402

MOD_BASE = 0x7FF000000000
MOD_SIZE = 0x1000
MOD_NAME = r"C:\fake\FFF.Native.dll"
STACK_BASE = 0x0000000000010000
STACK_SIZE = 0x1000
CTX_RIP_V = 0xDEADBEEFCAFE
CTX_RSP_V = STACK_BASE + 0x80
WILD_ADDR = 0xDEADBEEFCAFE
GOOD_PTR = MOD_BASE + 0x500
GOOD_PTR2 = MOD_BASE + 0x700


def build(path: str):
    hdr_size = 32
    dir_size = 4 * 12
    off_mod = hdr_size + dir_size                      # 80
    mod_blob = 4 + 108                                 # 112
    off_thr = off_mod + mod_blob                       # 192
    thr_blob = 4 + 48                                  # 52
    off_exc = off_thr + thr_blob                       # 244
    exc_blob = 168
    off_mem = off_exc + exc_blob                       # 412
    mem_blob = 4 + 16                                  # 20
    off_stack = off_mem + mem_blob                     # 432
    off_ctx = off_stack + STACK_SIZE                   # 4528
    ctx_size = 0x4D0
    off_name = off_ctx + ctx_size

    name_bytes = MOD_NAME.encode("utf-16-le")
    total = off_name + 4 + len(name_bytes)

    buf = bytearray(total)
    # header
    struct.pack_into("<IIIIIII", buf, 0, md.MINIDUMP_SIGNATURE, 0xA793, 4,
                     hdr_size, 0, 0, 0)
    # directory
    for k, (st, dsz, rva) in enumerate([
        (md.ST_MODULE_LIST, mod_blob, off_mod),
        (md.ST_THREAD_LIST, thr_blob, off_thr),
        (md.ST_EXCEPTION, exc_blob, off_exc),
        (md.ST_MEMORY_LIST, mem_blob, off_mem),
    ]):
        struct.pack_into("<III", buf, hdr_size + k * 12, st, dsz, rva)

    # module list
    struct.pack_into("<I", buf, off_mod, 1)
    p = off_mod + 4
    struct.pack_into("<QIIII", buf, p, MOD_BASE, MOD_SIZE, 0, 0x12345678, off_name)
    # 其余 52 字节 VS_FIXEDFILEINFO + 16 字节 Cv/Misc/Reserved 留 0

    # thread list
    struct.pack_into("<I", buf, off_thr, 1)
    struct.pack_into("<IIIIQQIIII", buf, off_thr + 4,
                     0x1234, 0, 0, 0, 0,
                     STACK_BASE, STACK_SIZE, off_stack,
                     ctx_size, off_ctx)

    # exception stream: ThreadId(4) __alignment(4) ExceptionRecord(152) ThreadContext(8)
    struct.pack_into("<II", buf, off_exc, 0x1234, 0)
    er = off_exc + 8
    struct.pack_into("<IIQQI", buf, er, 0xC0000005, 0, 0, WILD_ADDR, 2)
    struct.pack_into("<QQ", buf, er + 32, 8, WILD_ADDR)   # params[0]=8(execute), [1]=target

    # memory list：一段栈
    struct.pack_into("<I", buf, off_mem, 1)
    struct.pack_into("<QII", buf, off_mem + 4, STACK_BASE, STACK_SIZE, off_stack)

    # 栈内容：槽 0 = 野地址，槽 1/2 = 模块内指针，槽 3 = 普通数值
    struct.pack_into("<Q", buf, off_stack + 0, 0x1)
    struct.pack_into("<Q", buf, off_stack + 8, GOOD_PTR)
    struct.pack_into("<Q", buf, off_stack + 16, GOOD_PTR2)
    struct.pack_into("<Q", buf, off_stack + 24, 0x4141414141414141)

    # CONTEXT：只填 Rip/Rsp
    struct.pack_into("<I", buf, off_ctx + md.CTX_CONTEXT_FLAGS, 0x10001F)
    struct.pack_into("<Q", buf, off_ctx + md.CTX_RSP, CTX_RSP_V)
    struct.pack_into("<Q", buf, off_ctx + md.CTX_RIP, CTX_RIP_V)

    # 模块名字符串
    struct.pack_into("<I", buf, off_name, len(name_bytes))
    buf[off_name + 4: off_name + 4 + len(name_bytes)] = name_bytes

    open(path, "wb").write(bytes(buf))
    return off_name


def main():
    tmp = os.path.join(tempfile.gettempdir(), "crashscope_selftest.dmp")
    build(tmp)
    d = md.Dump(tmp)
    fails = []

    def chk(name, got, want):
        ok = got == want
        print(f"  [{'OK ' if ok else 'FAIL'}] {name}: got={got!r} want={want!r}")
        if not ok:
            fails.append(name)

    print("=== 合成 MDMP 往返自检 ===")
    chk("模块数", len(d.modules), 1)
    m = d.modules.find(GOOD_PTR)
    chk("模块归属命中", m.name if m else None, "FFF.Native.dll")
    chk("模块基址", m.base if m else None, MOD_BASE)
    chk("模块大小", m.size if m else None, MOD_SIZE)
    chk("越界不命中", d.modules.find(MOD_BASE + MOD_SIZE), None)
    chk("未加载地址不命中", d.modules.find(WILD_ADDR), None)

    e = d.exception
    chk("异常码", e.code, 0xC0000005)
    chk("异常地址", e.address, WILD_ADDR)
    chk("异常参数", e.params, [8, WILD_ADDR])
    chk("访问性质", e.av_kind, "EXECUTE")
    chk("线程号", e.thread_id, 0x1234)

    t = d.crash_thread()
    chk("崩溃线程 RSP", t.rsp, CTX_RSP_V)
    chk("崩溃线程 RIP", t.rip, CTX_RIP_V)
    chk("栈基址", t.stack_start, STACK_BASE)

    rsp, rip, sbase, sdata = d.crash_stack()
    chk("栈字节数", len(sdata), STACK_SIZE)
    hits = at.scan_stack(d.modules, rsp, sbase, sdata)
    chk("栈命中模块次数", len(hits), 2)
    if len(hits) == 2:
        chk("命中1 rva", hits[0]["rva"], 0x500)
        chk("命中2 rva", hits[1]["rva"], 0x700)
    seq = at.collapse(hits)
    chk("合并后段数", len(seq), 1)
    chk("合并后模块", seq[0]["module"] if seq else None, "FFF.Native.dll")
    chk("合并后计数", seq[0]["count"] if seq else None, 2)

    # 译码器抽查
    chk("译码 mov", at.decode_at(bytes.fromhex("48895c2410")).split(",")[0], "mov [rsp+0x10]")
    chk("译码 call [rax]", at.decode_at(bytes.fromhex("ff10")), "call [rax]  <-- 间接跳转（经坏指针跳到野地址的典型形态）")
    chk("译码 ud2", at.decode_at(bytes.fromhex("0f0b")).startswith("ud2"), True)
    chk("译码 int3", at.decode_at(bytes.fromhex("cc")).startswith("int3"), True)

    print()
    if fails:
        print(f"自检失败 {len(fails)} 项：{fails}")
        return 1
    print("全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
