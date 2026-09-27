using System.Globalization;

namespace _3FCompare.Core.Diagnostics;

/// <summary>
/// 进程退出码 →「是否原生崩溃」的判定与分类。
///
/// <para><b>为什么需要独立的一处判定</b>：崩溃退出码的口径在不同宿主里是<b>不一致</b>的，
/// 而"是不是崩溃"这个判断一旦分散到各处，就会出现同一份数据得出相反结论的情况。实测三种口径：
/// ① .NET <c>Process.ExitCode</c> 返回 <see cref="int"/>（<c>GetExitCodeProcess</c> 的 DWORD
/// 被直接重解释）⇒ <c>0xC0000005</c> 表现为 <b>-1073741819</b>；
/// ② Python <c>subprocess.returncode</c> 返回<b>无符号</b> 3221225477；
/// ③ bash 的 <c>$?</c> 把任何异常终止<b>压成 1</b>，与程序自己 <c>return 1</c> 无法区分。
/// 所以判定必须按"位模式"而非"数值大小"来做。</para>
///
/// <para><b>为什么判定区间取 0xC0000000..0xCFFFFFFF</b>：这是 NTSTATUS 里
/// Severity=3（错误）且 Customer=0 的区间，Windows 只有在进程被异常终止时才会把它当退出码。
/// 不用更宽的 <c>&gt;= 0x80000000</c>：那个范围混入了大量非崩溃语义的 HRESULT
/// （例如 <c>0x8000FFFF</c> 只是"意外失败"），正常程序也完全可能主动返回这类值，
/// 误判的代价是"用户点了关闭，程序又自己弹回来了"——这比漏判一次崩溃严重得多。</para>
///
/// <para><b>不覆盖什么</b>：托管异常导致的崩溃在 .NET 上表现为 <c>0xE0434352</c>
/// （CLR 的 <c>EXCEPTION_COMPLUS</c>），它<b>不</b>在本区间内，故不判为崩溃。这是有意的：
/// 托管异常已被 <c>AppDomain.UnhandledException</c> 捕获并落盘，重启只会让用户反复看到同一个
/// 必现错误；而原生 AV/UD 是外部钩子注入导致的偶发竞态，重启确实能恢复。</para>
/// </summary>
public static class CrashExitCode
{
    /// <summary>NTSTATUS 严重错误区间下界（含）。</summary>
    public const uint CrashFloor = 0xC0000000u;

    /// <summary>NTSTATUS 严重错误区间上界（不含）。</summary>
    public const uint CrashCeiling = 0xD0000000u;

    // ── 本项目实测出现过的码（docs/43：RTSS 钩子竞态）────────────────────
    /// <summary>访问违规：跳板内存已被释放，而入口 <c>E9</c> 仍指向它 ⇒ 跳进野地址。</summary>
    public const uint StatusAccessViolation = 0xC0000005u;

    /// <summary>非法指令：线程在中间态窗口里执行到半成品补丁字节 <c>FE BF</c>。</summary>
    public const uint StatusIllegalInstruction = 0xC000001Du;

    /// <summary>特权指令。</summary>
    public const uint StatusPrivilegedInstruction = 0xC0000096u;

    /// <summary>堆损坏。</summary>
    public const uint StatusHeapCorruption = 0xC0000374u;

    /// <summary>栈缓冲区溢出（/GS 检测到）。</summary>
    public const uint StatusStackBufferOverrun = 0xC0000409u;

    /// <summary>栈溢出。</summary>
    public const uint StatusStackOverflow = 0xC00000FDu;

    /// <summary>内存页读入错误（磁盘/映射失败）。</summary>
    public const uint StatusInPageError = 0xC0000006u;

    /// <summary>用户回调（如窗口过程）里抛出未处理异常。</summary>
    public const uint StatusFatalUserCallbackException = 0xC000041Du;

    /// <summary>非崩溃：进程正常退出。</summary>
    public const uint StatusOk = 0u;

    /// <summary>是否为「原生崩溃」退出码。
    /// <para>接受 <see cref="int"/> 是因为 <see cref="System.Diagnostics.Process.ExitCode"/>
    /// 给的就是有符号值；这里先按位重解释回无符号再比较，两种口径都能正确判定。</para></summary>
    public static bool IsCrash(int exitCode) => IsCrash(unchecked((uint)exitCode));

    /// <summary>是否为「原生崩溃」退出码（无符号口径）。</summary>
    public static bool IsCrash(uint exitCode) => exitCode >= CrashFloor && exitCode < CrashCeiling;

    /// <summary>退出码的人类可读描述（保留十六进制原值，便于与 WER 记录对账）。</summary>
    public static string Describe(int exitCode) => Describe(unchecked((uint)exitCode));

    /// <summary>退出码的人类可读描述（无符号口径）。</summary>
    public static string Describe(uint exitCode)
    {
        var hex = "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);
        return exitCode switch
        {
            StatusOk => hex + " 正常退出",
            StatusAccessViolation => hex + " STATUS_ACCESS_VIOLATION 访问违规",
            StatusIllegalInstruction => hex + " STATUS_ILLEGAL_INSTRUCTION 非法指令",
            StatusPrivilegedInstruction => hex + " STATUS_PRIVILEGED_INSTRUCTION 特权指令",
            StatusHeapCorruption => hex + " STATUS_HEAP_CORRUPTION 堆损坏",
            StatusStackBufferOverrun => hex + " STATUS_STACK_BUFFER_OVERRUN 栈缓冲区溢出",
            StatusStackOverflow => hex + " STATUS_STACK_OVERFLOW 栈溢出",
            StatusInPageError => hex + " STATUS_IN_PAGE_ERROR 页面读入错误",
            StatusFatalUserCallbackException => hex + " STATUS_FATAL_USER_CALLBACK_EXCEPTION 用户回调未处理异常",
            _ when IsCrash(exitCode) => hex + " 未分类的原生崩溃",
            _ => hex + " 非崩溃退出码",
        };
    }

    /// <summary>把 <see cref="int"/> 退出码按位重解释为无符号 NTSTATUS（日志/对账用）。</summary>
    public static uint AsUnsigned(int exitCode) => unchecked((uint)exitCode);
}
