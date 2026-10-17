using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Diagnostics;

/// <summary>原生崩溃的<b>进程内取证</b>：装一个 vectored exception handler，在进程被原生终止
/// 之前把"崩在哪、从哪来"落到磁盘。
///
/// <para><b>为什么必须有它（本轮的实测缺口）</b>：docs/43/44 定的崩溃形态是原生 AV / 非法指令，
/// 这类终止<b>不跑</b> <c>finally</c>、不跑 <c>AppDomain.ProcessExit</c>，托管侧
/// <c>UnhandledException</c> 也收不到（<see cref="CrashGuard"/> 的类注释把这条讲透了）。
/// 今晚 30 次交错会话崩了 11 次，能拿到的只有 WER 归档的模块名与偏移
/// （<c>dxgi.dll+0x33af0</c> / <c>StackHash_xxxx</c> + <c>PCH_AE_FROM_ntdll+0x161914</c>，
/// 见 <c>docs/50 §四之四</c>）—— <b>没有栈</b>。而本机 WER 不落 minidump，开 minidump
/// 要改机器级注册表（属"改环境"，本仓禁项）。</para>
///
/// <para><b>为什么 VEH 能收到而托管钩子不能</b>：<c>AddVectoredExceptionHandler</c> 注册在
/// ntdll 的异常派发链上（<c>RtlDispatchException</c>），位于任何托管处理器<b>之前</b>，
/// 原生 AV 也会走它。2026-10-01 实机探针证伪过另一条路：往 <c>AppDomain.UnhandledException</c>
/// 里加钩子、再 <c>Marshal.StructureToPtr</c> 写一个坏地址 ⇒ 处理器<b>一次都没进</b>
/// （进程直接以 0xC0000005 消失）。⇒ 只有 VEH 这条路。</para>
///
/// <para><b>两件产物，成本差三个数量级，故默认只落第一件</b>：
/// ① <c>fault-*.txt</c>：异常码、故障地址、IP 落在哪个模块（<see cref="FaultSymbolizer"/>）、
/// 关键寄存器、以及<b>从 RSP 起扫栈</b>得到的"落在某个模块内的返回地址"序列——
/// 有界（4 KB）、无堆分配（预格式化）、几十毫秒内写完。
/// ② <c>fault-*.dmp</c>：完整 minidump（含堆），<b>只在环境变量
/// <c>FC_FAULT_DUMP=1</c> 时才写</b>。理由：9 路 4K 一轮的提交量下完整 dump 会是 GB 级，
/// 而本仓的立场是"旧包/杂项不留存"、C 盘是移动靶；紧凑栈已经能答"是不是我们的帧"，
/// 需要逐行源码时才用 ① 的名字去查离线符号。</para>
///
/// <para><b>处理器内的硬约束</b>：崩的时候堆可能已经是坏的 ⇒ 不许 new、不许 LINQ、不许
/// 递归进日志系统（<see cref="AppLog"/> 的后台写线程可能正是被崩的那个）。栈扫描与格式化
/// 全走指针与固定缓冲，文件写入用 <c>CreateFile/WriteFile</c>。整个处理器包在
/// try/catch 里，任何失败都只是"这次不留痕"，绝不改变进程的终止方式
/// （一律返回 <see cref="EXCEPTION_CONTINUE_SEARCH"/>，不自愈、不吞异常，
/// 退出码与崩溃形态与装它之前<b>逐字节相同</b> —— 这条由
/// <c>NativeFaultRecorderExitCodeUnchanged</c> 一类判据守）。</para></summary>
internal static unsafe class NativeFaultRecorder
{
    /// <summary>紧凑留痕的目录（相对 <c>AppContext.BaseDirectory</c>）。</summary>
    public const string SubDir = "logs/faults";

    /// <summary>非默认产物（完整 dump）的开关：环境变量名。</summary>
    public const string DumpEnvVar = "FC_FAULT_DUMP";

    private const uint EXCEPTION_CONTINUE_SEARCH = 0;
    private const uint EXCEPTION_EXECUTE_HANDLER = 1;   // 本类刻意不使用：绝不吞异常

    // 只给"进程本来就要没了"的那几种码留痕。CLR 自己发的 .NET 异常（0xE0434352 /
    // 0x040911EC 之类 SEH 码）在正常运行的每一刻都会流经 VEH —— 给它们写文件等于
    // 每次托管异常都往磁盘砸一次同步 IO，还会把真正的原生崩溃淹没。
    private const uint CODE_ACCESS_VIOLATION   = 0xC0000005;
    private const uint CODE_IN_PAGE_ERROR      = 0xC0000006;
    private const uint CODE_PRIV_INSTRUCTION   = 0xC0000096;
    private const uint CODE_ILLEGAL_INSTRUCTION = 0xC000001D;
    private const uint CODE_STACK_OVERFLOW     = 0xC00000FD;
    // <c>__fastfail</c>（GS/CET/堆破坏都会走它）。加进来的理由是一条实测，但不是"多一类总好"：
    // 2026-10-03 有一轮（pid=45948）以 0xC0000409 退出，而同一 pid 早 13 秒已经写过一份 0xC0000005 留痕
    // ⇒ 那个 409 是"AV 之后的收尾码"，不是一种独立的死法。把它收进过滤器，是为了
    // **没有前置 AV 的纯 fastfail** 不再静默地什么都不留（原五种里没有它，一旦真发生就是零证据）。
    private const uint CODE_STACK_BUFFER_OVERRUN = 0xC0000409;

    /// <summary>栈扫描窗口（字节）。4 KB 足够覆盖一条深 UI 调用链的返回地址带；
    /// 再大只是在坏栈上读到更多垃圾。命中数与扫描深度都写进留痕，读数的人能自己判断。</summary>
    private const int ScanBytes = 4096;

    /// <summary>最多留几个地址（防一条烂链刷出上千行"看起来像返回地址"的数）。</summary>
    private const int MaxFrames = 64;

    /// <summary>保留最近几份留痕（磁盘卫生：<c>docs</c> 的立场是杂项不留存）。</summary>
    private const int KeepFiles = 8;

    private static FaultSymbolizer? _symbolizer;
    private static IntPtr _handle;
    // 委托必须字段持有：VEH 的原生回调在 GC 看来的确没有托管引用，回收了就是踩空指针。
    private static readonly VectoredHandler _delegate = Handler;

    private delegate uint VectoredHandler(IntPtr exceptionPointers);

    /// <summary>装 handler。<b>必须在原生崩溃之前调用</b>（<c>Program.Main</c> 早期）。
    /// 幂等：重复调用只装一次。</summary>
    /// <param name="baseDirectory">用于定位留痕目录；传 <c>AppContext.BaseDirectory</c>。</param>
    /// <returns>留痕目录（成功）或 <c>null</c>（没装成，调用方须明写而不是沉默）。</returns>
    public static string? Install(string baseDirectory)
    {
        if (_handle != IntPtr.Zero) return _directory;
        try
        {
            var dir = Path.Combine(baseDirectory, SubDir);
            Directory.CreateDirectory(dir);
            _directory = dir;

            BuildTable("install");

            _handle = AddVectoredExceptionHandler(1, _delegate);

            // 装上了必须留一行，否则"仪器没接上"会被读成"没崩"——这是本仓反复踩的假绿。
            // 用 ComponentLog 而不是 AppLog：AppLog 的写入走后台线程，硬崩时那一行还在队列里
            // 就没了（实测：第一次 --faultprobe 之后日志里连"已装 handler"都查不到）；
            // ComponentLog 是逐条 flush 的那一支。
            ComponentLog.Log(Comp.Crash, "FaultRecorder", -1,
                _handle != IntPtr.Zero
                    ? $"installed {_tableState} dir={dir} dump={(WantDump() ? "on" : "off (" + DumpEnvVar + "=1 to enable)")} trace={(TraceEnabled ? "on" : "off")}"
                    : "AddVectoredExceptionHandler FAILED — no in-process crash evidence this run");
            PruneOld(dir);
            return _handle != IntPtr.Zero ? dir : null;
        }
        catch (Exception ex)
        {
            try { AppLog.Error("Fault", $"[NativeFaultRecorder] 安装失败：{ex.Message}"); } catch { }
            return null;
        }
    }

    private static string? _directory;

    // 展开段每进程只试一次：二次异常再走一遍 RtlVirtualUnwind 没有意义，
    // 只会把"哪一份留痕是原发现场"这件事搅浑。
    private static int _walkAttempted;
    private static long _lastTableBuildTicks;
    private static string _tableState = "(未建)";

    /// <summary>崩溃现场不许再枚举进程 —— 表只在安装与布局事件上重建（见
    /// <see cref="MaybeRefreshTable"/>）。</summary>
    /// <param name="reason">建表原因，直接印进留痕 —— 读留痕的人要能判断这张表有多新。</param>
    private static void BuildTable(string reason)
    {
        // ⚠ 用 Process.Modules 而不是 VirtualQuery 逐页问：前者给的就是**模块**（镜像），
        //   后者会把堆/栈/映射文件也算成"私有内存段"，那样"跳到堆上执行"会被洗成
        //   "某模块+偏移"，那是取证里最坏的一种假读数（另见 FaultSymbolizer 的 outside 分支）。
        //   同理只用 ModuleMemorySize（= PE 的 SizeOfImage），不用私有内存大小。
        var ranges = new List<ModuleRange>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule m in self.Modules)
            {
                try
                {
                    var baseAddr = (ulong)m.BaseAddress.ToInt64();
                    var size = (ulong)m.ModuleMemorySize;
                    if (baseAddr == 0 || size == 0) continue;
                    ranges.Add(new ModuleRange(FileNameOf(m.ModuleName), baseAddr, size));
                }
                catch { /* 单个模块取不到不影响整机，跳过 */ }
            }
        }
        catch { /* 枚举整体失败：保留上一版表，别让仪器一夜之间变成瞎的 */ }
        if (ranges.Count == 0) return;                        // 空表比旧表更没用
        var built = new FaultSymbolizer(ranges);
        _tableState = $"modules={built.Count} built={DateTime.Now:HH:mm:ss} reason={reason}";
        _lastTableBuildTicks = Environment.TickCount64;
        _symbolizer = built;      // 整体换引用：并发读侧永远拿到一个完整不可变的表，不会读到半更新
    }

    /// <summary>模块表的<b>节流</b>刷新入口：布局/模式类事件调用，最多每 2 秒真枚举一次。
    ///
    /// <para>为什么必须刷新：表在 <c>Program.Main</c> 早期建，而 <c>dxgi.dll</c> 这类模块是
    /// <b>合成器起交换链之后才加载</b>的。不刷新就会把崩溃带里最有信息量的那一格读成
    /// <c>outside@…</c> —— 2026-10-01 01:14 第一次抓真崩就实测到了：留痕写
    /// <c>ip=outside@0x7fff7bd73af3</c>，而 WER 同一份崩溃报 <c>dxgi.dll+0x33af3</c>，
    /// 差值正是 dxgi 的镜像基址。"仪器只在新模块出现前有效"这种缺陷不报错，
    /// 只会稳定地产出误导性的读数。</para>
    ///
    /// <para>为什么不在崩溃现场刷新：那时堆可能已坏、加载器锁可能正被崩掉的线程自己持着，
    /// 而枚举进程模块既要分配又要走 loader。</para></summary>
    public static void MaybeRefreshTable()
    {
        if (!IsInstalled) return;
        if (Environment.TickCount64 - _lastTableBuildTicks < TableRefreshMinIntervalMs) return;
        BuildTable("layout-event");
    }

    private const long TableRefreshMinIntervalMs = 2000;

    /// <summary>当前模块表的状态串（留痕与判据共用同一份口径，不另写一个读数来源）。</summary>
    public static string TableState => _tableState;

    /// <summary>在崩溃现场取 IP 处若干字节（<c>ReadProcessMemory</c> 读本进程：
    /// 地址不可读时只回 FALSE，不会在 VEH 处理器里二次崩溃 —— 这也是不用裸指针 memcpy 的原因）。
    /// 用途是<b>把"内存里这份代码被改写过吗"变成一条能判的问题</b>：
    /// 拿这串字节和盘上同一份 PE 的同一 RVA 逐字节比，相同=没被动过，不同=有东西在运行时改了指令。</summary>
    private static string DescribeBytesAt(ulong address, int count)
    {
        var buf = stackalloc byte[count];
        nuint got = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), (nint)address, (nint)buf, (nuint)count, ref got) || got == 0)
            return "读不到(err=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")";
        var sb = new StringBuilder((int)got * 3);
        for (nuint i = 0; i < got; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(buf[(int)i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>用 ntdll 自己的解译者把崩溃上下文逐帧展开成<b>真调用链</b>。</summary>
    /// <remarks>
    /// <para>为什么还要这一段：留痕里的 <c>#N</c> 是<b>从 RSP 起扫描</b>的候选返回地址，
    /// 不是展开出来的帧 —— 这条行式已经骗过我一次（docs/50 §三十之二：拿去解符号的 7 个"帧"里 6 个
    /// 是 <c>.data</c>/<c>.idata</c> 里的指针）。扫描能补展开补不到的地方（半改写的现场），
    /// 但"谁调用了出事的那段代码"这个问题只有展开器答得了。</para>
    /// <para>三条硬前置，任何一条不满足就<b>一个字都不写</b>（宁可不给展开帧，也不给错的帧）：
    /// ① <c>sizeof(CONTEXT)==1232</c>；② <c>Rip@248</c>；③ 缓冲 16 字节对齐。
    /// 前两条的期望值由本机真头文件算出来（<c>tests/output/41-context-layout.txt</c>），不是手算；
    /// 结构一旦与 Windows 的布局脱节，这两条会在第一现场就把这条路关掉，
    /// 而不是让 <c>RtlVirtualUnwind</c> 往栈上越界写。</para>
    /// <para><b>调用时机是刻意的</b>：这一段在主记录已经 WriteFile 落盘<b>之后</b>才跑。
    /// 万一展开器自己在二次异常里倒下（被改写过的函数，展开码描述的是旧序言，确实可能歪），
    /// 已经落盘的那份记录不受影响。</para>
    /// <para>诚实标注：在"补丁落地时线程正跑在一条指令中间"这类现场，展开出的帧可能整体失真
    /// （展开码说的是原始函数序言）。所以每帧照旧带 <c>[code]/[!code]</c>，
    /// 读的人要按"与 <c>#N</c> 扫描对不对得上"来判断这一段今天有没有用。</para>
    /// </remarks>
    private static byte[] WalkNativeStack(CONTEXT* faultCtx)
    {
        const int contextBytes = 1232;                 // 实测：sizeof(CONTEXT)
        const int ripOffset = 248;                     // 实测：offsetof(CONTEXT, Rip)
        const int chainLimit = 32;                     // winnt.h:4533 UNWIND_CHAIN_LIMIT

        if (faultCtx is null || _symbolizer is null) return System.Array.Empty<byte>();
        if (System.Runtime.InteropServices.Marshal.SizeOf<CONTEXT>() != contextBytes)
            return Prefix("unwind=跳过（CONTEXT 尺寸 ≠ 1232）");
        if (System.Runtime.InteropServices.Marshal.OffsetOf<CONTEXT>(nameof(CONTEXT.Rip)).ToInt32() != ripOffset)
            return Prefix("unwind=跳过（Rip 偏移 ≠ 248）");
        if (System.Threading.Interlocked.Exchange(ref _walkAttempted, 1) != 0)
            return Prefix("unwind=跳过（本进程已试过展开）");

        var raw = System.Runtime.InteropServices.Marshal.AllocHGlobal(contextBytes + 16);
        var buf = (raw + 15) & ~15;                     // CONTEXT 是 DECLSPEC_ALIGN(16)：展开器会做对齐写
        try
        {
            Buffer.MemoryCopy(faultCtx, (void*)buf, contextBytes, contextBytes);
            var sb = new StringBuilder(256);
            sb.Append("unwind（RtlVirtualUnwind 逐帧；U 开头才是展开出来的调用链，#N 只是扫描命中）:\r\n");
            var frames = 0;
            var stop = "走完 " + chainLimit.ToString(CultureInfo.InvariantCulture) + " 帧上限";
            for (var n = 0; n < chainLimit; n++)
            {
                var controlPc = ((CONTEXT*)buf)->Rip;
                if (controlPc == 0) { stop = "Rip=0"; break; }
                var entry = RtlLookupFunctionEntry(controlPc, out var moduleBase, nint.Zero);
                if (entry == nint.Zero) { stop = "该地址没有函数表项（数据/JIT/无表模块）"; break; }
                var next = RtlVirtualUnwind(0, moduleBase, controlPc, entry, buf,
                                            out _, out _, nint.Zero);
                var callerPc = ((CONTEXT*)buf)->Rip;
                if (callerPc == 0) { stop = "展开后 Rip=0"; break; }
                if (callerPc == controlPc) { stop = "展开后 Rip 未前进（展开码在这里失效，后面几条不可信）"; break; }
                sb.Append("  U").Append(frames.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(Name(callerPc))
                  .Append(" (0x").Append(callerPc.ToString("x16", CultureInfo.InvariantCulture)).Append(')')
                  .Append(IsUnwindableCode(callerPc) ? " [code]" : " [!code]").Append("\r\n");
                frames++;
                if (next == nint.Zero) { stop = "下一帧地址没有函数表项（可能是栈底，也可能是无表模块/被改写点）"; break; }
                stop = "到达帧数上限 " + chainLimit.ToString(CultureInfo.InvariantCulture);
            }
            sb.Append("展开=").Append(frames.ToString(CultureInfo.InvariantCulture))
              .Append(" 帧，终止=").Append(stop).Append("\r\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
        catch
        {
            return Prefix("unwind=抛出（展开这条路自己出异常，主记录已在前面落盘）");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(raw);
        }

        static byte[] Prefix(string why) => Encoding.UTF8.GetBytes("unwind 段：" + why + "\r\n");
    }


    /// <summary>在 IP 附近找一条 5 字节近跳转（<c>E9 rel32</c>），把目标地址解出来交给同一张模块表归名。</summary>
    /// <remarks>
    /// 两种形状都要认，因为现场就是两种都出现过（2026-10-03，11 份留痕）：
    /// <list type="bullet">
    /// <item><b>补丁点本身</b>：IP 正好落在 <c>dxgi.dll+0x335a0</c>，那里内存里是
    ///   <c>E9 5B CA FE BF</c> 而盘上是 <c>48 89 5C 24 10</c>（1 例）；</item>
    /// <item><b>撕裂形</b>：IP 落在同一条补丁的第 2/3/4 个字节上（+0x335a2 ×2、+0x335a3 ×5），
    ///   此时 <c>E9</c> 在 <c>ip-2</c>…<c>ip-4</c>。只认第一种的话，11 例里只有 1 例能给归名 —— 那是仪器失灵。</item>
    /// </list>
    /// 撕裂形带误报风险（随便一个 0xE9 字节都能解出个"目标"），所以加了三道硬条件：
    /// ① 只回看 4 字节；② 解出的目标**不能**落在 IP 自己所属的模块里；
    /// ③ 目标的内存状态必须是"有镜像"或"已提交且可执行"，否则不配叫跳板。
    /// 三条件任一不满足 ⇒ 闭嘴。这一列是**归名线索**，不是判决：读的人仍要问"谁写的这 5 个字节"。</remarks>
    private static string DescribeDetourTarget(ulong rip)
    {
        if (_symbolizer is null) return string.Empty;
        for (var back = 0; back <= 4; back++)
        {
            var at = rip - (ulong)back;
            var buf = stackalloc byte[5];
            nuint got = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), (nint)at, (nint)buf, 5, ref got) || got < 5) continue;
            if (buf[0] != 0xE9) continue;
            uint rel = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
            var target = unchecked((ulong)((long)(at + 5) + (int)rel));
            var where = Name(target);
            if (back > 0)
            {
                // 撕裂形的三道硬条件（见方法注释）。第一种形状（back==0）没有误报问题，不设槛。
                var here = Name(rip);
                if (string.Equals(where, here, StringComparison.Ordinal)) continue;   // 目标还在本模块 ⇒ 不像补丁
                var state = DescribeRegion(target);
                var looksExecutable = state.Contains("MEM_IMAGE", StringComparison.Ordinal)
                                   || state.Contains("exec=True", StringComparison.Ordinal);
                var isGone = state.Contains("MEM_FREE", StringComparison.Ordinal);
                if (!looksExecutable && !isGone) continue;
            }
            return " detour" + (back == 0 ? "@" : "@ip-" + back.ToString(CultureInfo.InvariantCulture)) + "="
                 + where + " (0x" + target.ToString("x16", CultureInfo.InvariantCulture)
                 + "，目标内存=" + DescribeRegion(target) + ")"
                 + DescribeSecondHop(target);
        }
        return string.Empty;
    }

    /// <summary>补丁第一跳落进的那页如果是 <c>FF 25 00 00 00 00</c>（<c>jmp qword ptr [rip+0]</c>），
    /// 把紧跟其后的 8 字节绝对地址解出来，再交给<b>同一张模块表</b>归名。
    ///
    /// <para>为什么非要这一跳：2026-10-04 用 <c>tests/output/25-live-patch-probe.ps1</c> 在**健康进程**里
    /// 量到的整条链是 <c>dxgi.dll+0x335A0</c> →（<c>E9</c> 相对跳）→ <c>dxgi 基址−0x3FFE0000</c> 处一块
    /// 4 KB <c>MEM_PRIVATE / EXECUTE_READWRITE</c> 跳板页 →（<c>jmp [rip+0]</c>）→
    /// <c>RTSSHooks64.dll+0x73540</c>。少了第二跳，留痕只能说"跳出了 dxgi"，说不出"跳进了谁的代码"；
    /// 有了它，崩溃现场自己就带归属。</para>
    ///
    /// <para>只认 <c>disp32==0</c> 这一种形态（现场实测的那一种）；别的间接跳形态等真看到再加。
    /// 与 <see cref="DescribeDetourTarget"/> 同规矩：读不到、形态不对就闭嘴，不猜。</para></summary>
    private static string DescribeSecondHop(ulong target)
    {
        if (_symbolizer is null) return string.Empty;
        var buf = stackalloc byte[14];
        nuint got = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), (nint)target, (nint)buf, 14, ref got) || got < 14)
            return string.Empty;
        if (buf[0] != 0xFF || buf[1] != 0x25) return string.Empty;
        for (var i = 2; i < 6; i++)
            if (buf[i] != 0) return string.Empty;
        ulong abs = 0;
        for (var i = 13; i >= 6; i--) abs = (abs << 8) | buf[i];
        return " →第二跳jmp[rip+0]=" + Name(abs) + " (0x"
             + abs.ToString("x16", CultureInfo.InvariantCulture) + "，内存=" + DescribeRegion(abs) + ")";
    }

    /// <summary>崩溃现场唯一的<b>归名入口</b>：先查缓存表，表漏了这个模块时再问映射文件。
    ///
    /// <para>为什么非要第二道：表是在安装与布局事件上枚举进程的，而 <c>dxgi.dll</c> 这类模块
    /// 要等第一次合成起交换链之后才加载。2026-10-04 04:41 那份留痕里，表是崩溃前 <b>2 秒</b>刚建的
    /// （<c>modules=52 built=04:40:58</c>）却仍然没有 dxgi ⇒ <c>ip=outside@0x…321135a3</c>，
    /// 而同一段 <c>region=</c> 明明写着 <c>type=MEM_IMAGE allocBase=0x…320e0000</c>。
    /// "地址确实在某个 dll 里，但仪器说它是 outside"是这台仪器最容易误导人的一种读数。</para>
    ///
    /// <para>表命中就绝不问第二道：表的判定带 <c>SizeOfImage</c> 上界，比第二道准；
    /// 第二道只在上游已经确认这段是 <c>MEM_IMAGE</c> 时才归名，
    /// 所以"跳到私有可执行内存"这一格不会被洗成模块名（<see cref="MappedImageName"/>）。</para></summary>
    private static string Name(ulong address, bool markFallback = false)
    {
        var viaTable = _symbolizer?.Resolve(address);
        if (viaTable is not null && !viaTable.StartsWith("outside", StringComparison.Ordinal))
            return viaTable;
        var mapped = MappedImageName(address);
        if (mapped is null) return viaTable ?? $"outside@0x{address:x16}";
        // 标一下来源：这条读数不是加载器枚举给的、是现场按映射文件凑的，两者可信度不同，
        // 读的人必须能分辨（不标就会把"表明明漏了这个模块"这件事洗掉）。
        // ⚠ 必须写成**空格分隔的独立字段**而不是把标记粘在值上：
        //   `tests/output/fault-distribution.ps1` 用 `ip=(\S+)` 分桶，粘上去会让同一个
        //   `dxgi.dll+0x335a3` 裂成两桶（有的有标记、有的没有），分布数就废了。
        return markFallback ? mapped + " 来源=映射名" : mapped;
    }

    /// <summary>VirtualQuery 说这一段是 <c>MEM_IMAGE</c> 时，用 <c>GetMappedFileName</c> 拿映射文件路径，
    /// 配合 <c>AllocationBase</c>（对镜像就是基址）算出 <c>名字+0x偏移</c>。
    ///
    /// <para>VEH 里的三条约束都照顾到了：<b>不调加载器</b>（<c>GetModuleHandleEx(FROM_ADDRESS)</c>
    /// 要拿加载器锁，而锁很可能正被崩掉那个线程自己持着 ⇒ 处理器里自锁死）、<b>不枚举模块</b>、
    /// <b>缓冲区在栈上</b>。归不出来就返回 null，由调用方原样保留 <c>outside@…</c>——
    /// 宁可不归名，也不能归错名。</para>
    ///
    /// <para><c>MEM_PRIVATE</c>（钩子跳板、JIT）与 <c>MEM_MAPPED</c> 一律不归名：前者是这台仪器
    /// 要单独区分出来的那一格，后者不是代码镜像。</para></summary>
    private static string? MappedImageName(ulong address)
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery((nint)address, &mbi, (nint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return null;
        if (mbi.Type != MEM_IMAGE) return null;
        var allocBase = (ulong)mbi.AllocationBase;
        // 600 个 wchar 够装 `\Device\HarddiskVolumeN\Windows\SYSTEM32\…' 这种长路径。
        // 回值等于缓冲区大小 ⇒ 说明被截断，截断路径的名字不可信，直接放弃归名。
        Span<char> buf = stackalloc char[600];
        fixed (char* p = buf)
        {
            var len = K32GetMappedFileNameW(GetCurrentProcess(), (nint)address, (nint)p, (uint)buf.Length);
            if (len == 0 || len >= (uint)buf.Length) return null;
            return FaultSymbolizer.TryFormatImageRange(new string(p, 0, (int)len), allocBase, address);
        }
    }

    /// <summary>缓存表读不出 IP 归属时，<b>在崩溃现场直接问内核这块内存是什么</b>。
    ///
    /// <para>一次 <c>VirtualQuery</c>，不枚举模块、不查名字，只给三类事实：
    /// ① <c>MEM_IMAGE</c> ⇒ 这是某个 dll 的镜像，只是安装时那份加载器快照里没有它
    /// （晚加载，或被手动映射进来）；② <c>MEM_PRIVATE + 可执行保护</c> ⇒ 可执行的私有内存
    /// （钩子跳板、JIT 产物，或一个被踩的指针被当成代码执行）；③ 分配基址与镜像基址是否在
    /// 缓存表里。三者把"崩在一个谁都不认识的地址"分成完全不同的病 ——
    /// 没有这一行，仪器就答不了它被造出来要答的那个问题（12 次会话 5 次崩溃里有 3 次
    /// <c>ip=</c> 只有 <c>outside@…</c>，实测于 2026-10-01 01:22–01:27）。</para></summary>
    private static string DescribeRegion(ulong address)
    {
        MEMORY_BASIC_INFORMATION mbi;
        var ok = VirtualQuery((nint)address, &mbi, (nint)sizeof(MEMORY_BASIC_INFORMATION));
        if (ok == 0) return "VirtualQuery失败";
        var type = mbi.Type switch
        {
            MEM_IMAGE => "MEM_IMAGE",
            MEM_MAPPED => "MEM_MAPPED",
            MEM_PRIVATE => "MEM_PRIVATE",
            _ => $"0x{mbi.Type:x}",
        };
        var state = mbi.State == MEM_COMMIT ? "COMMIT" : $"0x{mbi.State:x}";
        var exec = (mbi.AllocationProtect & 0xF0) is 0x10 or 0x20 or 0x40 or 0x80;   // PAGE_*EXECUTE*
        // 分配基址归名：先查缓存表，表里没有这段镜像时由 <see cref="Name"/> 走映射文件那条
        // （MEM_IMAGE 时 AllocationBase 就是模块基址，配上映射路径的叶子名就能给出
        // `dxgi.dll+0x0 来源=映射名` —— 这是崩溃现场唯一不走加载器的归名办法，见 Name 的注释）。
        var allocBase = (ulong)mbi.AllocationBase;
        var owner = _symbolizer is null ? null : Name(allocBase, markFallback: true);
        var ownerTxt = owner is not null && !owner.StartsWith("outside", StringComparison.Ordinal)
            ? $" owner={owner}"
            : " owner=不在缓存表";
        return $"type={type} state={state} exec={exec} allocBase=0x{allocBase:x16} " +
               $"regionSize=0x{mbi.RegionSize:x} protect=0x{mbi.AllocationProtect:x}" + ownerTxt;
    }

    /// <summary>卸载（正常退出时调用；不调也不会错，进程要走时 handler 随进程消失）。</summary>
    public static void Uninstall()
    {
        var h = _handle;
        if (h == IntPtr.Zero) return;
        _handle = IntPtr.Zero;
        try { RemoveVectoredExceptionHandler(h); } catch { }
    }

    /// <summary>当前是否已经装上（判据与日志用，不是给业务用的）。</summary>
    public static bool IsInstalled => _handle != IntPtr.Zero;

    /// <summary><c>--faultprobe</c> 用：造一次**原生**访问违规 —— 崩在 kernel32 的
    /// <c>RtlMoveMemory</c>（<c>CopyMemory</c>）里，把 16 字节往地址 <c>1</c> 上写。
    ///
    /// <para>为什么不用 <c>*(int*)1 = 42</c>：托管代码里的 AV 会被 CLR 包成
    /// <c>AccessViolationException</c> 再抛出（SEH 码 <c>0xE0434352</c>，不是
    /// <c>0xC0000005</c>），本仪器的过滤器会正当得把它挡下 ⇒ 探针"没留痕"到底是仪器坏了
    /// 还是码不对，就分不开了。崩溃带里那些真崩（<c>dxgi.dll+0x33af0</c>、
    /// ntdll 的 syscall 存根）都是<b>原生帧</b>里的 AV，探针必须与它同形才叫验过这条路。</para></summary>
    public static void ForceAccessViolationForProbe()
    {
        var src = stackalloc byte[16];      // 合法源缓冲，确保坏的是目的地址而不是源
        CopyMemory(new IntPtr(1), (nint)src, 16);
    }

    // kernel32 并不导出 CopyMemory（那是头文件里的宏），真正的入口是 RtlMoveMemory。
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(nint dest, nint src, uint count);

    // ───────────────────────────── handler 本体 ─────────────────────────────

    private static uint Handler(IntPtr exceptionPointers)
    {
        try
        {
            if (exceptionPointers == IntPtr.Zero) return EXCEPTION_CONTINUE_SEARCH;
            var ep = (EXCEPTION_POINTERS*)exceptionPointers;
            var rec = ep->ExceptionRecord;
            if (rec == null) return EXCEPTION_CONTINUE_SEARCH;
            if (TraceEnabled) TraceCode(rec->ExceptionCode, (ulong)rec->ExceptionAddress);
            if (!IsFatalNativeCode(rec->ExceptionCode)) return EXCEPTION_CONTINUE_SEARCH;

            WriteRecord(ep);
            if (WantDump()) WriteFullDump(ep, rec->ExceptionCode);
        }
        catch
        {
            // 取证失败绝不改变进程的死亡方式。这里的 catch 是刻意的"哑"：崩溃现场再抛
            // 一个托管异常，只会让原始信息一起没了（同文件开头那条约束）。
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // ── 诊断开关：FC_FAULT_TRACE=1 ──
    // 为什么需要它：本仪器第一次实跑（--faultprobe，2026-10-01）没有落任何留痕，而"没留痕"
    // 有两种完全不同的坏法 —— handler 根本没被调用（装失败/签名错），或调用了但码被
    // 过滤器挡下（CLR 把原生 AV 包成托管异常时 SEH 码是 0xE0434352，不在五种致命码里）。
    // 不开 trace 就分不出这两类，仪器本身就成了第二个未知。trace 只在显式开启时写，
    // 默认零开销：常态下给每一次托管异常都追加一行文件 IO 是不可接受的。
    private static bool? _trace;
    private static bool TraceEnabled =>
        _trace ??= string.Equals(Environment.GetEnvironmentVariable("FC_FAULT_TRACE"), "1",
                                StringComparison.Ordinal);

    private static void TraceCode(uint code, ulong address)
    {
        var dir = _directory;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var path = System.IO.Path.Combine(dir, "trace.txt");
            var line = System.Text.Encoding.UTF8.GetBytes(
                $"code=0x{code:x8} addr=0x{address:x16} managedHint={(code == 0xE0434352 ? "CLR异常" : "-")}\r\n");
            var h = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return;
            try
            {
                SetFilePointer(h, 0, IntPtr.Zero, FILE_END);
                var written = 0u;
                fixed (byte* b = line) WriteFile(h, b, (uint)line.Length, out written, IntPtr.Zero);
            }
            finally { CloseHandle(h); }
        }
        catch { }
    }

    private static bool IsFatalNativeCode(uint code) =>
        code is CODE_ACCESS_VIOLATION or CODE_IN_PAGE_ERROR or CODE_PRIV_INSTRUCTION
            or CODE_ILLEGAL_INSTRUCTION or CODE_STACK_OVERFLOW or CODE_STACK_BUFFER_OVERRUN;

    /// <summary>把异常参数原样打出来，因为**同一句"崩在 X"底下是三种不同的病**：
    /// <list type="bullet">
    /// <item><c>0xC0000005</c>：<c>[0]</c>=0 读 / 1 写 / 8 执行，<c>[1]</c>=被访问的地址
    ///   ⇒ "写飞"与"取指令飞"是完全不同的修法；</item>
    /// <item><c>0xC0000409</c>（<c>__fastfail</c>）：<c>[0]</c> 是子类型码（1=GS cookie、2=快速失败检查、
    ///   4=动态 alloca 探测、5=未定义…）⇒ 只有这一列能把"CET/影子栈被拒"与"编译器栈保护"分开；</item>
    /// <item>其它码：Windows 自己声明 <c>ExceptionInformation</c> 无定义 ⇒ 打出来也只说明"这条没有参数"。</item>
    /// </list>
    /// 权威口径：<c>winnt.h</c> 的 <c>_EXCEPTION_RECORD</c> 注释（0xC0000005 两个参数、0xC000012A 两个参数，
    /// 其余由子系统定义），不是猜的。</summary>
    private static string DescribeExceptionParams(EXCEPTION_RECORD* rec)
    {
        var n = (int)rec->NumberParameters;
        if (n <= 0) return "无参数";
        var sb = new StringBuilder(48);
        for (var i = 0; i < n && i < 15; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("0x").Append(((ulong)rec->ExceptionInformation[i])
                .ToString("x16", CultureInfo.InvariantCulture));
        }
        return n.ToString(CultureInfo.InvariantCulture) + " 个参数=" + sb;
    }

    /// <summary>这个地址在不在"能被展开的代码"里 —— x64 上真代码在 <c>.pdata</c> 必有一条
    /// RUNTIME_FUNCTION，数据地址没有，所以 Windows 自己解栈用的那张表就是最省事的判据
    /// （比自己解析节表可靠：不必抄 <c>IMAGE_SECTION_HEADER</c> 布局）。</summary>
    private static bool IsUnwindableCode(ulong address) =>
        RtlLookupFunctionEntry(address, out _, nint.Zero) != nint.Zero;

    /// <summary>写紧凑留痕。<b>全程不分配托管对象</b>：直接往一个固定缓冲里写 ASCII。</summary>
    private static void WriteRecord(EXCEPTION_POINTERS* ep)
    {
        var dir = _directory;
        if (string.IsNullOrEmpty(dir) || _symbolizer is null) return;

        var rec = ep->ExceptionRecord;
        var ctx = ep->ContextRecord;
        if (rec is null || ctx is null) return;   // 指针不全就什么都别写（宁可少一份留痕，不要一份错留痕）
        var sb = new StringBuilder(4096);   // 唯一一次分配：地址解析要产字符串，省不掉但可以一次
        sb.Append("3FCompare 原生崩溃留痕\r\n");
        sb.Append("time=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" pid=").Append(Environment.ProcessId);
        sb.Append(" code=0x").Append(rec->ExceptionCode.ToString("x8", CultureInfo.InvariantCulture));
        sb.Append(" addr=0x").Append(((ulong)rec->ExceptionAddress).ToString("x16", CultureInfo.InvariantCulture));
        // 异常参数：同一句"崩在 X"底下可能是三种不同的病（读/写/执行、fastfail 的子类型码）。
        // 权威口径见 DescribeExceptionParams 的注释（winnt.h 的 _EXCEPTION_RECORD）。
        sb.Append(" param=").Append(DescribeExceptionParams(rec));
        var ipCached = Name((ulong)ctx->Rip, markFallback: true);
        sb.Append(" ip=").Append(ipCached);
        // 缓存表读成 outside 时补一句"这块内存到底是什么"。一次 VirtualQuery，不枚举模块、不查名字：
        //   MEM_IMAGE ⇒ 是某个 dll 的镜像（只是加载器快照里没有它：晚加载，或手动映射进来的）；
        //   MEM_PRIVATE + PAGE_*EXECUTE ⇒ 可执行的私有内存（钩子跳板 / JIT / 被踩的指针）；
        //   再配上 AllocationBase 是否在缓存表里，就能把"崩在没人认识的地址"分成两类完全不同的病。
        // 为什么必须分这一类：2026-10-01 那 12 次会话里 5 次崩溃，其中 3 次 `ip=` 只有
        // outside@0x7fff8bfc0000 这种读数，占了一半 —— 没有这一行，仪器就答不了它被造出来要答的那个问题。
        if (ipCached.StartsWith("outside@", StringComparison.Ordinal))
            sb.Append("  region=").Append(DescribeRegion((ulong)ctx->Rip));
        // 取 IP 处那 16 个字节（ReadProcessMemory 走本进程：地址坏了只回 FALSE，不会在处理器里二次崩溃）。
        // 为什么要这一列：判"**加载进内存的这份镜像，代码和盘上的文件是不是同一份**"。
        // 现场起因：2026-10-03 一份 0xc000001d(非法指令) 的留痕把 IP 定在 `dxgi.dll+0x335a3`，
        // 而 `dumpbin -disasm` 读盘上那份 dxgi.dll，+0x335a0 处是一条 5 字节指令
        // `48 89 5C 24 10`（mov [rsp+10h],rbx），+0x335a3 落在它**中间** ——
        // 正常返回不会从一条指令的第 4 个字节开始执行（`24 10` 本身是 `and al,10h`，能解码，
        // 所以"非法指令"是**从错误偏移起算**的后继解码，不是这条指令天生非法）。
        // 当时两种解释分不开：内存里被改过 / 我把基址算错了 ⇒ 只能靠下一例。
        // 结果（同一批 12:19 那例，IP 正好停在补丁首字节 +0x335a0）：盘上 `48 89 5C 24 10`，
        // 内存里 `E9 5B CA FE BF` = 一条 5 字节近 jmp，目标 0x7FFE87190000（页对齐，在 dxgi 基址下方约 1 GB；
        // 另一个进程的模块快照里没有镜像盖住它 —— 那只是旁证，跨进程一致性只对系统镜像验过）。
        // ⇒ **基址没错，代码在运行时被改写过**；另两例 `ip=outside@…` 现场 `state=0x10000`(MEM_FREE)
        //   是同进程硬证据：那块代码区当时已经没了。
        // 崩溃带的三个 IP（+0x335a0/+0x335a2/+0x335a3）就是这个补丁点本身与"线程正跑在
        // 这条 5 字节指令中间时补丁落地"的两种半改写取指位置。
        // 有了 ipbytes 就能一条命令判：与盘上同 RVa 的 16 字节逐字节相同 ⇒ 没被改写；
        // 不同 ⇒ 有东西在运行时动过这份代码（钩子/补丁），崩溃带的归属就落到它身上。
        sb.Append(" ipbytes=").Append(DescribeBytesAt((ulong)ctx->Rip, 16));
        // 现场实证（2026-10-03 12:19:05，pid=41896）：`ip=dxgi.dll+0x335a0` 处盘上是
        // `48 89 5C 24 10`（mov [rsp+10h],rbx），内存里是 `E9 5B CA FE BF` —— 一条 5 字节近 jmp，
        // 目标 0x7FFE87190000（页对齐、在 dxgi 下方约 1 GB）。也就是说这份 DXGI 的这段代码
        // 在运行时被**改写过**，而崩溃带的三个 IP（+0x335a0 / +0x335a2 / +0x335a3）正好是
        // 这个补丁点本身、以及"线程跑到一条 5 字节指令中间时补丁落地"的两种半改写形状。
        // 那么"是谁的补丁"就只剩一次解析：把 jmp 的目标地址交给同一张模块表去归名。
        sb.Append(DescribeDetourTarget((ulong)ctx->Rip));
        sb.Append("\r\n");

        // 崩溃带的关键线索之一在这里：IP 落在谁的镜像里。dxgi/ntdll/我们自己的 dll
        // 三种归属是三种完全不同的病（docs/50 §四之四 的分类判据）。
        sb.Append("rsp=0x").Append(((ulong)ctx->Rsp).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" rbp=0x").Append(((ulong)ctx->Rbp).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" rax=0x").Append(((ulong)ctx->Rax).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" rcx=0x").Append(((ulong)ctx->Rcx).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" rdx=0x").Append(((ulong)ctx->Rdx).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" r8=0x").Append(((ulong)ctx->R8).ToString("x16", CultureInfo.InvariantCulture));
        sb.Append("\r\nstack-scan（RSP 起，只保留落在已知模块内的 8 字节对齐候选返回地址）:\r\n");

        var frames = 0;
        var scanned = 0;
        var codeFrames = 0;
        var start = (ulong*)ctx->Rsp;
        var end = (ulong*)((byte*)start + ScanBytes);
        for (var p = start; p < end && frames < MaxFrames; p++)
        {
            scanned += 8;
            var candidate = *p;
            // 用符号表本身当唯一判据：命中模块内才算一帧。
            // ⚠ 不做"看上一个字是不是合法栈帧"那种启发式，那会把堆数据读成代码。
            var where = Name(candidate);
            if (where.StartsWith("outside@", StringComparison.Ordinal)) continue;
            // 第二道属性判据（2026-10-03 加）：这一条**是不是能展开的代码位置**。
            // 为什么必须打这一列：栈扫描会把 .rdata/.idata/.pdata/.data 里的指针读成"帧"，
            // 而 `#N` 的行式极易被当成回溯栈来解。实测（docs/50 §三十）：拿去解符号的 7 个
            // `FFF.Native.dll+0x…` 候选里只有 1 个落在 .text，其余 6 个落在 .data/.didat ——
            // 其中 `+0x4ee9a8` 正是 .data 里一个**名叫 pump 的全局变量**，
            // `+0x51822a/+0x5182c2/+0x51837a` 命中的符号是 `…_NULL_THUNK_DATA_DLB`（延迟载入导入表）。
            // 三天解不出名字不是 PDB 坏了，是那些地址根本不是返回地址。
            bool unwindable = IsUnwindableCode(candidate);
            if (unwindable) codeFrames++;
            sb.Append("  #").Append(frames.ToString(CultureInfo.InvariantCulture)).Append(" ")
              .Append(where).Append(" (0x").Append(candidate.ToString("x16", CultureInfo.InvariantCulture)).Append(")")
              .Append(unwindable ? " [code]" : " [!code]").Append("\r\n");
            frames++;
        }
        sb.Append("扫描=").Append(scanned.ToString(CultureInfo.InvariantCulture)).Append(" 字节，命中=").Append(frames)
          .Append("，其中 [code]=").Append(codeFrames.ToString(CultureInfo.InvariantCulture))
          .Append("（[!code] = 不是可展开的代码：模块内的数据地址／导出转发桩／无展开表的代码，" +
                  "都不能当返回地址去解符号）")
          .Append("，模块表=").Append(_tableState).Append("  ← 表若比崩溃早，晚加载的模块会读成 outside\r\n");

        var path = Path.Combine(dir, $"fault-{DateTime.Now:yyyyMMdd-HHmmss}-{rec->ExceptionCode:x8}.txt");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var h = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return;
        try
        {
            var written = 0u;
            fixed (byte* b = bytes) WriteFile(h, b, (uint)bytes.Length, out written, IntPtr.Zero);
            // 主记录**先落盘**，再试展开（顺序是刻意的：展开器在"代码被改写过"的现场可能自己歪）。
            var walk = WalkNativeStack(ctx);
            if (walk.Length > 0)
            {
                fixed (byte* w = walk) WriteFile(h, w, (uint)walk.Length, out written, IntPtr.Zero);
            }
        }
        finally { CloseHandle(h); }
        try { AppLog.Info("Fault", $"[NativeFaultRecorder] 留痕已写 {path}（命中 {frames} 帧）"); } catch { }
    }

    private static bool WantDump()
    {
        try { return Environment.GetEnvironmentVariable(DumpEnvVar) == "1"; }
        catch { return false; }
    }

    /// <summary>完整 minidump（含堆），只在 <c>FC_FAULT_DUMP=1</c> 时写。</summary>
    private static void WriteFullDump(EXCEPTION_POINTERS* ep, uint code)
    {
        var dir = _directory;
        if (string.IsNullOrEmpty(dir)) return;
        var path = Path.Combine(dir, $"fault-{DateTime.Now:yyyyMMdd-HHmmss}-{code:x8}.dmp");
        var h = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return;
        try
        {
            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = (uint)Environment.CurrentManagedThreadId,
                ExceptionPointers = (IntPtr)ep,
                ClientPointers = true,
            };
            MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, h,
                MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithUnloadedModules
                    | MiniDumpWithProcessThreadData | MiniDumpWithThreadInfo,
                &info, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        finally { CloseHandle(h); }
    }

    private static void PruneOld(string dir)
    {
        // 只留最近 KeepFiles 份：崩一次 GB 级磁盘是禁项，旧包/杂项不留存是本仓既有立场。
        try
        {
            var all = new List<string>(Directory.GetFiles(dir));
            all.Sort(StringComparer.Ordinal);          // 文件名带时间戳 ⇒ 字典序即时间序
            var drop = all.Count - KeepFiles;
            for (var i = 0; i < drop; i++)
                try { File.Delete(all[i]); } catch { }
        }
        catch { }
    }

    private static string FileNameOf(string moduleName)
    {
        var i = moduleName.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? moduleName.Substring(i + 1) : moduleName;
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MINIDUMP_EXCEPTION_INFORMATION
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers;
    }

    // x64 CONTEXT 的完整布局（权威口径由本机真头文件算出来，不是手算：
    // `tests/output/41-context-layout.cpp` + `41-build-layout-probe.bat`，读数落
    // `tests/output/41-context-layout.txt`：sizeof(CONTEXT)=1232、P1Home@0、ContextFlags@48、
    // EFlags@68、Dr0@72、Dr7@112、Rax@120、Rsp@152、Rbp@160、R15@240、Rip@248、
    // FltSave@256(512B)、VectorRegister@768(26×16B)、VectorControl@1184、DebugControl@1192、
    // LastBranchToRip@1200 … LastExceptionFromRip@1224；定义 `winnt.h:4391` 起）。
    // 为什么必须是**完整**的：`RtlVirtualUnwind` 会把解出来的寄存器**写回**这张表，
    // 并按展开码动到 Rip 之后的 XMM/向量槽位 ⇒ 交进去一个截断结构就是让它往栈上越界写。
    // Rip 之后我不逐个命名（那正是容易抄歪的地方），只用一块不透明的 `Tail[976]` 占满到 1232：
    // 本仪器只读标量寄存器，XMM 谁写谁读都不关我的事，但**边界必须对**。
    // 运行时还会逐次核对"总尺寸==1232 且 Rip@248"，对不上就根本不交给展开器（宁可没有展开段）。
    [StructLayout(LayoutKind.Sequential)]
    private struct CONTEXT
    {
        public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;              // @0…40
        public uint ContextFlags, MxCsr;                                          // @48、52
        public ushort SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;                   // @56…66
        public uint EFlags;                                                       // @68
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;                                // @72…112
        public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi,
                     R8, R9, R10, R11, R12, R13, R14, R15;                        // @120…240
        public ulong Rip;                                                         // @248
        public fixed byte Tail[976];      // FltSave@256 … LastExceptionFromRip@1224，整块占位到 1232
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        public fixed ulong ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_POINTERS
    {
        public EXCEPTION_RECORD* ExceptionRecord;
        public CONTEXT* ContextRecord;
    }

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint CREATE_ALWAYS = 2;
    private const uint OPEN_ALWAYS = 4;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_END = 2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // MiniDumpWriteDump 的选项位
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithProcessThreadData = 0x00000100;

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint firstHandler, VectoredHandler handler);

    [DllImport("kernel32.dll")]
    private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool WriteFile(IntPtr h, byte* buf, uint bytes, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetFilePointer(IntPtr h, int distance, IntPtr highDistance, uint moveMethod);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        private readonly uint _pad1;          // x64 对齐：结构与 Windows 的 48 字节布局逐字节一致
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        private readonly uint _pad2;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint MEM_IMAGE = 0x1000000;

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, MEMORY_BASIC_INFORMATION* buffer, nint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // 崩溃现场取字节唯一安全的方式：读本进程也走这条，坏地址只回 FALSE（不会二次 AV）。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, nint baseAddress,
        nint buffer, nuint size, ref nuint bytesReturned);

    // 映射文件名（psapi 的 K32 版在 **kernelbase.dll**；写 kernel32.psapi 在 .NET 加载器里
    // 会 DllNotFound —— 那条坑 10-03 已实测）。返回不含终止符的字符数，0 = 失败。
    [DllImport("kernelbase.dll", SetLastError = true)]
    private static extern uint K32GetMappedFileNameW(IntPtr hProcess, nint address,
        nint buffer, uint size);

    // x64 展开表的查找入口：命中 ⇒ controlPc 落在某模块有 RUNTIME_FUNCTION 的代码里；
    // 未命中返回 NULL（数据地址、JIT 码、无表模块）。第三个参数允许 NULL（官方签名里的
    // ExtendedContext，只在需要额外上下文时才用）。判"候选返回地址是不是代码"用它，
    // 而不是自己解析 PE 节表。
    [DllImport("ntdll.dll")]
    private static extern nint RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, nint extendedContext);

    // 逐帧展开（ntdll 自己的解译者，`winnt.h:23111` 的签名）：按 FunctionEntry 的展开码把
    // ContextRecord **改写成调用者的上下文**（Rip 变成调用点的返回地址）。
    // 交给它的必须是**完整且 16 字节对齐**的 CONTEXT —— 它会往 Rip 之后的 XMM 槽写回。
    [DllImport("ntdll.dll")]
    private static extern nint RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc,
        nint functionEntry, nint contextRecord, out nint handlerData,
        out ulong establisherFrame, nint contextPointers);

    [DllImport("dbghelp.dll")]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile,
        uint dumpType, MINIDUMP_EXCEPTION_INFORMATION* exceptionParam, IntPtr userStream,
        IntPtr callbackParam);
}
