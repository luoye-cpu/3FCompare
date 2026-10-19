# -*- coding: utf-8 -*-
"""解析像素判据轮：先验"两趟同一画面"这个前提，再算配对格均值差。

顺序很重要：如果同一枚素材两趟的网格就对不上，说明钉帧没成功，那后面所有"配对差"
都不可解释 —— 这时必须报"未判"，不能拿差值编故事。
"""
import os
import re
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "par1"
LABELS = ["a410", "r410", "a440", "r440", "a420", "r420"]


def read_arm(label, p):
    path = os.path.join(OUT, "grid_%s_p%d_%s.txt" % (label, p, SUFFIX))
    if not os.path.exists(path):
        return None
    text = open(path, encoding="utf-8", errors="replace").read()
    seek = re.search(r"seek: rc=(-?\d+) frame=(\d+)(?: pauseRc=(-?\d+))? pos100ns=(-?\d+) presented=(\d+)(?: state=(-?\d+))?", text)
    grid = re.search(r"grid: outBits=(\d+) rect=(\d+)x(\d+)@(\d+),(\d+) cells=(\d+)", text)
    cells = []
    for line in text.splitlines():
        if not line.startswith("G"):
            continue
        parts = [x.strip() for x in line[1:].split("|")[1:] if x.strip()]
        for part in parts:
            nums = part.split(",")
            if len(nums) == 3:
                try:
                    cells.append(tuple(float(x) for x in nums))
                except ValueError:
                    pass
    if not grid or not cells:
        return {"label": label, "p": p, "missing": True, "cells": [], "seek": seek}
    return {"label": label, "p": p, "missing": False,
            "outBits": int(grid.group(1)), "rect": grid.group(2) + "x" + grid.group(3),
            "ncells": len(cells), "cells": cells,
            "pos": int(seek.group(4)) if seek else None,
            "presented": int(seek.group(5)) if seek else None,
            "seekrc": int(seek.group(1)) if seek else None,
            "pauseRc": int(seek.group(3)) if seek and seek.group(3) else None}


arms = {}
for lab in LABELS:
    for p in (1, 2):
        a = read_arm(lab, p)
        if a is None:
            print("MISSING grid_%s_p%d" % (lab, p))
            continue
        arms[(lab, p)] = a

print("=== 每臂回读")
print("%-6s %-3s %-8s %-16s %-10s %s" % ("label", "p", "outBits", "pos100ns", "presented", "cells"))
for key in sorted(arms, key=lambda k: (k[1], k[0])):
    a = arms[key]
    if a.get("missing"):
        print("%-6s %-3d NO-GRID" % (a["label"], a["p"]))
        continue
    print("%-6s %-3d %-8d %-16s %-10s %d" % (a["label"], a["p"], a["outBits"],
          a["pos"], a["presented"], a["ncells"]))


def delta(x, y):
    if x is None or y is None or x.get("missing") or y.get("missing"):
        return None
    if len(x["cells"]) != len(y["cells"]):
        return None
    diffs = [abs(a - b) for ca, cb in zip(x["cells"], y["cells"]) for a, b in zip(ca, cb)]
    return max(diffs), sum(diffs) / len(diffs)


print("\n=== 前提检查：同一素材各趟是否真是同一张画（钉帧成没成）")
for lab in LABELS:
    per = [arms[(lab, p)] for p in (1, 2, 3) if (lab, p) in arms]
    worst = None
    for i in range(len(per)):
        for j in range(i + 1, len(per)):
            d = delta(per[i], per[j])
            if d is None:
                continue
            worst = d[0] if worst is None else max(worst, d[0])
    if worst is None:
        print("%-6s 不可算（缺臂或格数不等）" % lab)
        continue
    print("%-6s 趟间 max|Δ|=%.5f %s" % (lab, worst,
          "OK" if worst < 0.01 else "*** 前提不成立：这两趟不是同一画面，配对差不可用 ***"))

print("\n=== 配对与分叉")
PAIRS = [("a410", "r410", "yuv410p 直传 vs FFmpeg neighbor 扩 4:4:4"),
         ("a440", "r440", "yuv440p 直传 vs 同上"),
         ("a420", "r420", "对照：早就在表里的 yuv420p（标定本判据的噪声底）"),
         ("a410", "a440", "分叉：两枚新行互比，必须明显大"),
         ("r410", "r420", "分叉：两张 4:4:4 参考件互比（源几何本就不同）")]
for x, y, note in PAIRS:
    ds = [delta(arms.get((x, p)), arms.get((y, p))) for p in (1, 2, 3)]
    got = [d for d in ds if d]
    if not got:
        print("%s vs %-5s 不可算  (%s)" % (x, y, note))
        continue
    print("%s vs %-5s max|Δ|=%.5f (各趟 %s) mean=%.5f  %s" % (x, y, max(d[0] for d in got),
          ", ".join("%.5f" % d[0] for d in got), sum(d[1] for d in got) / len(got), note))
