#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
在 3FCompare 播放期间按秒采样各 GPU 的利用率/显存，用于实证"多路分散到多张卡"
是否真的降低了单卡负载（起因：历史性能占用里解码器多次报告过载）。

用法：
    python tools/measure_gpu_load.py --label 集中 \
        -- "C:/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe" \
           --multitest "C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4" 4 25

    FC_SPREAD_GPUS=AUTO  python tools/measure_gpu_load.py --label 分散 -- ...

可加 --env FC_SPREAD_GPUS=AUTO 由本脚本注入环境变量（避免 bash 变量名以数字开头的问题）。
输出：每张卡的平均/峰值利用率与显存，以及"所有卡之和"（近似总负载）。
"""
import argparse
import os
import subprocess
import sys
import time
from collections import defaultdict

SMI = r"C:\Windows\System32\nvidia-smi.exe"


def sample_once():
    try:
        out = subprocess.run(
            [SMI, "--query-gpu=name,utilization.gpu,memory.used",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return []
    rows = []
    for line in out.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < 3:
            continue
        try:
            rows.append((parts[0], int(parts[1]), int(parts[2])))
        except ValueError:
            continue
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", required=True)
    ap.add_argument("--seconds", type=int, default=24)
    ap.add_argument("--env", action="append", default=[],
                    help="注入环境变量，形如 NAME=VALUE（可重复）")
    ap.add_argument("--out", default=None)
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    args = ap.parse_args()

    cmd = [a for a in args.cmd if a != "--"]
    if not cmd:
        print("缺少要运行的命令（用 -- 分隔）")
        return 2

    env = dict(os.environ)
    for kv in args.env:
        if "=" in kv:
            k, v = kv.split("=", 1)
            env[k] = v

    print(f"[{args.label}] 启动: {' '.join(cmd[:1])} ...")
    proc = subprocess.Popen(cmd, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    util = defaultdict(list)
    mem = defaultdict(list)
    t0 = time.time()
    while time.time() - t0 < args.seconds:
        for name, u, m in sample_once():
            util[name].append(u)
            mem[name].append(m)
        time.sleep(1.0)
    rc = proc.poll()
    if rc is None:
        proc.terminate()
        try:
            rc = proc.wait(timeout=10)
        except Exception:
            proc.kill()
            rc = -9

    print(f"[{args.label}] 采样 {len(next(iter(util.values()))) if util else 0} 次，"
          f"进程退出码={rc}")
    print("%-40s %8s %8s %10s" % ("GPU", "平均%", "峰值%", "峰值显存MB"))
    total_avg = 0
    for name in sorted(util):
        us = util[name]
        ms = mem[name]
        if not us:
            continue
        avg = sum(us) / len(us)
        total_avg += avg
        print("%-40s %8.1f %8d %10d" % (name, avg, max(us), max(ms)))
    print("%-40s %8.1f" % ("各卡平均之和（近似总负载）", total_avg))

    if args.out:
        with open(args.out, "a", encoding="utf-8") as fh:
            fh.write(f"[{args.label}] exit={rc} total_avg={total_avg:.1f}\n")
            for name in sorted(util):
                if util[name]:
                    fh.write("  %s avg=%.1f peak=%d mem=%d\n" % (
                        name, sum(util[name]) / len(util[name]),
                        max(util[name]), max(mem[name])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
