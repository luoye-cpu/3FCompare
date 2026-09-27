#!/usr/bin/env python3
"""崩溃自愈（CrashGuard）端到端自检。

为什么必须用 Python 而不是 bash 驱动：bash 的 $? 会把任何异常终止压成 1，
与"程序自己 return 1"无法区分；而本自检要精确区分
"退出码 0"、"退出码 0xC0000005"、"退出码 -1073741819"（同一码的有符号形式）。
Python 的 subprocess.returncode 能拿到真实的 32 位退出码。

场景（脚本实现前 2 个）：
  1) 前 2 代模拟崩溃   -> 守护应重启 2 次，第 3 代成功 -> 退出码 0
  2) 前 9 代模拟崩溃   -> 重启 3 次后触发上限，停止   -> 退出码 0xC0000005

未实现（属人工步骤，不在本脚本内）：
  3) 反向验证：人工把判定改成"永不崩溃"后重跑场景 1
     -> 守护不应重启，直接把崩溃码原样返回 -> 退出码非 0

用法：python tools/verify_guard_selftest.py [--exe <path>]
"""
import argparse
import os
import subprocess
import sys
import tempfile

EXE = r"C:\PLAN\3FCompare\src\3FCompare\bin\Release\net11.0-windows\3FCompare.exe"
AV = 0xC0000005  # STATUS_ACCESS_VIOLATION


def norm(code):
    """退出码归一到无符号，兼容两种宿主口径。"""
    return code & 0xFFFFFFFF


def run_case(name, crash_times, expect_codes, expect_counter):
    counter = os.path.join(tempfile.gettempdir(), f"3fc_guard_{crash_times}.txt")
    if os.path.exists(counter):
        os.remove(counter)

    p = subprocess.run(
        [EXE, "--guard-selftest", counter, str(crash_times)],
        capture_output=True, text=True, timeout=120,
    )
    code = norm(p.returncode)
    got_counter = None
    if os.path.exists(counter):
        # 必须显式 utf-8 + with：Windows 默认 ANSI 会让中文日志乱码，且不关句柄会泄漏
        with open(counter, encoding="utf-8", errors="replace") as f:
            got_counter = f.read().strip()

    ok = code in expect_codes and got_counter == expect_counter
    print(f"[{'PASS' if ok else 'FAIL'}] {name}")
    print(f"        退出码 = 0x{code:08X}  期望 ∈ {[hex(c) for c in expect_codes]}")
    print(f"        子进程代数 = {got_counter}  期望 {expect_counter}")
    if not ok and p.stderr:
        tail = p.stderr.strip().splitlines()[-6:]
        print("        stderr 尾:", " | ".join(tail))
    return ok


def main():
    global EXE   # 必须在使用 EXE 之前声明
    ap = argparse.ArgumentParser(
        description="CrashGuard 端到端自检（实现 docstring 里的场景 1 / 2）")
    ap.add_argument("--exe", default=EXE, help="被测 3FCompare.exe 路径")
    EXE = ap.parse_args().exe
    if not os.path.exists(EXE):
        print(f"FAIL: exe 不存在 {EXE}")
        return 1

    results = []
    # 场景 1：能自愈（崩溃次数 < 上限 3）
    results.append(run_case(
        "崩溃 2 次后自愈成功", 2, [0], "3"))
    # 场景 2：超过上限 -> 停止重启，退出码为崩溃码
    results.append(run_case(
        "连续崩溃触发上限后停止", 9, [AV], "4"))

    all_ok = all(results)
    print("\n" + ("全部通过" if all_ok else "存在失败"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
