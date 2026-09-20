#!/usr/bin/env bash
# 多卡对照实验运行器（同批次交错）。只读产品代码，产物落 .review_pr/gpu_spread/
# 用法： run_spread.sh <rounds> <armSpec...>
#   armSpec = "标签|环境变量赋值(可空)"
set -u
ROUNDS="$1"; shift
ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/gpu_spread"
VIDEO="$ROOT/testmedia/media/real/real_4k_h264_60m.mp4"
LOG="$BIN/logs/app-2026-09-19.log"
CSV="$OUT/spread2.csv"

mkdir -p "$OUT/runlogs"
[ -f "$CSV" ] || echo "round,order,tag,exit,crashed,wall_s,adapter_lines,requested,actual(vendor:device),phase" > "$CSV"

for r in $(seq 1 "$ROUNDS"); do
  order=0
  for spec in "$@"; do
    order=$((order+1))
    IFS='|' read -r TAG ENVV <<< "$spec"
    before=$(wc -l < "$LOG" 2>/dev/null || echo 0)
    t0=$(date +%s)
    if [ -n "$ENVV" ]; then
      ( cd "$BIN" && export "$ENVV" && timeout 150 ./3FCompare.exe --multitest "$VIDEO" 4 30 \
          > "$OUT/runlogs/p2_${TAG}_r${r}.log" 2>&1 )
    else
      ( cd "$BIN" && timeout 150 ./3FCompare.exe --multitest "$VIDEO" 4 30 \
          > "$OUT/runlogs/p2_${TAG}_r${r}.log" 2>&1 )
    fi
    code=$?
    t1=$(date +%s)
    # 抽取本次运行新增日志里的 adapter 行（内核 A11 会打印**真实使用**的卡）
    tail -n +$((before+1)) "$LOG" 2>/dev/null \
      | grep -a "device adapter requested=" > "$OUT/runlogs/p2_${TAG}_r${r}.adapters" || true
    alines=$(grep -c . "$OUT/runlogs/p2_${TAG}_r${r}.adapters" 2>/dev/null || echo 0)
    # 实际使用的卡 = 每行的 vendor/device（requested 是请求值，可能被回退）
    adesc=$(sed -n 's/.*vendor=\([0-9]*\) device=\([0-9]*\).*/\1:\2/p' \
              "$OUT/runlogs/p2_${TAG}_r${r}.adapters" 2>/dev/null | tr '\n' '/' )
    areq=$(sed -n 's/.*requested=\(-\?[0-9]*\).*/\1/p' \
              "$OUT/runlogs/p2_${TAG}_r${r}.adapters" 2>/dev/null | tr '\n' '/' )
    if [ "$code" -ge 128 ]; then crashed=1; else crashed=0; fi
    phase=$(tail -1 "$OUT/runlogs/p2_${TAG}_r${r}.log" 2>/dev/null | tr -d '\r' | cut -c1-36)
    echo "$r,$order,$TAG,$code,$crashed,$((t1-t0)),$alines,\"$areq\",\"$adesc\",\"$phase\"" >> "$CSV"
    printf 'r%-2s %-6s exit=%-4s crash=%s wall=%-3ss adapters=%-2s | %s\n' \
      "$r" "$TAG" "$code" "$crashed" "$((t1-t0))" "$alines" "$phase"
  done
done
echo "DONE"
