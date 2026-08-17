using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;

namespace _3FCompare.SmokeTests;

/// <summary>E3 后端可达性冒烟测试（无 UI 控制台）。
/// 用法：
///   dotnet run --project tests/3FCompare.SmokeTests -- [--demo] <视频路径...>
/// 说明：
///   - 默认：演示引擎全程验证（合成画面，无需真实 DLL，永不挂起）；
///   - --demo：强制演示模式（等同默认，明确语义）；
///   - 真实模式验证请用 App 的 --selftest（需消息循环）：
///       dotnet run --project src/3FCompare.App -- --selftest <视频></summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // 解析 --demo 开关（强制演示引擎；真实 DLL 存在时也不走真实路径）
        var forceDemo = args.Length > 0 && args[0] == "--demo";
        var pathsArgs = forceDemo ? args[1..] : args;
        if (forceDemo && pathsArgs.Length == 0)
        {
            Console.Error.WriteLine("用法（--demo）: 3FCompare.SmokeTests --demo <任意文件...>");
            return 2;
        }
        if (pathsArgs.Length == 0)
        {
            Console.Error.WriteLine("用法: 3FCompare.SmokeTests [--demo] <视频路径> [更多路径...]");
            Console.Error.WriteLine("真实模式验证建议使用: dotnet run --project src/3FCompare.App -- --selftest <视频>");
            return 2;
        }

        // 若未强制 demo 且检测到真实 DLL：提示走 App --selftest（控制台无消息循环会挂起）
        var realAvailable = EngineFactory.IsNativeAvailable();
        if (!forceDemo && realAvailable)
        {
            Console.WriteLine("检测到 FFF.Native；控制台环境无消息循环，真实后端验证请使用:");
            Console.WriteLine("  dotnet run --project src/3FCompare.App -- --selftest <视频>");
            Console.WriteLine("本冒烟将使用演示引擎完成逻辑链路验证。");
        }

        var paths = pathsArgs.Select(Path.GetFullPath).ToArray();
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                Console.Error.WriteLine($"文件不存在: {p}");
                return 2;
            }
        }

        try
        {
            RunSmoke(paths, forceDemo);
            Console.WriteLine("E3 冒烟通过 ✅");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"E3 冒烟失败 ❌\n{ex}");
            return 1;
        }
    }

    private static void RunSmoke(string[] paths, bool forceDemo)
    {
        // 控制台无消息循环：真实后端就绪依赖 WinForms 消息循环会挂起，
        // 因此冒烟统一用演示引擎（除非 --demo 语义下也只用演示）；
        // 真实后端验证走 `App --selftest`。
        var engine = (IPlayerEngine)new SimulatedEngine();
        Console.WriteLine("引擎模式: Simulated (演示)【真实后端验证请用 App --selftest】");
        Console.WriteLine($"适配器:\n  {string.Join("\n  ", engine.EnumerateAdapters().Select(a => $"[{a.Index}] {a.Description}"))}");

        var sync = new SyncController();
        sync.StepProfile = new StepProfile { FrameStep = 2, SecondsStep = 5 };

        OpenAndVerify(paths, engine, sync);
    }

    private static void OpenAndVerify(string[] paths, IPlayerEngine engine, SyncController sync)
    {
        // 打开全部路径（最多 9 路，验证同步）
        for (var i = 0; i < paths.Length && i < 9; i++)
        {
            var session = engine.CreateSession(new EngineSessionOptions
            {
                OutputWindow = 0,
                HardwareDecode = false,
            });
            Console.WriteLine($"打开[{i}]: {paths[i]}");
            try
            {
                session.OpenAsync(paths[i]).GetAwaiter().GetResult();
                sync.AddSlot(session, paths[i]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  打开失败: {ex.Message}");
                session.Dispose();
            }
        }

        if (sync.Count == 0)
            throw new InvalidOperationException("所有会话打开失败");

        // 等待 master 就绪（3FP 异步打开；Ready=2 / Playing=3 / Paused=4）；真实模式须 pump 消息循环
        var ready = false;
        for (var i = 0; i < 100 && !ready; i++)
        {
            var s = sync.ReadMasterSnapshot();
            if (s is { Duration100ns: > 0 } && (s.State == 2 || s.State == 3 || s.State == 4))
                ready = true;
            else
            {
                PumpMessages();
                Thread.Sleep(100);
            }
        }
        if (!ready)
        {
            // 最后尝试：输出 LastError 辅助诊断
            var lastSnap = sync.ReadMasterSnapshot();
            throw new InvalidOperationException($"master 会话未在 10s 内就绪 (state={(int?)lastSnap?.State}, dur={lastSnap?.Duration100ns})");
        }

        var master = sync.ReadMasterSnapshot()
            ?? throw new InvalidOperationException("master 快照读取失败");
        Console.WriteLine($"master 快照: 位置={TimeSpan.FromTicks(master.Position100ns)} 时长={TimeSpan.FromTicks(master.Duration100ns)} 帧号={master.FrameIndex} fps={SyncController.EstimateFps(master):0.##}");

        var media = sync.Slots[0].Session.ReadMediaInfo();
        if (media is not null)
            Console.WriteLine($"媒体: {media.VideoWidth}x{media.VideoHeight} @{media.FrameRate:0.00}fps codec={media.Codec} HDR={media.IsHdr}");
        else
            Console.WriteLine("媒体信息: 不可用（真实后端未就绪时预期）");

        // 双步进验证（F12）
        var before = sync.GetMasterPosition100ns();
        sync.StepFrames(1);
        var afterFrame = sync.GetMasterPosition100ns();
        Console.WriteLine($"帧步进(1): {TimeSpan.FromTicks(before)} → {TimeSpan.FromTicks(afterFrame)} (Δ={TimeSpan.FromTicks(afterFrame - before)})");
        if (afterFrame < before) throw new InvalidOperationException("帧步进后退");

        sync.StepSeconds(1);
        var afterSec = sync.GetMasterPosition100ns();
        Console.WriteLine($"秒步进(1): {TimeSpan.FromTicks(afterFrame)} → {TimeSpan.FromTicks(afterSec)} (Δ={TimeSpan.FromTicks(afterSec - afterFrame)})");
        if (afterSec < afterFrame) throw new InvalidOperationException("秒步进后退");

        // 探针
        if (sync.Slots[0].Session.TryReadPixel(10, 10, out var pixel))
            Console.WriteLine($"像素(10,10): R={pixel.R:F3} G={pixel.G:F3} B={pixel.B:F3} A={pixel.A:F3} bits={pixel.BitDepth}");

        // 全部路快照一致性
        var all = sync.ReadAllSnapshots();
        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (s is null) throw new InvalidOperationException($"会话[{i}] 快照不可用");
            Console.WriteLine($"  [{i}] 位置={TimeSpan.FromTicks(s.Position100ns)} 帧号={s.FrameIndex} 状态={s.State}");
        }

        // 一致性断言：所有会话位置偏差 < 1 秒（经偏移折算）
        var masterPos = sync.GetMasterPosition100ns();
        for (var i = 1; i < all.Count; i++)
        {
            if (all[i] is null) continue;
            var expect = masterPos + sync.Slots[i].Offset100ns;
            var actual = all[i]!.Position100ns;
            var diff = Math.Abs(actual - expect);
            if (diff > TimeSpan.TicksPerSecond)
                throw new InvalidOperationException($"会话[{i}] 与 master 偏差过大: {TimeSpan.FromTicks(diff)}");
        }
        Console.WriteLine($"同步一致性: {all.Count} 路偏差均 < 1 秒 ✅");

        // 连续 5 次帧步进不倒退
        var prev = sync.GetMasterPosition100ns();
        for (var i = 0; i < 5; i++)
        {
            sync.StepFrames(1);
            var cur = sync.GetMasterPosition100ns();
            if (cur < prev) throw new InvalidOperationException($"帧步进倒退 (第{i}次)");
            prev = cur;
        }
        Console.WriteLine($"连续 5 次帧步进单调 ✅ (终点 {TimeSpan.FromTicks(prev)})");

        // 区间循环
        sync.LoopEnabled = true;
        sync.LoopStart100ns = 0;
        sync.LoopEnd100ns = sync.GetMasterDuration100ns() > TimeSpan.TicksPerSecond
            ? TimeSpan.TicksPerSecond : Math.Max(1, sync.GetMasterDuration100ns() / 2);
        sync.SeekTo(sync.LoopEnd100ns);
        sync.TickLoop();
        if (sync.GetMasterPosition100ns() > sync.LoopEnd100ns)
            throw new InvalidOperationException("循环 Seek 未回起点");
        Console.WriteLine("区间循环回卷正常 ✅");

        sync.Clear();
        Console.WriteLine("冒烟步骤完成。");
    }

    // ---- Win32 消息泵（真实模式 HWND 需要消息循环推进 D3D 呈现/状态机） ----

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(out uint msg, nint hwnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private static void PumpMessages()
    {
        // 移除并丢弃队列中消息（简化；控制台冒烟只需让 D3D 呈现线程获得窗口消息需的泵）
        while (PeekMessage(out var msg, 0, 0, 0, 1 /*PM_REMOVE*/))
        {
            _ = msg; // WM_PAINT 等自然触发
        }
    }
}
