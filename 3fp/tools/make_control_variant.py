import io, os, re

GUARD = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Player.Tests\Program.vb"
OUT   = r"C:\PLAN\3FCompare\3fp\build\guard_ab\variants\Program.vb.control"

src = io.open(GUARD, encoding="utf-8").read()

# 把闸函数体**整段**换成「立刻返回」，使唯一差异是函数体本身。
# 之前只插一行 Return 会留下大段不可达代码，DLL 里两种变体内容几乎相同，
# 导致字符级判据无法区分版本（实测踩到）。
开始 = src.index("    Private Sub 引导测试进程的文件关联(参数 As String())")
结束 = src.index("    End Sub", 开始) + len("    End Sub")
原体 = src[开始:结束]

新体 = ('    Private Sub 引导测试进程的文件关联(参数 As String())\n'
        '        Return   \' \u2605\u53d8\u5f02\uff1a\u6574\u6bb5\u6458\u6389\u95f8\uff08control \u7ec4\uff09\n'
        '    End Sub')

src = src[:开始] + 新体 + src[结束:]

# 同时在 Main 的调用点留一个可判别的标记：把调用改成注释掉，确保 control 真的不执行闸
src = src.replace("        引导测试进程的文件关联(参数)\n",
                  "        \' \u2605\u53d8\u5f02\uff1acontrol \u7ec4\u4e0d\u8c03\u95f8\n")

assert "FFF.Player.Tests.Settings." not in src.split("Private Sub 引导测试进程的文件关联")[1].split("End Sub")[0], "变异后函数体仍含哨兵"

io.open(OUT, "w", encoding="utf-8", newline="").write(src)
print("已写出 control 变体：", OUT, os.path.getsize(OUT), "字节")
print("原函数体长度：", len(原体), "→ 新：", len(新体))
