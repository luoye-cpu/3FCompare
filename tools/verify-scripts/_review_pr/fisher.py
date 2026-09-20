"""A/B 崩溃率比较：Fisher 精确检验（双侧）+ 单侧，纯标准库实现（超几何精确求和）。"""
import sys
from math import comb


def fisher_two_sided(a, b, c, d):
    """2x2: [[a,b],[c,d]] —— 返回双侧 p、右尾 p(表 B 更差) 等。"""
    n = a + b + c + d
    r1, c1 = a + b, a + c
    lo = max(0, c1 - (n - r1))
    hi = min(r1, c1)

    def prob(x):
        return comb(r1, x) * comb(n - r1, c1 - x) / comb(n, c1)

    p_obs = prob(a)
    eps = 1e-12
    two = sum(prob(x) for x in range(lo, hi + 1) if prob(x) <= p_obs + eps)
    # 左尾：A 的崩溃数 <= a（即 B 更差）
    left = sum(prob(x) for x in range(lo, a + 1))
    # 右尾：A 的崩溃数 >= a（即 A 更差）
    right = sum(prob(x) for x in range(a, hi + 1))
    return {"two_sided": min(1.0, two), "left_B_worse": min(1.0, left), "right_A_worse": min(1.0, right)}


def odds_ratio(a, b, c, d):
    if b * c == 0:
        return float("inf") if a * d > 0 else 0.0
    return (a * d) / (b * c)


if __name__ == "__main__":
    # 参数：A崩 A未崩 B崩 B未崩
    a, b, c, d = (int(x) for x in sys.argv[1:5])
    print(f"A: {a} 崩 / {a+b} 次 = {a/(a+b):.1%}" if a + b else "A: 无数据")
    print(f"B: {c} 崩 / {c+d} 次 = {c/(c+d):.1%}" if c + d else "B: 无数据")
    print(f"OR = {odds_ratio(a,b,c,d):.3g}")
    r = fisher_two_sided(a, b, c, d)
    print(f"Fisher 双侧 p = {r['two_sided']:.4f}")
    print(f"Fisher 单侧 p(B 崩溃更少) = {r['left_B_worse']:.4f}")
    print(f"Fisher 单侧 p(A 崩溃更多) = {r['right_A_worse']:.4f}")
