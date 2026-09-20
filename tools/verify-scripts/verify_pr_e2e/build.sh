#!/usr/bin/env bash
set -uo pipefail
ROOT="C:/PLAN/3FCompare/third_party/fff_project"
NAT="$ROOT/FFF.Native"
OBJ="C:/PLAN/3FCompare/.verify_pr_e2e/obj"
OUT="C:/PLAN/3FCompare/.verify_pr_e2e/out"
VC="C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231"
SDK="C:/Program Files (x86)/Windows Kits/10"
SDKV="10.0.28000.0"
FF="$ROOT/third_party/ffmpeg"
VCPKG="$ROOT/third_party/vcpkg_installed/x64-windows"

export PATH="$VC/bin/Hostx64/x64:$SDK/bin/$SDKV/x64:$PATH"
export INCLUDE="$VC/include;$SDK/Include/$SDKV/ucrt;$SDK/Include/$SDKV/um;$SDK/Include/$SDKV/shared;$SDK/Include/$SDKV/winrt"
export LIB="$VC/lib/x64;$SDK/Lib/$SDKV/ucrt/x64;$SDK/Lib/$SDKV/um/x64"

CL="$VC/bin/Hostx64/x64/cl.exe"
RC="$SDK/bin/$SDKV/x64/rc.exe"
LINK="$VC/bin/Hostx64/x64/link.exe"
# Windows 工具按 ';' 解析 PATH，且必须用反斜杠形式，否则 link 无法拉起 mt.exe (LNK1158)
WINPATH='C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64;C:\Program Files (x86)\Windows Kits\10\bin\10.0.28000.0\x64'

COMMON="/c /I$NAT/ /I$NAT/3FR /I$FF/include /I$VCPKG/include /Zi /nologo /W3 /WX- /diagnostics:column /sdl /O2 /Oi /GL /D NDEBUG /D FFFNATIVE_EXPORTS /D _WINDOWS /D _USRDLL /D _WINDLL /D _UNICODE /D UNICODE /Gm- /EHsc /MD /GS /Gy /fp:precise /Zc:wchar_t /Zc:forScope /Zc:inline /std:c++20 /permissive- /external:W3 /Gd /TP /FC /utf-8"

SRCS=(
  3FP/Api/PlayerApi.cpp 3FP/Audio/WasapiRenderer.cpp 3FP/Core/PlayerSession.cpp
  3FP/Core/PlayerSessionDisc.cpp 3FP/Disc/DiscInput.cpp 3FP/Hdr/HdrProcessor.cpp
  3FP/Render/VideoRenderer.cpp 3FP/Subtitle/AssSubtitleRenderer.cpp
  3FP/Subtitle/BitmapSubtitleDecoder.cpp 3FR/Api/NativeApi.cpp
  3FR/Audio/AudioMixer.cpp 3FR/Audio/AudioTrackEncoder.cpp 3FR/Audio/WasapiCapture.cpp
  3FR/Core/Session.cpp 3FR/Ffmpeg/VideoMuxer.cpp 3FR/Timeline/QpcTimeline.cpp
  Shared/Ffmpeg/FfmpegRuntime.cpp Shared/Ffmpeg/SharedFileInput.cpp dllmain.cpp
)

cd "$OBJ" || exit 1
echo "===== PCH ====="
"$CL" $COMMON /Yc"pch.h" /Fp"$OBJ/FFF.Native.pch" /Fo"$OBJ\\" /Fd"$OBJ/vc145.pdb" "$NAT/pch.cpp" 2>&1
echo "PCH_EXIT=$?"

for s in "${SRCS[@]}"; do
  echo "===== $s ====="
  "$CL" $COMMON /Yu"pch.h" /Fp"$OBJ/FFF.Native.pch" /Fo"$OBJ\\" /Fd"$OBJ/vc145.pdb" "$NAT/$s" 2>&1
  echo "EXIT=$?"
done

echo "===== RC ====="
"$RC" /D _UNICODE /D UNICODE /l"0x0409" /nologo /fo"$OBJ/FFF.Native.res" "$NAT/FFF.Native.rc" 2>&1
echo "RC_EXIT=$?"

echo "===== LINK ====="
OBJS=$(ls "$OBJ"/*.obj)
PATH="$WINPATH" "$LINK" /OUT:"$OUT/FFF.Native.dll" /NOLOGO \
  /LIBPATH:$FF/lib/x64 /LIBPATH:$VCPKG/lib \
  bluray.lib ass.lib avcodec-63.lib avformat-63.lib avutil-61.lib swresample-7.lib swscale-10.lib avfilter-12.lib \
  delayimp.lib d3d11.lib d3dcompiler.lib dxgi.lib dxguid.lib d2d1.lib dwrite.lib ole32.lib runtimeobject.lib \
  propsys.lib avrt.lib kernel32.lib user32.lib gdi32.lib winspool.lib comdlg32.lib advapi32.lib shell32.lib \
  ole32.lib oleaut32.lib uuid.lib odbc32.lib odbccp32.lib delayimp.lib \
  /DELAYLOAD:"bluray-3.dll" /DELAYLOAD:"ass-9.dll" /DELAYLOAD:"avcodec-63.dll" /DELAYLOAD:"avformat-63.dll" \
  /DELAYLOAD:"avutil-61.dll" /DELAYLOAD:"swresample-7.dll" /DELAYLOAD:"swscale-10.dll" /DELAYLOAD:"avfilter-12.dll" \
  /MANIFEST /MANIFESTUAC:NO /manifest:embed \
  /manifestinput:"$VC/include/manifest/segmentheap.manifest" \
  /DEBUG /PDB:"$OUT/FFF.Native.pdb" /SUBSYSTEM:WINDOWS /OPT:REF /OPT:ICF \
  /LTCG:incremental /LTCGOUT:"$OBJ/FFF.Native.iobj" /TLBID:1 /DYNAMICBASE /NXCOMPAT \
  /IMPLIB:"$OUT/FFF.Native.lib" /MACHINE:X64 /DLL $OBJS "$OBJ/FFF.Native.res" 2>&1
echo "LINK_EXIT=$?"
