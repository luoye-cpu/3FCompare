#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# verify_all_samples.sh —— 用内核实机验证程序批量跑样张集
#
# 前置：`3fp/build/verify_image_mode.exe` + 同目录 `FFF.Native.dll` + FFmpeg DLL。
#   ⚠ 若 stdout 为空且 exit=127 ⇒ 缺 `FFF.Native.dll`（延迟加载失败），
#     补：cp 3fp/kernel/FFF.Native/x64/Release/FFF.Native.dll 3fp/build/
#
# 用法（Git Bash）：bash 3fp/tools/verify_all_samples.sh
# 输出：`3fp/build/verify_all.txt`（每样张一段，含 GetImageInfo / 状态机 / 位深）
#
# ⚠ 路径陷阱：`"$S\\$f"` 会被多一层转义吞掉 ⇒ 必须写 `"$S"'\'"$f"`（实测见 2026-09-24 记录）。
# ══════════════════════════════════════════════════════════════════════════════
set -u
cd "$(dirname "$0")/../build" || exit 1

S='C:\PLAN\3FCompare\3fp\samples\media'
OUT=verify_all.txt
: > "$OUT"

FILES=(
  "basic\01_srgb_alpha_1920x1080.png"
  "basic\01b_srgb_1920x1080.png"
  "basic\01c_srgb_1920x1080.jpg"
  "meta\02_display_p3_1920x1080.png"
  "meta\03_exif_orientation6.jpg"
  "hdr\04_pq_10bit.avif"
  "hdr\06_hlg.jxl"
  "edge\11a_16bit_rgb48.png"
  "edge\11b_float.exr"
  "edge\09_huge_17000x64.png"
  "edge\09b_boundary_16384x64.png"
  "edge\08a_multipage.tiff"
  "edge\08b_single_256.ico"
  "seq\img001.png"
  "anim\07a_anim.webp"
  "anim\07b_anim.gif"
  "anim\07c_anim.png"
)

for f in "${FILES[@]}"; do
  echo "════════ $f ════════" >> "$OUT"
  timeout 90 ./verify_image_mode.exe "$S"'\'"$f" >> "$OUT" 2>&1
  echo "  [exit=$?]" >> "$OUT"
done

echo "完成：$(grep -c '^════' "$OUT") 项，$(wc -l < "$OUT") 行 ⇒ $(pwd)/$OUT"
