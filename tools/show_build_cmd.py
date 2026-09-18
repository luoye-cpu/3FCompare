#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印 MSBuild 上次构建 FFF.Native 时实际下发给 cl.exe / link.exe / rc.exe 的命令行。

tlog 是 UTF-16LE，且以 '^' 分段（'^<源文件>\r\n<命令行>\r\n'），直接 cat 看不到东西。
用途：核对"手工复刻的编译参数"与"官方 MSBuild 参数"是否一致。
"""
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
TLOG = os.path.join(ROOT, "third_party", "fff_project", "FFF.Native",
                    "obj", "x64", "Release", "FFF.Native.tlog")


def read_tlog(name):
    path = os.path.join(TLOG, name)
    if not os.path.exists(path):
        return None
    raw = open(path, "rb").read()
    # tlog 可能是 UTF-16LE 也可能是 UTF-8(BOM)。两种编码下 '^' 与 ASCII 参数都不会被吃掉：
    # UTF-16 用 utf-8 解会得到 '^' + '\x00' + ASCII，去 \x00 即可；UTF-8 直接用。
    # 按 BOM 判断真实编码：tlog 几乎总是 UTF-16LE（FF FE）。
    # 若误按 UTF-8 解，BOM 会变成两个 U+FFFD 落在首个 '^' 之前，形成一个"空命令行"假块。
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8-sig", errors="replace").replace("\x00", "")


def blocks(text):
    return [b for b in (text or "").split("^") if b.strip()]


def main():
    for name, label in (("CL.command.1.tlog", "cl.exe"),
                        ("rc.command.1.tlog", "rc.exe"),
                        ("link.command.1.tlog", "link.exe")):
        text = read_tlog(name)
        bs = blocks(text)
        print("=" * 78)
        print(f"{label}  —— {name}（共 {len(bs)} 条）")
        if not bs:
            print("  （缺失）")
            continue
        lines = [l for l in bs[0].replace("\r\n", "\n").split("\n") if l.strip()]
        print(f"  输入: {lines[0].strip()}")
        print("  命令行:")
        cmd = lines[1].strip() if len(lines) > 1 else ""
        # 只做折行，不改内容
        cur = "    "
        for tok in cmd.split(" "):
            if len(cur) + len(tok) > 150:
                print(cur)
                cur = "    " + tok + " "
            else:
                cur += tok + " "
        print(cur)
    return 0


if __name__ == "__main__":
    sys.exit(main())
