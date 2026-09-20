"""Create the upstream PR via the GitHub REST API.

The token is read from the GH_TOKEN environment variable only: it never appears
in argv, never touches disk, and is discarded when the process exits.
"""
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from urllib.parse import quote

OWNER = "Lake1059"
REPO = "FFF_Project"
HEAD = "luoye-cpu:upstream/pr-b-adapter"

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "docs", "upstream", "PR-SUBMISSION-待审核.md")

token = os.environ.get("GH_TOKEN", "").strip()
if not token:
    sys.exit("GH_TOKEN is not set")

with open(SOURCE, "r", encoding="utf-8") as handle:
    text = handle.read()


def slice_between(start_marker: str, end_marker: str) -> str:
    start = text.index(start_marker) + len(start_marker)
    end = text.index(end_marker)
    return text[start:end].strip()


# Title lives inside the fenced block under "## 标题".
title_block = slice_between("## 标题", "## 正文")
fenced = re.search(r"```(?:\w*)\n(.*?)```", title_block, re.DOTALL)
if fenced is None:
    sys.exit("could not locate the fenced title block")
title = " ".join(fenced.group(1).split())

# Body = everything from "### 概要" up to (but excluding) the checklist section.
body = slice_between("### 概要", "## 提交前 checklist")
# Drop a trailing horizontal rule left over from the English summary separator.
body = re.sub(r"\n?---\s*$", "", body).strip()


def api(path: str, method: str = "GET", payload=None):
    request = urllib.request.Request(
        "https://api.github.com" + path,
        method=method,
        headers={
            "Authorization": "Bearer " + token,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "3FCompare-PR-Creator",
        },
        data=None if payload is None else json.dumps(payload).encode("utf-8"),
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


status, info = api("/repos/%s/%s" % (OWNER, REPO))
if status != 200:
    sys.exit("repo lookup failed: %s %s" % (status, info))
base = info["default_branch"]
print("target      : %s/%s (default branch = %s)" % (OWNER, REPO, base))

# The branch name contains a slash, so it has to be percent-encoded in the path.
status, head_info = api("/repos/%s/%s/heads/%s" % (
    "luoye-cpu", REPO, quote("upstream/pr-b-adapter", safe="")))
if status != 200:
    # Non-fatal: the token may not expose the fork's refs even though the push
    # worked. The PR creation call below is the authoritative check.
    print("head branch : lookup unavailable (%s), continuing" % status)
else:
    print("head branch : %s @ %s" % (HEAD, head_info["object"]["sha"][:8]))

print("title       : %s" % title)
print("body        : %d chars, %d lines" % (len(body), body.count("\n") + 1))

status, result = api("/repos/%s/%s/pulls" % (OWNER, REPO), method="POST", payload={
    "title": title,
    "head": HEAD,
    "base": base,
    "body": body,
    "maintainer_can_modify": True,
})
if status != 201:
    sys.exit("PR creation failed (%s): %s" % (status, result))

print("")
print("PR created  : %s" % result["html_url"])
print("number      : #%s" % result["number"])
print("state       : %s" % result["state"])
print("commits     : %s | files: %s | +%s -%s" % (
    result["commits"], result["changed_files"], result["additions"], result["deletions"]))
