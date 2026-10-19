# -*- coding: utf-8 -*-
"""开第二个 PR（宿主接线 + UI 注入 + 档位文案）。token 只从环境变量进 header，绝不回显。"""
import json
import os
import urllib.request

TOK = os.environ["PR_TOK"]
REPO = "Lake1059/FFF_Project"
HEAD = "luoye-cpu:upstream/host-audio-plug-and-tier-labels"
BASE = "main"
TITLE = "Player: wire the audio render plug, inject its UI row, fix tier labels"

BODY = """One commit, 6 files, +202 -9. Base is `main` after #28.

Follows the same rule the video side already follows: **when the body changes something a
user can observe, the UI has to carry it** -- and when the body moves a restricted
implementation into the external module, the host is where the handshake lives.

1. **Audio render plug wiring** (the consumer for `FFF3FP_SetAudioRenderPlug` added in #28).
   `色彩变换模块接口` resolves `FFF_GetAudioRenderApi` at load; `尝试取音频渲染表` only hands
   the table over when the module returns 0 *and* the re-read struct satisfies
   version == 1, size >= 56, non-null context/supports/render/reset. The three refusal
   shapes stay distinguishable in the log reason -- old module without this face / module
   present but not authorised / module that claims authorised yet still refuses (only the
   third is a fault). After the table is handed over, `Dispose` deliberately skips
   `FreeLibrary`: the kernel holds raw pointers into this module's code section plus its own
   context, so unloading would turn the next render call into a jump into unmapped memory.
   `播放器会话` installs it once a session exists and reads back
   `读取音频渲染插槽状态` (active / rendered / declined).

2. **UI injection.** `双耳：HRTF（外部渲染器拥有床）` appears in the overlay **only** when
   `快照.音频双耳渲染生效`. No module, an old module, and an unauthorised module all render
   nothing -- matrix fold-down is current shipped behaviour, not a degradation that needs
   explaining to the user.

3. **Output-tier labels**, required by #28's default (deeper-than-8-bit SDR now prefers the
   16-bit scRGB chain): "on the HDR chain" is not "the source is HDR", and two places
   conflated them.
   - `色彩模式文本` printed `HDR 真实高亮` for any session on that chain, because
     `HDR规格文本(SDR, "HDR")` falls through to the literal `HDR`; it now checks `是HDR源`
     and says `scRGB 高位深（SDR 源）` for a promoted SDR picture.
   - `Form媒体信息.输出色彩` likewise keys its descriptors off `是HDR源`; only the BGRA8 row
     keeps the presentation mode (`HDR 转 SDR` is precisely the fall-back case).
   - `播放模型` carries `SDRscRGB模式 = 1UI` so the host asks for what the kernel now defaults to.

Verification status, read literally

- **Verified:** `FFF.Player` Release builds **0 errors / 0 warnings** in this tree (and the
  same edits build clean in my experiment tree). The kernel neutrality gate was re-run after
  these changes: verdict `本体干净`, royalty-free existence still **8/8**, and
  `BedBinauralRenderer` occurs **0** times under `FFF.Native/3FP` or `FFF.Player` -- the
  restricted implementation stays outside the body.
- **NOT verified: nothing was played.** No runtime arm proves the handshake succeeds against a
  real module, that the `双耳：` row ever appears, or that the new label wording renders.
  The module exporting `FFF_GetAudioRenderApi` is not shipped and needs an authorisation step,
  so on a machine without it the observable behaviour is unchanged from before this commit.
- **Not included on purpose:** the `内容描述` / `HDR 等级` overlay hunk sitting in my working
  tree belongs to the colour-transform stream and I am not certifying it here; my local
  measurement env gates were stripped rather than copied. The two trees' host files differ by
  18-102 lines per file, so this was ported hunk by hunk -- not by file copy (a copy would
  have silently overwritten host code that only exists upstream).
"""


def call(method, url, payload=None):
    req = urllib.request.Request(url, method=method, headers={
        "Authorization": "Bearer " + TOK, "Accept": "application/vnd.github+json",
        "User-Agent": "qoder-agent"})
    data = None
    if payload is not None:
        req.add_header("Content-Type", "application/json")
        data = json.dumps(payload).encode()
    with urllib.request.urlopen(req, data, timeout=90) as r:
        return json.loads(r.read().decode())


created = call("POST", "https://api.github.com/repos/%s/pulls" % REPO,
               {"title": TITLE, "head": HEAD, "base": BASE, "body": BODY})
print("created #%s %s" % (created["number"], created["html_url"]))
back = call("GET", "https://api.github.com/repos/%s/pulls/%s" % (REPO, created["number"]))
commits = call("GET", "https://api.github.com/repos/%s/pulls/%s/commits" % (REPO, created["number"]))
print("readback state=%s merged=%s mergeable=%s base=%s head=%s commits=%d files=%s +%s -%s" % (
    back["state"], back["merged"], back.get("mergeable"), back["base"]["sha"][:9],
    back["head"]["sha"][:9], len(commits), back.get("changed_files"),
    back.get("additions"), back.get("deletions")))
