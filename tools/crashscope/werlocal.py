"""WER LocalDumps 开关（拿 minidump 用）。

⚠ 用完必须 `python werlocal.py disable` 删除注册表键，避免长期开启写满磁盘。

用法：
    python werlocal.py enable [--folder DIR] [--dumptype 1|2]
    python werlocal.py status
    python werlocal.py disable
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys

KEY = r"HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps"
DEFAULT_FOLDER = os.path.join(os.environ.get("LOCALAPPDATA", os.environ.get("TEMP", ".")),
                              "Temp", "crashscope", "dumps")


def _reg(*args):
    p = subprocess.run(["reg"] + list(args), capture_output=True, text=True)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def enable(folder: str, dumptype: int, count: int = 20):
    os.makedirs(folder, exist_ok=True)
    _reg("add", KEY, "/f")
    _reg("add", KEY, "/v", "DumpFolder", "/t", "REG_EXPAND_SZ", "/d", folder, "/f")
    _reg("add", KEY, "/v", "DumpType", "/t", "REG_DWORD", "/d", str(dumptype), "/f")
    _reg("add", KEY, "/v", "DumpCount", "/t", "REG_DWORD", "/d", str(count), "/f")
    print(f"已启用 LocalDumps: folder={folder} DumpType={dumptype} DumpCount={count}")
    print("⚠ 用完请执行： python werlocal.py disable")


def disable():
    rc, out = _reg("delete", KEY, "/f")
    print(f"已删除 {KEY} (rc={rc})")
    if rc != 0:
        print(out.strip())


def status():
    rc, out = _reg("query", KEY)
    print(out.strip() if out.strip() else f"(未配置，rc={rc})")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["enable", "disable", "status"])
    ap.add_argument("--folder", default=DEFAULT_FOLDER)
    ap.add_argument("--dumptype", type=int, default=1,
                    help="1=MiniDumpNormal（含线程栈，够用且小） 2=全内存（可能数 GB）")
    ap.add_argument("--count", type=int, default=20)
    a = ap.parse_args()
    if a.action == "enable":
        enable(a.folder, a.dumptype, a.count)
    elif a.action == "disable":
        disable()
    else:
        status()


if __name__ == "__main__":
    main()
