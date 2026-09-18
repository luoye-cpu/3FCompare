#!/bin/bash
# issue #7（多路播放 Present/交换链竞态崩溃）修复验证 + 回归跑测
# 历史基线：2 路 4K，20s 预算 3 次 = 1 过 2 崩；25s 预算 2 次 = 0 过 2 崩（约 50% 崩溃率）
# 判定：10 次全过 ⇒ 若崩溃率仍为 50%，连续 10 次全过的概率仅 0.098%
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
mkdir -p "$LOG"
SUM="$LOG/summary.txt"
: > "$SUM"

run() {
  local name="$1"; shift
  local log="$LOG/$name.log"
  timeout 300 "$APP" "$@" > "$log" 2>&1
  local code=$?
  echo "[$name] exit=$code" | tee -a "$SUM"
}

echo "=== 内核 DLL 指纹 ===" | tee -a "$SUM"
sha256sum /c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll | tee -a "$SUM"

echo "=== [1] multitest 2 路 × 10 次（25s）===" | tee -a "$SUM"
for i in $(seq 1 10); do
  run "multitest2_$i" --multitest "$MEDIA/real_4k_hevc10_60m.mp4" 2 25
done

echo "=== [2] multitest 4 路 × 2 次（20s）===" | tee -a "$SUM"
for i in 1 2; do
  run "multitest4_$i" --multitest "$MEDIA/real_4k_h264_60m.mp4" 4 20
done

echo "=== [3] selftest 单路全量 × 5 素材 ===" | tee -a "$SUM"
for m in real_4k_h264_60m real_4k_hevc_hdr10_60m real_4k_av1_10bit_60m real_8k_av1_hdr10_100m real_8k_hevc10_150m; do
  run "selftest_$m" --selftest "$MEDIA/$m.mp4"
done

echo "=== [4] sessiontest 会话往返 ===" | tee -a "$SUM"
run "sessiontest" --sessiontest "$MEDIA/real_4k_h264_60m.mp4" "$MEDIA/real_4k_hevc_hdr10_60m.mp4"

echo "=== [5] screentest 抓帧导出 ===" | tee -a "$SUM"
run "screentest" --screentest "$MEDIA/real_4k_h264_60m.mp4" "C:/PLAN/3FCompare/.3fc_verify_20260917/screentest.png"

echo "=== 全部结束 ===" | tee -a "$SUM"
