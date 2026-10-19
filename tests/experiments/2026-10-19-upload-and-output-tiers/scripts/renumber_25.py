# -*- coding: utf-8 -*-
"""把 §26/§27 缩成 §25/§26（账本原名 25，被我改名成 27 后序号出现空洞）。

字节级替换 + CRLF 计数断言：这棵树里 sed/Get-Content 都会改行尾（本仓踩过）。
每条锚点必须恰好命中一次，否则不动盘。
"""
import io
import sys

P = r"C:\PLAN\3FCompare\docs\交接文档-2026-10-18-上传带宽定因.md"
raw = open(P, "rb").read()
before_crlf = raw.count(b"\r\n")
before_lf = raw.count(b"\n")
# 纯 CRLF（crlf==lf）或纯 LF（crlf==0）都算合法；只有混排才停。
# 上一版这里写成 crlf == lf，把合法的本仓纯 LF 文件当成"混行尾"拦掉了——判据自己错。
if not (before_crlf == before_lf or before_crlf == 0):
    print("ABORT: 混行尾 CRLF=%d LF=%d" % (before_crlf, before_lf))
    sys.exit(1)
print("行尾形态: %s" % ("CRLF" if before_crlf else "LF"))

PAIRS = [
    ("## 26. 输出定形", "## 25. 输出定形"),
    ("### 26.1 ", "### 25.1 "),
    ("### 26.2 ", "### 25.2 "),
    ("### 26.3 ", "### 25.3 "),
    ("### 26.4 ", "### 25.4 "),
    ("### 26.5 ", "### 25.5 "),
    ("## 27. 账本", "## 26. 账本"),
    ("- [x] §26 输出三档定形", "- [x] §25 输出三档定形"),
]

t = raw.decode("utf-8")
for old, new in PAIRS:
    n = t.count(old)
    if n != 1:
        print("ABORT: %r 命中 %d 次（要 1）" % (old, n))
        sys.exit(1)
    t = t.replace(old, new, 1)
    print("  OK", old)

out = t.encode("utf-8")
open(P, "wb").write(out)
chk = open(P, "rb").read()
assert chk.count(b"\n") == before_lf, "行数被动过"
assert chk.count(b"\r\n") == before_crlf, "行尾形态被动过"
print("写回完成，LF=%d CRLF=%d（与改前一致）" % (chk.count(b"\n"), chk.count(b"\r\n")))
