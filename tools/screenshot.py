#!/usr/bin/env python3
"""本机屏幕截图（纯标准库，无需 pillow）。

为什么自己写：PowerShell 的 `Add-Type` 被安全策略拦截（编译并加载 .NET 代码），
而本机没有 pillow。这里用 ctypes 调 GDI 抓屏，再用 zlib 手工编码 PNG
（PNG 结构很简单：IHDR + IDAT + IEND，每行一个 filter 字节）。

用法：
    python tools/screenshot.py out.png                  # 全屏
    python tools/screenshot.py out.png --region x,y,w,h # 指定区域（物理像素）
    python tools/screenshot.py out.png --bottom 120     # 屏底 120 px（看底栏/状态栏）
    python tools/screenshot.py out.png --window 3FCompare
                                                        # 指定窗口客户区（按标题子串）

注意：抓的是**物理像素**。本机主屏是 3840x2160，RenderScaling 1.5
（2560x1440 是逻辑尺寸），所以 1 DIP = 1.5 物理像素 —— 与日志里的 DIP 数值对账时要记得换算。
"""
import argparse
import ctypes
import os
import struct
import sys
import zlib
from ctypes import wintypes

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", ctypes.c_int), ("biHeight", ctypes.c_int),
        ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_int),
        ("biYPelsPerMeter", ctypes.c_int), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


def _png_chunk(tag: bytes, data: bytes) -> bytes:
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def write_png(path: str, w: int, h: int, bgra: bytes) -> None:
    """bgra：每行 w*4 字节，自顶向下。转 RGBA 后编码 PNG。"""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        row = bgra[y * stride:(y + 1) * stride]
        raw.append(0)  # filter type 0 (None)
        # BGRA -> RGBA
        raw += bytes(row[i + 2] if (i % 4) == 0 else
                     row[i] if (i % 4) == 2 else
                     row[i] for i in range(stride))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n"
           + _png_chunk(b"IHDR", ihdr)
           + _png_chunk(b"IDAT", zlib.compress(bytes(raw), 6))
           + _png_chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


def _bind_gdi():
    """绑定 user32/gdi32 并声明关键函数的 argtypes/restype。

    64 位下 HDC/HBITMAP 是 8 字节指针；不声明 restype 时 ctypes 默认按 c_int
    截断返回值 ⇒ 句柄高位丢失，SelectObject/BitBlt 拿到野句柄会失败或写坏内存。
    """
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

    HDC, HBITMAP, HANDLE = wintypes.HDC, wintypes.HBITMAP, wintypes.HANDLE

    user32.GetDC.argtypes = [wintypes.HWND]
    user32.GetDC.restype = HDC
    user32.ReleaseDC.argtypes = [wintypes.HWND, HDC]
    user32.ReleaseDC.restype = ctypes.c_int
    user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user32.GetWindowRect.restype = wintypes.BOOL
    user32.PrintWindow.argtypes = [wintypes.HWND, HDC, wintypes.UINT]
    user32.PrintWindow.restype = wintypes.BOOL

    gdi32.CreateCompatibleDC.argtypes = [HDC]
    gdi32.CreateCompatibleDC.restype = HDC
    gdi32.CreateCompatibleBitmap.argtypes = [HDC, ctypes.c_int, ctypes.c_int]
    gdi32.CreateCompatibleBitmap.restype = HBITMAP
    gdi32.SelectObject.argtypes = [HDC, HANDLE]
    gdi32.SelectObject.restype = HANDLE
    gdi32.BitBlt.argtypes = [HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                             HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
    gdi32.BitBlt.restype = wintypes.BOOL
    gdi32.GetDIBits.argtypes = [HDC, HBITMAP, wintypes.UINT, wintypes.UINT,
                                ctypes.c_void_p, ctypes.POINTER(BITMAPINFOHEADER),
                                wintypes.UINT]
    gdi32.GetDIBits.restype = ctypes.c_int
    gdi32.DeleteObject.argtypes = [HANDLE]
    gdi32.DeleteObject.restype = wintypes.BOOL
    gdi32.DeleteDC.argtypes = [HDC]
    gdi32.DeleteDC.restype = wintypes.BOOL
    return user32, gdi32


def grab(x: int, y: int, w: int, h: int) -> bytes:
    user32, gdi32 = _bind_gdi()
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass

    hdc_screen = user32.GetDC(0)
    if not hdc_screen:
        raise RuntimeError("GetDC 失败")
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    old = gdi32.SelectObject(hdc_mem, hbmp)
    try:
        if not gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x, y, SRCCOPY):
            raise RuntimeError("BitBlt 失败")
        bi = BITMAPINFOHEADER()
        bi.biSize = ctypes.sizeof(bi)
        bi.biWidth = w
        bi.biHeight = -h  # 负值 = 自顶向下
        bi.biPlanes = 1
        bi.biBitCount = 32
        bi.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        got = gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)
        if not got:
            raise RuntimeError("GetDIBits 失败")
        return buf.raw
    finally:
        gdi32.SelectObject(hdc_mem, old)
        gdi32.DeleteObject(hbmp)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)


def grab_window(hwnd: int) -> bytes:
    """用 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 抓指定窗口到内存。

    <para><b>为什么不用 BitBlt</b>：BitBlt 是物理拷贝屏幕，被前景窗口遮住的部分会拿到别人的内容。
    PrintWindow 让窗口<b>自己</b>渲染到我们的 DC，绕过 Z 序，所以即使被遮也能抓到完整画面。
    <c>PW_RENDERFULLCONTENT (=2)</c> 是 Vista+ 标志，强制包含 DWM 合成层 —— 否则 D3D/视频子 HWND 会全黑。</para>

    <para>已知限制：极少数应用（特殊 DWM 路径）可能仍返回空白，但 WPF/WinForms/Avalonia 都正常。</para>
    """
    user32, gdi32 = _bind_gdi()
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass

    rc = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rc)):
        raise RuntimeError("GetWindowRect 失败")
    w = rc.right - rc.left
    h = rc.bottom - rc.top
    if w <= 0 or h <= 0:
        raise RuntimeError(f"窗口矩形无效: {w}x{h}")

    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    old = gdi32.SelectObject(hdc_mem, hbmp)
    try:
        # PW_RENDERFULLCONTENT = 2（Vista+）。返回值：非 0 = 成功
        if not user32.PrintWindow(hwnd, hdc_mem, 2):
            raise RuntimeError("PrintWindow 失败")
        bi = BITMAPINFOHEADER()
        bi.biSize = ctypes.sizeof(bi)
        bi.biWidth = w
        bi.biHeight = -h
        bi.biPlanes = 1
        bi.biBitCount = 32
        bi.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        got = gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)
        if not got:
            raise RuntimeError("GetDIBits 失败")
        return buf.raw, w, h
    finally:
        gdi32.SelectObject(hdc_mem, old)
        gdi32.DeleteObject(hbmp)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)


def find_window_hwnd(title_part: str):
    """返回第一个可见且标题含 <paramref name="title_part"/> 的窗口 HWND。"""
    user32 = ctypes.windll.user32

    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def enum_proc(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if title_part.lower() in buf.value.lower():
            found.append((hwnd, buf.value))
        return True

    user32.EnumWindows(enum_proc, 0)
    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--region", help="x,y,w,h（物理像素）")
    ap.add_argument("--bottom", type=int, help="屏底 N 像素")
    ap.add_argument("--window", help="按标题子串抓窗口（用 PrintWindow，可被前景遮挡仍能抓到）")
    ap.add_argument("--bitblt", action="store_true",
                    help="强制 BitBlt 而非 PrintWindow（仅用于对比两种路径的差异）")
    args = ap.parse_args()

    user32 = ctypes.windll.user32
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass
    sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)

    if args.window:
        wins = find_window_hwnd(args.window)
        if not wins:
            print(f"没找到标题含 '{args.window}' 的可见窗口")
            return 1
        hwnd, title = wins[0]
        print(f"窗口 '{title}' hwnd={hwnd}")
        if args.bitblt:
            rc = ctypes.wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rc))
            x, y, w, h = rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top
            print(f"矩形: x={x} y={y} w={w} h={h}（BitBlt：被前景遮挡部分会拿到别人的内容）")
            data = grab(x, y, w, h)
        else:
            data, w, h = grab_window(hwnd)
            x, y = 0, 0  # PrintWindow 不依赖屏幕坐标；这两列只在最后打印用
            print(f"PrintWindow 抓到 {w}x{h}")
    elif args.region:
        x, y, w, h = (int(v) for v in args.region.split(","))
        data = grab(x, y, w, h)
    elif args.bottom:
        x, y, w, h = 0, sh - args.bottom, sw, args.bottom
        data = grab(x, y, w, h)
    else:
        x, y, w, h = 0, 0, sw, sh
        data = grab(x, y, w, h)

    write_png(os.path.abspath(args.out), w, h, data)
    print(f"已写到 {os.path.abspath(args.out)} ({w}x{h})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
