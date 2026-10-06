#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# generate_samples.sh —— 生成 3FP「图片模式」验证样张集
#
# 背景：3fp/samples/README.md 的 11 项样张清单是图片模式全部验证的前提。
#       本机**没有** ffmpeg.exe 在 PATH 上，但 `/c/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full`
#       是完整构建（含 ffmpeg/ffprobe/ffplay + 全部依赖 DLL，启用 libjxl / libaom / libwebp /
#       libzimg / liblcms2 / libopenjpeg / libx264 / libx265），足以脚本化生成绝大部分样张。
#
# 用法（Git Bash）：
#   bash 3fp/samples/tools/generate_samples.sh            # 生成全部可生成的
#   bash 3fp/samples/tools/generate_samples.sh --verify   # 只核验已有样张
#
# ⚠ 铁律 3：样张本体（3fp/samples/media/）不入库，生成脚本入库。
# ⚠ Windows 版 ffmpeg 不认 Git Bash 的 /c/... 路径，输出必须用反斜杠 Windows 路径。
# ══════════════════════════════════════════════════════════════════════════════
set -u

ROOT="/c/PLAN/3FCompare"
FFDIR="/c/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full"
FF="$FFDIR/ffmpeg.exe"
FP="$FFDIR/ffprobe.exe"
PY="C:/Users/20210/.workbuddy-ai/binaries/python/versions/3.13.12/python.exe"
INJECT="$ROOT/3fp/samples/tools/inject_metadata.py"
WINJECT='C:\PLAN\3FCompare\3fp\samples\tools\inject_metadata.py'
OUT="$ROOT/3fp/samples/media"
WOUT='C:\PLAN\3FCompare\3fp\samples\media'
TMPD="$ROOT/3fp/build/sampletmp"
WTMP='C:\PLAN\3FCompare\3fp\build\sampletmp'
HDR_VIDEO="$ROOT/testmedia/media/real/real_4k_hevc_hdr10_60m.mp4"
WHDR='C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc_hdr10_60m.mp4'

VERIFY_ONLY=0
[ "${1:-}" = "--verify" ] && VERIFY_ONLY=1

OK=0; FAIL=0; SKIP=0
FAILED_NAMES=()
ERRLOG="$(mktemp)"

run() {                       # run <描述> <命令...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>"$ERRLOG"; then
    echo "  ✓ $desc"; OK=$((OK+1)); return 0
  fi
  echo "  ✗ $desc"; sed 's/^/      /' "$ERRLOG" | tail -6; FAIL=$((FAIL+1))
  FAILED_NAMES+=("$desc"); return 1
}

probe() {                     # probe <相对路径> <ffprobe 参数...>
  local f="$1"; shift
  "$FP" -hide_banner -v error "$@" "$f" 2>&1
}

mkdir -p "$OUT"/{basic,hdr,anim,edge,seq,meta} 2>/dev/null
mkdir -p "$TMPD" 2>/dev/null
rm -rf "$OUT"/_* "$TMPD"/* 2>/dev/null
cd "$OUT" || exit 1

if [ "$VERIFY_ONLY" -eq 0 ]; then
echo "══ 生成样张（ffmpeg: $("$FF" -hide_banner -version 2>/dev/null | head -1)）══"

# ── 1. 基础盘：sRGB PNG（含 alpha）────────────────────────────────────────────
echo "[1] basic/"
run "sRGB PNG 1920x1080 + alpha" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=1920x1080:r=1" \
    -f lavfi -i "color=c=black:s=1920x1080:r=1" \
    -frames:v 1 -filter_complex \
      "[1:v]geq=lum='255*X/W':cb=128:cr=128,format=gray[g];[0:v]format=rgb24[rgb];[rgb][g]alphamerge" \
    -pix_fmt rgba -y "$WOUT\\basic\\01_srgb_alpha_1920x1080.png"

run "sRGB PNG 1920x1080 无 alpha（对照）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=1920x1080:r=1" -frames:v 1 -pix_fmt rgb24 \
    -y "$WOUT\\basic\\01b_srgb_1920x1080.png"

run "JPEG q2 1920x1080" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=1920x1080:r=1" -frames:v 1 -q:v 2 \
    -y "$WOUT\\basic\\01c_srgb_1920x1080.jpg"

# ── 2. Display P3 PNG（广色域 / M3 / Q10）─────────────────────────────────────
# ⚠ ffmpeg 的 png 编码器**没有** cICP/iCCP 选项 ⇒ `-color_primaries smpte432` 被静默丢弃
#   （实测输出与不带该参数逐字节相同）。必须后处理注入 cICP 块。
echo "[2] meta/ (Display P3)"
run "Display P3 PNG (primaries=smpte432, 经 cICP 注入)" \
  bash -c "set -e
    \"$FF\" -hide_banner -loglevel error -f lavfi -i 'testsrc2=s=1920x1080:r=1' -frames:v 1 -pix_fmt rgb24 \
      -y '$WTMP\\p3tmp.png'
    \"$PY\" '$WINJECT' cicp '$WTMP\\p3tmp.png' '$WOUT\\meta\\02_display_p3_1920x1080.png' 12 13 0 1"

# ── 3. EXIF Orientation=6 的 JPEG（Q3 / M2 自动旋转）──────────────────────────
# ⚠ ffmpeg 的 mjpeg 编码器不写 EXIF ⇒ `-metadata:s:v rotate=90` 无效（实测输出字节数不变）。
#   必须后处理注入 APP1。实测注入后 ffmpeg **帧级** side_data 出现
#   `3x3 displaymatrix / rotation=-90` ⇒ Q3 定案：mjpeg 解码器确实导出 DISPLAYMATRIX。
echo "[3] meta/ (EXIF Orientation)"
run "JPEG with EXIF Orientation=6（经 APP1 注入）" \
  bash -c "set -e
    \"$FF\" -hide_banner -loglevel error -f lavfi -i 'testsrc2=s=1920x1080:r=1' -frames:v 1 -q:v 2 \
      -y '$WTMP\\exiftmp.jpg'
    \"$PY\" '$WINJECT' exif '$WTMP\\exiftmp.jpg' '$WOUT\\meta\\03_exif_orientation6.jpg' 6"

# ── 7. 动画图（M2 默认停首帧 + loop=0）────────────────────────────────────────
echo "[7] anim/"
run "动画 WebP (10 帧)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x480:r=10:d=1" -frames:v 10 -loop 0 \
    -c:v libwebp_anim -y "$WOUT\\anim\\07a_anim.webp"

run "动画 GIF (10 帧)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x480:r=10:d=1" -frames:v 10 -loop 0 \
    -y "$WOUT\\anim\\07b_anim.gif"

run "APNG (10 帧，须用 -f apng，image2 会报同名冲突)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x480:r=10:d=1" -frames:v 10 -f apng -plays 0 \
    -y "$WOUT\\anim\\07c_anim.png"

# ⚠ 动画 AVIF 是**有真实时间轴**的序列（ftyp major_brand=avis，即 AVIF Sequence），
#   属 B 类，与 gif/apng/webp_anim 的"无时间轴循环图"（A 类）不同：
#   ffprobe 报 2 条流 —— 流0 是首帧静图（primary item，1 帧），流1 是 10 帧动画轨
#   （nb_frames=10 / duration=1.0）。本机 avif muxer 只此一条可生成动画的路径。
run "动画 AVIF (10 帧，avis 序列 / B 类)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x480:r=10:d=1" -frames:v 10 -loop 0 \
    -c:v libaom-av1 -crf 30 -cpu-used 8 -f avif \
    -y "$WOUT\\anim\\07d_anim.avif"

# ── 8. 多页 TIFF / ICO ────────────────────────────────────────────────────────
# ⚠ ffmpeg **没有 tiff muxer**（`-f tiff` 报 not known）；`-f image2 -update 1` 只落最后一页。
#   ⇒ 多页 TIFF 由 inject_metadata.py 按 baseline 规范直接合成。
echo "[8] edge/"
run "多页 TIFF (3 页，Python 合成)" \
  bash -c "set -e
    \"$FF\" -hide_banner -loglevel error -f lavfi -i 'testsrc2=s=640x480:r=1' -frames:v 3 \
      -y '$WTMP\\tif%02d.png'
    \"$PY\" '$WINJECT' tiff '$WOUT\\edge\\08a_multipage.tiff' \
      '$WTMP\\tif01.png' '$WTMP\\tif02.png' '$WTMP\\tif03.png'"

run "ICO 256x256（单尺寸；多尺寸 ffmpeg ico muxer 不支持）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=256x256:r=1" -frames:v 1 \
    -y "$WOUT\\edge\\08b_single_256.ico"

# ── 9. >16384 边长 PNG（K4 大图预检）─────────────────────────────────────────
echo "[9] edge/ (>16384)"
run "PNG 17000x64（超 16384 ⇒ K4 应拒绝）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "color=c=blue:s=17000x64:r=1" -frames:v 1 \
    -y "$WOUT\\edge\\09_huge_17000x64.png"

run "PNG 16384x64（边界内 ⇒ 应通过）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "color=c=green:s=16384x64:r=1" -frames:v 1 \
    -y "$WOUT\\edge\\09b_boundary_16384x64.png"

# ── 10. 序列图 img%03d.png ×10（K3：序列应回归定时视频）───────────────────────
echo "[10] seq/"
run "序列图 img001..010.png (640x480, 10fps)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x480:r=10:d=1" -frames:v 10 \
    -y "$WOUT\\seq\\img%03d.png"

# ── 11. 16bit PNG / EXR float（K7：RGB/float 色彩与精度）─────────────────────
echo "[11] edge/ (高位深)"
run "16bit PNG (rgb48be)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=1024x1024:r=1" -frames:v 1 -pix_fmt rgb48be \
    -y "$WOUT\\edge\\11a_16bit_rgb48.png"

run "EXR float (gbrpf32le)" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=512x512:r=1" -frames:v 1 -pix_fmt gbrpf32le \
    -y "$WOUT\\edge\\11b_float.exr"

# ── 4. PQ AVIF（HDR 静态图核心样张 / Q5）──────────────────────────────────────
echo "[4] hdr/ (PQ AVIF)"
if [ -f "$HDR_VIDEO" ]; then
  run "PQ AVIF 10bit（从 HDR10 视频抽帧）" \
    "$FF" -hide_banner -loglevel error -ss 2 -i "$WHDR" -frames:v 1 \
      -vf "scale=1920:1080" -pix_fmt yuv420p10le -c:v libaom-av1 -still-picture 1 -crf 22 \
      -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv \
      -y "$WOUT\\hdr\\04_pq_10bit.avif"
else
  echo "  ! 缺 HDR 母版 $HDR_VIDEO ⇒ 跳过 PQ AVIF"; SKIP=$((SKIP+1))
fi

# ── 6. HLG JXL（Q5）──────────────────────────────────────────────────────────
echo "[6] hdr/ (HLG JXL)"
if [ -f "$HDR_VIDEO" ]; then
  run "HLG JXL（PQ→HLG 经 zscale）" \
    "$FF" -hide_banner -loglevel error -ss 2 -i "$WHDR" -frames:v 1 \
      -vf "scale=1920:1080,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt2020:t=arib-std-b67:m=bt2020nc,format=rgb48le" \
      -c:v libjxl -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc \
      -y "$WOUT\\hdr\\06_hlg.jxl"
else
  echo "  ! 缺 HDR 母版 ⇒ 跳过 HLG JXL"; SKIP=$((SKIP+1))
fi

echo ""
echo "── 汇总：成功 $OK / 失败 $FAIL / 跳过 $SKIP ──"
[ "$FAIL" -gt 0 ] && printf '  失败项：%s\n' "${FAILED_NAMES[@]}"
fi   # VERIFY_ONLY

# ── 核验：逐项 ffprobe ───────────────────────────────────────────────────────
echo ""
echo "══ 核验（ffprobe）══"
shopt -s nullglob
for f in basic/*.* meta/*.* anim/*.* edge/*.* hdr/*.* seq/img001.png; do
  [ -f "$f" ] || continue
  sz=$(stat -c %s "$f")
  info=$(probe "$f" -select_streams v:0 \
          -show_entries stream=codec_name,pix_fmt,width,height,nb_frames,color_primaries,color_transfer,color_space \
          -of default=nw=1 2>/dev/null | tr '\n' ' ')
  printf "  %-38s %9s B  %s\n" "$f" "$sz" "$info"
done
echo ""
echo "══ 待人工准备 / 本机 ffmpeg 不可达（已在样张清单中登记）══"
echo "  · hdr/05_pq_heic.heic  —— 本机 ffmpeg 无 heif/heic muxer（只有 avif），需 iPhone 实拍样张"
echo "  · 真实 gain map HEIC / Ultra HDR JPEG —— 需真实素材（Q8）"
echo "  · anim 动画 JXL       —— ffmpeg 无 jxl muxer；libjxl_anim + image2 -update 1 产物 libjxl 解不开"
echo "  · anim 动画 HEIC      —— 无 heif muxer；mov 容器写 .heic 得到的是 major_brand=qt 的假 HEIC，非真素材"
echo "  · edge 多尺寸 ICO     —— ffmpeg ico muxer 只接受同尺寸多帧，多尺寸需外部拼装（已出单尺寸）"
