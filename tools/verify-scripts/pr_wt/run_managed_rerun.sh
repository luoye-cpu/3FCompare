#!/bin/bash
# 托管播放器端到端回归：从完整的 bin/Release 运行（x64 bin 缺 FFmpeg DLL）
set -u
BIN='C:\PLAN\3FCompare\.review_pr\player_env\fff\FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0'
OUT='C:\PLAN\3FCompare\.review_pr\pr_wt\final_tests'
MEDIA='C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4'

for i in 1 2; do
  ( cd "$BIN" && ./FFF.Player.Tests.exe --startup-regression "$MEDIA" ) \
    > "$OUT/managed_startup_run$i.log" 2>&1
  rc=$?
  echo "--- run$i EXIT=$rc ---"
  grep -E "^STARTUP|测试失败" "$OUT/managed_startup_run$i.log" | sed 's/^/    /'
done
