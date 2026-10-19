# -*- coding: utf-8 -*-
"""Api.h 的两处移植：sdrScRgbMode 默认翻成 Auto（含新文档块）、自适应降档注释带上实测数。
锚点全部要求恰好命中一次；行尾形态不变。"""
import sys

K = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h"
M = r"C:\PLAN\3FCompare\third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h"


def load(p):
    raw = open(p, "rb").read()
    crlf, lf = raw.count(b"\r\n"), raw.count(b"\n")
    if not (crlf == lf or crlf == 0):
        sys.exit("ABORT 混行尾 %s" % p)
    return raw.decode("utf-8").replace("\r\n", "\n"), crlf


k, _ = load(K)
m, m_crlf = load(M)


def span(text, a, b, tag):
    assert text.count(a) == 1, "%s 起点不唯一/缺失" % tag
    i = text.index(a)
    j = text.index(b, i) + len(b)
    return text[i:j]


# 1) sdrScRgbMode：整块（文档 + 默认值）
a = span(k, "    // Presentation policy for SDR sources on an Advanced Color display.",
         "std::uint32_t sdrScRgbMode = 1;", "sdrScRgbMode 新块")
old = span(m, "    // Presentation policy for SDR sources on an Advanced Color display.",
           "std::uint32_t sdrScRgbMode = 0;", "sdrScRgbMode 旧块")
if "sdrScRgbMode = 1;" in m:
    print("  SKIP sdrScRgbMode")
else:
    assert m.count(old) == 1
    m = m.replace(old, a, 1)
    print("  OK sdrScRgbMode -> Auto 默认（+%d 行）" % (a.count("\n") - old.count("\n")))

# 2) 自适应降档注释：把"about a millisecond"换成实测数
b = span(k, "    // Adaptive downscale-before-upload.",
         "std::uint32_t adaptiveDownscaleBeforeUpload = 0;", "自适应降档新块")
oldb = span(m, "    // Adaptive downscale-before-upload.",
            "std::uint32_t adaptiveDownscaleBeforeUpload = 0;", "自适应降档旧块")
if "about a millisecond" not in oldb:
    print("  SKIP 自适应注释（已订正）")
else:
    assert m.count(oldb) == 1
    m = m.replace(oldb, b, 1)
    print("  OK 自适应降档注释带上 26 ms 实测（+%d 行）" % (b.count("\n") - oldb.count("\n")))

out = (m.replace("\n", "\r\n") if m_crlf else m).encode("utf-8")
open(M, "wb").write(out)
chk = open(M, "rb").read()
assert chk.count(b"\n") == (chk.count(b"\r\n") if m_crlf else 0), "行尾被改坏"
print("写回完成：CRLF=%d LF=%d" % (chk.count(b"\r\n"), chk.count(b"\n")))
