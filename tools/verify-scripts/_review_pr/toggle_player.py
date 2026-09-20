#!/usr/bin/env python3
"""在"PR(v15)"与"基线(v14)"两种形态之间切换隔离播放器环境，用于 A/B 对照。

用法: toggle_player.py pr | base
  pr   : 播放器适配到 API 15（3 处改动）+ 期望部署 PR 内核
  base : 还原为上游原样（API 14）+ 期望部署原版内核
脚本幂等，可重复执行。只改 .review_pr/player_env/ 下的隔离副本。
"""
import io, sys, shutil, os

ROOT = r"C:\PLAN\3FCompare\.review_pr\player_env\fff"
SESS = os.path.join(ROOT, "FFF.Player", "Core", "播放器会话.vb")
INTER = os.path.join(ROOT, "FFF.Player", "Interop", "播放器原生接口.vb")

PR_SESS = [
    ("播放器原生接口.FFF3FP_GetApiVersion() <> 14UI",
     "播放器原生接口.FFF3FP_GetApiVersion() <> 15UI"),
    (".大小 = 原生播放器配置大小, .版本 = 14UI,",
     ".大小 = 原生播放器配置大小, .版本 = 15UI,"),
    (".强制HDR输出 = If(配置.强制HDR输出, 1UI, 0UI)\n",
     ".强制HDR输出 = If(配置.强制HDR输出, 1UI, 0UI),\n                .首选适配器索引 = -1\n"),
]
PR_INTER = [
    ("    Public 强制HDR输出 As UInteger\nEnd Structure",
     "    Public 强制HDR输出 As UInteger\n"
     "    ' API 15 新增：指定由哪个 DXGI 适配器创建 D3D11 设备；-1 保持原有策略。\n"
     "    ' 必须追加在结构体末尾，否则偏移与内核不一致。\n"
     "    Public 首选适配器索引 As Integer\nEnd Structure"),
]


def apply(path, pairs, reverse):
    s = io.open(path, encoding="utf-8").read()
    for a, b in pairs:
        src, dst = (b, a) if reverse else (a, b)
        s = s.replace(src, dst)
    io.open(path, "w", encoding="utf-8", newline="").write(s)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode not in ("pr", "base"):
        print(__doc__)
        return 2
    reverse = (mode == "base")
    apply(SESS, PR_SESS, reverse)
    apply(INTER, PR_INTER, reverse)
    print("已切换到:", "PR(v15)" if not reverse else "基线(v14)")
    for p in (SESS, INTER):
        s = io.open(p, encoding="utf-8").read()
        print("  %-28s v15痕迹=%s 适配器字段=%s" % (
            os.path.basename(p),
            "有" if "15UI" in s else "无",
            "有" if "首选适配器索引" in s else "无"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
