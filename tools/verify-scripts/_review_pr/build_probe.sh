#!/usr/bin/env bash
# 编译并运行 DXGI 探针（只读）。产物落 .review_pr/gpu_spread/
set -u
VC="C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231"
SDK="C:/Program Files (x86)/Windows Kits/10"
SDKV="10.0.28000.0"
export PATH="$VC/bin/Hostx64/x64:$SDK/bin/$SDKV/x64:$PATH"
export INCLUDE="$VC/include;$SDK/Include/$SDKV/ucrt;$SDK/Include/$SDKV/um;$SDK/Include/$SDKV/shared"
export LIB="$VC/lib/x64;$SDK/Lib/$SDKV/ucrt/x64;$SDK/Lib/$SDKV/um/x64"
cd /c/PLAN/3FCompare/.review_pr/gpu_spread
"$VC/bin/Hostx64/x64/cl.exe" /nologo /EHsc /O2 /std:c++17 dxgi_probe.cpp /Fe:dxgi_probe.exe \
  /link dxgi.lib d3d11.lib > build.log 2>&1
if [ $? -ne 0 ]; then echo "BUILD FAILED"; cat build.log; exit 1; fi
./dxgi_probe.exe
