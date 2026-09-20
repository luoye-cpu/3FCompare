using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace _3FCompare.Diagnostics;

/// <summary>
/// 已知第三方注入钩子的在场检测（RivaTuner / MSI Afterburner / NVIDIA 覆盖层）。
///
/// <para><b>为什么需要它</b>：<c>docs/33 §八</c> 已把"4 路崩溃"定案到 RTSS / MSI Afterburner 的
/// 注入式钩子上（A-B-A 实测：在场 18/20=90% → 退出 0/20=0% → 重启 11/12=91.7%，Fisher p=0.0001）。
/// 崩溃点落在我们<b>无法控制</b>的 <c>dxgi!CDXGISwapChain::Present</c> 内部，且异常是<b>被投递</b>的
/// （写栈指令却报"读 0xFFFF…FFFF"，记录与指令自相矛盾 —— 这正是注入式钩子的指纹）；
/// 其根源是 Afterburner 的内核驱动 <c>RTCore64.sys</c>（ring-0）与多路 flip-model Present 的交互。
/// <b>用户态改代码修不好它</b>（加锁无效、vtable 实测干净），所以本类只做一件事：
/// <b>让用户知道原因</b>，不再以为是软件坏了。</para>
///
/// <para><b>只读探测，绝不改动任何第三方状态</b>：不弹窗、不阻断启动、不结束任何进程、
/// 不修改任何第三方文件或注册表（解决方案是把 3FCompare.exe 加进 RTSS 的排除列表，
/// 由用户自己操作，见 docs/33 §八）。</para>
///
/// <para><b>AOT 安全</b>：走 <c>GetModuleHandleW</c> 这一条 <c>kernel32</c> 导出，零反射、
/// 零 <c>Process.Modules</c> 依赖（后者在 NativeAOT 下有裁剪标注与异常面）。名字比较用
/// <see cref="string.Equals(string,string,StringComparison)"/>，不做 <c>Type.GetType</c> 之类。</para>
/// </summary>
public static class HookDetector
{
    /// <summary>
    /// 已知会钩住呈现路径的注入模块（小写比较，<c>GetModuleHandle</c> 本身大小写不敏感）。
    /// <list type="bullet">
    /// <item><c>RTSSHooks64.dll</c> / <c>RTSSHooks.dll</c> —— RivaTuner Statistics Server（MSI Afterburner
    /// 的 OSD 也走它），即 <c>docs/33 §八</c> 的<b>已定案触发者</b>。</item>
    /// <item><c>nvspcap64.dll</c> —— NVIDIA ShadowPlay / 覆盖层，<b>次要嫌疑</b>：A-B-A 的 B 组里它仍在场
    /// 却不崩，故单独不构成原因，但仍值得记录（它同属注入式覆盖层家族）。</item>
    /// </list>
    /// </summary>
    private static readonly string[] CandidateModules =
    {
        "RTSSHooks64.dll",
        "RTSSHooks.dll",
        "nvspcap64.dll",
    };

    private static readonly object Gate = new();
    private static string[]? _detected;

    /// <summary>在场钩子的模块名列表（空数组 = 一个都不在）。首次访问时探测一次并缓存。</summary>
    public static IReadOnlyList<string> DetectedHooks
    {
        get
        {
            EnsureProbed();
            return _detected!;
        }
    }

    /// <summary>是否有任一已知注入钩子在场（多路播放崩溃风险的唯一判据）。</summary>
    public static bool IsOverlayHookPresent => DetectedHooks.Count > 0;

    /// <summary>
    /// 探测一次并把结果写进组件日志。在 <c>Program.Main</c> 里紧随
    /// <see cref="ComponentLog.Initialize"/> 调用 —— RTSS 这类注入发生在进程创建期
    /// （早于托管入口），所以启动时探测就能看到；而且这一条会落在崩溃之前，
    /// 事后对时间线时它是"崩溃不是软件自身问题"的第一个证据。
    /// </summary>
    public static void ProbeAndLog()
    {
        if (!ComponentLog.IsEnabled) return;
        var hooks = DetectedHooks;
        if (hooks.Count == 0)
        {
            // 明确记一条"扫过、没有"：否则无法区分"没检测到"与"根本没跑过检测"。
            ComponentLog.Log(Comp.Env, "HookScanClear", -1,
                $"scanned={CandidateModules.Length} hooks=none");
            return;
        }
        foreach (var hook in hooks)
            ComponentLog.Log(Comp.Env, "HookDetected", -1, $"hook={hook}");
    }

    private static void EnsureProbed()
    {
        if (Volatile.Read(ref _detected) is not null) return;
        lock (Gate)
        {
            if (_detected is not null) return;

            var found = new List<string>(CandidateModules.Length);
            foreach (var name in CandidateModules)
            {
                // GetModuleHandleW：模块已在**本进程**中加载则返回句柄，否则 NULL（并置 last error）。
                // 只查询、不加载 —— 绝不能改用 LoadLibrary，那会把钩子"请进来"。
                if (GetModuleHandleW(name) != IntPtr.Zero) found.Add(name);
            }
            Volatile.Write(ref _detected, found.ToArray());
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);
}
