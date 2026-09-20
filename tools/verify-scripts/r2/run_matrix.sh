#!/bin/bash
# 3FCompare 发布前实机测试驱动（只读测试，不做任何 git 操作）
# 注意：路径必须是 Windows 形式（C:/...）。MSYS 的 /c/... 形式程序不认，
#       会静默报"文件不存在"并 exit=2（首轮踩过，非产品缺陷）。
set -u
EXEDIR="C:/PLAN/3FCompare/src/3FCompare/bin/Debug/net11.0-windows"
OUT="C:/PLAN/3FCompare/.review_pr/r2"
MED="C:/PLAN/3FCompare/testmedia/media/real"
H264="$MED/real_4k_h264_60m.mp4"
HEVC10="$MED/real_4k_hevc10_60m.mp4"
HDR10="$MED/real_4k_hevc_hdr10_60m.mp4"
AV1="$MED/real_4k_av1_10bit_60m.mp4"

cd "$EXEDIR" || exit 1
SUM="$OUT/SUMMARY.tsv"
: > "$SUM"
printf "tag\titem\tconfig\trun\texit\tseconds\n" >> "$SUM"

run() { # run <tag> <item> <config> <run> <timeout> <args...>
  local tag="$1"; local item="$2"; local cfg="$3"; local rn="$4"; shift 4
  local tmo="$1"; shift
  local t0 t1 rc
  t0=$(date +%s)
  timeout "$tmo" ./3FCompare.exe "$@" > "$OUT/$tag.txt" 2>&1
  rc=$?
  t1=$(date +%s)
  printf "%s\t%s\t%s\t%s\t%d\t%d\n" "$tag" "$item" "$cfg" "$rn" "$rc" "$((t1-t0))" >> "$SUM"
  echo "[$tag] exit=$rc $((t1-t0))s" >&2
}

# ── 1. comparemodetest 2 路 ×3（4K h264）；r1 已单独完成
for i in 2 3; do run "cm2_h264_r$i" cm2_h264 "4K h264" "$i" 240 --comparemodetest "$H264" 2; done
# ── 2. comparemodetest 3 路 ×3（4K h264）
for i in 1 2 3; do run "cm3_h264_r$i" cm3_h264 "4K h264" "$i" 300 --comparemodetest "$H264" 3; done
# ── 3. comparemodetest 4 路 ×3（4K h264）
for i in 1 2 3; do run "cm4_h264_r$i" cm4_h264 "4K h264" "$i" 300 --comparemodetest "$H264" 4; done
# ── 4. comparemodetest 3 路 ×3（多编码）
for i in 1 2 3; do run "cm3_hevc10_r$i" cm3_hevc10 "4K hevc10" "$i" 300 --comparemodetest "$HEVC10" 3; done
for i in 1 2 3; do run "cm3_hdr10_r$i"  cm3_hdr10  "4K hdr10"  "$i" 300 --comparemodetest "$HDR10" 3; done
for i in 1 2 3; do run "cm3_av1_r$i"    cm3_av1    "4K av1"    "$i" 300 --comparemodetest "$AV1" 3; done
# ── 5. selftest 4K h264 ×3
for i in 1 2 3; do run "st_h264_r$i" st_h264 "4K h264" "$i" 300 --selftest "$H264"; done
# ── 6. magnifybench 2 路 z=2 ×3
for i in 1 2 3; do run "mb_z2_r$i" mb_z2 "4K h264 2路 z=2" "$i" 300 --magnifybench "$H264" 2 2; done
# ── 7. magnifybench 2 路 z=3 ×3
for i in 1 2 3; do run "mb_z3_r$i" mb_z3 "4K h264 2路 z=3" "$i" 300 --magnifybench "$H264" 2 3; done

echo "DONE" >&2
