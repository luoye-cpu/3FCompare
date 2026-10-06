# 重新编译所有依赖 FFF.Player.Api.h 的探针。
#
# 为什么需要：向 FFF3FPConfiguration 追加字段会改变 sizeof，而探针用
# `configuration.size = sizeof(configuration)` 上报自身结构大小。旧探针上报过短的
# size，内核会（正确地）拒绝该配置，表现为 "Create 失败"。
# 这是预期的 ABI 行为，不是内核缺陷 —— 但每次改 API 头后必须重编探针。
#
# 用法：pwsh -File tools/rebuild_probes.ps1

$ErrorActionPreference = 'Continue'
$root = 'C:\PLAN\3FCompare\3fp'
$kernel = Join-Path $root 'kernel\FFF.Native'
$lib = Join-Path $kernel 'x64\Release_anim\FFF.Native.lib'
$out = Join-Path $root 'build\vsr'
$tools = Join-Path $root 'tools'

$bat = @"
@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d $root
"@

# 只用内核库的探针
$simple = @('decode_profile','block_scale_probe','image_zoom_probe','scale_policy_probe',
            'upload_layout_probe','adaptive_scale_probe','read_pixel_probe','rotation_probe',
            'hdrpeak_probe','copyframe_probe','occlusion_probe','shot_probe','vsr_probe')
# 需要 libiamf 的探针
$iamf = @('iamf_layout_regression','iamf_playback_probe','iamf_probe')
$libiamfInclude = Join-Path $kernel '..\third_party\libiamf\include'
$libiamfOar = Join-Path $kernel '..\third_party\libiamf\oar\include'
$libiamfLib = Join-Path $kernel '..\third_party\libiamf\lib\x64'

$ok = 0; $fail = @()
foreach ($name in $simple) {
    $src = Join-Path $tools ($name + '.cpp')
    if (-not (Test-Path $src)) { continue }
    $line = "cl /nologo /EHsc /std:c++20 /O2 /I `"$kernel`" /Fe:`"$out\$name.exe`" /Fo:`"$out\`" `"$src`" `"$lib`" user32.lib gdi32.lib"
    Set-Content -Path (Join-Path $out 'rb_one.bat') -Value ($bat + $line) -Encoding ASCII
    $r = cmd /c "`"$out\rb_one.bat`"" 2>&1
    if (Test-Path (Join-Path $out "$name.exe")) {
        Copy-Item (Join-Path $out "$name.exe") (Join-Path $root "build\$name.exe") -Force
        Write-Host "  OK   $name"; $ok++
    } else {
        Write-Host "  FAIL $name"; $fail += $name
    }
}
foreach ($name in $iamf) {
    $src = Join-Path $tools ($name + '.cpp')
    if (-not (Test-Path $src)) { continue }
    $line = "cl /nologo /EHsc /std:c++20 /O2 /I `"$kernel`" /I `"$libiamfInclude`" /I `"$libiamfOar`" /DFFF_WITH_LIBIAMF=1 /Fe:`"$out\$name.exe`" /Fo:`"$out\`" `"$src`" `"$lib`" `"$libiamfLib\iamf.lib`" `"$libiamfLib\oar.lib`" user32.lib"
    Set-Content -Path (Join-Path $out 'rb_one.bat') -Value ($bat + $line) -Encoding ASCII
    $r = cmd /c "`"$out\rb_one.bat`"" 2>&1
    if (Test-Path (Join-Path $out "$name.exe")) {
        Copy-Item (Join-Path $out "$name.exe") (Join-Path $root "build\$name.exe") -Force
        Write-Host "  OK   $name"; $ok++
    } else {
        Write-Host "  FAIL $name"; $fail += $name
    }
}
Remove-Item (Join-Path $out 'rb_one.bat') -Force -ErrorAction SilentlyContinue
Write-Host "`n成功 $ok，失败 $($fail.Count)"
if ($fail.Count) { Write-Host ("失败项: " + ($fail -join ', ')) }
