# 重新编译所有依赖 FFF.Player.Api.h 的探针。
#
# 为什么需要：向 FFF3FPConfiguration 追加字段会改变 sizeof，而 FFF3FP_Create 会拒绝
# 小于 sizeof(FFF3FPConfiguration) 的结构体。旧探针上报过短的 size，内核（正确地）
# 返回 InvalidArgument，表现为 "Create 失败" —— 看起来像内核回归，实为探针未重编。
# 每次改 API 头后都必须运行本脚本。
#
# 用法：pwsh -File tools/rebuild_probes.ps1

$ErrorActionPreference = 'Continue'
$root = 'C:\PLAN\3FCompare\3fp'
$kernel = Join-Path $root 'kernel\FFF.Native'
$lib = Join-Path $kernel 'x64\Release_anim\FFF.Native.lib'
$out = Join-Path $root 'build\vsr'
$tools = Join-Path $root 'tools'
$libiamfInclude = Join-Path $kernel '..\third_party\libiamf\include'
$libiamfOar = Join-Path $kernel '..\third_party\libiamf\oar\include'
$libiamfLib = Join-Path $kernel '..\third_party\libiamf\lib\x64'

# 需要额外库的探针
$extraLibs = @{
    'copyframe_probe'      = 'user32.lib gdi32.lib'
    'occlusion_probe'      = 'user32.lib gdi32.lib'
    'shot_probe'           = 'user32.lib gdi32.lib'
    'read_pixel_probe'     = 'user32.lib gdi32.lib'
    'block_scale_probe'    = 'user32.lib'
    'image_zoom_probe'     = 'user32.lib'
    'scale_policy_probe'   = 'user32.lib'
    # Queries DXGI for the monitor's live HDR state, so it needs dxgi.
    'vsr_scrgb_probe'      = 'dxgi.lib'
}
$simple = @('decode_profile','block_scale_probe','image_zoom_probe','scale_policy_probe',
            'upload_layout_probe','adaptive_scale_probe','read_pixel_probe','rotation_probe',
            'hdrpeak_probe','copyframe_probe','occlusion_probe','shot_probe','vsr_probe',
            'thread_ladder_probe',
            # These were missing from the list and so kept failing with a stale struct
            # long after the header changed -- the same trap the delete-first fix above
            # exists to prevent, one level up.
            'frame_pool_probe','backend_probe','loop_stutter_probe','apng_seek_repro','audio_buffer_probe',
            'audio_drift_probe',
            'vsr_pixel_probe','vsr_scrgb_probe','pan_direction_probe','zoom_anchor_probe')
$iamf = @('iamf_layout_regression','iamf_playback_probe','iamf_probe','iamf_channel_order_probe')

function Invoke-Build([string]$name, [string]$extra) {
    $src = Join-Path $tools ($name + '.cpp')
    if (-not (Test-Path $src)) { return 'skip' }
    $exe = Join-Path $out ($name + '.exe')
    # 先删目标：编译失败时残留的旧二进制会让 Test-Path 误判为成功，
    # 从而发布一个用旧结构体编译的探针（这正是之前排查困难的根源）。
    Remove-Item $exe -Force -ErrorAction SilentlyContinue
    $batPath = Join-Path $out 'rb_one.bat'
    $libs = if ($extraLibs.ContainsKey($name)) { "user32.lib " + $extraLibs[$name] } else { "user32.lib" }
    $lines = @(
        '@echo off',
        'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul',
        "cd /d `"$root`"",
        "cl /nologo /EHsc /std:c++20 /O2 /I `"$kernel`" $extra /Fe:`"$exe`" /Fo:`"$out\\`" `"$src`" `"$lib`" $libs"
    )
    Set-Content -Path $batPath -Value ($lines -join "`r`n") -Encoding ASCII
    $log = cmd /c "`"$batPath`" 2>&1"
    Remove-Item $batPath -Force -ErrorAction SilentlyContinue
    if (Test-Path $exe) {
        Copy-Item $exe (Join-Path $root ("build\" + $name + ".exe")) -Force
        Write-Host "  OK   $name"
        return 'ok'
    }
    Write-Host "  FAIL $name"
    $log | Select-String 'error' | Select-Object -First 2 | ForEach-Object { Write-Host "       $($_.Line.Trim())" }
    return 'fail'
}

$ok = 0; $failed = @()
foreach ($name in $simple) {
    $extra = if ($extraLibs.ContainsKey($name)) { "/link $($extraLibs[$name])" } else { '' }
    # /link 必须在对象/库之前，这里改用简单方式：直接把库追加在最后
    $extra = ''
    $r = Invoke-Build $name $extra
    if ($r -eq 'ok') { $ok++ } elseif ($r -eq 'fail') { $failed += $name }
}
foreach ($name in $iamf) {
    $extra = "/I `"$libiamfInclude`" /I `"$libiamfOar`" /DFFF_WITH_LIBIAMF=1"
    $r = Invoke-Build $name $extra
    if ($r -eq 'ok') { $ok++ } elseif ($r -eq 'fail') { $failed += $name }
}
Write-Host "`n成功 $ok，失败 $($failed.Count)"
if ($failed.Count) { Write-Host ("失败项: " + ($failed -join ', ')) }
