#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# cover_probe_handmade.sh —— 手工构造 ffmpeg **写不出**、但解码器存在的三种图像，
# 再交给随包内核判能不能呈现。
#
# 为什么要手工构造：cover_probe.sh 的分母受限于"本机 ffmpeg 能否生成该格式"，
#   xpm（文本）、icns（macOS 图标容器）、ora（OpenRaster = zip+png）都只有解码侧，
#   不构造就永远量不到，而它们恰是"清单里到底该不该有它"的真问题。
#
# 构造依据（权威格式定义，非猜测）：
#   xpm  ：libXPM 的 C 头文件形式，"/* XPM */" + static char * 名[] { 宽高 色数  cpp, ... }
#   icns ：Apple Icon 容器 —— 'icns' + 总长(大端) + 每条目(type + 长度 + 数据)，
#          'ic08' 条目装的就是一个 128x128 PNG ⇒ 直接以现成 PNG 为 payload
#   ora  ：OpenRaster = ZIP，含 mimetype + stack.xml（引用 mergedimage.png）+ 该 png
#
# 用法：bash 3fp/tools/cover_probe_handmade.sh
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT=/c/PLAN/3FCompare
S="$ROOT/3fp/build/cover_probe"
PROBE="$ROOT/3fp/build/verify_image_mode.exe"
mkdir -p "$S"
SRC_PNG="$S/sample.png"          # 8x8 纯色 PNG，由 cover_probe.sh 生成
[ -s "$SRC_PNG" ] || { echo "缺少 $SRC_PNG，先跑 cover_probe.sh"; exit 1; }

# ── xpm：4x4 两色 ──
cat > "$S/hand.xpm" <<'EOF'
/* XPM */
static char * probe_xpm[] = {
"4 4 2 1",
" 	c None",
".	c #4080C0",
"..",
"..",
"..",
".."};
EOF

# ── icns：'icns' + 总长 + ('ic08' + 长度 + PNG 数据) ──
# 大端 4 字节长度用 perl 的 pack("N") 生成，比 shell 拼转义可靠
perl -e '
  my $png = shift; my $type = shift;
  open(my $in, "<:raw", $png) or die $!; local $/; my $data = <$in>; close $in;
  my $entry = length($data) + 8;
  my $total = $entry + 8 + 8;
  open(my $out, ">:raw", $ARGV[0]) or die $!;
  print $out "icns", pack("N", $total), $type, pack("N", $entry), $data;
  close $out;
' "$SRC_PNG" ic08 "$S/hand.icns"

# ── ora：zip(mimetype + stack.xml + mergedimage.png) ──
work="$S/ora_src"; rm -rf "$work"; mkdir -p "$work"
printf 'image/openraster' > "$work/mimetype"
cat > "$work/stack.xml" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<image src="mergedimage.png" w="8" h="8" version="0.0.3">
 <stack name="mergedimage" w="8" h="8" transparency="0" component="painted">
  <layer name="bg" src="mergedimage.png" w="8" h="8" transparency="0" offset-x="0" offset-y="0"/>
 </stack>
</image>
EOF
cp "$SRC_PNG" "$work/mergedimage.png"
# bsdtar 按**归档名后缀**选格式，.ora 认不出来 ⇒ 先写成 .zip 再改名
( cd "$work" && rm -f "$S/hand.zip" "$S/hand.ora" \
  && "/c/Windows/System32/tar.exe" -a -cf "$S/hand.zip" mimetype stack.xml mergedimage.png \
  && mv "$S/hand.zip" "$S/hand.ora" ) 2>"$S/ora.tar.err"

# ── 逐个判 ──
printf '%-10s %-9s %s\n' EXT OPEN PRESENT
for e in xpm icns ora; do
  f="$S/hand.$e"
  if [ ! -s "$f" ]; then printf '%-10s %-9s %s\n' "$e" "-" "无产物"; continue; fi
  timeout 60 "$PROBE" "$(cygpath -w "$f")" > "$S/hand_$e.log" 2>&1
  open=$(grep -a -o -m1 "^Open: [0-9-]*" "$S/hand_$e.log" | awk '{print $2}')
  present=$(grep -a -o -m1 "presented=[0-9]*" "$S/hand_$e.log" | cut -d= -f2)
  err=$(grep -a -o -m1 '"message":"[^"]*"' "$S/hand_$e.log" | cut -c12-70)
  printf '%-10s %-9s %-4s %s\n' "$e" "${open:-?}" "${present:-?}" "${err:-}"
done
