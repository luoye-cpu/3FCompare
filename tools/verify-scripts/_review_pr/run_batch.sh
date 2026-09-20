#!/usr/bin/env bash
# 4 路崩溃复测批量脚本（位置平衡设计）
#
# 用法: bash .review_pr/rtss_ab/run_batch.sh <tag> [rounds=10]
#   tag    : 环境标记，如 with_rtss / no_rtss（结果落 runlogs/<tag>_<ts>.tsv）
#   rounds : 轮数，每轮跑 2 个进程（pos1 + pos2），默认 10 轮 = 20 次
#
# 为什么每轮跑 2 个进程：
#   既有数据显示崩溃率由「同轮第 2 个进程 + 会话累计时长」主导
#   （pos1 6/17=35% vs pos2 14/17=82%，Fisher p=0.0134）。
#   只报总崩溃率会把这个已知混杂因素混进去，必须按位置分别统计。
#
# 判定标准见 docs/34。

set -u

TAG="${1:-noname}"
ROUNDS="${2:-10}"

EXE="C:/PLAN/3FCompare/src/3FCompare/bin/Debug/net11.0-windows/3FCompare.exe"
VIDEO="C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4"
ROUTES=4
DURATION=30

GAP_IN_PAIR=3     # 同一轮内两次运行的间隔（秒）——刻意很短，保留"紧接着再起一个进程"的条件
GAP_BETWEEN=20    # 轮与轮之间（秒）

OUTDIR="$(cd "$(dirname "$0")" && pwd)/runlogs"
mkdir -p "$OUTDIR"
TS="$(date +%Y%m%d_%H%M%S)"
TSV="$OUTDIR/${TAG}_${TS}.tsv"

echo -e "tag\tround\tpos\ttime\tpid\texit\twall_s\tverdict" > "$TSV"

if [ ! -x "$EXE" ] && [ ! -f "$EXE" ]; then
  echo "找不到可执行文件: $EXE" >&2; exit 2
fi
if [ ! -f "$VIDEO" ]; then
  echo "找不到素材: $VIDEO" >&2; exit 2
fi

crash=0; ok=0; drift=0; other=0

classify() {
  # 崩溃 = 进程异常终止；exit=1 是漂移断言失败，不是崩溃
  case "$1" in
    0) echo "ok";;
    1) echo "drift";;          # 同步达标失败 —— 不计入崩溃
    139|132|134|135|136|139) echo "crash";;   # SIGSEGV/SIGILL/SIGABRT/SIGBUS/SIGFPE
    3221225477|3221225478|3221226525|3221226028) echo "crash";;  # 0xC0000005 等 NTSTATUS
    124) echo "timeout";;      # 卡死
    *) echo "other";;
  esac
}

for ((r=1; r<=ROUNDS; r++)); do
  for pos in 1 2; do
    START=$(date +%s)
    OUT=$(timeout 180 "$EXE" --multitest "$VIDEO" "$ROUTES" "$DURATION" 2>&1)
    RC=$?
    END=$(date +%s)
    WALL=$((END-START))
    V=$(classify "$RC")
    case "$V" in
      crash) crash=$((crash+1));;
      ok)    ok=$((ok+1));;
      drift) drift=$((drift+1));;
      *)     other=$((other+1));;
    esac
    echo -e "${TAG}\t${r}\t${pos}\t$(date +%H:%M:%S)\t-\t${RC}\t${WALL}\t${V}" >> "$TSV"
    echo "[${TAG}] r${r} pos${pos} exit=${RC} wall=${WALL}s -> ${V}"

    # 蓝屏看门：如果这一跑异常快就崩了，记录但仍继续（蓝屏由外部检查）
    if [ "$pos" = "1" ]; then sleep "$GAP_IN_PAIR"; fi
  done
  echo "--- 累计: crash=${crash} ok=${ok} drift=${drift} other=${other} ---"
  sleep "$GAP_BETWEEN"
done

echo
echo "结果: $TSV"
echo "汇总: crash=${crash} ok=${ok} drift=${drift}(非崩溃) other=${other} 总=$((ROUNDS*2))"
echo "提示: 用同级 analyze.py 做 pos1/pos2 分组与 Fisher 检验"
