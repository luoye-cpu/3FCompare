#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# run_pacing_sweep.sh —— 呈现上限层全臂扫描（2026-10-16，随上限层语义改版）
#
# 读数口径（改版后）：
#   · frameRateBoundHz = 每个媒体一个常数（min(声明刷新率, open 时算出的媒体帧率上界)），
#     所以"内容快慢段"不看它，看探针差分出的一条条**实测呈现速率**（0.5s 窗 / 0.1s 步）。
#   · gridLocked = 上限在岗；lastSlotError100ns = 对上限节奏的偏差（负=早提交）；
#     pacingBypass=2 = 上限不可用（无输出窗口或取不到声明刷新率）。
#   · 屏幕停留那一组读数保留，判带预期值来自「每帧内容周期 ÷ 声明面板周期」。
#
# 规矩来自本轮踩过的坑（docs/03 §A9／A12）：
#   · 一支一个进程，跑与跑之间睡 20s。连跑时上一支的窗口销毁与 DWM 合成还没落定，
#     第一支的吞吐实测塌三成，那不是内核的账。
#   · 计时臂只在整机安静时有效：跑前自己确认 cl/link/ffmpeg/MSBuild 都不在动。
#     本轮 17:39 那一趟撕裂臂读 92.9%，事后发现有别的 lane 在跑 4K 抽取，重跑两次都是 100.0%。
#   · FPT_HOLD_TRACE=1 让内核把停留判带的每个样本打一行到 stderr（字段含义见 docs/03），
#     带外的归属只能靠它，聚合百分比分不开"上限没在岗""管线瞬时不到""观察窗跨越内容段边界"。
#
# 用法：bash 3fp/tools/run_pacing_sweep.sh [要跑的臂名，缺省全跑]
#   臂名：60a 60b 24 240 vrr 4k vfr
# 退出码：任一臂 RC 既不是 0 也不是 1（=建窗/建会话失败那种不可判）则整批 1。
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT=/c/PLAN/3FCompare/3fp
cd "$ROOT/build" || exit 1
L="$ROOT/patches/vfr-work-20261016/logs"
export TMP="$ROOT/build/_wintmp" TEMP="$TMP" TMPDIR="$TMP"
mkdir -p "$TMP"

M='C:\PLAN\3FCompare\testmedia\media\real\real_720p_h264_derived.mp4'
K4='C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4'
F24='C:\PLAN\3FCompare\testmedia\media\derived_24fps_fixture_20261015.mp4'
F240='C:\PLAN\3FCompare\testmedia\media\derived_240fps_fixture_20261015.mp4'
VFR='C:\PLAN\3FCompare\3fp\samples\media\vfr\12a_vfr_25then60.ts'
F120='C:\PLAN\3FCompare\3fp\samples\media\vfr\12e_cfr_120.mp4'

BAD=0
arm() {   # arm <tag> <秒> <media> <env=值>...
  local tag="$1" secs="$2" media="$3"; shift 3
  echo "### $tag $(date +%H:%M:%S)"
  env FPT_HOLD_TRACE=1 PCP_POLL_MS=1 "$@" \
    ./present_pacing_probe.exe "$media" "$secs" > "$L/sw_$tag.out" 2> "$L/sw_$tag.err"
  local rc=$?
  echo "RC=$rc"
  grep -o "呈现帧=[0-9]*（[0-9.]* fps）翻帧=.*\|计时判据前置.*\|屏幕停留对账：[^｜]*\|上限读数：.*\|实测呈现速率（.*\|PACING RESULT.*" \
    "$L/sw_$tag.out"
  grep -o "\[红\].*\|\[未判\].*" "$L/sw_$tag.out"
  if [ "$rc" -gt 1 ]; then BAD=$((BAD+1)); fi
  sleep 20
}

WANT="$*"
want() { [ -z "$WANT" ] && return 0; case " $WANT " in *" $1 "*) return 0;; *) return 1;; esac; }

want 60a && arm 60a 8 "$M"  PCP_CONTENT_FPS=60
want 60b && arm 60b 8 "$M"  PCP_CONTENT_FPS=60
want 24  && arm 24  8 "$F24" PCP_CONTENT_FPS=24
want 240 && arm 240 6 "$F240" PCP_CONTENT_FPS=240
want vrr && arm vrr 8 "$M"  PCP_CONTENT_FPS=60 PCP_TEARING=1
want 4k  && arm 4k  8 "$K4" PCP_CONTENT_FPS=60
# VFR 臂现在同时给两条判据当操作数：⑦"上限在窗口内恒定"（只有 VFR 文件能让
# "逐段重算上限"这种旧实现露出跨度）、⑥"实测呈现速率逐段跟上内容率"。
want vfr && arm vfr 4 "$VFR" PCP_VFR_EARLY_HZ=25 PCP_VFR_LATE_HZ=60
# 120fps 那档是"内容率≈面板率"的操作数（r=144/120=1.200，判带 {1,2}），
# 槽位瞄准删掉以后就是靠它证明 1/2 交替没坏。
want 120 && arm 120 8 "$F120" PCP_CONTENT_FPS=120

echo "不可判的臂 $BAD 支"
[ "$BAD" -eq 0 ]
