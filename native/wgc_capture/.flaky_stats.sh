#!/bin/bash
# .flaky_stats.sh — 大样本统计 selftest.exe 的间歇性失败。
#
# 用法: ./.flaky_stats.sh [N] [OUTDIR]
#   默认 N=300, OUTDIR=/tmp/flaky_stats
#
# 每次跑一个独立进程（与原始复现方式一致），保存每次的完整输出（含逐项 [PASS]/[FAIL] 与取证），
# 失败/挂起的整份输出另存为 FAILED_*.txt / HANG_*.txt，最后汇总：
#   - 失败率、失败规模分布
#   - 失败断言（用例名+期望/实得）的分布
#   - 每次的「左上 OSD 异常像素比例」代理量，用于与失败做相关性分析
cd "$(dirname "$0")" || exit 1
N="${1:-300}"
OUT="${2:-/tmp/flaky_stats}"
mkdir -p "$OUT"
SUM="$OUT/summary.tsv"
: > "$SUM"

pass=0; failn=0; hang=0
for i in $(seq 1 "$N"); do
  n=$(printf '%04d' "$i")
  f="$OUT/run_$n.txt"
  timeout 90 ./selftest.exe > "$f" 2>&1
  rc=$?
  # OSD 代理量：取第一次出现的「左上 200x120 区异常像素 = a/b (p%)」
  osd=$(grep -o '区异常像素 = [0-9]*/[0-9]* ([0-9.]*%)' "$f" | head -1 | grep -o '([0-9.]*%)' | tr -d '()%')
  [ -z "$osd" ] && osd="NA"
  if [ "$rc" -eq 124 ]; then
    hang=$((hang+1)); cp "$f" "$OUT/HANG_$n.txt"
    echo -e "$i\tHANG\t-\t-\t$osd" >> "$SUM"
    echo "run $n: HANG (最后进入: $(grep -o '^\[[0-9][^]]*\]' "$f" | tail -1))"
  elif [ "$rc" -eq 0 ]; then
    pass=$((pass+1))
    echo -e "$i\tPASS\t72\t0\t$osd" >> "$SUM"
  else
    failn=$((failn+1)); cp "$f" "$OUT/FAILED_$n.txt"
    df=$(grep -o '累计失败 [0-9]*' "$f" | tail -1 | grep -o '[0-9]*')
    echo -e "$i\tFAIL\t-\t$df\t$osd" >> "$SUM"
    echo "run $n: FAIL (失败 $df 项)  OSD异常区=$osd%"
    grep -h '    FAIL ' "$f" | sed 's/^    FAIL /    * /' | sort -u
  fi
done

echo "==================================================="
echo "合计 $N 次：通过 $pass / 断言失败 $failn / 挂起 $hang"
echo "失败率: $(awk -v f=$failn -v n=$N 'BEGIN{printf "%.2f%%", 100.0*f/n}')"
echo "--- 失败规模分布（失败项数 -> 次数）---"
awk -F'\t' '$2=="FAIL"{c[$4]++} END{for(k in c) print k": "c[k]}' "$SUM" | sort -n
echo "--- 失败断言分布（次数 -> 断言）---"
cat "$OUT"/FAILED_*.txt 2>/dev/null | grep '    FAIL ' | sed 's/^    FAIL //; s/   (.*//; s/实得 [-0-9]*//g; s/RGB([0-9,]*)/RGB(..)/g' \
  | sort | uniq -c | sort -rn | head -30
echo "--- OSD 代理量（左上异常像素%）分布：通过 vs 失败 ---"
awk -F'\t' '$2=="PASS"{print $5}' "$SUM" | sort -n | uniq -c | awk '{printf "PASS %s%%x%s\n",$2,$1}' | tail -12
awk -F'\t' '$2=="FAIL"{print $5}' "$SUM" | sort -n | uniq -c | awk '{printf "FAIL %s%%x%s\n",$2,$1}' | tail -12
echo "输出目录: $OUT"
