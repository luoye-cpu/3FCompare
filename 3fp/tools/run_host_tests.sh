#!/bin/bash
# ══════════════════════════════════════════════════════════════════════════════
# run_host_tests.sh —— 跑遍 FFF.Player.Tests 的全部「单参数」开关，汇总通过/失败
#
# 为什么需要：宿主测试有 51 个开关、**没有全量模式**，逐个手敲不现实；
#   而"上游更新后是否引入回归"必须靠**广覆盖扫描**，不能只跑自己关心的那一个。
#
# ⚠ 三个必须注意的点（都踩过）：
#   ① **构建必须先成功**。构建失败（例如 FFF.Player.exe 被占用报 MSB3021）时，
#      测试仍会跑**陈旧二进制**并可能"全绿" ⇒ 典型的假绿。脚本会先构建并检查错误数。
#   ② **不要用 `| tail` 取退出码**：`cmd | tail; echo $?` 拿到的是 tail 的状态（恒 0）。
#      本脚本重定向到文件再取 `$?`。
#   ③ **`--` 开关与 settings.json 无关，别再找"白名单"**（2026-09-25 晚更正）。
#      旧版本这里写着"带 `--` 的开关不污染 settings.json（自测白名单）"——**该白名单机制不存在**，
#      是凭空写下的假注释（`docs/12` §4.3 已点出）。真实机制是：
#        · 测试进程走 `Program.Main` 开头的 **`引导测试进程的文件关联(参数)`** 闸（`FFF.Player.Tests/Program.vb`）；
#          它清掉内存里的 9 个 `关联*` 选项，并把**设置文件路径重定向**到临时全 `False` 副本；
#          只清内存无效 —— `Form1_Load` 的 `设置.启动时加载设置()` 会把 `设置.实例对象` **整体替换**。
#        · 例外只放行两个**手动**开关（`--file-association-live-toggle/-release`，它们本就为写真机 HKCU 而设），
#          二者不在本脚本的扫描清单里。
#      ⇒ 结论：本脚本扫描时**不会**污染真机 HKCU（实测 21/21 CLEAN），但这靠的是**闸**，不是"开关前缀"。
#      真机注册表现场若已脏，用 `tools/restore_associations_baseline.ps1 -Apply` 还原（以基线文本为真源做差集）。
#      真机注册表清理**不要用 Git Bash 的 `reg delete`**：MSYS 会把 `/f` 当路径转换 ⇒ 命令静默不执行。
#
# 用法：bash 3fp/tools/run_host_tests.sh
# 输出：3fp/build/host_tests_result.txt
# ══════════════════════════════════════════════════════════════════════════════
set -u
ROOT=/c/PLAN/3FCompare
PROJ="$ROOT/3fp/kernel/FFF.Player.Tests"
OUT="$ROOT/3fp/build/host_tests_result.txt"
FLAGS="$ROOT/3fp/build/flags1.txt"
TIMEOUT=120

# ── 前置 0：把 TEMP/TMP 归一成 **Windows 风格路径** ──
# ⚠ Git Bash 默认把 TEMP/TMP 设成 POSIX 路径 `/tmp`，Windows 原生进程按 `C:\tmp` 之类解释，
#   于是测试里 `Path.GetTempPath()` 拿到的目录**不存在或不可建**，报
#   "The directory ... does not exist" / "Cannot create ... because a file or directory with the
#   same name already exists"。实测：修好 TEMP 后，此前 9 个失败里 8 个直接转绿。
export TEMP='C:\Users\20210\AppData\Local\Temp'
export TMP="$TEMP"
mkdir -p "$ROOT/3fp/build/_wintmp"
[ -d "/c/Users/20210/AppData/Local/Temp" ] || export TEMP="$ROOT/3fp/build/_wintmp"

# ── 前置：确保没有宿主实例占着 exe（否则构建会 MSB3021 失败）──
for i in 1 2 3 4 5; do
  taskkill //F //IM FFF.Player.exe >/dev/null 2>&1
  [ "$(tasklist 2>/dev/null | grep -ci 'FFF.Player.exe')" = "0" ] && break
  sleep 2
done

# ── 构建（必须成功；失败就停，别拿陈旧二进制当结果）──
cd "$PROJ" || exit 1
BUILD_LOG="$ROOT/3fp/build/host_tests_build.log"
"C:/Program Files/dotnet/dotnet.exe" build FFF.Player.Tests.vbproj -c Release --nologo > "$BUILD_LOG" 2>&1
ERRORS=$(grep -cE "error [A-Z]+[0-9]+" "$BUILD_LOG")
if [ "$ERRORS" != "0" ]; then
  echo "构建失败（$ERRORS 个错误）⇒ 中止，避免用陈旧二进制得到假绿。见 $BUILD_LOG"
  exit 1
fi
echo "构建通过（0 错误）"

cd "$PROJ/bin/Release/net10.0-windows10.0.26100.0" || exit 1
: > "$OUT"
PASS=0; FAIL=0

while IFS= read -r flag; do
  [ -z "$flag" ] && continue
  # ⚠ 变量名必须避开 TMP：前置已 `export TMP="$TEMP"`（第 41 行），此处若再
  #   `TMP=$(mktemp)` 会把**已导出的 TMP** 重新赋值成 mktemp 的**文件路径**，
  #   测试进程继承后 `Path.GetTempPath()` 返回一个文件 ⇒ 建临时目录时
  #   "Cannot create ... same name already exists"（2026-09-26 实测一次打挂 5 个测试）。
  RUNOUT=$(mktemp)
  timeout "$TIMEOUT" ./FFF.Player.Tests.exe "$flag" > "$RUNOUT" 2>&1
  code=$?
  if [ "$code" = "0" ] && ! grep -q "测试失败" "$RUNOUT"; then
    PASS=$((PASS + 1))
    printf 'PASS  %-46s %s\n' "$flag" "$(tail -1 "$RUNOUT" | cut -c1-60)" >> "$OUT"
  else
    FAIL=$((FAIL + 1))
    {
      printf 'FAIL  %-46s EXIT=%s\n' "$flag" "$code"
      grep -m3 "测试失败\|Unhandled\|Exception" "$RUNOUT" | sed 's/^/        /'
    } >> "$OUT"
  fi
  rm -f "$RUNOUT"
done < "$FLAGS"

echo "" >> "$OUT"
echo "通过 $PASS / 失败 $FAIL（共 $((PASS + FAIL))）" >> "$OUT"
cat "$OUT"
