#!/bin/bash
# rht_bisect.sh —— 用 run_host_tests.sh 完全相同的方式（含构建）跑开关，但**逐套件后立即检查现场**，
# 定位污染究竟出现在哪一个开关之后。
#
# 为什么需要：逐套件探测（ab_sweep_probe.ps1，每套件前还原到基线）结论是 21/21 全 CLEAN，
#   但 run_host_tests.sh 一次跑完却污染 ⇒ 差异必在"脚本自身的行为"里（先构建、TEMP 覆盖、
#   或某套件的副作用在**不还原**的情况下被下个套件放大）。本脚本保留"不还原"这一条件，
#   只加观测，从而复现并定位。
set -u
ROOT=/c/PLAN/3FCompare
PROJ="$ROOT/3fp/kernel/FFF.Player.Tests"
FLAGS="$ROOT/3fp/build/flags1.txt"
OUT="$ROOT/3fp/build/guard_ab/rht_bisect.txt"
TIMEOUT=120

export TEMP='C:\Users\20210\AppData\Local\Temp'
export TMP="$TEMP"

for i in 1 2 3 4 5; do
  taskkill //F //IM FFF.Player.exe >/dev/null 2>&1
  [ "$(tasklist 2>/dev/null | grep -ci 'FFF.Player.exe')" = "0" ] && break
  sleep 2
done

cd "$PROJ" || exit 1
"C:/Program Files/dotnet/dotnet.exe" build FFF.Player.Tests.vbproj -c Release --nologo > "$ROOT/3fp/build/guard_ab/rht_bisect_build.log" 2>&1
ERRORS=$(grep -cE "error [A-Z]+[0-9]+" "$ROOT/3fp/build/guard_ab/rht_bisect_build.log")
[ "$ERRORS" != "0" ] && { echo "构建失败（$ERRORS）"; exit 1; }

cd "$PROJ/bin/Release/net10.0-windows10.0.26100.0" || exit 1
: > "$OUT"

# 用一个 PowerShell 子进程判「当前现场是否已偏离基线」——走 PowerShell 才能可靠读注册表
check() {
  local tag="$1"
  powershell.exe -NoProfile -Command "
\$ROOT='C:\PLAN\3FCompare\3fp'
& \"\$ROOT\tools\dump_associations.ps1\" -Out \"\$env:TEMP\_bisect_now.txt\" -Exts '.png,.jpg,.gif,.bmp,.tiff,.apng,.webp,.heic,.avif' | Out-Null
\$b=Get-Content \"\$ROOT\build\guard_ab\baseline_before.txt\" -Encoding UTF8
\$n=Get-Content \"\$env:TEMP\_bisect_now.txt\" -Encoding UTF8
if (\$null -eq (Compare-Object \$b \$n -SyncWindow 0)) { 'CLEAN' } else { 'DIRTY' }
" 2>/dev/null | tr -d '\r'
}

echo "跑前: $(check 前)" >> "$OUT"
while IFS= read -r flag; do
  [ -z "$flag" ] && continue
  TMPF=$(mktemp)
  timeout "$TIMEOUT" ./FFF.Player.Tests.exe "$flag" > "$TMPF" 2>&1
  code=$?
  st=$(check "$flag")
  echo "$st  $flag  (EXIT=$code)" >> "$OUT"
  rm -f "$TMPF"
done < "$FLAGS"
cat "$OUT"
