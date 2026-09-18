#!/bin/bash
# 与 verify-issue7-ab 相同，但改用 **4 路** 以提高崩溃发生率、放大 A/B 差异，
# 从而用可接受的样本量达到统计显著性。
#
# 主要终点 = exit 139（0xC0000005 原生崩溃）
# 次要终点 = exit 1（漂移 >100ms 断言失败，属另一个问题，仅记录）
set -u
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll"
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
A="/c/PLAN/3FCompare/.3fc_verify_20260917/kernel_pr8reverted.dll"
B="/c/PLAN/3FCompare/third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/ab4_summary.txt"
PAIRS=${PAIRS:-8}

: > "$SUM"
echo "=== issue #7 受控交替对照 · 4 路（A=修复前 / B=修复后，各 $PAIRS 次）===" | tee -a "$SUM"
echo "A(修复前) $(sha256sum "$A" | cut -c1-16)   B(修复后) $(sha256sum "$B" | cut -c1-16)" | tee -a "$SUM"
echo "静置冷却 45s ..." | tee -a "$SUM"
sleep 45

pa=0; ca=0; oa=0
pb=0; cb=0; ob=0
seqA=""; seqB=""

for p in $(seq 1 "$PAIRS"); do
  for side in A B; do
    if [ "$side" = "A" ]; then src="$A"; else src="$B"; fi
    cp -f "$src" "$BIN"
    timeout 300 "$APP" --multitest "$MEDIA" 4 20 > "$LOG/ab4_${p}${side}.log" 2>&1
    c=$?
    case "$c" in
      0)   [ "$side" = "A" ] && pa=$((pa+1)) || pb=$((pb+1));;
      # 139 = SIGSEGV ← 0xC0000005 访问违例
      # 132 = SIGILL  ← 0xC000001D 非法指令（历史转储簇 B，同样是崩溃，别漏统计）
      139|3221225477|132|3221225501) [ "$side" = "A" ] && ca=$((ca+1)) || cb=$((cb+1));;
      *)   [ "$side" = "A" ] && oa=$((oa+1)) || ob=$((ob+1));;
    esac
    if [ "$side" = "A" ]; then seqA="$seqA $c"; else seqB="$seqB $c"; fi
    printf "对%02d %s exit=%-3s (A崩=%s B崩=%s)\n" "$p" "$side" "$c" "$ca" "$cb" | tee -a "$SUM"
  done
done

echo "--- 结果（4 路）---" | tee -a "$SUM"
echo "A 修复前: 通过=$pa 崩溃=$ca 其它=$oa   序列:$seqA" | tee -a "$SUM"
echo "B 修复后: 通过=$pb 崩溃=$cb 其它=$ob   序列:$seqB" | tee -a "$SUM"
cp -f "$B" "$BIN"
echo "已恢复部署为 B(修复后)" | tee -a "$SUM"
