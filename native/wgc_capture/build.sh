#!/usr/bin/env bash
#
# build.sh — 构建 3FC.WgcCapture.dll 并运行自测（在 Git Bash 中运行）。
#
# 用法：
#   ./build.sh              # 构建 DLL + selftest.exe，然后运行自测
#   ./build.sh --no-run     # 只构建，不运行自测
#   ./build.sh --clean      # 先清掉构建产物再构建
#
# 产出（均在脚本所在目录，已被 .gitignore 忽略，不入库）：
#   3FC.WgcCapture.dll        原生 WGC 抓屏 DLL（x64）
#   3FC.WgcCapture.lib        /LD 附带的导入库（C# P/Invoke 其实不需要，保留备用）
#   selftest.exe              自测程序
#   *.obj / *.exp / *.pdb     中间产物
#
# 退出码：构建失败或**自测失败**均返回非 0（S2）。
#
# 编译环境探测（S2）：cl.exe 不在 PATH，INCLUDE/LIB 需手工拼装。按以下顺序解析，
# 换机器/换 VS 版本都不用改脚本：
#   1) 环境变量显式覆盖：WGC_CL / WGC_VC_DIR / WGC_SDK_DIR
#   2) vswhere.exe 定位 VS 安装目录 → 取其中最新的 MSVC 工具链
#   3) 在标准安装目录下按版本号排序取最新（MSVC / Windows Kits）
# 若探测失败，可用上面的环境变量手工指定，例如：
#   WGC_CL='C:\...\cl.exe' WGC_VC_DIR='C:\...\VC\Tools\MSVC\14.51.36231' \
#   WGC_SDK_DIR='C:\Program Files (x86)\Windows Kits\10' ./build.sh

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"

# Git Bash 会把 /nologo、/LD 这类参数误当路径转换，这里整体关掉
export MSYS2_ARG_CONV_EXCL='*'

# 把 Windows 路径转成 Git Bash 可用的 POSIX 路径（cygpath 在 Git Bash 里一定有）
to_posix() { cygpath -u "$1"; }
# 反向：cl.exe 只认 Windows 路径（INCLUDE/LIB 必须是分号分隔的反斜杠路径）
to_windows() { cygpath -w "$1"; }

# ---------------------------------------------------------------------------
# 1) MSVC 工具链：cl.exe 所在目录 + 该工具链根（含 include/lib）
# ---------------------------------------------------------------------------
find_vc_dir() {
    if [ -n "${WGC_VC_DIR:-}" ]; then
        echo "${WGC_VC_DIR}"; return 0
    fi
    local vsroot=""
    local vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
    if [ -x "$vswhere" ]; then
        vsroot="$("$vswhere" -latest -products '*' -property installationPath 2>/dev/null | tr -d '\r' || true)"
    fi
    local roots=()
    [ -n "$vsroot" ] && roots+=("$(to_posix "$vsroot")")
    for d in "/c/Program Files/Microsoft Visual Studio"/*/* "/c/Program Files (x86)/Microsoft Visual Studio"/*/*; do
        [ -d "$d/VC/Tools/MSVC" ] && roots+=("$d")
    done
    # 各 VS 安装下的 MSVC 版本目录里取最新（版本号字典序对 14.x 有效）
    local best=""
    for r in "${roots[@]}"; do
        for m in "$r/VC/Tools/MSVC"/*; do
            [ -x "$m/bin/Hostx64/x64/cl.exe" ] || continue
            if [ -z "$best" ] || [ "$(basename "$m")" \> "$(basename "$best")" ]; then best="$m"; fi
        done
    done
    [ -n "$best" ] && { echo "$best"; return 0; }
    return 1
}

# ---------------------------------------------------------------------------
# 2) Windows SDK：需要 ucrt / um / shared / winrt / cppwinrt 五套头
# ---------------------------------------------------------------------------
find_sdk_dir() {
    if [ -n "${WGC_SDK_DIR:-}" ]; then
        echo "${WGC_SDK_DIR}"; return 0
    fi
    local best="" v
    for base in "/c/Program Files (x86)/Windows Kits/10" "/c/Program Files/Windows Kits/10"; do
        [ -d "$base/Include" ] || continue
        for inc in "$base/Include"/*; do
            [ -d "$inc/ucrt" ] && [ -d "$inc/um" ] && [ -d "$inc/shared" ] && \
                [ -d "$inc/cppwinrt" ] || continue
            v="$(basename "$inc")"
            if [ -z "$best" ] || [ "$v" \> "$(basename "$best")" ]; then best="$inc"; fi
        done
    done
    [ -n "$best" ] && { dirname "$(dirname "$best")"; return 0; }   # 返回 SDK 根（含 Include/Lib）
    return 1
}

VC_DIR="$(find_vc_dir || true)"
SDK_DIR="$(find_sdk_dir || true)"

if [ -z "$VC_DIR" ] || [ -z "$SDK_DIR" ]; then
    cat >&2 <<'EOF'
无法自动探测 MSVC 工具链 / Windows SDK。请用环境变量显式指定后重试：
  WGC_CL='C:\...\VC\Tools\MSVC\<ver>\bin\Hostx64\x64\cl.exe'
  WGC_VC_DIR='C:\...\VC\Tools\MSVC\<ver>'
  WGC_SDK_DIR='C:\Program Files (x86)\Windows Kits\10'
（需要 Visual Studio 的「使用 C++ 的桌面开发」负载，含 Windows SDK + cppwinrt 头。）
EOF
    exit 2
fi

CL="${WGC_CL:-$(to_posix "$VC_DIR")/bin/Hostx64/x64/cl.exe}"
if [ ! -x "$CL" ]; then
    echo "找不到可执行的 cl.exe: $CL（可用 WGC_CL 覆盖）" >&2
    exit 2
fi

SDK_P="$(to_posix "$SDK_DIR")"
# SDK 版本目录（Include/Lib 下的具体版本号）
SDK_VER="$(basename "$(ls -d "$SDK_P/Include"/* | sort | tail -1)")"

# cl.exe 只认 Windows 路径
VC_W="$(to_windows "$VC_DIR")"
SDK_W="$(to_windows "$SDK_DIR")"
export INCLUDE="$VC_W\\include;$SDK_W\\Include\\$SDK_VER\\ucrt;$SDK_W\\Include\\$SDK_VER\\um;$SDK_W\\Include\\$SDK_VER\\shared;$SDK_W\\Include\\$SDK_VER\\winrt;$SDK_W\\Include\\$SDK_VER\\cppwinrt"
export LIB="$VC_W\\lib\\x64;$SDK_W\\Lib\\$SDK_VER\\ucrt\\x64;$SDK_W\\Lib\\$SDK_VER\\um\\x64"
echo "工具链: $VC_DIR"
echo "SDK   : $SDK_DIR (Include/Lib 版本 $SDK_VER)"

RUN=1
for arg in "$@"; do
    case "$arg" in
        --no-run) RUN=0 ;;
        --clean)  rm -f ./*.dll ./*.lib ./*.exp ./*.obj ./*.pdb ./*.exe ./*.bmp ;;
        *) echo "未知参数: $arg" >&2; exit 2 ;;
    esac
done

# 通用编译选项：/EHsc 异常、/MD 动态 CRT、/O2 优化、/std:c++20、/utf-8 源码为 UTF-8
COMMON=(/nologo /EHsc /MD /O2 /std:c++20 /utf-8 /W3)

echo "[1/3] 构建 3FC.WgcCapture.dll"
"$CL" "${COMMON[@]}" /LD /DWGC_CAPTURE_BUILD \
    /Fe:3FC.WgcCapture.dll \
    wgc_capture.cpp \
    windowsapp.lib d3d11.lib dxgi.lib dwmapi.lib ole32.lib user32.lib

echo
echo "[2/3] 构建 selftest.exe"
"$CL" "${COMMON[@]}" \
    /Fe:selftest.exe \
    selftest.cpp \
    3FC.WgcCapture.lib d3d11.lib dxgi.lib dwmapi.lib ole32.lib user32.lib gdi32.lib

echo
echo "构建完成："
ls -l 3FC.WgcCapture.dll selftest.exe

if [ "$RUN" -eq 1 ]; then
    echo
    echo "[3/3] 运行自测"
    echo "----------------------------------------------------------------"
    # set -e 下失败会立刻退出、拿不到退出码，所以这里先关掉再取 $?
    set +e
    ./selftest.exe
    rc=$?
    set -e
    echo "----------------------------------------------------------------"
    echo "自测退出码: $rc"
    if [ "$rc" -ne 0 ]; then
        echo "自测失败：$rc 项失败（或进程异常退出，退出码 $rc）" >&2
        exit "$rc"
    fi
    echo "自测全部通过"
fi
