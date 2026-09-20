#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查询上游 PR 的状态与评论（只读）。token 只经环境变量传入，不落盘、不进 argv。"""
import json
import os
import urllib.request

TOKEN = os.environ.get("GH_TOKEN")
if not TOKEN:
    raise SystemExit("缺少 GH_TOKEN 环境变量")

BASE = "https://api.github.com/repos/Lake1059/FFF_Project"


def get(path):
    req = urllib.request.Request(BASE + path, headers={
        "Authorization": "Bearer " + TOKEN,
        "Accept": "application/vnd.github+json",
        "User-Agent": "3fcompare-pr-check",
    })
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


pr = get("/pulls/9")
print("PR #9: %s" % pr.get("title"))
print("  state      = %s" % pr.get("state"))
print("  merged     = %s" % pr.get("merged"))
print("  merged_at  = %s" % pr.get("merged_at"))
print("  merge_sha  = %s" % pr.get("merge_commit_sha"))
print("  base       = %s <- head %s" % (pr.get("base", {}).get("ref"),
                                         pr.get("head", {}).get("ref")))
print("  commits    = %s, files = %s, +%s -%s" % (pr.get("commits"), pr.get("changed_files"),
                                                   pr.get("additions"), pr.get("deletions")))

for label, path in (("review comments", "/pulls/9/comments"),
                    ("issue comments", "/issues/9/comments")):
    cs = get(path)
    print("\n%s: %d" % (label, len(cs)))
    for c in cs:
        who = (c.get("user") or {}).get("login")
        body = (c.get("body") or "").replace("\r\n", "\n").strip()
        print("  - [%s] %s" % (who, body[:600]))
        if path.endswith("comments") and "path" in c:
            print("      %s:%s  %s" % (c.get("path"), c.get("line") or c.get("original_line"),
                                        (c.get("diff_hunk") or "").split("\n")[-1][:120]))
