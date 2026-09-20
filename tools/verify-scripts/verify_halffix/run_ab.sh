#!/usr/bin/env bash
# Interleaved A/B run of the pixel-equivalence harness against the two kernel
# variants. Single variable = FFF.Native.dll next to the exe.
# Usage: run_ab.sh <rounds>
set -uo pipefail
BASE="C:/PLAN/3FCompare/.verify_halffix"
FFOUT="C:/PLAN/3FCompare/.verify_pr_e2e/out"   # FFmpeg DLLs (delay-loaded)
ROUNDS="${1:-2}"

export PATH="$FFOUT:$PATH"
export FFF_TEST_HDR=1                          # force R16G16B16A16_FLOAT swap chain

for i in $(seq 1 "$ROUNDS"); do
  for v in fixed prefix; do
    echo "###### ROUND $i  VARIANT=$v  $(date +%H:%M:%S) ######"
    ( cd "$BASE/run_$v" && ./pixel_equiv.exe )
    echo "ROUND=$i VARIANT=$v EXIT=${PIPESTATUS[0]}"
    sleep 5
  done
done
