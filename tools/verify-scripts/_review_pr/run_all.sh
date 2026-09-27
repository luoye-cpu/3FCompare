#!/bin/bash
# 29 号验收：完整实机验证矩阵
# 仅测试用；不修改任何 src/ tests/ 文件
set -u
ROOT="/c/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/verify29"
MEDIA4K="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
MEDIA4K_HEVC="C:/PLAN/3FCompare/testmedia/media/real/real_4k_hevc10_60m.mp4"
SUMMARY="$OUT/summary.tsv"

mkdir -p "$OUT"
: > "$SUMMARY"
printf "%-28s\t%-8s\t%-6s\t%-10s\t%s\n" "ITEM" "CONFIG" "RUN" "EXIT" "VERDICT" >> "$SUMMARY"

run_one() {  # $1=item $2=config $3=run $4=timeout $5..=argv
  local item="$1" cfg="$2" runno="$3" tmo="$4"; shift 4
  local log="$OUT/${item}_${cfg}_r${runno}.log"
  local t0=$(date +%s)
  timeout "$tmo" "$BIN/3FCompare.exe" "$@" > "$log" 2>&1
  local ec=$?
  local wall=$(( $(date +%s) - t0 ))
  local verdict
  case "$ec" in
    0)   verdict="PASS" ;;
    4)
      # exit=4 有**两个**来源，不能只看退出码：
      #   ① --magnifybench 放大闸门拒绝 —— 程序会打印 "SKIPPED 闸门拒绝" 与
      #      "未完成（exit=4 SKIPPED）"，这才是真 SKIP；
      #   ② 真崩溃/异常退出恰好也退 4 —— 此时日志里不会有 SKIPPED。
      # 旧写法一律当 SKIP ⇒ ② 被静默吞掉（假绿）。故回看 $log：只有确实出现
      # SKIPPED 才判 SKIP，否则按失败处理。
      if grep -q "SKIPPED" "$log" 2>/dev/null; then
        verdict="SKIP"
      else
        verdict="FAIL($ec,无SKIPPED)"
      fi
      ;;
    124) verdict="TIMEOUT" ;;
    139|3221225477) verdict="CRASH_AV" ;;
    132|3221225501) verdict="CRASH_ILLEGAL" ;;
    3221225725) verdict="CRASH_STACK" ;;
    *)   verdict="FAIL($ec)" ;;
  esac
  printf "%-28s\t%-8s\t%-6s\t%-10s\t%s\n" "$item" "$cfg" "$runno" "$ec" "$verdict" >> "$SUMMARY"
  echo "[$(date +%H:%M:%S)] $item $cfg r$runno exit=$ec ($verdict) wall=${wall}s"
}

echo "════ 开始：$(date +%F' '%T) ════"

# ── 1. comparemodetest 2 / 3 / 4 路 × 3 次 ──
for r in 2 3 4; do
  for i in 1 2 3; do
    run_one "comparemodetest_${r}r" "cm$r" "$i" 300 --comparemodetest "$MEDIA4K" "$r"
  done
done

# ── 2. selftest × 3 ──
for i in 1 2 3; do
  run_one "selftest" "st" "$i" 420 --selftest "$MEDIA4K"
done

# ── 3. magnifybench 4 路 1/2/3 倍 × 2 ──
for z in 1 2 3; do
  for i in 1 2; do
    run_one "magnifybench_4r_z${z}" "mb4z$z" "$i" 300 --magnifybench "$MEDIA4K" 4 "$z"
  done
done

# ── 4. 补充：magnifybench 2 路（4 路已知崩溃，2 路取功能证据）──
for z in 2 3; do
  for i in 1 2; do
    run_one "magnifybench_2r_z${z}" "mb2z$z" "$i" 300 --magnifybench "$MEDIA4K" 2 "$z"
  done
done

# ── 5. 补充：其它 4K 编码格式 comparemodetest 3 路 × 1（覆盖解码器多样性）──
for m in real_4k_hevc10_60m real_4k_hevc_hdr10_60m real_4k_av1_10bit_60m; do
  run_one "comparemodetest_3r_${m}" "cm3x" "1" 300 --comparemodetest "C:/PLAN/3FCompare/testmedia/media/real/${m}.mp4" 3
done

echo "════ 结束：$(date +%F' '%T) ════"
echo ""
cat "$SUMMARY"
