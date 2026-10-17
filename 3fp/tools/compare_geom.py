#!/usr/bin/env py -3
"""lane M geometry comparator: parse rd2_geom_probe [row]/[col] readouts from two runs
and report per-point channel deltas, so the "top-left kept / bottom-right lost" crop
shape is visible instead of just an average.

usage: py compare_geom.py <control.txt> <engaged.txt> [label]
"""
import io
import sys


def parse(path):
    rows, cols = [], []
    for line in io.open(path, encoding='utf-8', errors='replace'):
        parts = line.split()
        # "[row] x y r g b a outBits" -- 8 fields; a failed read prints "FAILED" in slot 6.
        if len(parts) != 8 or parts[0] not in ('[row]', '[col]') or parts[6] == 'FAILED':
            continue
        try:
            point = (int(parts[1]), int(parts[2]),
                     tuple(float(v) for v in parts[3:6]))
        except ValueError:
            continue
        (rows if parts[0] == '[row]' else cols).append(point)
    return rows, cols


def max_delta(a, b):
    return max(abs(x - y) for x, y in zip(a, b))


def main():
    control_rows, control_cols = parse(sys.argv[1])
    other_rows, other_cols = parse(sys.argv[2])
    label = sys.argv[3] if len(sys.argv) > 3 else 'B'
    print('== %s: %s  vs  %s ==' % (label, sys.argv[1].split('\\')[-1],
                                    sys.argv[2].split('\\')[-1]))
    for tag, a, b in (('row', control_rows, other_rows), ('col', control_cols, other_cols)):
        if len(a) != len(b) or not a:
            print('  [%s] length mismatch / empty: %d vs %d' % (tag, len(a), len(b)))
            continue
        deltas = []
        for (x, y, ca), (xb, yb, cb) in zip(a, b):
            if (x, y) != (xb, yb):
                print('  [%s] coordinate drift' % tag)
                break
            deltas.append((x, y, ca, cb, max_delta(ca, cb)))
        changed = [d for d in deltas if d[4] > 1e-4]
        mean = sum(d[4] for d in deltas) / len(deltas)
        print('  [%s] points=%d changed=%d (%.4f) mean=%.4f max=%.5f'
              % (tag, len(deltas), len(changed), len(changed) / len(deltas), mean,
                 max(d[4] for d in deltas)))
        # First divergence: the crop keeps the top-left, so the run of identical points
        # sits at the start of the scan and the divergence is a single edge.
        first = next((i for i, d in enumerate(deltas) if d[4] > 1e-4), None)
        if first is not None:
            x, y, ca, cb, dd = deltas[first]
            print('       first divergent point: (%d,%d) control=%.5f/%.5f/%.5f %s=%.5f/%.5f/%.5f'
                  % (x, y, ca[0], ca[1], ca[2], label, cb[0], cb[1], cb[2]))
            tail = deltas[first + 1:]
            same_tail = [d for d in tail if d[4] <= 1e-4]
            print('       identical points AFTER the edge: %d (crop = one edge, no interleaving)'
                  % len(same_tail))
        flat = {}
        for d in deltas:
            flat.setdefault(d[3], []).append(d[0])
        if len(flat) == 1 and label.startswith('eng'):
            print('       %s scan collapsed to a single colour: %s' % (tag, list(flat)[0]))


main()
