#!/bin/bash
# PR-B v2 修复后的回归测试轮次（顺序执行，避免 GPU 争用）
set -u
EXEDIR='C:\PLAN\3FCompare\.review_pr\player_env\fff\FFF.Player.Tests\bin\x64\Release\net10.0-windows10.0.26100.0'
EXE="$EXEDIR\FFF.Player.Tests.exe"
MEDIA='C:\PLAN\3FCompare\testmedia\media\real'
SDR="$MEDIA\\real_4k_h264_60m.mp4"
SDR10="$MEDIA\\real_4k_hevc10_60m.mp4"
HDR="$MEDIA\\real_4k_hevc_hdr10_60m.mp4"
LOG='C:\PLAN\3FCompare\.review_pr\pr_wt\tests_round2'
mkdir -p "$LOG"

run() {
  local name="$1"; shift
  local ts; ts=$(date +%H:%M:%S)
  ( cd "$EXEDIR" && "$EXE" "$@" ) > "$LOG/$name.log" 2>&1
  local rc=$?
  printf '%-28s EXIT=%-4s %s  (log: %s.log)\n' "$name" "$rc" "$ts" "$name"
  return $rc
}

FAIL=0
run startup-regression      --startup-regression      "$SDR"   || FAIL=1
run empty-layer-regression  --empty-layer-regression  "$SDR"   || FAIL=1
run color-regression        --color-regression        "$SDR" "$HDR" || FAIL=1
run hdr-switch-regression   --hdr-switch-regression   "$HDR"   || FAIL=1
run startup-regression-10bit --startup-regression     "$SDR10" || FAIL=1

echo "===== 测试轮次结束 FAIL=$FAIL ====="
exit $FAIL
