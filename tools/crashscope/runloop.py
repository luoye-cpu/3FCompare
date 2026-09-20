"""批量取证驱动：连跑 N 次 4 路播放，每次采集模块快照，并在每次运行后检查是否发生蓝屏。

蓝屏检测：System 日志 Kernel-Power 41（意外断电/复位）。一旦本次运行后新增该事件，
立即停止后续运行并报告——蓝屏不是进程崩溃，继续跑会污染结论。

用法：
    python runloop.py --times 8 --seconds 30 --tag p1
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import capture as cap  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DUMPS = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Temp", "crashscope", "dumps")

_PS = r"""
$e = Get-WinEvent -FilterHashtable @{LogName='System'; Id=41} -MaxEvents 50 -ErrorAction SilentlyContinue
if ($e) { $e | ForEach-Object { "{0:o}" -f $_.TimeCreated } } else { "NONE" }
"""


def kernel_power41_events():
    p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", _PS],
                       capture_output=True, text=True)
    out = [l.strip() for l in (p.stdout or "").splitlines() if l.strip()]
    return [l for l in out if l != "NONE"]


def list_dumps(dump_dir):
    if not os.path.isdir(dump_dir):
        return []
    return [f for f in os.listdir(dump_dir) if f.lower().endswith(".dmp")]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--times", type=int, default=8)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--routes", type=int, default=4)
    ap.add_argument("--tag", default="p1")
    ap.add_argument("--dumps", default=DEFAULT_DUMPS)
    ap.add_argument("--start-index", type=int, default=1)
    a = ap.parse_args()

    base_events = kernel_power41_events()
    print(f"[驱动] 基线 Kernel-Power 41 事件数 = {len(base_events)}")
    if base_events:
        print(f"        最近一次：{base_events[-1]}")
    dumps_before = set(list_dumps(a.dumps))

    results = []
    aborted = False
    for i in range(a.start_index, a.start_index + a.times):
        rid = f"{a.tag}_r{i:02d}"
        print(f"\n{'=' * 70}\n[{rid}] 第 {i} 次运行开始 "
              f"{time.strftime('%H:%M:%S')}\n{'=' * 70}")
        try:
            res = cap.run_once(cap.DEFAULT_EXE, cap.DEFAULT_MEDIA, a.routes,
                               a.seconds, rid,
                               log_dir=os.path.join(HERE, "runs", "logs"))
        except Exception as ex:
            print(f"[{rid}] 采集器异常：{type(ex).__name__}: {ex}")
            break

        out = os.path.join(HERE, "runs", f"{rid}.json")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        results.append(res)

        print(f"[{rid}] 退出码={res['exit_code_hex']} ({res['exit_kind']}) "
              f"耗时={res['duration_s']}s 模块={len(res['modules_last'])} "
              f"快照={len(res['snapshots'])} 采集失败={res['snapshot_failures']}")
        for h in res["wer_hits"]:
            print(f"    WER {h['delta_s']:+.1f}s {h['faulting_module']} "
                  f"code={h['exception_code']} off={h['exception_offset']} "
                  f"data={h['exception_data']}")

        # 蓝屏检查
        now = kernel_power41_events()
        if len(now) > len(base_events):
            new = now[len(base_events):]
            print(f"\n!!! 检测到新的 Kernel-Power 41 事件（蓝屏）：{new}")
            print("!!! 立即停止后续运行。")
            aborted = True
            break

        dumps_now = set(list_dumps(a.dumps)) - dumps_before
        if dumps_now:
            print(f"    新增 dump: {sorted(dumps_now)}")
            dumps_before |= set(list_dumps(a.dumps))

    print(f"\n{'=' * 70}\n[驱动] 完成 {len(results)} 次运行"
          f"{'（因蓝屏中止）' if aborted else ''}\n{'=' * 70}")
    for r in results:
        print(f"  {r['run_id']:<12} {r['exit_code_hex']:<12} {r['exit_kind']:<8} "
              f"{r['duration_s']:>6.1f}s  WER命中={len(r['wer_hits'])}")

    summary = {"aborted_on_bsod": aborted, "runs": len(results),
               "run_ids": [r["run_id"] for r in results]}
    with open(os.path.join(HERE, "runs", f"_driver_{a.tag}.json"), "w",
              encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
