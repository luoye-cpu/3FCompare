#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# verify_animated.sh —— 动图（animated image）专项验证
#
# 判据（G4「动画图默认停首帧，可手动播放」）：
#   ① `[open]` 时 **presented >= 1 且 swapPresents >= 1** —— 打开后**不按播放**就已显示首帧。
#      ⚠ 修复前这里是 0/0（黑屏），因为内核给动图 arm 了 playbackPreroll_，
#        而 preroll 只在 Playing 时完成 ⇒ PumpVideoPresentation 一直被挡。
#   ② `staticImage=false` 且 `ANIMATED=1` —— 动图**不进**图片模式（它要能播放）。
#   ③ 按 Play 后帧号继续增长 —— 「可手动播放」。
#
# 用法：bash 3fp/tools/verify_animated.sh
# 输出：3fp/build/anim_verify.txt
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT=/c/PLAN/3FCompare
PROBE="$ROOT/3fp/build/verify_image_mode.exe"
SAMPLES='C:\PLAN\3FCompare\3fp\samples\media\anim'
OUT="$ROOT/3fp/build/anim_verify.txt"
: > "$OUT"

shopt -s nullglob
FILES=("$ROOT/3fp/samples/media/anim/"*)
if [ ${#FILES[@]} -eq 0 ]; then echo "anim 目录下没有样张"; exit 1; fi

PASS=0; FAIL=0
for path in "${FILES[@]}"; do
  name=$(basename "$path")
  raw=$(cd "$ROOT/3fp/build" && timeout 90 ./verify_image_mode.exe "$SAMPLES"'\'"$name" 2>&1)

  # ⚠ 探针会打两行 `[open]`：先 `[open] state=...`（无 presented），再 `[open] <尺寸> ... presented=`
  #   ⇒ 必须匹配**含 presented=** 的那行，否则取到 state 行会得到 presented=?
  open_line=$(printf '%s' "$raw" | grep -m1 'presented=' | grep '^  \[open\]' || true)
  presented=$(printf '%s' "$open_line" | grep -oE 'presented=[0-9]+' | cut -d= -f2)
  swaps=$(printf '%s' "$open_line" | grep -oE 'swapPresents=[0-9]+' | cut -d= -f2)
  static=$(printf '%s' "$raw" | grep -m1 '"staticImage"' | grep -oE 'true|false' || echo '?')
  flags=$(printf '%s' "$raw" | grep -m1 '^  STATIC=' || echo '')
  after_play=$(printf '%s' "$raw" | grep -m1 '^  \[after-play\] .*presented=' || true)
  play_presented=$(printf '%s' "$after_play" | grep -oE 'presented=[0-9]+' | cut -d= -f2)
  err=$(printf '%s' "$raw" | grep -m1 'event=Error' || true)

  ok='PASS'
  [ -z "${presented:-}" ] && ok='FAIL(无 open 行)'
  [ -n "${presented:-}" ] && [ "$presented" -lt 1 ] && ok='FAIL(首帧未呈现)'
  [ "$static" = 'true' ] && ok='FAIL(被误判为静态图)'
  [ -n "$err" ] && ok="FAIL($err)"

  if [ "$ok" = 'PASS' ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
  {
    printf '%-8s %s\n' "$ok" "$name"
    printf '         [open] presented=%s swapPresents=%s   staticImage=%s   播放后 presented=%s\n' \
      "${presented:-?}" "${swaps:-?}" "$static" "${play_presented:-?}"
    [ -n "$flags" ] && printf '         %s\n' "$(printf '%s' "$flags" | sed 's/^ *//')"
    [ -n "$err" ] && printf '         %s\n' "$err"
  } >> "$OUT"
done

echo "" >> "$OUT"
echo "通过 $PASS / 失败 $FAIL（共 $((PASS+FAIL))）" >> "$OUT"
cat "$OUT"

# 判据必须外泄给调用者。此前脚本以 `cat` 的退出码收尾 ⇒ 恒 0，逐条 FAIL 不会让任何
# 上层门禁变红（"全绿 + 功能坏"）。一条都没跑到同样算红，不许报成"全过"。
if [ "$((PASS+FAIL))" -eq 0 ]; then
  echo "结果：判红（一条样张都没跑到）"
  exit 1
fi
if [ "$FAIL" -ne 0 ]; then
  echo "结果：判红（$FAIL 条失败 / 共 $((PASS+FAIL)) 条）"
  exit 1
fi
echo "结果：判绿（$PASS 条全过）"
exit 0
