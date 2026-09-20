#!/bin/bash
# 3FCompare NativeAOT 发布包测试矩阵运行器
# 只读产品代码；产物落 .review_pr/pubtest/
set -u
BIN="/c/Users/20210/AppData/Local/Temp/pubtest"
EXE="C:/Users/20210/AppData/Local/Temp/pubtest/3FCompare.exe"
MED="C:/PLAN/3FCompare/testmedia/media/real"
OUT="/c/PLAN/3FCompare/.review_pr/pubtest"
H264="$MED/real_4k_h264_60m.mp4"
HEVC10="$MED/real_4k_hevc10_60m.mp4"
HDR10="$MED/real_4k_hevc_hdr10_60m.mp4"
AV1="$MED/real_4k_av1_10bit_60m.mp4"
mkdir -p "$OUT"

run() { # run <tag> <timeout> <args...>
  local tag="$1"; shift
  local to="$1"; shift
  local f="$OUT/$tag.txt"
  ( cd "$BIN" && timeout "$to" "$EXE" "$@" ) > "$f" 2>&1
  local rc=$?
  local last
  last=$(grep -E "全部通过|失败|exit|✓" "$f" | tail -1)
  echo "[$tag] exit=$rc | $last"
}

case "${1:-all}" in
  smoke)
    run "smoke_cm2" 180 --comparemodetest "$H264" 2
    ;;
  cm234)
    for i in 1 2 3; do run "cm2_r$i" 200 --comparemodetest "$H264" 2; done
    for i in 1 2 3; do run "cm3_r$i" 260 --comparemodetest "$H264" 3; done
    for i in 1 2 3; do run "cm4_r$i" 320 --comparemodetest "$H264" 4; done
    ;;
  codecs)
    for i in 1 2 3; do
      run "cm3_hevc10_r$i" 260 --comparemodetest "$HEVC10" 3
      run "cm3_hdr10_r$i"  260 --comparemodetest "$HDR10" 3
      run "cm3_av1_r$i"    260 --comparemodetest "$AV1" 3
    done
    ;;
  gates)
    for i in 1 2 3; do run "st_r$i" 320 --selftest "$H264"; done
    for i in 1 2 3; do run "mb_z2_r$i" 320 --magnifybench "$H264" 2 2; done
    ;;
  all)
    "$0" cm234; "$0" codecs; "$0" gates
    ;;
esac
echo "PHASE-DONE:${1:-all}"
