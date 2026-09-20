#!/bin/bash
# 实机测试：干净重建后的内核，跑 3 路真实素材 + 托管侧逻辑回归
set -u
BIN='C:\PLAN\3FCompare\.review_pr\bin_a11'
MEDIA='C:\PLAN\3FCompare\testmedia\media\real'
LOGDIR='C:\PLAN\3FCompare\.review_pr\pr_wt\final_tests'
mkdir -p "$LOGDIR"

run_e2e() {
  local name="$1"; local media="$2"
  ( cd "$BIN" && ./test_pr.exe "$media" ) > "$LOGDIR/$name.log" 2>&1
  local rc=$?
  local npass nfail
  npass=$(grep -c ": PASS" "$LOGDIR/$name.log")
  nfail=$(grep -c ": FAIL" "$LOGDIR/$name.log")
  printf '%-24s EXIT=%-3s PASS=%-3s FAIL=%-3s\n' "$name" "$rc" "$npass" "$nfail"
  return $rc
}

FAIL=0
run_e2e e2e_sdr_4k_h264  "$MEDIA\\real_4k_h264_60m.mp4"      || FAIL=1
run_e2e e2e_sdr_4k_hevc10 "$MEDIA\\real_4k_hevc10_60m.mp4"   || FAIL=1
run_e2e e2e_hdr_4k_hevc10 "$MEDIA\\real_4k_hevc_hdr10_60m.mp4" || FAIL=1

# 托管侧（FFF.Player.Tests）纯逻辑回归：不创建窗口，可在无桌面交互的环境跑
TESTEXE='C:\PLAN\3FCompare\.review_pr\player_env\fff\FFF.Player.Tests\bin\x64\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe'
( cd "$(dirname "$TESTEXE")" && ./FFF.Player.Tests.exe --logic-audit-regression ) \
  > "$LOGDIR/managed_logic.log" 2>&1
MRC=$?
printf '%-24s EXIT=%-3s\n' "managed_logic_audit" "$MRC"
[ "$MRC" -ne 0 ] && FAIL=1

echo "===== 实机测试结束 FAIL=$FAIL ====="
exit $FAIL
