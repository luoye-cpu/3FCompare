#!/bin/bash
# 撕裂／VRR 对照臂两次隔开通跑：这一臂的 holdSampled 从 0 变成了非零，
# "锁关掉就没有操作数"那条旧结论要么作废要么改写，单趟不能当判词。
set -u
ROOT=/c/PLAN/3FCompare/3fp
cd "$ROOT/build" || exit 1
L="$ROOT/patches/vfr-work-20261016/logs"
export TMP="$ROOT/build/_wintmp" TEMP="$TMP" TMPDIR="$TMP"
mkdir -p "$TMP"
MEDIA='C:\PLAN\3FCompare\testmedia\media\real\real_720p_h264_derived.mp4'
for i in 1 2; do
  echo "=== vrr run $i $(date +%H:%M:%S) ==="
  FPT_HOLD_TRACE=1 PCP_CONTENT_FPS=60 PCP_TEARING=1 PCP_POLL_MS=1 \
    ./present_pacing_probe.exe "$MEDIA" 8 > "$L/vrr$i.out" 2> "$L/vrr$i.err"
  echo "RC=$?"
  grep -o "末次 gridLocked.*\|呈现帧=.*翻帧=.*\|屏幕停留对账：.*\|PACING RESULT.*" "$L/vrr$i.out"
  [ "$i" != "2" ] && sleep 25
done
echo "=== locked run 4（同件同窗，作对照的另一侧）==="
FPT_HOLD_TRACE=1 PCP_CONTENT_FPS=60 PCP_POLL_MS=1 \
  ./present_pacing_probe.exe "$MEDIA" 8 > "$L/keep4.out" 2> "$L/keep4.err"
echo "RC=$?"
grep -o "呈现帧=.*翻帧=.*\|屏幕停留对账：.*\|计时判据前置.*\|PACING RESULT.*" "$L/keep4.out"
exit 0
