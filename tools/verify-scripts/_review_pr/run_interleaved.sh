#!/usr/bin/env bash
# 交错（同批次交替）实验运行器 —— 临时归因用，不提交。
# 为什么必须交错：docs/18 §2.5 已证明崩溃率对系统状态敏感，跨批次（先跑完 A 再跑 B）比较无效。
#
# 用法： run_interleaved.sh <rounds> <armSpec...>
#   armSpec = "标签|素材路径|路数|时长秒|环境变量赋值(可空)"
# 每轮按 arm 顺序各跑 1 次，直到跑满 rounds 轮。
set -u
ROUNDS="$1"; shift
ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/crash_attrib"
LOG="$BIN/logs/app-2026-09-19.log"
CSV="$OUT/interleaved.csv"

mkdir -p "$OUT/runlogs"
[ -f "$CSV" ] || echo "round,order,tag,video,routes,dur,exit,crashed,drift_seeks,wall_s,phase" > "$CSV"

for r in $(seq 1 "$ROUNDS"); do
  order=0
  for spec in "$@"; do
    order=$((order+1))
    IFS='|' read -r TAG VIDEO ROUTES DUR ENVV <<< "$spec"
    before=$(grep -c "漂移校正" "$LOG" 2>/dev/null || echo 0)
    t0=$(date +%s)
    if [ -n "$ENVV" ]; then
      # 注意：本机 PATH 里的 `env` 是个坏 shim（会让子进程静默立刻退出），
      # 必须用 bash 内联赋值 / export，不能走 `env`。
      ( cd "$BIN" && export $ENVV && ./3FCompare.exe --multitest "$VIDEO" "$ROUTES" "$DUR" \
          > "$OUT/runlogs/${TAG}_r${r}.log" 2>&1 )
    else
      ( cd "$BIN" && ./3FCompare.exe --multitest "$VIDEO" "$ROUTES" "$DUR" \
          > "$OUT/runlogs/${TAG}_r${r}.log" 2>&1 )
    fi
    code=$?
    t1=$(date +%s)
    after=$(grep -c "漂移校正" "$LOG" 2>/dev/null || echo 0)
    drift=$((after - before))
    if [ "$code" -ge 128 ]; then crashed=1; else crashed=0; fi
    # 崩溃发生的阶段 = 运行日志最后一行
    phase=$(tail -1 "$OUT/runlogs/${TAG}_r${r}.log" 2>/dev/null | tr -d '\r' | cut -c1-40)
    echo "$r,$order,$TAG,$(basename "$VIDEO"),$ROUTES,$DUR,$code,$crashed,$drift,$((t1-t0)),\"$phase\"" >> "$CSV"
    printf 'r%-2s %-14s exit=%-4s crash=%s drift=%-3s wall=%-3ss | %s\n' \
      "$r" "$TAG" "$code" "$crashed" "$drift" "$((t1-t0))" "$phase"
  done
done
