#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# deploy_ffmpeg_for_tests.sh —— 把 FFmpeg 运行时部署到测试输出目录
#
# 为什么需要这个脚本：
#   内核 FFF.Native 在加载时按 **exe 同目录** 查找 FFmpeg DLL。缺 DLL 时不会报错，
#   而是静默降级为"演示模式"（界面看起来在跑，实际没有真实解码）⇒ 极易误判
#   "本机没有 FFmpeg / 环境限制"。
#   而 `bin/` 被 .gitignore 忽略 ⇒ **每次干净重建后都要重新复制一遍**。
#   本脚本把这一步固化，幂等、可重复运行。
#
# 用法（Git Bash）：
#   tools/deploy_ffmpeg_for_tests.sh              # 默认 Debug
#   tools/deploy_ffmpeg_for_tests.sh Release      # 指定配置
#   tools/deploy_ffmpeg_for_tests.sh Debug Release # 多个配置
#   tools/deploy_ffmpeg_for_tests.sh --check      # 只校验，不复制
#
# 退出码：0 = 全部到位；非 0 = 有缺失（明细见输出）
# ══════════════════════════════════════════════════════════════════════════════
set -u

ROOT="/c/PLAN/3FCompare"
APP_NAME="3FCompare"

# FFmpeg 来源（按优先级）：第 1 个更完整（含 exe 与依赖 DLL），第 2 个是裸 DLL 兜底
SRC_CANDIDATES=(
  "/c/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full"
  "$ROOT/runtime"
)

# 必须到位的关键文件（缺任何一个都视为部署失败）
REQUIRED_FILES=(
  "avcodec-63.dll"
  "avformat-63.dll"
  "avutil-61.dll"
  "swscale-10.dll"
  "swresample-7.dll"
  "ffmpeg.exe"
)

# 期望到位（不作为硬失败，仅告警）
EXPECTED_FILES=(
  "avfilter-12.dll"
  "avdevice-63.dll"
  "ffprobe.exe"
  "libc++.dll"
  "libomp.dll"
  "libwinpthread-1.dll"
)

CHECK_ONLY=0
CONFIGS=()

for arg in "$@"; do
  case "$arg" in
    --check|-c) CHECK_ONLY=1 ;;
    -h|--help)
      sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    Debug|Release|*) CONFIGS+=("$arg") ;;
  esac
done

if [ "${#CONFIGS[@]}" -eq 0 ]; then
  CONFIGS=("Debug")
fi

# ── 1. 选源 ──────────────────────────────────────────────────────────────────
SRC=""
for cand in "${SRC_CANDIDATES[@]}"; do
  if [ -d "$cand" ] && [ -f "$cand/avcodec-63.dll" ]; then
    SRC="$cand"
    break
  fi
done

if [ -z "$SRC" ]; then
  echo "✗ 找不到可用的 FFmpeg 源目录。已尝试：" >&2
  for cand in "${SRC_CANDIDATES[@]}"; do
    if [ -d "$cand" ]; then
      echo "    $cand  （存在，但缺少 avcodec-63.dll）" >&2
    else
      echo "    $cand  （不存在）" >&2
    fi
  done
  echo "  ⇒ 请确认上面任一路径下有 FFmpeg 运行时（至少 avcodec-63.dll）。" >&2
  exit 2
fi

echo "══ FFmpeg 部署 ══"
echo "源目录 : $SRC"
if [ "$SRC" = "${SRC_CANDIDATES[0]}" ]; then
  echo "来源   : ffmpeg-full（完整包：DLL + ffmpeg/ffprobe/ffplay.exe + 依赖 DLL）"
else
  echo "来源   : runtime（裸 DLL 兜底包，无 exe）"
fi

# 源里有多少文件可复制
mapfile -t SRC_FILES < <(cd "$SRC" && ls -1 *.dll *.exe 2>/dev/null)
echo "可复制 : ${#SRC_FILES[@]} 个文件"

# ── 2. 逐个配置部署 ──────────────────────────────────────────────────────────
TOTAL_FAIL=0

for CFG in "${CONFIGS[@]}"; do
  echo ""
  echo "── 配置 $CFG ──"

  # 找 TFM 输出目录（net11.0-windows / net10.0-windows 等）
  BINROOT="$ROOT/src/$APP_NAME/bin/$CFG"
  if [ ! -d "$BINROOT" ]; then
    echo "  ✗ 输出目录不存在：$BINROOT"
    echo "    ⇒ 请先构建：dotnet build src/$APP_NAME/$APP_NAME.csproj -c $CFG"
    TOTAL_FAIL=$((TOTAL_FAIL+1))
    continue
  fi

  DEST=""
  for d in "$BINROOT"/net*-windows; do
    [ -d "$d" ] && DEST="$d" && break
  done
  if [ -z "$DEST" ]; then
    echo "  ✗ 在 $BINROOT 下找不到 net*-windows 输出目录"
    echo "    ⇒ 请先构建：dotnet build src/$APP_NAME/$APP_NAME.csproj -c $CFG"
    TOTAL_FAIL=$((TOTAL_FAIL+1))
    continue
  fi

  echo "  目标 : $DEST"

  if [ "$CHECK_ONLY" -eq 1 ]; then
    echo "  （--check：跳过复制）"
  else
    COPIED=0; SKIPPED=0
    for f in "${SRC_FILES[@]}"; do
      if [ -f "$DEST/$f" ] && cmp -s "$SRC/$f" "$DEST/$f"; then
        SKIPPED=$((SKIPPED+1))     # 已一致 ⇒ 幂等跳过
      else
        cp -f "$SRC/$f" "$DEST/$f" || { echo "  ✗ 复制失败：$f"; TOTAL_FAIL=$((TOTAL_FAIL+1)); }
        COPIED=$((COPIED+1))
      fi
    done
    echo "  复制 : 更新 $COPIED 个，已一致跳过 $SKIPPED 个"
  fi

  # ── 3. 校验关键文件 ────────────────────────────────────────────────────────
  MISSING=()
  for f in "${REQUIRED_FILES[@]}"; do
    [ -f "$DEST/$f" ] || MISSING+=("$f")
  done

  if [ "${#MISSING[@]}" -gt 0 ]; then
    echo "  ✗ 关键文件缺失（${#MISSING[@]} 个）：${MISSING[*]}"
    echo "    ⇒ 内核会静默降级为演示模式，不要在此状态下做验证！"
    TOTAL_FAIL=$((TOTAL_FAIL+1))
  else
    echo "  ✓ 关键文件到位：${REQUIRED_FILES[*]}"
  fi

  WARN=()
  for f in "${EXPECTED_FILES[@]}"; do
    [ -f "$DEST/$f" ] || WARN+=("$f")
  done
  [ "${#WARN[@]}" -gt 0 ] && echo "  ! 建议补充（非硬性）：${WARN[*]}"

  # 摘要：打印几个关键文件的大小，便于人工核对是不是"真包"
  echo "  摘要 :"
  for f in avcodec-63.dll avformat-63.dll avutil-61.dll ffmpeg.exe; do
    if [ -f "$DEST/$f" ]; then
      printf "    %-18s %10s bytes\n" "$f" "$(stat -c %s "$DEST/$f")"
    fi
  done
done

echo ""
if [ "$TOTAL_FAIL" -eq 0 ]; then
  echo "✓ 部署完成，全部配置均通过校验"
  exit 0
else
  echo "✗ 有 $TOTAL_FAIL 项未通过，请按上面提示处理"
  exit 1
fi
