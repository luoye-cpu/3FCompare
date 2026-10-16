#!/bin/bash
# 60fps 呈现节律臂，三次隔开通跑（计时判据不能被别的批次夹着跑）。
# 每一趟都开 FPT_HOLD_TRACE=1：带外样本要逐条归因，聚合百分比分不开"上限没在岗"和
# "管线跟不上"这两件事。
set -u
ROOT=/c/PLAN/3FCompare/3fp
cd "$ROOT/build" || exit 1
mkdir -p "$ROOT/patches/vfr-work-20261016/logs"
L="$ROOT/patches/vfr-work-20261016/logs"
MEDIA='C:\PLAN\3FCompare\testmedia\media\real\real_720p_h264_derived.mp4'
for i in 1 2 3; do
  echo "=== run $i 开始 $(date +%H:%M:%S) ==="
  FPT_HOLD_TRACE=1 PCP_CONTENT_FPS=60 PCP_POLL_MS=1 \
    ./present_pacing_probe.exe "$MEDIA" 8 > "$L/keep$i.out" 2> "$L/keep$i.err"
  echo "RC=$?"
  grep -o "呈现帧=[0-9]*（[0-9.]* fps）[^|]*" "$L/keep$i.out" | head -1
  grep -o "屏幕停留对账：[^|]*" "$L/keep$i.out" | head -1
  grep -o "计时判据前置：.*" "$L/keep$i.out" | head -1
  grep -o "上限读数：.*" "$L/keep$i.out" | head -1
  grep -o "实测呈现速率（.*" "$L/keep$i.out" | head -1
  grep -o "PACING RESULT.*" "$L/keep$i.out" | head -1
  [ "$i" != "3" ] && sleep 25
done
