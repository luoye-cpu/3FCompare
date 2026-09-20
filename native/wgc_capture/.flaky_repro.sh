#!/bin/bash
# 复现 selftest.exe 的间歇性失败：循环运行，一旦「项失败」计数非 0 就保存整份输出并停止。
# 用法： ./.flaky_repro.sh <次数>
cd "$(dirname "$0")" || exit 1
N="${1:-40}"
OUTDIR="${OUTDIR:-/tmp/flaky}"
mkdir -p "$OUTDIR"
for i in $(seq 1 "$N"); do
  f="$OUTDIR/run_$(printf '%02d' "$i").txt"
  timeout 90 ./selftest.exe > "$f" 2>&1
  rc=$?
  if grep -q '0 项失败' "$f"; then
    echo "run $i: PASS (exit=$rc)"
  else
    echo "run $i: FAIL (exit=$rc)  <-- 已保存 $f"
    cp "$f" "$OUTDIR/FAILED_run_$(printf '%02d' "$i").txt"
    break
  fi
done
echo "---- 完成，输出目录 $OUTDIR ----"
