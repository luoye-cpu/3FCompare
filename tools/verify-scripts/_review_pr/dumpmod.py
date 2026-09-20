"""解析 minidump 模块表，定位 bugcheck 参数地址所属模块。

用法: python dumpmod.py <dump> [addr_hex ...]
"""
import struct
import sys

path = sys.argv[1]
targets = [int(a, 16) for a in sys.argv[2:]]
data = open(path, 'rb').read()

sig, ver, nstreams, dir_rva, checksum, ts, flags = struct.unpack_from('<IIIIIII', data, 0)
assert sig == 0x504D444D, hex(sig)
print(f"dump={path} version={ver} streams={nstreams} flags=0x{flags:x}")

streams = {}
off = dir_rva
for _ in range(nstreams):
    stype, dsize, rva = struct.unpack_from('<III', data, off)
    streams.setdefault(stype, (rva, dsize))
    off += 12

# 异常流
if 6 in streams:
    rva, _ = streams[6]
    tid, align = struct.unpack_from('<II', data, rva)
    (code, eflags, rec, addr, nparam) = struct.unpack_from('<IIQQI', data, rva + 8)
    params = struct.unpack_from('<15Q', data, rva + 8 + 28)
    print(f"[Exception] tid={tid} code=0x{code:08x} addr=0x{addr:016x} "
          f"params={[hex(p) for p in params[:nparam]]}")
    if addr:
        targets.append(addr)

# 模块表
mods = []
if 4 in streams:
    rva, _ = streams[4]
    n = struct.unpack_from('<I', data, rva)[0]
    print(f"[Modules] {n}")
    p = rva + 4
    for i in range(n):
        base, size, cs, ts2, name_rva = struct.unpack_from('<QIIII', data, p)
        p += 108
        ln = struct.unpack_from('<I', data, name_rva)[0]
        name = data[name_rva + 4:name_rva + 4 + ln].decode('utf-16-le', 'replace')
        mods.append((base, size, name, ts2))

mods.sort()

def find(addr):
    for base, size, name, _ in mods:
        if base <= addr < base + size:
            return base, size, name
    return None

import datetime
print("\n=== 故障地址归属 ===")
for t in targets:
    r = find(t)
    if r:
        base, size, name = r
        print(f"0x{t:016x} -> {name}  (base=0x{base:016x} size=0x{size:x} +0x{t-base:x})")
    else:
        print(f"0x{t:016x} -> 未命中任何模块")

print("\n=== 非微软内核模块（第三方驱动候选） ===")
sysnames = ('ntoskrnl', 'hal.dll', 'kd', 'mcupdate', 'werkernel', 'ntkrnlmp')
third = []
for base, size, name, ts2 in mods:
    low = name.lower().replace('\\', '/')
    if any(k in low for k in sysnames):
        continue
    if '/windows/system32/drivers/' in low or low.startswith('\\systemroot\\system32\\drivers\\'):
        # 系统自带驱动，但同时列出，便于核对时间戳
        pass
    try:
        t = datetime.datetime.utcfromtimestamp(ts2).strftime('%Y-%m-%d')
    except Exception:
        t = '?'
    third.append((base, size, name, t))
for base, size, name, t in third:
    low = name.lower().replace('\\', '/')
    is_ms = ('/windows/' in low) or low.startswith('\\systemroot')
    tag = 'MS  ' if is_ms else '3RD '
    print(f"{tag} 0x{base:016x} size=0x{size:07x} {t}  {name}")
