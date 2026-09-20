#!/bin/bash
# magnifybench 重复率测量：<n> <标签>
set -u
BIN="/c/Users/20210/AppData/Local/Temp/pubtest"
EXE="C:/Users/20210/AppData/Local/Temp/pubtest/3FCompare.exe"
H264="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
OUT="/c/PLAN/3FCompare/.review_pr/pubtest"
N="${1:-6}"; TAG="${2:-mbz2}"
for i in $(seq 1 "$N"); do
  f="$OUT/${TAG}_x$i.txt"
  ( cd "$BIN" && timeout 120 "$EXE" --magnifybench "$H264" 2 2 ) > "$f" 2>&1
  rc=$?
  last=$(grep -E "放大后平均|magnifybench: 完成|BENCHPHASE|BENCHGEOM" "$f" | tail -1)
  echo "[${TAG}_x$i] exit=$rc | $last"
done
