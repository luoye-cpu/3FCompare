#!/bin/bash
# 验证"跨渲染器并发 Present"是否为 issue #7 的触发条件。
#
# G = 加了**进程级 Present 门**（全渲染器共享递归锁，串行化所有 Present）的实验内核
# B = 基线内核（无门）
# 同批次交替跑，8 路 @25s。
#   若 G 崩溃显著少于 B ⇒ 跨渲染器并发 Present 确为触发条件（内核问题，可修）
#   若两者相当         ⇒ 根因不在这层（转向第三方 hook / DXGI 内部）
#
# 统计口径：exit=139(0xC0000005) 与 exit=132(0xC000001D) 都算崩溃；exit=1 是漂移断言，另计。
set -u
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll"
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
G="/c/PLAN/3FCompare/.3fc_verify_20260917/kernel_gate_v4.dll"
B="/c/PLAN/3FCompare/third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/gate_v4_8route_summary.txt"
PAIRS=${PAIRS:-8}

: > "$SUM"
echo "=== Present 门验证（V4=非阻塞串行(8路) / B=基线，各 $PAIRS 次，8 路 @25s）===" | tee -a "$SUM"
echo "G(有门) $(sha256sum "$G" | cut -c1-16)   B(基线) $(sha256sum "$B" | cut -c1-16)" | tee -a "$SUM"
echo "静置冷却 45s ..." | tee -a "$SUM"
sleep 45

pg=0; cg=0; og=0
pb=0; cb=0; ob=0
seqG=""; seqB=""

for p in $(seq 1 "$PAIRS"); do
  for side in G B; do
    if [ "$side" = "G" ]; then src="$G"; else src="$B"; fi
    cp -f "$src" "$BIN"
    timeout 300 "$APP" --multitest "$MEDIA" 8 25 > "$LOG/g8v4_${p}${side}.log" 2>&1
    c=$?
    case "$c" in
      0)   [ "$side" = "G" ] && pg=$((pg+1)) || pb=$((pb+1));;
      139|3221225477|132|3221225501) [ "$side" = "G" ] && cg=$((cg+1)) || cb=$((cb+1));;
      *)   [ "$side" = "G" ] && og=$((og+1)) || ob=$((ob+1));;
    esac
    if [ "$side" = "G" ]; then seqG="$seqG $c"; else seqB="$seqB $c"; fi
    printf "对%02d %s exit=%-3s (G崩=%s B崩=%s)\n" "$p" "$side" "$c" "$cg" "$cb" | tee -a "$SUM"
  done
done

echo "--- 结果 ---" | tee -a "$SUM"
echo "G 有门: 通过=$pg 崩溃=$cg 其它=$og   序列:$seqG" | tee -a "$SUM"
echo "B 基线: 通过=$pb 崩溃=$cb 其它=$ob   序列:$seqB" | tee -a "$SUM"
cp -f "$B" "$BIN"
echo "已恢复部署为 B(基线)" | tee -a "$SUM"
