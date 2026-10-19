#!/usr/bin/env python3
# 汇总拖拽 UI 阻塞读数：把 量产品拖拽UI线程阻塞.ps1 落的 per-sample CSV 重算成分臂百分位。
#
# 为什么单独要一个脚本：产品侧那行 ARM 汇总只打在控制台，台账里每引一个数都得能回到盘上文件。
# CSV 每行是 (phase, t_ms, latency_us, timeout)，phase 就是臂名（idle/moves/zoom/drag/ctrl…）。
# 用法：py -3 3fp/tools/汇总拖拽UI阻塞读数.py _3fc_tmp/drag_quant/ui_block_*.csv
import sys, csv, os

def pct(sorted_vals, p):
    if not sorted_vals:
        return float('nan')
    i = round(p * (len(sorted_vals) - 1))
    return sorted_vals[max(0, min(i, len(sorted_vals) - 1))]

def one(path):
    buckets = {}
    with open(path, newline='', encoding='utf-8') as fh:
        for row in csv.DictReader(fh):
            # 旧 CSV（10-19 14:2x~15:0x 那几轮）没有 kind 列，一律当旁路 WM_NULL 读数。
            kind = (row.get('kind') or 'ui').strip()
            if kind == '':
                kind = 'ui'
            ph = kind + ':' + row['phase']
            b = buckets.setdefault(ph, {'v': [], 'to': 0})
            if row['timeout'] == '1':
                b['to'] += 1
            else:
                b['v'].append(float(row['latency_us']))
    lines = ['== ' + path]
    for ph in buckets:          # 保持相位出现顺序（dict 保序）
        b = buckets[ph]
        v = sorted(b['v'])
        if not v:
            lines.append('  %-8s n=%5d 全部超时=%d（无有效样本）' % (ph, len(v), b['to']))
            continue
        lines.append('  %-8s n=%5d p50=%7.3fms p95=%7.3fms p99=%7.3fms max=%8.3fms '
                     '>8ms=%-3d >16ms=%-3d 超时=%d' % (
                         ph, len(v), pct(v, .5) / 1000, pct(v, .95) / 1000, pct(v, .99) / 1000,
                         v[-1] / 1000, sum(1 for x in v if x > 8000),
                         sum(1 for x in v if x > 16000), b['to']))
    return '\n'.join(lines)

if __name__ == '__main__':
    args = sys.argv[1:]
    if not args:
        print('用法：py -3 汇总拖拽UI阻塞读数.py <ui_block_*.csv> ...', file=sys.stderr)
        raise SystemExit(2)
    out = '\n'.join(one(p) for p in args)
    print(out)
    target = os.path.join(os.path.dirname(args[-1]), 'arm_summary.txt')
    with open(target, 'a', encoding='utf-8') as fh:
        fh.write(out + '\n')
    print('--> 已追加到 ' + target, file=sys.stderr)
