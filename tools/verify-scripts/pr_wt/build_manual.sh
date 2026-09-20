#!/bin/bash
# 手工复刻 FFF.Native 官方构建参数（来源：旧 obj 的 *.tlog），输出到独立 obj_pr2 目录。
# 只编译/链接，不改任何源文件。
set -u

MODE="${1:-all}"

SRC='C:\PLAN\3FCompare\.review_pr\pr_wt\FFF.Native'
OBJ='C:\PLAN\3FCompare\.review_pr\pr_wt\FFF.Native\obj_pr2\x64\Release'
MAIN='C:\PLAN\3FCompare\third_party\fff_project\third_party'
FFMPEG_INC="$MAIN\\ffmpeg\\include"
FFMPEG_LIB="$MAIN\\ffmpeg\\lib\\x64"
VCPKG_INC="$MAIN\\vcpkg_installed\\x64-windows\\include"
VCPKG_LIB="$MAIN\\vcpkg_installed\\x64-windows\\lib"

MSVC='C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
SDK='C:\Program Files (x86)\Windows Kits\10'
SDKV='10.0.28000.0'

CL="$MSVC\\bin\\Hostx64\\x64\\cl.exe"
LINK="$MSVC\\bin\\Hostx64\\x64\\link.exe"
RC="$SDK\\bin\\$SDKV\\x64\\rc.exe"

export INCLUDE="$MSVC\\include;$SDK\\Include\\$SDKV\\ucrt;$SDK\\Include\\$SDKV\\um;$SDK\\Include\\$SDKV\\shared;$SDK\\Include\\$SDKV\\winrt"
export LIB="$MSVC\\lib\\x64;$SDK\\Lib\\$SDKV\\ucrt\\x64;$SDK\\Lib\\$SDKV\\um\\x64"
# link.exe 阶段 PATH 必须 Windows 风格（反斜杠 + 分号），否则 LNK1158 找不到 mt.exe
# 只作为单条命令的环境前缀注入；并保留原始 PATH（整段替换会让 cl 预编译头行为异常）
WINPATH="$MSVC\\bin\\Hostx64\\x64;$SDK\\bin\\$SDKV\\x64;C:\\Windows\\system32;C:\\Windows;$PATH"

cd "$SRC" || exit 1

COMMON=(/c /I"$SRC" /I"$SRC\\3FR" /I"$FFMPEG_INC" /I"$VCPKG_INC"
  /Zi /nologo /W3 /WX- /diagnostics:column /sdl /O2 /Oi /GL
  /D NDEBUG /D FFFNATIVE_EXPORTS /D _WINDOWS /D _USRDLL /D _WINDLL /D _UNICODE /D UNICODE
  /Gm- /EHsc /MD /GS /Gy /fp:precise /Zc:wchar_t /Zc:forScope /Zc:inline
  /std:c++20 /permissive- /external:W3 /Gd /TP /FC /utf-8)

PCH_OUT="$OBJ\\FFF.Native.pch"

SOURCES=(pch.cpp
  3FP/Api/PlayerApi.cpp
  3FP/Disc/DiscInput.cpp
  3FP/Core/PlayerSessionDisc.cpp
  3FP/Audio/WasapiRenderer.cpp
  3FP/Core/PlayerSession.cpp
  3FP/Hdr/HdrProcessor.cpp
  3FP/Render/VideoRenderer.cpp
  3FP/Subtitle/AssSubtitleRenderer.cpp
  3FP/Subtitle/BitmapSubtitleDecoder.cpp
  3FR/Api/NativeApi.cpp
  3FR/Audio/AudioTrackEncoder.cpp
  3FR/Audio/AudioMixer.cpp
  3FR/Audio/WasapiCapture.cpp
  3FR/Core/Session.cpp
  dllmain.cpp
  3FR/FFmpeg/VideoMuxer.cpp
  Shared/FFmpeg/FfmpegRuntime.cpp
  Shared/FFmpeg/SharedFileInput.cpp
  3FR/Timeline/QpcTimeline.cpp)

compile_one() {
  local src="$1" yc="$2" log="$3"
  PATH="$WINPATH" "$CL" "${COMMON[@]}" "$yc" /Fp"$PCH_OUT" /Fo"$OBJ\\\\" /Fd"$OBJ\\vc145.pdb" "$src" > "$log" 2>&1
  local rc=$?
  local ne nw
  ne=$(grep -c -E "(error|fatal error) [A-Z]+[0-9]+" "$log")
  nw=$(grep -c -E "warning [A-Z]+[0-9]+" "$log")
  if [ "$rc" -eq 0 ] && [ "$ne" -eq 0 ]; then
    printf 'PASS  %-45s err=%s warn=%s\n' "$src" "$ne" "$nw"
  else
    printf 'FAIL  %-45s err=%s warn=%s (exit=%s)\n' "$src" "$ne" "$nw" "$rc"
  fi
  return $rc
}

LOGDIR="$SRC/obj_pr2/logs"
mkdir -p "$LOGDIR"

if [ "$MODE" = "all" ] || [ "$MODE" = "compile" ]; then
  echo "===== 编译阶段 (20 TU) ====="
  FAILED=0
  for s in "${SOURCES[@]}"; do
    base="${s##*/}"; base="${base%.cpp}"
    if [ "$s" = "pch.cpp" ]; then
      compile_one "$s" '/Ycpch.h' "$LOGDIR/$base.log" || FAILED=1
    else
      compile_one "$s" '/Yupch.h' "$LOGDIR/$base.log" || FAILED=1
    fi
  done
  echo "===== 编译阶段结束 FAILED=$FAILED ====="
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "rc" ]; then
  echo "===== rc 阶段 ====="
  PATH="$WINPATH" "$RC" /D _UNICODE /D UNICODE /l"0x0409" /nologo /fo"$OBJ\\FFF.Native.res" "$SRC\\FFF.Native.rc" > "$LOGDIR/rc.log" 2>&1
  echo "rc exit=$? -> $OBJ\\FFF.Native.res"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "link" ]; then
  echo "===== 链接阶段 ====="
  OBJS=()
  for s in "${SOURCES[@]}"; do
    base="${s##*/}"; base="${base%.cpp}"
    OBJS+=("$OBJ\\$base.obj")
  done
  PATH="$WINPATH" "$LINK" /OUT:"$OBJ\\FFF.Native.dll" /NOLOGO \
    /LIBPATH:"$FFMPEG_LIB" /LIBPATH:"$VCPKG_LIB" \
    bluray.lib ass.lib "avcodec-63.lib" "avformat-63.lib" "avutil-61.lib" \
    "swresample-7.lib" "swscale-10.lib" "avfilter-12.lib" delayimp.lib \
    d3d11.lib d3dcompiler.lib dxgi.lib dxguid.lib d2d1.lib dwrite.lib \
    ole32.lib runtimeobject.lib propsys.lib avrt.lib kernel32.lib user32.lib \
    gdi32.lib winspool.lib comdlg32.lib advapi32.lib shell32.lib oleaut32.lib \
    uuid.lib odbc32.lib odbccp32.lib \
    /DELAYLOAD:"bluray-3.dll" /DELAYLOAD:"ass-9.dll" /DELAYLOAD:"avcodec-63.dll" \
    /DELAYLOAD:"avformat-63.dll" /DELAYLOAD:"avutil-61.dll" /DELAYLOAD:"swresample-7.dll" \
    /DELAYLOAD:"swscale-10.dll" /DELAYLOAD:"avfilter-12.dll" \
    /MANIFEST /MANIFESTUAC:NO /manifest:embed \
    /manifestinput:"$MSVC\\include\\manifest\\segmentheap.manifest" \
    /DEBUG /PDB:"$OBJ\\FFF.Native.pdb" /SUBSYSTEM:WINDOWS /OPT:REF /OPT:ICF \
    /LTCG:incremental /LTCGOUT:"$OBJ\\FFF.Native.iobj" /TLBID:1 /DYNAMICBASE /NXCOMPAT \
    /IMPLIB:"$OBJ\\FFF.Native.lib" /MACHINE:X64 /DLL \
    "${OBJS[@]}" "$OBJ\\FFF.Native.res" > "$LOGDIR/link.log" 2>&1
  echo "link exit=$?"
  ls -l "$SRC/obj_pr2/x64/Release/FFF.Native.dll" 2>&1
fi
