"""列出 DLL 里出现的依赖 DLL 名（快速定位延迟/静态加载缺失）。"""
import re
import sys
from pathlib import Path

for arg in sys.argv[1:]:
    p = Path(arg)
    if not p.exists():
        print(f"{p.name}: MISSING")
        continue
    data = p.read_bytes()
    names = sorted({m.decode() for m in re.findall(rb"[A-Za-z0-9_+\-.]+\.dll", data)
                    if not m.lower().startswith(b"api-ms")}, key=str.lower)
    print(f"{p.name} -> {names}")
