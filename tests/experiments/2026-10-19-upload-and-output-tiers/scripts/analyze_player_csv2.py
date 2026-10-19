# -*- coding: utf-8 -*-
"""实机上传带宽聚合 v2 —— 带宽的两个操作数都改成量出来的。

v1 (analyze_player_csv.py) 只输出每呈现帧的毫秒数。交接文档 §2.4 的"有效拷贝带宽
恒为 ~1 GB/s"那张表，其 MB 列在任何一份 CSV 里都不存在，而且数值与 copy_ms 逐臂
相等（720p 0.10/0.13、4K 1.84/1.86、8K 19.77/19.82），所以那张表是拿 copy_ms 自己
当分子分母算出来的恒等式，不是测量。

v2 用新增的两列把这件事变成测量：
  uploads            本窗口真正上传了几张画面
  bytes_per_upload   每次上传搬了多少字节（由内核逐行统计，不是"理论帧大小"）
并把分母从"呈现帧"换成"上传次"：被丢掉的那此帧如果也上传过，用呈现帧做分母会把
每帧耗时压低 (presented/uploads) 倍。

旧 CSV（没有这两列）一律记"未判"，不补 0。
"""
import csv
import glob
import os
import statistics
import sys

CSVDIR = r"C:\PLAN\3FCompare\_3fc_tmp\player_csv"

TIMING = ["sw_ms", "hw_ms", "upload_ms", "mapwait_ms", "copy_ms", "resample_ms",
          "devlock_ms", "presentwait_ms", "pass_per_frame", "total_ms", "fps"]
NEW = ["uploads", "bytes_per_upload", "upath"]


def med(xs):
    return statistics.median(xs) if xs else 0.0


def load(path):
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fields = reader.fieldnames or []
        for r in reader:
            try:
                rows.append({k: float(v) for k, v in r.items()})
            except (ValueError, TypeError):
                continue
    if not rows:
        return rows, set()
    base = med([r["presented"] for r in rows])
    rows = [r for r in rows if r["presented"] >= base * 0.5]
    if len(rows) > 2:
        rows = rows[1:-1]
    return rows, set(fields)


def main(pattern="*"):
    files = sorted(glob.glob(os.path.join(CSVDIR, pattern + ".csv")))
    if not files:
        print("无 CSV：", CSVDIR, pattern)
        return
    print(f"{'臂':<20}{'视口':>11}{'src':>11}{'fps':>6}{'pres/s':>7}{'up/s':>6}"
          f"{'up/pres':>8}{'B/upload':>11}{'copy/up ms':>11}{'GB/s':>7}{'map ms':>8}{'path':>5}")
    print("-" * 128)
    for p in files:
        rows, fields = load(p)
        if not rows:
            print(f"{os.path.basename(p)[:-4]:<20}  (空/无效)")
            continue
        name = os.path.basename(p)[:-4]
        have = all(c in fields for c in NEW)
        vp = f"{int(med([r['clientW'] for r in rows]))}x{int(med([r['clientH'] for r in rows]))}"
        src = f"{int(med([r['videoW'] for r in rows]))}x{int(med([r['videoH'] for r in rows]))}"
        pres = med([r["presented"] for r in rows])
        copy_pres = med([r["copy_ms"] for r in rows])
        mapms = med([r["mapwait_ms"] for r in rows])
        if not have:
            # 旧口径：没有字节数、没有上传次数，带宽无法计算，只能报每呈现帧耗时。
            print(f"{name:<20}{vp:>11}{src:>11}{med([r['fps'] for r in rows]):>6.1f}"
                  f"{pres:>7.1f}{'--':>6}{'--':>8}{'未判':>11}{copy_pres:>11.3f}"
                  f"{'未判':>7}{mapms:>8.3f}{'未判':>5}")
            continue
        ups = med([r["uploads"] for r in rows])
        bu = med([r["bytes_per_upload"] for r in rows])
        upath = med([r["upath"] for r in rows])
        # copy_ms 是"每呈现帧"的毫秒数；换成"每上传次"要乘 pres/up。
        copy_up = copy_pres * pres / ups if ups > 0 else 0.0
        gb = (bu / 1e9) / (copy_up / 1000.0) if copy_up > 0 else 0.0
        ratio = ups / pres if pres > 0 else 0.0
        print(f"{name:<20}{vp:>11}{src:>11}{med([r['fps'] for r in rows]):>6.1f}"
              f"{pres:>7.1f}{ups:>6.1f}{ratio:>8.2f}{bu:>11,.0f}{copy_up:>11.3f}"
              f"{gb:>7.2f}{mapms:>8.3f}{upath:>5.0f}")

    print("\n列名说明：up/pres = 每呈现一帧要上传几张（>1 说明丢帧前已经拷过，"
          "用呈现帧做分母会低估每帧拷贝耗时）；B/upload 由内核逐行统计；"
          "GB/s = B/upload ÷ copy/up。path 1=平面STAGING 2=平面DYNAMIC 3/4/5=UpdateSubresource（不记字节）。")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "*")
