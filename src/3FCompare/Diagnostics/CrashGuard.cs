using System.Diagnostics;
using System.Globalization;
using CoreDiag = _3FCompare.Core.Diagnostics;

namespace _3FCompare.Diagnostics;

/// <summary>
/// 崩溃自愈：父进程守护 + 子进程重启。
///
/// <para><b>为什么必须是进程外方案</b>：要自愈的崩溃是<b>原生</b>访问违规与非法指令
/// （<c>0xC0000005</c> / <c>0xC000001D</c>，根因见 docs/43：RTSS 的 inline hook 在
/// <c>dxgi.dll</c> 函数入口反复装卸，且 5 字节跳转补丁是逐字节写入的）。
/// 这类终止<b>不跑</b> <c>finally</c>、<b>不跑</b> <c>AppDomain.ProcessExit</c>、
/// <c>UnhandledException</c> 也收不到 —— 进程内没有任何一个时刻可以"自救"。
/// 唯一的办法是让另一个进程发现它死了，再把它拉起来。</para>
///
/// <para><b>为什么不合并交换链（N→1）来根治</b>：实验证实崩溃率只与"同时存在的交换链
/// 数量 N"相关，N→1 确实能把崩溃率压到近乎为零。但该改法与"每路独占窗口 / 跨显示器 /
/// 可指定不同显卡"的产品特性直接冲突，属架构取舍，不由本类决定。自愈是在<b>不动架构</b>
/// 的前提下把"丢工作"降级成"闪一下自动恢复"。</para>
///
/// <para><b>触发条件为什么定成"命令行里没有任何 -- 前缀参数"</b>：
/// 项目里所有自动化门禁（<c>--selftest</c> / <c>--multitest</c> / <c>--screentest</c> /
/// <c>--sessiontest</c> / <c>--comparemodetest</c> / <c>--magnifybench</c> / <c>--autodemo</c>）
/// 都带模式位，它们依赖退出码与 stdout 文本判定成败；多一层父进程会让这些判定全部失真。
/// 用这条极简规则，门禁零影响，而用户双击 / 拖文件打开（无 <c>--</c>）仍然受保护。
/// 子进程带 <c>--child</c>，天然不递归。</para>
/// </summary>
internal static class CrashGuard
{
    /// <summary>子进程标记：由守护进程加上，{ ChildArg, 父进程 PID }。</summary>
    public const string ChildArg = "--child";

    /// <summary>恢复标记：告诉子进程"你是崩溃后被拉起来的，去读上次会话"。</summary>
    public const string RestoreArg = "--crash-restore";

    /// <summary>显式关闭守护的参数写法。
    ///
    /// <para>它<b>不需要</b>在 <see cref="ShouldGuard"/> 里单独判断：那个方法一见任何
    /// <c>--</c> 前缀参数就返回 false，本参数自然落在其内。保留这个常量的意义是
    /// <b>自文档</b>——让人在查"怎么关掉自愈"时能在代码里找到答案，而不是靠猜。
    /// 真正"无命令行参数也能关"的逃生口是环境变量 <c>FC_NO_GUARD=1</c>（快捷方式场景用）。</para></summary>
    public const string NoGuardArg = "--no-guard";

    /// <summary>守护自检（端到端，不需要真的制造崩溃）：{ SelfTestArg, 计数文件, [崩溃次数] }。</summary>
    public const string SelfTestArg = "--guard-selftest";

    /// <summary>自动演示/巡检。与 <see cref="Program"/> 的分发共用同一个常量，
    /// 避免两处各写一份字面量（"同一功能两份实现"是本项目已多次踩到的缺陷来源）。</summary>
    public const string AutodemoArg = "--autodemo";

    /// <summary>连续崩溃重启上限。超过则停止重启并隔离快照。</summary>
    public const int MaxConsecutiveRestarts = 3;

    /// <summary>子进程活过这么久才崩溃，就不算"启动即崩"，连续计数归零。
    /// 否则一次偶发崩溃会被永久记账，用久了必然撞上上限。</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    /// <summary>崩溃后重启前的等待。给 WER 写完整转储（本机每个 55~64MB）留出时间，
    /// 也避免"闪一下"快到用户来不及察觉。</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(1200);

    private const int ParentPollMs = 2000;

    /// <summary>是否应以守护模式启动。
    /// 判据见类注释：命令行里<b>没有任何</b> <c>--</c> 前缀参数，且未被环境变量/显式参数关闭。</summary>
    public static bool ShouldGuard(string[] args)
    {
        // 逃生口：排查自愈本身的问题时不必改代码
        if (Environment.GetEnvironmentVariable("FC_NO_GUARD") == "1") return false;

        // 唯一的例外：--autodemo 是**无人值守**的演示/巡检模式，恰恰是最需要自愈的场景
        // （现场没人在，崩了没人重新打开 9 路 4K）。放行它的依据是 2026-09-21 全仓核查：
        // tools/ 与 pack.ps1、发布门禁.ps1 中**没有任何一处**使用 --autodemo，
        // 因此不存在"退出码/stdout 判定被父进程改变"的风险。
        if (args.Length > 0 && args[0] == AutodemoArg) return true;

        foreach (var a in args)
        {
            if (a is null) continue;
            if (a.StartsWith("--", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>守护主循环：反复拉起子进程，直到它"非崩溃退出"或触发重启上限。
    /// </summary>
    /// <param name="originalArgs">本次启动的原始参数（会原样透传给子进程）。</param>
    /// <param name="restoreOnRestart">崩溃重启时是否追加 <see cref="RestoreArg"/>。</param>
    /// <returns>最后一个子进程的退出码（守护进程自身的退出码即取此值）。</returns>
    public static int RunGuard(string[] originalArgs, bool restoreOnRestart = true, bool isSelfTest = false)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 1;

        var ppid = Environment.ProcessId;
        var consecutiveCrashes = 0;
        var lastCode = 0;
        var generation = 0;

        CoreDiag.AppLog.Info("Guard",
            $"守护启动 pid={ppid} 上限={MaxConsecutiveRestarts} 参数=[{string.Join(' ', originalArgs)}]");

        while (true)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                // 不重定向标准流：子进程沿用守护进程自己的控制台句柄，
                // 否则依赖 stdout 文本判定的脚本会读不到任何输出。
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            psi.ArgumentList.Add(ChildArg);
            psi.ArgumentList.Add(ppid.ToString(CultureInfo.InvariantCulture));
            foreach (var a in originalArgs) psi.ArgumentList.Add(a);
            if (restoreOnRestart && consecutiveCrashes > 0) psi.ArgumentList.Add(RestoreArg);

            Process? child;
            try
            {
                child = Process.Start(psi);
            }
            catch (Exception ex)
            {
                CoreDiag.AppLog.Error("Guard", $"拉起子进程失败：{ex.Message}");
                return lastCode == 0 ? 2 : lastCode;
            }
            if (child is null)
            {
                CoreDiag.AppLog.Error("Guard", "拉起子进程返回 null");
                return lastCode == 0 ? 2 : lastCode;
            }

            generation++;
            var sw = Stopwatch.StartNew();
            CoreDiag.AppLog.Info("Guard", $"第 {generation} 代子进程 pid={child.Id}");
            child.WaitForExit();
            sw.Stop();

            lastCode = child.ExitCode;
            var u = CoreDiag.CrashExitCode.AsUnsigned(lastCode);
            child.Dispose();

            if (!CoreDiag.CrashExitCode.IsCrash(lastCode))
            {
                // 干净退出：把"上次会话"清掉，否则下次启动会莫名其妙恢复一份旧状态
                // ⚠ 自检必须跳过这一步：RunSelfTest 复用本方法跑守护循环，它的子进程
                // "正常退出"只是自检成功，却被当成用户会话正常结束 ⇒ 跑一次守护自检
                // 就会删掉真实用户的自动保存快照（docs/45 P2-c）。
                if (!isSelfTest)
                    _3FCompare.Core.Settings.SessionAutosave.Clear();
                else
                    CoreDiag.AppLog.Info("Guard", "自检子进程干净退出（按自检语义，不清会话快照）");
                CoreDiag.AppLog.Info("Guard",
                    $"子进程正常退出 code={CoreDiag.CrashExitCode.Describe(lastCode)}，守护结束");
                return lastCode;
            }

            consecutiveCrashes++;
            CoreDiag.AppLog.Error("Guard",
                $"子进程崩溃 第 {consecutiveCrashes} 次 存活 {sw.Elapsed.TotalSeconds:F1}s " +
                $"{CoreDiag.CrashExitCode.Describe(u)} (int={lastCode})");

            // 活够了才崩 ⇒ 不是"一打开就崩"，之前的账不算数
            if (sw.Elapsed >= StableAfter && consecutiveCrashes > 1)
            {
                CoreDiag.AppLog.Info("Guard", $"子进程存活超过 {StableAfter.TotalSeconds}s，连续崩溃计数归零");
                consecutiveCrashes = 1;
            }

            if (consecutiveCrashes > MaxConsecutiveRestarts)
            {
                // 反复崩溃：多半是"打开这组文件就必崩"。留着快照会自激循环
                // （重启 → 恢复 → 再崩），所以改名隔离而不是删除，用户还能取回内容。
                var quarantined = _3FCompare.Core.Settings.SessionAutosave.Quarantine();
                CoreDiag.AppLog.Error("Guard",
                    $"连续崩溃 {consecutiveCrashes} 次超过上限 {MaxConsecutiveRestarts}，停止重启" +
                    (quarantined ? "，已隔离上次会话快照" : "（无快照可隔离）"));
                return lastCode;
            }

            Thread.Sleep(RestartDelay);
        }
    }

    /// <summary>自检子进程体：前 <paramref name="crashTimes"/> 次以访问违规退出，之后正常退出。
    /// 用来端到端验证守护的重启与上限逻辑，无需真的制造原生崩溃。</summary>
    public static int RunSelfTestChild(string counterPath, int crashTimes)
    {
        var n = 0;
        try
        {
            if (File.Exists(counterPath))
                int.TryParse(File.ReadAllText(counterPath).Trim(), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out n);
        }
        catch { }
        n++;
        try { File.WriteAllText(counterPath, n.ToString(CultureInfo.InvariantCulture)); } catch { }

        if (n <= crashTimes)
        {
            Console.Error.WriteLine($"[guard-selftest] 第 {n} 次启动：模拟崩溃");
            Console.Error.Flush();
            return unchecked((int)CoreDiag.CrashExitCode.StatusAccessViolation);
        }
        Console.Error.WriteLine($"[guard-selftest] 第 {n} 次启动：正常退出");
        Console.Error.Flush();
        return 0;
    }

    /// <summary>自检入口（父侧）：走真实的守护循环，子进程跑 <see cref="RunSelfTestChild"/>。
    /// </summary>
    /// <param name="counterPath">跨代计数文件。</param>
    /// <param name="crashTimes">前几次模拟崩溃；大于 <see cref="MaxConsecutiveRestarts"/> 可验证上限。</param>
    public static int RunSelfTest(string counterPath, int crashTimes)
    {
        try { File.Delete(counterPath); } catch { }
        return RunGuard(new[] { SelfTestArg, counterPath, crashTimes.ToString(CultureInfo.InvariantCulture) },
                        restoreOnRestart: false, isSelfTest: true);
    }

    /// <summary>强制终止当前进程（kernel32!TerminateProcess）。
    /// 不用 ExitProcess / <c>Environment.Exit</c>：后者会依次执行所有 DLL 的
    /// DLL_PROCESS_DETACH 并走 CLR 托管停机，此刻内核原生线程仍活着且可能反向
    /// P/Invoke 托管代码，实测在 FFF.Native / D3D11 上会死锁挂住
    /// （见 MainWindow.SelfTest.cs:48、:57、:728 三处实测结论）。
    /// TerminateProcess 不跑 detach、不走托管停机。</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint hProcess, int exitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    /// <summary>子进程侧的"父进程存活"看门狗：父没了就自己退出。
    ///
    /// <para><b>为什么需要</b>：守护进程被强杀（任务管理器 / <c>Stop-Process</c>）时，
    /// 子进程会成为孤儿——窗口残留、GPU 资源不释放，且没人再管它。
    /// 用 Job Object 也能做到，但那需要 P/Invoke 且要处理嵌套 Job 的兼容问题；
    /// 轮询父 PID 是纯托管的，代价是每 2 秒一次 <c>GetProcessById</c>，可忽略。</para></summary>
    public static void StartParentWatcher(int parentPid)
    {
        // 静默返回等于"孤儿防护悄悄失效"，排查时无从下手 ⇒ 必须落日志说明是哪种不启用
        if (parentPid <= 0)
        {
            CoreDiag.AppLog.Warn("Guard",
                $"父 PID 非法（{parentPid}），未启用父进程看门狗：守护被强杀后本进程会残留为孤儿");
            return;
        }
        if (parentPid == Environment.ProcessId)
        {
            CoreDiag.AppLog.Warn("Guard", "父 PID 与自身相同，未启用父进程看门狗（疑似参数串味）");
            return;
        }
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(ParentPollMs);
                try
                {
                    using var p = Process.GetProcessById(parentPid);
                    if (!p.HasExited) continue;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // 拿不到 = 父进程已经不在了
                }
                catch
                {
                    continue; // 其他异常（权限等）不据此自杀
                }
                CoreDiag.AppLog.Warn("Guard", $"父进程 {parentPid} 已消失，子进程自行退出");
                try { CoreDiag.AppLog.Shutdown(); } catch { }
                // 这里**绝不能**用 Environment.Exit：它要走 CLR 托管停机与 DLL detach，
                // 此刻内核原生线程仍活着并可能反向 P/Invoke 托管代码，实测在 FFF.Native /
                // D3D11 上会死锁挂住（见 MainWindow.SelfTest.cs:48、:57、:728 的实测结论）——
                // 那样"孤儿防护"自己就变成了孤儿制造机。TerminateProcess 不跑托管停机；
                // 日志已在上一行显式冲刷（AppLog.Shutdown 会排空队列并 Flush/Dispose writer）。
                TerminateProcess(GetCurrentProcess(), 0);
            }
        })
        { IsBackground = true, Name = "guard-parent-watcher" };
        t.Start();
    }
}
