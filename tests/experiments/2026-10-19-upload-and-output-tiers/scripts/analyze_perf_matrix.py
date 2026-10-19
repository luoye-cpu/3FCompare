"""把 PerfTest 的逐秒 CSV 折叠成各素材的阶段开销分解。

CSV 列: t_s,decoded_s,presented_s,swap_s,dropped_s,coalesced_s,queued,
        sw_ms,hw_ms,upload_ms,devlock_ms,presentwait_ms,total_ms,fps
每阶段 ms 已是 per-frame 值（内核累计量 / 本秒呈现帧数）。
"""
import csv, os, glob, sys

CSVDIR = r'C:\PLAN\3FCompare\_3fc_tmp\perf_csv'
WARMUP = 3          # 前 3 秒是启动/线程梯爬升，不计入稳态
STAGES = ['sw_ms', 'hw_ms', 'upload_ms', 'devlock_ms', 'presentwait_ms']

LOG = r'C:\PLAN\3FCompare\_3fc_tmp\thread-scheduling-20261017\perf_matrix.log'


def parse_source_fps():
    """按 ARM 段从 ladder trace 的 sourceFps= 取真实源帧率（不猜）。"""
    import re, collections
    out, cur = {}, None
    with open(LOG, encoding='utf-8', errors='replace') as f:
        for line in f:
            m = re.match(r'==== ARM (\S+) ====', line.strip())
            if m:
                cur = m.group(1)
                out.setdefault(cur, collections.Counter())
                continue
            if cur:
                for s in re.findall(r'sourceFps=([0-9.]+)', line):
                    out[cur][float(s)] += 1
    return {k: (v.most_common(1)[0][0] if v else 0.0) for k, v in out.items()}


SOURCE_FPS = parse_source_fps()


def load(path):
    rows = []
    with open(path, newline='', encoding='utf-8') as f:
        for r in csv.DictReader(f):
            try:
                rows.append({k: float(v) for k, v in r.items()})
            except ValueError:
                continue
    return rows


def summarize(label, rows):
    steady = [r for r in rows if r['t_s'] >= WARMUP and r['presented_s'] > 0]
    if not steady:
        return None
    n = len(steady)
    avg = lambda k: sum(r[k] for r in steady) / n
    fps = avg('presented_s')
    stages = {s: avg(s) for s in STAGES}
    measured = sum(stages.values())
    src = SOURCE_FPS.get(label)
    budget = 1000.0 / src if src else 0.0
    dropped = avg('dropped_s')
    return {
        'label': label, 'windows': n, 'fps': fps, 'src': src,
        'budget': budget, 'stages': stages, 'measured': measured,
        'unaccounted': (budget - measured) if budget else 0.0,
        'dropped': dropped, 'coalesced': avg('coalesced_s'),
        'queued': avg('queued'), 'decoded': avg('decoded_s'),
    }


def main():
    results = []
    for p in sorted(glob.glob(os.path.join(CSVDIR, '*.csv'))):
        label = os.path.splitext(os.path.basename(p))[0]
        s = summarize(label, load(p))
        if s:
            results.append(s)
    if not results:
        print('no data'); return

    print('=' * 96)
    print('%-16s %6s %6s %8s %8s %8s %8s %8s %8s %7s' % (
        '素材', 'fps', '源fps', '软转换', '硬传输', '上传', '设备锁', '呈现等待', '已计量', '未计量'))
    print('-' * 96)
    for r in results:
        s = r['stages']
        print('%-16s %6.1f %6.1f %8.2f %8.2f %8.2f %8.2f %8.2f %8.2f %7.2f' % (
            r['label'], r['fps'], r['src'], s['sw_ms'], s['hw_ms'], s['upload_ms'],
            s['devlock_ms'], s['presentwait_ms'], r['measured'], r['unaccounted']))
    print('-' * 96)
    print('（单位 ms/帧；未计量 = 源帧预算 - 已计量，负数=已超预算，主要是解码与调度等待）')

    print('\n=== 已计量部分的占比（%）===')
    print('%-16s %8s %8s %8s %8s %8s' % ('素材', '软转换', '硬传输', '上传', '设备锁', '呈现等待'))
    for r in results:
        m = r['measured'] or 1.0
        s = r['stages']
        print('%-16s %8.1f %8.1f %8.1f %8.1f %8.1f' % (
            r['label'], 100 * s['sw_ms'] / m, 100 * s['hw_ms'] / m,
            100 * s['upload_ms'] / m, 100 * s['devlock_ms'] / m,
            100 * s['presentwait_ms'] / m))

    print('\n=== 吞吐与丢帧 ===')
    print('%-16s %8s %8s %8s %8s %8s %8s' % (
        '素材', '解码/s', '呈现/s', '丢帧/s', '合并/s', '队列', '达成率%'))
    for r in results:
        ach = 100 * r['fps'] / r['src'] if r['src'] else 0
        print('%-16s %8.1f %8.1f %8.1f %8.1f %8.1f %8.1f' % (
            r['label'], r['decoded'], r['fps'], r['dropped'], r['coalesced'],
            r['queued'], ach))

    print('\n=== 全矩阵阶段累加（ms/帧，各素材稳态均值再平均）===')
    tot = {s: sum(r['stages'][s] for r in results) / len(results) for s in STAGES}
    grand = sum(tot.values())
    for s in STAGES:
        print('  %-14s %7.3f ms  (%5.1f%%)' % (s, tot[s], 100 * tot[s] / grand))
    print('  %-14s %7.3f ms' % ('合计', grand))


if __name__ == '__main__':
    main()
