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
            // Flush 会把尚未凑齐一行的残留文本也送进日志，避免最后半行丢失
            _dual.Flush();
            Console.SetError(_original ?? Console.Out);
            _dual = null;
        }
    }

    private sealed class DualWriter : TextWriter
    {
        private readonly TextWriter _stderr;
        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>未凑满一行的残留文本。
        ///
        /// 为什么是 [ThreadStatic] 而不是实例字段 + lock：Console.Error 常被多个
        /// 线程同时写（解码线程、UI 线程、内核回调线程），共享缓冲会把两行各自的
        /// 碎片拼成一行，日志彻底没法看；加锁则让日志变成全局串行点。
        /// 线程私有缓冲两者都避开了（B11）。</summary>
        [ThreadStatic] private static StringBuilder? _pending;

        /// <summary>单行上限：调用方若一直不换行（如进度条式的连续 Write），
        /// 缓冲会无限增长。超过就强制成一条日志。</summary>
        private const int MaxPendingChars = 64 * 1024;

        internal DualWriter(TextWriter stderr) => _stderr = stderr;

        public override void Write(char value)
        {
            _stderr.Write(value);
            AppendChar(value);
        }

        public override void Write(string? value)
        {
            _stderr.Write(value);
            if (!string.IsNullOrEmpty(value)) AppendText(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            _stderr.Write(buffer, index, count);
            if (count > 0) AppendText(new string(buffer, index, count));
        }

        public override void WriteLine(string? value)
        {
            _stderr.WriteLine(value);
            if (value is not null) AppendText(value);
            FlushPending();
        }

        public override void WriteLine()
        {
            _stderr.WriteLine();
            FlushPending();
        }

        public override void Flush()
        {
            _stderr.Flush();
            FlushPending();
        }

        private static StringBuilder Pending => _pending ??= new StringBuilder(256);

        /// <summary>逐字符路径原先**完全不转发**（只写给 stderr）：
        /// Console.Error.WriteLine(char) 之类会整段丢失，与 Write(string) 行为不一致（B11）。</summary>
        private static void AppendChar(char c)
        {
            // 换行由 '\n' 统一触发；'\r' 直接丢掉，否则 "\r\n" 会在行尾留一个 CR
            if (c == '\r') return;
            if (c == '\n')
            {
                FlushPending();
                return;
            }

            var sb = Pending;
            sb.Append(c);
            if (sb.Length >= MaxPendingChars) FlushPending();
        }

        /// <summary>按 '\n' 切分成整行入队：整块缓冲当一条日志会让
        /// 多行内容在日志文件里挤成一行，无法按行检索（B11）。</summary>
        private static void AppendText(string s)
        {
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\n') continue;

                var end = i;
                if (end > start && s[end - 1] == '\r') end--;   // 去掉行尾 CR
                if (end > start) Pending.Append(s, start, end - start);
                FlushPending();
                start = i + 1;
            }

            if (start >= s.Length) return;
            Pending.Append(s, start, s.Length - start);
            if (Pending.Length >= MaxPendingChars) FlushPending();
        }

        private static void FlushPending()
        {
            var sb = _pending;
            if (sb is null || sb.Length == 0) return;
            var line = sb.ToString();
            sb.Clear();
            AppLog.Raw(line);
        }
    }
}
