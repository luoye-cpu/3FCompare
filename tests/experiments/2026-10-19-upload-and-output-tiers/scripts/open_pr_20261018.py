# -*- coding: utf-8 -*-
"""开 PR：POST 一次，然后必须 GET 回读（POST 的响应里字段不全，本仓踩过）。
token 只从环境变量进 Authorization 头，绝不打印、绝不进 argv。"""
import json
import os
import urllib.request

TOK = os.environ["PR_TOK"]
REPO = "Lake1059/FFF_Project"
HEAD = "luoye-cpu:upstream/output-tiers-and-audio-render-plug"
BASE = "main"
TITLE = "3FP: direct path for 7 pixel formats, 16-bit output tier, external bed render"

BODY = """Two commits.

**1. `f0d971d` — renderer input/output tables**

* **Seven formats were paying a full-frame CPU `sws_scale`.** NV16 / NV20LE / NV24 /
  P410LE / P416LE / YUV410P / YUV440P were missing from `DescribeInput`, so they fell
  through `default: return {}` to layout 0. Measured in one round against a control in the
  same binary at 1920x1080: the CPU conversion route costs **3.952 ms/frame**, these rows
  **0.000 ms**, both at 60 fps; the cost scales with pixels (~15.8 ms/frame at 4K,
  ~63 ms at 8K). No new shader sampling branch — texture sizing already divides by
  `1 << shift`, layout 2 already carries two samples per texel.
* **The LSB/MSB split is pinned, and cross-checked outside the table.** NV20LE is
  LSB-aligned (`shift 0`) while P210/P410/P010 are MSB-aligned (`shift 6`); getting it
  backwards is a silent 64x colour error, so every row has a `static_assert` *and* the
  whole table was checked field-by-field against the `AVPixFmtDescriptor` of the
  `libavutil` the product links — **14/14 agree**. `static_assert` alone would only be the
  table certifying itself.
* **Output tiers: prefer 16-bit scRGB, degrade to 10-bit only when it cannot be had.**
  The `>=16 / >=10 / else` ladder was written out twice (both swap-chain paths) and is now
  one `constexpr` with assertions over the source-depth domain. Deeper-than-8-bit sources
  prefer `R16G16B16A16_FLOAT` + `...G10_NONE_P709`; the 10-bit chain stays as the
  degradation for a display Windows does not report as Advanced Color, an OS/driver
  without the ACM colour-space API, or a chain that refuses G10. `sdrScRgbMode` now
  defaults to Auto.
  **A bug this fixes:** the decision block sat inside `if (requestedMode_ == MapToHdr)`, so
  the documented Auto policy was a no-op for an ordinary SDR caller. Evidence, not
  reasoning: with the policy forced on, a 10-bit HEVC source still reported `outBits=10`.
  Now: 8-bit source stays `outBits=8`; 10-bit source goes **10 → 16** and the pixel-readback
  probe independently reports `colorMode = MapToHdr`. 16/16 arms valid, two passes.
* **Corrects the comment left by the large-frame upload work.** Its table quotes
  `7680x4320 Map 22.266 ms (UpdateSubresource 15.213 ms, 1.46x)`. That column is the
  original author's own measurement and this machine does not reproduce it: staging-with-ring
  and a single `Map(WRITE_DISCARD)` measured the **same** at 8K (11.529 vs 11.509 ms —
  `CopyResource` is queued behind the next frame's copy), and `UpdateSubresource` measured
  4.6% **slower** at 4K, unjudged at 8K (two runs of one mode differ by 8.9–9.7%, the same
  size as the effect). The comment now says plainly that those numbers are not an argument
  for moving the ladder to `UpdateSubresource`. What the measurements do support is the
  ring itself: one staging slot waits 2.36–2.99 ms/frame, two slots 0.0155 ms, and capping
  the queue depth does not substitute for it. The stale "about a millisecond of CPU work"
  claim on the adaptive-downscale field is replaced with the measured 26 ms/frame at 8K,
  which is why it stays off by default.

**2. `6e574b2` — audio bed render becomes an external plug** (not my work; I am the one
pushing it, so here is what I did and did not check). Adds an additive export + function
table and deletes `Audio/BedBinauralRenderer.{h,cpp}`. **Behaviour change on merge:**
nothing in this PR calls `FFF3FP_SetAudioRenderPlug` yet — the host-side wiring is not
included — so the bed path changes when this lands, not when a module is unlocked. If that
should be split, say so and I will separate the deletion into its own commit. I verified
the build; I did **not** verify any audio output (no A/B, no listening check).

Verification status — please read this literally

- **Verified:** `FFF.Native` Release x64, **0 warnings / 0 errors**, for the tree as
  committed. The `sizeof`/`offsetof` pins are certified by that compile because none of
  them was derived from a field table.
- **Verified by file test:** only YUV410P and YUV440P. The five semi-planar rows have **no
  runtime arm on this machine**: `ffv1` canonicalises all five to their planar siblings
  (each read back with ffprobe, 5/5), and rawvideo-in-NUT records `nv24` as `rgb555le`, so
  no file input can hand them to `Render`. They are reachable via a hardware transfer's
  `sw_format`; that arm is untested here.
- **Not verified:** pixel-level chroma siting for the new rows, and colour equivalence
  against a reference render. The local corpus (37 files, 36 readable) contains **no**
  4:2:2, 4:4:4 or semi-planar video at all — 23 `yuv420p`, 13 `yuv420p10le`.
- **Known cost, unmeasured:** a promoted session no longer takes the "ordinary SDR uploads
  do not wait for Present" fast path. Frame-rate impact is not established here (n=2/arm).
- **Deliberately excluded:** the measurement scaffolding from my own tree
  (`FFF3FP_UPLOAD_COPY_MODE=us/bulk/stream`, `FFF3FP_UPLOAD_RING`, `FFF3FP_MAX_FRAME_LATENCY`,
  `FFF3FP_UPLOAD_FORCE_DYNAMIC`, `FFF3FP_SDR_SCRGB`, `FFF3FP_DECODER`) and the perf sampler,
  plus the host-side audio wiring and one unrelated overlay change. The trees' host VB files
  have diverged enough that a file copy would have overwritten upstream-only code, so the
  host wiring needs its own patch-set rather than being dragged in here.
"""


def call(method, url, payload=None):
    req = urllib.request.Request(url, method=method,
                                 headers={"Authorization": "Bearer " + TOK,
                                          "Accept": "application/vnd.github+json",
                                          "User-Agent": "qoder-agent"})
    data = None
    if payload is not None:
        req.add_header("Content-Type", "application/json")
        data = json.dumps(payload).encode()
    with urllib.request.urlopen(req, data, timeout=90) as r:
        return json.loads(r.read().decode())


created = call("POST", "https://api.github.com/repos/%s/pulls" % REPO,
               {"title": TITLE, "head": HEAD, "base": BASE, "body": BODY})
print("created #%s" % created["number"], created["html_url"])

back = call("GET", "https://api.github.com/repos/%s/pulls/%s" % (REPO, created["number"]))
commits = call("GET", "https://api.github.com/repos/%s/pulls/%s/commits" % (REPO, created["number"]))
print("readback: state=%s merged=%s mergeable=%s base=%s head=%s" % (
    back["state"], back["merged"], back.get("mergeable"),
    back["base"]["sha"][:9], back["head"]["sha"][:9]))
print("commits=%d files=%s +%s -%s" % (len(commits), back.get("changed_files"),
                                       back.get("additions"), back.get("deletions")))
print("head on remote? %s" % (back["head"]["sha"][:9] == commits[-1]["sha"][:9]))
