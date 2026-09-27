#!/usr/bin/env python3
"""传输条悬停探针：把鼠标停在某个按钮上，等待 ToolTip 弹出，然后抓屏取证。

为什么需要它：Avalonia 的 ToolTip 由**独立 Popup 顶层窗口**渲染。
BitBlt 抓整屏会受 Z 序遮挡；所以本脚本先临时把已知遮挡窗口压底，
再 BitBlt 抓屏验证主区域是否出现了提示框。
对每个**新出现的顶层窗口**额外用 PrintWindow 单独抓一份
（Popup 独立 hwnd，PrintWindow 能直接渲染）。

用法：
    python tools/probe_tooltip.py --title 3FCompare --dip-x -45 --dip-y -76 \\
        --push-down-class Chrome_WidgetWin_1 --push-down-class CabinetWClass

坐标约定：--dip-x / --dip-y 是**相对窗口客户区中心的 DIP 偏移**
（自动乘 RenderScaling 转物理像素）。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time
import zlib

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
try:
    ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def enum_windows():
    out = []

    @EnumWindowsProc
    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            out.append((hwnd, cls.value, buf.value, (r.left, r.top, r.right, r.bottom)))
        return True

    user32.EnumWindows(cb, 0)
    return out


def find_hwnd(title_part):
    hits = [w for w in enum_windows() if title_part.lower() in w[2].lower()]
    return hits


def bitblt(x, y, w, h):
    """抓屏幕区域（含分层窗口：带 CAPTUREBLT）。返回 (w, h, bgra_bytes)。"""
    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    try:
        gdi32.SelectObject(hdc_mem, bmp)

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                        ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                        ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                        ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                        ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                        ("biClrImportant", ctypes.c_uint32)]

        bi = BITMAPINFOHEADER()
        bi.biSize = ctypes.sizeof(bi)
        bi.biWidth = w
        bi.biHeight = -h  # 负值 = 自顶向下
        bi.biPlanes = 1
        bi.biBitCount = 32
        bi.biCompression = 0

        buf = ctypes.create_string_buffer(w * h * 4)
        if not gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, x, y,
                            0x00CC0020 | 0x40000000):  # SRCCOPY + CAPTUREBLT（含分层窗口）
            raise RuntimeError("BitBlt 失败")
        if not gdi32.GetDIBits(hdc_mem, bmp, 0, h, buf, ctypes.byref(bi), 0):
            raise RuntimeError("GetDIBits 失败")
        return w, h, buf.raw
    finally:
        # 无论成功/抛异常都必须释放 GDI 句柄，否则反复抓屏会耗尽 GDI 配额
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)


def write_png(path, w, h, bgra):
    """手编码 PNG（无第三方依赖）。BGRA -> RGB 逐行输出。"""
    raw = bytearray()
    stride = w * 4
    for row in range(h):
        raw.append(0)  # filter type 0
        line = bgra[row * stride:(row + 1) * stride]
        for px in range(w):
            b, g, r = line[px * 4], line[px * 4 + 1], line[px * 4 + 2]
            raw += bytes((r, g, b))

    def chunk(tag, data):
        c = ctypes.c_uint32(zlib.crc32(tag + data) & 0xFFFFFFFF)
        return (ctypes.c_uint32(len(data)).value.to_bytes(4, "big") + tag + data
                + c.value.to_bytes(4, "big"))

    ihdr = (w.to_bytes(4, "big") + h.to_bytes(4, "big")
            + bytes((8, 2, 0, 0, 0)))
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        f.write(chunk(b"IEND", b""))


def printwindow(hwnd):
    """用 PW_RENDERFULLCONTENT 把指定窗口的像素抓到内存。返回 (w, h, bgra)。"""
    rc = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rc))
    w = rc.right - rc.left
    h = rc.bottom - rc.top
    if w <= 0 or h <= 0:
        raise RuntimeError("窗口矩形无效 %dx%d" % (w, h))
    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    try:
        gdi32.SelectObject(hdc_mem, bmp)

        class BIH(ctypes.Structure):
            _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                        ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                        ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                        ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                        ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                        ("biClrImportant", ctypes.c_uint32)]
        bi = BIH()
        bi.biSize = ctypes.sizeof(bi); bi.biWidth = w; bi.biHeight = -h
        bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        ok = user32.PrintWindow(hwnd, hdc_mem, 2)
        gdi32.GetDIBits(hdc_mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
        if not ok:
            # GetLastError 可能为 0（成功但窗口没响应）—— 不抛，由调用方判断内容是否为空
            pass
        return w, h, buf.raw
    finally:
        # 无论成功/抛异常都必须释放 GDI 句柄，否则反复抓屏会耗尽 GDI 配额
        gdi32.DeleteObject(bmp); gdi32.DeleteDC(hdc_mem); user32.ReleaseDC(0, hdc_screen)


def set_z(hwnd, where, rect=None):
    if rect is None:
        return user32.SetWindowPos(hwnd, where, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002)
    L, T, R, B = rect
    return user32.SetWindowPos(hwnd, where, L, T, R - L, B - T, 0x0010 | 0x0040)


def push_windows_below(class_name):
    """临时把指定 class 的可见顶层窗口压到 Z 序底部，返回其原 (hwnd, rect) 列表。"""
    saved = [(w[0], w[3]) for w in enum_windows() if w[1] == class_name]
    for hwnd, _ in saved:
        set_z(hwnd, 1)
    return saved


def restore_windows(saved):
    """把之前压底的窗口按原顺序顶回（最后一个置顶 = 整体仍在顶部）。"""
    for hwnd, rect in saved:
        set_z(hwnd, 0, rect=rect)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--title", default="3FCompare")
    ap.add_argument("--dip-x", type=float, default=-45.0, help="相对窗口中心的 X 偏移（DIP）")
    ap.add_argument("--dip-y", type=float, default=-76.0, help="相对窗口底边的 Y 偏移（DIP）")
    ap.add_argument("--scale", type=float, default=1.5)
    ap.add_argument("--wait", type=float, default=2.0, help="悬停后等待秒数")
    ap.add_argument("--strip", type=int, default=220, help="抓取的底部条带高度（物理像素）")
    ap.add_argument("--push-down-class", action="append", default=[],
                    help="临时压底的窗口 class（可重复），例如 Chrome_WidgetWin_1")
    ap.add_argument("--out-dir", default=".3fc_dumps")
    args = ap.parse_args()

    hits = find_hwnd(args.title)
    if not hits:
        print("找不到标题含 %r 的可见窗口" % args.title)
        return 2
    for hwnd, cls, title, rect in hits:
        print("窗口 hwnd=%d class=%s title=%r rect=%s" % (hwnd, cls, title, rect))
    hwnd, _, _, rect = hits[0]
    L, T, R, B = rect

    # 顶到 Z 序最前（不抢焦点）
    user32.SetWindowPos(hwnd, 0, L, T, R - L, B - T, 0x0001 | 0x0002 | 0x0040 | 0x0010)
    time.sleep(0.4)

    # 临时压底遮挡窗口（一般是 IDE），捕获完即还原
    pushed = []
    for cls in args.push_down_class:
        pushed.extend(push_windows_below(cls))
    print("已临时压底 %d 个窗口" % len(pushed))
    time.sleep(0.3)

    # 还原必须放在 finally：中途任何异常（BitBlt / PrintWindow / 写盘失败）
    # 都不能把用户的 IDE / Chrome 永久压在 Z 序底部。
    try:
        cx = (L + R) / 2.0
        px = int(cx + args.dip_x * args.scale)
        py = int(B + args.dip_y * args.scale)
        print("悬停物理坐标 = (%d, %d)" % (px, py))

        before = {w[0] for w in enum_windows()}
        sx, sy = L, max(0, B - args.strip)
        sw, sh = R - L, min(args.strip, B - sy)

        # 基线（未悬停）
        w0, h0, buf0 = bitblt(sx, sy, sw, sh)
        os.makedirs(args.out_dir, exist_ok=True)
        base = os.path.join(args.out_dir, "tip_base.png")
        write_png(base, w0, h0, buf0)

        # 悬停
        user32.SetCursorPos(px, py)
        time.sleep(0.2)
        user32.SetCursorPos(px + 1, py)  # 触发一次真实的 move
        time.sleep(args.wait)

        after = enum_windows()
        new = [w for w in after if w[0] not in before]
        print("--- 悬停后出现的新顶层窗口 %d 个 ---" % len(new))
        for h_, cls, title, r in new:
            print("  NEW hwnd=%d class=%s title=%r rect=%s" % (h_, cls, title, r))

        # 用 PrintWindow 单独抓每个新窗口（独立 Popup 屏幕上是抓不到的）
        os.makedirs(args.out_dir, exist_ok=True)
        for i, (h_, cls, title, r) in enumerate(new):
            try:
                w2, h2, buf2 = printwindow(h_)
                out = os.path.join(args.out_dir, "tip_popup_%d_%s.png" % (i, cls))
                write_png(out, w2, h2, buf2)
                print("  抓到 NEW[%d] -> %s (%dx%d)" % (i, os.path.abspath(out), w2, h2))
            except Exception as e:
                print("  抓到 NEW[%d] 失败: %s" % (i, e))

        w1, h1, buf1 = bitblt(sx, sy, sw, sh)
        hov = os.path.join(args.out_dir, "tip_hover.png")
        write_png(hov, w1, h1, buf1)

        diff = sum(1 for i in range(0, len(buf0), 997) if buf0[i] != buf1[i])
        print("基线 PNG   : %s (%dx%d)" % (base, w0, h0))
        print("悬停 PNG   : %s (%dx%d)" % (hov, w1, h1))
        print("抽样差异点 : %d" % diff)
        print("绝对路径   : %s / %s" % (os.path.abspath(base), os.path.abspath(hov)))
        return 0
    finally:
        # 还原被压底的窗口
        restore_windows(pushed)
        print("已还原 %d 个被压底的窗口" % len(pushed))


if __name__ == "__main__":
    sys.exit(main())
