# content_probe.ps1 - does the *picture content* actually reach the screen?
#
# Why this exists: the T2 criterion in startup_image_time.ps1 (screen-DC grayscale
# stddev >= 8, 2 consecutive polls) was proven NOT to measure content:
#   1. causal order - host instrumentation puts Form1_Shown 231.6 ms AFTER probe T2,
#      and the open chain can only start from the BeginInvoke inside Shown.
#   2. negative control - launching with no image still fires T2 at 986.3 ms
#      (with image: 1020.6 ms; difference within noise).
# This tool decides "content is on screen" by comparing the window's CLIENT AREA,
# captured via PrintWindow(PW_RENDERFULLCONTENT), against a reference frame captured
# from the same fixture at steady state, using per-pixel mean absolute difference (MAD).
#
# It also samples the legacy screen-DC criterion in the SAME poll, so at the exact
# moment legacy T2 fires we can print both readings side by side and settle whether the
# old sampler reads the wrong region (PrintWindow ~200 vs screen DC ~7) or the threshold
# itself is meaningless.
#
# DPI: this process calls SetProcessDPIAware() before any coordinate math. Without it
# GetWindowRect lies in 96dpi virtualized space (1.5x here: 3840x2160 reported as
# 2560x1440) and PrintWindow only paints the top-left block. (tools/WinUi.cs:210-234.)
#
# Modes:
#   reference   : -Exe <app> -Image <fixture> -SaveRef <png> [-SettleSec 6]
#   measurement : -Exe <app> -Image <fixture|''> -RefPng <png> [-MadTol 6.0]
#                 [-TimeoutSec 12] [-PollMs 8] [-Runs N] [-CsvOut <csv>] [-PollLog <csv>]
#
# Usage (typical, red fixture):
#   powershell -NoProfile -ExecutionPolicy Bypass -File content_probe.ps1 `
#     -Exe <FFF.Player.exe> -Image <sample_red_1920.png> -SaveRef <ref.png>
#   powershell ... -Exe ... -Image <sample_red_1920.png> -RefPng <ref.png> -CsvOut <csv>
#
# Postconditions: every round ends with Kill + 1.2 s settle (host is single instance;
# a leftover process makes the next round fail silently).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Image = '',
    [string]$SaveRef = '',
    [string]$RefPng = '',
    [int]$SettleSec = 6,
    [double]$MadTol = 6.0,
    [int]$TimeoutSec = 12,
    [int]$PollMs = 8,
    [int]$Runs = 1,
    [double]$LegacyStdDevMin = 8.0,
    [string]$CsvOut = '',
    [string]$PollLog = '',
    [string]$DiagPng = '',
    [string]$Tag = 'probe'
)
$ErrorActionPreference = 'Stop'

if (($SaveRef -eq '') -and ($RefPng -eq '')) { throw 'give either -SaveRef <png> (reference mode) or -RefPng <png> (measurement mode)' }
if (($SaveRef -ne '') -and ($RefPng -ne '')) { throw '-SaveRef and -RefPng are exclusive' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "exe not found: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if ($Image -ne '' -and -not (Test-Path -LiteralPath $Image)) { throw "image not found: $Image" }
if ($Image -ne '') { $Image = (Resolve-Path -LiteralPath $Image).Path }

# --- Win32 + GDI+ in one Add-Type (no build step needed) ------------------------------
Add-Type -AssemblyName System.Drawing
# Size/Point/Rectangle live in System.Drawing.Primitives (forwarded types): referencing
# only System.Drawing makes Add-Type fail with CS1069 on the screen-grab helper.
$dotnetDrawing = @(([System.Drawing.Bitmap]).Assembly.Location,
                   ([System.Drawing.Size]).Assembly.Location)
if (-not ('CProbe.Native' -as [type])) {
    $src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;

namespace CProbe {
    public static class Native {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll", SetLastError = true)] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);
        const int SRCCOPY = 0x00CC0020;
        const uint PW_RENDERFULLCONTENT = 2;

        public static string LastError = "";

        // First visible top-level window of the pid that has a title: hwnd + class + title.
        public static bool FindTitledWindow(uint wantedPid, out IntPtr hwnd, out string cls, out string title) {
            hwnd = IntPtr.Zero; cls = ""; title = "";
            IntPtr found = IntPtr.Zero; string fCls = "", fTitle = "";
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != wantedPid) return true;
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var t = new StringBuilder(len + 2); GetWindowTextW(h, t, t.Capacity);
                if (t.Length == 0) return true;
                var c = new StringBuilder(256); GetClassNameW(h, c, 256);
                found = h; fCls = c.ToString(); fTitle = t.ToString();
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            hwnd = found; cls = fCls; title = fTitle; return true;
        }

        // ---- legacy path (verbatim criterion from startup_image_time.ps1): screen DC +
        // ---- StretchBlt of the client rect, grayscale stddev over a coarse grid.
        public static bool SampleClientPixels(IntPtr hwnd, int step, out double mean, out double stdDev) {
            mean = 0; stdDev = 0;
            RECT rc;
            if (!GetClientRect(hwnd, out rc)) return false;
            int w = rc.R - rc.L, h = rc.B - rc.T;
            if (w < 160 || h < 120) return false;
            int cw = Math.Min(w, 640), ch = Math.Min(h, 400);
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr bmp = CreateCompatibleBitmap(screenDc, cw, ch);
            IntPtr old = SelectObject(memDc, bmp);
            bool ok = false;
            try {
                if (!StretchBlt(memDc, 0, 0, cw, ch, screenDc, pt.X, pt.Y, w, h, SRCCOPY)) return false;
                var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = cw, biHeight = -ch, biPlanes = 1, biBitCount = 32, biCompression = 0 } };
                var bytes = new byte[cw * ch * 4];
                IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
                try {
                    if (GetDIBits(memDc, bmp, 0, (uint)ch, ptr, ref bmi, 0) == 0) return false;
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                } finally { Marshal.FreeHGlobal(ptr); }
                long sum = 0, sum2 = 0, n = 0;
                for (int y = 0; y < ch; y += step) {
                    int row = y * cw * 4;
                    for (int x = 0; x < cw; x += step) {
                        int i = row + x * 4;
                        int g = (bytes[i] * 299 + bytes[i + 1] * 587 + bytes[i + 2] * 114) / 1000;
                        sum += g; sum2 += (long)g * g; n++;
                    }
                }
                if (n < 100) return false;
                mean = (double)sum / n;
                double v = (double)sum2 / n - mean * mean;
                stdDev = v > 0 ? Math.Sqrt(v) : 0;
                ok = true;
            } finally {
                SelectObject(memDc, old);
                DeleteObject(bmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
            }
            return ok;
        }

        // ---- PrintWindow path: full-window 32bppArgb DIB, client crop into caller buffer.
        static IntPtr _scrDc = IntPtr.Zero, _mem = IntPtr.Zero, _bmp = IntPtr.Zero, _old = IntPtr.Zero, _bits = IntPtr.Zero;
        static int _bw = -1, _bh = -1;
        static BITMAPINFO _bmi;
        static byte[] _full;

        static void ReleaseTarget() {
            if (_bits != IntPtr.Zero) { Marshal.FreeHGlobal(_bits); _bits = IntPtr.Zero; }
            if (_bmp != IntPtr.Zero) { DeleteObject(_bmp); _bmp = IntPtr.Zero; }
            if (_mem != IntPtr.Zero) { DeleteDC(_mem); _mem = IntPtr.Zero; }
            if (_scrDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _scrDc); _scrDc = IntPtr.Zero; }
            _bw = -1; _bh = -1; _full = null;
        }

        // per-stage cost of the last CaptureClient call (ms) - documents poll granularity
        public static double TPrint, TDib, TCopy;

        public static void WindowRect(IntPtr h, out int l, out int t, out int r, out int b) {
            RECT wr; l = t = r = b = 0;
            if (GetWindowRect(h, out wr)) { l = wr.L; t = wr.T; r = wr.R; b = wr.B; }
        }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        const int GA_ROOT = 3;

        // Which window actually owns the pixels at a screen point (top-most in z-order there)?
        // Distinguishes "the screen region is covered by another window" from "this window's
        // image plane is simply invisible to a GDI screen capture".
        public static void WindowAtPoint(int x, int y, out string cls, out string title, out uint pid, out IntPtr root) {
            cls = ""; title = ""; pid = 0; root = IntPtr.Zero;
            IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
            if (h == IntPtr.Zero) return;
            root = GetAncestor(h, GA_ROOT);
            uint p; GetWindowThreadProcessId(root, out p); pid = p;
            var c = new StringBuilder(256); GetClassNameW(root, c, 256); cls = c.ToString();
            int len = GetWindowTextLength(root);
            if (len > 0) { var t = new StringBuilder(len + 2); GetWindowTextW(root, t, t.Capacity); title = t.ToString(); }
        }

        // Real screen pixels under the client rect, via Graphics.CopyFromScreen.
        // A third, independent capture path: if this agrees with PrintWindow but the
        // legacy StretchBlt sampler does not, the legacy sampler reads the wrong place.
        // status: 0 ok, -1 grab failed, -2 geometry, -3 dst too small.
        public static int GrabScreenRect(IntPtr hwnd, byte[] dst, out int w, out int h, out int sx, out int sy) {
            w = 0; h = 0; sx = 0; sy = 0;
            RECT cr; if (!GetClientRect(hwnd, out cr)) { LastError = "GetClientRect"; return -2; }
            w = cr.R - cr.L; h = cr.B - cr.T;
            if (w < 8 || h < 8) { LastError = "client-size " + w + "x" + h; return -2; }
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) { LastError = "ClientToScreen"; return -2; }
            sx = pt.X; sy = pt.Y;
            if (dst == null || dst.Length < w * h * 4) { LastError = "dst-too-small"; return -3; }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var gfx = Graphics.FromImage(bmp);
            try {
                gfx.CopyFromScreen(sx, sy, 0, 0, new Size(w, h));
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), dst, y * w * 4, w * 4);
                bmp.UnlockBits(bd);
                return 0;
            } catch (Exception e) { LastError = "CopyFromScreen(" + e.GetType().Name + ")"; return -1; }
            finally { gfx.Dispose(); bmp.Dispose(); }
        }

        static bool EnsureTarget(int w, int h) {
            if (_bw == w && _bh == h && _bits != IntPtr.Zero) return true;
            ReleaseTarget();
            _scrDc = GetDC(IntPtr.Zero);
            _mem = CreateCompatibleDC(_scrDc);
            _bmp = CreateCompatibleBitmap(_scrDc, w, h);
            _old = SelectObject(_mem, _bmp);
            _bmi = new BITMAPINFO();
            _bmi.bmiHeader.biSize = 40; _bmi.bmiHeader.biWidth = w; _bmi.bmiHeader.biHeight = -h;
            _bmi.bmiHeader.biPlanes = 1; _bmi.bmiHeader.biBitCount = 32; _bmi.bmiHeader.biCompression = 0;
            _bits = Marshal.AllocHGlobal(w * h * 4);
            _full = new byte[w * h * 4];
            _bw = w; _bh = h;
            return true;
        }

        // status: 0 ok, -1 PrintWindow failed, -2 geometry failed, -3 dst too small
        // (out dims are still filled so the caller can resize its buffer), -4 GetDIBits failed.
        // dst receives the CLIENT crop, BGRA, alpha forced to 255.
        public static int CaptureClient(IntPtr hwnd, byte[] dst,
                out int cliW, out int cliH, out int winW, out int winH, out int offX, out int offY) {
            cliW = 0; cliH = 0; winW = 0; winH = 0; offX = 0; offY = 0;
            LastError = "";
            RECT wr; if (!GetWindowRect(hwnd, out wr)) { LastError = "GetWindowRect"; return -2; }
            winW = wr.R - wr.L; winH = wr.B - wr.T;
            if (winW < 8 || winH < 8) { LastError = "winrect-size " + winW + "x" + winH; return -2; }
            RECT cr; if (!GetClientRect(hwnd, out cr)) { LastError = "GetClientRect"; return -2; }
            cliW = cr.R - cr.L; cliH = cr.B - cr.T;
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) { LastError = "ClientToScreen"; return -2; }
            offX = pt.X - wr.L; offY = pt.Y - wr.T;
            if (cliW < 8 || cliH < 8) { LastError = "client-size " + cliW + "x" + cliH; return -2; }
            if (offX < 0 || offY < 0 || offX + cliW > winW || offY + cliH > winH) {
                // clamp into the window bitmap (DWM borders can push the client outside)
                if (offX < 0) { cliW += offX; offX = 0; }
                if (offY < 0) { cliH += offY; offY = 0; }
                if (offX + cliW > winW) cliW = winW - offX;
                if (offY + cliH > winH) cliH = winH - offY;
                if (cliW < 8 || cliH < 8) { LastError = "clamped-size " + cliW + "x" + cliH; return -2; }
            }
            if (dst == null || dst.Length < cliW * cliH * 4) { LastError = "dst-too-small"; return -3; }
            if (!EnsureTarget(winW, winH)) { LastError = "EnsureTarget"; return -2; }
            var t0 = DateTime.UtcNow;
            if (!PrintWindow(hwnd, _mem, PW_RENDERFULLCONTENT)) {
                LastError = "PrintWindow(err=" + Marshal.GetLastWin32Error() + ")"; return -1;
            }
            var t1 = DateTime.UtcNow;
            if (GetDIBits(_mem, _bmp, 0, (uint)winH, _bits, ref _bmi, 0) == 0) {
                LastError = "GetDIBits(err=" + Marshal.GetLastWin32Error() + ")"; return -4;
            }
            var t2 = DateTime.UtcNow;
            Marshal.Copy(_bits, _full, 0, winW * winH * 4);
            for (int y = 0; y < cliH; y++) {
                int srow = (offY + y) * winW * 4 + offX * 4;
                int drow = y * cliW * 4;
                for (int x = 0; x < cliW; x++) {
                    int s = srow + x * 4, d = drow + x * 4;
                    dst[d] = _full[s]; dst[d + 1] = _full[s + 1]; dst[d + 2] = _full[s + 2];
                    dst[d + 3] = 255;
                }
            }
            TPrint = (t1 - t0).TotalMilliseconds; TDib = (t2 - t1).TotalMilliseconds;
            TCopy = (DateTime.UtcNow - t2).TotalMilliseconds;
            return 0;
        }

        // BGRA buffer -> R/G/B means + grayscale mean/stddev (same 299/587/114 weights as legacy).
        public static void Stats(byte[] px, int w, int h,
                out double rMean, out double gMean, out double bMean, out double grayMean, out double grayStd) {
            rMean = gMean = bMean = grayMean = grayStd = 0;
            long n = (long)w * h; if (n <= 0) return;
            long sr = 0, sg = 0, sb = 0, sY = 0; double sY2 = 0;
            int lim = (int)n * 4;
            for (int i = 0; i + 3 < lim; i += 4) {
                int b = px[i], g = px[i + 1], r = px[i + 2];
                sr += r; sg += g; sb += b;
                int y = (r * 299 + g * 587 + b * 114) / 1000;
                sY += y; sY2 += (double)y * y;
            }
            rMean = (double)sr / n; gMean = (double)sg / n; bMean = (double)sb / n;
            grayMean = (double)sY / n;
            double v = sY2 / n - grayMean * grayMean;
            grayStd = v > 0 ? Math.Sqrt(v) : 0;
        }

        // Per-channel mean absolute difference, plus the share of pixels whose per-pixel
        // mean channel diff exceeds 8. -1 when sizes differ (caller must mark the poll invalid).
        public static double Mad(byte[] a, byte[] b, int w, int h, out double diffFrac) {
            diffFrac = -1;
            long n = (long)w * h; if (n <= 0) return -1;
            long sum = 0; long over = 0;
            int lim = (int)n * 4;
            for (int i = 0; i + 3 < lim; i += 4) {
                int d = Math.Abs((int)a[i] - (int)b[i]) + Math.Abs((int)a[i + 1] - (int)b[i + 1]) + Math.Abs((int)a[i + 2] - (int)b[i + 2]);
                sum += d;
                if (d > 24) over++;   // per-pixel mean channel diff > 8
            }
            diffFrac = (double)over / n;
            return (double)sum / (n * 3);
        }

        public static bool SavePng(byte[] px, int w, int h, string path) {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, IntPtr.Add(bd.Scan0, y * bd.Stride), w * 4);
                bmp.UnlockBits(bd);
                bmp.Save(path, ImageFormat.Png);
                return true;
            } catch { return false; } finally { bmp.Dispose(); ReleaseTarget(); }
        }

        public static bool LoadPng(string path, out int w, out int h, out byte[] px) {
            w = 0; h = 0; px = null;
            var bmp = new Bitmap(path);
            try {
                w = bmp.Width; h = bmp.Height;
                if (w < 8 || h < 8) return false;
                px = new byte[w * h * 4];
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), px, y * w * 4, w * 4);
                bmp.UnlockBits(bd);
                return true;
            } finally { bmp.Dispose(); }
        }
    }
}
'@
    Add-Type -TypeDefinition $src -ReferencedAssemblies $dotnetDrawing | Out-Null
}
# Must run before any coordinate math, or GetWindowRect reports the 96dpi-virtualized
# rect (1.5x here) and PrintWindow paints only the top-left block.
[CProbe.Native]::SetProcessDPIAware() | Out-Null

# --- reference frame geometry (measurement mode) -------------------------------------
$refPx = $null; $refW = 0; $refH = 0
if ($RefPng -ne '') {
    if (-not (Test-Path -LiteralPath $RefPng)) { throw "ref png not found: $RefPng" }
    $RefPng = (Resolve-Path -LiteralPath $RefPng).Path
    if (-not [CProbe.Native]::LoadPng($RefPng, [ref]$refW, [ref]$refH, [ref]$refPx)) { throw "LoadPng failed: $RefPng" }
    $rr = 0.0; $rg = 0.0; $rb = 0.0; $ry = 0.0; $rs = 0.0
    [CProbe.Native]::Stats($refPx, $refW, $refH, [ref]$rr, [ref]$rg, [ref]$rb, [ref]$ry, [ref]$rs)
    Write-Host ("ref loaded: {0} {1}x{2} R={3:N1} G={4:N1} B={5:N1} gray={6:N1}/{7:N1}" -f $RefPng, $refW, $refH, $rr, $rg, $rb, $ry, $rs)
}

$pollRows = New-Object System.Collections.Generic.List[object]

function ZOrderEvidence($hwnd, $cx, $cy) {
    $zc = ''; $zt = ''; $zp = [uint32]0; $zr = [IntPtr]::Zero
    [CProbe.Native]::WindowAtPoint($cx, $cy, [ref]$zc, [ref]$zt, [ref]$zp, [ref]$zr)
    return ("top-window-at({0},{1}) hwnd={2} pid={3} class={4} title={5} sameAsTarget={6}" -f `
        $cx, $cy, $zr.ToInt64(), $zp, $zc, $zt, ($zr -eq $hwnd))
}

function Start-Host {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.Arguments = if ($Image -ne '') { '"' + $Image + '"' } else { '' }
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = (Split-Path -Parent $Exe)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    return @{ Proc = $p; Sw = $sw; Start = [DateTime]::UtcNow }
}

function Stop-Host($p) {
    try { if ($null -ne $p -and -not $p.HasExited) { $p.Kill() } } catch { }
    try { if ($null -ne $p) { $p.WaitForExit(5000) | Out-Null } } catch { }
    Start-Sleep -Milliseconds 1200
}

function Wait-T1($h) {
    $script:cls = ''
    $script:title = ''
    $script:t1Ms = -1.0
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($h.Proc.HasExited) { return $null }
        [IntPtr]$hw = [IntPtr]::Zero
        if ([CProbe.Native]::FindTitledWindow([uint32]$h.Proc.Id, [ref]$hw, [ref]$script:cls, [ref]$script:title)) {
            $script:t1Ms = [math]::Round($h.Sw.Elapsed.TotalMilliseconds, 1)
            return $hw
        }
        Start-Sleep -Milliseconds 4
    }
    return $null
}

# ============================ reference mode ==========================================
if ($SaveRef -ne '') {
    $existing = Get-Process FFF.Player -ErrorAction SilentlyContinue
    if ($existing) { throw "FFF.Player already running (pid $($existing.Id -join ',')) - kill it first (single instance)." }
    $h = Start-Host
    try {
        $hwnd = Wait-T1 $h
        if ($null -eq $hwnd) { throw 'reference mode: no titled window within timeout' }
        $stay = $SettleSec - $h.Sw.Elapsed.TotalSeconds
        if ($stay -gt 0) { Start-Sleep -Milliseconds ([int]($stay * 1000)) }
        # two-step: the first call only reports the client geometry (status -3), then we
        # allocate exactly that buffer.
        $dst = New-Object byte[] 4096
        $cw = 0; $ch = 0; $ww = 0; $wh = 0; $ox = 0; $oy = 0
        $st = [CProbe.Native]::CaptureClient($hwnd, $dst, [ref]$cw, [ref]$ch, [ref]$ww, [ref]$wh, [ref]$ox, [ref]$oy)
        if ($st -eq -3) {
            $dst = New-Object byte[] ($cw * $ch * 4)
            $st = [CProbe.Native]::CaptureClient($hwnd, $dst, [ref]$cw, [ref]$ch, [ref]$ww, [ref]$wh, [ref]$ox, [ref]$oy)
        }
        if ($st -ne 0) { throw "CaptureClient failed status=$st err=$([CProbe.Native]::LastError)" }
        $r = 0.0; $g = 0.0; $b = 0.0; $y = 0.0; $s = 0.0
        [CProbe.Native]::Stats($dst, $cw, $ch, [ref]$r, [ref]$g, [ref]$b, [ref]$y, [ref]$s)
        $lm = 0.0; $ls = 0.0
        $lok = [CProbe.Native]::SampleClientPixels($hwnd, 3, [ref]$lm, [ref]$ls)
        if (-not [CProbe.Native]::SavePng($dst, $cw, $ch, $SaveRef)) { throw "SavePng failed: $SaveRef" }
        # Same-moment cross-check at steady state: what does a real screen grab of this
        # client rect say, and is this window really the top-most one there?
        $sdst = New-Object byte[] ($cw * $ch * 4)
        $sw2 = 0; $sh2 = 0; $sx = 0; $sy = 0
        $sst = [CProbe.Native]::GrabScreenRect($hwnd, $sdst, [ref]$sw2, [ref]$sh2, [ref]$sx, [ref]$sy)
        $scrLine = 'GrabScreenRect-failed'
        $ff = 0.0
        if ($sst -eq 0) {
            $sr = 0.0; $sg = 0.0; $sb = 0.0; $sy2 = 0.0; $ss2 = 0.0
            [CProbe.Native]::Stats($sdst, $sw2, $sh2, [ref]$sr, [ref]$sg, [ref]$sb, [ref]$sy2, [ref]$ss2)
            $scrLine = ("SCREEN-copy R={0:N1}/G={1:N1}/B={2:N1} gray={3:N1}/{4:N1}" -f $sr, $sg, $sb, $sy2, $ss2)
            if ($sw2 -eq $cw -and $sh2 -eq $ch) {
                $sm = [CProbe.Native]::Mad($sdst, $dst, $sw2, $sh2, [ref]$ff)
                $scrLine += (" MAD-vs-PW={0}" -f [math]::Round($sm, 2))
            }
        }
        Write-Host ("REF-XCHECK client={0}x{1}@{2},{3} {4} | {5}" -f $sw2, $sh2, $sx, $sy, $scrLine, (ZOrderEvidence $hwnd ($sx + [int]($sw2 / 2)) ($sy + [int]($sh2 / 2))))
        Write-Host ("REF tag={0} settle={1}s class={2} win={3}x{4} client={5}x{6} off={7},{8} PW:R={9:N1}/G={10:N1}/B={11:N1} gray={12:N1}/{13:N1} legacyDC(mean/std)={14}/{15} path={16}" -f `
            $Tag, $SettleSec, $script:cls, $ww, $wh, $cw, $ch, $ox, $oy, $r, $g, $b, $y, $s, $lm, $ls, $SaveRef)
    } finally { Stop-Host $h.Proc }
    return
}

# ============================ measurement mode ========================================
$results = @()
for ($round = 1; $round -le $Runs; $round++) {
    $existing = Get-Process FFF.Player -ErrorAction SilentlyContinue
    if ($existing) { Write-Warning "FFF.Player already running (pid $($existing.Id -join ',')) - killing (single instance)."; $existing | Stop-Process -Force; Start-Sleep -Milliseconds 1200 }
    $h = Start-Host
    $procId = $h.Proc.Id
    $t1 = -1.0; $tLegacy = -1.0; $tContent = -1.0; $tContentFirst = -1.0
    $legacyStreak = 0; $contentStreak = 0
    $note = ''
    $lmAtLegacy = -1.0; $lsAtLegacy = -1.0
    $pwR = $pwG = $pwB = $pwY = $pwS = -1.0; $madAtLegacy = -1.0
    $lmAtContent = $lsAtContent = -1.0; $madAtContent = -1.0
    $pwRc = $pwGc = $pwBc = $pwYc = $pwSc = -1.0
    $minMad = 1e9; $invalidSizePolls = 0; $pwFailPolls = 0; $polls = 0
    $winL = -1; $winT = -1; $wrR = -1; $wrB = -1; $scrM = -1.0; $scrS = -1.0; $scrMad = -1.0
    $pwPrintMs = -1.0; $pwDibMs = -1.0; $pwCopyMs = -1.0
    $cls = ''; $title = ''
    $dst = New-Object byte[] ($refW * $refH * 4)

    $hwnd = Wait-T1 $h
    if ($null -eq $hwnd) { $note = 'timeout-no-titled-window' }
    if ($null -ne $hwnd) { $t1 = $script:t1Ms }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($null -ne $hwnd -and [DateTime]::UtcNow -lt $deadline) {
        if ($h.Proc.HasExited) { $note = "early-exit exit=$($h.Proc.ExitCode)"; break }
        $ms = [math]::Round($h.Sw.Elapsed.TotalMilliseconds, 1)
        $polls++

        # --- PrintWindow path (content criterion) ---
        $cw = 0; $ch = 0; $ww = 0; $wh = 0; $ox = 0; $oy = 0
        $st = [CProbe.Native]::CaptureClient($hwnd, $dst, [ref]$cw, [ref]$ch, [ref]$ww, [ref]$wh, [ref]$ox, [ref]$oy)
        if ($st -eq -3) { $dst = New-Object byte[] ($cw * $ch * 4); $st = [CProbe.Native]::CaptureClient($hwnd, $dst, [ref]$cw, [ref]$ch, [ref]$ww, [ref]$wh, [ref]$ox, [ref]$oy) }
        $sizeValid = ($st -eq 0 -and $cw -eq $refW -and $ch -eq $refH)
        $mad = -1.0; $frac = -1.0
        $r = $g = $b = $y = $s = -1.0
        if ($st -eq 0) {
            if (-not $sizeValid) { $invalidSizePolls++ }
            else {
                # dst holds the client crop packed row-by-row, so the first cw*ch*4 bytes
                # are exactly the pixels to compare - no copy needed.
                [CProbe.Native]::Stats($dst, $cw, $ch, [ref]$r, [ref]$g, [ref]$b, [ref]$y, [ref]$s)
                $mad = [CProbe.Native]::Mad($dst, $refPx, $cw, $ch, [ref]$frac)
                if ($mad -ge 0) { $minMad = [math]::Min($minMad, $mad) }
            }
        } else { $pwFailPolls++ }

        # --- legacy screen-DC path, same poll ---
        $lM = 0.0; $lS = 0.0
        $lOk = [CProbe.Native]::SampleClientPixels($hwnd, 3, [ref]$lM, [ref]$lS)

        if ($PollLog -ne '') { $pollRows.Add([pscustomobject]@{ Tag = $Tag; Round = $round; Ms = $ms; PwStatus = $st; CliW = $cw; CliH = $ch; WinW = $ww; WinH = $wh; SizeValid = $sizeValid; Mad = [math]::Round($mad, 2); DiffFrac = [math]::Round($frac, 4); PwR = [math]::Round($r, 1); PwG = [math]::Round($g, 1); PwB = [math]::Round($b, 1); PwGray = [math]::Round($y, 1); PwStd = [math]::Round($s, 1); LegMean = [math]::Round($lM, 1); LegStd = [math]::Round($lS, 1); LegOk = $lOk; Err = [CProbe.Native]::LastError }) }

        # --- T2_legacy (stddev >= 8, two consecutive polls) ---
        if ($tLegacy -lt 0 -and $lOk) {
            if ($lS -ge $LegacyStdDevMin) {
                $legacyStreak++
                if ($legacyStreak -ge 2) {
                    $tLegacy = $ms
                    $lmAtLegacy = [math]::Round($lM, 1); $lsAtLegacy = [math]::Round($lS, 1)
                    $pwR = [math]::Round($r, 1); $pwG = [math]::Round($g, 1); $pwB = [math]::Round($b, 1)
                    $pwY = [math]::Round($y, 1); $pwS = [math]::Round($s, 1); $madAtLegacy = [math]::Round($mad, 2)
                    # --- three-way same-instant comparison: PrintWindow vs real screen copy
                    # --- vs the legacy StretchBlt sampler. Only path to settle whether the
                    # --- legacy sampler reads the wrong region or the threshold is meaningless.
                    if ($DiagPng -ne '') {
                        $pwPrintMs = [CProbe.Native]::TPrint; $pwDibMs = [CProbe.Native]::TDib; $pwCopyMs = [CProbe.Native]::TCopy
                        [CProbe.Native]::WindowRect($hwnd, [ref]$winL, [ref]$winT, [ref]$wrR, [ref]$wrB)
                        if ($st -eq 0) { [CProbe.Native]::SavePng($dst, $cw, $ch, ($DiagPng + '_pw.png')) | Out-Null }
                        $sdst = New-Object byte[] ($cw * $ch * 4)
                        $sw2 = 0; $sh2 = 0; $sx = 0; $sy = 0
                        $sst = [CProbe.Native]::GrabScreenRect($hwnd, $sdst, [ref]$sw2, [ref]$sh2, [ref]$sx, [ref]$sy)
                        if ($sst -eq 0) {
                            $sr = 0.0; $sg = 0.0; $sb = 0.0
                            $ff = 0.0
                            [CProbe.Native]::Stats($sdst, $sw2, $sh2, [ref]$sr, [ref]$sg, [ref]$sb, [ref]$scrM, [ref]$scrS)
                            if ($sw2 -eq $refW -and $sh2 -eq $refH) { $scrMad = [CProbe.Native]::Mad($sdst, $refPx, $sw2, $sh2, [ref]$ff) }
                            [CProbe.Native]::SavePng($sdst, $sw2, $sh2, ($DiagPng + '_screen.png')) | Out-Null
                            Write-Host ("DIAG-atT2legacy ms={0} winRect={1},{2},{3},{4} client={5}x{6}@{7},{8} | PW R={9:N1}/G={10:N1}/B={11:N1} gray={12:N1}/{13:N1} MAD={14} | SCREEN-copy R={15:N1}/G={16:N1}/B={17:N1} gray={18:N1}/{19:N1} MAD={20} | legacyDC mean={21}/std={22} | pwMs print={23:N1} dib={24:N1} copy={25:N1}" -f `
                                $ms, $winL, $winT, $wrR, $wrB, $sw2, $sh2, $sx, $sy, `
                                $pwR, $pwG, $pwB, $pwY, $pwS, $madAtLegacy, $sr, $sg, $sb, $scrM, $scrS, ([math]::Round($scrMad, 2)), $lmAtLegacy, $lsAtLegacy, `
                                $pwPrintMs, $pwDibMs, $pwCopyMs)
                        } else { Write-Host "DIAG-atT2legacy GrabScreenRect status=$sst err=$([CProbe.Native]::LastError)" }
                        Write-Host ("DIAG-zorder " + (ZOrderEvidence $hwnd ($sx + [int]($sw2 / 2)) ($sy + [int]($sh2 / 2))))
                        # What is physically on the monitor at that rect? Settles "wrong region"
                        # (screen shows the red window => sampler coords are wrong) against
                        # "the window's real pixels are not its PrintWindow content" (occlusion /
                        # GDI cannot see the D3D surface).
                        try {
                            Add-Type -AssemblyName System.Windows.Forms
                            $sb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                            $fbmp = New-Object System.Drawing.Bitmap($sb.Width, $sb.Height)
                            $fg = [System.Drawing.Graphics]::FromImage($fbmp)
                            $fg.CopyFromScreen(0, 0, 0, 0, $fbmp.Size)
                            $fbmp.Save(($DiagPng + '_full.png'), [System.Drawing.Imaging.ImageFormat]::Png)
                            $fg.Dispose(); $fbmp.Dispose()
                            Write-Host ("DIAG full-screen snapshot saved: {0}_full.png {1}x{2}" -f $DiagPng, $sb.Width, $sb.Height)
                        } catch { Write-Host "DIAG full-screen snapshot failed: $($_.Exception.Message)" }
                    }
                }
            } else { $legacyStreak = 0 }
        }
        # --- T2_content (MAD < tol, two consecutive size-valid polls) ---
        if ($tContent -lt 0 -and $sizeValid -and $mad -ge 0 -and $mad -lt $MadTol) {
            if ($tContentFirst -lt 0) { $tContentFirst = $ms }
            $contentStreak++
            if ($contentStreak -ge 2) {
                $tContent = $ms
                $lmAtContent = [math]::Round($lM, 1); $lsAtContent = [math]::Round($lS, 1); $madAtContent = [math]::Round($mad, 2)
                $pwRc = [math]::Round($r, 1); $pwGc = [math]::Round($g, 1); $pwBc = [math]::Round($b, 1)
                $pwYc = [math]::Round($y, 1); $pwSc = [math]::Round($s, 1)
            }
        } elseif ($tContent -lt 0) { $contentStreak = 0 }

        if ($tLegacy -ge 0 -and $tContent -ge 0) { break }
        Start-Sleep -Milliseconds $PollMs
    }
    if ($tContent -lt 0 -and $note -eq '') { $note = 'no-content-hit' }
    if ($tLegacy -lt 0 -and $note -eq '') { $note = 'no-legacy-hit' }

    Stop-Host $h.Proc

    $results += [pscustomobject]@{
        Tag = $Tag; Round = $round; Mode = $(if ($Image -ne '') { 'image' } else { 'empty' })
        Ref = $(if ($RefPng -ne '') { Split-Path -Leaf $RefPng } else { '' }); Image = $(if ($Image -ne '') { Split-Path -Leaf $Image } else { 'EMPTY' })
        T1 = $t1; T2_legacy = $tLegacy; T2_content = $tContent; T2_content_first_sub_tol_poll = $tContentFirst
        LegMeanAtT2legacy = $lmAtLegacy; LegStdAtT2legacy = $lsAtLegacy
        PwR = $pwR; PwG = $pwG; PwB = $pwB; PwGray = $pwY; PwStd = $pwS; MadAtT2legacy = $madAtLegacy
        LegMeanAtT2content = $lmAtContent; LegStdAtT2content = $lsAtContent; MadAtT2content = $madAtContent
        PwRc = $pwRc; PwGc = $pwGc; PwBc = $pwBc; PwGrayc = $pwYc; PwStdC = $pwSc
        MinMad = $(if ($minMad -ge 1e9) { -1 } else { [math]::Round($minMad, 2) })
        WinRect = "$winL,$winT,$wrR,$wrB"; ScreenGray = [math]::Round($scrM, 1); ScreenStd = [math]::Round($scrS, 1); ScreenMad = [math]::Round($scrMad, 2)
        PwPrintMs = [math]::Round($pwPrintMs, 1); PwDibMs = [math]::Round($pwDibMs, 1); PwCopyMs = [math]::Round($pwCopyMs, 1)
        InvalidSizePolls = $invalidSizePolls; PwFailPolls = $pwFailPolls; Polls = $polls; Note = $note; Class = $cls
    }
    $r0 = $results[-1]
    Write-Host ("RESULT tag={0} round={1} mode={2} T1={3} T2_legacy={4} T2_content={5} firstSubTolPoll={27} | atT2legacy legDC={6}/{7} PWgray={8}/{9} RGB={10}/{11}/{12} MAD={13} screenCopy={14}/{15} MAD={16} rect={17} | atT2content PWgray={18}/{19} RGB={20}/{21}/{22} MAD={23} legDC={24}/{25} | minMAD={26} invalidSize={28} pwFail={29} polls={30} pollCostMs print={31}/dib={32}/copy={33} note={34}" -f `
        $r0.Tag, $r0.Round, $r0.Mode, $r0.T1, $r0.T2_legacy, $r0.T2_content, `
        $r0.LegMeanAtT2legacy, $r0.LegStdAtT2legacy, $r0.PwGray, $r0.PwStd, $r0.PwR, $r0.PwG, $r0.PwB, $r0.MadAtT2legacy, `
        $r0.ScreenGray, $r0.ScreenStd, $r0.ScreenMad, $r0.WinRect, `
        $r0.PwGrayc, $r0.PwStdC, $r0.PwRc, $r0.PwGc, $r0.PwBc, $r0.MadAtT2content, $r0.LegMeanAtT2content, $r0.LegStdAtT2content, `
        $r0.MinMad, $r0.T2_content_first_sub_tol_poll, $r0.InvalidSizePolls, $r0.PwFailPolls, $r0.Polls, `
        $r0.PwPrintMs, $r0.PwDibMs, $r0.PwCopyMs, $r0.Note)
}

if ($CsvOut -ne '') { $results | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8; Write-Host "detail csv: $CsvOut" }
if ($PollLog -ne '' -and $pollRows.Count -gt 0) {
    $pollRows | Export-Csv -LiteralPath $PollLog -NoTypeInformation -Encoding UTF8
    Write-Host "poll csv: $PollLog ($($pollRows.Count) rows)"
}
