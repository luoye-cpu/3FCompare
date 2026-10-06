"""读 TTF/OTF 的 family name（nameID=1），用于构造引用该字体的 ASS 测试。

用法：python tools/ttf_name.py <font.ttf> [...]
为什么需要：验证「内核会不会用 MKS 容器里的字体附件」时，ASS 必须引用**真实字体名**
才能区分「字体生效」与「回退 Arial」；字体名得从字体文件里读出来，不能猜。
"""
import struct
import sys
from pathlib import Path


def family_names(path: Path):
    data = path.read_bytes()
    if data[:4] in (b"\x00\x01\x00\x00", b"true", b"ttcf"):
        num_tables = struct.unpack(">H", data[4:6])[0]
        base = 12
    elif data[:4] == b"OTTO":
        num_tables = struct.unpack(">H", data[4:6])[0]
        base = 12
    else:
        return []
    name_offset = None
    for i in range(num_tables):
        rec = data[base + i * 16: base + i * 16 + 16]
        if len(rec) < 16:
            break
        tag = rec[:4]
        offset = struct.unpack(">I", rec[8:12])[0]
        if tag == b"name":
            name_offset = offset
            break
    if name_offset is None:
        return []
    count = struct.unpack(">H", data[name_offset + 2:name_offset + 4])[0]
    storage = struct.unpack(">H", data[name_offset + 4:name_offset + 6])[0]
    found = []
    for i in range(count):
        rec = data[name_offset + 6 + i * 12: name_offset + 6 + i * 12 + 12]
        platform_id, _enc, lang_id, name_id, length, off = struct.unpack(">HHHHHH", rec)
        if name_id != 1:
            continue
        raw = data[name_offset + storage + off: name_offset + storage + off + length]
        if platform_id == 3:  # Windows: UTF-16BE
            text = raw.decode("utf-16-be", errors="replace")
        elif platform_id == 1:  # Mac: UTF-8 / ASCII
            text = raw.decode("utf-8", errors="replace")
        else:
            continue
        found.append((platform_id, lang_id, text.strip("\x00")))
    return found


for arg in sys.argv[1:]:
    p = Path(arg)
    print(f"=== {p.name} ===")
    for platform_id, lang_id, text in family_names(p):
        print(f"  platform={platform_id} lang={lang_id:#06x} name={text!r}")
