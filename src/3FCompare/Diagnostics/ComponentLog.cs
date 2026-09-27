using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace _3FCompare.Diagnostics;

/// <summary>
/// 组件名常量表（固定词表，避免各处拼写漂移）。
///
/// <para>用 <c>const string</c> 而不是 <c>enum</c>：调用点直接传常量，无需 <c>ToString()</c> 或查表，
/// 埋点路径上零额外分配。日志里出现的就是这几个词，用 <c>grep "\[Sync\]"</c> 即可按组件过滤。</para>
/// </summary>
public static class Comp
{
    /// <summary>引擎/会话（打开、就绪、关闭、路数变化、设备变化）。</summary>
    public const string Engine = "Engine";
    /// <summary>视频表面（子 HWND 创建/销毁/resize）。</summary>
    public const string Surface = "Surface";
    /// <summary>呈现/Redraw/视图变换。</summary>
    public const string Render = "Render";
    /// <summary>同步与停滞恢复（停滞检测、轻量恢复、漂移校正）。</summary>
    public const string Sync = "Sync";
    /// <summary>覆盖层/对比模式（模式进出、叠加开关、分割、SetWindowRgn）。</summary>
    public const string Overlay = "Overlay";
    /// <summary>抓屏（WGC/GDI 选路与回退）。</summary>
    public const string Capture = "Capture";
    /// <summary>主题切换。</summary>
    public const string Theme = "Theme";
    /// <summary>设置读写。</summary>
    public const string Settings = "Settings";
    /// <summary>进程环境（第三方注入钩子在场检测等"不是我们代码产生的"外部条件）。</summary>
    public const string Env = "Env";
    /// <summary>崩溃钩子自身（仅由本类写入）。</summary>
    public const string Crash = "Crash";
}

/// <summary>
/// 组件生命周期日志（诊断期专用）——回答"崩溃前各组件在做什么 / 生命周期走到哪一步"。
///
/// <para><b>与 <c>AppLog</c> 的分工</b>：<c>AppLog</c> 是通用落盘日志（后台队列 + 专用线程，
/// UI/解码线程零阻塞）；本类是<b>结构化事件流</b>（固定字段：时间戳/组件/事件/路号/数值），
/// 面向"事后按组件 + 路号重建时间线"。两者文件并列（<c>app-*.log</c> / <c>component-*.log</c>）。</para>
///
/// <para><b>⚠ 为什么必须逐条 flush（本项目的已知坑）</b>：
/// stdout 与文件流都是<b>块缓冲</b>的。进程被硬崩（原生访问违例 / <c>std::terminate</c> /
/// <c>Environment.FailFast</c>）终止时，CLR 不会替我们跑任何"退出前冲刷"的代码，
/// 缓冲区里那几百字节——恰恰就是崩溃前最后几秒的日志——随进程一起消失，
/// 于是"日志系统"在唯一真正需要它的时刻交白卷。
/// 所以本类<b>不使用</b>后台队列/批量写：每一条 <see cref="Log(string,string)"/> 都同步
/// <c>WriteLine + Flush</c>，把数据推进 OS 页缓存。页缓存的生命周期属于内核，
/// 进程死掉后依然在磁盘上（下一次 <c>Read</c>/<c>copy</c> 就能看到），这才是"硬崩不丢"的保证。
/// 代价是每条一次写系统调用——组件事件不是逐帧量（见下方"轻量"约定），完全可以承受。</para>
///
/// <para><b>⚠ 唯一的例外：<see cref="LogThrottled"/></b>（#24）。<c>WM_SIZE</c> 这类埋点会被
/// 用户"拖动窗口边框"连续触发（每次鼠标移动一次），若每条都同步落盘，磁盘卡顿/杀软扫描时
/// 就在 UI 线程上冻结界面——这与本文件"移除 WndProc 高频 WriteLine"的既有结论自相矛盾
/// （等于把同步 I/O 又请回 UI 线程）。故这类埋点走 <see cref="LogThrottled"/>：
/// 仍<b>立即写进缓冲</b>（内容不丢、顺序不变、ring buffer 照记），只是把"推进 OS 页缓存"
/// 合并到 <see cref="FlushThrottleMs"/> 一次。<b>权衡与保证边界</b>：</para>
/// <list type="bullet">
/// <item><description>正常埋点（<see cref="Log(string,string)"/> 及全部既有调用点）<b>语义完全不变</b>，
/// 仍逐条 flush ⇒ "硬崩不丢"的保证对它们依旧成立；</description></item>
/// <item><description>被节流的只有 resize 突发的两条埋点，最坏情况是"硬崩且崩前
/// &lt; <see cref="FlushThrottleMs"/> 内除 resize 外再无其它埋点"时丢掉这几行。
/// 该窗口比心跳周期（<see cref="HeartbeatIntervalMs"/> = 1s）<b>更短</b>，
/// 而心跳本身每条都 flush ⇒ 日志对"崩前各组件在做什么"的<b>时间分辨率（1s）不受影响</b>，
/// 只是把"最后 1 秒"的分辨率从"逐条"放宽到"200ms 粒度"；</description></item>
/// <item><description>进程被<b>托管</b>异常/ExitProcess 终止时不受影响：崩溃钩子与
/// <see cref="Shutdown"/> 仍会显式 <see cref="Flush"/>，连缓冲里的节流行一起带走。
/// 只有"原生硬崩且撞在节流窗口内"这一条路径会丢 ≤<see cref="FlushThrottleMs"/> 的行。</description></item>
/// <item><description>不采用"后台线程 + 队列"的理由：那会把"硬崩不丢"从"已推进页缓存"
/// 退化成"已入队"，丢的窗口从 200ms 变成"队列未及时消费的任意时长"，而且新增了
/// 线程/队列/停机次序三个失败面。节流是"改动面最小、丢的窗口有硬上界"的那个选择。</description></item>
/// </list>
///
/// <para><b>崩溃钩子只是兜底</b>：<see cref="AppDomain.CurrentDomain"/> 的未处理异常、
/// Avalonia UI 线程未处理异常、未观察任务异常都会再 dump 一次内存 ring buffer。
/// 但原生侧硬崩（dxgi Present 访问违例）时这些钩子<b>根本不会被执行</b>——
/// 真正的保障是上面那条"逐条 flush"，钩子只负责在托管异常路径上多留一份现场。</para>
///
/// <para><b>轻量约定（埋点必须遵守）</b>：高帧率路径上的调用点先用
/// <see cref="IsEnabled"/> 短路，再做字符串拼接；事件本身只在<b>状态变化</b>时记
/// （resize / 模式切换 / 停滞 / 设备变化），<b>不要</b>逐帧记。</para>
///
/// <para><b>开关</b>：默认开启（诊断期）。置环境变量 <c>FFF_NO_COMPONENT_LOG=1</c>，
/// 或把 <see cref="Enabled"/> 设为 false（设置项）即可关闭，开销降为零。</para>
/// </summary>
public static class ComponentLog
{
    /// <summary>内存 ring buffer 容量（条）。逐条已落盘，它只用于"写入失败 / 钩子来得及跑"时兜底。</summary>
    public const int RingCapacity = 500;

    /// <summary>组件日志保留天数。与 <c>AppLog</c>（24 小时）不同：诊断日志需要跨天比对，
    /// 故留长一些，但仍必须清理——否则便携目录会无限膨胀。</summary>
    public const int RetentionDays = 7;

    private static readonly object WriteLock = new();
    private static StreamWriter? _writer;
    private static FileStream? _stream;
    private static string? _path;
    private static DateTime _dateStamp;
    private static bool _initialized;

    /// <summary>上次把数据推进 OS 页缓存（<c>StreamWriter.Flush</c>）的时刻，
    /// <see cref="Environment.TickCount64"/> 口径；-1 = 尚未落盘过。
    /// <para>只在 <see cref="WriteLock"/> 内写，<see cref="LogThrottled"/> 无锁读
    /// （<see cref="Volatile"/>）——读到稍旧的值只会让本拍多落一次盘，不影响正确性。</para></summary>
    private static long _lastFlushTicks = -1;

    /// <summary>实际落盘次数（自测/单测钩子，见 <see cref="FlushCount"/>）。</summary>
    private static int _flushCount;
    /// <summary>高频埋点因节流跳过落盘的次数（自测/单测钩子，见 <see cref="ThrottledSkipCount"/>）。</summary>
    private static int _throttledSkipCount;

    /// <summary>高频埋点的最小落盘间隔（毫秒）。
    /// <para>取 200ms 的依据：① 远小于心跳周期 <see cref="HeartbeatIntervalMs"/>（1s），
    /// 故不会降低日志的取证时间分辨率；② 远大于窗口拖动时的 WM_SIZE 间隔（~16ms@60Hz），
    /// 故一次连续拖动只会落盘一次而不是每帧一次——UI 线程上的同步 I/O 次数降一个数量级。</para></summary>
    public const int FlushThrottleMs = 200;

    /// <summary>实际落盘次数（自测/单测用；生产代码不读）。</summary>
    internal static int FlushCount => Volatile.Read(ref _flushCount);

    /// <summary>因节流而跳过落盘的次数（自测/单测用；生产代码不读）。
    /// 判据：<c>&gt; 0</c> 说明"高频埋点确实不再逐条同步落盘"（#24 回归）。</summary>
    internal static int ThrottledSkipCount => Volatile.Read(ref _throttledSkipCount);

    /// <summary>节流判定（纯函数，便于单测逐条验算边界）。
    /// <paramref name="lastFlushTicks"/> &lt; 0（从未落盘）时必须放行 —— 否则进程刚起来
    /// 若第一条埋点就是高频埋点，它会一直等到下一次非节流埋点才落盘。</summary>
    internal static bool ShouldFlushNow(long nowTicks, long lastFlushTicks, int throttleMs)
        => lastFlushTicks < 0 || nowTicks - lastFlushTicks >= throttleMs;

    /// <summary>日志是否可用。<c>volatile</c>：写侧（Initialize/Shutdown）与读侧（各组件线程的
    /// <see cref="IsEnabled"/> 短路）无锁并发；布尔读的可见性延迟只会让一两条日志晚一拍，
    /// 不影响正确性。</summary>
    private static volatile bool _ready;

    // ---- ring buffer（环形数组，避免 Queue 的扩容与 GC）----
    private static readonly string[] Ring = new string[RingCapacity];
    private static int _ringNext;
    private static int _ringCount;

    /// <summary>日志总开关（默认开）。关闭后所有写入调用立即返回。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>当前是否真的会落盘。埋点调用点用它短路，避免白拼字符串。</summary>
    public static bool IsEnabled => Enabled && _ready;

    /// <summary>当前日志文件完整路径（未初始化或已关闭时为 null）。</summary>
    public static string? CurrentFile => _path;

    // ══════════ 心跳（低频存活探针） ══════════

    /// <summary>心跳间隔（毫秒）。
    /// <para><b>1 秒一条</b>（原为 5 秒，P1 取证实测后下调）：4 路崩溃有 6/8 落在
    /// Play 之后 1.5~5s 的"空闲播放"段，5 秒周期使其中 4 次连一条"播放中"样本都没拿到，
    /// 于是"崩前 presented 是否还在增长"这个最关键的问题无法回答。1 秒周期保证
    /// 任何 ≥1s 的存活区间都至少留下一条样本，同时仍稀到不污染按组件过滤的事件流
    /// （单次 30s 运行约 30 行 × ~300B ≈ 9KB，可忽略）。</para></summary>
    public const int HeartbeatIntervalMs = 1000;

    /// <summary>上次心跳的 <see cref="Environment.TickCount64"/>；-1 = 尚未发过（首拍立即发）。</summary>
    private static long _lastHeartbeatTick = -1;

    /// <summary>
    /// 心跳节流闸门：距上次心跳不足 <see cref="HeartbeatIntervalMs"/> 时返回 false。
    ///
    /// <para><b>为什么节流放在本类</b>：调用点是 UI 轮询（250ms 一拍）。若让调用点各自
    /// 记时间戳，"1 秒"这个节奏就会散落在 UI 里、每处都要重复一份；放在本类，
    /// 心跳周期与其它埋点约定同源（换周期只改这一个常量）。</para>
    ///
    /// <para><b>零分配、无锁</b>：只读一个 <c>long</c> 加一次 CAS，没有字符串、没有锁，
    /// 因此可被高频轮询安全调用——真正落盘仍是 1 秒一条。CAS 同时充当并发去重：
    /// 多线程同时判定时只有一个能拿到 true。</para>
    /// </summary>
    public static bool HeartbeatDue()
    {
        if (!IsEnabled) return false;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastHeartbeatTick);
        if (last >= 0 && now - last < HeartbeatIntervalMs) return false;
        return Interlocked.CompareExchange(ref _lastHeartbeatTick, now, last) == last;
    }

    /// <summary>logs 目录（与 <c>AppLog</c> 同一个，文件名前缀不同）。</summary>
    public static string LogsDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 初始化（幂等 + 线程安全）。在 <c>Program.Main</c> 里紧随 <c>AppLog.Initialize()</c> 调用，
    /// 以便捕获最早期的组件事件。
    /// </summary>
    public static void Initialize()
    {
        lock (WriteLock)
        {
            if (_initialized) return;
            _initialized = true;

            // 环境变量关闭（与 FFF_NO_KERNEL_LOG 同一命名风格，便于排障时成对使用）
            if (Environment.GetEnvironmentVariable("FFF_NO_COMPONENT_LOG") == "1")
            {
                Enabled = false;
                return;
            }

            try
            {
                Directory.CreateDirectory(LogsDirectory);
                PurgeOldLogs();

                _dateStamp = DateTime.Today;
                _path = BuildPath(_dateStamp);
                // FileShare.ReadWrite：允许取证工具（tools/crashscope 之类）在应用运行中读取本文件
                _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    // ⚠ AutoFlush 必须为 false（#24）。它原先为 true，作用是"逐条 flush 的第二重保险"，
                    // 但它对**每一次 Write 调用**都强制冲刷底层流 —— 那会让 LogThrottled 的节流
                    // 完全失效（写缓冲后立刻被 AutoFlush 推下去），等于把同步 I/O 又请回 UI 线程。
                    // 安全性没有降低：本类所有写路径都在写完后**显式** Flush（WriteLineLocked /
                    // DumpRing / RollFileLocked / Shutdown / 公开的 Flush），
                    // 唯一的例外是 LogThrottled 刻意不落盘的那部分，这正是节流的目的。
                    AutoFlush = false,
                };
                _ready = true;

                WriteLineLocked($"══════════ 组件日志 会话开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                                $"PID={Environment.ProcessId} ══════════", flush: true);
                WriteLineLocked($"文件={Path.GetFileName(_path)} 组件词表=" +
                                $"{Comp.Engine}/{Comp.Surface}/{Comp.Render}/{Comp.Sync}/" +
                                $"{Comp.Overlay}/{Comp.Capture}/{Comp.Theme}/{Comp.Settings}/{Comp.Env}",
                                flush: true);
            }
            catch
            {
                // 日志初始化失败绝不影响主程序（与 AppLog 同一策略）：静默降级为无日志运行
                _writer = null;
                _stream = null;
                _ready = false;
            }

            InstallCrashHooks();
        }
    }

    /// <summary>冲刷并关闭。进程退出钩子里调用。</summary>
    public static void Shutdown()
    {
        lock (WriteLock)
        {
            FlushWriterLocked();   // 把节流窗口里还没落盘的行一起带走
            try { _writer?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _writer = null;
            _stream = null;
            _ready = false;
        }
    }

    /// <summary>显式冲刷（崩溃钩子用；正常路径每条已自行 flush，高频埋点按 <see cref="FlushThrottleMs"/> 节流）。</summary>
    public static void Flush()
    {
        lock (WriteLock)
        {
            FlushWriterLocked();
        }
    }

    /// <summary>落盘并记录时刻/计数。必须在 <see cref="WriteLock"/> 内调用。</summary>
    private static void FlushWriterLocked()
    {
        try { _writer?.Flush(); } catch { /* 磁盘满 / 杀软独占：丢日志但绝不抛 */ }
        _lastFlushTicks = Environment.TickCount64;
        _flushCount++;
    }

    // ══════════ 写入 API ══════════
    //
    // 全部重载最终汇聚到 Log(comp, evt, route, detail)。
    // 调用点约定：if (!ComponentLog.IsEnabled) return;  之后再拼 detail 字符串。

    /// <summary>记一条事件（无路号、无数值）。</summary>
    public static void Log(string component, string evt)
        => Log(component, evt, -1, null);

    /// <summary>记一条事件（带路号）。</summary>
    public static void Log(string component, string evt, int route)
        => Log(component, evt, route, null);

    /// <summary>记一条事件（带路号 + 自由数值串）。</summary>
    /// <remarks>刻意<b>不</b>提供 <c>(…, string key, double/long value)</c> 之类的重载：
    /// 整数实参会同时匹配 double 与 long 两个重载而报 CS0121 二义性，
    /// 给后来者埋一个只在传字面量时才暴露的坑。调用点用插值串拼 <c>k=v</c> 即可，
    /// 反正都已被 <see cref="IsEnabled"/> 短路保护。</remarks>
    public static void Log(string component, string evt, int route, string? detail)
    {
        if (!IsEnabled) return;
        WriteLineLocked(Format(component, evt, route, detail), flush: true);
    }

    /// <summary>
    /// 高频埋点：内容与 <see cref="Log(string,string,int,string?)"/> 完全相同（同样立即写进缓冲、
    /// 同样进 ring buffer），但**落盘受 <see cref="FlushThrottleMs"/> 节流**。
    ///
    /// <para><b>只给"会被用户连续拖动触发"的埋点用</b>（当前唯一调用点：<c>PlayerSurface</c> 的
    /// WM_SIZE <c>Resize</c> 与紧随其后的 <c>RedrawRequest</c>）。别处一律用 <see cref="Log(string,string,int,string?)"/>：
    /// 逐条 flush 才是本类"硬崩不丢最后一条"的保证，放宽它必须有"这条埋点本身会被高频触发"的理由。</para>
    ///
    /// <para><b>为什么不是"不写盘"</b>：本方法<b>一定会写</b>（进 <see cref="StreamWriter"/> 缓冲），
    /// 只是把"推进 OS 页缓存"合并到下一次落盘。而下一次落盘一定会到来：任何一次普通
    /// <see cref="Log(string,string,int,string?)"/>（心跳 1s 一条）、<see cref="Flush"/>、
    /// <see cref="Shutdown"/> 都会把它一起带走。故丢失窗口有硬上界 = <see cref="FlushThrottleMs"/>。</para>
    /// </summary>
    public static void LogThrottled(string component, string evt, int route, string? detail)
    {
        if (!IsEnabled) return;
        var flush = ShouldFlushNow(Environment.TickCount64, Volatile.Read(ref _lastFlushTicks), FlushThrottleMs);
        WriteLineLocked(Format(component, evt, route, detail), flush);
    }

    /// <summary>
    /// 安装 Avalonia UI 线程未处理异常钩子。由 <c>AppEntry.Initialize()</c> 调用
    ///（那里已经有同类钩子；本类不去碰 <c>Dispatcher.UIThread</c>，避免在 Avalonia 启动前
    /// 提前实例化 Dispatcher）。
    /// </summary>
    public static void InstallUiHook()
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                try
                {
                    Log(Comp.Crash, "UiUnhandledException", -1,
                        $"{e.Exception.GetType().Name}: {e.Exception.Message}");
                    DumpRing("UiUnhandledException");
                    Flush();
                }
                catch { /* 崩溃钩子内绝不抛 */ }
            };
        }
        catch { /* Avalonia 未就绪时静默跳过 */ }
    }

    // ══════════ 内部 ══════════

    private static string Format(string component, string evt, int route, string? detail)
    {
        // 预估容量：时间戳 12 + 组件/事件名 + 余量，一次分配到位（避免 StringBuilder 扩容）
        var sb = new StringBuilder(96);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(component).Append("] ").Append(evt);
        if (route >= 0) sb.Append(" r=").Append(route.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(detail)) sb.Append(' ').Append(detail);
        return sb.ToString();
    }

    /// <summary>写入一行：先入 ring buffer，再按 <paramref name="flush"/> 决定是否<b>同步落盘并 flush</b>。
    ///
    /// <para><paramref name="flush"/> = false 只由 <see cref="LogThrottled"/> 传（见该方法的权衡说明）：
    /// 内容已写进缓冲，但留在 <see cref="StreamWriter"/> 里等下一次落盘。</para></summary>
    private static void WriteLineLocked(string line, bool flush)
    {
        lock (WriteLock)
        {
            // ring buffer 先行：即便下面的磁盘写入失败，内存里仍留有现场可供钩子 dump
            Ring[_ringNext] = line;
            _ringNext = (_ringNext + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;

            if (_writer is null) return;
            try
            {
                // 跨天滚动：比较 DateTime 值（无分配），比每天每条都格式化日期串便宜
                var today = DateTime.Today;
                if (today != _dateStamp) RollFileLocked(today);

                _writer.WriteLine(line);
                // ⚠ 显式 Flush 是主保障，不是冗余：把缓冲推进 OS 页缓存，
                // 进程随后被硬崩也不会丢这一行（AutoFlush 已关闭，这里是唯一保障）。
                if (flush) FlushWriterLocked();
                else _throttledSkipCount++;
            }
            catch
            {
                // 磁盘满 / 杀软独占 / 网络盘断线：丢日志但绝不抛——
                // 日志写不出去不该把播放器拖垮。
            }
        }
    }

    /// <summary>跨天滚动到新文件。必须在 <see cref="WriteLock"/> 内调用。</summary>
    private static void RollFileLocked(DateTime today)
    {
        try
        {
            FlushWriterLocked();
            _writer?.Dispose();
            _stream?.Dispose();
        }
        catch { }

        try
        {
            _dateStamp = today;
            _path = BuildPath(today);
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,   // 同 Initialize：显式 Flush 才是唯一保障（见那里的注释）
            };
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Comp.Crash}] FileRolled 跨天滚动到 " +
                              $"{Path.GetFileName(_path)}");
            FlushWriterLocked();
        }
        catch
        {
            _writer = null;
            _stream = null;
        }
    }

    private static string BuildPath(DateTime day)
        => Path.Combine(LogsDirectory,
            $"component-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    /// <summary>把 ring buffer 内容追加到日志（崩溃钩子用）。</summary>
    private static void DumpRing(string reason)
    {
        lock (WriteLock)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Comp.Crash}] RingDumpBegin " +
                                  $"reason={reason} count={_ringCount}");
                // 从最旧到最新输出：_ringNext 是"下一个写入位"，即最旧一条（未填满时从 0 起）
                var start = _ringCount < RingCapacity ? 0 : _ringNext;
                for (var i = 0; i < _ringCount; i++)
                {
                    var idx = (start + i) % RingCapacity;
                    var entry = Ring[idx];
                    if (entry is not null) _writer.WriteLine("  | " + entry);
                }
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Comp.Crash}] RingDumpEnd reason={reason}");
                FlushWriterLocked();
            }
            catch { }
        }
    }

    private static int _hooksInstalled;

    private static void InstallCrashHooks()
    {
        if (Interlocked.Exchange(ref _hooksInstalled, 1) != 0) return;

        // ⚠ 硬崩（原生访问违例）时这些钩子不会被执行——它们只覆盖"托管异常走到顶层"的路径。
        // 真正的硬崩保障是逐条 flush（见类注释）。
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    var text = e.ExceptionObject as Exception;
                    Log(Comp.Crash, "UnhandledException", -1,
                        $"terminating={e.IsTerminating} " +
                        (text is null ? e.ExceptionObject?.ToString() ?? "(未知异常对象)"
                                      : $"{text.GetType().Name}: {text.Message}"));
                    DumpRing("UnhandledException");
                    Flush();
                }
                catch { }
            };
        }
        catch { }

        try
        {
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    Log(Comp.Crash, "UnobservedTaskException", -1,
                        $"{e.Exception.GetType().Name}: {e.Exception.Message}");
                }
                catch { }
            };
        }
        catch { }

        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Log(Comp.Crash, "ProcessExit");
                    Flush();
                }
                catch { }
            };
        }
        catch { }
    }

    private static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogsDirectory, "component-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }
}
