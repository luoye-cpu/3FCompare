import struct

path = r"C:\Windows\System32\dxgi.dll"
data = open(path, "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
nsec = struct.unpack_from("<H", data, pe+6)[0]
optsz = struct.unpack_from("<H", data, pe+20)[0]
opt = pe + 24
magic = struct.unpack_from("<H", data, opt)[0]
ddoff = opt + (0x60 if magic == 0x10b else 0x70)
exp_rva, exp_sz = struct.unpack_from("<II", data, ddoff)
sec = opt + optsz

def rva2off(rva):
    for i in range(nsec):
        o = sec + i*40
        vs, va, rs, pr = struct.unpack_from("<IIII", data, o+8)
        if va <= rva < va + max(vs, rs):
            return rva - va + pr
    return None

eo = rva2off(exp_rva)
nfun, nnam = struct.unpack_from("<II", data, eo+0x14)
afun_rva, anam_rva, aord_rva = struct.unpack_from("<III", data, eo+0x1C)
afun, anam, aord = rva2off(afun_rva), rva2off(anam_rva), rva2off(aord_rva)
print("PE32+? %s  nsec=%d  导出: nfun=%d nnam=%d" % (magic == 0x20b, nsec, nfun, nnam))

funcs = []
for i in range(nnam):
    no = rva2off(struct.unpack_from("<I", data, anam + i*4)[0])
    end = data.index(b"\0", no)
    name = data[no:end].decode("ascii", "replace")
    frva = struct.unpack_from("<I", data, afun + struct.unpack_from("<H", data, aord + i*2)[0]*4)[0]
    funcs.append((frva, name))
funcs.sort()

for target in (0x19530, 0x33AF0, 0x33AF3):
    best = None
    for frva, name in funcs:
        if frva <= target and (best is None or frva > best[0]):
            best = (frva, name)
    off = rva2off(target)
    print("\n崩溃 RVA %#x → 最近导出 %s + %#x（文件偏移 %#x）" % (target, best[1], target-best[0], off))
    if off:
        print("  崩溃点前 8 字节 + 后 16 字节:")
        print("   " + " ".join("%02X" % b for b in data[off-8:off]))
        print("   *" + " ".join("%02X" % b for b in data[off:off+16]))

# 顺带：该模块是否被打过补丁（比较磁盘文件与内存镜像不易做，这里只报文件自身特征）
print("\ndxgi.dll 文件大小 = %d 字节" % len(data))
