#!/bin/bash
# 撕裂／VRR 对照臂两次隔开通跑。
# 改版后这一臂要同时交两条账：上限让位（gridLocked 恒 0、bypass=1）**和**内容那一半的
# 上限仍然生效（实测呈现速率不得高于 min(声明刷新率, 内容率)）。单趟不能当判词。
# 屏幕停留那一组读数在本臂不参与判据（上限在岗标志为 0 时它没有操作数），但照样打印，
# 因为"锁关掉就没有操作数"这条旧结论要靠它来作废或改写。
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
  grep -o "末次 gridLocked.*\|上限读数：.*\|实测呈现速率（.*\|呈现帧=.*翻帧=.*\|屏幕停留对账：.*\|PACING RESULT.*" "$L/vrr$i.out"
  [ "$i" != "2" ] && sleep 25
done
echo "=== locked run 4（同件同窗，作对照的另一侧）==="
FPT_HOLD_TRACE=1 PCP_CONTENT_FPS=60 PCP_POLL_MS=1 \
  ./present_pacing_probe.exe "$MEDIA" 8 > "$L/keep4.out" 2> "$L/keep4.err"
echo "RC=$?"
grep -o "呈现帧=.*翻帧=.*\|屏幕停留对账：.*\|计时判据前置.*\|PACING RESULT.*" "$L/keep4.out"
exit 0
