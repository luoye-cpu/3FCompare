# -*- coding: utf-8 -*-
"""聚合实机（FFF.Player）性能采样 CSV。

窗口过滤规则（重要）：
  第一个窗口含起播（解码器预热、首帧、队列填充），最后一个窗口会被"到时退出"
  截断成不足 1 秒，两者的每帧耗时都不代表稳态。中位数是基准，低于其一半的
  窗口视为不完整直接丢弃。
"""
import csv
import glob
import os
import statistics
import sys

CSVDIR = r"C:\PLAN\3FCompare\_3fc_tmp\player_csv"

COLS = ["sw_ms", "hw_ms", "upload_ms", "mapwait_ms", "copy_ms", "resample_ms",
        "devlock_ms", "presentwait_ms", "pass_per_frame", "total_ms", "fps",
        "presented", "dropped", "coalesced", "queued"]


def med(xs):
    return statistics.median(xs) if xs else 0.0


def load(path):
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({k: float(v) for k, v in r.items()})
            except ValueError:
                continue
    if not rows:
        return rows
    base = med([r["presented"] for r in rows])
    # 丢不完整窗口：中位呈现帧数的一半以下视为被截断
    rows = [r for r in rows if r["presented"] >= base * 0.5]
    if len(rows) > 2:
        rows = rows[1:-1]      # 再弃首尾：起播窗口与收尾窗口
    return rows


def main(pattern="*"):
    files = sorted(glob.glob(os.path.join(CSVDIR, pattern + ".csv")))
    if not files:
        print("无 CSV：", CSVDIR, pattern)
        return
    print(f"{'臂':<22}{'视口':>11}{'fps':>7}{'丢帧/s':>8}{'上传':>9}{'映射':>8}{'拷贝':>9}"
          f"{'重采样':>8}{'呈现等':>8}{'pass/f':>8}{'已计量':>9}{'窗':>4}")
    print("-" * 118)
    agg = {}
    for p in files:
        rows = load(p)
        if not rows:
            print(f"{os.path.basename(p):<22}  (空/无效)")
            continue
        name = os.path.basename(p)[:-4]
        vp = f"{int(med([r['clientW'] for r in rows]))}x{int(med([r['clientH'] for r in rows]))}"
        src = f"{int(med([r['videoW'] for r in rows]))}x{int(med([r['videoH'] for r in rows]))}"
        m = {c: med([r[c] for r in rows]) for c in COLS}
        share = (m["upload_ms"] / m["total_ms"] * 100.0) if m["total_ms"] > 0 else 0.0
        print(f"{name:<22}{vp:>11}{m['fps']:>7.1f}{m['dropped']:>8.1f}{m['upload_ms']:>9.3f}"
              f"{m['mapwait_ms']:>8.3f}{m['copy_ms']:>9.3f}{m['resample_ms']:>8.3f}"
              f"{m['presentwait_ms']:>8.3f}{m['pass_per_frame']:>8.2f}{m['total_ms']:>9.3f}{len(rows):>4}")
        agg[name] = dict(m, vp=vp, src=src, share=share, windows=len(rows))

    print("\n上传内部构成（占 upload 的比例，中位数）")
    print(f"{'臂':<22}{'映射等待':>10}{'拷贝':>10}{'上传外重采样':>14}{'上传占已计量':>14}{'源':>12}{'视口':>11}")
    print("-" * 95)
    for name, m in agg.items():
        up = m["upload_ms"]
        mw = (m["mapwait_ms"] / up * 100.0) if up > 0 else 0.0
        cp = (m["copy_ms"] / up * 100.0) if up > 0 else 0.0
        print(f"{name:<22}{mw:>9.1f}%{cp:>9.1f}%{m['resample_ms']:>13.3f}{m['share']:>13.1f}%"
              f"{m['src']:>12}{m['vp']:>11}")
    return agg


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "*")
