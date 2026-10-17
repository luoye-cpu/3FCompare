using System;
using System.Collections.Generic;

namespace _3FCompare.Core.Diagnostics;

/// <summary>某个模块在进程地址空间里的占位（基址 + 镜像大小）。</summary>
/// <param name="Name">模块文件名（不含路径，日志里要的就是这个）。</param>
/// <param name="Base">镜像基址。</param>
/// <param name="Size">镜像大小。<b>必须是镜像大小而不是"这段私有内存的大小"</b>：私有内存映射
/// 还含堆、栈与内存映射文件，把那些也算进来会让"跳到堆上执行"这种真正的损坏读数被误报成
/// "<c>ntdll.dll+0x9a3f1</c>"（见 <see cref="FaultSymbolizer"/> 的 outside 分支）。</param>
public readonly record struct ModuleRange(string Name, ulong Base, ulong Size);

/// <summary>把一个绝对地址解析成 <c>模块名+0x偏移</c> —— 崩溃取证的<b>唯一一处</b>换算。
///
/// <para><b>为什么要单独抽成 Core 的纯函数</b>：真正抓栈的那段（vectored handler +
/// <c>MiniDumpWriteDump</c>）在 <c>3FCompare</c> 工程里、没有单测覆盖（该工程无 xunit 容器），
/// 而"边界判错"恰恰是这种换算最可能的坏法：把 <c>Base+Size</c> 当命中 ⇒ 下一个模块第一条指令
/// 被记成上一个模块的尾巴；把私有内存段当模块 ⇒ 跳飞（execute on heap）被记成一个看着合理的
/// <c>模块+偏移</c>，从此再也查不出来。两类都必须能不开窗口、不崩进程就判红。</para>
///
/// <para><b>不做的事</b>：不查导出表、不解析 PDB。偏移留给离线工具
/// （<c>dotnet-dump</c> / dumpbin）去符号化 —— 进程内做符号解析要引入 dbghelp 的状态与
/// 搜索路径，而在崩溃现场加载它们本身就是新的风险源。</para></summary>
public sealed class FaultSymbolizer
{
    private readonly ModuleRange[] _byBase;

    public FaultSymbolizer(IEnumerable<ModuleRange> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = new List<ModuleRange>();
        foreach (var m in modules)
        {
            // Size==0 的条目直接丢：它不可能包住任何地址，留着只会让二分找不到、又不承认找不到。
            if (m.Size == 0 || string.IsNullOrEmpty(m.Name)) continue;
            list.Add(m);
        }
        // 按基址排序；同基址（同一模块被重复枚举）保留先出现的那份，不合并、不猜。
        list.Sort(static (a, b) => a.Base.CompareTo(b.Base));
        _byBase = list.ToArray();
    }

    /// <summary>参与解析的模块数。<b>为 0 时整台仪器是瞎的</b>，调用方须据此报警而不是沉默。</summary>
    public int Count => _byBase.Length;

    /// <summary>命中返回 <c>name+0x偏移</c>（偏移小写十六进制、不补零）；
    /// 落在所有模块之外返回 <c>outside@0x{地址}</c> —— 后者是"指针被踩/跳到非镜像内存"的
    /// 直接读数，不能洗成模块名。</summary>
    public string Resolve(ulong address)
    {
        var lo = 0;
        var hi = _byBase.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var m = _byBase[mid];
            if (address < m.Base) { hi = mid - 1; continue; }
            // 半开区间 [Base, Base+Size)：Base+Size 已经属于下一段，必须算"不在此模块内"。
            if (address >= m.Base + m.Size) { lo = mid + 1; continue; }
            return $"{m.Name}+0x{address - m.Base:x}";
        }
        return $"outside@0x{address:x16}";
    }

    /// <summary>缓存表漏掉某个模块时，用<b>映射文件名 + 该内存区域的分配基址</b>凑出归属
    /// （崩溃现场唯一不需要走加载器就能归名的办法）。
    ///
    /// <para>表是安装时与布局事件上枚举的，而 <c>dxgi.dll</c> 这类模块要等第一次合成起交换链
    /// 之后才加载 ⇒ 现场完全可能"地址确实在某个 dll 的镜像里，但表里没有它"，读出来就是一行
    /// 假的 <c>outside@…</c>（2026-10-01 与 2026-10-04 各实测到一次，后者是崩溃前 2 秒刚重建过表
    /// 仍然缺 dxgi）。所以现场问 <c>GetMappedFileName</c> 拿文件路径，配合 <c>VirtualQuery</c>
    /// 给的 <c>AllocationBase</c>（对 <c>MEM_IMAGE</c> 就是镜像基址）算偏移。</para>
    ///
    /// <para><b>上界不在这里判</b>：本函数没有镜像大小，调用方必须先确认那一段是
    /// <c>MEM_IMAGE</c>（<c>MEM_PRIVATE</c> 的钩子跳板/堆**不许**用这条路归名，否则
    /// "跳到私有可执行内存"这一格会被洗成模块名 —— 那正是本仪器要区分出来的东西）。</para>
    ///
    /// <para>返回 <c>文件名+0x偏移</c>（与 <see cref="Resolve"/> 同版式）；任何前置不满足回 <c>null</c>，
    /// 由调用方保留原样的 <c>outside@…</c> 读数。宁可不归名，也不能归错名。</para></summary>
    /// <param name="mappedPath">映射文件全路径，可来自 <c>GetMappedFileNameW</c>
    /// （形如 <c>\Device\HarddiskVolume3\Windows\SYSTEM32\dxgi.dll</c>）。</param>
    /// <param name="allocationBase">该内存区域的分配基址（<c>MEMORY_BASIC_INFORMATION.AllocationBase</c>）。</param>
    /// <param name="address">要归名的地址。</param>
    public static string? TryFormatImageRange(string? mappedPath, ulong allocationBase, ulong address)
    {
        if (string.IsNullOrWhiteSpace(mappedPath)) return null;
        if (allocationBase == 0 || address < allocationBase) return null;   // 基址不可信 ⇒ 宁可不写
        // 只取文件名，且把 `\Device\…` 这种命名空间路径与正常 Win32 路径一视同仁地按最后一段切。
        var slash = mappedPath!.LastIndexOfAny(new[] { '\\', '/' });
        var name = slash < 0 ? mappedPath : mappedPath[(slash + 1)..];
        if (name.Length == 0) return null;                                  // 结尾是分隔符 ⇒ 没有文件名
        // 大小写与表里那份保持原样：这台仪器的模块名都是小写叶子（FileNameOf），
        // 而映射名可能回 `DXGI.dll` ⇒ 统一一次，免得同一份留痕里出现两个名字被当成两个模块。
        return $"{name.ToLowerInvariant()}+0x{address - allocationBase:x}";
    }
}
