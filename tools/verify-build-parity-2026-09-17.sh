#!/bin/bash
# 构建一致性对照实验：官方 MSBuild 产物 vs 手工 cl/link 产物，崩溃率是否不同。
#
# 背景：09-17 的复测用的是"手工复刻 MSBuild 参数"构建的内核 DLL（5111b352），
#      若构建参数与官方有偏差，"崩溃未修复"的结论就站不住。
# 对照物：09-16 12:56 由真正的 MSBuild 构建的 DLL（694a099b），
#      其输入源码（0fe33c4）与当前基线（3ac124a）的 FFF.Native **逐字节一致**。
# 判据：两者崩溃率若无差异 ⇒ 与构建方式无关，结论稳固。
set -u
APP="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe"
BIN="/c/PLAN/3FCompare/src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll"
SMOKE="/c/PLAN/3FCompare/tests/3FCompare.SmokeTests/bin/Release/net11.0/FFF.Native.dll"
OFFICIAL="/c/Users/20210/AppData/Local/Temp/fff_dll_backup/FFF.Native.dll.old-20260916"
MANUAL="/c/PLAN/3FCompare/third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll"
MEDIA="C:/PLAN/3FCompare/testmedia/media/real/real_4k_hevc10_60m.mp4"
LOG="/c/PLAN/3FCompare/.3fc_verify_20260917"
SUM="$LOG/build_parity.txt"
N=${N:-10}

: > "$SUM"
echo "=== 构建一致性对照实验（各 $N 次，2 路 25s）===" | tee -a "$SUM"

run_batch () {
  local label="$1" dll="$2"
  cp -f "$dll" "$BIN"; cp -f "$dll" "$SMOKE"
  echo "[$label] 部署 $(sha256sum "$dll" | cut -c1-16)" | tee -a "$SUM"
  local pass=0 crash=1 other=0 detail=""
  for i in $(seq 1 "$N"); do
    timeout 300 "$APP" --multitest "$MEDIA" 2 25 > "$LOG/bp_${label}_$i.log" 2>&1
    local c=$?
    case "$c" in
      0) pass=$((pass+1));;
      139|3221225477) crash=$((crash+1));;
      *) other=$((other+1));;
    esac
    detail="$detail $c"
  done
  crash=$((crash-1))   # 初始化为 1 的补偿
  echo "[$label] 通过=$pass 崩溃=$crash 其它=$other  退出码序列:$detail" | tee -a "$SUM"
}

run_batch OFFICIAL "$OFFICIAL"
run_batch MANUAL   "$MANUAL"

echo "=== 实验结束，已恢复手工构建产物 ===" | tee -a "$SUM"
sha256sum "$BIN"
