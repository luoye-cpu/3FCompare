#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""扫一组 FFmpeg DLL 的 configuration 构建串，判 nonfree/GPL 变体。

为什么要有这个文件：发布出去的 FFmpeg 变体决定了义务（GPL 组件 ⇒ 整体加源码要约；
`--enable-nonfree` ⇒ 分发条件更严）。而 `pack.ps1` 只会"拷 *.dll 并数数量"，
数量对了变体错了照样打出"完整版"。这里把"变体对不对"变成一条能跑、能判红、能自检的门禁。

用法：
  py -3 tools/check_ffmpeg_variant.py --dir <包内 ffmpeg-full 目录>
  py -3 tools/check_ffmpeg_variant.py --self-test [--tmpdir <可写目录>]

退出码：
  0  扫到构建串，且不含 --enable-nonfree
  1  扫到构建串且**含** --enable-nonfree（点名文件与偏移）
  2  一个构建串都没读到 ⇒ 变体判定**未执行**（不许冒充 0）
  3  自检失败 / 目录不存在 / 读不到任何 DLL（守卫自己坏了）
"""
import argparse
import io
import os
import sys
import tempfile

MARK_NEEDLES = (b'--enable-',)          # 构建串的存在性锚点
NONFREE = b'--enable-nonfree'
GPL = b'--enable-gpl'
VERSION3 = b'--enable-version3'
PREFIX = b'--prefix='
CHUNK = 1 << 20
OVERLAP = 4096


def scan(path):
    """返回 (字节数, 是否读到构建串, nonfree 偏移列表, gpl 命中数, version3 命中数, prefix 样例)。"""
    size = os.path.getsize(path)
    has_cfg = False
    nonfree_at = []
    gpl = 0
    v3 = 0
    prefix = b''
    off = 0
    tail = b''
    with io.open(path, 'rb') as f:
        while True:
            buf = f.read(CHUNK)
            if not buf:
                break
            window = tail + buf
            base = off - len(tail)
            if PREFIX in window:
                i = window.find(PREFIX)
                j = window.find(b' ', i)
                prefix = window[i:(j if j > i else i + 120)]
            if any(n in window for n in MARK_NEEDLES):
                has_cfg = True
            p = 0
            while True:
                k = window.find(NONFREE, p)
                if k < 0:
                    break
                nonfree_at.append(base + k)
                p = k + 1
            gpl += window.count(GPL)
            v3 += window.count(VERSION3)
            tail = window[-OVERLAP:] if len(window) > OVERLAP else window
            off += len(buf)
    return size, has_cfg, nonfree_at, gpl, v3, prefix.decode('ascii', 'replace')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dir')
    ap.add_argument('--self-test', action='store_true')
    ap.add_argument('--tmpdir')
    ap.add_argument('--quiet', action='store_true')
    a = ap.parse_args()

    if a.self_test:
        # 正命中标定：造一个含 nonfree 串的假 DLL，必须被扫出来 ⇒ 探针是活的。
        d = a.tmpdir or tempfile.gettempdir()
        try:
            os.makedirs(d, exist_ok=True)
            probe = os.path.join(d, 'avtest-selfprobe.dll')
            body = (b'\x00' * 4096 + b'FFmpeg configuration "--prefix=/ffbuild/x '
                    + NONFREE + b' ' + GPL + b'"' + b'\x00' * 9000)
            with io.open(probe, 'wb') as f:
                f.write(body)
            size, has_cfg, at, g, v3, pfx = scan(probe)
            ok = (has_cfg and len(at) == 1 and g == 1 and size == len(body))
            os.remove(probe)
        except Exception as e:
            print('SELFTEST_FAILED: %s: %s' % (type(e).__name__, e))
            return 3
        if not ok:
            print('SELFTEST_FAILED: 合成件应有 1 处 nonfree 命中，实得 has_cfg=%s 命中=%d gpl=%d 尺寸=%d/%d'
                  % (has_cfg, len(at), g, size, len(body)))
            return 3
        print('SELFTEST_OK: 探针能扫出合成件里的 nonfree（偏移 %d）' % at[0])
        return 0

    if not a.dir or not os.path.isdir(a.dir):
        print('GUARD_ERROR: 目录不存在：%s' % a.dir)
        return 3
    dlls = sorted(f for f in os.listdir(a.dir) if f.lower().endswith('.dll'))
    if not dlls:
        print('GUARD_ERROR: %s 里没有 .dll ⇒ 无从判定变体' % a.dir)
        return 3

    cfg_seen = 0
    offenders = []
    for name in dlls:
        p = os.path.join(a.dir, name)
        size, has_cfg, at, g, v3, pfx = scan(p)
        if has_cfg:
            cfg_seen += 1
        if at:
            offenders.append((name, size, at, g, v3, pfx))
        if not a.quiet:
            print('%-28s %10d B  构建串=%s  --enable-gpl=%d  version3=%d  nonfree=%d  prefix=%s'
                  % (name, size, 'Y' if has_cfg else 'n', g, v3, len(at), (pfx[:52] if pfx else '-')))

    print('files=%d 含构建串=%d 含 nonfree=%d' % (len(dlls), cfg_seen, len(offenders)))
    if offenders:
        for name, size, at, g, v3, pfx in offenders:
            print('NONFREE %s (%d B) 命中偏移 %s  gpl=%d  prefix=%s' % (name, size, at[:4], g, pfx[:60]))
        print('VERDICT=NONFREE（分发条件最严的一档，需换构建或显式放行）')
        return 1
    if cfg_seen == 0:
        print('VERDICT=UNKNOWN（一个构建串都没读到，变体判定未执行，别当通过）')
        return 2
    print('VERDICT=CLEAN（%d 件带构建串，均无 nonfree）' % cfg_seen)
    return 0


if __name__ == '__main__':
    sys.exit(main())
