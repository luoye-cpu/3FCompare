#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# verify_jxr.sh —— 验证某份 FFmpeg 构建的 JXR 解码能力，判断"是否已满血"
#
# 为什么需要：JXR 的阻断点是上游 FFmpeg `libjxr` 封装的像素格式 GUID 映射表
#   （`docs/15` 定案：只覆盖 8bpp Gray）。上游一修好，换 DLL 就该能用。
#   本脚本把「能不能用」压成一个可重复跑的判据，**每次换依赖后跑一遍**。
#
# 三层判据（全绿才算满血）：
#   ① `-decoders` 里有 libjxr          —— 依赖层：解码器是否编入
#   ② 8 种 pix_fmt 自产往返全部 DEC-OK —— 解码层：GUID 映射表是否补全
#   ③ 4 个真实样本全部 OPEN-OK         —— 真实层：Windows HDR Calibration 的 JXR
#
# ⚠⚠ 判据必须看 **stderr**，不能看退出码！
#   `ffmpeg -frames:v 1 -f null -` 在**解码失败**时仍返回 exit 0
#   （报 `jxrlib JPEG XR decode failed` 到 stderr，输出流为空但"未出错退出"）。
#   实测踩坑：只看退出码 ⇒ 8 种全报 DEC-OK 的**假绿**。正确判据 = `-v error` 下 stderr 为空。
#
# 用法：bash 3fp/tools/verify_jxr.sh <ffmpeg.exe 路径>
#   默认（不带参数）用随包的旧 FFmpeg 做对照。
# ══════════════════════════════════════════════════════════════════════════════
set -u

FF="${1:-C:/PLAN/3FCompare/src/3FCompare/bin/Debug/net11.0-windows/ffmpeg.exe}"
JXRDIR="/c/PLAN/3FCompare/3fp/samples/media/jxr"

if [ ! -x "$FF" ] && [ ! -f "$FF" ]; then
  echo "找不到 ffmpeg：$FF"
  exit 2
fi

echo "ffmpeg: $FF"
"$FF" -hide_banner -version 2>&1 | head -1
echo ""

echo "=== ① 解码器是否编入 ==="
if "$FF" -hide_banner -decoders 2>&1 | grep -qi 'libjxr'; then
  "$FF" -hide_banner -decoders 2>&1 | grep -i 'libjxr'
  L1=OK
else
  echo "  (无 libjxr)"
  L1=FAIL
fi
echo ""

echo "=== ② 自产 8 种 pix_fmt 往返 ==="
T=$(mktemp -d)
DECOK=0; DECTOT=0
for pf in gray gray16le rgb24 bgr24 rgba bgra rgb48le rgba64le; do
  "$FF" -hide_banner -v error -f lavfi -i "testsrc=size=64x64:rate=1" -frames:v 1 \
        -pix_fmt "$pf" -c:v libjxr -update 1 "$T/$pf.jxr" -y 2>/dev/null
  if [ -s "$T/$pf.jxr" ]; then
    DECTOT=$((DECTOT + 1))
    # ⚠ 判据 = stderr 为空（不是退出码；see 头部说明）
    ERR=$("$FF" -hide_banner -v error -i "$T/$pf.jxr" -frames:v 1 -f null - 2>&1)
    if [ -z "$ERR" ]; then
      echo "  DEC-OK   $pf"
      DECOK=$((DECOK + 1))
    else
      echo "  DEC-FAIL $pf  | $(echo "$ERR" | head -1)"
    fi
  else
    echo "  ENC-FAIL $pf"
  fi
done
rm -rf "$T"
[ "$DECTOT" -gt 0 ] && [ "$DECOK" = "$DECTOT" ] && L2=OK || L2=FAIL
echo "  解码成功 $DECOK / $DECTOT"
echo ""

echo "=== ③ 真实样本（Windows HDR Calibration） ==="
OPENOK=0; OPENTOT=0
for f in HDColorHDRCalibration fh2_fp16 FH4-HDR DHR-CoJ1002; do
  if [ -f "$JXRDIR/$f.jxr" ]; then
    OPENTOT=$((OPENTOT + 1))
    ERR=$("$FF" -hide_banner -v error -i "$JXRDIR/$f.jxr" -frames:v 1 -f null - 2>&1)
    if [ -z "$ERR" ]; then
      echo "  OPEN-OK   $f"
      OPENOK=$((OPENOK + 1))
    else
      echo "  OPEN-FAIL $f  | $(echo "$ERR" | head -1)"
    fi
  else
    echo "  MISSING   $f（样本不在，跳过）"
  fi
done
[ "$OPENTOT" -gt 0 ] && [ "$OPENOK" = "$OPENTOT" ] && L3=OK || L3=FAIL
echo "  真实样本成功 $OPENOK / $OPENTOT"
echo ""

echo "════════════════════════════════════"
echo "① 依赖层  : $L1"
echo "② 解码层  : $L2"
echo "③ 真实层  : $L3"
if [ "$L1" = OK ] && [ "$L2" = OK ] && [ "$L3" = OK ]; then
  echo "⇒ ✅ 已满血：JXR 可解码。按 docs/16 §6 收尾（补文件关联白名单等）。"
  exit 0
else
  echo "⇒ ❌ 未满血：见 docs/15 / docs/16。3FP 侧已就绪，等上游修 libjxr GUID 表。"
  exit 1
fi
