# 3FCompare —— Shared FFmpeg 能力面探测
#
# 用途：直接 P/Invoke runtime/ 下的 avcodec / avformat，枚举**实际可用**的视频解码器与解复用器，
#       用于核对《3FP 图片查看功能可行性分析》§5 的格式覆盖结论（不依赖 ffmpeg.exe / ffprobe.exe）。
#
# 为什么需要它：3FP 允许用户自行更换 FFmpeg 核心，图片格式覆盖取决于用户放的 DLL，
#       不能靠"我记得 BtbN 构建里有什么"来下结论，必须探测真实 DLL。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools/探测FFmpeg能力.ps1
#   powershell -ExecutionPolicy Bypass -File tools/探测FFmpeg能力.ps1 -RuntimeDir C:\path\to\ffmpeg\dll
#   powershell -ExecutionPolicy Bypass -File tools/探测FFmpeg能力.ps1 -Full   # 输出全部解码器/解复用器
#
# 退出码：0 = 探测成功；1 = DLL 加载/探测失败（此时报告不得据此下结论）

param(
    [string]$RuntimeDir = (Join-Path $PSScriptRoot "..\runtime"),
    [switch]$Full
)

$ErrorActionPreference = "Stop"
$RuntimeDir = [IO.Path]::GetFullPath($RuntimeDir)

if (-not (Test-Path (Join-Path $RuntimeDir "avcodec-63.dll"))) {
    Write-Error "未找到 avcodec-63.dll：$RuntimeDir。请用 -RuntimeDir 指定 Shared FFmpeg 的 DLL 目录。"
    exit 1
}

# --- SetDllDirectory：让后续 DllImport("avcodec-63.dll") 能在本目录解析到 ---
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FfmpegProbeNative {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);
    public static void SetDirectory(string dir) {
        if (!SetDllDirectory(dir)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr av_version_info();

    [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr av_codec_iterate(ref IntPtr opaque);
    [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int av_codec_is_decoder(IntPtr codec);

    [DllImport("avformat-63.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr av_demuxer_iterate(ref IntPtr opaque);

    // AVCodec:   +0 const char* name; +8 const char* long_name; +16 enum AVMediaType type; +20 enum AVCodecID id
    // AVInputFormat: +0 const char* name; +8 const char* long_name; +16 int flags; +24 const char* extensions
    public static string Str(IntPtr p, int offset) {
        var s = Marshal.ReadIntPtr(p, offset);
        return s == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(s) ?? "");
    }
    public static int I32(IntPtr p, int offset) { return Marshal.ReadInt32(p, offset); }
}
'@

[FfmpegProbeNative]::SetDirectory($RuntimeDir)

# --- 版本 ---
$versionPtr = [FfmpegProbeNative]::av_version_info()
$version = if ($versionPtr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::PtrToStringAnsi($versionPtr) } else { "(未知)" }
Write-Host "FFmpeg 构建版本: $version"
Write-Host "DLL 目录: $RuntimeDir"
Write-Host ""

# --- 枚举解码器 ---
$decoders = @{}
$opaque = [IntPtr]::Zero
while ($true) {
    $p = [FfmpegProbeNative]::av_codec_iterate([ref]$opaque)
    if ($p -eq [IntPtr]::Zero) { break }
    if ([FfmpegProbeNative]::av_codec_is_decoder($p) -eq 0) { continue }
    $type = [FfmpegProbeNative]::I32($p, 16)   # 0 = AVMEDIA_TYPE_VIDEO
    if ($type -ne 0) { continue }
    $name = [FfmpegProbeNative]::Str($p, 0)
    $id   = [FfmpegProbeNative]::I32($p, 20)
    if ($name) { $decoders[$name] = $id }
}

# --- 枚举解复用器 ---
$demuxers = @{}
$opaque = [IntPtr]::Zero
while ($true) {
    $p = [FfmpegProbeNative]::av_demuxer_iterate([ref]$opaque)
    if ($p -eq [IntPtr]::Zero) { break }
    $name = [FfmpegProbeNative]::Str($p, 0)
    $ext  = [FfmpegProbeNative]::Str($p, 24)
    if ($name) { $demuxers[$name] = $ext }
}

Write-Host "视频解码器总数: $($decoders.Count)    解复用器总数: $($demuxers.Count)"
Write-Host ""

if ($Full) {
    Write-Host "--- 全部视频解码器 ---"
    $decoders.Keys | Sort-Object | ForEach-Object { Write-Host "  $_" }
    Write-Host "--- 全部解复用器 ---"
    $demuxers.Keys | Sort-Object | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    exit 0
}

# --- 图片相关能力对照 ---
$watchDecoders = @(
    'png', 'apng', 'mjpeg', 'bmp', 'gif', 'webp', 'tiff',
    'libjxl', 'libjxl_anim', 'jpegxl', 'targa', 'tga',
    'hevc', 'av1', 'rawvideo', 'exr', 'ppm', 'dpx', 'pcx',
    'dcraw', 'svg', 'alias_pix', 'sunrast', 'sgi', 'qdraw', 'rasc', 'fits'
)
$watchDemuxers = @(
    'image2', 'image2pipe', 'image2_alias_pix', 'image2_brand_pix',
    'gif', 'apng', 'webp', 'webp_anim', 'jpegxl_anim', 'ico', 'dcraw',
    'mov', 'avif', 'heic', 'movie', 'rawvideo', 'svg_pipe', 'tiff_pipe',
    'png_pipe', 'jpeg_pipe', 'webp_pipe', 'jpegls_pipe', 'bmp_pipe', 'exr_pipe'
)

Write-Host "=== 图片相关解码器 ==="
foreach ($d in $watchDecoders) {
    $hit = $decoders.ContainsKey($d)
    $mark = if ($hit) { "有" } else { "--" }
    Write-Host ("  [{0,-2}] {1}" -f $mark, $d)
}

Write-Host ""
Write-Host "=== 图片相关解复用器 ==="
foreach ($d in $watchDemuxers) {
    $hit = $demuxers.ContainsKey($d)
    $mark = if ($hit) { "有" } else { "--" }
    $ext = if ($hit) { $demuxers[$d] } else { "" }
    Write-Host ("  [{0,-2}] {1,-18} {2}" -f $mark, $d, $ext)
}

# 解复用器的注册名可能是复合名（如 mov 的真实名称是 "mov,mp4,m4a,3gp,3g2,mj2"），
# 必须按逗号拆开后逐段匹配，否则会误判为"不支持"。
function Test-Demuxer([string]$name) {
    foreach ($k in $demuxers.Keys) {
        foreach ($alias in ($k -split ',')) { if ($alias.Trim() -eq $name) { return $true } }
    }
    return $false
}

Write-Host ""
Write-Host "=== 关键词模糊命中（用于发现命名差异，如 mov 的复合名）==="
foreach ($k in @('jxl', 'jpegxl', 'avif', 'heic', 'mov', 'mp4', 'webp', 'dcraw', 'svg')) {
    $hits = @()
    $hits += $decoders.Keys | Where-Object { $_ -match $k } | ForEach-Object { "dec:$_" }
    $hits += $demuxers.Keys | Where-Object { $_ -match $k } | ForEach-Object { "demux:$_" }
    if ($hits.Count -gt 0) { Write-Host ("  {0,-8} -> {1}" -f $k, ($hits -join ', ')) }
    else { Write-Host ("  {0,-8} -> （无）" -f $k) }
}

Write-Host ""
Write-Host "=== 关键判定 ==="
$judge = @(
    @{ Name = 'PNG（含 APNG）';      Ok = ($decoders.ContainsKey('png') -and (Test-Demuxer 'image2')) },
    @{ Name = 'JPEG';                Ok = ($decoders.ContainsKey('mjpeg') -and (Test-Demuxer 'image2')) },
    @{ Name = 'WebP（含动画）';      Ok = ($decoders.ContainsKey('webp') -and ((Test-Demuxer 'webp') -or (Test-Demuxer 'image2'))) },
    @{ Name = 'GIF';                 Ok = ($decoders.ContainsKey('gif') -and (Test-Demuxer 'gif')) },
    @{ Name = 'TIFF';                Ok = ($decoders.ContainsKey('tiff') -and (Test-Demuxer 'image2')) },
    @{ Name = 'BMP / ICO';           Ok = ($decoders.ContainsKey('bmp') -and (Test-Demuxer 'ico')) },
    @{ Name = 'JPEG XL';             Ok = ($decoders.ContainsKey('libjxl') -or $decoders.ContainsKey('jpegxl')) },
    @{ Name = 'JPEG XL 动画';        Ok = (Test-Demuxer 'jpegxl_anim') },
    @{ Name = 'AVIF（AV1 + mov/avif）'; Ok = ($decoders.ContainsKey('av1') -and ((Test-Demuxer 'avif') -or (Test-Demuxer 'mov'))) },
    @{ Name = 'HEIC（HEVC + mov）';  Ok = ($decoders.ContainsKey('hevc') -and (Test-Demuxer 'mov')) },
    @{ Name = '相机 RAW（dcraw）';   Ok = (Test-Demuxer 'dcraw') },
    @{ Name = 'EXR / HDR 扫描图';    Ok = ($decoders.ContainsKey('exr')) },
    @{ Name = 'SVG';                 Ok = ($decoders.ContainsKey('svg')) }
)
foreach ($j in $judge) {
    Write-Host ("  [{0,-2}] {1}" -f $(if ($j.Ok) { "OK" } else { "×" }), $j.Name)
}

# --- GainMap 佐证：ffmpeg 是否暴露任何 gain map 相关组件 ---
Write-Host ""
Write-Host "=== GainMap 佐证（名称含 gain / hdr 的解码器与解复用器）==="
$gainHits = @()
$gainHits += $decoders.Keys  | Where-Object { $_ -match 'gain|gainmap' }
$gainHits += $demuxers.Keys  | Where-Object { $_ -match 'gain|gainmap' }
if ($gainHits.Count -eq 0) {
    Write-Host "  未发现任何名称含 gain/gainmap 的解码器或解复用器"
} else {
    $gainHits | ForEach-Object { Write-Host "  命中: $_" }
}

# --- 对 3FP 静态图判定的影响 ---
# 3FP 的 IsStaticImageDemuxer()（PlayerSession.cpp:322-329）只认 demuxer 名 == image2 或 *_pipe。
# 因此"由哪个 demuxer 承载"直接决定该文件会不会走静态图分支。
Write-Host ""
Write-Host "=== 对 3FP IsStaticImageDemuxer 的影响（只认 image2 / *_pipe）==="
$hasAvifDemuxer = (Test-Demuxer 'avif') -or (Test-Demuxer 'heic')
if ($hasAvifDemuxer) {
    Write-Host "  存在独立 avif/heic 解复用器 ⇒ AVIF/HEIC 由 *_pipe 类承载，可能命中静态图判定"
} else {
    Write-Host "  ⚠ 无独立 avif/heic 解复用器 ⇒ AVIF/HEIC 由 mov 承载（注册名 mov,mp4,m4a,3gp,3g2,mj2）"
    Write-Host "    IsStaticImageDemuxer 只匹配 image2 与 *_pipe ⇒ **AVIF/HEIC 不会被判为静态图**"
    Write-Host "    后果：不走 DecodeInitialStillImage、无 stillImageFrame_ 常驻、可能尝试硬件解码、单帧后立即 Ended"
    Write-Host "    这正是报告 K3（改用 nb_frames<=1 判定）要一并解决的问题"
}
foreach ($d in @('jpegxl_pipe', 'jpegxl_anim', 'webp_pipe', 'webp_anim', 'png_pipe', 'tiff_pipe')) {
    if (Test-Demuxer $d) { Write-Host ("  可用 *_pipe/anim 解复用器: {0}" -f $d) }
}

Write-Host ""
Write-Host "说明：本脚本只探测 FFmpeg 组件是否注册，**不验证**解码后是否填充 color_trc/primaries，"
Write-Host "      也不验证 gain map 是否作为第二路流暴露 —— 那需要有样张跑解码流程（见报告 §11 Q5/Q8）。"
exit 0
