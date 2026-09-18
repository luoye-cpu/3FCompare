#!/bin/bash
# issue #7 修复效果的**受控**对照实验
#
# 为什么必须这么做：
#   同一份内核、同条件 2 路 @25s，03:14 批崩 4/10，03:47 批崩 1/20 —— 该崩溃对
#   系统状态高度敏感，跨批次比崩溃率毫无意义。唯一可信的办法是把"修复前/修复后"
#   放进**同一批次交替**跑，让系统漂移对两者影响相同。
#
# A = 回退 PR #8 的对照内核（修复前）  kernel_pr8reverted.dll
# B = 当前基线内核（修复后）            x64/Release/FFF.Native.dll
#
# 固定条件：WerSvc 保持 Stopped；开始前静置冷却；交替顺序 A/B 抵消时间趋势。
set -u
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll"
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
A="/c/PLAN/3FCompare/.3fc_verify_20260917/kernel_pr8reverted.dll"
B="/c/PLAN/3FCompare/third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real/real_4k_hevc10_60m.mp4"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/ab_summary.txt"
PAIRS=${PAIRS:-8}

: > "$SUM"
echo "=== issue #7 受控交替对照（A=修复前 / B=修复后，各 $PAIRS 次）===" | tee -a "$SUM"
echo "A(修复前) $(sha256sum "$A" | cut -c1-16)"   | tee -a "$SUM"
echo "B(修复后) $(sha256sum "$B" | cut -c1-16)"   | tee -a "$SUM"
echo "WerSvc 状态: $(sc query WerSvc 2>/dev/null | grep -i state | head -1 || echo '?')" | tee -a "$SUM"

# 静置冷却，降低上一批高负载留下的 GPU 热状态差异
echo "静置冷却 45s ..." | tee -a "$SUM"
sleep 45

pa=0; ca=0; oa=0
pb=0; cb=0; ob=0
seqA=""; seqB=""

for p in $(seq 1 "$PAIRS"); do
  for side in A B; do
    if [ "$side" = "A" ]; then src="$A"; else src="$B"; fi
    cp -f "$src" "$BIN"
    timeout 300 "$APP" --multitest "$MEDIA" 2 25 > "$LOG/ab_${p}${side}.log" 2>&1
    c=$?
    case "$c" in
      0)   [ "$side" = "A" ] && pa=$((pa+1)) || pb=$((pb+1));;
      # 139 = SIGSEGV ← 0xC0000005 访问违例
      # 132 = SIGILL  ← 0xC000001D 非法指令（历史转储簇 B，同样是崩溃，别漏统计）
      139|3221225477|132|3221225501) [ "$side" = "A" ] && ca=$((ca+1)) || cb=$((cb+1));;
      *)   [ "$side" = "A" ] && oa=$((oa+1)) || ob=$((ob+1));;
    esac
    if [ "$side" = "A" ]; then seqA="$seqA $c"; else seqB="$seqB $c"; fi
    printf "对%02d %s exit=%s  (A 崩=%s / B 崩=%s)\n" "$p" "$side" "$c" "$ca" "$cb" | tee -a "$SUM"
  done
done

echo "--- 结果 ---" | tee -a "$SUM"
echo "A 修复前: 通过=$pa 崩溃=$ca 其它=$oa   序列:$seqA" | tee -a "$SUM"
echo "B 修复后: 通过=$pb 崩溃=$cb 其它=$ob   序列:$seqB" | tee -a "$SUM"
# 收尾：恢复部署为基线内核
cp -f "$B" "$BIN"
echo "已恢复部署为 B(修复后)" | tee -a "$SUM"
