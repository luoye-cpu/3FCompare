#!/bin/bash
# Debug 版 magnifybench 对照：<n>
set -u
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Debug/net11.0-windows"
H264="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
OUT="/c/PLAN/3FCompare/.review_pr/pubtest/debug_mb"
mkdir -p "$OUT"
N="${1:-6}"
for i in $(seq 1 "$N"); do
  f="$OUT/dbg_mbz2_x$i.txt"
  ( cd "$BIN" && timeout 120 ./3FCompare.exe --magnifybench "$H264" 2 2 ) > "$f" 2>&1
  rc=$?
  last=$(grep -E "放大后平均|magnifybench: 完成|BENCHPHASE|BENCHGEOM" "$f" | tail -1)
  echo "[dbg_mbz2_x$i] exit=$rc | $last"
done
