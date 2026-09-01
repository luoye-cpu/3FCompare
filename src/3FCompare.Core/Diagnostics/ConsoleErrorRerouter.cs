using System;
using System.IO;
using System.Text;

namespace _3FCompare.Core.Diagnostics;

/// <summary>
/// F-LOG 双写器：把 Console.Error 的所有输出同时写入 stderr 管道和 AppLog 落盘队列。
/// 在 Program.Main 最先调用 Install() 后，全代码库原有的 Console.Error.WriteLine
/// 调用自动落盘——无需逐个替换 55 处调用点。
/// </summary>
public static class ConsoleErrorRerouter
{
    private static TextWriter? _original;
    private static DualWriter? _dual;

    /// <summary>安装双写器（幂等）。在 AppLog.Initialize() 之后调用。</summary>
    public static void Install()
    {
        if (_dual is not null) return;
        _original = Console.Error;
        _dual = new DualWriter(_original);
        Console.SetError(_dual);
    }

    /// <summary>恢复原始 stderr（关闭/测试用）。</summary>
    public static void Uninstall()
    {
        if (_dual is not null)
        {
            _dual.Flush();
            Console.SetError(_original ?? Console.Out);
            _dual = null;
        }
    }

    private sealed class DualWriter : TextWriter
    {
        private readonly TextWriter _stderr;
        public override Encoding Encoding => Encoding.UTF8;

        internal DualWriter(TextWriter stderr) => _stderr = stderr;

        public override void Write(string? value)
        {
            _stderr.Write(value);
            if (!string.IsNullOrEmpty(value))
                AppLog.Raw(value);
        }

        public override void WriteLine(string? value)
        {
            _stderr.WriteLine(value);
            if (value is not null)
                AppLog.Raw(value);
        }

        public override void Write(char value) { _stderr.Write(value); }
        public override void Write(char[] buffer, int index, int count)
        {
            _stderr.Write(buffer, index, count);
            if (count > 0)
                AppLog.Raw(new string(buffer, index, count));
        }
        public override void WriteLine() { _stderr.WriteLine(); }
        public override void Flush() => _stderr.Flush();
    }
}
