#!/usr/bin/env bash
# 判别实验：z=1（对照，静置播放）vs z=3（实验，放大风暴），2 路 / 同一 4K 素材。
# 同批次交错 A→B，各 ROUNDS 次。只变 zoom 一个变量。
#
# 用法： run_zoom_ab.sh "<轮次列表>" [AB|BA|ALT]
#   例： run_zoom_ab.sh "1 2"        （A 先 B 后）
#        run_zoom_ab.sh "9 10" BA    （顺序对调：B 先 A 后 —— 用于拆开「臂」与「同轮次序/时刻」混淆）
#        run_zoom_ab.sh "17 18" ALT  （逐轮交替 AB/BA，使两臂在次序上平衡）
# 产物： results.tsv / runlogs/<ARM>_r<k>.log
#
# 混杂说明（不静默修改）：z=1 时 magnifybench 只跑一个基线阶段
# （源码 MainWindow.SelfTest.cs:1813-1819 显式 return），故 A 臂总播放时长 = secondsPerPhase，
# B 臂 = 2×secondsPerPhase。该时长差在分析阶段用「崩溃发生在哪个阶段」拆开处理。
set -u
ROUNDS_LIST="${1:-1 2 3 4 5 6 7 8}"
ORDER="${2:-AB}"
ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
EXE="./3FCompare.exe"
OUT="$ROOT/.review_pr/zoomjudge"
VIDEO="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
TSV="$OUT/results.tsv"
WERROOT="/c/ProgramData/Microsoft/Windows/WER/ReportArchive"

mkdir -p "$OUT/runlogs"
[ -f "$TSV" ] || printf "round\tarm\tzoom\texit\tcrash\tsig3\tsig6\tsig7\tsig8\twer_new\twall_s\tphase_last\n" > "$TSV"

# 输出一行： <计数>|<最新报告目录名>|<sig3>|<sig6>|<sig7>|<sig8>
wer_state() {
  local cnt newest
  cnt=$(ls -d "$WERROOT"/AppCrash_3FCompare* 2>/dev/null | wc -l | tr -d ' ')
  newest=$(ls -dt "$WERROOT"/AppCrash_3FCompare* 2>/dev/null | head -1)
  if [ -z "$newest" ]; then echo "$cnt||"; return; fi
  local txt
  txt=$(iconv -f UTF-16LE -t UTF-8 "$newest/Report.wer" 2>/dev/null | grep -aE '^(Sig\[3\]\.Value|Sig\[6\]\.Value|Sig\[7\]\.Value|Sig\[8\]\.Value)=' )
  local s3 s6 s7 s8
  s3=$(echo "$txt" | grep -a '^Sig\[3\]' | head -1 | cut -d= -f2- | tr -d '\r')
  s6=$(echo "$txt" | grep -a '^Sig\[6\]' | head -1 | cut -d= -f2- | tr -d '\r')
  s7=$(echo "$txt" | grep -a '^Sig\[7\]' | head -1 | cut -d= -f2- | tr -d '\r')
  s8=$(echo "$txt" | grep -a '^Sig\[8\]' | head -1 | cut -d= -f2- | tr -d '\r')
  echo "$cnt|$(basename "$newest")|$s3|$s6|$s7|$s8"
}

run_one() {  # $1=round $2=arm $3=zoom
  local r="$1" arm="$2" z="$3"
  local log="$OUT/runlogs/${arm}_r${r}.log"
  local pre pre_key post post_key
  pre="$(wer_state)"; pre_key="$(echo "$pre" | cut -d'|' -f1,2)"
  local t0=$(date +%s)
  ( cd "$BIN" && timeout 200 "$EXE" --magnifybench "$VIDEO" 2 "$z" 30 ) > "$log" 2>&1
  local ec=$?
  local t1=$(date +%s)

  # WER 归档异步且有节流（实测今日 167 次崩溃仅归档 88 份）：最多等 8s。
  # 主签名通道改用「.NET Runtime 事件 1026」，事后按时间窗对齐（见 analyze.md）。
  local waited=0 sig3="" sig6="" sig7="" sig8="" wer_new=0
  while [ "$waited" -lt 8 ]; do
    post="$(wer_state)"; post_key="$(echo "$post" | cut -d'|' -f1,2)"
    if [ "$post_key" != "$pre_key" ]; then
      wer_new=1
      sig3="$(echo "$post" | cut -d'|' -f3)"
      sig6="$(echo "$post" | cut -d'|' -f4)"
      sig7="$(echo "$post" | cut -d'|' -f5)"
      sig8="$(echo "$post" | cut -d'|' -f6)"
      break
    fi
    sleep 2; waited=$((waited+2))
  done

  local crash=0
  case "$ec" in
    139|132|11|6|4|3|3221225477|3221225501|3221225725) crash=1 ;;
  esac
  [ "$wer_new" = "1" ] && crash=1

  local phase
  phase="$(tail -1 "$log" 2>/dev/null | tr -d '\r' | cut -c1-48)"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t"%s"\n' \
    "$r" "$arm" "$z" "$ec" "$crash" "$sig3" "$sig6" "$sig7" "$sig8" "$wer_new" "$((t1-t0))" "$phase" >> "$TSV"
  printf '[%s] r%-2s %-2s z=%s exit=%-4s crash=%s wer=%s sig3=%s sig8=%s wall=%ss\n         last: %s\n' \
    "$(date +%H:%M:%S)" "$r" "$arm" "$z" "$ec" "$crash" "$wer_new" "${sig3:-none}" "${sig8:-none}" "$((t1-t0))" "$phase"
}

echo "════ 批次开始 $(date +%F' '%T)  轮次=[$ROUNDS_LIST]  顺序=$ORDER  A=z1 / B=z3  2路 4K ════"
for r in $ROUNDS_LIST; do
  # ALT：逐轮交替 A→B / B→A，使两臂在「同轮次序」上平衡（用于拆开臂与次序混淆）
  local_order="$ORDER"
  if [ "$ORDER" = "ALT" ]; then
    if [ $((r % 2)) -eq 1 ]; then local_order="AB"; else local_order="BA"; fi
  fi
  if [ "$local_order" = "BA" ]; then
    run_one "$r" B 3
    run_one "$r" A 1
  else
    run_one "$r" A 1
    run_one "$r" B 3
  fi
done
echo "════ 批次结束 $(date +%F' '%T) ════"
