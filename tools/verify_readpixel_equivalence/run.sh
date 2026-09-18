#!/usr/bin/env bash
#
# 校验 FFF3FP_ReadVideoPixelRegion（批量回读）与 FFF3FP_ReadVideoPixel（单点回读）
# 在同一坐标上是否一致，并**同时覆盖 10 位与 16 位两条交换链路径**。
#
# 为什么需要这个工具：
#   kernel 的 ReadPixelRegion 曾把 R16G16B16A16_FLOAT 当成 float（16 字节/像素）读取，
#   而它每通道其实是 HALF（2 字节，共 8 字节/像素）——数值全错且越界读取一倍。
#   默认路径下 HDR 素材会回退 10 位，**16F 分支永远跑不到**，所以必须靠
#   FFF_TEST_HDR=1 强制 16 位交换链才能覆盖。详见 docs/19。
#
# 用法：
#   run.sh <FFF.Native.dll 路径> [FFmpeg DLL 目录]
#       FFmpeg 目录默认 <repo>/runtime
#   环境变量：
#       RPE_MEDIA   被测媒体（默认 real_4k_hevc_hdr10_60m.mp4；必须是 HDR 源，
#                   否则内核不会建 HDR 交换链，16 位用例会被跳过）
#       WORK        工作目录（默认 <repo>/.verify_rpe）
#
# 退出码：0 = 两条路径都一致；1 = 出现不一致；2 = 环境/参数错误。
set -uo pipefail

KERNEL_DLL="${1:-}"
FFDIR="${2:-}"

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
WORK="${WORK:-$ROOT/.verify_rpe}"
: "${FFDIR:=$ROOT/runtime}"
: "${RPE_MEDIA:=$ROOT/testmedia/media/real/real_4k_hevc_hdr10_60m.mp4}"
export RPE_MEDIA

VC="C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231"
SDK="C:/Program Files (x86)/Windows Kits/10"
SDKV="10.0.28000.0"

if [ -z "$KERNEL_DLL" ]; then
  echo "usage: run.sh <FFF.Native.dll> [ffmpeg-dir]" >&2
  exit 2
fi
KERNEL_DLL="$(cd "$(dirname "$KERNEL_DLL")" && pwd)/$(basename "$KERNEL_DLL")"
KDIR="$(dirname "$KERNEL_DLL")"
if [ ! -f "$KERNEL_DLL" ]; then echo "FATAL: kernel not found: $KERNEL_DLL" >&2; exit 2; fi
if [ ! -f "$RPE_MEDIA" ]; then echo "FATAL: media not found: $RPE_MEDIA" >&2; exit 2; fi
# 内核（FFmpeg）只认 Windows 路径，MSYS 的 /c/... 会被拒
# （"The path must identify an existing regular local file"）
RPE_MEDIA="$(cygpath -w "$RPE_MEDIA")"
export RPE_MEDIA

BIN="$WORK/bin"
mkdir -p "$BIN"

# --- 1. FFmpeg 依赖必须与 exe 同目录（只加 PATH 不行：MSYS 的正斜杠路径
#        Windows LoadLibrary 解析不了，延迟加载会在 open 阶段失败）----------
for d in avcodec-63 avformat-63 avutil-61 swresample-7 swscale-10 avfilter-12; do
  if [ -f "$FFDIR/$d.dll" ]; then cp -f "$FFDIR/$d.dll" "$BIN/"; else
    echo "WARN: missing $d.dll in $FFDIR" >&2
  fi
done
[ -f "$FFDIR/ass-9.dll" ] && cp -f "$FFDIR/ass-9.dll" "$BIN/"
cp -f "$KERNEL_DLL" "$BIN/FFF.Native.dll"

# --- 2. 编译测试程序（需要内核的导入库；构建产物里 .lib 与 .dll 同目录）-----
EXE="$BIN/pixel_equiv.exe"
if [ ! -f "$EXE" ] || [ "$HERE/test_pixel_equiv.cpp" -nt "$EXE" ]; then
  LIB="$KDIR/FFF.Native.lib"
  [ -f "$LIB" ] || LIB="$(ls "$WORK"/*/FFF.Native.lib 2>/dev/null | head -1)"
  if [ ! -f "$LIB" ]; then
    echo "FATAL: FFF.Native.lib not found next to the kernel (needed to link)" >&2
    exit 2
  fi
  export PATH="$VC/bin/Hostx64/x64:$SDK/bin/$SDKV/x64:$PATH"
  export INCLUDE="$VC/include;$SDK/Include/$SDKV/ucrt;$SDK/Include/$SDKV/um;$SDK/Include/$SDKV/shared"
  # cl.exe 把以 '/' 开头的 MSYS 路径（/c/...）当成命令行选项 ⇒ 必须转成 Windows 形式
  SRC_W="$(cygpath -w "$HERE/test_pixel_equiv.cpp")"
  OBJ_W="$(cygpath -w "$WORK/pixel_equiv.obj")"
  EXE_W="$(cygpath -w "$EXE")"
  LIB_W="$(cygpath -w "$LIB")"
  INC_W="$(cygpath -w "$ROOT/third_party/fff_project/FFF.Native")"
  UCRT_W="$(cygpath -w "$SDK/Lib/$SDKV/ucrt/x64")"
  UM_W="$(cygpath -w "$SDK/Lib/$SDKV/um/x64")"
  VCLIB_W="$(cygpath -w "$VC/lib/x64")"
  ( cd "$WORK" && \
    "$VC/bin/Hostx64/x64/cl.exe" /nologo /std:c++20 /EHsc /MD /O2 /I "$INC_W" \
      /Fo:"$OBJ_W" /c "$SRC_W" && \
    "$VC/bin/Hostx64/x64/link.exe" /nologo /OUT:"$EXE_W" \
      /LIBPATH:"$VCLIB_W" /LIBPATH:"$UCRT_W" /LIBPATH:"$UM_W" \
      "$OBJ_W" "$LIB_W" kernel32.lib user32.lib gdi32.lib ) || {
    echo "FATAL: failed to build the harness" >&2; exit 2; }
fi

# --- 3. 跑两条路径 ----------------------------------------------------------
run_one() {   # $1=标签  $2=是否强制 HDR
  local tag="$1" hdr="$2" log="$WORK/$1.log" rc=0
  if [ "$hdr" = "1" ]; then export FFF_TEST_HDR=1; else unset FFF_TEST_HDR; fi
  ( cd "$BIN" && ./pixel_equiv.exe ) > "$log" 2>&1 || rc=$?
  local rti verdict depth
  rti="$(grep -m1 '^rti result' "$log")"
  depth="$(printf '%s' "$rti" | sed -n 's/.*outputBitDepth=\([0-9]*\).*/\1/p')"
  verdict="$(grep -m1 '^VERDICT' "$log")"
  printf '  %-10s exit=%-3s outputBitDepth=%-3s %s\n' "$tag" "$rc" "${depth:-?}" "$verdict"
  case "$verdict" in
    *CONSISTENT*) [ "$rc" -eq 0 ] && return 0 || return 1 ;;
    *) return 1 ;;
  esac
}

echo "=== ReadPixelRegion vs ReadPixel equivalence ==="
echo "  kernel : $KERNEL_DLL"
echo "  media  : $RPE_MEDIA"

fail=0
echo "-- 10-bit path (default) --"
run_one sdr 0 || fail=1
echo "-- 16-bit path (FFF_TEST_HDR=1) --"
run_one hdr 1 || fail=1

echo
if [ "$fail" -eq 0 ]; then
  echo "RESULT: PASS (both swap-chain paths agree with the single-point API)"
else
  echo "RESULT: FAIL - see $WORK/sdr.log and $WORK/hdr.log"
fi
exit "$fail"
