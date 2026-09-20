#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 ② 汇总表（Markdown）。崩溃时刻取自 Windows 事件日志 .NET Runtime 1026 事件
（毫秒级，与 shell 退出码时钟一致，7/8 吻合到 ±0.5s）。"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import parse, clock2s  # noqa: E402

OUT = os.path.dirname(os.path.abspath(__file__))

# .NET Runtime 事件 1026（c0000005）时刻，来自 Application 事件日志
CRASH = {
    "1": "09:11:01.748", "2": "09:11:11.047", "3": "09:11:29.448",
    "4": "09:11:44.515", "5": "09:11:54.018", "6": "09:12:05.021",
    "7": "09:12:20.437", "8": "09:12:31.067",
}

print("| 运行 | 退出码 | 崩溃时刻(.NET Runtime 1026) | 末条心跳 | 距崩溃 | 崩前最后一条心跳的 4 路 Δpresented (≈fps) | 最后非心跳事件 | 心跳停顿 |")
print("|---|---|---|---|---|---|---|---|")
for n in sorted(CRASH, key=int):
    p = os.path.join(OUT, f"run{n}.log")
    hbs, events = parse(p)
    ct = clock2s(CRASH[n])
    last = hbs[-1]
    gap = ct - last["ts"]
    # 崩前最后一条"真·崩前"心跳：若崩溃时刻早于末条心跳（run8），退回上一条
    pre = last
    if gap < 0 and len(hbs) >= 2:
        pre = hbs[-2]
    prev_ts = None
    idx = hbs.index(pre)
    prev_ts = hbs[idx - 1]["ts"] if idx >= 1 else None
    dt = (pre["ts"] - prev_ts) if prev_ts else None
    cells = []
    for r in pre["routes"]:
        if r["dp"] is None or not dt:
            cells.append(f"L{r['i']}=?")
        else:
            cells.append(f"L{r['i']}={r['dp']}({r['dp']/dt:.0f})")
    dps = " / ".join(cells)
    st = pre["routes"][0]["state"] if pre["routes"] else "?"
    # 停顿
    gs = []
    for a, b in zip(hbs, hbs[1:]):
        d = b["ts"] - a["ts"]
        if d > 1.5:
            gs.append(f"{d:.2f}s")
    gapstr = ",".join(gs) if gs else "—"
    ev = events[-1] if events else None
    evs = f"{ev['t']} {ev['evt']}" if ev else "—"
    gapv = f"{gap:+.2f}s" if gap >= 0 else f"**{gap:+.2f}s**"
    print(f"| {n} | 139 崩 | {CRASH[n]} | {last['t']} | {gapv} | "
          f"{st}: {dps} | {evs} | {gapstr} |")

print()
print("注：括号内为按该心跳与上一心跳的实际间隔折算的速率（正常 55~62fps）。")
print("run2 末条心跳在 Play 之前（state=Ready, pres=0），故无 Δ 可算。")
print("run8 的崩溃时刻早于其末条心跳（见正文），故取 09:12:30.896 那条作崩前样本。")
