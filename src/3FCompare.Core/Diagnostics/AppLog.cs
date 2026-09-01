using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace _3FCompare.Core.Diagnostics;

/// <summary>
/// 3FCompare 落盘日志（F-LOG）：
/// - 写入 exe 同目录 logs/ 文件夹，按天滚动，保留最近 N 天
/// - 线程安全：后台写队列 + 专用落盘线程（UI/解码线程零阻塞）
/// - 启动即初始化（Program.Main 最先调用），捕获从第一行起的全部内容
/// - 内核侧日志经 NativeLogBridge 汇入同一通道
/// 用法：AppLog.Info("模块", "消息"); AppLog.Error("模块", ex);
/// </summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static StreamWriter? _writer;
    private static string? _logFilePath;
    private static readonly object WriterLock = new();
    private static volatile bool _flushRequested;
    private static Thread? _worker;

    /// <summary>保留时长（小时）。超过即自动清理。</summary>
    public const double RetentionHours = 24;

    /// <summary>当前日志文件完整路径（初始化前为 null）。</summary>
    public static string? CurrentLogFile => _logFilePath;

    /// <summary>logs 目录完整路径。</summary>
    public static string LogsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 初始化（幂等）。必须在 Main 最先调用——早于任何引擎/UI 初始化。
    /// </summary>
    public static void Initialize()
    {
        if (_worker is not null) return; // 幂等
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            PurgeOldLogs();

            var fileName = $"app-{DateTime.Now:yyyy-MM-dd}.log";
            _logFilePath = Path.Combine(LogsDirectory, fileName);

            // 追加模式：同一天多次启动合并到一个文件，用分隔行区分会话
            var isNew = !File.Exists(_logFilePath);
            _writer = new StreamWriter(_logFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = false,
            };

            if (!isNew)
                WriteRaw(string.Empty);
            WriteRaw($"══════════ 会话开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                     $"PID={Environment.ProcessId} ══════════");
            WriteRaw($"版本={typeof(AppLog).Assembly.GetName().Version} " +
                     $"OS={Environment.OSVersion.VersionString} " +
                     $"64bit={Environment.Is64BitProcess}");

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "AppLog.Writer",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }
        catch
        {
            // 日志初始化失败绝不影响主程序：静默降级为无日志运行
            _writer = null;
        }
    }

    /// <summary>信息级。</summary>
    public static void Info(string module, string message) => Enqueue("INFO", module, message);

    /// <summary>警告级。</summary>
    public static void Warn(string module, string message) => Enqueue("WARN", module, message);

    /// <summary>错误级。</summary>
    public static void Error(string module, string message) => Enqueue("ERROR", module, message);

    /// <summary>错误级 + 异常。</summary>
    public static void Error(string module, Exception ex) =>
        Enqueue("ERROR", module, $"{ex.GetType().Name}: {ex.Message}\n    {ex.StackTrace?.Replace("\n", "\n    ")}");

    /// <summary>调试级（默认记录；量大时可按需过滤）。</summary>
    public static void Debug(string module, string message) => Enqueue("DEBUG", module, message);

    /// <summary>
    /// 停机前冲刷队列并关闭文件。OnClosing 时调用。
    /// </summary>
    public static void Shutdown()
    {
        Enqueue("INFO", "App", "会话结束");
        _flushRequested = true;
        try { _worker?.Join(2000); } catch { }
        lock (WriterLock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    // ──────── 内部 ────────

    private static void Enqueue(string level, string module, string message)
    {
        if (_writer is null) return; // 未初始化（极早期崩溃）：丢弃但不抛
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{module}] {message}";
        Queue.Enqueue(line);
    }

    /// <summary>内核/原生侧直通入口（绕过格式化，原生已带时间戳时可用）。</summary>
    public static void Raw(string line) => Queue.Enqueue(line);

    private static void WriteRaw(string line) => _writer?.WriteLine(line);

    private static void WorkerLoop()
    {
        while (true)
        {
            var wrote = false;
            while (Queue.TryDequeue(out var line))
            {
                lock (WriterLock)
                {
                    try { _writer?.WriteLine(line); } catch { }
                }
                wrote = true;
            }
            if (wrote)
            {
                lock (WriterLock) { try { _writer?.Flush(); } catch { } }
            }

            if (_flushRequested && Queue.IsEmpty)
            {
                lock (WriterLock) { try { _writer?.Flush(); } catch { } }
                return;
            }
            // 无内容时低频休眠；有积压时立即继续
            Thread.Sleep(wrote ? 10 : 150);
        }
    }

    private static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddHours(-RetentionHours);
            foreach (var file in Directory.EnumerateFiles(LogsDirectory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }
}

/// <summary>
/// F-LOG：内核日志 sink 安装器（Core 层，可访问 internal Interop）。
/// 把 FFF.Native 内部线程的 UTF-8 日志汇入 AppLog 落盘通道。
/// </summary>
public static class KernelLogBridge
{
    private static Backend.Interop.Fff3FpNativeProbe.FFF3FPLogCallback? _delegate;

    public static void Install()
    {
        _delegate = (context, linePtr) =>
        {
            try
            {
                if (linePtr == nint.Zero) return;
                var line = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(linePtr);
                if (!string.IsNullOrEmpty(line))
                    AppLog.Raw($"[内核] {line}");
            }
            catch { /* 日志回调内绝不抛 */ }
        };
        Backend.Interop.Fff3FpNativeProbe.FFF3FP_SetLogCallback(_delegate, nint.Zero);
        AppLog.Debug("Kernel", "日志 sink 已安装");
    }
}
