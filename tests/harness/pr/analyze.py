#!/usr/bin/env python3
# 判别实验统计：z=1（A，对照）vs z=3（B，实验），2 路 4K。
# 分类：crash（进程异常终止）/ stall（exit=1，渲染停滞断言，非崩溃）/ pass
# 输出：各子集 2x2 表 + Fisher 精确检验双侧 p（精确超几何求和，不依赖 scipy）
import math, os, csv, re

HERE = os.path.dirname(os.path.abspath(__file__))
TSV = os.path.join(HERE, 'results.tsv')
LOGDIR = os.path.join(HERE, 'runlogs')


def fisher_two_sided(a, b, c, d):
    """2x2 表 [[a,b],[c,d]] 的双侧 Fisher 精确检验 p 值。"""
    n = a + b + c + d
    r1, r2 = a + b, c + d
    c1 = a + c
    denom = math.comb(n, r1)

    def p_of(k):
        return math.comb(c1, k) * math.comb(n - c1, r1 - k) / denom

    p_obs = p_of(a)
    total = 0.0
    for k in range(max(0, r1 - (n - c1)), min(r1, c1) + 1):
        pk = p_of(k)
        if pk <= p_obs * (1 + 1e-9):
            total += pk
    return p_obs, total


def odds_ratio(a, b, c, d):
    if b == 0 or c == 0:
        return float('inf') if a * d else 0.0
    return (a * d) / (b * c)


def load():
    rows = []
    with open(TSV, encoding='utf-8') as f:
        for r in csv.DictReader(f, delimiter='\t'):
            r['round'] = int(r['round'])
            r['exit'] = int(r['exit'])
            r['crash'] = int(r['crash'])
            r['wall_s'] = int(r['wall_s'])
            log = os.path.join(LOGDIR, f"{r['arm']}_r{r['round']}.log")
            txt = open(log, encoding='utf-8', errors='replace').read() if os.path.exists(log) else ''
            r['has_magnified_phase'] = 'name=magnified' in txt
            r['has_final_avg'] = '放大后平均' in txt
            r['stall_fail'] = (r['exit'] == 1)
            rows.append(r)
    return rows


def table(rows, pred):
    sub = [r for r in rows if pred(r)]
    a = sum(1 for r in sub if r['arm'] == 'A' and r['crash'])
    b = sum(1 for r in sub if r['arm'] == 'A' and not r['crash'])
    c = sum(1 for r in sub if r['arm'] == 'B' and r['crash'])
    d = sum(1 for r in sub if r['arm'] == 'B' and not r['crash'])
    return a, b, c, d, sub


def report(name, rows, pred):
    a, b, c, d, sub = table(rows, pred)
    p_obs, p = fisher_two_sided(a, b, c, d)
    print(f"\n--- {name}  (n={len(sub)}) ---")
    print(f"  表 [[A崩={a}, A未崩={b}], [B崩={c}, B未崩={d}]]")
    print(f"  A(z=1) 崩溃 {a}/{a+b} = {100*a/(a+b):.1f}%   B(z=3) 崩溃 {c}/{c+d} = {100*c/(c+d):.1f}%")
    print(f"  OR = {odds_ratio(a,b,c,d):.3f}   Fisher 双侧 p = {p:.4f}")
    return (a, b, c, d, p)


def report_pos(rows, name, want_pos):
    """按同轮次序（位置1=同轮第一个进程 / 位置2=第二个）比较，与臂无关。"""
    def pos_of(r):
        if r['round'] <= 8:
            return 1 if r['arm'] == 'A' else 2
        if r['round'] <= 16:
            return 1 if r['arm'] == 'B' else 2
        # ALT：奇数轮 AB，偶数轮 BA
        if r['round'] % 2 == 1:
            return 1 if r['arm'] == 'A' else 2
        return 1 if r['arm'] == 'B' else 2
    sub = [r for r in rows if pos_of(r) == want_pos]
    k = sum(1 for r in sub if r['crash'])
    return k, len(sub), sub


rows = load()
print("=" * 72)
print(f"总运行 {len(rows)} 次；A(z=1) {sum(1 for r in rows if r['arm']=='A')} 次，B(z=3) {sum(1 for r in rows if r['arm']=='B')} 次")
print("=" * 72)

print("\n【分类统计】")
for arm in ('A', 'B'):
    sub = [r for r in rows if r['arm'] == arm]
    cr = sum(1 for r in sub if r['crash'])
    st = sum(1 for r in sub if r['stall_fail'])
    ps = sum(1 for r in sub if r['exit'] == 0)
    print(f"  {arm}(z={'1' if arm=='A' else '3'}): 崩溃 {cr} / 停滞断言(exit=1) {st} / 正常 {ps}  (共 {len(sub)})")

print("\n【主设计：轮 1-8，固定 A→B 顺序（16 次）】")
report("轮1-8 A→B", rows, lambda r: r['round'] <= 8)

print("\n【顺序对调：轮 9-16，固定 B→A（8 次）】")
report("轮9-16 B→A", rows, lambda r: 9 <= r['round'] <= 16)

print("\n【位置平衡对照：轮 17-21，逐轮交替 AB/BA（10 次）】")
report("轮17-21 ALT", rows, lambda r: r['round'] >= 17)

print("\n【全部 34 次（次序/时刻与臂部分混淆，仅供参考）】")
report("全部", rows, lambda r: True)

print("\n" + "=" * 72)
print("【次序（位置）效应：与臂无关】")
k1, n1, s1 = report_pos(rows, "pos1", 1)
k2, n2, s2 = report_pos(rows, "pos2", 2)
print(f"  同轮第 1 个进程：崩溃 {k1}/{n1} = {100*k1/n1:.1f}%")
print(f"  同轮第 2 个进程：崩溃 {k2}/{n2} = {100*k2/n2:.1f}%")
p_obs, p = fisher_two_sided(k1, n1 - k1, k2, n2 - k2)
print(f"  OR = {odds_ratio(k1, n1-k1, k2, n2-k2):.3f}   Fisher 双侧 p = {p:.4f}")

print("\n" + "=" * 72)
print("【崩溃阶段归属（B 臂 z=3，14 次崩溃）】")
bc = [r for r in rows if r['arm'] == 'B' and r['crash']]
pre = [r for r in bc if not r['has_magnified_phase']]
post = [r for r in bc if r['has_magnified_phase']]
print(f"  B 崩溃 {len(bc)} 次：放大前（未打印 magnified 阶段）= {len(pre)} 次；放大阶段内/之后 = {len(post)} 次")
for r in bc:
    tag = '放大阶段内' if r['has_magnified_phase'] else '放大前'
    print(f"    r{r['round']:>2} wall={r['wall_s']:>3}s  exit={r['exit']}  [{tag}]  {r['phase_last'][:44]}")
ac = [r for r in rows if r['arm'] == 'A' and r['crash']]
print(f"  A 臂 z=1（无放大阶段）崩溃 {len(ac)} 次，全部位于基线阶段：")
for r in ac:
    print(f"    r{r['round']:>2} wall={r['wall_s']:>3}s  exit={r['exit']}  {r['phase_last'][:44]}")

print("\n【z=3 放大阶段的功能性异常（停滞，非崩溃）】")
for r in rows:
    if r['arm'] == 'B' and r['stall_fail']:
        print(f"    r{r['round']:>2} exit=1 有magnified阶段={r['has_magnified_phase']} 有最终均值={r['has_final_avg']}")
