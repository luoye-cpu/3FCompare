"""驱动：最多 N 次 4 路播放，拿到 >=2 份 minidump 就停；检测到蓝屏立即停。

放在 .review_pr/crashdumps/（已被 gitignore），只是本次取证的驱动，不进 tools。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "crashscope"))
import capture as cap  # noqa: E402

DUMPS = HERE
EXE = os.path.join(REPO, "src", "3FCompare", "bin", "Debug", "net11.0-windows",
                   "3FCompare.exe")
MEDIA = os.path.join(REPO, "testmedia", "media", "real", "real_4k_h264_60m.mp4")
TAG = sys.argv[1] if len(sys.argv) > 1 else "dump"
MAXRUNS = int(sys.argv[2]) if len(sys.argv) > 2 else 6
WANT = int(sys.argv[3]) if len(sys.argv) > 3 else 2


def dumps():
    return sorted(f for f in os.listdir(DUMPS) if f.lower().endswith(".dmp"))


def power41():
    ps = ("Get-WinEvent -FilterHashtable @{LogName='System'; Id=41} -MaxEvents 30 "
          "-ErrorAction SilentlyContinue | ForEach-Object { '{0:o}' -f $_.TimeCreated }")
    p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
                       capture_output=True, text=True)
    return [l.strip() for l in (p.stdout or "").splitlines() if l.strip()]


base41 = power41()
print(f"[驱动] 基线 Kernel-Power41 = {len(base41)}")
results = []
for i in range(1, MAXRUNS + 1):
    before = set(dumps())
    rid = f"{TAG}_r{i:02d}"
    print(f"\n{'=' * 60}\n[{rid}] 第 {i}/{MAXRUNS} 次 {time.strftime('%H:%M:%S')}\n{'=' * 60}")
    try:
        res = cap.run_once(EXE, MEDIA, 4, 30, rid,
                           log_dir=os.path.join(HERE, "logs"))
    except Exception as ex:
        print(f"[{rid}] 采集器异常 {type(ex).__name__}: {ex}")
        break
    results.append(res)
    print(f"[{rid}] 退出码={res['exit_code_hex']} ({res['exit_kind']}) "
          f"耗时={res['duration_s']}s 模块={len(res['modules_last'])}")
    for h in res["wer_hits"]:
        print(f"    WER {h['delta_s']:+.1f}s {h['faulting_module']} "
              f"code={h['exception_code']} off={h['exception_offset']}")
    new = sorted(set(dumps()) - before)
    for n in new:
        print(f"    *** 新 dump: {n} ({os.path.getsize(os.path.join(DUMPS, n)):,} 字节)")
    now41 = power41()
    if len(now41) > len(base41):
        print(f"!!! 检测到新 Kernel-Power41（蓝屏）：{now41[len(base41):]}  -> 立即停止")
        break
    if len(dumps()) >= WANT:
        print(f"\n[驱动] 已拿到 {len(dumps())} 份 dump，达标，停止。")
        break

print(f"\n[驱动] 共 {len(results)} 次运行，dump 共 {len(dumps())} 份：{dumps()}")
with open(os.path.join(HERE, f"driver_{TAG}.json"), "w", encoding="utf-8") as f:
    json.dump([{k: v for k, v in r.items() if k != "modules_last"} for r in results],
              f, ensure_ascii=False, indent=2)
