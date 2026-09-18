#!/bin/bash
# 基线健康检查：确认经过多轮内核实验后，当前部署的基线内核仍然完全正常。
# 只跑回归项（不含 multitest，那是有已知崩溃的项，另做专项）。
set -u
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/baseline_regress.txt"
: > "$SUM"

echo "=== 基线健康检查（部署内核 sha256）===" | tee -a "$SUM"
sha256sum /c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll | tee -a "$SUM"

echo "=== [1] Core 单元测试 ===" | tee -a "$SUM"
(cd /c/PLAN/3FCompare && dotnet test tests/3FCompare.Core.Tests/3FCompare.Core.Tests.csproj -c Release --no-restore --nologo 2>&1 | tail -3) | tee -a "$SUM"

echo "=== [2] selftest 单路全量 × 3 素材 ===" | tee -a "$SUM"
for m in real_4k_h264_60m real_4k_hevc_hdr10_60m real_8k_av1_hdr10_100m; do
  timeout 300 "$APP" --selftest "$MEDIA/$m.mp4" > "$LOG/reg_selftest_$m.log" 2>&1
  echo "  selftest $m exit=$?" | tee -a "$SUM"
done

echo "=== [3] sessiontest 会话往返 ===" | tee -a "$SUM"
timeout 300 "$APP" --sessiontest "$MEDIA/real_4k_h264_60m.mp4" "$MEDIA/real_4k_hevc_hdr10_60m.mp4" > "$LOG/reg_sessiontest.log" 2>&1
echo "  sessiontest exit=$?" | tee -a "$SUM"

echo "=== [4] screentest 抓帧导出 ===" | tee -a "$SUM"
timeout 300 "$APP" --screentest "$MEDIA/real_4k_h264_60m.mp4" "C:/PLAN/3FCompare/.3fc_verify_20260917/reg_screentest.png" > "$LOG/reg_screentest.log" 2>&1
echo "  screentest exit=$?" | tee -a "$SUM"

echo "=== 结束 ===" | tee -a "$SUM"
ls -la "$LOG/reg_screentest.png" 2>/dev/null | tee -a "$SUM"
