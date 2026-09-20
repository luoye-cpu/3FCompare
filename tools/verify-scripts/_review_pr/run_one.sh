#!/usr/bin/env bash
# 崩前事件序列取证运行器（P1 4 路崩溃）——只读产品代码，产物落 .review_pr/crashseq/
#
# 用法： run_one.sh <runN>
# 产物： runN.log        = 运行前存档（当天 component 日志的累计快照）
#        runN.after.log  = 运行后存档（累计快照，用于切出本次切片）
#        runN.slice.log  = 本次运行的事件切片（从最后一个"会话开始"横幅到文件末尾）
#        runN.stdout.log = 进程 stdout/stderr
set -u
RUN="$1"
ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/crashseq"
VIDEO="$ROOT/testmedia/media/real/real_4k_h264_60m.mp4"
CLOG="$BIN/logs/component-2026-09-20.log"
CSV="$OUT/runs.csv"

mkdir -p "$OUT"
[ -f "$CSV" ] || echo "run,exit,wall_s,crashed,start_clock,exit_clock,last_line_clock,phase" > "$CSV"

# 运行前存档：多次运行会混写同一个文件，不先存档就无法归因
cp "$CLOG" "$OUT/run${RUN}.log" 2>/dev/null

t0=$(date +%s.%3N)
start_clock=$(date +%H:%M:%S)
# cd 到 exe 目录再跑（与 .review_pr/crash_attrib/run_interleaved.sh 的既有约定一致）；
# 素材传绝对路径。timeout 兜底：崩溃后若被 WER 挂住不至于无限等待。
( cd "$BIN" && timeout 180 ./3FCompare.exe --multitest "$VIDEO" 4 30 \
    > "$OUT/run${RUN}.stdout.log" 2>&1 )
code=$?
t1=$(date +%s.%3N)
exit_clock=$(date +%H:%M:%S)

cp "$CLOG" "$OUT/run${RUN}.after.log" 2>/dev/null

# 本次切片：after 文件里最后一个"会话开始"横幅 → 文件末尾
awk '/会话开始/{start=NR} {line[NR]=$0} END{for(i=start;i<=NR;i++) print line[i]}' \
    "$OUT/run${RUN}.after.log" > "$OUT/run${RUN}.slice.log"

crashed=0; [ "$code" -ge 128 ] && crashed=1
last_clock=$(grep -o '^[0-9][0-9]:[0-9][0-9]:[0-9][0-9]\.[0-9][0-9][0-9]' \
    "$OUT/run${RUN}.slice.log" | tail -1)
phase=$(tail -1 "$OUT/run${RUN}.stdout.log" 2>/dev/null | tr -d '\r' | cut -c1-70)
wall=$(awk -v a="$t0" -v b="$t1" 'BEGIN{printf "%.1f", b-a}')

printf 'run%-2s exit=%-4s crash=%s wall=%-5ss %s→%s 末条=%s\n       阶段: %s\n' \
    "$RUN" "$code" "$crashed" "$wall" "$start_clock" "$exit_clock" "${last_clock:-?}" "$phase"
echo "$RUN,$code,$wall,$crashed,$start_clock,$exit_clock,${last_clock:-?},\"$phase\"" >> "$CSV"
