"""List open PRs (number, title, head ref, base sha, changed-file count) without echoing the token.

Usage: py gh_pr_status.py [--numbers 29,30]
Credentials come from `git credential fill`; the token is never printed.
"""
import json
import subprocess
import sys
import urllib.request

REPO = "Lake1059/FFF_Project"


def token() -> str:
    proc = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, check=True,
    )
    for line in proc.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("no password= line from git credential fill")


def get(url: str, tok: str):
    req = urllib.request.Request(url, headers={
        "Authorization": f"token {tok}", "User-Agent": "3fp-ledger", "Accept": "application/vnd.github+json",
    })
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.load(resp)


def main() -> None:
    tok = token()
    wanted = None
    if "--numbers" in sys.argv:
        wanted = {int(x) for x in sys.argv[sys.argv.index("--numbers") + 1].split(",")}
    pulls = get(f"https://api.github.com/repos/{REPO}/pulls?state=open&per_page=50", tok)
    print(f"open PRs: {len(pulls)}")
    for pr in pulls:
        if wanted and pr["number"] not in wanted:
            continue
        files = get(f"https://api.github.com/repos/{REPO}/pulls/{pr['number']}/files?per_page=100", tok)
        add = sum(f["additions"] for f in files)
        rem = sum(f["deletions"] for f in files)
        print(f"#{pr['number']} head={pr['head']['label']}:{pr['head']['ref']} base={pr['base']['sha'][:7]} "
              f"files={len(files)} +{add} -{rem} created={pr['created_at']} "
              f"mergeable={pr.get('mergeable_state')}")
        print(f"   title: {pr['title']}")
        for f in files:
            print(f"   {f['status']:<10} +{f['additions']:<5} -{f['deletions']:<5} {f['filename']}")


if __name__ == "__main__":
    main()
