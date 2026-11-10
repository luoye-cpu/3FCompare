import math, sys, csv, glob, os, collections

def hyper(x, r1, r2, k):
    # 组1 大小 r1，组2 大小 r2，总事件数 k；求组1 恰有 x 个事件的概率
    if x < max(0, k - r2) or x > min(r1, k):
        return 0.0
    return math.comb(r1, x) * math.comb(r2, k - x) / math.comb(r1 + r2, k)

def fisher(a, b, c, d):
    """a=组1事件 b=组1非事件 c=组2事件 d=组2非事件，返回 (单尾, 双侧p)"""
    r1, r2, k = a + b, c + d, a + c
    p_obs = hyper(a, r1, r2, k)
    lo, hi = max(0, k - r2), min(r1, k)
    two = sum(hyper(x, r1, r2, k) for x in range(lo, hi + 1)
              if hyper(x, r1, r2, k) <= p_obs * (1 + 1e-9))
    one = sum(hyper(x, r1, r2, k) for x in range(lo, a + 1))
    return p_obs, min(1.0, two), min(1.0, one)

def load(labels):
    rows = []
    for lab in labels:
        f = os.path.join(os.path.dirname(os.path.abspath(__file__)), f"results_{lab}.csv")
        if os.path.exists(f):
            with open(f, encoding="utf-8-sig") as fh:
                rows += [r for r in csv.DictReader(fh) if r.get("case")]
    return rows

if __name__ == "__main__":
    labels = sys.argv[1:] or ["A", "A2", "A3", "A4"]
    rows = load(labels)
    agg = collections.defaultdict(lambda: collections.Counter())
    for r in rows:
        agg[r["case"]][r["verdict"]] += 1
    print("== 汇总（臂 %s）==" % ",".join(labels))
    for c in sorted(agg):
        n = sum(agg[c].values()); cr = agg[c]["CRASH"]
        print("  %-4s 崩 %2d/%2d = %5.1f%%   OK=%d OTHER=%d HANG=%d"
              % (c, cr, n, 100.0 * cr / n, agg[c]["OK"], agg[c]["OTHER"], agg[c]["HANG"]))
    print()
    def cmp(x, y):
        ax = agg[x]["CRASH"]; bx = sum(agg[x].values()) - ax
        ay = agg[y]["CRASH"]; by = sum(agg[y].values()) - ay
        _, p2, p1 = fisher(ax, bx, ay, by)
        print("  %s(%d/%d) vs %s(%d/%d): Fisher 双侧 p=%.4f  单尾 p=%.4f"
              % (x, ax, ax + bx, y, ay, ay + by, p2, p1))
    print("== 两两检验 ==")
    ks = sorted(agg)
    for i in range(len(ks)):
        for j in range(i + 1, len(ks)):
            cmp(ks[i], ks[j])
