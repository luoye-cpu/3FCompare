# -*- coding: utf-8 -*-
"""把 scrgb 轮的日志解析成"每臂一行"的表：策略 × 素材 -> outBits / 呈现帧数 / 像素样本。

只从落盘日志取数，不手算比值；像素一列直接给探针打出来的第一个点，方便和另一臂对眼。
"""
import re
import sys

LOG = r"C:\PLAN\3FCompare\_3fc_tmp\scrgb_%s.log" % (sys.argv[1] if len(sys.argv) > 1 else "scrgb2")
text = open(LOG, encoding="utf-8", errors="replace").read()

blocks = re.split(r"^WITNESS ", text, flags=re.M)[1:]
rows = []
for b in blocks:
    head = b.splitlines()[0]
    m = re.match(r"(\S+) (plain|fhdr) scrgb=(\d) p(\d)", head)
    if not m:
        continue
    label, flagtag, pol, p = m.groups()
    out = re.findall(r"^(open|play): .*?outBits=(\d+)", b, re.M)
    pres = re.search(r"presented=(\d+)", b)
    px = re.search(r"\(\s*\d+,\s*\d+\) outBits=(\d+) mode=(\d+) R=(-?[\d.]+) G=(-?[\d.]+) B=(-?[\d.]+)", b)
    mode = re.search(r"outBits=\d+ mode=(\d+)", b)
    fail = re.search(r"Create: (-?\d+)|Open timed out|read failed: (-?\d+)", b)
    note = fail.group(0) if fail else ""
    if mode:
        note += " mode=" + mode.group(1)
    rows.append((label, flagtag, pol, p,
                 "/".join(v for _k, v in out) or "-",
                 pres.group(1) if pres else "-",
                 ("%.4f/%d" % (float(px.group(3)), int(px.group(1)))) if px else "-",
                 ("%.4f/%d" % (float(px.group(4)), int(px.group(1)))) if px else "-",
                 ("%.4f/%d" % (float(px.group(5)), int(px.group(1)))) if px else "-",
                 note))

print("%-13s %-6s %-6s %-3s %-9s %-9s %-24s %s" %
      ("media", "flag", "scrgb", "p", "outBits", "pres", "R|G|B (first pt)", "note"))
for r in rows:
    print("%-13s %-6s %-6s %-3s %-9s %-9s %-24s %s" % (r[0], r[1], r[2], r[3], r[4], r[5],
          (r[6] + "|" + r[7] + "|" + r[8]) if r[6] != "-" else "-", r[9]))
print("\narms=%d  (ARM_NOOPEN/ARM_INVALID in log: %d)" %
      (len(rows), len(re.findall(r"ARM_NOOPEN|ARM_INVALID", text))))
