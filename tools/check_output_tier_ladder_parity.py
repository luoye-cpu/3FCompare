# -*- coding: utf-8 -*-
"""输出档位阶梯的一致性判据：内核 C++、宿主 VB 回退式、两处文案表必须给同一个答案。

为什么是"文本对撞"而不是反射调用宿主函数：`Form媒体信息.输出色彩` 的入参是
`播放器快照`（由原生结构填充的只读镜像），在测试里造不出可信实例；而这条风险本身
是"同一条阶梯被抄了三份、改动只落在一处"，所以拿**三处的规则式**对撞就是它该有的形状。
判据自己带牙：`--mutate` 会生成一份把内核阈值改坏的临时副本，必须判红才算通过。

参考实现是本文件里的 python（与两处产品代码无关）：
    档位 = 16 若 hdr 否则 (10 若源位深 > 8 否则 8)
"""
import argparse
import os
import re
import sys

ROOT = r"C:\PLAN\3FCompare"
KERNEL = os.path.join(ROOT, r"3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp")
SHIPPING_KERNEL = os.path.join(ROOT, r"third_party\fff_project\FFF.Native\3FP\Render\VideoRenderer.cpp")
HOST_INFO = os.path.join(ROOT, r"3fp\kernel\FFF.Player\Form媒体信息.vb")
SHIPPING_HOST_INFO = os.path.join(ROOT, r"third_party\fff_project\FFF.Player\Form媒体信息.vb")
OVERLAY = os.path.join(ROOT, r"3fp\kernel\FFF.Player\UI\播放器信息图层呈现器.vb")

DEPTH_DOMAIN = (0, 8, 10, 12, 16)


def reference_tier(source_bits, hdr):
    """独立参考实现 —— 不是被测的那两处代码，所以拿它当尺子不是自证。"""
    if hdr:
        return 16
    return 10 if source_bits > 8 else 8


def parse_kernel(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    m = re.search(r"OutputBitDepthForSource\(const std::uint32_t sourceBitDepth,\s*"
                  r"const bool hdr\) noexcept \{(.*?)\n\}", text, re.S)
    if not m:
        return None, "找不到 OutputBitDepthForSource 的函数体"
    body = m.group(1)
    hdr_line = re.search(r"if \(hdr\) return (\d+);", body)
    deep_line = re.search(r"if \(sourceBitDepth > (\d+)\) return (\d+);", body)
    tail = re.search(r"return (\d+);\s*$", body.strip())
    if not (hdr_line and deep_line and tail):
        return None, "函数体形状不认识（三条 return 没逐条抓到）"
    hdr_value = int(hdr_line.group(1))
    threshold = int(deep_line.group(1))
    deep_value = int(deep_line.group(2))
    base = int(tail.group(1))
    got = {}
    for bits in DEPTH_DOMAIN:
        got[bits] = {}
        for hdr in (False, True):
            if hdr:
                got[bits][hdr] = hdr_value
            elif bits > threshold:
                got[bits][hdr] = deep_value
            else:
                got[bits][hdr] = base
    return got, "hdr=%d threshold=>%d deep=%d base=%d" % (hdr_value, threshold, deep_value, base)


def parse_host_fallback(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    m = re.search(r"If\(hdr, (\d+), If\(sourceBits > (\d+), (\d+), (\d+)\)\)", text)
    if not m:
        return None, "找不到宿主的回退阶梯式子"
    hdr_value, threshold, deep, base = (int(x) for x in m.groups())
    got = {}
    for bits in DEPTH_DOMAIN:
        got[bits] = {False: (deep if bits > threshold else base), True: hdr_value}
    return got, "hdr=%d threshold=>%d deep=%d base=%d" % (hdr_value, threshold, deep, base)


def parse_label_tiers(path, marker):
    """文案表：>=16 与 >=10 两道界（宿主与呈现器各一份）。"""
    text = open(path, encoding="utf-8", errors="replace").read()
    if marker == "info":
        hits = re.findall(r"If outputBits >= (\d+) Then", text)
    else:
        hits = re.findall(r"Case (\d+) : Return \"", text)
    return [int(h) for h in hits], text


def compare(name, got, path, notes):
    ok = True
    for bits in DEPTH_DOMAIN:
        for hdr in (False, True):
            want = reference_tier(bits, hdr)
            have = got[bits][hdr]
            if want != have:
                ok = False
                notes.append("   [FAIL] %s 源位深=%d hdr=%s：期望档位 %d，读到的式子给 %d"
                             % (name, bits, hdr, want, have))
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mutate", action="store_true",
                    help="自我对撞：把内核阈值改成 >10 再跑一遍，必须判红")
    a = ap.parse_args()

    kernel_path = KERNEL
    if a.mutate:
        text = open(kernel_path, encoding="utf-8").read()
        bad = text.replace("if (sourceBitDepth > 8) return 10;",
                           "if (sourceBitDepth > 10) return 10;", 1)
        if bad == text:
            print("[FAIL] 变异没改动任何东西 —— 锚点不存在，判据可能是死的")
            return 1
        kernel_path = os.path.join(ROOT, r"_3fc_tmp\mutated_VideoRenderer.cpp")
        open(kernel_path, "w", encoding="utf-8").write(bad)
        print("（变异臂：内核阈值改成 >10，期望下面判红）")

    notes = []
    all_ok = True
    for label, path in (("内核·实验树", kernel_path), ("内核·出货树", SHIPPING_KERNEL)):
        got, note = parse_kernel(path)
        if got is None:
            notes.append("   [FAIL] %s：%s" % (label, note))
            all_ok = False
            continue
        ok = compare(label, got, path, notes)
        all_ok = all_ok and ok
        print("%-18s %s  （%s）" % (label, "一致" if ok else "判红", note))
    for label, path in (("宿主回退·实验树", HOST_INFO), ("宿主回退·出货树", SHIPPING_HOST_INFO)):
        got, note = parse_host_fallback(path)
        if got is None:
            notes.append("   [FAIL] %s：%s" % (label, note))
            all_ok = False
            continue
        ok = compare(label, got, path, notes)
        all_ok = all_ok and ok
        print("%-18s %s  （%s）" % (label, "一致" if ok else "判红", note))

    # 文案表的两道界必须与阶梯同源（16 / 10），否则"档位对、标签错"
    for label, path, marker in (("宿主文案", HOST_INFO, "info"),):
        vals, _ = parse_label_tiers(path, marker)
        want = [16, 10]
        if vals != want:
            all_ok = False
            notes.append("   [FAIL] %s：界值读到 %s，期望 %s" % (label, vals, want))
        else:
            print("%-18s 一致  （>=16 / >=10）" % label)
    vals, _ = parse_label_tiers(OVERLAY, "overlay")
    # 呈现器那张表是 Case 8/10/16 三个明确档位
    if sorted(set(vals) & {8, 10, 16}) != [8, 10, 16]:
        all_ok = False
        notes.append("   [FAIL] 呈现器文案：Case 档位读到 %s，期望含 8/10/16" % vals)
    else:
        print("%-18s 一致  （Case 8/10/16）" % "呈现器文案")

    for line in notes:
        print(line)
    if a.mutate:
        print("结论: 变异臂%s（判红=对据有牙）" % ("判红了" if not all_ok else "**没判红 —— 判据是死的**"))
        return 0 if not all_ok else 1
    print("结论: " + ("四处阶梯同源" if all_ok else "判红：档位式在四处中至少一处不同水"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
