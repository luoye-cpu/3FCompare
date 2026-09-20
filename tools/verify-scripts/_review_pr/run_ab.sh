#!/usr/bin/env bash
# 进程级 Present 锁对照实验：同批次交错 A(基线) → B(新锁)，每轮各 1 次。
# 铁律：跨批次比较无效（机器负载漂移）⇒ 轮内交替。
# 用法： run_ab.sh <rounds> <video> <durationSec> [routes]
set -u
ROUNDS="${1:?rounds}"
VIDEO="${2:?video}"
DUR="${3:?durationSec}"
ROUTES="${4:-4}"
ARMS="${5:-A B}"   # 预实验只跑基线时可传 "A"

ROOT="C:/PLAN/3FCompare"
BIN="$ROOT/src/3FCompare/bin/Debug/net11.0-windows"
OUT="$ROOT/.review_pr/exp_pr9"
DLL_A="$ROOT/third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll"
DLL_B="$ROOT/third_party/fff_project/FFF.Native/x64/Release_pr9/FFF.Native.dll"
SHA_A="67454de21ad69be3f10ef35ace3cad7797e18784e20220d9849329cf787cf1b8"
SHA_B="29f5877506986f21f7709972e2b26d7380a9eedf3a9ed4e4c8f08c9959a0002d"
TARGET="$BIN/FFF.Native.dll"
TIMEOUT_S=240

mkdir -p "$OUT/runlogs"
CSV="$OUT/ab.csv"
[ -f "$CSV" ] || echo "round,order,arm,video,routes,dur,exit,crashed,module,exception,wall_s,sha_ok,phase" > "$CSV"

sha_of() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }
uptime_s() { cut -d' ' -f1 /proc/uptime; }

# 崩溃取证：取时间窗内最新的 3FCompare 崩溃事件，抽出出错模块名与异常代码
crash_module() { # <windowMs>
  wevtutil qe Application /q:"*[System[Provider[@Name='Application Error'] and TimeCreated[timediff(@SystemTime) <= $1]]]" /f:text /c:6 2>/dev/null \
  | tr -d '\r' \
  | awk '
      /^出错应用程序名称|^Faulting application name/ { seen=1 }
      /3FCompare/ { hit=1 }
      /^出错模块名称|^Faulting module name/ {
        if (hit && mod=="") { mod=$0; sub(/^[^:]*:[ ]*/,"",mod); sub(/[,，].*$/,"",mod) }
      }
      /^异常代码|^Exception code/ {
        if (hit && exc=="") { exc=$0; sub(/^[^:]*:[ ]*/,"",exc); sub(/[,，].*$/,"",exc) }
      }
      /^Event\[/ { hit=0 }
      END { printf "%s|%s", (mod==""?"none":mod), (exc==""?"-":exc) }
    '
}

BOOT0=$(uptime_s)
echo "== 实验开始 $(date '+%F %T') | uptime=${BOOT0}s | 轮数=$ROUNDS | 素材=$(basename "$VIDEO") | 路数=$ROUTES | 时长=${DUR}s"

abort=0
for r in $(seq 1 "$ROUNDS"); do
  order=0
  for arm in $ARMS; do
    order=$((order+1))
    if [ "$arm" = "A" ]; then SRC="$DLL_A"; WANT="$SHA_A"; else SRC="$DLL_B"; WANT="$SHA_B"; fi

    # 1) 换 DLL（磁盘已存在时内核绝不覆盖，见 NativeRuntime.cs:36-52）
    cp -f "$SRC" "$TARGET"
    got=$(sha_of "$TARGET")
    if [ "$got" != "$WANT" ]; then
      echo "!! 致命：换入 $arm 后 bin DLL sha 不匹配 ($got != $WANT)，中止"; abort=1; break
    fi

    t0=$(date +%s)
    ( cd "$BIN" && timeout "$TIMEOUT_S" ./3FCompare.exe --multitest "$VIDEO" "$ROUTES" "$DUR" \
        > "$OUT/runlogs/${arm}_r${r}.log" 2>&1 )
    code=$?
    t1=$(date +%s)
    wall=$((t1-t0))

    shaok=1
    if [ "$(sha_of "$TARGET")" != "$WANT" ]; then echo "!! 警告：运行后 bin DLL 被改写，该轮作废"; shaok=0; fi

    if [ "$code" -ge 128 ] && [ "$code" -ne 124 ]; then crashed=1; else crashed=0; fi

    mod="—"; exc=""
    if [ "$crashed" = "1" ]; then
      pair=$(crash_module $(( (wall + 20) * 1000 )))
      mod="${pair%%|*}"; exc="${pair##*|}"
    fi

    kern=$(grep -h -o "FFF.Native.dll 与内嵌资源[^；]*" "$BIN"/logs/app-*.log 2>/dev/null | tail -1)
    phase=$(tail -1 "$OUT/runlogs/${arm}_r${r}.log" 2>/dev/null | tr -d '\r' | cut -c1-46)

    echo "$r,$order,$arm,$(basename "$VIDEO"),$ROUTES,$DUR,$code,$crashed,$mod,$exc,$wall,$shaok,\"$phase\"" >> "$CSV"
    printf 'r%-2s %s exit=%-4s crash=%s mod=%-12s exc=%-12s wall=%-3ss sha=%s | %s\n' \
      "$r" "$arm" "$code" "$crashed" "$mod" "$exc" "$wall" "$shaok" "$phase"
    [ -n "$kern" ] && echo "        └ 内核留痕: $kern"

    # 蓝屏检测：uptime 倒退 = 系统重启过
    unow=$(uptime_s)
    if awk -v a="$unow" -v b="$BOOT0" 'BEGIN{exit !(a<b)}'; then
      echo ""
      echo "###### 检测到系统重启（蓝屏）！uptime ${BOOT0}s → ${unow}s"
      echo "###### 立即停止实验。已完成：第 $r 轮第 $order 个"
      echo "$r,$order,$arm,REBOOT,,,,,,,,," >> "$CSV"
      abort=1; break
    fi
  done
  [ "$abort" = "1" ] && break
done

# 实验结束：无条件恢复基线 DLL
cp -f "$DLL_A" "$TARGET"
restored=$(sha_of "$TARGET")
echo "== 实验结束 $(date '+%F %T')"
if [ "$restored" = "$SHA_A" ]; then echo "== 基线 DLL 已恢复 ✓ sha=$restored"; else echo "!! 恢复失败 sha=$restored"; fi
