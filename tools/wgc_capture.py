#!/usr/bin/env python3
"""WGC (Windows Graphics Capture) capture - avoids the GDI PrintWindow/BitBlt
SDR-conversion path that is suspected of distorting HDR playback colors.

Usage:
    py wgc_capture.py out.png --window TITLE_SUBSTR
    py wgc_capture.py out.png --monitor 1
"""
import argparse
import ctypes
import sys
import time
from ctypes import wintypes

from windows_capture import WindowsCapture


def find_hwnd(title_part):
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def proc(hwnd, _):
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

    user32.EnumWindows(proc, 0)
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--window", help="capture window by title substring")
    ap.add_argument("--monitor", type=int, default=1, help="monitor index (1-based)")
    ap.add_argument("--stats", action="store_true", help="print center-crop mean BGR")
    a = ap.parse_args()

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass

    if a.window:
        wins = find_hwnd(a.window)
        if not wins:
            print("NOWINDOW", a.window)
            return 1
        hwnd, title = wins[0]
        print("HWND", hwnd, "TITLE", title.encode("ascii", "replace").decode())
        cap = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=hwnd)
    else:
        cap = WindowsCapture(cursor_capture=False, draw_border=False,
                             monitor_index=a.monitor)

    state = {"saved": False}

    @cap.event
    def on_frame_arrived(frame, ctl):
        if state["saved"]:
            return
        state["saved"] = True
        frame.save_as_image(a.out)
        if a.stats:
            try:
                arr = frame.convert_to_bgr()
                h, w = arr.shape[:2]
                c = arr[h // 4: 3 * h // 4, w // 4: 3 * w // 4]
                means = [round(float(c[:, :, i].mean()), 1) for i in range(3)]
                print("STATS", w, "x", h, "meanBGR", means)
            except Exception as e:
                print("STATSFAIL", e)
        ctl.stop()

    @cap.event
    def on_closed():
        pass

    ctl = cap.start_free_threaded()
    t0 = time.time()
    while not state["saved"] and time.time() - t0 < 15:
        time.sleep(0.1)
    if not state["saved"]:
        print("TIMEOUT")
        return 2
    print("SAVED", a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
