using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Threading;

namespace _3FCompare.Core.Diagnostics;

/// <summary>日志级别。数值越大越严重，<see cref="AppLog.MinLevel"/> 之下的一律丢弃。</summary>
public enum LogLevel
{
    /// <summary>调试（默认记录；量大时可调高 MinLevel 关掉）。</summary>
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 3FCompare 落盘日志（F-LOG）：
/// - 写入 exe 同目录 logs/ 文件夹，按天滚动 + 按大小分片，保留最近 N 天
/// - 线程安全：后台写队列 + 专用落盘线程（UI/解码线程零阻塞）
/// - 启动即初始化（Program.Main 最先调用），捕获从第一行起的全部内容
/// - 内核侧日志经 NativeLogBridge 汇入同一通道
/// 用法：AppLog.Info("模块", "消息"); AppLog.Error("模块", ex);
/// 量大的调试日志请先短路：if (AppLog.IsDebugEnabled) AppLog.Debug(...)
/// </summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static StreamWriter? _writer;
    private static string? _logFilePath;
    private static readonly object WriterLock = new();

    /// <summary>初始化专用锁。**不能**复用 WriterLock：
    /// Initialize 会直接调 WriteRaw（不进锁），复用只会让两段逻辑互相牵连。</summary>
    private static readonly object InitLock = new();

    private static volatile bool _flushRequested;
    private static Thread? _worker;

    /// <summary>保留时长（小时）。超过即自动清理。</summary>
    public const double RetentionHours = 24;

    /// <summary>队列上限（行）。
    ///
    /// 为什么必须有界：Raw 是内核日志的直通入口，内核一秒能刷几百行；
    /// 一旦落盘跟不上（磁盘满 / 杀软扫描 / 网络盘），队列只增不减直到 OOM，
    /// 而且死的是**播放器**不是日志——代价完全不成比例（B5）。</summary>
    public const int MaxQueuedLines = 10000;

    /// <summary>单个日志文件的大小上限（字节）。
    ///
    /// 文件名原先只在 Initialize 算一次，PurgeOldLogs 也只在启动时跑一次：
    /// 24 小时连续播放 + 内核逐帧日志，当天那一个文件能涨到 GB 级，
    /// 而"按天滚动"对当天完全无能为力（B6）。</summary>
    public const long MaxLogFileBytes = 32L * 1024 * 1024;

    /// <summary>当前日志分片已写入的字节数（UTF-8 精确值，+2 算 CRLF）。
    /// 用计数器估算而**不是**每条都查 FileInfo——后者是昂贵的系统调用。</summary>
    private static long _bytesWritten;

    /// <summary>当前分片序号（0 = 当天的 app-yyyy-MM-dd.log）。</summary>
    private static int _rolloverIndex;

    /// <summary>当天日期串（yyyy-MM-dd）。用 InvariantCulture 格式化：
    /// 部分区域性的默认日历不是公历，"yyyy" 会给出完全不同的年份。</summary>
    private static string _logDate = string.Empty;

    /// <summary>队列溢出提示是否已发过（0/1，Interlocked）。
    /// 只发一次，否则"提示自己"也会把队列撑爆，形成自我放大的死循环。</summary>
    private static int _overflowReported;

    /// <summary>worker 连续写入失败多少次后自杀退出。</summary>
    private const int MaxConsecutiveWriteFailures = 8;

    /// <summary>低于该级别的日志直接丢弃。默认 Debug（= 全记，与改动前行为一致）。</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    /// <summary>调试级是否开启。调用方用它短路，可以省掉字符串拼接的开销。</summary>
    public static bool IsDebugEnabled => MinLevel <= LogLevel.Debug;

    /// <summary>信息级是否开启。</summary>
    public static bool IsInfoEnabled => MinLevel <= LogLevel.Info;

    /// <summary>警告级是否开启。</summary>
    public static bool IsWarnEnabled => MinLevel <= LogLevel.Warn;

    /// <summary>错误级是否开启。Error 一般不该被关掉，留着只为接口对称。</summary>
    public static bool IsErrorEnabled => MinLevel <= LogLevel.Error;

    /// <summary>当前日志文件完整路径（初始化前为 null）。</summary>
    public static string? CurrentLogFile => _logFilePath;

    /// <summary>logs 目录完整路径。</summary>
    public static string LogsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 初始化（幂等且线程安全）。必须在 Main 最先调用——早于任何引擎/UI 初始化。
    /// </summary>
    public static void Initialize()
    {
        // 幂等 + 线程安全：两个线程同时进来会各建一个 StreamWriter 追加同一个文件，
        // 两个 writer 各自缓冲、交替落盘 ⇒ 日志行互相截断、内容交错（B9）。
        lock (InitLock)
        {
            if (_worker is not null) return; // 幂等
            try
            {
                Directory.CreateDirectory(LogsDirectory);
                PurgeOldLogs();

                _logDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _rolloverIndex = 0;
                _logFilePath = BuildLogFileName(0);
                // 同一天重启是追加：把已存在的字节数算进滚动判据，
                // 否则每次启动都从 0 计，当天的文件可以无限增长
                _bytesWritten = SafeFileLength(_logFilePath);

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
    }

    /// <summary>信息级。</summary>
    public static void Info(string module, string message)
    {
        if (!IsInfoEnabled) return;
        Enqueue("INFO", module, message);
    }

    /// <summary>警告级。</summary>
    public static void Warn(string module, string message)
    {
        if (!IsWarnEnabled) return;
        Enqueue("WARN", module, message);
    }

    /// <summary>错误级。</summary>
    public static void Error(string module, string message)
    {
        if (!IsErrorEnabled) return;
        Enqueue("ERROR", module, message);
    }

    /// <summary>错误级 + 异常。</summary>
    public static void Error(string module, Exception ex)
    {
        if (!IsErrorEnabled) return;
        Enqueue("ERROR", module, $"{ex.GetType().Name}: {ex.Message}\n    {ex.StackTrace?.Replace("\n", "\n    ")}");
    }

    /// <summary>调试级（默认记录；量大时可按需过滤）。
    /// 调用方请用 <c>if (AppLog.IsDebugEnabled)</c> 短路，避免白拼字符串。</summary>
    public static void Debug(string module, string message)
    {
        if (!IsDebugEnabled) return;
        Enqueue("DEBUG", module, message);
    }

    /// <summary>
    /// 停机前冲刷队列并关闭文件。OnClosing 时调用。
    /// </summary>
    public static void Shutdown()
    {
        Enqueue("INFO", "App", "会话结束");
        _flushRequested = true;

        // worker 在 _flushRequested 且队列排空后自行 return。
        // 原先固定 Join(2000)，超时就往下走把 _writer 置 null；而 worker 可能只是在
        // Sleep(150)，醒来后 _writer 已为 null，它的写入被 `?.` 静默丢弃
        // ⇒ 最后一批日志（往往正是崩溃前最有价值的那几行）丢失。
        // 改为等线程真正结束（上限 5s）再释放 writer。
        try
        {
            if (_worker is not null && _worker.IsAlive)
            {
                var deadline = Environment.TickCount64 + 5000;
                while (_worker.IsAlive && Environment.TickCount64 < deadline)
                    Thread.Sleep(10);
            }
        }
        catch { }

        // ⚠ 这里**绝不能**用无超时的 lock：worker 若卡在 WriteLine 里
        //（磁盘满 / 被杀软独占扫描 / 网络盘断线），lock 会一直等下去，
        // 于是"关窗口"变成"程序假死"，用户只能杀进程（B7）。
        // 改成 TryEnter(2s)：拿不到就放弃 flush——宁可丢最后几行日志，
        // 也不能让用户关不掉程序。
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(WriterLock, 2000, ref lockTaken);
            if (lockTaken)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            }
        }
        catch { }
        finally
        {
            if (lockTaken) Monitor.Exit(WriterLock);
            // 无论有没有拿到锁都置 null：worker 循环里 `_writer?.` 会全部变空操作，
            // 下一轮 `_flushRequested && Queue.IsEmpty` 即退出，彻底脱离文件
            _writer = null;
        }
    }

    // ──────── 内部 ────────

    private static void Enqueue(string level, string module, string message)
    {
        if (_writer is null) return; // 未初始化（极早期崩溃）：丢弃但不抛
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{module}] {message}";
        TryEnqueue(line);
    }

    /// <summary>内核/原生侧直通入口（绕过格式化，原生已带时间戳时可用）。</summary>
    public static void Raw(string line)
    {
        // 与 Enqueue 一样必须先判空。原先这里直接 Queue.Enqueue：
        // Initialize 失败时 _writer 为 null，worker 的 `?.` 写不出去，
        // 于是队列只进不出；内核一秒几百行 ⇒ 几分钟就 OOM（B5）。
        if (_writer is null) return;
        TryEnqueue(line);
    }

    /// <summary>入队（带上限）。</summary>
    private static void TryEnqueue(string line)
    {
        if (Queue.Count >= MaxQueuedLines)
        {
            // 队列满 ⇒ 丢弃新行。溢出提示**只发一次**，而且是先挤掉队首腾出位置再写，
            // 不会因为"写提示"又触发一次溢出判断（自我放大）。
            if (Interlocked.Exchange(ref _overflowReported, 1) == 0)
            {
                Queue.TryDequeue(out _);
                Queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [WARN ] [AppLog] " +
                              $"日志队列溢出（上限 {MaxQueuedLines} 行），后续日志将被丢弃");
            }
            return;
        }

        Queue.Enqueue(line);
        // 用 UTF-8 实际字节数累加，作为"是否需要滚动"的判据。
        // 这比每条都 new FileInfo(...) 便宜几个数量级（B6）。
        Interlocked.Add(ref _bytesWritten, Encoding.UTF8.GetByteCount(line) + 2L);
    }

    private static void WriteRaw(string line) => _writer?.WriteLine(line);

    private static void WorkerLoop()
    {
        var consecutiveFailures = 0;
        while (true)
        {
            var wrote = false;
            while (Queue.TryDequeue(out var line))
            {
                lock (WriterLock)
                {
                    try
                    {
                        // 滚动判据只是个 long 比较，超限才碰文件系统，可以逐条调用
                        RollOverIfNeededLocked();
                        _writer?.WriteLine(line);
                        consecutiveFailures = 0;
                    }
                    catch
                    {
                        // 磁盘满 / 杀软独占 / 网络盘断线：连续失败说明这一轮写不进去了。
                        // 与其每 150ms 重试一次无限空转，不如自杀退出（B7）——
                        // 没有日志顶多是难排查，程序被日志线程拖死则是真事故。
                        if (++consecutiveFailures >= MaxConsecutiveWriteFailures) return;
                    }
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

    /// <summary>按大小滚动到下一个分片（B6）。必须在 <see cref="WriterLock"/> 内调用。</summary>
    private static void RollOverIfNeededLocked()
    {
        if (_writer is null) return;                       // 已 Shutdown：不要复活日志
        if (Volatile.Read(ref _bytesWritten) < MaxLogFileBytes) return;

        try
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                _rolloverIndex++;
                var candidate = BuildLogFileName(_rolloverIndex);
                var existing = SafeFileLength(candidate);
                if (existing >= MaxLogFileBytes) continue;  // 这一片也已写满，继续往后找

                try { _writer.Flush(); } catch { }
                _writer.Dispose();
                _logFilePath = candidate;
                _bytesWritten = existing;
                _writer = new StreamWriter(candidate, append: true, Encoding.UTF8)
                {
                    AutoFlush = false,
                };
                WriteRaw($"────────── 日志分片续写 #{_rolloverIndex}" +
                         $"（上一片已达 {MaxLogFileBytes / (1024 * 1024)}MB）──────────");
                return;
            }
        }
        catch
        {
            // 滚动失败就继续往当前文件写，总比丢日志好
        }
    }

    /// <summary>日志文件名：index 0 = app-yyyy-MM-dd.log，其余 = app-yyyy-MM-dd_N.log。
    /// 带 _N 后缀的同样匹配 PurgeOldLogs 的 "app-*.log"，会被一并清理。</summary>
    private static string BuildLogFileName(int index)
        => index <= 0
            ? Path.Combine(LogsDirectory, $"app-{_logDate}.log")
            : Path.Combine(LogsDirectory, $"app-{_logDate}_{index}.log");

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
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

    /// <summary>可选加固：把 logs 目录的 ACL 收紧为"仅当前用户可访问"（B10）。
    ///
    /// ⚠ **默认不调用**，需要时才由部署方显式调用。原因：便携部署下同一份程序
    /// 常被同一台机器上的多个用户先后运行；一旦把目录 ACL 锁给用户 A，
    /// 用户 B 就再也写不进 logs（Initialize 会静默降级成"无日志"），
    /// 这比"日志可被同机其他用户读"更糟——功能没了，而且同样是静默的。
    /// 单用户固定安装（如装进 %LOCALAPPDATA% 下的目录）才适合调用它。</summary>
    public static void HardenLogsDirectoryAccess()
    {
        // ACL API 只在 Windows 上存在；其它平台直接跳过（也避免 CA1416 平台告警）
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            // 用 DirectoryInfo 上的 ACL 扩展方法而不是 Directory.GetAccessControl(string)：
            // 前者自 .NET Core 3.0 起就在框架里，后者是较新版本才加的静态便捷方法
            var info = new DirectoryInfo(LogsDirectory);
            var security = info.GetAccessControl();
            // 切断继承 + 重置为"仅当前用户完全控制"
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.ResetAccessRule(new FileSystemAccessRule(
                Environment.UserName,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // 加固失败不影响日志本身：照样写，只是权限保持原样
        }
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
        Backend.Interop.Fff3FpNativeProbe.FFF3FP_SetLogCallback(
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_delegate), nint.Zero);
        AppLog.Debug("Kernel", "日志 sink 已安装");
    }

    /// <summary>
    /// 卸载内核日志 sink（进程退出前必须调用）。
    /// 内核解码/播放线程生命周期长于托管侧：若不注销，CLR 停机后内核线程仍会反向 P/Invoke
    /// 回调本委托，触发 "Attempt to execute managed code after the .NET runtime thread state
    /// has been destroyed."（coreclr/vm/ceemain.cpp:1750）并使进程以 127 退出——
    /// 表现为"测试全部通过但进程崩溃"。
    /// </summary>
    public static void Uninstall()
    {
        if (_delegate is null) return;
        try
        {
            Backend.Interop.Fff3FpNativeProbe.FFF3FP_SetLogCallback(nint.Zero, nint.Zero);
            AppLog.Debug("Kernel", "日志 sink 已卸载");
        }
        catch { /* 卸载失败不阻塞退出 */ }
        finally
        {
            _delegate = null;
        }
    }
}
