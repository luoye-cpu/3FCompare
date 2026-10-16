#!/bin/bash
# 12e（120fps 配 144Hz，r≈1.2）的**成对交错** A/B：
#   新内核 → 旧内核 → 新内核 → 旧内核
# 为什么成对而不追"整机安静"：这台盒子当晚有游戏进程长期占 ~300% CPU，绝对百分比会整体
# 变差，但两条臂跑在同一个负载窗里，**相对差**就是问题本身（删掉槽位分配以后带内有没有变坏）。
# 仪器只有一支：两条臂都用**改版前编好的探针**（结构体布局本轮没动，所以旧探针读新内核合法）。
# 用它的原因是打印格式必须逐字相同才能对着读；判据文本按旧语义印，这里只取原始读数行。
# 每次换 DLL 都当场核 sha256，防止"两条臂其实是同一个二进制"这种假对照。
set -u
ROOT=/c/PLAN/3FCompare/3fp
cd "$ROOT/build" || exit 1
L="$ROOT/patches/vfr-work-20261016/logs"
PRE="$ROOT/patches/vfr-work-20261016/pre-cap-redesign"
export TMP="$ROOT/build/_wintmp" TEMP="$TMP" TMPDIR="$TMP"
MEDIA='C:\PLAN\3FCompare\3fp\samples\media\vfr\12e_cfr_120.mp4'
sha() { py -c "import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest()[:16])" "$1"; }
cp FFF.Native.dll "$L/FFF.Native.NEW.dll"
cp "$PRE/present_pacing_probe.exe" ./pp_old.exe     # 两条臂共用这一支，打印格式才逐字相同
run() {   # run <tag> <dll-source>
  local tag="$1" src="$2"
  cp "$src" FFF.Native.dll
  echo "### $tag $(date +%H:%M:%S) dll_sha=$(sha FFF.Native.dll)"
  PCP_CONTENT_FPS=120 PCP_POLL_MS=1 ./pp_old.exe "$MEDIA" 8 \
    > "$L/ab120_$tag.out" 2> "$L/ab120_$tag.err"
  echo "RC=$?"
  grep -o "呈现帧=.*\|屏幕停留对账：[^|]*\|网格标定[^|]*\|PACING RESULT.*" "$L/ab120_$tag.out"
  sleep 15
}
echo "NEW sha=$(sha $L/FFF.Native.NEW.dll)  OLD sha=$(sha $PRE/FFF.Native.dll)"
run new1 "$L/FFF.Native.NEW.dll"
run old1 "$PRE/FFF.Native.dll"
run new2 "$L/FFF.Native.NEW.dll"
run old2 "$PRE/FFF.Native.dll"
cp "$L/FFF.Native.NEW.dll" FFF.Native.dll
echo "### 还原后 build/FFF.Native.dll sha=$(sha FFF.Native.dll)（必须等于 NEW）"
