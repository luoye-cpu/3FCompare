#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 PDB 提取公共符号表，把「模块 + RVA」解析成「函数名 + 偏移」。

为什么不用 dbghelp：本机的 dbghelp 对这份 FFF.Native.dll 只肯装载导出符号
（SymGetModuleInfo64 返回 SymType=SymExport、LoadedPdbName 为空），推测是
DLL 的 CodeView GUID 与旁边那份 PDB 不匹配（PDB 比 DLL 更新时间晚 5 小时）。
pdbparse 又依赖旧版 construct，装不上。所以这里直接读 MSF 容器。

只需要走通这条链：
  Superblock(MSF 7.0) → 流目录 → 流1(PDB Info: GUID/Age) → 流3(DBI 头: PublicStreamIndex)
  → Publics 流(PSGSIHDR + S_PUB32 记录) → 用 DLL 的节表把 seg:off 换算成 RVA

用法：
    python tools/pdb_resolve.py <foo.pdb> <foo.dll> <rva-hex> [更多 rva...]
    rva 支持 0x 前缀或纯十六进制。
"""

import bisect
import struct
import sys

MSF_MAGIC = b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\x00\x00\x00"
S_PUB32 = 0x110E


class Pdb:
    def __init__(self, pdb_path, dll_path):
        with open(pdb_path, "rb") as f:
            self.data = f.read()
        if self.data[:32] != MSF_MAGIC:
            raise ValueError("不是 MSF 7.0 容器：%s" % pdb_path[:32])
        (self.block_size, _free_map, _nblocks, self._num_dir_bytes, _unk,
         self.block_map_addr) = struct.unpack_from("<IIIIII", self.data, 32)
        self.streams = self._read_directory()
        self.sections = read_sections(dll_path)
        self.symbols = []          # [(rva, name), ...] 已按 rva 排序
        self._load_publics()

    def _blocks(self, start_block, nbytes):
        """按块索引表取回一个流的字节内容。"""
        per = self.block_size
        out = bytearray()
        idx_off = start_block * per
        need = (nbytes + per - 1) // per
        for i in range(need):
            b = struct.unpack_from("<I", self.data, idx_off + i * 4)[0]
            chunk = self.data[b * per:(b + 1) * per]
            out += chunk
        return bytes(out[:nbytes])

    def _read_directory(self):
        # 流目录的块索引数组位于 block_map_addr 指向的块里，
        # 条目数 = ceil(NumDirectoryBytes / BlockSize)。
        ndir_blocks = (self._num_dir_bytes + self.block_size - 1) // self.block_size
        idx_off = self.block_map_addr * self.block_size
        blocks = [struct.unpack_from("<I", self.data, idx_off + i * 4)[0]
                  for i in range(ndir_blocks)]
        db = b"".join(self.data[b * self.block_size:(b + 1) * self.block_size]
                      for b in blocks)[:self._num_dir_bytes]
        pos = 0
        nstreams = struct.unpack_from("<I", db, pos)[0]
        pos += 4
        sizes = []
        for _ in range(nstreams):
            s = struct.unpack_from("<I", db, pos)[0]
            pos += 4
            sizes.append(s)
        out = []
        for s in sizes:
            if s in (0, 0xFFFFFFFF):
                out.append(b"")
                continue
            nblk = (s + self.block_size - 1) // self.block_size
            blocks = []
            for _ in range(nblk):
                blocks.append(struct.unpack_from("<I", db, pos)[0])
                pos += 4
            buf = b"".join(self.data[b * self.block_size:(b + 1) * self.block_size]
                           for b in blocks)
            out.append(buf[:s])
        return out

    def _u32(self, off):
        return struct.unpack_from("<I", self.data, off)[0]

    @property
    def guid_age(self):
        info = self.streams[1] if len(self.streams) > 1 else b""
        if len(info) >= 28:
            _ver, _sig, age = struct.unpack_from("<III", info, 0)
            return info[12:28], age
        return b"", 0

    def _load_publics(self):
        if len(self.streams) <= 3:
            return
        dbi = self.streams[3]
        if len(dbi) < 64:
            return
        # DBI 头布局：VerSig(0) VerHdr(4) Age(8) GlobalStream(12) BuildNumber(14)
        #   PublicStream(16) PdbDllVersion(18) SymRecordStream(20)
        # 注意：真正的 CodeView 符号记录（含 S_PUB32）在 **SymRecordStream**，
        # PublicStream 只是按地址索引的哈希表——曾有版本错读 PublicStream，得到 0 条。
        fields = struct.unpack_from("<IIIHHHHH", dbi, 0)
        sym_index = fields[7]
        if sym_index == 0xFFFF or sym_index >= len(self.streams):
            return
        buf = self.streams[sym_index]
        got = []
        pos, n = 0, len(buf)
        while pos + 4 <= n:
            reclen, rectype = struct.unpack_from("<HH", buf, pos)
            if reclen < 2 or pos + 2 + reclen > n:
                # 容错了再扫：遇到非法记录按 2 字节重同步，不要整段放弃
                pos += 2
                continue
            if rectype == S_PUB32 and reclen >= 11:
                _flags, off, seg = struct.unpack_from("<IIH", buf, pos + 4)
                name = buf[pos + 14:pos + 2 + reclen].split(b"\x00")[0]
                rva = self._rva(seg, off)
                if rva is not None:
                    got.append((rva, demangle(name.decode("utf-8", "replace"))))
            pos += 2 + reclen
        got.sort()
        self.symbols = got

    def _rva(self, seg, off):
        if seg == 0 or seg > len(self.sections):
            return None
        va, _vsz = self.sections[seg - 1]
        return va + off

    def lookup(self, rva):
        if not self.symbols:
            return None
        i = bisect.bisect_right(self.symbols, (rva, "\uffff")) - 1
        if i < 0:
            return None
        base, name = self.symbols[i]
        return name, rva - base


_UNDNAME = None


def _undname_init():
    global _UNDNAME
    if _UNDNAME is not None:
        return _UNDNAME
    try:
        import ctypes
        d = ctypes.WinDLL("dbghelp")
        d.UnDecorateSymbolName.argtypes = [ctypes.c_char_p, ctypes.c_char_p,
                                           ctypes.c_uint, ctypes.c_uint]
        d.UnDecorateSymbolName.restype = ctypes.c_uint
        _UNDNAME = d
    except Exception:
        _UNDNAME = False
    return _UNDNAME


def demangle(name):
    """把 MSVC 修饰名还原成可读签名（失败时原样返回）。"""
    d = _undname_init()
    if not d:
        return name
    try:
        import ctypes
        buf = ctypes.create_string_buffer(2048)
        n = d.UnDecorateSymbolName(name.encode("utf-8", "replace"), buf,
                                   2048, 0x0001 | 0x1000 | 0x0800)  # UNDNAME_COMPLETE
        if n:
            return buf.value.decode("utf-8", "replace")
    except Exception:
        pass
    return name


def read_sections(dll_path):
    """读 PE 节表，返回 [(VirtualAddress, VirtualSize), ...]（按节序号）。"""
    with open(dll_path, "rb") as f:
        d = f.read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt_size = struct.unpack_from("<H", d, pe + 20)[0]
    so = pe + 24 + opt_size
    out = []
    for i in range(nsec):
        b = so + i * 40
        vsz, va = struct.unpack_from("<II", d, b + 8)
        out.append((va, vsz))
    return out


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    pdb, dll = sys.argv[1], sys.argv[2]
    p = Pdb(pdb, dll)
    guid, age = p.guid_age
    print("PDB: %s" % pdb)
    print("  GUID 前8字节=%s age=%d  公共符号 %d 个  节数 %d" % (
        guid[:8].hex().upper(), age, len(p.symbols), len(p.sections)))
    for a in sys.argv[3:]:
        try:
            rva = int(a, 16)
        except ValueError:
            print("  ? 无法解析: %s" % a)
            continue
        r = p.lookup(rva)
        print("  +0x%-8X %s" % (rva, ("%s + 0x%X" % r) if r else "<未找到符号>"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
