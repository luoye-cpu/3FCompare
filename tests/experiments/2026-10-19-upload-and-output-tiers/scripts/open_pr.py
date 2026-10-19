# -*- coding: utf-8 -*-
"""Open the fork -> upstream PR, then print only its number and URL.

The token comes from git credential fill via the environment; it is never written to
argv (which shows in the process list) and never echoed.
"""
import json
import os
import urllib.error
import urllib.request

TOK = os.environ["PR_TOK"]
API = "https://api.github.com/repos/Lake1059/FFF_Project/pulls"

TITLE = "3FP: adaptive playback placement/pacing + upload byte-count telemetry"

BODY = """Two commits, 12 files.

**1. Upload telemetry -- give the upload split its two missing operands**

`uploadCopy100ns` was a duration with no byte count beside it, and it was divided by
*presented* frames while nothing recorded how many pictures were uploaded. Every
bandwidth figure read off the snapshot was therefore a theoretical frame size
(`W*H*1.5*bpp`) over an inferred denominator.

Appends `uploadCount` (440), `uploadCopyBytes` (448) and `uploadPath` (456) to
`FFF3FPSnapshot`, `440 -> 464`, with the offsets pinned, mirrored at both publish
sites and cleared on Close. `uploadPath` names the branch that produced the numbers:
1 staging rung, 2 `Map(WRITE_DISCARD)` rung, 3 packed `UpdateSubresource`, 4 half-float,
5 RGB layout 0. Bytes are exact for 1, 2 and 5; arms 3 and 4 record none rather than
guessing a size they do not carry, and stay identifiable through `uploadPath` instead
of being averaged into the others.

**2. Adaptive playback** (from `9b82322`): the placement ladder, present pacing, and the
measured upload ladder this telemetry reads.

Verification status -- please read this part literally

- Built: `FFF.Native` Release x64, **0 warnings / 0 errors**, which is also what certifies
  the new `sizeof`/`offsetof` assertions (they were not derived from a field table).
- **Not run in this session:** `FFF.Player.Tests`, including `PlaybackPlacementRegression.vb`.
  The regression suite was not executed, so nothing here claims it passes.
- **Runtime measurements below were taken on a sibling tree's build, not on this branch.**
  The numbers are reported for the upload path in general; this branch's own behaviour at
  playback level is unverified.

Measurements that motivated the byte counter (fullscreen 3836x1921 viewport, software
decode, each arm witnessed to have no other player instance live, 30 sample windows):

| arm | bytes/upload | copy per upload | GB/s |
|---|---|---|---|
| 4K HEVC 10bit | 24,883,200 | 1.79 ms | 13.9 |
| 8K AV1 HDR10 | 99,532,800 | 10.35 ms | 9.6 |

- `uploads == presented` on every arm, so dropped frames are not uploaded first.
- Adding four concurrent 100 MB copy threads took 8K AV1 from 9.75 ms to 19.1-20.2 ms,
  **whether those threads sat on P cores or on E cores** -- so the ceiling here is DRAM
  bandwidth, not `Map` blocking and not memory type. `uploadMapWait100ns` is 0.009-0.015 ms.
- Consequence for anyone touching this path: on this machine, 8K per-frame copy time
  drifts +-8% between quiet, witnessed runs, so **an effect under ~10% at 8K cannot be
  resolved by a single A/B**; interleave 6-8 passes and compare medians.

Open question kept honest

The pre-#25 route (planar via `UpdateSubresource`) was compared against the staging ring.
At 4K it is 4.6% slower, with the same-mode repeat agreeing to 1.4% -- that one is decided.
At 8K the two same-mode passes themselves differed by 8.9-9.7%, the same size as the
difference being tested, so **the 8K comparison is explicitly not concluded here**.

Note: the env-gated copy-mode experiments used to gather this (`row` / `bulk` / `stream`,
and the `UpdateSubresource` arm) live in the test tree and are deliberately **not** part of
this branch, which carries the measurement without the harness switches.

"""

payload = {
    "title": TITLE,
    "head": "luoye-cpu:upstream/adaptive-playback-pr",
    "base": "main",
    "body": BODY,
}

req = urllib.request.Request(
    API,
    data=json.dumps(payload).encode("utf-8"),
    headers={
        "Authorization": "Bearer " + TOK,
        "Accept": "application/vnd.github+json",
        "Content-Type": "application/json",
        "User-Agent": "qoder",
        "X-GitHub-Api-Version": "2022-11-28",
    },
    method="POST",
)
try:
    with urllib.request.urlopen(req, timeout=90) as r:
        d = json.load(r)
        print("PR_CREATED #%s" % d["number"])
        print("URL " + d["html_url"])
        print("HEAD %s @ %s" % (d["head"]["label"], d["head"]["sha"][:12]))
        print("BASE %s @ %s" % (d["base"]["label"], d["base"]["sha"][:12]))
        print("COMMITS %s  CHANGED_FILES %s" % (d.get("commits"), d.get("changed_files")))
        print("MERGEABLE %s  STATE %s" % (d.get("mergeable"), d.get("state")))
except urllib.error.HTTPError as e:
    print("HTTP %s" % e.code)
    print(e.read().decode("utf-8", "replace")[:900])
