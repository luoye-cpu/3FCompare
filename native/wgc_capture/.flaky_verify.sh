#!/bin/bash
# 最终验证：连续 N 次运行 selftest.exe（timeout 30s 兜底），统计通过/断言失败/挂起，
# 并汇总「会话重建（持续 Present 窗口）」耗时分布。
cd "$(dirname "$0")" || exit 1
N="${1:-30}"
OUTDIR="${OUTDIR:-/tmp/final}"
mkdir -p "$OUTDIR"
pass=0; failn=0; hang=0
for i in $(seq 1 "$N"); do
  n=$(printf '%03d' "$i")
  timeout 30 ./selftest.exe > "$OUTDIR/out_$n.txt" 2>&1
  rc=$?
  if [ "$rc" -eq 124 ]; then
    hang=$((hang+1))
    last=$(grep -o '^\[[0-9]*\][^（]*' "$OUTDIR/out_$n.txt" | tail -1)
    echo "run $n: HANG(超时)  最后进入: ${last:-<无>}"
    cp "$OUTDIR/out_$n.txt" "$OUTDIR/HANG_$n.txt"
  elif grep -q '0 项失败' "$OUTDIR/out_$n.txt"; then
    pass=$((pass+1))
    echo "run $n: PASS  $(grep -o '会话重建（目标为持续 Present 窗口）: rc=0 耗时 [0-9]* ms' "$OUTDIR/out_$n.txt")"
  else
    failn=$((failn+1))
    echo "run $n: FAIL  $(grep -o '结果：.*===' "$OUTDIR/out_$n.txt")  $(grep -o '\[FAIL\].*' "$OUTDIR/out_$n.txt" | head -1)"
    cp "$OUTDIR/out_$n.txt" "$OUTDIR/FAILED_$n.txt"
  fi
done
echo "==============================================="
echo "合计 $N 次：0 失败 $pass / 断言失败 $failn / 挂起 $hang"
echo "会话重建耗时分布（ms）："
grep -h -o '会话重建（目标为持续 Present 窗口）: rc=0 耗时 [0-9]*' "$OUTDIR"/out_*.txt 2>/dev/null | grep -o '[0-9]*$' | sort -n | uniq -c | tr '\n' ' '
echo
