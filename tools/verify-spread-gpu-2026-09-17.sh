#!/bin/bash
# 验证：把各播放路分散到不同物理 GPU，能否规避 issue #7 崩溃。
#
# 依据：内核 A11 的 preferredAdapterIndex 同时决定解码与渲染用哪张卡
#       （FFmpeg D3D11VA 复用同一个 ID3D11Device），每路完全独立。
#       崩溃根因是"跨渲染器并发 Present"——若各路落在不同 GPU，
#       其 DXGI Present 路径彼此独立，可能顺带规避冲突。
# 已验证分配生效：requested=0→device=11266(5080)，requested=2→device=10464(4060)。
#
# S = 分散（FC_SPREAD_GPUS=0,2 轮转两张 NVIDIA）   B = 集中（默认 -1，同一张卡）
set -u
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows"
APP="$BIN/3FCompare.exe"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/spread_gpu_summary.txt"
PAIRS=${PAIRS:-8}
cd "$BIN" || exit 1

: > "$SUM"
echo "=== 多卡分散验证（S=分散到 0,2 / B=集中默认，各 $PAIRS 次，8 路 @25s）===" | tee -a "$SUM"
echo "静置冷却 45s ..." | tee -a "$SUM"
sleep 45

ps_=0; cs=0; os=0
pb=0; cb=0; ob=0
seqS=""; seqB=""

for p in $(seq 1 "$PAIRS"); do
  for side in S B; do
    if [ "$side" = "S" ]; then
      FC_SPREAD_GPUS=0,2 timeout 300 "$APP" --multitest "$MEDIA" 8 25 > "$LOG/sg_${p}S.log" 2>&1
    else
      timeout 300 "$APP" --multitest "$MEDIA" 8 25 > "$LOG/sg_${p}B.log" 2>&1
    fi
    c=$?
    case "$c" in
      0)   [ "$side" = "S" ] && ps_=$((ps_+1)) || pb=$((pb+1));;
      139|3221225477|132|3221225501) [ "$side" = "S" ] && cs=$((cs+1)) || cb=$((cb+1));;
      *)   [ "$side" = "S" ] && os=$((os+1)) || ob=$((ob+1));;
    esac
    if [ "$side" = "S" ]; then seqS="$seqS $c"; else seqB="$seqB $c"; fi
    printf "对%02d %s exit=%-3s (S崩=%s B崩=%s)\n" "$p" "$side" "$c" "$cs" "$cb" | tee -a "$SUM"
  done
done

echo "--- 结果 ---" | tee -a "$SUM"
echo "S 分散多卡: 通过=$ps_ 崩溃=$cs 其它=$os   序列:$seqS" | tee -a "$SUM"
echo "B 集中单卡: 通过=$pb 崩溃=$cb 其它=$ob   序列:$seqB" | tee -a "$SUM"
