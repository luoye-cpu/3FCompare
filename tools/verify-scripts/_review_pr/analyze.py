#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Same-batch A/B stats: crash rates + Fisher exact + order-effect checks.

Reads results_ab.csv produced by run_ab.ps1.

Verdict convention (fixed by the project's earlier mix-up):
    exit<0  -> CRASH   (a real crash)
    exit=1  -> ASSERT  (drift-assertion failure -- NOT a crash, counted separately)
    exit=0  -> OK
"""
import collections
import csv
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def hyper(x, r1, r2, k):
    if x < max(0, k - r2) or x > min(r1, k):
        return 0.0
    return math.comb(r1, x) * math.comb(r2, k - x) / math.comb(r1 + r2, k)


def fisher(a, b, c, d):
    """a/b = group1 event/non-event, c/d = group2. -> (two-sided p, one-sided p)."""
    r1, r2, k = a + b, c + d, a + c
    if r1 == 0 or r2 == 0 or k == 0 or k == r1 + r2:
        return 1.0, 1.0
    p_obs = hyper(a, r1, r2, k)
    lo, hi = max(0, k - r2), min(r1, k)
    two = sum(hyper(x, r1, r2, k) for x in range(lo, hi + 1)
              if hyper(x, r1, r2, k) <= p_obs * (1 + 1e-9))
    one = sum(hyper(x, r1, r2, k) for x in range(a, hi + 1))
    return min(1.0, two), min(1.0, one)


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def embed_class(raw):
    if not raw:
        return "?"
    if "与内嵌资源不一致" in raw:
        return "不一致"
    if "与内嵌资源一致" in raw:
        return "一致"
    return "?"


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "results_ab.csv")
    with open(path, encoding="utf-8-sig") as fh:
        rows = [r for r in csv.DictReader(fh) if r.get("arm")]
    if not rows:
        print("no data")
        return

    agg = collections.defaultdict(collections.Counter)
    for r in rows:
        agg[r["arm"]][r["verdict"]] += 1

    print("== per-run ==")
    print("  %-4s %-4s %-4s %-8s %-12s %-7s %-7s %-9s %-6s %s"
          % ("arm", "rnd", "pos", "verdict", "exit", "wall", "dllOk", "embed", "rtss", "threads e/m"))
    for r in rows:
        print("  %-4s %-4s %-4s %-8s %-12s %-7s %-7s %-9s %-6s %s/%s"
              % (r["arm"], r["round"], r["pos"], r["verdict"],
                 (r.get("exitCode") or "") + (" " + r["codeHex"] if r.get("codeHex") else ""),
                 r["wallSec"], r["dllOk"], embed_class(r.get("embedRaw", "")),
                 r["rtssUp"], r.get("thrEarly"), r.get("thrMid")))

    print("\n== crash rate (CRASH = exit<0; ASSERT is NOT a crash) ==")
    for arm in sorted(agg):
        c = agg[arm]
        n = sum(c.values())
        lo, hi = wilson(c["CRASH"], n)
        print("  %s arm: CRASH %d/%d = %5.1f%%  (95%%CI %.1f-%.1f)  OK=%d ASSERT=%d OTHER=%d HANG=%d"
              % (arm, c["CRASH"], n, 100.0 * c["CRASH"] / n, 100 * lo, 100 * hi,
                 c["OK"], c["ASSERT"], c["OTHER"], c["HANG"]))

    print("\n== Fisher exact test (A = pre-fix, B = fixed) ==")
    if "A" in agg and "B" in agg:
        a = agg["A"]["CRASH"]; b = sum(agg["A"].values()) - a
        c = agg["B"]["CRASH"]; d = sum(agg["B"].values()) - c
        p2, p1 = fisher(a, b, c, d)
        print("  primary  (CRASH vs rest):        A %d/%d vs B %d/%d  two-sided p=%.4f  one-sided p=%.4f"
              % (a, a + b, c, c + d, p2, p1))
        # conservative: only OK counts as success (ASSERT/OTHER/HANG all count as failure)
        a2, b2 = agg["A"]["CRASH"] + agg["A"]["ASSERT"] + agg["A"]["OTHER"] + agg["A"]["HANG"], agg["A"]["OK"]
        c2, d2 = agg["B"]["CRASH"] + agg["B"]["ASSERT"] + agg["B"]["OTHER"] + agg["B"]["HANG"], agg["B"]["OK"]
        p2b, p1b = fisher(a2, b2, c2, d2)
        print("  strict   (only OK passes):       A %d/%d vs B %d/%d  two-sided p=%.4f  one-sided p=%.4f"
              % (a2, a2 + b2, c2, c2 + d2, p2b, p1b))
        # risk difference
        rd = a / (a + b) - c / (c + d)
        print("  risk difference (A - B) = %+.1f pp  (ratio %.2fx)"
              % (100 * rd, (a / (a + b)) / (c / (c + d)) if c else float("inf")))

    print("\n== order effect (first-run vs second-run) ==")
    for pos in ("1", "2"):
        sel = [r for r in rows if r["pos"] == pos]
        cr = sum(1 for r in sel if r["verdict"] == "CRASH")
        print("  overall pos%s: CRASH %d/%d = %5.1f%%" % (pos, cr, len(sel), 100.0 * cr / max(1, len(sel))))
    for arm in sorted(agg):
        for pos in ("1", "2"):
            sel = [r for r in rows if r["arm"] == arm and r["pos"] == pos]
            if sel:
                cr = sum(1 for r in sel if r["verdict"] == "CRASH")
                print("  %s arm pos%s: CRASH %d/%d" % (arm, pos, cr, len(sel)))
    p1 = [r for r in rows if r["pos"] == "1"]
    p2 = [r for r in rows if r["pos"] == "2"]
    a = sum(1 for r in p1 if r["verdict"] == "CRASH"); b = len(p1) - a
    c = sum(1 for r in p2 if r["verdict"] == "CRASH"); d = len(p2) - c
    p2s, p1s = fisher(a, b, c, d)
    print("  Fisher pos1 vs pos2: %d/%d vs %d/%d  two-sided p=%.4f  (p>0.05 => no order effect)"
          % (a, a + b, c, c + d, p2s))

    print("\n== round stratification (each round must hold exactly 1xA + 1xB) ==")
    rounds = sorted({int(r["round"]) for r in rows})
    for rd in rounds:
        sel = [r for r in rows if int(r["round"]) == rd]
        arms = collections.Counter(r["arm"] for r in sel)
        order = " -> ".join(r["arm"] for r in sorted(sel, key=lambda x: x["pos"]))
        bad = "" if (arms["A"] == 1 and arms["B"] == 1 and len(sel) == 2) else "  <-- MALFORMED"
        print("  round %-2d order %-8s A=%d B=%d%s" % (rd, order, arms["A"], arms["B"], bad))

    print("\n== manipulation validity ==")
    bad_dll = [r for r in rows if str(r["dllOk"]).lower() != "true"]
    print("  DLL swap verification failures: %d/%d" % (len(bad_dll), len(rows)))
    for arm in sorted(agg):
        want = "一致" if arm == "B" else "不一致"
        sel = [r for r in rows if r["arm"] == arm]
        good = sum(1 for r in sel if embed_class(r.get("embedRaw", "")) == want)
        print("  %s arm embed-log cross-check: %d/%d read '%s' (expected: embedded resource == B arm)"
              % (arm, good, len(sel), want))
    bad_rtss = [r for r in rows if str(r["rtssUp"]).lower() != "true"]
    print("  RTSS/Afterburner alive at every run: %d/%d" % (len(rows) - len(bad_rtss), len(rows)))
    for arm in sorted(agg):
        thr = [int(r["thrMid"]) for r in rows
               if r["arm"] == arm and r.get("thrMid") not in ("", "-1", None)]
        if thr:
            print("  %s arm steady-state thread count: %s (mean %.1f, min %d)"
                  % (arm, thr, sum(thr) / len(thr), min(thr)))
    inj = sum(1 for r in rows if r.get("injected"))
    print("  RTSS/nvspcap injected into process: %d/%d" % (inj, len(rows)))

    print("\n== wall time ==")
    for arm in sorted(agg):
        w = [float(r["wallSec"]) for r in rows if r["arm"] == arm and r.get("wallSec")]
        if w:
            print("  %s arm: mean %.1fs  min %.1fs  max %.1fs" % (arm, sum(w) / len(w), min(w), max(w)))


if __name__ == "__main__":
    main()
