# -*- coding: utf-8 -*-
"""把实验树里这几笔产品改动移植到出货镜像树（上游 PR 用）。

移植的内容（**只带产品语义，不带实验钩子**）：
  1) B1：DescribeInput 补 7 枚格式 + 逐枚 static_assert
  2) 输出档位阶梯合并成 constexpr SwapChainFormatForOutputBits + 12 条 static_assert
  3) scRGB 优先：EnsureSwapChain 的守卫放开到 SDR 请求，fallbackReason 只在真降级时写
  4) PR #25 那条 rung 注释的更正（它引用的数字不能当本树的推荐依据）

刻意不移植：FFF3FP_UPLOAD_COPY_MODE=us/bulk/stream、UPLOAD_RING、MAX_FRAME_LATENCY、
UPLOAD_FORCE_DYNAMIC、SDR_SCRGB 这些测量门（镜像树本来就没有，见 grep 读数），
以及宿主 Form1.vb 的 FFF3FP_DECODER 门。

每条锚点要求恰好命中一次；行尾形态改前改后必须一致。
"""
import re
import sys

KERN = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp"
MIR = r"C:\PLAN\3FCompare\third_party\fff_project\FFF.Native\3FP\Render\VideoRenderer.cpp"


def load(path):
    raw = open(path, "rb").read()
    crlf, lf = raw.count(b"\r\n"), raw.count(b"\n")
    if not (crlf == lf or crlf == 0):
        sys.exit("ABORT 混行尾 %s crlf=%d lf=%d" % (path, crlf, lf))
    return raw.decode("utf-8").replace("\r\n", "\n"), crlf, lf


k, _, _ = load(KERN)
m, m_crlf, m_lf = load(MIR)
print("mirror 行尾: %s (LF=%d)" % ("CRLF" if m_crlf else "LF", m_lf))


def span(text, start_marker, end_marker, tag):
    i = text.index(start_marker)
    j = text.index(end_marker, i) + len(end_marker)
    assert text.count(start_marker) == 1, "%s 起点不唯一" % tag
    return text[i:j]


# --- 1) B1：七枚表 + 注释 + 断言，逐字取自实验树 ---------------------------------------
rows = span(k, "    // Semi-planar 4:2:2 and 4:4:4.",
            "    case AV_PIX_FMT_YUV440P:\n        return {1, 8, 1.0f, 0, 1};", "B1 表体")
asserts = span(k, "// Every format added to the table above is pinned here:",
               "DescribeInput(AV_PIX_FMT_YUV440P).chromaHeightShift == 1);", "B1 断言")
nolayout0 = span(k, "// None of these may fall through to the CPU conversion route any more;",
                 "DescribeInput(AV_PIX_FMT_YUV440P).layout != 0);", "B1 不退 layout 0 断言")

anchor = "    case AV_PIX_FMT_P216LE:\n        return {2, 16, 1.0f, 1, 0};\n"
assert m.count(anchor) == 1, "镜像树 P216LE 锚点命中 %d 次" % m.count(anchor)
if "AV_PIX_FMT_NV16" in m:
    print("  SKIP B1 表体（已在镜像树里）")
else:
    m = m.replace(anchor, anchor + rows, 1)
    print("  OK B1 表体 +%d 行" % rows.count("\n"))

# 断言块：接在镜像树已有的 P212 断言之后
a_anchor = ("static_assert(DescribeInput(AV_PIX_FMT_P212LE).sampleScale == 65535.0f / 65520.0f);\n")
if a_anchor not in m:
    a_anchor = "    DescribeInput(AV_PIX_FMT_P212LE).sampleScale == 65535.0f / 65520.0f);\n"
assert m.count(a_anchor) == 1, "镜像树 P212 断言锚点命中 %d 次" % m.count(a_anchor)
if "DescribeInput(AV_PIX_FMT_NV16)" in m:
    print("  SKIP B1 断言（已存在）")
else:
    m = m.replace(a_anchor, a_anchor + asserts + "\n" + nolayout0 + "\n", 1)
    print("  OK B1 断言 +%d 行" % (asserts.count("\n") + nolayout0.count("\n")))

# --- 2) 档位阶梯合并 --------------------------------------------------------------------
ladder = span(k, "// The single ladder behind every output-buffer format.",
              "static_assert(SwapChainFormatForOutputBits(OutputBitDepthForSource(16, true)) ==\n"
              "    DXGI_FORMAT_R16G16B16A16_FLOAT);", "档位阶梯块")
head_old = "    if (hdr) return 16;\n    if (sourceBitDepth > 8) return 10;\n    return 8;\n}\n"
assert m.count(head_old) == 1, "OutputBitDepthForSource 尾部锚点命中 %d 次" % m.count(head_old)
if "SwapChainFormatForOutputBits" in m:
    print("  SKIP 档位阶梯（已存在）")
else:
    m = m.replace(head_old, head_old + "\n" + ladder + "\n", 1)
    print("  OK 档位阶梯 +%d 行" % ladder.count("\n"))

for old, new, tag in [
    ("    description.Format = outputBits >= 16 ? DXGI_FORMAT_R16G16B16A16_FLOAT :\n"
     "        (outputBits >= 10 ? DXGI_FORMAT_R10G10B10A2_UNORM :\n"
     "            DXGI_FORMAT_B8G8R8A8_UNORM);",
     "    description.Format = SwapChainFormatForOutputBits(outputBits);", "CreateSwapChain 阶梯"),
    ("    const auto format = formatBits >= 16 ? DXGI_FORMAT_R16G16B16A16_FLOAT :\n"
     "        (formatBits >= 10 ? DXGI_FORMAT_R10G10B10A2_UNORM :\n"
     "            DXGI_FORMAT_B8G8R8A8_UNORM);",
     "    const auto format = SwapChainFormatForOutputBits(formatBits);", "ReconfigureSwapChain 阶梯"),
]:
    if m.count(old) == 1:
        m = m.replace(old, new, 1)
        print("  OK", tag)
    elif new in m:
        print("  SKIP", tag, "（已合并）")
    else:
        sys.exit("ABORT %s 锚点命中 %d 次" % (tag, m.count(old)))

# --- 3) scRGB 优先：放开守卫 + 只在真降级时写理由 ---------------------------------------
g_old = "    if (requestedMode_ == FFF3FPColorMode::MapToHdr) {\n        // Wide-gamut SDR also needs scRGB to retain colors outside Rec.709."
g_new = ("    // The Auto policy applies to a plain MapToSdr caller too: the tier ladder prefers the\n"
         "    // 16-bit scRGB chain for anything deeper than 8 bits, so a session that only asked for\n"
         "    // SDR is still promoted when the display can carry scRGB -- which is what\n"
         "    // FFF3FPConfiguration.sdrScRgbMode documents. Keeping the whole block inside\n"
         "    // `requestedMode_ == MapToHdr` made the policy a no-op for the normal SDR caller;\n"
         "    // measured here: a 10-bit HEVC source stayed at outBits=10 with the gate forced on.\n"
         "    if (requestedMode_ == FFF3FPColorMode::MapToHdr || sdrScRgbMode_ != 0) {\n"
         "        // Wide-gamut SDR also needs scRGB to retain colors outside Rec.709.")
if m.count(g_old) == 1:
    m = m.replace(g_old, g_new, 1)
    print("  OK 守卫放开")
elif "|| sdrScRgbMode_ != 0) {" in m:
    print("  SKIP 守卫已放开")
else:
    sys.exit("ABORT 守卫锚点命中 %d 次" % m.count(g_old))

r_old = ("        try { std::lock_guard fallbackLock(fallbackMutex_);\n"
         "            fallbackReason_ = nextMode == requestedMode_ ? std::string{} : reason;\n"
         "        } catch (...) {}")
r_new = ("        // Only a caller that actually asked for HDR can be *demoted*: a promotion out of\n"
         "        // MapToSdr must not be reported as a fallback reason.\n"
         "        const auto demoted = requestedMode_ == FFF3FPColorMode::MapToHdr &&\n"
         "            nextMode != requestedMode_;\n"
         "        try { std::lock_guard fallbackLock(fallbackMutex_);\n"
         "            fallbackReason_ = demoted ? reason : std::string{};\n"
         "        } catch (...) {}")
if m.count(r_old) == 1:
    m = m.replace(r_old, r_new, 1)
    print("  OK fallbackReason 只在真降级时写")
elif "const auto demoted = requestedMode_" in m:
    print("  SKIP fallbackReason（已改）")
else:
    sys.exit("ABORT fallbackReason 锚点命中 %d 次" % m.count(r_old))

# --- 4) PR #25 那条 rung 注释的更正 ------------------------------------------------------
c_old = ("//          7680x4320  Map 22.266 ms  (15.213 ms, 1.46x) -- more than a 60 fps\n"
         "//                      frame period, so the upload alone would cap presentation.\n")
c_new = ("//          7680x4320  Map 22.266 ms  (15.213 ms, 1.46x) -- more than a 60 fps\n"
         "//                      frame period, so the upload alone would cap presentation.\n"
         "// The UpdateSubresource column above is the ORIGINAL author's own measurement, and this\n"
         "// machine does not reproduce it: measured here on the same content, staging-with-ring\n"
         "// and a single Map(WRITE_DISCARD) cost the same at 8K (11.529 vs 11.509 ms, because\n"
         "// CopyResource is queued and hidden inside the next frame's copy), and UpdateSubresource\n"
         "// is 4.6% SLOWER at 4K (unjudged at 8K -- two runs of one mode already differ by 8.9-9.7%,\n"
         "// same size as the effect). So the bracketed numbers are NOT a recommendation to switch\n"
         "// this ladder to UpdateSubresource -- if anyone reads them that way, the reading is wrong.\n"
         "// What is measured and load-bearing here is the ring: with a single staging slot the CPU\n"
         "// waits on the previous CopyResource for 2.36-2.99 ms per frame, with two slots 0.0155 ms\n"
         "// (a depth-1 queue cap does not substitute -- 2x2 of latency x ring, 8/8 arms valid).\n")
if m.count(c_old) == 1:
    m = m.replace(c_old, c_new, 1)
    print("  OK rung 注释更正 +%d 行" % c_new.count("\n"))
elif "ORIGINAL author's own measurement" in m:
    print("  SKIP rung 注释（已更正）")
else:
    sys.exit("ABORT rung 注释锚点命中 %d 次" % m.count(c_old))

out = (m.replace("\n", "\r\n") if m_crlf else m).encode("utf-8")
open(MIR, "wb").write(out)
chk = open(MIR, "rb").read()
assert chk.count(b"\n") == (chk.count(b"\r\n") if m_crlf else 0), "行尾被改坏"
print("写回完成 %s：CRLF=%d LF=%d" % ("CRLF" if m_crlf else "LF", chk.count(b"\r\n"), chk.count(b"\n")))
