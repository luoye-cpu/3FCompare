#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# cover_probe.sh —— 实测「随包内核能不能解码」某个扩展名对应的图像文件
#
# 为什么自己生成再自己判：文件关联要列的是**本播放器能打开**的格式，
#   分母不能靠猜、也不能靠外部 ffmpeg CLI（CLI 版本与随包 avformat-63.dll 不同批）。
#   ⇒ 用本机 ffmpeg 当**生成器**（能写出该格式即可），用 build/verify_image_mode.exe
#     （加载随包 FFF.Native.dll + avformat-63.dll）当**判据**。
#
# 判据：Open: 0（FFFResult::Success）且 snapshot 的 presented>=1。
#   只看 Open 不够：能探测到容器不代表能解出并呈现像素。
#
# 用法：bash 3fp/tools/cover_probe.sh [扩展名 ...]     不带参数则跑 CANDIDATES 全集
# 输出：3fp/build/cover_probe/<ext>.log 与汇总表
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT=/c/PLAN/3FCompare
OUT="$ROOT/3fp/build/cover_probe"
PROBE="$ROOT/3fp/build/verify_image_mode.exe"
FF="C:/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full/ffmpeg.exe"
mkdir -p "$OUT"

# 候选：当前文件关联清单里**没有**的、FFmpeg 侧存在图像解码器的扩展名
CANDIDATES=(apng jp2 j2k jpf jpx j2c jpc pgf psd icns xbm dpx cin fits iff ilbm lbm \
            bw rgb int pal sgi ora cut mic pictor vif cdg avs yop bmv vcraw genicon \
            pdp sga rst txg rpl fmvc tmv pixjs emf wmfg wgml svg msl clip dcf stl jpgv)

# 反向清单：已经在 文件关联管理器.vb 里声明的图片扩展名，逐条验"能不能真打开"。
# 目的不是挑错，而是不让设置页承诺一个点开只有黑屏的格式。
PRESENT=(png jpg jpeg jpe jfif bmp dib gif webp avif ico tiff tif heic \
         avifs heif jxl cur exr hdr tga pcx dds qoi \
         pnm pbm pgm ppm pam xpm xwd sun ras sgi pcd)

case "${1:-}" in
  --current) shift; exts=("${PRESENT[@]}") ;;
  # 这些扩展名 ffmpeg 写不出来，但字节流与别的格式同族 ⇒ 拿已验通的样本换扩展名/剥头，
  # 判的是"随包 avformat 能否按这个扩展名认出来并呈现"，正是双击要走的那一步。
  --alias)
    shift
    S="$ROOT/3fp/build/cover_probe"
    cp "$S/sample.jpg" "$S/sample.jpe"; cp "$S/sample.jpg" "$S/sample.jfif"
    cp "$S/sample.ico" "$S/sample.cur"; cp "$S/sample.ppm" "$S/sample.pnm"
    tail -c +15 "$S/sample.bmp" > "$S/sample.dib"        # BMP 去掉 14 字节文件头即 DIB
    for e in jpe jfif cur pnm dib heic; do
      src="$S/sample.$e"
      [ "$e" = heic ] && src="$ROOT/3fp/samples/media/basic/01d_heic_1280x720.heic"
      timeout 60 "$PROBE" "$(cygpath -w "$src")" > "$OUT/$e.log" 2>&1
      open=$(grep -a -o -m1 "^Open: [0-9-]*" "$OUT/$e.log" | awk '{print $2}')
      present=$(grep -a -o -m1 "presented=[0-9]*" "$OUT/$e.log" | cut -d= -f2)
      printf '%-10s %-10s %-9s %s\n' "$e" "alias" "${open:-?}" "${present:-?}"
    done
    exit 0 ;;
  *)
    exts=("$@")
    [ ${#exts[@]} -eq 0 ] && exts=("${CANDIDATES[@]}")
    ;;
esac

printf '%-10s %-10s %-9s %s\n' EXT GEN OPEN PRESENT
for e in "${exts[@]}"; do
  f="$OUT/sample.$e"
  # 8x8 纯色一帧；显式 -f 让部分需要点名 muxer 的格式也能写出
  "$FF" -hide_banner -loglevel error -y -f lavfi -i "color=c=0x4080c0:s=8x8:r=1" \
        -frames:v 1 -update 1 "$f" > "$OUT/$e.gen.log" 2>&1
  if [ ! -s "$f" ]; then
    printf '%-10s %-10s %-9s %s\n' "$e" "NO-GEN" "-" "-"
    continue
  fi
  timeout 60 "$PROBE" "C:\\PLAN\\3FCompare\\3fp\\build\\cover_probe\\sample.$e" \
      > "$OUT/$e.log" 2>&1
  open=$(grep -a -o -m1 "^Open: [0-9-]*" "$OUT/$e.log" | awk '{print $2}')
  present=$(grep -a -o -m1 "presented=[0-9]*" "$OUT/$e.log" | cut -d= -f2)
  printf '%-10s %-10s %-9s %s\n' "$e" "ok" "${open:-?}" "${present:-?}"
done
