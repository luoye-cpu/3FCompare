using System;
using System.IO;

namespace _3FCompare.Diagnostics;

/// <summary>拖放诊断的**即时落盘**通道：每次写都 open-append-close，不经任何队列。</summary>
/// <para><b>为什么要单独一个通道</b>（2026-09-26 实测）：排查"从资源管理器拖进来完全没反应"时，
/// 我把判据读在 <c>logs/app-*.log</c> 的 <c>[Drop]</c> 行上，于是连着三轮得出"消息没进回调"。
/// 但同一个活进程在同一时刻，<c>component-*.log</c> 是实时在写的
///（<c>[Render] SetViewTransform</c> 看得见）——也就是**读不到的那一侧才可疑，而不是被测物**。
/// 再看 AppLog 的实现：worker 线程 + 有界队列 + <c>AutoFlush=false</c>，而守护进程与子进程
/// 两个写者共用同一文件名，另有测试会 <c>ResetForTests</c> 换目录/清文件（当天 15:42 那份
/// app 日志被截到 209 字节）。三条都会让"没有行"与"没有事件"长得一模一样。</para>
/// <para>本通道只做一件事：把拖放相关的事件立刻追写到 <c>&lt;程序目录&gt;/logs/drop-trace.txt</c>。
/// 频率极低（人手动拖），所以不用队列、不用节流。只做记录，不参与任何判决。</para>
public static class DropTrace
{
    private static readonly object Gate = new();
    private static bool _headerWritten;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "logs", "drop-trace.txt");

    public static void Line(string what)
    {
        try
        {
            lock (Gate)
            {
                var file = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (!_headerWritten)
                {
                    _headerWritten = true;
                    var iv = "未知";
                    try
                    {
                        var atts = typeof(DropTrace).Assembly.GetCustomAttributes(
                            typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                        if (atts.Length > 0)
                        {
                            iv = ((System.Reflection.AssemblyInformationalVersionAttribute)atts[0])
                                .InformationalVersion;
                        }
                    }
                    catch { }
                    File.AppendAllText(file, Environment.NewLine +
                        $"════ 拖放跟踪 pid={Environment.ProcessId} 版本={iv} " +
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ════{Environment.NewLine}");
                }
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {what}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断通道绝不能把拖放路径带崩：这一行放弃即可。
        }
    }
}
