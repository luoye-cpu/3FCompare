#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Clean-criterion A/B stats for the single-presenter fix.

Reads results_ab_clean.csv produced by run_ab_clean.ps1.

Verdict convention:
    exit<0  -> CRASH   (a real crash)
    exit=1  -> ASSERT  (assertion failure; drift failure is one kind, flagged by sawDrift)
    exit=3  -> OTHER   (watchdog: stuck > 40s)
    exit=0  -> OK
Total failure = anything that is not OK.

The 14:30 batch used a 100ms drift tolerance, tighter than the project's own
measured 138-195ms normal drift, which manufactured spurious failures. The
tolerance is now 250ms, so "any failure" ~= "crash" and the contrast is clean.
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


def truthy(v):
    return str(v).strip().lower() == "true"


def embed_class(raw):
    if not raw:
        return "?"
    if "与内嵌资源不一致" in raw:
        return "不一致"
    if "与内嵌资源一致" in raw:
        return "一致"
    return "?"


def is_fail(v):
    return v != "OK"


def has_drift(r):
    """A drift failure is an assertion line that names drift.

    NOTE: the CSV's sawDrift column is NOT usable -- the success line
    'multitest[..]: 漂移 OK' also contains the drift keyword, so sawDrift
    over-reports. failLine only captures lines containing '失败', and a
    drift failure reads 'multitest[步骤N]: 失败 ✗ 第 i 路漂移 ... > 250ms'.
    """
    return "漂移" in (r.get("failLine") or "")


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "results_ab_clean.csv")
    with open(path, encoding="utf-8-sig") as fh:
        rows = [r for r in csv.DictReader(fh) if r.get("arm")]
    if not rows:
        print("no data")
        return

    agg = collections.defaultdict(collections.Counter)
    for r in rows:
        agg[r["arm"]][r["verdict"]] += 1

    print("== per-run (arm rnd pos verdict exit wall dllOk embed rtss threads) ==")
    for r in rows:
        print("  %-2s r%-2s p%-2s %-7s exit=%-6s %-6ss dllOk=%-5s embed=%-4s rtss=%-5s thr=%s/%s drift=%s"
              % (r["arm"], r["round"], r["pos"], r["verdict"],
                 (r.get("exitCode") or "") + (" " + r["codeHex"] if r.get("codeHex") else ""),
                 r["wallSec"], r["dllOk"], embed_class(r.get("embedRaw", "")),
                 r["rtssUp"], r.get("thrEarly"), r.get("thrMid"), has_drift(r)))

    print("\n== summary per arm (n=%d total runs) ==" % len(rows))
    for arm in sorted(agg):
        c = agg[arm]
        n = sum(c.values())
        crash = c["CRASH"]
        drift = sum(1 for r in rows if r["arm"] == arm and has_drift(r))
        fail = sum(1 for r in rows if r["arm"] == arm and is_fail(r["verdict"]))
        ok = c["OK"]
        lo_c, hi_c = wilson(crash, n)
        lo_f, hi_f = wilson(fail, n)
        print("  %s arm: runs=%d  CRASH=%d (%.1f%%, 95%%CI %.1f-%.1f)  drift-fail=%d  "
              "TOTAL-FAIL=%d (%.1f%%, 95%%CI %.1f-%.1f)  PASS(OK)=%d  "
              "[ASSERT=%d OTHER=%d HANG=%d]"
              % (arm, n, crash, 100.0 * crash / n, 100 * lo_c, 100 * hi_c, drift,
                 fail, 100.0 * fail / n, 100 * lo_f, 100 * hi_f, ok,
                 c["ASSERT"], c["OTHER"], c["HANG"]))

    print("\n== Fisher exact (A = pre-fix, B = fixed) ==")
    if "A" in agg and "B" in agg:
        nA, nB = sum(agg["A"].values()), sum(agg["B"].values())
        a = agg["A"]["CRASH"]; b = nA - a
        c = agg["B"]["CRASH"]; d = nB - c
        p2, p1 = fisher(a, b, c, d)
        print("  PRIMARY  crash rate:   A %d/%d (%.1f%%) vs B %d/%d (%.1f%%)  "
              "two-sided p=%.4f  one-sided p=%.4f"
              % (a, nA, 100.0 * a / nA, c, nB, 100.0 * c / nB, p2, p1))

        a2 = sum(1 for r in rows if r["arm"] == "A" and is_fail(r["verdict"]))
        c2 = sum(1 for r in rows if r["arm"] == "B" and is_fail(r["verdict"]))
        p2b, p1b = fisher(a2, nA - a2, c2, nB - c2)
        print("  TOTAL    any failure:  A %d/%d (%.1f%%) vs B %d/%d (%.1f%%)  "
              "two-sided p=%.4f  one-sided p=%.4f"
              % (a2, nA, 100.0 * a2 / nA, c2, nB, 100.0 * c2 / nB, p2b, p1b))

        rd = (a / nA) - (c / nB)
        print("  risk difference (A - B) crash: %+.1f pp" % (100 * rd))
        rd2 = (a2 / nA) - (c2 / nB)
        print("  risk difference (A - B) fail : %+.1f pp" % (100 * rd2))

    print("\n== order effect ==")
    positions = sorted({r["pos"] for r in rows}, key=lambda x: int(x))
    for pos in positions:
        sel = [r for r in rows if r["pos"] == pos]
        cr = sum(1 for r in sel if r["verdict"] == "CRASH")
        fl = sum(1 for r in sel if is_fail(r["verdict"]))
        print("  pos%s overall: CRASH %d/%d = %5.1f%%   TOTAL-FAIL %d/%d = %5.1f%%"
              % (pos, cr, len(sel), 100.0 * cr / max(1, len(sel)),
                 fl, len(sel), 100.0 * fl / max(1, len(sel))))
    for arm in sorted(agg):
        for pos in positions:
            sel = [r for r in rows if r["arm"] == arm and r["pos"] == pos]
            if sel:
                cr = sum(1 for r in sel if r["verdict"] == "CRASH")
                print("    %s arm pos%s: CRASH %d/%d" % (arm, pos, cr, len(sel)))

    # first run of a round vs later runs (warm-up / ordering drift)
    first = [r for r in rows if r["pos"] == "1"]
    later = [r for r in rows if r["pos"] != "1"]
    a = sum(1 for r in first if r["verdict"] == "CRASH"); b = len(first) - a
    c = sum(1 for r in later if r["verdict"] == "CRASH"); d = len(later) - c
    p2s, _ = fisher(a, b, c, d)
    print("  Fisher pos1 vs pos2..4: CRASH %d/%d vs %d/%d  two-sided p=%.4f  (p>0.05 => no order effect)"
          % (a, a + b, c, c + d, p2s))

    # first half vs second half
    h1 = [r for r in rows if int(r["round"]) <= 4]
    h2 = [r for r in rows if int(r["round"]) > 4]
    a = sum(1 for r in h1 if r["verdict"] == "CRASH"); b = len(h1) - a
    c = sum(1 for r in h2 if r["verdict"] == "CRASH"); d = len(h2) - c
    p2h, _ = fisher(a, b, c, d)
    print("  Fisher rounds1-4 vs rounds5-8: CRASH %d/%d vs %d/%d  two-sided p=%.4f"
          % (a, a + b, c, c + d, p2h))

    print("\n== round stratification (each round must hold exactly 2xA + 2xB) ==")
    rounds = sorted({int(r["round"]) for r in rows})
    for rd in rounds:
        sel = [r for r in rows if int(r["round"]) == rd]
        arms = collections.Counter(r["arm"] for r in sel)
        order = " -> ".join(r["arm"] for r in sorted(sel, key=lambda x: int(x["pos"])))
        bad = "" if (arms["A"] == 2 and arms["B"] == 2 and len(sel) == 4) else "  <-- MALFORMED"
        print("  round %-2d order %-14s A=%d B=%d%s" % (rd, order, arms["A"], arms["B"], bad))

    print("\n== manipulation validity ==")
    bad_dll = [r for r in rows if not truthy(r["dllOk"])]
    print("  DLL swap verification failures: %d/%d" % (len(bad_dll), len(rows)))
    for arm in sorted(agg):
        want = "一致" if arm == "B" else "不一致"
        sel = [r for r in rows if r["arm"] == arm]
        good = sum(1 for r in sel if embed_class(r.get("embedRaw", "")) == want)
        print("  %s arm embed-log cross-check: %d/%d read '%s'" % (arm, good, len(sel), want))
    bad_rtss = [r for r in rows if not truthy(r["rtssUp"])]
    print("  RTSS/Afterburner alive at every run: %d/%d" % (len(rows) - len(bad_rtss), len(rows)))
    inj = sum(1 for r in rows if r.get("injected"))
    print("  RTSS/nvspcap injected into process: %d/%d" % (inj, len(rows)))
    for arm in sorted(agg):
        thr = [int(r["thrMid"]) for r in rows
               if r["arm"] == arm and r.get("thrMid") not in ("", "-1", None)]
        if thr:
            print("  %s arm steady-state thread count: %s (mean %.1f, min %d)"
                  % (arm, thr, sum(thr) / len(thr), min(thr)))

    print("\n== wall time ==")
    for arm in sorted(agg):
        w = [float(r["wallSec"]) for r in rows if r["arm"] == arm and r.get("wallSec")]
        if w:
            print("  %s arm: mean %.1fs  min %.1fs  max %.1fs" % (arm, sum(w) / len(w), min(w), max(w)))

    print("\n== drift failure detail ==")
    dsel = [r for r in rows if has_drift(r)]
    if not dsel:
        print("  none")
    for r in dsel:
        print("  %s r%s p%s: %s" % (r["arm"], r["round"], r["pos"], r.get("driftLine")))

    print("\n== non-OK, non-drift failure detail ==")
    nsel = [r for r in rows if is_fail(r["verdict"]) and not has_drift(r)]
    if not nsel:
        print("  none")
    for r in nsel:
        print("  %s r%s p%s [%s]: %s" % (r["arm"], r["round"], r["pos"], r["verdict"], r.get("failLine")))


if __name__ == "__main__":
    main()
