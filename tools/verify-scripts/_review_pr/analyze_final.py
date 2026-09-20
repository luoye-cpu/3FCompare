#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""合并两阶段数据做最终统计（自实现 Fisher，无 scipy）。"""
import csv, os
from collections import defaultdict
from math import comb

BASE = r"C:\PLAN\3FCompare\.review_pr\gpu_spread"

def fisher(a, b, c, d):
    n = a + b + c + d
    r1, r2 = a + b, c + d
    c1 = a + c
    if n == 0 or c1 == 0: return 1.0
    def p(x): return comb(r1, x) * comb(r2, c1 - x) / comb(n, c1)
    lo, hi = max(0, c1 - r2), min(r1, c1)
    po = p(a)
    return sum(p(x) for x in range(lo, hi + 1) if p(x) <= po + 1e-12)

def load(path, phase):
    out = []
    if not os.path.exists(path): return out
    for r in csv.DictReader(open(path, encoding="utf-8", errors="replace")):
        if r.get("tag"): r["phase"] = phase; out.append(r)
    return out

rows = load(os.path.join(BASE, "spread_phase1.csv"), "P1") + \
       load(os.path.join(BASE, "spread2.csv"), "P2")

by = defaultdict(list)
for r in rows: by[r["tag"]].append(r)

LABEL = {"off": "关闭(默认,-1)", "even": "EVEN", "auto": "AUTO", "manual": "0,2"}
ORDER = ["off", "even", "auto", "manual"]

print("=" * 92)
print("【合并汇总】4 路 4K60 真实播放 30s，同批次交错，每轮 4 配置交替")
print("=" * 92)
print("%-14s %5s %7s %9s %9s %8s %8s %9s %s" % (
    "配置", "n", "崩溃", "崩溃率", "早期失败", "挂死", "干净完成", "干净率", "实际使用的卡"))
S = {}
for t in ORDER:
    rs = by.get(t, [])
    if not rs: continue
    n = len(rs)
    crash = sum(1 for r in rs if r["crashed"] == "1")
    hang = sum(1 for r in rs if r["exit"] == "124")
    early = sum(1 for r in rs if r["crashed"] == "0" and r["exit"] not in ("0", "124"))
    clean = sum(1 for r in rs if r["exit"] == "0")
    used = sorted({p for r in rs for p in r["actual(vendor:device)"].strip("/").split("/") if p})
    names = {"4318:11266": "RTX5080", "32902:42891": "Intel UHD", "4318:10464": "RTX4060"}
    S[t] = (crash, n, clean)
    print("%-14s %5d %7d %8.0f%% %9d %8d %8d %8.0f%% %s" % (
        LABEL[t], n, crash, 100.0*crash/n, early, hang, clean, 100.0*clean/n,
        ",".join(names.get(u, u) for u in used)))

print()
print("【两两 Fisher 检验】")
print("  (A) 硬崩溃：崩溃 vs 未崩溃")
for i in range(len(ORDER)):
    for j in range(i+1, len(ORDER)):
        t1, t2 = ORDER[i], ORDER[j]
        if t1 not in S or t2 not in S: continue
        a, n1, _ = S[t1]; b, n2, _ = S[t2]
        p = fisher(a, n1-a, b, n2-b)
        print("    %-14s vs %-14s : %2d/%-2d vs %2d/%-2d   p=%.4f%s" % (
            LABEL[t1], LABEL[t2], a, n1, b, n2, p, "  ←显著" if p < 0.05 else ""))

print()
print("  (B) 可用性：干净完成(exit=0) vs 其余（崩溃+早期失败+挂死）")
for i in range(len(ORDER)):
    for j in range(i+1, len(ORDER)):
        t1, t2 = ORDER[i], ORDER[j]
        if t1 not in S or t2 not in S: continue
        c1, n1, _ = S[t1]; c2, n2, _ = S[t2]
        p = fisher(c1, n1-c1, c2, n2-c2)
        print("    %-14s vs %-14s : %2d/%-2d vs %2d/%-2d   p=%.4f%s" % (
            LABEL[t1], LABEL[t2], c1, n1, c2, n2, p, "  ←显著" if p < 0.05 else ""))

print()
print("【分阶段】验证可复现性（关键：Phase1 的 0,2 优势是否复现）")
for ph in ("P1", "P2"):
    line = []
    for t in ORDER:
        rs = [r for r in by.get(t, []) if r["phase"] == ph]
        if not rs: continue
        cr = sum(1 for r in rs if r["crashed"] == "1")
        line.append("%s=%d/%d" % (LABEL[t], cr, len(rs)))
    print("  %s: %s" % (ph, "  ".join(line)))

print()
print("【多卡是否真被使用（内核 A11 实测回报，非请求值）】")
for t in ORDER:
    rs = by.get(t, [])
    if not rs: continue
    seqs = defaultdict(int)
    for r in rs:
        seqs[r["actual(vendor:device)"].strip("/")] += 1
    print("  %-14s 观测到的实际卡序列（出现次数）:" % LABEL[t])
    names = {"4318:11266": "RTX5080", "32902:42891": "IntelUHD", "4318:10464": "RTX4060"}
    for k, v in sorted(seqs.items(), key=lambda kv: -kv[1])[:4]:
        print("      %-40s ×%d   [%s]" % (
            k.replace("/", " → "), v, " ".join(names.get(x, x) for x in k.split("/") if x)))
