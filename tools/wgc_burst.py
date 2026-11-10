#!/usr/bin/env python3
"""Launch a GUI app and time-slice-capture it with WGC (Windows Graphics Capture).

Purpose: prove *when* the video picture first appears relative to the surrounding UI
(custom chrome / overlay layers) during startup.

GDI PrintWindow is unusable for 3FP's HDR (scRGB) swapchain -- it returns
channel-swapped garbage -- so WGC is the only trustworthy capture path here.

Two modes:
  * window mode (default): waits for the window, then captures it directly.
    Loses the first ~0.5-1s because WGC setup only starts once the hwnd exists.
  * --precapture-monitor N: starts full-monitor capture *before* launching, then
    crops each frame to the app window. Covers the whole startup from t=0.

Usage:
    py wgc_burst.py --exe APP.exe --media FILE.mkv --outdir DIR [--frames 40]
                    [--interval 60] [--env KEY=VAL ...] [--precapture-monitor 1]

Output: DIR/f000.png ... plus DIR/log.txt (per-frame timestamp + regional stats).
"""
import argparse
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

from windows_capture import WindowsCapture

user32 = ctypes.windll.user32


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD),
                ("rcMonitor", wintypes.RECT),
                ("rcWork", wintypes.RECT),
                ("dwFlags", wintypes.DWORD)]


def find_hwnd(title_part, visible_only=True):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def proc(hwnd, _):
        if visible_only and not user32.IsWindowVisible(hwnd):
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


def window_rect(hwnd):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def monitor_origin(hwnd):
    MONITOR_DEFAULTTONEAREST = 2
    hmon = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    if hmon and user32.GetMonitorInfoW(hmon, ctypes.byref(mi)):
        return mi.rcMonitor.left, mi.rcMonitor.top
    return 0, 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True)
    ap.add_argument("--media", default="")
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--frames", type=int, default=40)
    ap.add_argument("--interval", type=int, default=60, help="ms between saved frames")
    ap.add_argument("--env", action="append", default=[], help="KEY=VAL for child")
    ap.add_argument("--title-substr", default="")
    ap.add_argument("--scale-width", type=int, default=1100)
    ap.add_argument("--precapture-monitor", type=int, default=0,
                    help="capture this monitor (1-based) from before launch")
    ap.add_argument("--wait-hidden", action="store_true",
                    help="window mode: match the hwnd even before it becomes visible "
                         "(lets the capture session warm up during Load)")
    a = ap.parse_args()

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass

    os.makedirs(a.outdir, exist_ok=True)
    log = open(os.path.join(a.outdir, "log.txt"), "w", encoding="utf-8", buffering=1)

    def w(msg):
        log.write(msg + "\n")
        print(msg)

    env = dict(os.environ)
    for item in a.env:
        if "=" in item:
            k, v = item.split("=", 1)
            env[k] = v
            w("ENV %s=%s" % (k, v))

    subprocess.run(["taskkill", "/f", "/im", os.path.basename(a.exe)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.8)

    from PIL import Image
    import numpy as np

    needle = a.title_substr or os.path.splitext(os.path.basename(a.media))[0] or "FFF"
    if not a.title_substr and a.wait_hidden:
        needle = "FFF.Player"      # the Designer default text, valid before OpenMedia
    st = {"n": 0, "want": True, "stopped": False, "hwnd": None, "t_win": None,
          "w_rect0": None}
    t0 = [0.0]
    tmp = os.path.join(a.outdir, "_tmp.png")

    def dump(frame, idx):
        try:
            frame.save_as_image(tmp)
            img = Image.open(tmp)
            hwnd = st["hwnd"]
            rect = window_rect(hwnd) if hwnd else None
            if a.precapture_monitor and rect and rect[2] > 200 and rect[3] > 200:
                mx, my = monitor_origin(hwnd)
                box = (rect[0] - mx, rect[1] - my,
                       rect[0] - mx + rect[2], rect[1] - my + rect[3])
                img = img.crop(box)
            if a.scale_width and img.width > a.scale_width:
                nh = int(round(img.height * a.scale_width / img.width))
                img = img.resize((a.scale_width, nh), Image.LANCZOS)
            out = os.path.join(a.outdir, "f%03d.png" % idx)
            img.save(out)
            arr = np.asarray(img.convert("RGB"), dtype=np.float32)
            h, wpx = arr.shape[:2]
            cm = arr[h // 4: 3 * h // 4, wpx // 4: 3 * wpx // 4]
            tm = arr[0:max(1, h // 12), :]
            bm = arr[max(1, h - h // 10):h, :]
            cmean = [round(float(cm[:, :, i].mean()), 1) for i in range(3)]
            tmean = [round(float(tm[:, :, i].mean()), 1) for i in range(3)]
            bmean = [round(float(bm[:, :, i].mean()), 1) for i in range(3)]
            cstd = round(float(cm.mean(axis=2).std()), 1)
            w("FRAME %03d t=%.0fms center_mean=%s cstd=%.1f top_mean=%s bot_mean=%s "
              "%dx%d rect=%s -> f%03d.png"
              % (idx, (time.time() - t0[0]) * 1000, cmean, cstd, tmean, bmean,
                 wpx, h, rect, idx))
        except Exception as e:
            w("FRAMEFAIL %03d %s: %s" % (idx, type(e).__name__, e))

    def make_events(cap):
        @cap.event
        def on_frame_arrived(frame, ctl):
            if st["stopped"] or not st["want"]:
                return
            st["want"] = False
            idx = st["n"]
            st["n"] += 1
            dump(frame, idx)
            if st["n"] >= a.frames:
                st["stopped"] = True
                ctl.stop()

        @cap.event
        def on_closed():
            pass

    if a.precapture_monitor:
        cap = WindowsCapture(cursor_capture=False, draw_border=False,
                             monitor_index=a.precapture_monitor)
        make_events(cap)
        ctl = cap.start_free_threaded()
        time.sleep(0.6)            # let the capture session go live
        t0[0] = time.time()
        w("PRECAPTURE monitor=%d armed at t=0" % a.precapture_monitor)
        proc = subprocess.Popen([a.exe] + ([a.media] if a.media else []),
                                env=env, cwd=os.path.dirname(a.exe))
        # window bookkeeping on the main thread
        while not st["stopped"] and time.time() - t0[0] < 30:
            if st["hwnd"] is None:
                wins = find_hwnd(needle)
                if wins:
                    st["hwnd"] = wins[0][0]
                    st["t_win"] = (time.time() - t0[0]) * 1000
                    st["w_rect0"] = window_rect(st["hwnd"])
                    w("WINDOW t=%.0fms rect=%s" % (st["t_win"], st["w_rect0"]))
            if not st["want"]:
                time.sleep(a.interval / 1000.0)
                if not st["stopped"]:
                    st["want"] = True
            else:
                time.sleep(0.005)
    else:
        hwnd = None
        t_launch = time.time()
        t0[0] = t_launch              # absolute time base = process launch
        proc = subprocess.Popen([a.exe] + ([a.media] if a.media else []),
                                env=env, cwd=os.path.dirname(a.exe))
        while time.time() - t_launch < 12:
            wins = [x for x in find_hwnd(needle, visible_only=not a.wait_hidden)
                    if window_rect(x[0])[2] > 200]
            if wins:
                hwnd = wins[0][0]
                st["hwnd"] = hwnd
                st["t_win"] = (time.time() - t_launch) * 1000
                w("HWND t=%.0fms visible=%d rect=%s"
                  % (st["t_win"], 1 if user32.IsWindowVisible(hwnd) else 0,
                     window_rect(hwnd)))
                break
            time.sleep(0.01)
        if not hwnd:
            w("NOWINDOW %r" % needle)
            proc.kill()
            log.close()
            return 1
        ctl = None
        for attempt in range(80):
            fresh = [x for x in find_hwnd(needle, visible_only=False)
                     if window_rect(x[0])[2] > 200]
            if fresh:
                hwnd = fresh[0][0]
                st["hwnd"] = hwnd
            try:
                cap = WindowsCapture(cursor_capture=False, draw_border=False,
                                     window_hwnd=hwnd)
                make_events(cap)
                ctl = cap.start_free_threaded()
                w("CAPSTART t=%.0fms attempt=%d"
                  % ((time.time() - t0[0]) * 1000, attempt))
                break
            except Exception as e:
                w("CAPRETRY %s: %s" % (type(e).__name__, e))
                time.sleep(0.1)
        while not st["stopped"] and time.time() - t0[0] < 30:
            if not st["want"]:
                time.sleep(a.interval / 1000.0)
                if not st["stopped"]:
                    st["want"] = True
            else:
                time.sleep(0.005)

    w("CAPTURED %d frames; window_seen=%s; total=%.0fms; rect_end=%s"
      % (st["n"],
         ("%.0fms" % st["t_win"]) if st["t_win"] else "n/a",
         (time.time() - t0[0]) * 1000,
         window_rect(st["hwnd"]) if st["hwnd"] else "n/a"))
    time.sleep(0.4)
    try:
        proc.kill()
    except Exception:
        pass
    try:
        os.remove(tmp)
    except Exception:
        pass
    log.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
