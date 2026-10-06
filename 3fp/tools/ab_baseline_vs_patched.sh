#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# ab_baseline_vs_patched.sh —— 同一份探针、同批次交替跑「基线内核 vs 改动内核」
#
# 目的（Q1）：给出「上游基线打开图片时的实际行为」这一**实测**证据，
#             而不是靠读代码推断。这是 PR 描述里"为什么需要这个改动"的直接依据。
#
# 目录约定：
#   3fp/build/ab/baseline/   FFF.Native.dll = 基线内核（不含 K1–K4 / GetImageInfo）+ FFmpeg DLL + 探针
#   3fp/build/ab/patched/    FFF.Native.dll = 改动内核（88526b5）+ FFmpeg DLL + 同一份探针
#
# ⚠ 同一份探针能同时跑两版内核，靠的是把 `FFF3FP_GetImageInfo` 改为 **GetProcAddress 动态解析**
#   （见 tools/verify_image_mode.cpp）。若静态导入，基线内核会因"找不到入口点"直接加载失败。
# ⚠ 交替顺序（A,B,A,B,…）而非 A 全跑完再 B —— 避免把环境漂移误读成版本差异。
# ⚠ 结果先写临时文件、结束时原子改名：若上一轮被 kill，残留进程仍持有结果文件的 fd，
#   本轮 truncate 后它会按旧偏移续写 ⇒ 结果出现**交错与空洞**（实测踩过，白读一轮）。
#
# 用法：bash 3fp/tools/ab_baseline_vs_patched.sh
# 输出：3fp/build/ab/ab_result.txt
# ══════════════════════════════════════════════════════════════════════════════
set -u
cd "$(dirname "$0")/../build/ab" || exit 1

S='C:\PLAN\3FCompare\3fp\samples\media'
OUT=ab_result.txt
TMP="$OUT.tmp"
: > "$TMP"

CASES=(
  "PNG-静态图|basic\01b_srgb_1920x1080.png"
  "JPG-静态图|basic\01c_srgb_1920x1080.jpg"
  "AVIF-PQ-静态图|hdr\04_pq_10bit.avif"
  "TIFF-多页|edge\08a_multipage.tiff"
  "ICO|edge\08b_single_256.ico"
  "PNG-超16384|edge\09_huge_17000x64.png"
  "PNG-边界16384|edge\09b_boundary_16384x64.png"
  "WebP-动画|anim\07a_anim.webp"
)

echo "══════ A/B 对照：基线内核 vs 改动内核（同批次交替）══════" >> "$TMP"

for entry in "${CASES[@]}"; do
  label="${entry%%|*}"; rel="${entry##*|}"
  path="$S"'\'"$rel"
  {
    echo ""
    echo "──── 用例：$label（$rel）────"
  } >> "$TMP"
  for side in baseline patched; do
    echo "  ── [$side] ──" >> "$TMP"
    ( cd "$side" && timeout 90 ./verify_baseline.exe "$path" 2>&1 ) |
      grep -E "Open:|OpenCompleted|event=|\[open\] state|frameCount|STATIC=|GetImageInfo|staticImage|PlaybackEnded|Seek\(|after-seek|新产生的事件|未导出" |
      sed 's/^/    /' >> "$TMP"
  done
done

mv -f "$TMP" "$OUT"
echo "完成 ⇒ $(pwd)/$OUT"
