# -*- coding: utf-8 -*-
"""解析周期判据轮：比的是"过均值次数"（周期），不是格均值。

判读规则（跑之前就该写死，别看完数再编）：
  * 配对（同一图案的 4:x:y 原件 vs FFmpeg 自己扩出来的 4:4:4 兄弟）——
    col 图案比 **H** 次数，row 图案比 **V** 次数，必须相等（±0）
  * 分叉（同图案不同位移）：a410row 与 a440row 的 V 次数必须差出 ~2 倍（2^2 vs 2^1）
    ；a410col 与 a420col 的 H 次数必须差出 ~2 倍（2^2 vs 2^1）
  * 噪声底：a420（早就在表里的行）配对必须也相等 —— 它不等就说明这台量具仍然不可用
"""
import os
import re
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "chr1"
LABELS = ["a410col", "r410col", "a410row", "r410row",
          "a440col", "r440col", "a440row", "r440row",
          "a420col", "r420col", "a420row", "r420row"]


def read(label, p):
    path = os.path.join(OUT, "scan_%s_p%d_%s.txt" % (label, p, SUFFIX))
    if not os.path.exists(path):
        return None
    t = open(path, encoding="utf-8", errors="replace").read()
    h = re.search(r"scan: H B=(-?\d+) R=(-?\d+) G=(-?\d+)", t)
    v = re.search(r"scan: V B=(-?\d+) R=(-?\d+) G=(-?\d+)", t)
    m = re.search(r"outBits=(\d+)", t)
    fi = re.search(r"frameIndex=(-?\d+)", t)
    return {"H": [int(x) for x in h.groups()] if h else None,
            "V": [int(x) for x in v.groups()] if v else None,
            "outBits": int(m.group(1)) if m else None,
            "frameIndex": int(fi.group(1)) if fi else None}


data = {(lab, p): read(lab, p) for lab in LABELS for p in (1, 2)}

print("=== 每臂过均值次数（B 通道；H=水平线，V=垂直线）")
print("%-9s %-22s %-22s %-8s %s" % ("label", "p1 H/V B", "p2 H/V B", "outBits", "frameIndex"))
for lab in LABELS:
    a, b = data[(lab, 1)], data[(lab, 2)]
    fmt = lambda d: "-" if (d is None or d["H"] is None or d["V"] is None) else "%d / %d" % (d["H"][0], d["V"][0])
    print("%-9s %-22s %-22s %-8s %s" % (lab, fmt(a), fmt(b),
          a["outBits"] if a else "-", a["frameIndex"] if a else "-"))


def cmp_pair(x, y, axis, note, expect_equal=True):
    vals = []
    for p in (1, 2):
        a, b = data[(x, p)], data[(y, p)]
        if not a or not b or a[axis] is None or b[axis] is None:
            vals.append(None)
            continue
        vals.append((a[axis][0], b[axis][0]))
    got = [v for v in vals if v]
    if not got:
        print("%-28s 不可算（缺臂）%s" % ("%s↔%s %s" % (x, y, axis), note))
        return None
    ok = all(v[0] == v[1] for v in got) if expect_equal else all(v[0] != v[1] for v in got)
    print("%-28s %s  读数 %s  %s" % ("%s↔%s %s" % (x, y, axis),
          "相等" if all(v[0] == v[1] for v in got) else "不等",
          "; ".join("%d vs %d" % v for v in got), note))
    return ok


print("\n=== 配对（原件 vs FFmpeg 自己扩的 4:4:4 兄弟；相等=我的位移与它一致）")
r1 = cmp_pair("a410col", "r410col", "H", "yuv410p 水平位移=2")
r2 = cmp_pair("a410row", "r410row", "V", "yuv410p 垂直位移=2")
r3 = cmp_pair("a440row", "r440row", "V", "yuv440p 垂直位移=1")
r4 = cmp_pair("a420col", "r420col", "H", "噪声底对照：yuv420p 水平位移=1")
r5 = cmp_pair("a420row", "r420row", "V", "噪声底对照：yuv420p 垂直位移=1")

print("\n=== 分叉（不同位移的同图案，必须不等，否则量具仍然没牙）")
d1 = cmp_pair("a410row", "a440row", "V", "2^2 vs 2^1 ⇒ 必须不等", expect_equal=False)
d2 = cmp_pair("a410col", "a420col", "H", "2^2 vs 2^1 ⇒ 必须不等", expect_equal=False)
d3 = cmp_pair("a420row", "r410row", "V", "位移 1 vs 2 的兄弟件互比 ⇒ 应不等", expect_equal=False)

verdicts = [v for v in (r1, r2, r3, r4, r5, d1, d2, d3) if v is not None]
print("\n判定: %s（%d/%d 项符合预期）" % ("全部符合" if all(verdicts) and len(verdicts) == 8
      else "有项不符 ⇒ 见上面逐行，不下结论", sum(verdicts), len(verdicts) or 8))
