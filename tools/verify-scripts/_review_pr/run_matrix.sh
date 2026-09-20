#!/usr/bin/env bash
# 临时归因实验脚本（不提交）。用法：
#   run_matrix.sh <tag> <video> <routes> <durationSec> <runs>
# 记录每次退出码 / 是否崩溃 / 该次运行期间漂移校正次数。
set -u
TAG="$1"; VIDEO="$2"; ROUTES="$3"; DUR="$4"; RUNS="$5"
ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/crash_attrib"
LOG="$BIN/logs/app-2026-09-19.log"
CSV="$OUT/results.csv"

mkdir -p "$OUT/runlogs"
[ -f "$CSV" ] || echo "tag,video,routes,dur,run,exit,crashed,drift_seeks,wall_s" > "$CSV"

for i in $(seq 1 "$RUNS"); do
  before=$(grep -c "漂移校正" "$LOG" 2>/dev/null || echo 0)
  t0=$(date +%s)
  ( cd "$BIN" && _3FC_CRASH_TRACE="${CRASH_TRACE:-}" ./3FCompare.exe --multitest "$VIDEO" "$ROUTES" "$DUR" \
      > "$OUT/runlogs/${TAG}_${i}.log" 2>&1 )
  code=$?
  t1=$(date +%s)
  after=$(grep -c "漂移校正" "$LOG" 2>/dev/null || echo 0)
  drift=$((after - before))
  # 崩溃判定：SIGSEGV(139) 或 Windows 访问违例退出码(3221225477=0xC0000005 映射)
  if [ "$code" -ge 128 ] || [ "$code" -eq 5 ]; then crashed=1; else crashed=0; fi
  echo "$TAG,$(basename "$VIDEO"),$ROUTES,$DUR,$i,$code,$crashed,$drift,$((t1-t0))" >> "$CSV"
  printf '%-22s run %2d/%s exit=%-4s crash=%s drift=%s wall=%ss\n' "$TAG" "$i" "$RUNS" "$code" "$crashed" "$drift" "$((t1-t0))"
done
