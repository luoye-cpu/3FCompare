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

mkdir -p "$OUT"/{basic,hdr,anim,edge,seq,meta,vfr} 2>/dev/null
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

# ── 12. 可变帧率节律夹具（present_pacing_probe 的 VFR 臂要用）──────────────────
# 为什么要有：内核的翻帧上限恒取 min(面板率, 内容率)，而"内容率"这个操作数此前
#   只在 CFR 片上量过（60/24/240 三臂，avg_frame_rate == r_frame_rate，
#   所以那三条臂根本分不出实现用的是哪一个数）。这组建三段不同速率拼成的片子，
#   让 avg / r / 逐帧时长三者两两不等，任何"内容率该取哪个"的改动都能被量出来。
# 同一条内容写三种容器，差异全在容器有没有把节奏告诉 demuxer：
#   mp4 —— 170/170 帧带 duration，avg=85/2(42.5)，r=25/1（**低于真峰 60**，
#          因为把慢段放前面时 FFmpeg 只在流头采样；顺序对调 r 就变 60/1）
#   mkv —— 170/170 帧带 duration，avg=r=85/2
#   ts  —— **0/170 帧带 duration**，且 avg=0/0 无效、r=300/1
#          ⇒ 这一支是整条判据的牙：既拿不到逐帧时长，容器率也全不可用，
#            唯一还能还原 25↔60 变速的信息就是包级 PTS 差（实测间距集合 {0.0167,0.0400}）。
# ⚠ 没做裸流（.264）那一档：实测 Annex-B 裸流连 packet pts 都是空的
#   （-show_entries packet=pts_time 输出 0 行），节奏信息根本不存在，
#   所以不能给它建判据——那种源正确行为就是退回"内容率未知"。
echo "[12] vfr/ (可变帧率)"
VFR_FILTER='[0:v][1:v]concat=n=2:v=1:a=0[v]'
run "VFR 640x360：前 2s@25fps + 后 2s@60fps（真峰 60、均 42.5、r 报 25）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x360:r=25:d=2" \
    -f lavfi -i "testsrc2=s=640x360:r=60:d=2" \
    -filter_complex "$VFR_FILTER" -map "[v]" \
    -fps_mode passthrough -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -y "$WOUT\\vfr\\12a_vfr_25then60.mp4"
for c in mkv ts; do
  run "同上内容 remux 成 .$c（-c copy，passthrough 不改 PTS）" \
    "$FF" -hide_banner -loglevel error -i "$WOUT\\vfr\\12a_vfr_25then60.mp4" \
      -c copy -fps_mode passthrough -y "$WOUT\\vfr\\12a_vfr_25then60.$c"
done

# 自证见文末"核验"段（--verify 也要跑，夹具坏了两种模式都得判红）。

# 同一条内容再出一版**两段都低于 60**的（20 与 40）：无窗口的内容周期探针里
# DisplayCadenceHz() 退到构造默认 60，于是 min(60, 40)=40 才是可判的读数；
# 用 25/60 那对的话晚段会读成 60 == 面板默认值，分不清是内容率还是面板那一半赢。
# 整片平均 = (20+40)/2 = 30，两档都不是 30 ⇒ "不许停在平均上"那条判据同样有操作数。
# ⚠ 用 R1/R2 而不是 `for pair in "20 40"; do set -- $pair`：脚本开头靠 $1 判 --verify，
#   在顶层 set -- 会把它覆盖掉，后面再读位置参数就是假话。
R1=20; R2=40
run "VFR 640x360：前 3s@${R1}fps + 后 3s@${R2}fps（真峰 $R2、均 $(( (R1+R2)/2 ))）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x360:r=$R1:d=3" \
    -f lavfi -i "testsrc2=s=640x360:r=$R2:d=3" \
    -filter_complex "$VFR_FILTER" -map "[v]" \
    -fps_mode passthrough -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -y "$WOUT\\vfr\\12b_vfr_${R1}then${R2}.mp4"
run "同上内容 remux 成 .ts" \
  "$FF" -hide_banner -loglevel error -i "$WOUT\\vfr\\12b_vfr_${R1}then${R2}.mp4" \
    -c copy -fps_mode passthrough -y "$WOUT\\vfr\\12b_vfr_${R1}then${R2}.ts"

# CFR 对照（与 12b 第一段同率，20fps 恒定）：内容周期改成"先读队列里下一帧的 PTS 差"
# 之后，CFR 这条路取数的**来源**变了（原来是 frame->duration，现在是 PTS 差），
# 所以必须有一件恒定帧率夹具来量"读数和以前一样"，而不是靠推理说它一样。
run "CFR 640x360 @20fps 6s（.mp4）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x360:r=20:d=6" \
    -fps_mode passthrough -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -y "$WOUT\\vfr\\12c_cfr_20.mp4"
run "同上内容 remux 成 .ts" \
  "$FF" -hide_banner -loglevel error -i "$WOUT\\vfr\\12c_cfr_20.mp4" \
    -c copy -fps_mode passthrough -y "$WOUT\\vfr\\12c_cfr_20.ts"

# 裸流件（从 12c 那件**恒定** 20fps 出 Annex-B）：它存在的理由有两个——
#   ① 它是"包级 PTS 全空、只剩解容器猜出的统一包时长"那一档的唯一入口（实测：PTS 0/120、时长 120/120），
#      内核只能走合成时钟，正确读数是一条 20.0Hz 平台段而不是两段换档；
#   ② 它不接受定位，所以是"失败的 seek 会不会把会话判成结束"这条判据的现成操作数。
# ⚠ 用 12c（恒定）而不是 12a/12b（可变）来做这一件：恒定裸流在 FFmpeg 那边
#   av_guess=20/1 与 r_frame_rate=40/1 不相等，容器那条比较路会因此把恒定说成可变——
#   于是这件同时是"实测优先于容器"这条修复的对照件（修好后必须读 constant）。
run "裸 Annex-B 20fps 恒定 6s（由 12c 转 annexb，包级 PTS 全空）" \
  "$FF" -hide_banner -loglevel error -i "$WOUT\\vfr\\12c_cfr_20.mp4" \
    -c copy -bsf:v h264_mp4toannexb -f h264 -y "$WOUT\\vfr\\12d_bare_20.264"

# 12e：CFR 120fps 一件，专给"内容率≈面板率"那一档当操作数。
#   为什么是 120：本机面板声明 144，上限=min(144,120)=120 ⇒ 每帧该占 144/120=1.200 个刷新周期，
#   ⌊⌋/⌈⌉ 落在 {1,2} 之间交替。这正是"槽位分配"唯一可能有用的形状（60fps 那档 r=2.4 太宽松，
#   怎么排都进带；24fps 那档 r=6.000 整除，带会塌成一个点）。上限改成 per-媒体 常数以后，
#   这一件用来判"只留上限"有没有把 1/2 交替弄坏。
run "CFR 640x360 @120fps 6s（.mp4，r≈1.2 交替操作数）" \
  "$FF" -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=s=640x360:r=120:d=6" \
    -fps_mode passthrough -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -y "$WOUT\\vfr\\12e_cfr_120.mp4"

echo ""
echo "── 汇总：成功 $OK / 失败 $FAIL / 跳过 $SKIP ──"
[ "$FAIL" -gt 0 ] && printf '  失败项：%s\n' "${FAILED_NAMES[@]}"
fi   # VERIFY_ONLY

# ── 核验：逐项 ffprobe ───────────────────────────────────────────────────────
echo ""
echo "══ 核验（ffprobe）══"

# VFR 夹具自证：这几条不成立，present_pacing_probe ⑥ 与 content_period_probe 就是在空转，
# 所以生成模式和 --verify 模式都必须跑。判据两半：
#   ① "带逐帧时长的帧数" —— 决定内核走的是 frame->duration 那一档还是必须退回 PTS 差；
#   ② 包级 PTS 间距按**声明的两段速率**各开 ±15% 窗口去数，且两窗口之外的数量必须为 0
#      （闭合：有余数就说明还有第三种没被解释的节奏）。
# ⚠ 前两版都在这里栽过：先写成"不同间距恰好 2 种"，被 .mkv 否掉（Matroska 时间戳 1ms
#   粒度，16.667ms 交替舍成 16/17 ⇒ 三种取值）；再写成固定"慢带 30–50ms"，那对 12b 的
#   20fps（整 50ms）刚好落在开区间外。现在按段率算窗口，12a/12b 共用同一份判据。
# ⚠ awk 的 -v 不求算式：`-v t=1.0/25` 会把 t 设成字符串"1.0/25"，数值化后停在 1.0，
#   于是两档全数不到、余量=169 —— 上一版就是这样静默失效的。比率要在 BEGIN 里算。
VFR_BAD=0
vfr_pts() { probe "$1" -select_streams v:0 -show_entries packet=pts_time -of csv=p=0; }
vfr_selfcheck() {   # vfr_selfcheck <相对路径> <期望带 duration 的帧数> <段1率> <段2率>
  local rel="$1" want_dur="$2" r1="$3" r2="$4" tot has n1 n2 other rates
  [ -f "$rel" ] || { echo "  ✗ 缺 $rel"; VFR_BAD=$((VFR_BAD+1)); return 1; }
  tot=$(vfr_pts "$rel" | wc -l)
  has=$(probe "$rel" -select_streams v:0 -show_entries frame=duration_time -of csv=p=0 \
        | grep -cv -E '^N/A$|^$')
  n1=$(vfr_pts "$rel" | awk -v r=$r1 'BEGIN{t=1.0/r} NR>1{d=$1-p; if(d>t*0.85&&d<t*1.15)++n}{p=$1}END{print n+0}')
  n2=$(vfr_pts "$rel" | awk -v r=$r2 'BEGIN{t=1.0/r} NR>1{d=$1-p; if(d>t*0.85&&d<t*1.15)++n}{p=$1}END{print n+0}')
  other=$((tot-1-n1-n2))
  rates=$(probe "$rel" -select_streams v:0 \
          -show_entries stream=avg_frame_rate,r_frame_rate -of csv=p=0)
  printf "  %-24s 包=%s 带时长=%s/%s 间距[1/%s:%d 1/%s:%d 两档外:%d] 率[avg,r]=%s\n" \
    "$rel" "$tot" "$has" "$tot" "$r1" "$n1" "$r2" "$n2" "$other" "$rates"
  if [ "$has" -ne "$want_dur" ] || [ "$n1" -lt 10 ] || [ "$n2" -lt 10 ] || [ "$other" -ne 0 ]; then
    echo "    ✗ 自证失败（要带时长 $want_dur/$tot、两档各 ≥10、两档外=0）"
    VFR_BAD=$((VFR_BAD+1)); return 1
  fi
  return 0
}
echo ""
echo "  ── vfr/ 夹具自证 ──"
vfr_selfcheck "vfr/12a_vfr_25then60.mp4" 170 25 60
vfr_selfcheck "vfr/12a_vfr_25then60.mkv" 170 25 60
vfr_selfcheck "vfr/12a_vfr_25then60.ts"    0 25 60
vfr_selfcheck "vfr/12b_vfr_20then40.mp4" 180 20 40
vfr_selfcheck "vfr/12b_vfr_20then40.ts"     0 20 40
[ "$VFR_BAD" -gt 0 ] && echo "  ⇒ VFR 夹具不可用：$VFR_BAD 条自证失败" \
                     || echo "  ⇒ VFR 夹具五条自证全过"
# CFR 对照件：只判"单一间距 + 两档都有逐帧时长"。
# 值得记一笔的实测反差：同为 TS，**CFR 的 .ts 每包都带 duration，VFR 的 .ts 一个都不带**
# —— 所以"退回容器那一档"这条路只在可变帧率上被踩到，这也正是旧实现从没红过的原因。
cfr_selfcheck() {              # cfr_selfcheck <相对路径> <率> <期望带 duration 的帧数>
  local rel="$1" r="$2" want_dur="$3" tot has one
  [ -f "$rel" ] || { echo "  ✗ 缺 $rel"; VFR_BAD=$((VFR_BAD+1)); return 1; }
  tot=$(vfr_pts "$rel" | wc -l)
  has=$(probe "$rel" -select_streams v:0 -show_entries frame=duration_time -of csv=p=0 \
        | grep -cv -E '^N/A$|^$')
  one=$(vfr_pts "$rel" | awk -v r=$r 'BEGIN{t=1.0/r} NR>1{d=$1-p; if(d>t*0.85&&d<t*1.15)++n}{p=$1}END{print n+0}')
  printf "  %-24s 包=%s 带时长=%s/%s 单一间距(1/%s)=%d/%d\n" "$rel" "$tot" "$has" "$tot" "$r" "$one" "$((tot-1))"
  if [ "$has" -ne "$want_dur" ] || [ "$one" -ne $((tot-1)) ]; then
    echo "    ✗ CFR 对照件自证失败"; VFR_BAD=$((VFR_BAD+1)); return 1
  fi
  return 0
}
cfr_selfcheck "vfr/12c_cfr_20.mp4" 20 120
cfr_selfcheck "vfr/12c_cfr_20.ts"  20 120
# 12e 的自证判"单一间距 1/120 覆盖全部 719 个差分 + 每帧都带 duration"。它不验"1.2 个刷新周期"
# 那件事——那是内核侧判带的操作数，夹具只管源里的节奏是纯净的 120。
cfr_selfcheck "vfr/12e_cfr_120.mp4" 120 720
# 裸流件自证：判的是"这源里到底有哪一种节奏信息"。包数必须齐（120）、包级 PTS 必须**全空**
#   （没有定位基准），而包时长必须**齐全且统一**（解容器按它猜出的率给的）——这两条一起
#   决定了裸流的正确读数是"合成一个恒定时钟"，而不是"什么都不知道"。哪天 FFmpeg 开始给
#   裸流写真时间戳，这条会先变红，届时"⑥/ⓔ 未判"的预期就得重估。
bare_selfcheck() {              # bare_selfcheck <相对路径> <包数>
  local rel="$1" want="$2" tot pts dur
  [ -f "$rel" ] || { echo "  ✗ 缺 $rel"; VFR_BAD=$((VFR_BAD+1)); return 1; }
  tot=$(probe "$rel" -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 | wc -l)
  pts=$(probe "$rel" -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 \
        | grep -cv -E '^N/A$|^$')
  dur=$(probe "$rel" -select_streams v:0 -show_entries packet=duration_time -of csv=p=0 \
        | grep -cv -E '^N/A$|^$')
  printf "  %-24s 包=%s/%s 有PTS=%s 有统一时长=%s/%s\n" "$rel" "$tot" "$want" "$pts" "$dur" "$want"
  # 实测把原本的假设改了一半：裸流的**包级 PTS 确实全空**，但解容器会按它猜出的率
  # 给每个包一份**统一时长**（12d = 120/120 有 duration_time）。所以裸流的正确读数是
  # "按猜出的恒定率合成时钟"，不是"完全没有节奏信息"——判据按实测写。
  if [ "$tot" -ne "$want" ] || [ "$pts" -ne 0 ] || [ "$dur" -ne "$want" ]; then
    echo "    ✗ 裸流件自证失败（要包数齐、PTS 全空、包时长齐全）"; VFR_BAD=$((VFR_BAD+1)); return 1
  fi
  return 0
}
bare_selfcheck "vfr/12d_bare_20.264" 120
[ "$VFR_BAD" -gt 0 ] && echo "  ⇒ 合计 $VFR_BAD 条自证失败（VFR+CFR 对照件+裸流件）" \
                     || echo "  ⇒ 全部九条自证全过（含 CFR 对照件、r≈1.2 那一件与裸流件）"

shopt -s nullglob
for f in basic/*.* meta/*.* anim/*.* edge/*.* hdr/*.* seq/img001.png vfr/*.*; do
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
