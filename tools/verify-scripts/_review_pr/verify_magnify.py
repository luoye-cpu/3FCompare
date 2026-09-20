# 独立验算：子窗口放大 + 偏移 + 裁剪（含 letterbox 适配）
#
# 定义（DIP，容器坐标）：
#   cell   = 格矩形 (cx, cy, cw, ch)
#   z      = 放大倍数 >= 1
#   (u, v) = 要露出的区域左上角，**源画面归一化坐标**（0~1），须在 [0, 1-1/z]
#   fit    = 画面在窗口中的适配矩形（letterbox 居中，与内核 CalculateVideoDestination 同构）
#
# 换算：
#   W.size = cell.size * z                       （窗口 = 格的 z 倍）
#   R.x    = fit.x + u*fit.w                     （区域左上 = 适配矩形内 u 处）
#   R.y    = fit.y + v*fit.h
#   R.size = fit.size / z                        （区域尺寸 = 适配尺寸的 1/z）
#   W.pos  = cell.pos - (R.x, R.y)               （窗口左上 = 格左上 − 区域左上）
#
# 目标：格内露出的内容 == 源画面 [u, u+1/z] x [v, v+1/z]，且各路用同一 (z,u,v) 时
#       露出的源区间相同（无缝）


def fit_dest(sw, sh, ow, oh, limit_to_native=False):
    """内核 CalculateVideoDestination 的同构实现（aspect-preserving contain，居中）。
    limit_to_native 对应内核的 limitToNativeSize：C# 侧从不开启（默认 false），
    此处保留该参数只为能用内核的第三条 static_assert 做交叉验证。"""
    if sw <= 0 or sh <= 0 or ow <= 0 or oh <= 0:
        return (0, 0, 1, 1)
    if limit_to_native and sw <= ow and sh <= oh:
        return ((ow - sw) // 2, (oh - sh) // 2, sw, sh)
    w, h = ow, oh
    if ow * sh <= oh * sw:
        h = max(1, (ow * sh + sw // 2) // sw)
    else:
        w = max(1, (oh * sw + sh // 2) // sh)
    w, h = min(w, ow), min(h, oh)
    return ((ow - w) // 2, (oh - h) // 2, w, h)


def magnify(cell, z, u, v, sw, sh):
    cx, cy, cw, ch = cell
    if not (z > 1.0):
        # z <= 1：恒等退化 —— 窗口 == 格，区域 == 整个窗口（不裁剪），与现状完全一致
        return (cx, cy, cw, ch), (0.0, 0.0, cw, ch), (0, 0, int(round(cw)), int(round(ch)))
    m = 1.0 - 1.0 / z
    u = min(max(u, 0.0), m)
    v = min(max(v, 0.0), m)
    Ww, Wh = cw * z, ch * z
    # 源尺寸未知（演示模式）⇒ 无从计算 letterbox，按"画面铺满窗口"处理（fit = 整个窗口）
    if sw > 0 and sh > 0:
        fx, fy, fw, fh = fit_dest(sw, sh, int(round(Ww)), int(round(Wh)))
    else:
        fx, fy, fw, fh = 0, 0, Ww, Wh
    Rx, Ry = fx + u * fw, fy + v * fh
    Rw, Rh = fw / z, fh / z
    W = (cx - Rx, cy - Ry, Ww, Wh)
    R = (Rx, Ry, Rw, Rh)
    return W, R, (fx, fy, fw, fh)


FAILED = []


def check(name, cell, z, u, v, sw, sh):
    W, R, fit = magnify(cell, z, u, v, sw, sh)
    cx, cy, cw, ch = cell
    # 期望的源区间：u/v 先按 [0, 1-1/z] 钳制（z<=1 时恒为 0）
    if not (z > 1.0):
        eu_u, ev_v, span = 0.0, 0.0, 1.0
    else:
        span = 1.0 / z
        eu_u = min(max(u, 0.0), 1.0 - span)
        ev_v = min(max(v, 0.0), 1.0 - span)
    errs = []
    # 1) 区域必须在窗口内（否则格内留洞）
    if R[0] < -1e-9 or R[1] < -1e-9 or R[0] + R[2] > W[2] + 1e-9 or R[1] + R[3] > W[3] + 1e-9:
        errs.append("区域越出窗口")
    # 2) 区域在容器坐标下必须正好落在格上
    if abs((W[0] + R[0]) - cx) > 1e-9 or abs((W[1] + R[1]) - cy) > 1e-9:
        errs.append("区域与格未对齐")
    # 3) 区域覆盖的源画面比例必须正好是 [u, u+1/z] x [v, v+1/z]（钳制后）
    su = (R[0] - fit[0]) / fit[2]
    eu = (R[0] + R[2] - fit[0]) / fit[2]
    sv = (R[1] - fit[1]) / fit[3]
    ev = (R[1] + R[3] - fit[1]) / fit[3]
    if abs(su - eu_u) > 1e-9 or abs(eu - (eu_u + span)) > 1e-9:
        errs.append(f"源x区间错 {su}..{eu} 期望 {eu_u}..{eu_u + span}")
    if abs(sv - ev_v) > 1e-9 or abs(ev - (ev_v + span)) > 1e-9:
        errs.append(f"源y区间错 {sv}..{ev} 期望 {ev_v}..{ev_v + span}")
    # 4) 放大倍数：窗口相对格是 z 倍（z<=1 时是恒等）
    zz = z if z > 1.0 else 1.0
    if abs(W[2] - cw * zz) > 1e-9 or abs(W[3] - ch * zz) > 1e-9:
        errs.append("窗口尺寸 != 格 x z")
    if errs:
        FAILED.append(name)
    print(("OK  " if not errs else "FAIL"), name,
          "W=%s R=%s fit=%s" % (tuple(round(x, 3) for x in W),
                                tuple(round(x, 3) for x in R), fit), errs)


# --- 用内核的 static_assert 交叉验证 fit_dest ---
assert fit_dest(1920, 1080, 1280, 1024) == (0, 152, 1280, 720), fit_dest(1920, 1080, 1280, 1024)
assert fit_dest(1080, 1920, 1920, 1080) == (656, 0, 608, 1080), fit_dest(1080, 1920, 1920, 1080)
assert fit_dest(640, 360, 1920, 1080, True) == (640, 360, 640, 360), fit_dest(640, 360, 1920, 1080, True)
print("fit_dest 与内核 static_assert 一致 ✓")

check("无 letterbox z=2 u=0.25", (100, 50, 400, 300), 2, 0.25, 0.0, 800, 600)
check("无 letterbox 右下角 z=2", (100, 50, 400, 300), 2, 0.5, 0.5, 800, 600)
check("letterbox 16:9 in 4:3 z=2", (0, 0, 400, 300), 2, 0.25, 0.0, 1920, 1080)
check("ABC 格A(500x600) z=2", (0, 0, 500, 600), 2, 0.25, 0.25, 1920, 1080)
check("ABC 格B(500x300) z=2", (500, 0, 500, 300), 2, 0.25, 0.25, 1920, 1080)
check("z=4 极端裁剪 u=0.75", (0, 0, 400, 300), 4, 0.75, 0.75, 1920, 1080)
check("z=1 退化（应等价现状）", (0, 0, 400, 300), 1, 0.0, 0.0, 1920, 1080)
check("裁剪越界自动钳制 u=0.9,z=2", (0, 0, 400, 300), 2, 0.9, 0.9, 1920, 1080)
check("未知源尺寸（演示模式）", (0, 0, 400, 300), 2, 0.25, 0.25, 0, 0)

# --- 跨路一致性：不同格的 A/B 在相同 (z,u,v) 下露出的源区间必须相同 ---
print("\n跨格一致性（源 16:9）：")
CASES = [
    ("ABC: A=500x600(5:6) vs B=500x300(5:3)", (0, 0, 500, 600), (500, 0, 500, 300)),
    ("极端: A=800x300(8:3) vs B=300x800(3:8)", (0, 0, 800, 300), (300, 0, 300, 800)),
]
for title, ca, cb in CASES:
    na, nb = "A", "B"
    _, Ra, fa = magnify(ca, 2, 0.25, 0.25, 1920, 1080)
    _, Rb, fb = magnify(cb, 2, 0.25, 0.25, 1920, 1080)
    sa = ((Ra[0] - fa[0]) / fa[2], (Ra[0] + Ra[2] - fa[0]) / fa[2])
    sb = ((Rb[0] - fb[0]) / fb[2], (Rb[0] + Rb[2] - fb[0]) / fb[2])
    ok = abs(sa[0] - sb[0]) < 1e-9 and abs(sa[1] - sb[1]) < 1e-9
    if not ok:
        FAILED.append("跨格一致 " + title)
    print(f"  [{title}]")
    print(f"    含 letterbox 修正：{na} 源x={tuple(round(x, 3) for x in sa)}  "
          f"{nb} 源x={tuple(round(x, 3) for x in sb)}  -> {'OK' if ok else 'FAIL'}")

    # 反例：忽略 letterbox（直接把格当画面算区域）时 A/B 露出的源区间不一致
    naive = {}
    for n, c in [(na, ca), (nb, cb)]:
        z, u = 2, 0.25
        cx, cy, cw, ch = c
        Ww, Wh = cw * z, ch * z
        fx, fy, fw, fh = fit_dest(1920, 1080, int(round(Ww)), int(round(Wh)))
        rx = u * cw * z  # 朴素方案：把"画面"当成整窗
        naive[n] = ((rx - fx) / fw, (rx + cw - fx) / fw)
    same = abs(naive[na][0] - naive[nb][0]) < 1e-9
    print(f"    反例(忽略 letterbox)：{na} 实际源x={tuple(round(x, 3) for x in naive[na])}  "
          f"{nb} 实际源x={tuple(round(x, 3) for x in naive[nb])}"
          f"  -> {'一致' if same else '不一致 ⇒ 证明必须做 letterbox 修正'}")

print("\n结果:", "全部通过 ✓" if not FAILED else f"失败 {FAILED} ✗")
