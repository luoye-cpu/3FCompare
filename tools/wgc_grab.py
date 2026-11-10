#!/usr/bin/env python3
"""Grab frames from the FFF.Player window located by PROCESS NAME (title-independent).

Saves N frames spaced `--interval` seconds apart starting `--delay` seconds after the
window appears, then reports per-frame center-crop statistics computed from the PNG.

Usage:
    py wgc_grab.py --outdir D:\\out --proc FFF.Player.exe --frames 3 --interval 1.0
"""
import argparse
import ctypes
import os
import time
from ctypes import wintypes

from windows_capture import WindowsCapture

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def proc_exe_name(pid):
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buf))
        if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return os.path.basename(buf.value)
        return ""
    finally:
        k32.CloseHandle(h)


def find_by_proc(name):
    u = ctypes.windll.user32
    hit = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        if not u.IsWindowVisible(hwnd):
            return True
        if u.GetWindowTextLengthW(hwnd) <= 0:
            return True
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if proc_exe_name(pid.value).lower() == name.lower():
            buf = ctypes.create_unicode_buffer(u.GetWindowTextLengthW(hwnd) + 1)
            u.GetWindowTextW(hwnd, buf, len(buf))
            hit.append((hwnd, pid.value, buf.value))
        return True

    u.EnumWindows(cb, 0)
    # prefer the widest window (the main frame, not tooltips/dialogs)
    if len(hit) > 1:
        best, bestarea = hit[0], 0
        for h, _p, _t in hit:
            rc = wintypes.RECT()
            u.GetClientRect(h, ctypes.byref(rc))
            area = rc.right * rc.bottom
            if area > bestarea:
                best, bestarea = (h, _p, _t), area
        return [best]
    return hit


def stats_of(path):
    from PIL import Image
    im = Image.open(path).convert("RGB")
    w, h = im.size
    c = im.crop((w // 4, h // 4, 3 * w // 4, 3 * h // 4))
    px = list(c.resize((64, 64)).getdata())
    n = len(px)
    mr = sum(p[0] for p in px) / n
    mg = sum(p[1] for p in px) / n
    mb = sum(p[2] for p in px) / n
    var = sum((p[0] - mr) ** 2 + (p[1] - mg) ** 2 + (p[2] - mb) ** 2 for p in px) / (3 * n)
    return w, h, (round(mr, 1), round(mg, 1), round(mb, 1)), round(var ** 0.5, 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--proc", default="FFF.Player.exe")
    ap.add_argument("--prefix", default="g")
    ap.add_argument("--frames", type=int, default=3)
    ap.add_argument("--interval", type=float, default=1.0)
    ap.add_argument("--delay", type=float, default=0.0, help="extra wait after window found")
    ap.add_argument("--wait", type=float, default=25.0, help="timeout waiting for window")
    a = ap.parse_args()

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass

    t0 = time.time()
    hit = []
    while time.time() - t0 < a.wait:
        hit = find_by_proc(a.proc)
        if hit:
            break
        time.sleep(0.1)
    if not hit:
        print("NOWINDOW proc=%s" % a.proc)
        return 1
    hwnd, pid, title = hit[0]
    print("HWND %d PID %d TITLE %s" % (hwnd, pid, title.encode("ascii", "replace").decode()))

    os.makedirs(a.outdir, exist_ok=True)
    if a.delay > 0:
        time.sleep(a.delay)

    cap = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=hwnd)
    state = {"n": 0, "last": 0.0, "paths": []}
    t_start = time.time()

    @cap.event
    def on_frame_arrived(frame, ctl):
        now = time.time()
        if state["n"] >= a.frames or (state["n"] and now - state["last"] < a.interval):
            return
        p = os.path.join(a.outdir, "%s_%02d.png" % (a.prefix, state["n"]))
        try:
            frame.save_as_image(p)
            state["paths"].append(p)
            state["n"] += 1
            state["last"] = now
            print("FRAME %02d t=%.0fms %s" % (state["n"] - 1, (now - t_start) * 1000, p))
        except Exception as e:
            print("SAVEFAIL", type(e).__name__, e)
        if state["n"] >= a.frames:
            ctl.stop()

    @cap.event
    def on_closed():
        pass

    cap.start_free_threaded()
    t1 = time.time()
    while state["n"] < a.frames and time.time() - t1 < 20:
        time.sleep(0.05)

    for p in state["paths"]:
        try:
            w, h, mean, sd = stats_of(p)
            print("STATS %s %dx%d meanRGB=%s sd=%.1f" % (os.path.basename(p), w, h, mean, sd))
        except Exception as e:
            print("STATSFAIL", os.path.basename(p), type(e).__name__, e)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
