#!/bin/bash
# 3FCompare 实机测试矩阵运行器（只读产品代码，产物落 .review_pr/）
set -u
BIN="C:/PLAN/3FCompare/src/3FCompare/bin/Debug/net11.0-windows"
MED="C:/PLAN/3FCompare/testmedia/media/real"
OUT="/c/PLAN/3FCompare/.review_pr"
H264="$MED/real_4k_h264_60m.mp4"
HEVC10="$MED/real_4k_hevc10_60m.mp4"
HDR10="$MED/real_4k_hevc_hdr10_60m.mp4"
AV1="$MED/real_4k_av1_10bit_60m.mp4"

run() { # run <tag> <timeout> <args...>
  local tag="$1"; shift
  local to="$1"; shift
  local f="$OUT/$tag.txt"
  cd "$BIN"
  timeout "$to" ./3FCompare.exe "$@" > "$f" 2>&1
  local rc=$?
  echo "[$tag] exit=$rc  -> $f"
}

# ── comparemodetest 2/3/4 路 × 3 ──
for i in 1 2 3; do
  run "cm2_r$i" 180 --comparemodetest "$H264" 2
done
for i in 1 2 3; do
  run "cm3_r$i" 240 --comparemodetest "$H264" 3
done
for i in 1 2 3; do
  run "cm4_r$i" 300 --comparemodetest "$H264" 4
done

# ── 多编码 3 路 × 3 ──
for i in 1 2 3; do
  run "cm3_hevc10_r$i" 240 --comparemodetest "$HEVC10" 3
  run "cm3_hdr10_r$i"  240 --comparemodetest "$HDR10" 3
  run "cm3_av1_r$i"    240 --comparemodetest "$AV1" 3
done

# ── selftest × 3 ──
for i in 1 2 3; do
  run "st_r$i" 300 --selftest "$H264"
done

# ── magnifybench 2 路 z=2 / z=3 × 3 ──
for i in 1 2 3; do
  run "mb_z2_r$i" 300 --magnifybench "$H264" 2 2
done
for i in 1 2 3; do
  run "mb_z3_r$i" 300 --magnifybench "$H264" 2 3
done

echo "DONE"
