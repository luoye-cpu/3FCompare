#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# run_content_period.sh —— 无窗口内容周期探针的五臂跑批（2026-10-16）
#
# 为什么单独一个脚本：这五臂是 A12 的验收口，判据不吃合成/焦点，所以可以也应该反复跑；
# 放在 3fp/build/ 里会随构建目录一起被清（build/ 整目录在 .gitignore），故入库到 tools/。
#
# 用法（Git Bash 或 PowerShell 下 bash）：
#   bash 3fp/tools/run_content_period.sh              # 五臂
# 期望（本轮实到的数，写在 docs/03 §A12）：
#   12b.ts / 12b.mp4 → 各两条平台段 [≈2.60s @20.0Hz][≈1.88s @40.0Hz]，PASS 判7/红0/未判0
#   12c.ts / 12c.mp4 → 各一条 [≈4.50s @20.0Hz]，PASS 判4/红0/未判5（那 5 条是 VFR 专属，主动未判）
# 消融臂（证明判据有牙，别当功能跑）见 §A12 末尾。
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/build" || { echo "找不到 $ROOT/build"; exit 1; }
[ -x ./content_period_probe.exe ] || { echo "先 py 3fp/tools/build_probe.py content_period_probe"; exit 1; }

# TMP 必须指进工作区：沙箱按进程拦 %TEMP% 写，那会把这一臂的读数塌成"会话没跑起来"。
mkdir -p _wintmp
export TMPDIR="$PWD/_wintmp"
export TMP="$TMPDIR" TEMP="$TMPDIR"

MEDIA="$(cd "$ROOT/samples/media/vfr" && pwd)"
P="C:\\PLAN\\3FCompare\\3fp\\samples\\media\\vfr"

FAILED=0
run() {   # run <tag> <文件> <早率> <晚率> <整片平均|-> <期望的 frameRateMode>
  local tag="$1" file="$2" e="$3" l="$4" a="${5:--}" want="$6"
  local log="cp2_$tag.log"
  local -a ev=(CPCP_EARLY_HZ="$e" CPCP_LATE_HZ="$l" CPCP_EXPECT_MODE="$want")
  [ "$a" != "-" ] && ev+=(CPCP_AVG_HZ="$a")
  env "${ev[@]}" ./content_period_probe.exe "$P\\$file" 4.5 > "$log" 2>&1
  local rc=$?
  printf -- "-- %-8s RC=%d :: %s :: %s %s\n" "$tag" "$rc" \
    "$(grep -o '平台段 [0-9]* 条：.*' "$log" | head -1)" \
    "$(grep -o '媒体时钟推进=[0-9.]*s' "$log" | head -1)" \
    "$(grep -o '传统口径丢弃=[0-9]*' "$log" | head -1)"
  grep -o '内核 media info.*' "$log" | sed 's/^/   /'
  grep -E '\[红\]' "$log" | sed 's/^/   /'
  grep -o 'CPCP RESULT.*' "$log" | sed 's/^/   /'
  [ "$rc" -ne 0 ] && FAILED=$((FAILED+1))
  return 0
}

# 期望的 frameRateMode 由夹具的**生成参数**决定（20 与 40 两段 ⇒ variable；单一 20 ⇒
# constant），不是从内核读数反推的——否则就是拿被测的那个数给自己出题。
run 12b-ts  12b_vfr_20then40.ts  20 40 30 variable
run 12b-mp4 12b_vfr_20then40.mp4 20 40 30 variable
run 12c-ts  12c_cfr_20.ts        20 20 -  constant
run 12c-mp4 12c_cfr_20.mp4       20 20 -  constant
# 裸流臂**故意不跳 seek**：这一支守的就是"定位失败不该把会话判成结束"那条前置。
# 修之前它会以"零帧＋定位失败"判红；修之后应当出帧、只有一条 20.0Hz 平台段（合成时钟），
# 且 ⑥ 必须读 constant——12d 是恒定裸流，而容器那两个数（20/1 与 40/1）会说 variable，
# 所以这一支同时是"实测优先于容器"的对照件。
run 12d-bare 12d_bare_20.264     20 20 -  constant

echo ""
echo "五臂合计失败 $FAILED 支"
[ "$FAILED" -eq 0 ] || exit 1
