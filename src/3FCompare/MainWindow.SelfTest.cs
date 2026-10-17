using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using _3FCompare.Localization;
using _3FCompare.Controls;
using _3FCompare.Panels;
using _3FCompare.Services;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;
using _3FCompare.Platform;

namespace _3FCompare;

/// <summary>主窗口（M2：核心播放面已接线）。
/// 打开/播放/步进/循环/缩放平移/网格布局/时间轴/状态栏全量；面板与对话框 M3 实装。</summary>
public partial class MainWindow : Window
{
    /// <summary>自测/压力测试模式（由命令行 --selftest/--screentest/--multitest 置位）。
    /// 置位时跳过窗口几何持久化，避免测试窗口的位置/尺寸覆盖用户配置。</summary>
    private bool _selfTestMode;

    /// <summary>自测"跳过"登记表（P0-5）：跳过必须在收尾汇总里显式可见，不能静默当通过。
    /// 只有"该环境本就无法判别"的项才允许登记；条件成立与否本身就说明出问题的，一律判红。</summary>
    private static readonly System.Collections.Generic.List<string> _selfTestSkips = new();

    /// <summary>登记一次跳过并立即打印原因（收尾由 <see cref="FlushSelfTestSkips"/> 再汇总一次）。</summary>
    private static void RecordSelfTestSkip(string reason)
    {
        _selfTestSkips.Add($"[{_step}] {reason}");
        Log($"⚠ SKIP（已登记，不计入通过）：{reason}");
    }

    /// <summary>收尾打印跳过汇总：有跳过时明确声明"未验证 ≠ 通过"。
    /// 真实模式下本清单应为空——探针映射的两处旧跳过已按"条件不成立即真出问题"改为判红。</summary>
    private static void FlushSelfTestSkips()
    {
        if (_selfTestSkips.Count == 0) return;
        Log($"⚠ 本次自测有 {_selfTestSkips.Count} 项跳过（未验证，不等于通过）：");
        foreach (var s in _selfTestSkips) Log($"   • {s}");
    }

    /// <summary>multitest 判别实验：真正并发 Present 的路数（默认 <see cref="int.MaxValue"/> = 全部）。
    ///
    /// <para><b>为什么需要它</b>：P1「4 路崩溃」现有数据里，<b>设备/交换链数量</b>与
    /// <b>并发 Present 的线程数</b>都随路数一起增长，两者被混在一起，无法归因。
    /// 由环境变量 <c>FC_MULTITEST_ACTIVE</c> 指定：仍然打开全部 N 路（设备 + 交换链数量不变），
    /// 但只让前 ACTIVE 路播放，其余路 <c>Pause</c>（不再 Present）。
    /// 这样 A/B 两臂的唯一差异就只剩「并发 Present 的线程数」。</para>
    ///
    /// <para>取值 &lt; 打开路数时，被暂停的路会因不再推进而必然触发漂移/停滞断言，
    /// 故 <see cref="CheckDrift"/> 与 <see cref="CheckPresentedGrowthAsync"/> 跳过 index ≥ 本值的路。</para></summary>
    private int _multitestActiveRoutes = int.MaxValue;

    /// <summary>强制终止当前进程（kernel32!TerminateProcess）。
    /// 不用 ExitProcess：后者会依次执行所有 DLL 的 DLL_PROCESS_DETACH，
    /// 实测在 FFF.Native / D3D11 上会死锁挂住。TerminateProcess 不跑 detach，且能指定退出码。</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint hProcess, int exitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    /// <summary>
    /// 自测/压力测试统一退出口：复刻 OnClosing 的清理链后再退出。
    /// 直接 Environment.Exit 会绕过 OnClosing，播放器会话不 Destroy；且 CLR 的托管停机
    /// 期间仍有原生线程反向回调托管委托，触发 ceemain.cpp:1750 断言并以 127 退出
    /// （测试明明全部通过却报崩溃）。这里改用 TerminateProcess：日志/控制台已显式冲刷，
    /// 无需走托管停机，退出码也能如实传递。
    /// </summary>
    // docs/41 复核：ExitSelfTest 必须不可重入。看门狗经 Dispatcher.Post 投递的调用，
    // 与自测方法 finally 里的调用会并发/嵌套——本方法内部还要 RunJobs()，
    // 会把排队中的自测续体跑起来 ⇒ 内层再进一次本方法并抢先 TerminateProcess，
    // 覆盖掉外层（往往是看门狗的 3 或真实的失败码）。
    private int _selfTestExiting;

    private void ExitSelfTest(int code)
    {
        if (System.Threading.Interlocked.Exchange(ref _selfTestExiting, 1) != 0)
        {
            Console.Error.WriteLine($"[ExitSelfTest] 已在退出流程中，忽略重复调用 code={code}");
            Console.Error.Flush();
            return;
        }
        // docs/41 #17（本次审查最严重的假绿入口）：内核/FFmpeg 不可用时自测跑在【演示模式】，
        // 而大量断言包在 if (_realMode) 内被**整段跳过** —— 原本依然 exit=0，
        // 门禁拿到的"全绿"其实是"什么都没测"。这里一处收口：演示模式下的"通过"降级为失败码 2。
        // 确需在演示模式下调试（例如纯 UI 结构断言）时，设环境变量 FC_SELFTEST_ALLOW_DEMO=1 豁免。
        if (code == 0 && !_realMode &&
            System.Environment.GetEnvironmentVariable("FC_SELFTEST_ALLOW_DEMO") != "1")
        {
            Console.Error.WriteLine(
                "❌ 自测完成于【演示模式】：内核/FFmpeg 不可用，if (_realMode) 包裹的断言已被跳过，" +
                "exit=0 不代表通过。请先跑 tools/deploy_ffmpeg_for_tests.sh 并检查内核；" +
                "或设 FC_SELFTEST_ALLOW_DEMO=1 显式豁免。");
            Console.Error.Flush();
            code = 2;
        }
        // ① 先销毁全部播放器会话（与 OnClosing 同一条清理链）
        DestroyAllSessions();
        // ② 再卸载内核日志 sink（同样是托管委托）
        try { _3FCompare.Core.Diagnostics.KernelLogBridge.Uninstall(); } catch { }
        Console.Error.WriteLine($"[ExitSelfTest] 清理完毕 slots={_sync.Slots.Count} {DateTime.Now:HH:mm:ss.fff}");
        // ③ 关闭窗口并让 Avalonia 真正销毁 HWND。
        //    停机时若 HWND 仍在，系统投递的窗口消息会经 Avalonia 的托管 WndProc
        //    反向进入已销毁的 CLR —— 这正是 ceemain.cpp:1750 断言的触发点。
        try
        {
            Close();
            for (var i = 0; i < 20; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(50);
            }
        }
        catch { }
        // ④ 静默期：等内核工作线程收尾，避免飞行中的回调撞上 CLR 停机
        try { System.Threading.Thread.Sleep(600); } catch { }
        Console.Error.WriteLine($"[ExitSelfTest] 准备退出 code={code} {DateTime.Now:HH:mm:ss.fff}");
        // ⑤ 冲刷日志与控制台（TerminateProcess 不走托管停机，必须在这里显式冲刷）
        try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
        Console.Out.Flush();
        Console.Error.Flush();
        // ⑥ 强制终止进程：跳过 CLR 托管停机与 DLL detach，
        //    原生线程没有机会回调已销毁的运行时，退出码也能如实传递
        TerminateProcess(GetCurrentProcess(), code);
        System.Threading.Thread.Sleep(3000); // 兜底：理论上不会走到
        Environment.Exit(code);
    }

    private async System.Threading.Tasks.Task RunScreentestAsync(string input, string outputPng)
    {
        StartDeadlineWatchdog("screentest", 120);   // docs/41 #16：原先无任何超时兜底
        var code = 1;
        try
        {
            _step = "screentest 打开";
            Grid.SetCount(1, _realMode);
            Console.WriteLine($"screentest: 打开 {input} ({(_realMode ? "真实" : "演示")})");
            _coordinator.OpenFiles(new[] { input }, autoPlay: true);

            _step = "screentest 等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            _step = "screentest 渲染等待";
            await System.Threading.Tasks.Task.Delay(500);

            _step = "screentest 抓帧";
            var surface = Grid.GetSurface(0);
            // P0-2：与导出路径保持一致——内核原生回读优先，抓屏只作兜底。
            // 这样 screentest 同时也验证了"原生回读可用"这条主线。
            System.Drawing.Bitmap? bmp = CaptureNativeFrame(_sync.Slots.FirstOrDefault()?.Session);
            if (bmp is null && surface is not null && surface.Hwnd != 0)
                bmp = _3FCompare.App.Capture.ScreenFrameCapture.CaptureWindowFrame(surface.Hwnd);

            if (bmp is not null)
            {
                // 走与导出完全相同的写出路径（补 sRGB 色彩标记），否则测的和发的是两套代码
                using (bmp)
                {
                    WritePngWithSrgbChunk(bmp, outputPng);
                    var media = _sync.Slots.FirstOrDefault()?.Session.ReadMediaInfo();
                    Console.WriteLine($"screentest: 导出 {bmp.Width}×{bmp.Height}" +
                        (media is not null ? $"（源 {media.VideoWidth}×{media.VideoHeight}）" : string.Empty));
                    // P0-2 回归：旧实现固定降采样到 320px 宽，这里卡住"明显偏小"的回归。
                    // 注意内核 FFF3FP_ReadVideoPixelRegion 语义是"回读已呈现帧"，
                    // 故导出分辨率 = 呈现分辨率（随窗口/显示器变化），不等于源分辨率。
                    if (bmp.Width < 640)
                    {
                        Console.Error.WriteLine($"screentest: ✗ 导出宽度异常小 {bmp.Width}（疑似退回降采样路径）");
                        code = 1;
                    }
                    else
                    {
                        var size = new FileInfo(outputPng).Length;
                        Console.WriteLine($"screentest: PNG {size} bytes");
                        code = size > 1000 ? 0 : 1;
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("screentest: 抓帧失败");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screentest: 失败 {ex.Message}");
        }
        finally
        {
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    /// <summary>当前已就绪的路数（Ready/Paused/Playing 均算就绪）。</summary>
    private int CountReady()
        => _sync.ReadAllSnapshots().Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));

    /// <summary>等待全部路就绪。<b>不要</b>用"路数够了"代替——Slot 在打开请求时就入列，
    /// 此时会话还没 Ready，任何 Seek/播放都是空操作。</summary>
    private async System.Threading.Tasks.Task<bool> WaitAllRoutesReadyAsync(int routes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountReady() >= routes) return true;
            await System.Threading.Tasks.Task.Delay(150);
        }
        return CountReady() >= routes;
    }

    /// <summary>--sessiontest &lt;video&gt; [video2] [video3]：会话存取往返回归。
    /// <para>专门盯死两个已修缺陷，它们都不会让程序崩溃，只会让功能静默失效：</para>
    /// <list type="bullet">
    /// <item>P0-1：加载会话时网格没先重建到会话路数 → OpenFilesCore 取不到播放面板 →
    /// onAllOpened 永不执行（不 Seek、不恢复循环、不播放，画面一片空白）。</item>
    /// <item>P0-3：autoPlay 配额在异常路径不归还 → 同样导致 onAllOpened 永不触发。</item>
    /// </list>
    /// 断言：重载后路数一致、位置回到保存点（±500ms）、且处于播放态。</summary>
    private async System.Threading.Tasks.Task RunSessiontestAsync(string[] videoPaths)
    {
        // docs/41 #16；复核修正：原 300s 与 regress-baseline 脚本的 `timeout 300` 相等，
        // 外部 kill 会抢先看门狗 ⇒ 兜底失效且诊断丢失。统一取 240s（< 外部 300s）。
        StartDeadlineWatchdog("sessiontest", 240);
        var code = 1;
        try
        {
            foreach (var v in videoPaths)
            {
                if (!File.Exists(v)) { Console.Error.WriteLine($"sessiontest: 文件不存在 {v}"); ExitSelfTest(2); }
            }

            _step = "会话往返-打开";
            Grid.SetCount(videoPaths.Length, _realMode);
            Log($"打开 {videoPaths.Length} 路");
            _coordinator.OpenFiles(videoPaths, autoPlay: false);

            // 必须等"全部就绪"而不只是"路数够了"：211MB 的 4K 素材打开要数秒，
            // 未就绪时 SeekTo 是空操作，后面测出来的"位置没恢复"其实是测试自己的问题。
            _step = "会话往返-等就绪";
            if (!await WaitAllRoutesReadyAsync(videoPaths.Length, TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine($"sessiontest: ✗ 打开未在 30s 内就绪（就绪 {CountReady()}/{videoPaths.Length}）");
                ExitSelfTest(1);
            }
            Log($"{videoPaths.Length} 路就绪 ✓");
            var failures = new System.Collections.Generic.List<string>();

            // docs/15 §2.1：帧步进必须让**所有路**进入暂停态。
            // 单路测不出这条——master 走 StepFrame 时内核自己会 SetState(Paused)，
            // 而从路走 Seek、Seek 不改变播放状态，缺陷只在多路下显现。
            // 所以这条断言放在 sessiontest（天然多路），selftest 的单路断言只作兜底。
            if (_realMode && videoPaths.Length >= 2)
            {
                _step = "多路帧步进后全路暂停";
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);
                var playingBefore = _sync.ReadAllSnapshots().Count(s => s?.State == PlayerState.Playing);
                if (playingBefore < 2)
                {
                    failures.Add($"步进前置不足：{videoPaths.Length} 路中只有 {playingBefore} 路在播放，" +
                                 "无法验证 docs/15 §2.1（此场景必须在播放态下步进）");
                }
                else
                {
                    // ⚠ 这里**刻意**在 UI 线程直调 StepFrames（不包 Task.Run），别当成缺陷"顺手修"：
                    // SyncController.StepFrames 的契约确是"不要在 UI 线程直接调用"（内含 50ms 复测等待
                    // 与最多 9 路原生 Seek，数十~上百 ms）。自测/门禁路径是**唯一例外**——
                    // ① 没有用户在看这个界面，没有交互会被冻结（冻结的代价＝0）；
                    // ② 下面的断言依赖"调用返回后立刻读快照"的同步顺序，改异步会引入时序不确定性，
                    //    把一条确定性回归变成 flaky（假红比不测更糟）。
                    // 生产路径（键盘 / 传输栏）一律走 StepFramesAsync，见 MainWindow.Playback.cs。
                    _sync.StepFrames(_sync.StepProfile.FrameStep);
                    await System.Threading.Tasks.Task.Delay(400);
                    var stateDesc = string.Join(", ",
                        _sync.ReadAllSnapshots().Select((s, i) => $"路{i}={s?.State}"));
                    Log($"多路帧步进后状态: {stateDesc}");
                    var stillPlaying = _sync.ReadAllSnapshots()
                        .Select((s, i) => (Index: i, State: s?.State))
                        .Where(x => x.State == PlayerState.Playing)
                        .Select(x => x.Index)
                        .ToArray();
                    if (stillPlaying.Length > 0)
                    {
                        failures.Add($"帧步进后仍有路在播放: {stateDesc}" +
                                     "（docs/15 §2.1：master 走 StepFrame 会被内核置 Paused，" +
                                     "从路走 Seek 却继续播 ⇒ 画面立刻错帧）");
                    }
                }
                _sync.Pause(); // 后续步骤按暂停态继续
                await System.Threading.Tasks.Task.Delay(200);
            }

            // C6 布局还原回归的基准：保存前把视图摆成"非单屏 + 3x3 预设"，
            // 保存后（清空阶段）再翻转成"单屏 + 3x3"。旧实现"快照只存不还原"会停在后者的状态——
            // 这类失效不崩溃、不报错，只能靠断言抓（C6 正是因此潜伏至今）。
            Grid.SingleView = false;
            Grid.SetGridLayout("3x3");
            // 期望必须来自"用户实际选择的预设"，而不是按路数推导——
            // 旧代码用 CodeFor(false, count)，期望值与保存值同源，属自证，
            // 于是"用户选 3x3 却被存成 2x2"这条回归永远测不出（docs/14 §1.1）。
            var expectLayoutCode = _3FCompare.Core.Display.GridLayout
                .CodeFromPreset(Grid.Preset, Grid.SingleView);

            // 挪到一个非零点，才能验证"位置被恢复"而不是恰好都在 0
            _step = "会话往返-定位";
            _sync.SeekTo(TimeSpan.TicksPerSecond); // 1s
            await System.Threading.Tasks.Task.Delay(600);
            var before = _sync.GetMasterPosition100ns();
            Log($"保存前位置 {before / 10_000}ms");
            if (before < TimeSpan.TicksPerMillisecond * 500)
            {
                Console.Error.WriteLine($"sessiontest: ✗ Seek 未生效（位置 {before / 10_000}ms），无法验证位置恢复");
                ExitSelfTest(1);
            }

            _step = "会话往返-保存";
            var snapshot = BuildSessionSnapshot();
            if (snapshot.GridLayout != expectLayoutCode)
            {
                Console.Error.WriteLine($"sessiontest: ✗ 快照布局代码不符 实际={snapshot.GridLayout} 期望={expectLayoutCode}");
                ExitSelfTest(1);
            }
            var json = snapshot.ToJson();
            var roundTrip = SessionSnapshot.FromJson(json);
            if (roundTrip is null || roundTrip.Items.Count != videoPaths.Length)
            {
                Console.Error.WriteLine($"sessiontest: ✗ 快照 JSON 往返路数不符 实际={roundTrip?.Items.Count ?? -1}");
                ExitSelfTest(1);
            }
            if (roundTrip!.GridLayout != expectLayoutCode)
            {
                Console.Error.WriteLine($"sessiontest: ✗ 快照 JSON 往返布局代码不符 实际={roundTrip.GridLayout} 期望={expectLayoutCode}");
                ExitSelfTest(1);
            }

            _step = "会话往返-清空";
            foreach (var s in Grid.Surfaces) s.DetachSession();
            _sync.Clear();
            Grid.SetCount(0, _realMode);
            // C6：把布局摆成与快照不同（单屏 + 3x3），还原逻辑必须把它覆盖回快照值
            Grid.SingleView = true;
            Grid.SetGridLayout("3x3");
            await System.Threading.Tasks.Task.Delay(200);

            _step = "会话往返-重载";
            LoadSessionSnapshot(roundTrip!);

            if (!await WaitAllRoutesReadyAsync(videoPaths.Length, TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine($"sessiontest: ✗ 重载未在 30s 内就绪（就绪 {CountReady()}/{videoPaths.Length}）——P0-1 回归");
                ExitSelfTest(1);
            }

            // C3：换路后放大镜采样缓冲必须恢复。
            // 缺陷形态是"一次读回失败 → 缓冲置 null → 之后永远走同一判据提前 return，只剩空框"。
            _step = "会话往返-放大镜缓冲";
            var magnifierOk = true;
            if (_sync.Slots.Count > 0)
            {
                Magnifier.SimulatePixelReadFailureForSelfTest();
                Magnifier.AttachSession(_sync.Slots[0].Session);
                magnifierOk = Magnifier.HasPixelGrid;
                Log($"放大镜采样缓冲: {(magnifierOk ? "已恢复 ✓" : "仍为 null ✗")}");
            }

            // autoPlay=true：全部打开后应自动进入播放。给 10s 观察窗（首帧起播有延迟）。
            _step = "会话往返-等播放";
            var playDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < playDeadline && _sync.ReadMasterSnapshot()?.State != PlayerState.Playing)
                await System.Threading.Tasks.Task.Delay(100);

            _step = "会话往返-断言";
            var after = _sync.Count;
            var posAfter = _sync.GetMasterPosition100ns();
            var snap = _sync.ReadMasterSnapshot();
            var expectSingle = _3FCompare.Core.Display.GridLayout.IsSingleView(expectLayoutCode);
            var expectPreset = _3FCompare.Core.Display.GridLayout.PresetOf(expectLayoutCode);
            Log($"重载后 路数={after} 位置={posAfter / 10_000}ms 状态={snap?.State} " +
                $"布局 SingleView={Grid.SingleView} 预设={Grid.Preset}");

            // 收集式断言：一次跑完把所有回归项都判掉，避免"修好一项才发现下一项坏了"。
            if (after != videoPaths.Length)
            {
                failures.Add($"路数不符 实际={after} 期望={videoPaths.Length}（P0-1 回归）");
            }
            else
            {
                // 播放会自动推进，故只卡下界：不允许回到 0（=onAllOpened 没执行 → 没 Seek）。
                if (posAfter < before - TimeSpan.TicksPerMillisecond * 500)
                    failures.Add($"位置未恢复到保存点 保存={before / 10_000}ms 实际={posAfter / 10_000}ms（P0-1/P0-3 回归）");
                if (snap is null || snap.State != PlayerState.Playing)
                    failures.Add($"重载后未自动播放 实际={snap?.State}（P0-3 回归）");
            }
            // C6：网格布局必须随快照还原（SingleView 与预设两个维度都要对上）
            if (Grid.SingleView != expectSingle || Grid.Preset != expectPreset)
                failures.Add($"布局未随会话还原 实际 SingleView={Grid.SingleView} 预设={Grid.Preset}，" +
                             $"期望 SingleView={expectSingle} 预设={expectPreset}（C6 回归）");
            // C3：换路后放大镜采样缓冲必须恢复
            if (!magnifierOk)
                failures.Add("换路后放大镜采样缓冲未恢复（C3 回归）");

            if (failures.Count > 0)
            {
                foreach (var f in failures) Console.Error.WriteLine($"sessiontest: ✗ {f}");
                code = 1;
            }
            else
            {
                Console.WriteLine($"sessiontest: ✓ 路数={after} 位置={posAfter / 10_000}ms 状态={snap?.State} 布局={Grid.Preset}");
                code = 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sessiontest: 异常 {ex.Message}");
            code = 2;
        }
        finally
        {
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    // ══════════ 打开 / 拖放 ══════════


    /// <summary>布局回归断言（docs/10 §5）：底部栏自上而下必须是
    /// 时间轴 → 传输栏 → 状态栏（主流播放器把 seek 条紧贴画面、按钮置于其下），
    /// 且侧栏宽度/折叠态与设置一致。DockPanel 的 Dock=Bottom 是反向堆叠，
    /// 单纯改 XAML 声明顺序很容易改反，故固化成自测断言。
    /// <para><b>前置条件</b>：底部三栏处于展开态。docs/31 阶段 4 起时间轴/状态栏可被用户折叠
    /// 并持久化，若带着用户的折叠态来断言，折叠栏的 Bounds 为 0，顺序断言会给出误导性失败；
    /// 故调用方（<see cref="RunSelftestAsync"/>）在断言前显式复位到展开并等过一次布局，
    /// 使断言结果只由布局代码决定、与用户配置无关。</para></summary>
    private void AssertBottomBarOrder()
    {
        var timeline = TimelineHost.Bounds.Top;
        var transport = TransportHost.Bounds.Top;
        var status = StatusBarHost.Bounds.Top;
        if (!(timeline < transport && transport < status))
            throw new InvalidOperationException(
                $"底部栏顺序错误：Timeline={timeline:0.#} Transport={transport:0.#} StatusBar={status:0.#}（期望 Timeline < Transport < StatusBar）");
        if (!TimelineHost.IsVisible || !TransportHost.IsVisible || !StatusBarHost.IsVisible)
            throw new InvalidOperationException("底部栏存在不可见项");

        // 三态各有自己的期望宽度：Hidden 是 0，不能再按"折叠/未折叠"两分法取 RailWidth
        //（否则用户把侧栏持久化成"完全隐藏"后，这条断言会拿 48 去比 0 而误报失败）。
        var col = MainArea.ColumnDefinitions[0].Width;
        var expect = _sidebar.Mode switch
        {
            SidebarMode.Expanded => _sidebar.ExpandedWidth,
            SidebarMode.Rail => Controls.ToolsSidebar.RailWidth,
            _ => 0,
        };
        if (!col.IsAbsolute || Math.Abs(col.Value - expect) > 0.5)
            throw new InvalidOperationException($"侧栏宽度异常：实际={col.Value:0.#} 期望={expect:0.#}（{_sidebar.Mode}）");

        Log($"布局 ✓ 时间轴({timeline:0.#}) → 传输栏({transport:0.#}) → 状态栏({status:0.#})，侧栏 {col.Value:0.#}px（{_sidebar.Mode}）");
    }

    /// <summary>「加路 / 减路」两个按钮的契约。走的是<b>生产处理器本身</b>
    /// （<c>AddSlotPlaceholder</c> / <c>RemoveLastSlot</c>，即传输栏左下角那两个键），不是测试里重算的一份。
    ///
    /// <para><b>为什么必须常驻门禁</b>：发布门禁 11 项里没有任何一步按过这两个键，
    /// <c>--comparemodetest</c> 也是用 <c>Grid.SetCount(routes)</c> 直接起盘的 ——
    /// 于是"加路的锚点取的是会话数而不是网格路数"这一类缺陷可以长期存在而不被判红。
    /// 本方法是这条路径上唯一的真机断言。</para>
    ///
    /// <para><b>钉住的四条</b>：
    /// ① 加路只让<b>网格路数</b> +1，绝不凭空多出一路素材（会话数不变）；
    /// ② 减路优先撤尾部的<b>空 lane</b>，素材一路都不许掉（路数与会话实例都不变）——
    ///    这条是 "+ 与 − 互逆" 的全部内容，也是历史上最容易击穿用户数据的一条
    ///    （旧实现会在撤空 lane 时把最后一路的会话连同解码位置一起销毁）；
    ///    ⚠ ① 与 ② 需要 <c>lane0 + 2 ≤ MaxLaneCount</c> 的余量：在天花板上起盘（9 路整轮）时
    ///    "加路扩格"这件事根本不可达，硬断言会把产品的**正确拒绝**读成缺陷（2026-10-01 首次
    ///    跑 9 路档就是这样红的）。没有余量时两条明写"不适用"，天花板契约全部由 ③ 承担。
    /// ③ D1..D9 与上限：网格顶到 9 路后，「只加不减」必须真成立（按 D2 不得撤空 lane），
    ///    且此时加路既不得把网格<b>缩小</b>、也必须给出可见反馈（不静默）；
    /// ④ 对比模式下若新 lane 落在格表之外（AB 只有 2 格），它必然不可见 ——
    ///    既然画面看不到，状态栏就必须说清楚，且模式不得被加路改掉。</para>
    ///
    /// <para><b>自恢复</b>：全程只增撤空 lane（素材始终占 <c>[0, 会话数)</c> 那些格），
    /// 结束时把网格还原到进入时的路数，后续步骤看到的状态与本方法无关。</para></summary>
    /// <param name="label">日志前缀（区分是哪条自动化流程在跑）。</param>
    private async System.Threading.Tasks.Task AssertLaneAddRemoveContractAsync(string label)
    {
        var lane0 = Grid.Count;
        var route0 = _sync.Count;
        var sessions0 = _sync.Slots.Select(s => s.Session).ToList();

        // ① 与 ② 描述的是"还能再加"那一段。已经在天花板上时，产品对加路的**正确**响应是
        // 拒绝 + 可见反馈（见 LaneCountAfterAdd / NotifyLaneLimit），此时硬断言
        // "Grid.Count == lane0+1" 会把正确行为读成缺陷 —— 2026-10-01 第一次拿 9 路跑整轮
        // 就是这样红的（此前没有任何脚本跑过 9 路档，所以这条一直没暴露）。
        // 天花板上的契约由下面 ③ 逐条硬断言（不缩格 / 有反馈 / 不动素材），故这里**明写跳过原因**，
        // 不是悄悄不判。② 连加两条空 lane，故需要 2 格余量。
        var headroom = lane0 + 2 <= MaxLaneCount;
        if (headroom)
        {
            // ── ① 加路 = 网格 +1，且不造素材 ──
            AddSlotPlaceholder();
            if (Grid.Count != lane0 + 1)
                throw new InvalidOperationException(
                    $"{label}: 加路未扩格 网格 {lane0}→{Grid.Count}（期望 {lane0 + 1}）");
            if (_sync.Count != route0)
                throw new InvalidOperationException(
                    $"{label}: 加路凭空多出一路素材 路数 {route0}→{_sync.Count}");

            // ── ② 连撤两次：只撤 lane，素材不许掉 ──
            AddSlotPlaceholder();          // 现在尾部有两条空 lane
            RemoveLastSlot();
            RemoveLastSlot();
            var sessionsNow = _sync.Slots;
            if (_sync.Count != route0 || Grid.Count != lane0 || sessionsNow.Count != sessions0.Count)
                throw new InvalidOperationException(
                    $"{label}: 撤空 lane 时误删素材 网格 {lane0 + 2}→{Grid.Count}（期望回 {lane0}）" +
                    $" 路数 {route0}→{_sync.Count}");
            for (var i = 0; i < sessions0.Count; i++)
                if (!ReferenceEquals(sessions0[i], sessionsNow[i].Session))
                    throw new InvalidOperationException(
                        $"{label}: 第 {i} 路的会话实例被换掉（撤空 lane 不该触碰素材）");
        }
        else
            Console.WriteLine($"  {label} 加减路契约：① ②（加路扩格 / 连撤两次）**不适用**——" +
                              $"网格已在 {lane0} 路（上限 {MaxLaneCount}，需 {lane0}+2≤{MaxLaneCount} 才有余量），" +
                              $"产品对加路的正确响应是拒绝+反馈；那条契约由下面 ③ 硬断言，不是没判");

        // ── ④ 对比态下加路：网格必须给新 lane 一格（合并的整个意义），拉动则必须隐藏 + 提示 ──
        // 同样只在"加得进去"时成立：触顶时 AddSlotPlaceholder 根本不产生新 lane，
        // 下面那句"最后一条 lane 必须可见"会退化成在检一条**与本次加路无关**的旧 lane（假绿）。
        if (_compareActive && Grid.Count < MaxLaneCount)
        {
            var shapeBefore = (_partition.Cols, _partition.Rows);
            var weightsBefore = _partition.ColumnWeights.ToArray();
            var statusBefore = StatusInfo.Text;
            var lanesBefore = Grid.Count;
            AddSlotPlaceholder();
            await System.Threading.Tasks.Task.Delay(150);   // IsVisible 由 Arrange 决定，等一次布局
            var addedLane = Grid.GetSurface(Grid.Count - 1);

            if (_compareOverlayActive)
            {
                // 左右拉动恒两格：多出来的 lane 必须不可见，且状态栏必须说清楚
                if (Grid.Count > 2 && addedLane is { IsVisible: true })
                    throw new InvalidOperationException(
                        $"{label}: 左右拉动下第 {Grid.Count} 条 lane 超出两格却仍然可见（拉动只铺满两路）");
                if (Grid.Count > 2 && StatusInfo.Text == statusBefore)
                    throw new InvalidOperationException(
                        $"{label}: 新 lane 被拉动的两格挤掉，状态栏却没有任何提示（用户看到的是「点了没反应」）");
            }
            else if (addedLane is not { IsVisible: true })
            {
                // 网格：一路一格，加一条 lane 就多一格 —— 这正是"以网格的渲染覆盖为基准"
                // 与旧 A/B（只显示前 2~4 路）的分水岭，看不见就说明合并没落地。
                throw new InvalidOperationException(
                    $"{label}: 网格对比下新加的第 {Grid.Count} 条 lane 不可见，" +
                    $"一路一格的基准没落地（网格 {lanesBefore}→{Grid.Count}，形状 {_partition.Cols}x{_partition.Rows}）");
            }

            // 形状未变就不许动用户拖好的列宽（形状真变了才退回均匀，见 GridPartition.Retune）
            if ((_partition.Cols, _partition.Rows) == shapeBefore &&
                !weightsBefore.SequenceEqual(_partition.ColumnWeights.ToArray()))
                throw new InvalidOperationException(
                    $"{label}: 加路在形状 {shapeBefore} 未变时改掉了已拖好的列宽 " +
                    $"[{string.Join(", ", weightsBefore)}]→[{string.Join(", ", _partition.ColumnWeights.ToArray())}]");
            RemoveLastSlot();
            await System.Threading.Tasks.Task.Delay(150);
        }
        else if (_compareActive)
            Console.WriteLine($"  {label} 加减路契约：④（对比态加路必须给新 lane 一格）**不适用**——" +
                              $"网格已在上限 {MaxLaneCount} 路，加路根本产不出新 lane");

        // ── ③ D1..D9 只加不减 + 上限：触顶后加路既不得缩格、也必须给可见反馈 ──
        // 天花板用产品自己的常数（LaneCountAfterAdd 里那个 MaxLaneCount），不在这里再写一份 9。
        GrowLanes(MaxLaneCount);
        if (Grid.Count != MaxLaneCount)
            throw new InvalidOperationException(
                $"{label}: D{MaxLaneCount} 未把网格扩到 {MaxLaneCount} 路 网格 {lane0}→{Grid.Count}");
        GrowLanes(2);   // 已在 9 路时按 D2：承诺是"只加不减"，不得反过来撤掉 7 条空 lane
        if (Grid.Count != MaxLaneCount)
            throw new InvalidOperationException(
                $"{label}: D2 在 {MaxLaneCount} 路时撤了空 lane {MaxLaneCount}→{Grid.Count}（违反「只加不减」）");
        var statusAtLimit = StatusInfo.Text;
        AddSlotPlaceholder();
        if (Grid.Count != MaxLaneCount)
            throw new InvalidOperationException(
                $"{label}: 已达 {MaxLaneCount} 路上限时加路反而缩了网格 {MaxLaneCount}→{Grid.Count}");
        if (StatusInfo.Text == statusAtLimit)
            throw new InvalidOperationException($"{label}: 加路触顶时无任何可见反馈");
        if (_sync.Count != route0)
            throw new InvalidOperationException(
                $"{label}: 加路触顶却改动了素材路数 {route0}→{_sync.Count}");

        Grid.SetCount(lane0, _realMode);                     // 还原（撤的全是空 lane）
        if (Grid.Count != lane0 || _sync.Count != route0)
            throw new InvalidOperationException(
                $"{label}: 还原失败 网格={Grid.Count}（期望 {lane0}）路数={_sync.Count}（期望 {route0}）");
        Log($"{label} 加减路契约 ✓ " +
            (headroom
                ? $"加路只扩格({lane0}→{lane0 + 1})、撤空 lane 不动素材(路数恒 {route0})、"
                : $"① ② 不适用（已在 {MaxLaneCount} 路上限，见上），") +
            $"{MaxLaneCount} 路上限不缩格且有反馈");
    }

    /// <summary>侧栏三态回归闸门（D1 / D2 / D3 / D4 / D6）。
    ///
    /// <para>这些必须是硬断言而不是日志，因为它们各自对应一个"看起来在跑、实际没用"的缺陷：
    /// 只藏内层控件会留下横线与约 41px 空白（D3，并连带把行高从 206 撑到 248，即 D6）；
    /// 把 <c>_content.Content</c> 置 null 会让五个共享面板停止参与布局、外部调用失去反馈（D4）；
    /// 不接拖拽结束事件则宽度永不回写、退出不落盘（D2）。
    /// 静默通过等于把"折叠后极难看、拖完就忘"这两件事留给用户。</para>
    ///
    /// <para><b>前置条件</b>：调用方已复位过底部栏。本方法自身把侧栏复位到 Expanded 再开始，
    /// 结束前恢复原来的展开宽度 —— 后面的探针映射等断言依赖一个正常宽度的侧栏。</para></summary>
    private async System.Threading.Tasks.Task AssertSidebarThreeStatesAsync()
    {
        var savedWidth = _sidebar.ExpandedWidth;
        var col = MainArea.ColumnDefinitions[0];

        // ---- Expanded ----
        _sidebar.SetMode(SidebarMode.Expanded);
        await System.Threading.Tasks.Task.Delay(150);
        if (!col.Width.IsAbsolute || Math.Abs(col.Width.Value - _sidebar.ExpandedWidth) > 0.5)
            throw new InvalidOperationException($"三态[Expanded]列宽异常：{col.Width.Value:0.#} ≠ {_sidebar.ExpandedWidth:0.#}");
        if (!SidebarSplitter.IsVisible)
            throw new InvalidOperationException("三态[Expanded]分割条应可见");
        if (!_sidebar.IsContentHostVisible)
            throw new InvalidOperationException("三态[Expanded]内容区应可见");

        // ---- Rail：只剩图标栏，内容区整块不可见（D3）----
        _sidebar.SetMode(SidebarMode.Rail);
        await System.Threading.Tasks.Task.Delay(150);
        if (Math.Abs(col.Width.Value - Controls.ToolsSidebar.RailWidth) > 0.5)
            throw new InvalidOperationException($"三态[Rail]列宽异常：{col.Width.Value:0.#} ≠ {Controls.ToolsSidebar.RailWidth}");
        if (SidebarSplitter.IsVisible)
            throw new InvalidOperationException("三态[Rail]分割条应不可见（48px 图标栏没有可拖区间）");
        if (_sidebar.IsContentHostVisible)
            throw new InvalidOperationException("三态[Rail]内容区仍可见 —— D3 复发（会留下横线与空白）");
        if (!_sidebar.IsRailVisible)
            throw new InvalidOperationException("三态[Rail]图标导航栏应可见");

        // ---- Hidden：列宽 0，侧栏与分割条都不见 ----
        _sidebar.SetMode(SidebarMode.Hidden);
        await System.Threading.Tasks.Task.Delay(150);
        if (Math.Abs(col.Width.Value) > 0.5)
            throw new InvalidOperationException($"三态[Hidden]列宽应为 0，实际 {col.Width.Value:0.#}");
        if (SidebarHost.IsVisible)
            throw new InvalidOperationException("三态[Hidden]侧栏宿主应不可见");
        if (SidebarSplitter.IsVisible)
            throw new InvalidOperationException("三态[Hidden]分割条应不可见");
        // D4：面板实例必须仍然挂载（只改可见性、不摘内容），否则状态与订阅全丢
        if (!ReferenceEquals(_sidebar.Active, _probe))
            throw new InvalidOperationException("三态[Hidden]激活面板被摘掉了（面板实例必须保持挂载）");

        // ---- rail 图标点击 ⇒ 恢复 Expanded 且目标面板被激活 ----
        _sidebar.SetMode(SidebarMode.Rail);
        await System.Threading.Tasks.Task.Delay(150);
        _sidebar.Activate(_bookmarks); // 先切到别的面板，确保下面验的是"点击真的激活了目标"
        var railBtn = _sidebar.RailButtonFor(_probe)
            ?? throw new InvalidOperationException("取不到探针面板的图标导航按钮");
        railBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await System.Threading.Tasks.Task.Delay(150);
        if (_sidebar.Mode != SidebarMode.Expanded)
            throw new InvalidOperationException($"rail 图标点击后未恢复 Expanded（实际 {_sidebar.Mode}）");
        if (!ReferenceEquals(_sidebar.Active, _probe))
            throw new InvalidOperationException("rail 图标点击后未激活目标面板");
        if (!SidebarSplitter.IsVisible)
            throw new InvalidOperationException("rail 图标点击恢复 Expanded 后分割条未恢复可见");

        // ---- 拖拽回写（D2）----
        col.Width = new GridLength(300, GridUnitType.Pixel);
        await System.Threading.Tasks.Task.Delay(100);
        SidebarSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        if (Math.Abs(_sidebar.ExpandedWidth - 300) > 0.5)
            throw new InvalidOperationException($"拖拽未回写展开宽度：{_sidebar.ExpandedWidth:0.#} ≠ 300");

        // ---- 拖到小于 PanelMinWidth ⇒ 切 Hidden ----
        col.Width = new GridLength(100, GridUnitType.Pixel);
        await System.Threading.Tasks.Task.Delay(100);
        SidebarSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        await System.Threading.Tasks.Task.Delay(100);
        if (_sidebar.Mode != SidebarMode.Hidden)
            throw new InvalidOperationException(
                $"拖到 {100}（< PanelMinWidth={Controls.ToolsSidebar.PanelMinWidth:0}）后应切 Hidden，实际 {_sidebar.Mode}");

        // ---- 复原：后续断言依赖一个正常展开的侧栏 ----
        _sidebar.UpdateExpandedWidth(savedWidth);
        _sidebar.SetMode(SidebarMode.Expanded);
        await System.Threading.Tasks.Task.Delay(150);
        Log($"侧栏三态 ✓ Expanded({_sidebar.ExpandedWidth:0.#}) / Rail({Controls.ToolsSidebar.RailWidth}) / Hidden(0)；拖拽回写与 rail 点击均生效");
    }

    /// <summary>悬浮传输栏回归闸门（docs/31 阶段 4.3）。覆盖三件事：
    /// <list type="number">
    /// <item><description>开启后传输栏真的搬到 owned 顶层窗上，且该窗取得逐像素透明、
    /// 命中测试钩子可用、挂上了 owner、<b>没有</b>被置顶、确实排在主窗口之上（否则会被视频子 HWND 盖住）；</description></item>
    /// <item><description>栏体贴着视频区底部（位置正确），主窗口尺寸变化后仍跟得上；</description></item>
    /// <item><description>关闭后传输栏<b>完好地</b>回到常驻位（控件未被级联销毁）、浮条窗口已销毁（不泄漏句柄）。</description></item>
    /// </list>
    ///
    /// <para>为什么这些必须是硬断言而不是日志：透明降级与命中测试失效都只在真机出现，
    /// 且都会让浮条从"装饰"变成"故障"（前者盖住视频、后者吞掉视频边缘的拖拽/滚轮），
    /// 静默通过等于把风险留给用户。姿态与对比模式覆盖层的 B3 判定一致。</para></summary>
    private async System.Threading.Tasks.Task AssertFloatingTransportAsync()
    {
        _step = "悬浮传输栏";
        _settings.FloatingTransport = true;
        ApplyBottomBarVisibility();
        await System.Threading.Tasks.Task.Delay(200);

        var win = _floatingTransport
            ?? throw new InvalidOperationException("开启悬浮传输栏后未创建 owned 顶层窗");
        if (TransportHost.IsVisible)
            throw new InvalidOperationException("悬浮模式常驻传输栏仍可见（应与浮条二选一）");
        if (win.Content is not Border { Child: Control bar } || !ReferenceEquals(bar, _transport))
            throw new InvalidOperationException("传输栏未挂到浮条窗口上");

        // 自测没有真实鼠标靠近，不能靠热区轮询（也不该依赖光标实际位置）——直接驱动浮现。
        ShowFloatingTransport();
        await System.Threading.Tasks.Task.Delay(300);

        if (!win.IsBarVisible)
            throw new InvalidOperationException("ShowFloatingTransport 后浮条仍不可见");
        if (win.AchievedTransparency != WindowTransparencyLevel.Transparent)
            throw new InvalidOperationException(
                $"浮条未取得逐像素透明（实际 {win.AchievedTransparency}），会遮挡视频画面");
        if (!win.HitTestHookInstalled)
            throw new InvalidOperationException("浮条命中测试钩子未安装（留白环带会吞掉视频边缘的鼠标消息）");
        if (!win.OwnerHwndAttached)
            throw new InvalidOperationException("浮条未挂上主窗口 owner（Z 序不可靠，可能被视频子 HWND 盖住）");
        if (win.HasTopmostStyle)
            throw new InvalidOperationException("浮条被置顶（会浮在其它应用之上，用户已明确禁止）");
        if (!win.IsAboveHostInZOrder)
            throw new InvalidOperationException("浮条不在主窗口之上（会被视频子 HWND 盖住）");

        AssertFloatingTransportGeometry(win, "初始");

        // resize 跟随：改宽度后栏体必须重新贴合视频区底边。
        var widthBefore = Width;
        if (WindowState == WindowState.Normal)
        {
            var syncsBeforeResize = win.GeometrySyncs;
            Width = widthBefore + 120;
            await SettleFloatingTransportAsync(win, "resize 后", syncsBeforeResize,
                () => FloatingTransportGeometryMatches(win, out _));
            AssertFloatingTransportGeometry(win, "resize 后");
            var syncsBeforeRestore = win.GeometrySyncs;
            Width = widthBefore;
            await SettleFloatingTransportAsync(win, "resize 还原后", syncsBeforeRestore,
                () => FloatingTransportGeometryMatches(win, out _));
            AssertFloatingTransportGeometry(win, "resize 还原后");
        }
        else
        {
            Log($"窗口状态={WindowState}，跳过 resize 跟随断言");
        }

        // 坑 1 的闸门：关闭悬浮时传输栏必须完好回到常驻位（绝不能被窗口级联销毁）。
        _settings.FloatingTransport = false;
        ApplyBottomBarVisibility();
        await System.Threading.Tasks.Task.Delay(300);

        if (_floatingTransport is not null)
            throw new InvalidOperationException("关闭悬浮后浮条窗口未被销毁（顶层窗句柄泄漏）");
        if (!ReferenceEquals(TransportHost.Child, _transport))
            throw new InvalidOperationException("关闭悬浮后传输栏未回到常驻位（控件可能已被级联销毁）");
        if (!_transport.IsAttachedToVisualTree())
            throw new InvalidOperationException("传输栏已脱离视觉树（关闭浮条时被误销毁）");

        await System.Threading.Tasks.Task.Delay(200);
        AssertBottomBarOrder();
        Log("悬浮传输栏 ✓ 透明 / 命中测试 / owner / 非置顶 / Z 序 / 位置 / resize / 回收 全部通过");
    }

    /// <summary>
    /// 等浮条几何**收敛**（宽度与 Y 连续 3 次采样都不变）后再让调用方去断言。
    /// </summary>
    /// <para>原先这里只是 <c>await Task.Delay(400)</c>（docs/41 复核实测发现的偶发假红）：
    /// 冷启动（刚重建、磁盘缓存未热）时 400ms 不够，实测 1/7 次在"resize 还原后"读到的
    /// 是<b>上一次加宽后</b>的宽度（实际 1448 vs 期望 1328，差值正好是本次 resize 的 +120）
    /// ⇒ 门禁偶发红，每次都要花时间排障，而 product code 并没有问题。</para>
    /// <para>改成轮询收敛：既不削弱判据（收敛后仍要求 2px 内一致，宽度真算错照样红），
    /// 也不再依赖一个拍脑袋的固定等待时长。超时（3s）后不再等，直接交给断言——
    /// 真不收敛时仍会如实判红。</para>
    /// <para><b>Y 也必须一起轮询</b>：写尺寸会让 Avalonia 把浮条夹回工作区，被夹走的正是 Y，
    /// 而断言比的是栏体底边（<c>Position.Y</c> + 高度）。只盯宽度的话，夹取后的错值会被判成
    /// "已收敛"直接放行 —— 浮条停在 1519 而非 1629 那次错值就是这么躲过门禁的。</para>
    private async System.Threading.Tasks.Task SettleFloatingTransportAsync(
        Controls.FloatingTransportWindow win, string label, int sinceSyncs = 0,
        Func<bool>? converged = null, int timeoutMs = 3000)
    {
        var startedUtc = System.DateTime.UtcNow;
        var deadline = startedUtc + System.TimeSpan.FromMilliseconds(timeoutMs);
        // 第一段：等"重算确实发生过"。只要求数值连续不变会在**还没轮到重算**的那段时间上过早收敛——
        // 实测 resize 还原后那轮耗时 558ms，轮询采到的 3 次稳定值全是上一次的 1448 ⇒ 假红。
        // 超时不抛，交给后面的原判据，真不重算照样红（不削弱断言）。
        while (System.DateTime.UtcNow < deadline && win.GeometrySyncs <= sinceSyncs)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(15);
        }
        var synced = win.GeometrySyncs > sinceSyncs;
        var syncsSeen = win.GeometrySyncs - sinceSyncs;

        // 第二段：给了判据就**重试判据本身**（首选路径）；没给才退回"宽高稳定 3 次采样"。
        // 为什么优先重试判据：一次"发生过重算"不代表那次重算读的是新尺寸 —— 2026-09-27 包内自测
        // 红在"还原尺寸后浮条仍是 1448"，就是重算先于主窗尺寸落地发生了一次，代数计数满足了、值却是旧的。
        var lastW = win.Bounds.Width;
        var lastY = (double)win.Position.Y;
        var stable = 0;
        var ok = false;
        while (System.DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(30);
            if (converged is not null)
            {
                if (converged()) { ok = true; break; }
                continue;
            }
            var nowW = win.Bounds.Width;
            var nowY = (double)win.Position.Y;
            stable = System.Math.Abs(nowW - lastW) < 0.01 && System.Math.Abs(nowY - lastY) < 0.5
                ? stable + 1
                : 0;
            lastW = nowW;
            lastY = nowY;
            if (stable >= 3) { ok = true; break; }
        }
        var elapsedMs = (int)(System.DateTime.UtcNow - startedUtc).TotalMilliseconds;
        // 只记录、不改判决：这几行是给"又红了"准备的现场，不是新的通过条件。
        if (converged is null && !synced)
            Log($"{label}：{timeoutMs}ms 内未观测到几何重算（GeometrySyncs 仍为 {win.GeometrySyncs}），仍按原判据断言");
        else if (!ok)
            Log($"{label}：{timeoutMs}ms 内未收敛（耗时 {elapsedMs}ms、观测到 {syncsSeen} 次重算、" +
                $"最后 宽={win.Bounds.Width:0.#} Y={win.Position.Y}），仍按原判据断言");
        else
            Log($"{label}：收敛于 {elapsedMs}ms（重算 {syncsSeen} 次）");
    }

    /// <summary>浮条几何闸门：窗口宽 = 视频区宽 - 2×边距 + 2×留白，栏体底边 = 视频区底边 - 边距。
    /// 全部按物理像素比对，容差 2px。</summary>
    /// <summary>浮条几何是否与主窗<b>当前</b>布局一致：只回答是/否并给出偏差，不抛异常。
    ///
    /// <para><b>为什么把判据拆成"判定 + 断言"两层</b>：resize 之后需要<b>有界重试</b>，而重试的必须
    /// 是这条判据本身。之前重试的是别的东西 —— 先是"数值连续 3 次不变"（会在还没轮到重算时收敛成
    /// 假红），再改成"等 <c>GeometrySyncs</c> 前进"（2026-09-27 实测仍然红：还原尺寸后确实发生了一次
    /// 重算，但那次重算读到的还是上一轮的 1448，因为主窗自己的尺寸当时还没落地）。
    /// 在判据之外猜"什么时候算稳定"，猜错要么把真问题等成绿灯、要么把正常延迟等成假红；
    /// 直接重试判据本身，只有"超时仍不成立"这一种红，而那正是真故障。</para></summary>
    private bool FloatingTransportGeometryMatches(Controls.FloatingTransportWindow win, out string mismatch)
    {
        mismatch = "";
        double h = CenterPanel.Bounds.Height;
        if (h <= 0) { mismatch = "视频区高度为 0，无法校验浮条位置"; return false; }

        PixelPoint tl, br;
        try
        {
            tl = CenterPanel.PointToScreen(new Point(0, 0));
            br = CenterPanel.PointToScreen(new Point(CenterPanel.Bounds.Width, h));
        }
        catch (Exception ex)
        {
            // 布局过渡态里 PointToScreen 可能短暂失败：判"尚未成立"，交给重试与超时，
            // 不在这里下结论（真一直失败到最后，超时后的断言会带着这条原因判红）。
            mismatch = $"视频区 PointToScreen 失败 {ex.Message}";
            return false;
        }

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        const double pad = Controls.FloatingTransportWindow.Pad;
        const double margin = Controls.FloatingTransportWindow.EdgeMargin;

        var expectWidthDip = (br.X - tl.X) / scaling - 2 * margin + 2 * pad;
        var barBottom = win.Position.Y + (win.Bounds.Height - pad) * scaling;
        var expectBottom = br.Y - margin * scaling;

        if (Math.Abs(win.Bounds.Width - expectWidthDip) > 2)
        {
            mismatch = $"浮条宽度不符 实际={win.Bounds.Width:0.#} 期望={expectWidthDip:0.#} DIP";
            return false;
        }
        if (Math.Abs(barBottom - expectBottom) > 2)
        {
            mismatch = $"浮条底边不符 实际={barBottom:0.#} 期望={expectBottom:0.#}（视频区底边 {br.Y}）";
            return false;
        }
        return true;
    }

    private void AssertFloatingTransportGeometry(Controls.FloatingTransportWindow win, string label)
    {
        if (!FloatingTransportGeometryMatches(win, out var mismatch))
            throw new InvalidOperationException($"{label}：{mismatch}");

        var h = CenterPanel.Bounds.Height;
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        const double pad = Controls.FloatingTransportWindow.Pad;
        var br = CenterPanel.PointToScreen(new Point(CenterPanel.Bounds.Width, h));
        var barBottom = win.Position.Y + (win.Bounds.Height - pad) * scaling;
        Log($"{label}：浮条 {win.Bounds.Width:0.#}×{win.Bounds.Height:0.#} DIP @ ({win.Position.X},{win.Position.Y}) " +
            $"底边={barBottom:0.#} 视频区底边={br.Y}");
    }

    /// <summary>
    /// docs/41 #16：给任意自测模式挂一条**总时限**看门狗。
    /// 原先只有 --selftest 有（基于 _step 稳定 40s），其余 5 个模式完全没有兜底 ⇒
    /// 任一原生调用（抓帧 / Present / 会话销毁）挂起时进程永久不退出，门禁挂到超时。
    /// 这里用「总时限」而不是「步骤稳定」：后者要求 _step 频繁更新，而 --sessiontest /
    /// --magnifybench 这类单步骤很长的模式会被误触发（假红）。退出码 3 与 selftest 看门狗一致。
    /// </summary>
    private void StartDeadlineWatchdog(string mode, int seconds)
    {
        LogArtifactIdentity(mode);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(seconds));
            Console.Error.WriteLine($"{mode}: 看门狗触发 ✗ 总时长超过 {seconds}s（疑似原生调用挂起）");
            Console.Error.Flush();
            // 与 selftest 看门狗同一套双保险：先投递 UI 线程，再给不依赖 UI 线程的硬兜底，
            // 否则 UI 线程被同步阻塞时 Post 永不执行，进程从"超时退出"退化成"永久挂起"。
            Dispatcher.UIThread.Post(() => ExitSelfTest(3));
            WatchdogHardExit(mode, 3);
        });
    }

    /// <summary>把"这份读数出自哪个产物"打在每条自动化日志的开头（切片 1a：新鲜度守卫的运行时半边）。
    ///
    /// <para><b>为什么必须有</b>：本轮两次假读数都源于<b>跑的不是最新产物</b> —— 一次是构建被文件
    /// 占用打断后仍继续跑（于是打出引用了源码里已删文案的判红消息），一次是拿旧二进制复跑新判据。
    /// 两者都能靠"日志头的产物 mtime 对比源码 mtime"当场识破，不该靠人事后 grep 消息串。</para>
    ///
    /// <para><b>为什么以 <c>Environment.ProcessPath</c> 为主</b>：AOT / 单文件发布时托管程序集嵌在
    /// exe 里，<c>Assembly.Location</c> 恒返回空串（编译器警告 IL3000 说的就是这件事）——
    /// 恰恰在最需要溯源的 AOT 包内自测里，拿 Location 当身份会打印"(无独立文件)"。
    /// Location 只在 `dotnet X.dll` 这类框架依赖启动方式下有意义，故仅当它非空时附加打印。</para>
    /// <para>整段异常不外抛：这是取证，不能反过来打断自测判决。</para></summary>
    /// <summary>构建期源码指纹的短摘要（日志用）。原始值是"每文件 SHA256 全值"的拼接（约 3 KB），
    /// 不能整条打进日志；逐项比对由 <c>tools/验证守卫.ps1 -Fingerprint</c> 读
    /// 输出目录里的 <c>source-fingerprint.txt</c> 完成。</summary>
    private static readonly string SourceFingerprintShort =
        System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(_3FCompare.BuildInfo.SourceFingerprint.Value)))[..16];

    private void LogArtifactIdentity(string mode)
    {
        try
        {
            var informational = (typeof(MainWindow).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?
                .InformationalVersion ?? "(未标注)";

            var exe = Environment.ProcessPath ?? string.Empty;
            var exeStamp = string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)
                ? "(无文件)"
                : System.IO.File.GetLastWriteTimeUtc(exe).ToString("yyyy-MM-dd HH:mm:ss") + "Z";
            Console.WriteLine($"[产物] mode={mode} process={exe}");
            Console.WriteLine($"[产物]   InformationalVersion={informational} process-mtime={exeStamp} " +
                              $"baseDir={AppContext.BaseDirectory}");
            // 内容指纹（构建期生成，见 3FCompare.csproj 的 GenerateSourceFingerprint）：
            // mtime 只能说"有东西比产物新"，指纹能说"是哪个文件没进这次构建"。
            // 原始指纹是"每文件 SHA256 全值"拼接（约 3 KB），日志里只打它的短摘要；
            // 逐项比对由 tools/验证守卫.ps1 -Fingerprint 拿 source-fingerprint.txt 做。
            Console.WriteLine($"[产物]   source-fingerprint={SourceFingerprintShort} " +
                              $"files={_3FCompare.BuildInfo.SourceFingerprint.FileCount} " +
                              "（覆盖面=App 工程自身源文件；Core/native 由验证守卫按 mtime 兜）");

            // ⚠ bench 开关必须**每轮无条件**打进 stdout，不能只在"开关起作用那一支"留痕。
            //   教训：#32 的第一批 A/B（12 对 24 轮）里，臂自验读的是"组播放被守卫拒掉"那一行——
            //   它只在恰好发生拒绝的轮次才出现 ⇒ 24 轮里只有 3 对可判、不一致对 0 ⇒ McNemar p=1.0，
            //   等于花 10 分钟判了个寂寞（off 臂 12 轮里 9 轮不可判）。开关状态是进程启动就定的事实，
            //   打在每轮头部才是可配对的证据。
            Console.WriteLine("[产物]   bench-knobs:" +
                $" FC_HOLD_PLAY_PENDING={System.Environment.GetEnvironmentVariable("FC_HOLD_PLAY_PENDING") ?? "(unset)"}" +
                $" ⇒ HoldRejectedPlay={_3FCompare.Core.Sync.SyncController.HoldRejectedPlay}" +
                $" FC_STEPS={System.Environment.GetEnvironmentVariable("FC_STEPS") ?? "(unset)"}" +
                $" FC_NOMOSAIC={System.Environment.GetEnvironmentVariable("FC_NOMOSAIC") ?? "(unset)"}" +
                $" FC_SKIP_ZINJECT={System.Environment.GetEnvironmentVariable("FC_SKIP_ZINJECT") ?? "(unset)"}" +
                $" FC_REGION_SPACING_MS={System.Environment.GetEnvironmentVariable("FC_REGION_SPACING_MS") ?? "(unset)"}" +
                $" FC_KEEP_LAYOUTOVERLAY={System.Environment.GetEnvironmentVariable("FC_KEEP_LAYOUTOVERLAY") ?? "(unset)"}" +
                $" FC_FORCE_GDI_ROUTE={System.Environment.GetEnvironmentVariable("FC_FORCE_GDI_ROUTE") ?? "(unset)"}");


#pragma warning disable IL3000 // 单文件下恒为空：正因可能为空，才只在非空时附加打印
            var asmPath = typeof(MainWindow).Assembly.Location;
            if (!string.IsNullOrEmpty(asmPath) &&
                !string.Equals(asmPath, exe, StringComparison.OrdinalIgnoreCase) &&
                System.IO.File.Exists(asmPath))
                Console.WriteLine($"[产物]   managed-assembly={asmPath} " +
                                  $"assembly-mtime={System.IO.File.GetLastWriteTimeUtc(asmPath):yyyy-MM-dd HH:mm:ss}Z");
#pragma warning restore IL3000

            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[产物] 身份读取失败（不作为自测判决）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 看门狗的**硬兜底**：不依赖 UI 线程，到点直接强制结束进程。
    /// </summary>
    /// <para>⚠ 必须尊重 <see cref="ExitSelfTest"/> 的不可重入闸门（docs/41 复核必修）：
    /// ExitSelfTest 的清理链（DestroyAllSessions → Close → 20×50ms RunJobs → 600ms 静默）
    /// 正常就要 1.5~2s，慢盘/多会话下更久。若看门狗恰好在它<b>开始前</b>触发，原实现的
    /// 固定 5s 硬兜底会在一次<b>正常退出进行中</b>把进程杀成 code=3——把通过谎报成超时失败。
    /// 这里先查闸门：已在退出流程中则追加 60s 宽限，期间进程会带着<b>真实退出码</b>自行结束；
    /// 宽限用尽仍存活才判定"退出链真卡死"，此时才硬杀（宁可丢码也不永久挂起）。</para>
    private void WatchdogHardExit(string mode, int code)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(5));
            var exiting = System.Threading.Volatile.Read(ref _selfTestExiting) != 0;
            if (exiting)
                await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(60));
            Console.Error.WriteLine(exiting
                ? $"{mode}: 看门狗兜底硬退出（退出链超时 60s 未结束）"
                : $"{mode}: 看门狗兜底硬退出（Post 未被执行）");
            Console.Error.Flush();
            // 用 TerminateProcess 而不是 Environment.Exit：后者要走 CLR 托管停机与
            // DLL detach，原生线程会反向回调已销毁的运行时（ceemain.cpp:1750），
            // 退出码退化成 127，与"测试失败"无法区分。
            TerminateProcess(GetCurrentProcess(), code);
        });
    }

    private async System.Threading.Tasks.Task RunSelftestAsync(string videoPath, string? dropVideoPath = null)
    {
        // --selftest 用的是下面这个自建 40s 卡步计时器，不走 StartDeadlineWatchdog ⇒
        // 产物身份要在方法入口自己打一次，否则唯一进门禁的这条流程反而没有"出自哪个二进制"的记录。
        LogArtifactIdentity("selftest");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var last = _step; var stable = 0;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                stable = _step == last ? stable + 1 : 0;
                last = _step;
                if (stable >= 40)
                {
                    Console.Error.WriteLine($"selftest: 看门狗触发 ✗ 卡在步骤 [{_step}] 超过 40s");
                    Console.Error.Flush();
                    // D6：本循环跑在 Task.Run（线程池线程），而 ExitSelfTest 内部会
                    // DestroyAllSessions()、Close()、Dispatcher.UIThread.RunJobs()
                    // ——三者都直接操作 Avalonia 控件与播放器会话。跨线程调用会让
                    // 结论不可信：可能在 UI 线程正忙时并发销毁会话，表现为假崩溃；
                    // 也可能因异常被吞而静默返回，表现为假通过。
                    // 投递回 UI 线程执行，与主线程自测流程串行。
                    Dispatcher.UIThread.Post(() => ExitSelfTest(3));
                    // ⚠ 兜底：只 Post 是不够的。若 UI 线程正好被同步阻塞（例如卡在
                    // 某个原生调用或同步等待里），Post 永不执行 ⇒ 看门狗形同虚设，
                    // 进程从"40s 后以 code=3 退出"退化成"永久挂起"，CI 直接超时。
                    // 给一个不依赖 UI 线程的硬兜底。走到这里时日志已打印出 [_step]，排障信息不会丢。
                    WatchdogHardExit("selftest", 3);
                    return; // 已请求退出：停止本循环，避免每秒重复投递
                }
            }
        });
        var code = 2;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"selftest: 文件不存在 {videoPath}");
                ExitSelfTest(2);
            }

            _step = "打开";
            Grid.SetCount(1, _realMode);
            Log($"打开 {videoPath} ({(_realMode ? "真实" : "演示")}模式)");
            _coordinator.OpenFiles(new[] { videoPath }, autoPlay: true);

            // 等待就绪（≤15s）
            _step = "等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            var ready = _sync.ReadMasterSnapshot();
            if (ready is null || !PlaybackCoordinator.IsReadyState(ready.State))
                throw new InvalidOperationException($"未就绪（状态={ready?.State}）");
            Log($"就绪 ✓ 时长={TimeSpan.FromTicks(ready.Duration100ns):hh\\:mm\\:ss}");

            // 布局断言必须在窗口完成一次布局之后跑（构造期 Bounds 全是 0）
            _step = "布局";
            // docs/31 阶段 4：时间轴/状态栏可折叠、传输栏可悬浮，三者均可持久化。
            // 断言前显式复位到默认（常驻三栏），再等一次布局让 IsVisible 变更生效
            // —— 否则自测结果会随用户配置而变。只改内存中的 _settings，不落盘
            // （_selfTestMode 下全部保存路径均已屏蔽）。
            _settings.TimelineCollapsed = false;
            _settings.StatusBarCollapsed = false;
            _settings.FloatingTransport = false;
            ApplyBottomBarVisibility();
            await System.Threading.Tasks.Task.Delay(200);
            AssertBottomBarOrder();

            // 加路/减路按钮契约（须在任何依赖网格路数的断言前跑完并自行还原网格）
            _step = "加减路契约";
            await AssertLaneAddRemoveContractAsync("selftest");

            // 侧栏三态（D1/D2/D3/D4/D6）：同样必须在窗口完成一次布局之后跑
            _step = "侧栏三态";
            await AssertSidebarThreeStatesAsync();

            // 自动播放断言（打开完成→统一 Play 契约；须在步进前验证——步进会暂停播放）
            _step = "自动播放断言";
            var deadline2 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline2)
            {
                var s = _sync.ReadMasterSnapshot();
                if (s is { State: PlayerState.Playing }) { Log($"播放中 pos={TimeSpan.FromTicks(s.Position100ns):g}"); break; }
                await System.Threading.Tasks.Task.Delay(100);
            }
            if (_realMode && _sync.ReadMasterSnapshot() is not { State: PlayerState.Playing })
                throw new InvalidOperationException($"自动播放未启动（状态={_sync.ReadMasterSnapshot()?.State}）");

            // VRR 呈现路径覆盖。⚠ 这条过去只读 `SetPresentConfig` 的返回值 ⇒ **恒绿形状**：
            //   返回值只说明交换链**允许**撕裂，不说明每次 Present 真的没等（修复前后同样打 ✓，
            //   README 里"已支持"就是从这行抄的）。现在改成量内核那两个计数器：
            //   `PresentWait100ns / SwapChainPresents` = 单次 Present 平均阻塞时长。
            //   VSync 锁定时应约等于一个刷新周期（60Hz ≈ 16.7ms）；撕裂真生效应趋近 0。
            // ⚠ 判决口径（别改回去）：**数值本身不判红**——固定刷新率屏上"允许撕裂但仍被节流"是合法硬件形态，
            //   把它写成判红就是把环境当缺陷。只有**仪器级**事实才判红：计数器倒退或压根不动。
            _step = "VRR 呈现";
            var vrrAccepted = _sync.Slots[0].Session.SetPresentConfig(true);
            Log($"VRR 撕裂呈现: SetPresentConfig(撕裂) 返回 {(vrrAccepted ? "true（交换链接受该请求）" : "false（不支持→静默回退 VSync）")}" +
                " —— 这**只是请求结果**，不代表撕裂真发生");
            if (_realMode)
            {
                var vrrFrom = _sync.ReadMasterSnapshot();
                var presentFrom = vrrFrom is null ? -1L : vrrFrom.SwapChainPresents;
                var waitFrom = vrrFrom is null ? -1L : vrrFrom.PresentWait100ns;
                var vrrDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                long presentDelta = 0, waitDelta = 0;
                while (DateTime.UtcNow < vrrDeadline)
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    var now = _sync.ReadMasterSnapshot();
                    if (now is null) continue;
                    presentDelta = now.SwapChainPresents - presentFrom;
                    waitDelta = now.PresentWait100ns - waitFrom;
                    if (waitDelta < 0 || presentDelta < 0)
                        throw new InvalidOperationException(
                            $"VRR 计数器倒退：Present +{presentDelta}、Present 内等待 +{waitDelta}（100ns）" +
                            " ⇒ 内核那两个计数不是同一个会话的单调累计，本判据的原料不成立");
                    if (presentDelta >= 10) break;
                }
                if (presentDelta < 3)
                {
                    // 采不到呈现次数就是**没量到**：明写未验证，不打 ✓（本仓规矩：未验证 ≠ 通过）。
                    Log($"VRR 单次 Present 等待：**未验证**（3s 内仅采到 {presentDelta} 次呈现，样本不足）" +
                        $"⇒ 这一项没量到东西，别看上面那行 true 就当撕裂验过");
                }
                else
                {
                    var msPerPresent = waitDelta / 1e4 / presentDelta;
                    Log($"VRR 实测：{presentDelta} 次 Present 内总阻塞 {waitDelta / 1e4:0.##}ms" +
                        $" ⇒ 单次平均 {msPerPresent:0.##}ms；VSync 锁定特征 ≈ 一个刷新周期，撕裂真开应趋近 0" +
                        (vrrAccepted && msPerPresent >= 8.0
                            ? "；⚠ 交换链**允许**撕裂但每次仍在等 ⇒ 这台屏/这个驱动下那句 true 不足以说明撕裂发生"
                            : string.Empty));
                }
            }


            // RTX VSR 状态读取。判决口径（别改回去）：
            //   ① 内核不提供该入口时记 **SKIP，不判红** —— 出货内核（third_party 那棵）今天
            //      还没有 Set/GetVideoSuperResolutionStatus 两个导出（实测那份 DLL 93 个导出、
            //      源码 grep 0 命中），这是已知缺口，不是故障。
            //   ② Active 只表示驱动没拒绝这次请求，**不是**画面被增强的证据：本机实测该路径的
            //      HRESULT 连对一个伪造的 GUID 都返回 S_OK，所以它不携带任何能力信息。
            //   ③ 唯一够格判红的是一条内核自己保证的不变量：VideoRenderer.cpp:7178 在
            //      Active=1 时把 Reason 覆写成 None。读到别的组合，说明托管镜像在按错偏移读
            //      ——正是"尺寸相同、字段换序"那类会静默全错读数的事故。
            _step = "RTX VSR 状态";
            var vsrAccepted = _sync.Slots[0].Session.SetVideoSuperResolution(true);
            if (!_sync.Slots[0].Session.ReadVideoSuperResolutionStatus(out var vsr))
            {
                Log($"RTX VSR: 请求登记结果 {(vsrAccepted ? "true" : "false")}，状态读不出来" +
                    " ⇒ **SKIP**（盘上的内核不含该导出，不代表功能坏，也不代表功能没坏）");
            }
            else
            {
                Log($"RTX VSR: requested={vsr.Requested} NVIDIA适配器={vsr.NvidiaAdapter} " +
                    $"active={vsr.Active} 驱动拒绝闩={vsr.DriverRejected} 原因={vsr.Reason} " +
                    $"源={vsr.SourceWidth}x{vsr.SourceHeight} 目标={vsr.TargetWidth}x{vsr.TargetHeight}");
                if (vsr.Active && vsr.Reason != VideoSuperResolutionReason.None)
                    throw new InvalidOperationException(
                        $"VSR active=true 却带原因码 {vsr.Reason} ⇒ 内核在 active 时恒把 reason 写成 None，" +
                        "读到别的组合说明托管镜像字段偏移与内核不一致");
                Log("  ⚠ 上面 active 只说明驱动没拒绝本次请求；画质档位与总开关归 NVIDIA App / 控制面板，" +
                    "本程序设不了也读不到，画面是否真被增强这一项本步骤**不判**");
            }


            // 帧步进 +1：位置不得后退（真实模式）
            // 且**必须**在播放态下步进——这是 docs/15 §2.1 的回归场景：
            // master 走 StepFrame 时内核会 SetState(Paused)，从路走 Seek 却保持播放态，
            // 于是"播放中步进"会让 master 停住、从路继续跑，画面立刻错帧。
            // 旧实现恰好因为调用方（含 multitest）都先 Pause 才一直没暴露。
            _step = "帧步进";
            if (_realMode)
            {
                _sync.Play();                                   // 刻意不暂停
                await System.Threading.Tasks.Task.Delay(400);
                // 前置条件必须坐实：若压根没进播放态，下面那条"步进后全路暂停"
                // 的断言等于没测到目标场景，会变成一条假绿的回归用例。
                var playingCount = _sync.ReadAllSnapshots().Count(s => s?.State == PlayerState.Playing);
                if (playingCount == 0)
                {
                    throw new InvalidOperationException(
                        "帧步进回归前置条件不足：无法进入播放态" +
                        $"（运行时错误={_sync.LastRuntimeError ?? "无"}）——此场景必须在播放中步进才成立");
                }
                Log($"帧步进前置：{playingCount} 路处于播放态 ✓");
                var before = _sync.GetMasterPosition100ns();
                // 同 sessiontest：UI 线程直调是自测路径的**刻意例外**（无用户可冻结 + 断言依赖
                // "返回后立刻读快照"的同步顺序），理由与代价论证见 RunSessiontestAsync 里那处注释。
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"帧步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    Log($"⚠ 帧步进位置后退（已知问题）{before} → {after}");

                // 核心断言：步进后**所有路**都必须是暂停态
                await System.Threading.Tasks.Task.Delay(300);
                var snaps = _sync.ReadAllSnapshots();
                var stateDesc = string.Join(", ", snaps.Select((s, i) => $"路{i}={s?.State}"));
                // P5：这条**不是失败**，措辞必须中性，否则容易被误读成整轮失败。
                // docs/15 §2.1 约定「帧步进必须让所有路进入暂停态」⇒ 路进入 Paused 是**预期结果**；
                // 暂停态下后续的 Play 返回 InvalidState 同样属正常表现（实测来源是停滞看门狗的
                // Pause→Play 轻量恢复与步进态重叠），它只被记进 SyncController.LastRuntimeError
                // 供排障，**不参与**本轮的通过/失败判定。
                // 真正的失败判据是下面那条"仍有路在播放"的断言（抛异常 ⇒ 非 0 退出）。
                Log($"帧步进后状态: {stateDesc}（预期；后续 Play 返回 InvalidState 属暂停态下的正常表现）");
                var stillPlaying = snaps
                    .Select((s, i) => (Index: i, State: s?.State))
                    .Where(x => x.State == PlayerState.Playing)
                    .Select(x => x.Index)
                    .ToArray();
                if (stillPlaying.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"帧步进后仍有路在播放: {stateDesc}（docs/15 §2.1：步进必须先让全路暂停，" +
                        "否则对比的是错帧）");
                }
            }

            // 秒步进 +1：同上断言
            _step = "秒步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                // StepSeconds 与 StepFrames 同源（持 _stepGate 逐路原生 Seek）。这里同样是
                // 自测路径的刻意例外：单次调用、有界耗时、无用户可冻结，且断言依赖同步顺序。
                _sync.StepSeconds(_sync.StepProfile.SecondsStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"秒步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    Log($"⚠ 秒步进位置后退（已知问题）{before} → {after}");
            }

            // 窗口最大化/还原测试：验证窗口最大化后视频渲染是否继续
            _step = "窗口最大化测试";
            if (_realMode)
            {
                // 确保播放中
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                var hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (hwnd != nint.Zero)
                {
                    var before = _sync.GetMasterPosition100ns();
                    var beforeSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化前: pos={TimeSpan.FromTicks(before):g} presented={beforeSnap?.PresentedVideoFrames} state={beforeSnap?.State}");

                    // 最大化（使用 Avalonia WindowState 以触发布局更新）
                    WindowState = WindowState.Maximized;
                    await System.Threading.Tasks.Task.Delay(3000);

                    // 强制重新测量/布局，确保子 HWND 尺寸同步
                    Grid.InvalidateMeasure();
                    Grid.InvalidateArrange();
                    UpdateLayout();

                    var midSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化后: pos={TimeSpan.FromTicks(midSnap?.Position100ns ?? 0):g} presented={midSnap?.PresentedVideoFrames} state={midSnap?.State}");

                    // 如果进入 Failed 状态，等待恢复尝试（PollSnapshots 中的 recovery 逻辑）
                    if (midSnap?.State == PlayerState.Failed)
                    {
                        Log($"引擎进入 Failed 状态，等待恢复...");
                        await System.Threading.Tasks.Task.Delay(3000);
                        var recoverySnap = _sync.ReadMasterSnapshot();
                        Log($"恢复后: state={recoverySnap?.State} presented={recoverySnap?.PresentedVideoFrames}");
                        if (recoverySnap?.State == PlayerState.Failed)
                        {
                            Log($"❌ 引擎恢复失败，渲染管线永久停滞");
                            throw new InvalidOperationException("窗口最大化导致引擎永久失败");
                        }
                        else
                        {
                            Log($"✅ 引擎恢复成功 (state={recoverySnap?.State})");
                        }
                    }

                    // 还原（使用 Avalonia WindowState 以触发布局更新）
                    WindowState = WindowState.Normal;
                    await System.Threading.Tasks.Task.Delay(3000);

                    // 强制重新测量/布局
                    Grid.InvalidateMeasure();
                    Grid.InvalidateArrange();
                    UpdateLayout();

                    var afterSnap = _sync.ReadMasterSnapshot();
                    var after = _sync.GetMasterPosition100ns();
                    var presentedDelta = (afterSnap?.PresentedVideoFrames ?? 0) - (midSnap?.PresentedVideoFrames ?? 0);
                    Log($"还原后: pos={TimeSpan.FromTicks(after):g} state={afterSnap?.State} presented={afterSnap?.PresentedVideoFrames} (Δpresented/3秒={presentedDelta})");

                    if (presentedDelta <= 0 && afterSnap?.State == PlayerState.Playing)
                    {
                        Log($"❌ 窗口最大化/还原后视频卡死！presented 未增长");
                        throw new InvalidOperationException("最大化/还原导致渲染停滞");
                    }
                    else if (afterSnap?.State == PlayerState.Failed)
                    {
                        Log($"❌ 引擎处于 Failed 状态，渲染管线已死锁");
                        throw new InvalidOperationException("最大化/还原导致引擎永久失败");
                    }
                    else if (afterSnap?.State != PlayerState.Playing)
                    {
                        Log($"⚠ 播放状态变为 {afterSnap?.State}（非卡死，尝试恢复播放）");
                        _sync.Play();
                        await System.Threading.Tasks.Task.Delay(1000);
                        var final = _sync.ReadMasterSnapshot();
                        Log($"恢复播放后: state={final?.State} presented={final?.PresentedVideoFrames}");
                    }
                    else
                    {
                        Log($"✅ 最大化/还原测试通过：presented +{presentedDelta}/3秒");
                    }
                }
                else
                {
                    Log("⚠ 无法获取窗口句柄，跳过最大化测试");
                }
            }

            // #24 回归：WM_SIZE 埋点不得在 UI 线程上同步落盘。
            // 判据用"节流跳过计数 / 落盘计数"而不是计时：计时受机器负载影响（本仓库已多次被这类
            // 不稳定断言坑过），而计数是确定性的。注入方式沿用本文件"UI 消息注入"的既有先例
            // （SendMessageW 在同线程下直接调用窗口过程，见下方 P/Invoke 注释），
            // 于是 8 条 WM_SIZE 在同一拍内被处理、必然落在同一个 FlushThrottleMs 窗口里。
            // 反向验证：把 PlayerSurface 的 Resize 埋点换回 Log（或去掉节流）⇒ 8 条各自落盘 ⇒ 判红。
            _step = "Resize 埋点不阻塞 UI 线程";
            if (_realMode)
            {
                const uint WM_SIZE = 0x0005;
                var surface = Grid.GetSurface(0)
                    ?? throw new InvalidOperationException("无法取得 PlayerSurface 0，无法注入 WM_SIZE");
                if (surface.Hwnd == nint.Zero)
                    throw new InvalidOperationException("PlayerSurface 0 尚无子 HWND，无法注入 WM_SIZE");

                var skipsBefore = _3FCompare.Diagnostics.ComponentLog.ThrottledSkipCount;
                var flushesBefore = _3FCompare.Diagnostics.ComponentLog.FlushCount;
                // 8 个互不相同且与真实客户区不同的尺寸：`_clientW/_clientH` 去重只放行"尺寸真变"的那些，
                // 否则本步会静默变成"什么都没注入"。
                for (var i = 1; i <= 8; i++)
                {
                    var w = 100 + i * 37;
                    var h = 100 + i * 23;
                    // lParam 低 16 位 = 宽、高 16 位 = 高（与 PlayerSurface 的 WM_SIZE 解析同构）。
                    // 两个操作数都显式转 long：只转一个会触发 CS0675（有符号扩展上的按位或）。
                    SendMessageW(surface.Hwnd, WM_SIZE, nint.Zero,
                        (nint)((long)(w & 0xFFFF) | ((long)(h & 0xFFFF) << 16)));
                }
                // 让一拍拍掉 Background 优先级的 RedrawRequest 埋点（它们同样走节流路径）。
                await Dispatcher.UIThread.InvokeAsync(() => { });

                // #1 修复（断言偶发假红）：ComponentLog 的节流是**全局共享**的
                // （FlushThrottleMs=200，ShouldFlushNow 不按组件区分），故计数取值窗口
                // 必须尽可能短且确定。此刻仍在 UI 线程、await 刚回来，别的 dispatcher
                // 回调插不进来 ⇒ 立即取值，窗口确定性地最小。
                // 原先把取值放在下面的 Task.Delay(50) 之后：那段等待的时长受慢机器 /
                // 磁盘卡顿 / 杀软扫描影响，会被拉长到 400ms+，从而跨过 2 次 200ms 边界，
                // 使 flushes 偶发变成 3 而假红（正常情况窗口 <200ms 时才是稳的）。
                var skips = _3FCompare.Diagnostics.ComponentLog.ThrottledSkipCount - skipsBefore;
                var flushes = _3FCompare.Diagnostics.ComponentLog.FlushCount - flushesBefore;

                await System.Threading.Tasks.Task.Delay(50);

                // 判据一：WM_SIZE 埋点确实走了节流路径（8 条里至多第一条落盘 ⇒ 至少 7 条被跳过）。
                // 用 7 而不是 8：突发里的**第一条**埋点距上次落盘往往已 >200ms，它本就该落盘。
                if (skips < 7)
                    throw new InvalidOperationException(
                        $"注入 8 条 WM_SIZE 后只有 {skips} 条埋点走了节流路径（应 ≥7）⇒ " +
                        "Resize 埋点仍在 UI 线程上逐条同步落盘（#24 回归：磁盘卡顿/杀软扫描会冻结界面）");
                // 判据二（更本质）：整段突发里落盘次数被压到 ≤2。
                // 允许 2 = 突发第一条 + 恰好落在窗口内的那一次 1s 心跳；而"Resize 埋点回到
                // Log（或去掉节流）"会让 8 条各自落盘、落盘次数跳到 9 以上，必然判红。
                // 注：取值窗口已前移到 Delay 之前以消除慢机器假红，故 RedrawRequest 埋点不在
                // 本判据的覆盖内（它由判据一的 skips 覆盖）——这是为确定性作的取舍。
                if (flushes > 2)
                    throw new InvalidOperationException(
                        $"注入 8 条 WM_SIZE 期间落盘 {flushes} 次（应 ≤2）⇒ 节流未生效，" +
                        "UI 线程上仍有逐条同步 I/O（#24 回归）");
                Log($"✅ Resize 埋点节流生效：8 条 WM_SIZE 埋点全部未同步落盘（跳过 {skips} 次落盘，" +
                    $"实际落盘 {flushes} 次，合并窗口 {_3FCompare.Diagnostics.ComponentLog.FlushThrottleMs}ms）");
            }

            // 悬浮传输栏（docs/31 阶段 4.3）：唯一依赖真机"owned 顶层窗 + 逐像素透明 +
            // WM_NCHITTEST 穿透"三件套的功能，只有跑在真实素材与真实合成器上才有意义。
            // 放在最大化/还原之后：此刻渲染管线已被证明能扛住窗口尺寸剧变，
            // 本步骤自己引起的两次布局高度变化（浮条进出常驻位）不至于被误判成管线回归。
            await AssertFloatingTransportAsync();

            _step = "媒体信息";
            var media = _sync.Slots[0].Session.ReadMediaInfo();
            if (media is not null)
            {
                // P4：显示用**真实帧率**，优先媒体信息里的 nominalFrameRate。
                // 为什么不能直接用 SyncController.EstimateFps(ready)：
                //   ① 它的 24.0 回退是**内部漂移校正**（半帧阈值、帧率差异判定）的既定语义，
                //      不能改（改它会破坏同步），但拿它做显示会把 60fps 素材谎报成 24fps；
                //   ② 快照的 FrameRate 取自引擎的媒体信息缓存（Fff3FpEngine.ReadSnapshot），
                //      缓存由 ReadMediaInfo() 预热——此处 ready 是**预热之前**读的，
                //      所以 snap.FrameRate==0（实测确认），进而回退到 24。
                // media.FrameRate 直接来自内核 JSON 的 streams[].nominalFrameRate（实测=60），
                // 权威且与缓存时序无关；EstimateFps 仅作兜底。
                var fps = media.FrameRate > 0 ? media.FrameRate : SyncController.EstimateFps(ready);
                Log($"媒体 {media.VideoWidth}x{media.VideoHeight} @{fps:0.##}fps {media.Codec} HDR={media.IsHdr}");
            }

            // C4：探针坐标映射。兜底分支的分母必须是"表面物理尺寸"——一旦退化成 physX 自己，
            // physX/physX ≡ 1，任何位置都被映射到右下角的越界像素（表现为探针恒读失败）。
            //
            // P0-5 修复（判据与分辨率耦合 + 静默跳过）：
            //   ① 旧判据的期望值 `(int)(videoWidth*0.25)` 与实现 `(int)(physX/surfaceW*videoWidth)`
            //      在 physX=surfaceW*0.25 时是**同一个表达式**（0.25 是 2 的幂，IEEE 下精确），
            //      偏差恒为 0，±2px 容差永远用不满；单点取样还挡不住"退化成常量"。
            //      改为 1/4 + 3/4 双点：1/4 点零容差，跨度 ±1 源像素（纯取整残差，与分辨率无关）。
            //   ② 旧判据比对量里的"0.5 源像素中心项"来自 Core/Backend/PixelReadback.cs:42
            //      `(srcX + 0.5) * DestWidth / videoWidth`。它在**缓冲像素**上的幅度是
            //      0.5 × DestWidth/videoWidth（随放大倍率线性增长）：320×180 放到 ~1920 宽 ≈ 3px，
            //      而容差固定 ±2px ⇒ 必红；4K 缩到 1920 宽 ≈ 0.25px ⇒ 稳过。
            //      修法见下方主路径：期望值改用同一套像素中心约定的**连续值**，残差只剩 floor
            //      的量化误差（< 1 缓冲像素），与素材分辨率、放大倍率均无关，且 1px 是能给出的最紧界。
            //   ③ 两处"跳过不判红"按"条件不成立是否说明真出问题了"分别处置，见下。
            _step = "探针映射";
            {
                var surface = Grid.GetSurface(0);
                if (surface is null || media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0)
                {
                    // 门禁约定：自测必须跑在 testmedia/media/real/ 的真实素材上，且窗口已完成布局。
                    // 因此该条件不成立 ≠ "本环境测不了"，而是**会话没打开 / 界面没起来** ⇒ 判红。
                    // （旧实现只打一句"⚠ 跳过"就继续，等于静默通过，是门禁恒绿的一个入口。）
                    throw new InvalidOperationException(
                        $"探针映射断言的前置条件不成立：表面={(surface is null ? "缺失" : "存在")}，" +
                        $"媒体={(media is null ? "缺失" : $"{media.VideoWidth}x{media.VideoHeight}")}" +
                        "（需要真实素材 + 已完成布局；旧实现此处静默跳过）");
                }
                else
                {
                    var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                    var sw = surface.Bounds.Width * scale;
                    var sh = surface.Bounds.Height * scale;
                    if (sw <= 0 || sh <= 0)
                        throw new InvalidOperationException("表面尚未布局（尺寸为 0），无法验证探针映射");

                    // 兜底映射 = 整面等比。取 1/4 与 3/4 两个归一化位置，钉住"线性 + 不退化成常量"。
                    // 1/4、3/4 都是 2 的幂分数 ⇒ physX/surfaceW 在 IEEE 下精确（0.25/0.75 乘除无舍入），
                    // 故 1/4 点用**零容差**（0 是能给出的最紧界）；跨度用 ±1 **源像素**——
                    // 那是 floor 取整的固有残差，与素材分辨率、表面尺寸都无关，320×180 与 4K 同样成立。
                    var q1 = _3FCompare.Core.Display.VideoPixelMap.MapFallback(
                        sw * 0.25, sh * 0.25, sw, sh, media.VideoWidth, media.VideoHeight);
                    var q3 = _3FCompare.Core.Display.VideoPixelMap.MapFallback(
                        sw * 0.75, sh * 0.75, sw, sh, media.VideoWidth, media.VideoHeight);
                    if (q1 is null || q3 is null)
                        throw new InvalidOperationException("探针兜底映射返回 null（表面/片源尺寸非法）");

                    var ex = media.VideoWidth / 4;      // ≡ (int)(videoWidth*0.25)，但显式表达"四分之一"
                    var ey = media.VideoHeight / 4;
                    if (q1.Value.X != ex || q1.Value.Y != ey)
                        throw new InvalidOperationException(
                            $"探针兜底映射 1/4 处 ({q1.Value.X},{q1.Value.Y}) ≠ 期望({ex},{ey})" +
                            "（C4 回归：分母又退化成 physX 自己了？）");

                    var spanX = q3.Value.X - q1.Value.X;
                    var spanY = q3.Value.Y - q1.Value.Y;
                    if (Math.Abs(spanX - media.VideoWidth / 2) > 1 || Math.Abs(spanY - media.VideoHeight / 2) > 1)
                        throw new InvalidOperationException(
                            $"探针兜底映射跨度异常：3/4−1/4 = ({spanX},{spanY})，期望≈({media.VideoWidth / 2},{media.VideoHeight / 2})" +
                            "（映射退化成常量，或缩放系数写错）");
                    Log($"探针兜底映射 1/4→({q1.Value.X},{q1.Value.Y}) 3/4→({q3.Value.X},{q3.Value.Y}) " +
                        $"跨度({spanX},{spanY}) 源 {media.VideoWidth}x{media.VideoHeight}");

                    // 主路径（有 RenderTargetInfo）。探针点取 **dest 矩形中心**，不取表面中心：
                    // zoom/pan 之后 dest 可能不居中，表面中心会落在黑边上（MapToSource 判 null），
                    // 旧实现据此"跳过"——把一次真实验证机会静默丢掉。dest 中心恒在视频内容区内。
                    var session0 = _sync.Slots[0].Session;
                    // ⚠ `out var` 若声明在 `&&` 的**右侧**，短路后编译器不认为它已赋值（CS0165），
                    // 故先无条件取值，再单独判 Dest/Swap 是否为空。
                    var hasRt = session0.ReadRenderTargetInfo(out var rtProbe);
                    if (hasRt && (rtProbe.DestWidth <= 0 || rtProbe.DestHeight <= 0 || rtProbe.SwapWidth == 0))
                        hasRt = false;
                    if (!hasRt && _realMode)
                        throw new InvalidOperationException(
                            "真实内核未上报 RenderTargetInfo（Dest/Swap 为空）：探针无法扣除 letterbox，" +
                            "C4 的坐标域修复完全依赖它（旧实现此处静默跳过 ⇒ 假通过）");
                    if (!hasRt)
                    {
                        // 演示模式（SimulatedEngine.ReadRenderTargetInfo 恒 false）是设计如此，不是缺陷，
                        // 但必须显式登记跳过原因并在收尾汇总里可见（演示模式本身已被 ExitSelfTest 判为不可通过）。
                        RecordSelfTestSkip("探针主路径映射未验证：演示模式无 RenderTargetInfo 接口");
                    }
                    else
                    {
                        var destCx = (rtProbe.DestX + rtProbe.DestWidth / 2.0) / scale;
                        var destCy = (rtProbe.DestY + rtProbe.DestHeight / 2.0) / scale;
                        var c = MapPointerToVideoPixel(surface, session0, new Point(destCx, destCy))
                            ?? throw new InvalidOperationException(
                                $"dest 矩形中心 ({rtProbe.DestX + rtProbe.DestWidth / 2},{rtProbe.DestY + rtProbe.DestHeight / 2}) " +
                                "映射为 null（该点必在视频内容区内，不该为 null）");
                        Log($"探针主路径映射 dest中心 → ({c.X},{c.Y}) 源 {media.VideoWidth}x{media.VideoHeight}");
                        if (c.X < 0 || c.X >= media.VideoWidth ||
                            c.Y < 0 || c.Y >= media.VideoHeight)
                            throw new InvalidOperationException($"探针主路径映射越界 ({c.X},{c.Y})");

                        // 坐标域回归（2026-09-16 实测新发现）：
                        // 内核 FFF3FP_ReadVideoPixel 的坐标域是**后台缓冲**，不是片源分辨率
                        // （4K 与 720p 片源下越界边界恒等于缓冲尺寸）。片源像素换算后必须落在
                        // destination 矩形内对应位置；若把片源坐标直传内核，会落到缓冲右下角读成黑。
                        //
                        // 期望值取"源像素**中心**在缓冲上的连续位置"，与 SourceToBackBuffer 内部
                        // `(srcX + 0.5) * DestWidth / videoWidth` 是同一套像素中心约定 ——
                        // 残差就只剩向下取整的量化误差（< 1 缓冲像素），与素材分辨率、放大倍率无关。
                        // 旧判据用无中心项的 `DestX + DestWidth/2` 配固定 ±2px：320×180 放到 ~1920 宽时
                        // 中心项折合 3 缓冲像素 ⇒ 必红；4K 缩到 1920 宽时只有 0.25px ⇒ 稳过（判据与素材耦合）。
                        var expXd = rtProbe.DestX + (c.X + 0.5) * rtProbe.DestWidth / media.VideoWidth;
                        var expYd = rtProbe.DestY + (c.Y + 0.5) * rtProbe.DestHeight / media.VideoHeight;
                        var bc = _3FCompare.Core.Backend.PixelReadback.SourceToBackBuffer(
                            c.X, c.Y, media.VideoWidth, media.VideoHeight, rtProbe);
                        if (bc is null)
                            throw new InvalidOperationException("源→缓冲坐标换算返回 null");
                        Log($"坐标域换算 源({c.X},{c.Y}) → 缓冲({bc.Value.X},{bc.Value.Y}) " +
                            $"期望≈({expXd:F2},{expYd:F2}) 缓冲 {rtProbe.SwapWidth}x{rtProbe.SwapHeight}");
                        // 容差 1 缓冲像素 = floor 量化的精确上界（残差 ∈ [0,1)），是能给出的最紧界：
                        // 任何 ≥1 缓冲像素的实现错误（含"片源坐标直传内核"）都会判红，4K 下同样有牙；
                        // 而 <1 像素的差异在 4K 下本就不可见，不判红不会漏掉真实缺陷。
                        if (Math.Abs(bc.Value.X - expXd) >= 1.0 || Math.Abs(bc.Value.Y - expYd) >= 1.0)
                            throw new InvalidOperationException(
                                $"探针读取坐标域错误：换算到 ({bc.Value.X},{bc.Value.Y})，期望≈({expXd:F2},{expYd:F2})" +
                                "（又把片源坐标直传内核了？偏差 ≥1 缓冲像素）");
                        if (bc.Value.X < 0 || bc.Value.X >= rtProbe.SwapWidth ||
                            bc.Value.Y < 0 || bc.Value.Y >= rtProbe.SwapHeight)
                            throw new InvalidOperationException(
                                $"探针读取坐标越界 ({bc.Value.X},{bc.Value.Y})，缓冲 {rtProbe.SwapWidth}x{rtProbe.SwapHeight}");
                    }
                }
            }

            // 放大镜 vs 探针 交叉验证（docs/15 §2.2 遗留的"未实测"项）
            // 两者是"取光标下像素"的两份实现。本轮 P0 缺陷正是放大镜坐标算错
            // 而探针一直是对的；只验证坐标公式不够（公式对了也可能传错参数），
            // 用**真实像素值**比对才能证明放大镜显示的就是光标下的内容。
            // 单路即可做，不依赖多路媒体，因此不受已知的多路崩溃阻塞。
            _step = "放大镜-探针一致性";
            if (_realMode)
            {
                var surface = Grid.GetSurface(0);
                var session = _sync.Slots.Count > 0 ? _sync.Slots[0].Session : null;
                if (surface is null || session is null)
                {
                    Log("⚠ 无表面/会话，跳过放大镜一致性断言");
                }
                else
                {
                    // ⚠ 必须**先暂停**再采样。两次读取（放大镜走区域回读、探针走单像素回读）
                    // 前后相隔几十毫秒，播放中画面已换帧 —— 实测出现过"放大镜读黑、探针读蓝"
                    // 的假失败，和"两边都读黑"的假绿（差 0 却什么也没证明）。
                    // 暂停 + 固定位置才能保证两边看的是同一帧。
                    _sync.Pause();
                    // docs/16 契约：子 HWND resize 后必须 Redraw 才会继续 flips，
                    // 区域回读读的是"已呈现帧"，不 Redraw 可能拿到 resize 前的陈旧/空缓冲。
                    _sync.RedrawAll();

                    var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                    _sync.SeekTo(TimeSpan.TicksPerSecond * 2);
                    await System.Threading.Tasks.Task.Delay(300);
                    _sync.RedrawAll();
                    await System.Threading.Tasks.Task.Delay(150);

                    // 3×3 网格（docs/15 §2.5 遗留项的单路解法）：只比对单点有"碰巧一致"的风险，
                    // 九宫格能同时覆盖水平/垂直方向的映射错误 —— 例如漏乘 RenderScaling
                    // 会随离原点越远偏得越多，单点可能仍落在同一色块里而漏判。
                    var candidates = new (string Name, Point Local)[]
                    {
                        ("1/4",  new Point(surface.Bounds.Width * 0.25, surface.Bounds.Height * 0.25)),
                        ("中上", new Point(surface.Bounds.Width * 0.50, surface.Bounds.Height * 0.25)),
                        ("3/4",  new Point(surface.Bounds.Width * 0.75, surface.Bounds.Height * 0.25)),
                        ("左中", new Point(surface.Bounds.Width * 0.25, surface.Bounds.Height * 0.50)),
                        ("正中", new Point(surface.Bounds.Width * 0.50, surface.Bounds.Height * 0.50)),
                        ("右中", new Point(surface.Bounds.Width * 0.75, surface.Bounds.Height * 0.50)),
                        ("左下", new Point(surface.Bounds.Width * 0.25, surface.Bounds.Height * 0.75)),
                        ("中下", new Point(surface.Bounds.Width * 0.50, surface.Bounds.Height * 0.75)),
                        ("右下", new Point(surface.Bounds.Width * 0.75, surface.Bounds.Height * 0.75)),
                    };

                    var compared = 0;
                    var worst = 0.0;
                    var worstAt = string.Empty;

                    // 失配时**先取证再抛**：把放大镜那一格周围的缓冲像素摊开，找探针的值落在哪一格。
                    // 落在别的格 ⇒ 两条路径只是"读了不同的格"（探针绕源域截断、放大镜直接取光标格，
                    //   比值 >1 时相位差可到 ~1 格，硬边界素材上就是两种颜色）；
                    // 就在中心格 ⇒ 放大镜读错了面/拿了过期格；
                    // 邻域里根本没有 ⇒ 两边看的不是同一代帧（这才是真正的取值链路缺陷）。
                    // 不做这一步就只能靠推测归因 —— 上一轮我把这条红记成"未解释"，就是这个债。
                    string DescribeSampleOffset(double probeR, double probeG, double probeB)
                    {
                        var (gridLeft, gridTop, cursorX, cursorY) = Magnifier.SampleWindowForSelfTest;
                        const int R = 2;                       // 中心 ±2 格
                        var span = 2 * R + 1;
                        if (gridLeft < 0 || gridTop < 0)
                            return $"｜取证不可得：放大镜没有可信采样窗口（grid=({gridLeft},{gridTop})）";
                        var cellX = gridLeft + MagnifierOverlay.ZoomGrid / 2;
                        var cellY = gridTop + MagnifierOverlay.ZoomGrid / 2;
                        var buf = new float[span * span * 4];
                        if (!session.TryReadPixelRegion(cellX - R, cellY - R, span, span, buf, out _))
                            return $"｜取证不可得：({cellX - R},{cellY - R}) 起的 {span}×{span} 回读失败";
                        var bestCol = -1; var bestRow = -1; var bestErr = double.MaxValue;
                        for (var row = 0; row < span; row++)
                            for (var col = 0; col < span; col++)
                            {
                                var i = (row * span + col) * 4;
                                var e = Math.Max(Math.Abs(buf[i] - probeR),
                                        Math.Max(Math.Abs(buf[i + 1] - probeG), Math.Abs(buf[i + 2] - probeB)));
                                if (e >= bestErr) continue;
                                bestErr = e; bestCol = col; bestRow = row;
                            }
                        var verdict = bestErr > 0.02 ? "邻域内没有探针这个值 ⇒ 两边不是同一代帧/同一个面（真缺陷）"
                            : bestCol == R && bestRow == R ? "探针的值就在放大镜那一格里 ⇒ 放大镜读错面或拿了过期格（真缺陷）"
                            : $"两路读了**不同的格**，格差 ({bestCol - R},{bestRow - R})";
                        return $"｜放大镜读缓冲格 ({cellX},{cellY})（它当时的光标物理像素=({cursorX},{cursorY})）；" +
                            $"探针的值落在这块 {span}×{span} 的相对格 ({bestCol - R},{bestRow - R})，残差 {bestErr:F4} ⇒ {verdict}";
                    }

                    foreach (var cand in candidates)
                    {
                        var m = MapPointerToVideoPixel(surface, session, cand.Local);
                        if (m is null) continue;
                        if (!session.TryReadPixelAtSource(m.Value.X, m.Value.Y, out var ps)) continue;
                        // 纯黑点不参与：读不出差异，只会稀释结果（本素材正中心实测纯黑）
                        if (Math.Max(ps.R, Math.Max(ps.G, ps.B)) <= 0.02) continue;

                        // ⚠ 必须跨过放大镜自身的采样节流（MagnifierOverlay.MinReadIntervalMs = 33ms）。
                        // 九宫格是连续同步采样的，若不等就调 UpdateAt，回读会被节流直接丢弃，
                        // 放大镜返回的仍是**上一点**的像素 ⇒ 与探针必然不一致，
                        // 表现为"第 2 个点开始全错"这种极具误导性的失败（实为测试写法的锅，
                        // 不是坐标公式错）。间隔取 40ms 留出余量。
                        await System.Threading.Tasks.Task.Delay(40);
                        Magnifier.UpdateAt(surface, cand.Local, scale);
                        if (!Magnifier.TryGetCenterSample(out var mr, out var mg, out var mb))
                            throw new InvalidOperationException(
                                $"放大镜在 {cand.Name} 无采样：探针有值而放大镜拿不到像素（采样路径已失效）");

                        var d = Math.Max(Math.Abs(mr - ps.R),
                                 Math.Max(Math.Abs(mg - ps.G), Math.Abs(mb - ps.B)));
                        compared++;
                        if (compared == 1 || d > worst) { worst = d; worstAt = cand.Name; }
                        if (d > 0.02)
                            throw new InvalidOperationException(
                                $"放大镜与探针在 {cand.Name} 不一致（差 {d:F4}）：" +
                                $"放大镜=({mr:F3},{mg:F3},{mb:F3}) 探针=({ps.R:F3},{ps.G:F3},{ps.B:F3})" +
                                DescribeSampleOffset(ps.R, ps.G, ps.B) +
                                "（§2.2 那一族缺陷的形状是「中心格等值却报不一致」或「邻域里根本没有」；" +
                                "取证说「格差 (dx,dy)」则属源域截断与缓冲格号的相位差，见 #13 的口径）");
                    }

                    if (compared == 0)
                    {
                        // 一个有效点都没有 ⇒ 断言会恒绿，必须诚实跳过而不是"通过"
                        Log("⚠ 九宫格全部纯黑/回读失败，本环境无法判别，跳过一致性断言");
                    }
                    else
                    {
                        Log($"✅ 九宫格一致性通过：{compared}/9 个有效采样点全部一致" +
                            $"（最大差 {worst:F4} @ {worstAt}，RenderScaling={scale}）");
                    }
                    _sync.Play();
                }
            }

            // #22 回归：放大镜浮窗的**摆放坐标**必须是"光标在容器里的坐标"，
            // 而不是 surface 局部坐标（后者只有第 1 格恰好正确）。
            // 放在九宫格一致性之后：那时采样链路已被证明是对的，本步只管位置。
            _step = "放大镜浮窗坐标";
            await AssertMagnifierFollowsCursorAsync(1, "单路");

            // ══════════ 空格暂停 → 继续：必须真的恢复出帧（2026-09-26 新增）══════════
            // 这条路径此前**零覆盖**——全仓没有任何测试调用过 `TogglePlay()`（空格键与底栏按钮共用它），
            // 所以"暂停后再按空格不播了"能一路活到用户手上。
            // ⚠ 判据不能只看 State：交换链停摆时 State 照样是 Playing、`pos` 照常推进，
            //   只有 `PresentedVideoFrames` 不再增长（契约见 `SyncController.RedrawAll` 的注释）。
            //   所以这里必须断言"恢复后 presented 真的在涨"，只断言 state 回到 Playing 是没有牙的。
            _step = "空格暂停-继续必须恢复出帧";
            {
                AssertTransportTipsAreTruthful();
                var s0 = _sync.ReadMasterSnapshot();
                if (s0 is null)
                    throw new InvalidOperationException("读不到主路快照 ⇒ 本步前提不成立");
                if (s0.State != PlayerState.Playing)
                {
                    try { _sync.Play(); } catch { }
                    await System.Threading.Tasks.Task.Delay(300);
                    s0 = _sync.ReadMasterSnapshot();
                }
                if (s0 is not { State: PlayerState.Playing })
                    throw new InvalidOperationException(
                        $"前置失败：无法把主路置于 Playing（当前 {s0?.State.ToString() ?? "<null>"}）");

                RaiseKeyOn(FocusedRouteSource!, global::Avalonia.Input.Key.Space);   // 第一次空格：暂停
                var paused = false;
                var deadlinePause = System.DateTime.UtcNow.AddMilliseconds(2000);
                while (System.DateTime.UtcNow < deadlinePause)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                    if (_sync.ReadMasterSnapshot() is { State: PlayerState.Paused }) { paused = true; break; }
                }
                if (!paused)
                    throw new InvalidOperationException(
                        $"第一次空格后未进入 Paused（当前 {_sync.ReadMasterSnapshot()?.State}）" +
                        $"⇒ 空格键的暂停分支没生效，后面的【继续】无从谈起");

                var presAtPause = _sync.ReadMasterSnapshot()?.PresentedVideoFrames ?? -1;

                RaiseKeyOn(FocusedRouteSource!, global::Avalonia.Input.Key.Space);   // 第二次空格：继续
                var resumed = false;
                long presNow = presAtPause;
                var deadlineResume = System.DateTime.UtcNow.AddMilliseconds(2500);
                while (System.DateTime.UtcNow < deadlineResume)
                {
                    await System.Threading.Tasks.Task.Delay(120);
                    var sn = _sync.ReadMasterSnapshot();
                    presNow = sn?.PresentedVideoFrames ?? -1;
                    if (sn is { State: PlayerState.Playing } && presNow > presAtPause + 4) { resumed = true; break; }
                }
                var after = _sync.ReadMasterSnapshot();
                Log($"空格暂停→继续：暂停时 presented={presAtPause} → 恢复后 presented={presNow} " +
                    $"state={after?.State} pos={TimeSpan.FromTicks(after?.Position100ns ?? 0):g}");
                // ⚠ 只记录、不改判决：恢复成功之后再连续观测 12 秒。本步原来只在 2.5 秒窗口里判
                //   【恢复出帧】，若停摆是『恢复后又撑几秒才卡住』，那个窗口看不见——不能拿
                //   『没看见』当『没有』。判据与阈值一字未动。
                {
                    long prevWatch = presNow; int stallAt = -1;
                    var samples = new System.Collections.Generic.List<string>();
                    for (var t = 0; t < 24; t++)
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        var sn = _sync.ReadMasterSnapshot();
                        var cur = sn?.PresentedVideoFrames ?? -1;
                        samples.Add($"{t * 0.5:F1}s:{cur - prevWatch}");
                        if (stallAt < 0 && sn is { State: PlayerState.Playing } && cur <= prevWatch) stallAt = t;
                        if (cur > prevWatch) prevWatch = cur;
                    }
                    var fin = _sync.ReadMasterSnapshot();
                    var joined = string.Join(" ", samples);
                    var firstStall = stallAt < 0 ? "从未" : (stallAt * 0.5).ToString("F1") + "s";
                    Log($"[观测] 恢复后 12 秒、每 0.5 秒的 presented 增量：{joined}");
                    Log($"[观测] 首次【播着但不涨帧】= {firstStall} 最终 state={fin?.State} presented={fin?.PresentedVideoFrames}" +
                        $" pos={TimeSpan.FromTicks(fin?.Position100ns ?? 0):g}");
                }
                if (!resumed)
                    throw new InvalidOperationException(
                        $"第二次空格后未恢复出帧：state={after?.State} presented 暂停时={presAtPause} 现在={presNow}" +
                        $"（要求 >+4）⇒ 交换链停摆：`TogglePlay()` 的恢复分支缺 `RedrawAll()`" +
                        $"（契约见 SyncController.RedrawAll）");
            }

            // 视图变换压力测试：模拟用户快速滚动缩放，检测是否导致视频卡死
            _step = "视图变换压力测试";
            {
                // 先 Seek 到视频开头，确保有足够的播放时长
                _sync.SeekTo(0);
                await System.Threading.Tasks.Task.Delay(300);
                // 启用循环防止短视频播完
                var dur = _sync.GetMasterDuration100ns();
                _sync.LoopEnabled = true;
                _sync.LoopStart100ns = 0;
                _sync.LoopEnd100ns = dur;
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                // 确认播放中 + 读取呈现计数器基线
                var beforePos = _sync.GetMasterPosition100ns();
                var beforeSnap = _sync.ReadMasterSnapshot();
                var beforePresented = beforeSnap?.PresentedVideoFrames ?? -1;
                var beforeSwapPresents = beforeSnap?.SwapChainPresents ?? -1;
                var beforeState = beforeSnap?.State;
                Log($"变换前: pos={TimeSpan.FromTicks(beforePos):g} state={beforeState} presented={beforePresented} swap={beforeSwapPresents}");

                // 模拟用户快速滚动 20 次（每次间隔 50ms，模拟快速滚轮）
                for (var i = 0; i < 20; i++)
                {
                    var z = 1f + (i % 5) * 0.5f; // 1.0 → 3.0 循环
                    // 组件日志：自测扇出直接调 _sync.SetViewTransform，绕开了 SendViewTransform，
                    // 故这里单独补一条（这是"放大 → 内核资源重建"的高风险路径）。
                    _3FCompare.Diagnostics.ComponentLog.Log(
                        _3FCompare.Diagnostics.Comp.Render, "SetViewTransform", -1,
                        $"src=selftestFanout z={z:0.###}");
                    _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                    await System.Threading.Tasks.Task.Delay(50);
                }

                // 等待 2 秒让引擎处理完排队的变换
                await System.Threading.Tasks.Task.Delay(2000);

                // 检查视频是否仍在播放 + 呈现计数器是否继续增长
                var midSnap = _sync.ReadMasterSnapshot();
                var midState = midSnap?.State;
                var midPresented = midSnap?.PresentedVideoFrames ?? -1;
                var afterTransforms = _sync.GetMasterPosition100ns();

                await System.Threading.Tasks.Task.Delay(1000);
                var finalSnap = _sync.ReadMasterSnapshot();
                var finalPresented = finalSnap?.PresentedVideoFrames ?? -1;
                var finalCheck = _sync.GetMasterPosition100ns();

                var presentDelta = finalPresented - midPresented;
                Log($"变换中: state={midState} presented={midPresented} (+{midPresented - beforePresented})");
                Log($"1秒后: pos={TimeSpan.FromTicks(finalCheck):g} presented={finalPresented} (Δpresented/秒={presentDelta})");
                Log($"位置增量(1秒) = {(finalCheck - afterTransforms) / 10000}ms");

                if (finalCheck <= afterTransforms && midState == PlayerState.Playing)
                {
                    Log($"❌ 视频已卡死！presented 停在 {finalPresented}");
                    throw new InvalidOperationException(
                        $"视图变换导致视频卡死: presented Δ={presentDelta}");
                }
                else if (presentDelta <= 0 && midState == PlayerState.Playing)
                {
                    Log($"❌ 渲染管线停滞！presented 计数不再增长");
                    throw new InvalidOperationException(
                        $"渲染管线停滞: presented Δ={presentDelta}, pos Δ={(finalCheck - afterTransforms) / 10000}ms");
                }
                else if (midState == PlayerState.Failed)
                {
                    // docs/41 #14：原先 Failed 与 Paused 挤在同一个"非卡死"分支里，只打一行 ⚠
                    // 就继续往下走 ⇒ 渲染管线已经死了，末尾照样 exit=0。
                    Log($"❌ 变换压力后引擎处于 Failed：presented Δ={presentDelta}");
                    throw new InvalidOperationException("视图变换导致引擎进入 Failed（渲染管线已死）");
                }
                else if (midState == PlayerState.Ended)
                {
                    Log($"❌ 变换压力后播放已结束（Ended），位置不再推进：pos={TimeSpan.FromTicks(finalCheck):g}");
                    throw new InvalidOperationException("视图变换后播放意外结束，无法判定是否卡死");
                }
                else if (midState != PlayerState.Playing)
                {
                    Log($"⚠ 播放状态变为 {midState}（非 Failed/Ended，不判红）");
                }
                else
                {
                    Log($"✅ 压力测试通过：20 次快速变换后视频继续播放 (presented +{presentDelta}/秒)");
                }

                // 恢复正常视图并继续播放
                _sync.SetViewTransform(1.0f, 0f, 0f);
                _sync.Play();
            }

            // 单路放大平移的**真实量程**实测（2026-09-26 用户报"左右拖不动、上下方向反了"）。
            // 方向那件事已由 ViewPan 的负号钉死（离线单测 + 变异验牙），这里量的是另一件事：
            // 内核 destination 原点若无符号，平移那段代码的 max(0,·) 会把"往右下露出更多源画面"
            // 那一半方向整段夹死 ⇒ 托管侧的 ±1 只是**声称值**。这条把内核真正上报的原点扫出来。
            // 判据有两条：① pan 增大时原点必须单调不增（内核语义变了就先红，别让 ViewPan 的负号失配）；
            // ② 扫描跨度必须等于拟合盒尺寸（内核退回夹负原点的写法时跨度恒为 0 ⇒ 判红，
            //    这正是 2026-09-26 实测到的形状：pan 扫全程、上报原点恒 (0,0)）。
            _step = "单路平移量程";
            if (_realMode && _sync.Slots.FirstOrDefault()?.Session is { } panSession)
            {
                const float panZoom = 2.0f;
                var sweep = new System.Collections.Generic.List<(float Pan, int X, int Y)>();
                var expectedSpanX = 0;
                var expectedSpanY = 0;
                var haveFitBox = false;

                // 等内核真出过帧：变换是 relaxed 原子写、下一帧才生效，所以再等两帧，
                // 避免读到"已经排队但还没带上新变换"的那一帧。
                // ⚠ 绝不能改成"等读数变了再读"——原点被夹死时它恒不变，而那正是本条要量的东西。
                async System.Threading.Tasks.Task WaitForPresentedAsync(int timeoutMs)
                {
                    var deadline = System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(timeoutMs);
                    var target = (_sync.ReadMasterSnapshot()?.PresentedVideoFrames ?? -1) + 2;
                    while (System.DateTime.UtcNow < deadline &&
                           (_sync.ReadMasterSnapshot()?.PresentedVideoFrames ?? -1) < target)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        await System.Threading.Tasks.Task.Delay(20);
                    }
                }

                // 先量 z=1 的拟合盒 —— 它同时给出"量程应该是多少"的**独立参照**：
                // z=2 时放大盒 = 2×拟合盒，"仍盖住拟合盒"的原点合法区间是 [x0 − w, x0]，
                // 全长恰等于拟合盒宽 w。pan 钳到 ±1 正好铺满两端，所以扫描跨度必须 == w。
                // （拿被测实现自己算期望值会构造恒等；这里用的是另一拍的几何。）
                _sync.SetViewTransform(1.0f, 0f, 0f);
                await WaitForPresentedAsync(1500);
                if (!panSession.ReadRenderTargetInfo(out var fit))
                {
                    RecordSelfTestSkip("单路平移量程未验证：读不到 z=1 的拟合盒");
                }
                else
                {
                    expectedSpanX = (int)fit.DestWidth;
                    expectedSpanY = (int)fit.DestHeight;
                    haveFitBox = true;
                    foreach (var pan in new[] { -1f, -0.5f, 0f, 0.5f, 1f })
                    {
                        _sync.SetViewTransform(panZoom, pan, pan);
                        await WaitForPresentedAsync(1500);
                        if (panSession.ReadRenderTargetInfo(out var rtPan))
                            sweep.Add((pan, rtPan.DestX, rtPan.DestY));
                    }
                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                }

                if (sweep.Count == 0)
                {
                    RecordSelfTestSkip("单路平移量程未验证：内核未上报 RenderTargetInfo");
                }
                else
                {
                    var liveX = sweep.Max(s => s.X) - sweep.Min(s => s.X);
                    var liveY = sweep.Max(s => s.Y) - sweep.Min(s => s.Y);
                    Log($"   z={panZoom:0.#} 扫描 " + string.Join(" ",
                        sweep.Select(s => $"pan={s.Pan:0.0}→dest=({s.X},{s.Y})")) +
                        $" ⇒ 原点可动区间 横 {liveX}px / 纵 {liveY}px（期望 = 拟合盒 " +
                        $"{fit.DestWidth}x{fit.DestHeight}）");

                    // 仪表自证 ①：pan 增大时原点必须**单调不增**（内核是"视口位置"语义）。
                    // 哪天它变成同向，ViewPan 里那个负号就错了 —— 这条先红，
                    // 而不是等用户再报一次"方向反了"。
                    for (var i = 1; i < sweep.Count; i++)
                        if (sweep[i].X > sweep[i - 1].X || sweep[i].Y > sweep[i - 1].Y)
                            throw new InvalidOperationException(
                                $"单路平移方向变了：pan {sweep[i - 1].Pan:0.0}→{sweep[i].Pan:0.0} 时原点 " +
                                $"({sweep[i - 1].X},{sweep[i - 1].Y})→({sweep[i].X},{sweep[i].Y}) 反而增大 ⇒ " +
                                "内核已不是【视口位置】语义，ViewPan 的负号要跟着改");

                    // 仪表自证 ②：量程必须真的放开了。内核退回夹负原点的写法时跨度恒为 0
                    // （2026-09-26 实测就是这个形状），所以这条判红即"拖不动"复现。
                    // 容差 2px：原点逐帧取整，两端各差 1px。没读到拟合盒时不判（上面已记 SKIP）。
                    if (haveFitBox && (Math.Abs(liveX - expectedSpanX) > 2 || Math.Abs(liveY - expectedSpanY) > 2))
                        throw new InvalidOperationException(
                            $"单路平移量程不符：实测 横 {liveX}px / 纵 {liveY}px，" +
                            $"期望 {expectedSpanX}x{expectedSpanY}（=拟合盒尺寸）⇒ " +
                            "画盒原点被夹住（内核 VideoDestination 原点若无符号、平移段用 max(0,·)，" +
                            "左/上半段量程就会整段消失）。见 docs/48 §八");
                }

                // ── 跟手增益实测（2026-09-29 用户报"缩小了拖动变慢、放大了拖动变快、像素钉不住光标"）──
                // 上面那条只证明**量程放开了**；这条量的是另一件事：走完整生产换算（ViewPan.Accumulate）
                // 得到的 pan，落到内核以后，"画面在屏幕上走过的像素"是否 == "光标走过的像素"，
                // 而且必须在**多个倍率**下都成立 —— 旧换算漏掉 (z−1) 归一化，恰好在 z=2 侥幸成立，
                // 单点测量永远发现不了（z=2 是用户最常停的那一档）。
                // 期望值不来自被测公式：拿内核自己上报的 DestX/DestY 当参照尺。
                _step = "单路跟手";
                if (haveFitBox)
                {
                    // 拖动量取拟合盒的 5%：小到在任何测试倍率下都不会顶到 ±1 端点
                    // （z=1.15 时量程只有 0.075×拟合盒宽），大到让逐帧取整的 1px 误差不占比。
                    const double dragFraction = 0.05;
                    var dragX = (double)fit.DestWidth * dragFraction;
                    var dragY = (double)fit.DestHeight * dragFraction;
                    var worstErr = 0.0;
                    var measured = 0;

                    foreach (var z in new[] { 1.15f, 2f, 4f, 8f })
                    {
                        _sync.SetViewTransform(z, 0f, 0f);
                        await WaitForPresentedAsync(1500);
                        if (!panSession.ReadRenderTargetInfo(out var before)) continue;

                        // 量程基准与生产路径同源：由内核上报的（变换后）Dest 反推拟合盒
                        var (fitW, fitH) = _3FCompare.Platform.ViewPan.FitBoxFromDest(
                            before.DestWidth, before.DestHeight, z);
                        var (panX, panY) = _3FCompare.Platform.ViewPan.Accumulate(
                            0f, 0f, dragX, dragY, fitW, fitH, z);

                        _sync.SetViewTransform(z, panX, panY);
                        await WaitForPresentedAsync(1500);
                        if (!panSession.ReadRenderTargetInfo(out var after)) continue;
                        measured++;

                        // 画盒左沿右移 == 画面向右走 == 光标向右拖 ⇒ 同号
                        var movedX = (double)(after.DestX - before.DestX);
                        var movedY = (double)(after.DestY - before.DestY);
                        var err = Math.Max(Math.Abs(movedX - dragX), Math.Abs(movedY - dragY));
                        worstErr = Math.Max(worstErr, err);
                        Log($"   z={z:0.##}：拖 ({dragX:0.0},{dragY:0.0})px ⇒ pan=({panX:0.###},{panY:0.###}) ⇒ " +
                            $"内核原点 ({before.DestX},{before.DestY})→({after.DestX},{after.DestY}) " +
                            $"= 画面走 ({movedX:0.0},{movedY:0.0})px");
                    }

                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);

                    // fail-closed：一次都没读到就不判绿（守卫空转 == 没有这条门禁）
                    if (measured == 0)
                        RecordSelfTestSkip("单路跟手未验证：内核四个倍率下都没上报 RenderTargetInfo");
                    // 容差 2px：原点逐帧 lround，两端各差 1px。
                    // 旧律（分母无 (z−1)）在这一条下的误差量级：z=1.15 差 13 倍、z=8 差 7 倍 ⇒ 必红。
                    else if (worstErr > 2.0)
                        throw new InvalidOperationException(
                            $"单路放大不跟手：跨 z=1.15/2/4/8（{measured} 个点）最大偏差 {worstErr:0.##}px（判据 ≤2px）。" +
                            "pan 增益必须按 (z−1) 归一化并且分轴用被拖那一路的拟合盒，" +
                            "否则小倍率拖得慢、大倍率冲过头（2026-09-29 用户报的那两条症状）");
                }

                // ── 光标锚点闭环（同上，2026-09-29 用户报"像素和光标固定不上"）──
                // 上面那条量的是拖动，这条量的是滚轮改倍率时"光标下那一点会不会跑"。
                // ⚠ 期望值只用**内核两次上报的 dest 盒**算（屏幕位置 = DestX + u·DestWidth，
                // u 由第一次读数定义），不掺被测公式 —— 拿 AnchorPan 自己算期望就是构造恒等。
                // 注：自测的滚轮注入步没有光标落在画面上（走的是"可见区中心"退化支），
                // 所以锚点在这里必须单独成一步，不能拿那条冒充已验。
                _step = "单路锚点";
                if (haveFitBox)
                {
                    // "光标"是一个**绝对后台缓冲坐标**（未放大画面 3/4 处），不随读数漂；
                    // 各倍率下的 u 由内核当次上报的盒子反推，因此不等于 0.75 的恒等假测量。
                    var cursorX = (double)fit.DestX + 0.75 * fit.DestWidth;
                    var cursorY = (double)fit.DestY + 0.75 * fit.DestHeight;
                    var worstAnchor = 0.0;
                    var anchorPoints = 0;
                    var clamped = 0;

                    // 跳变对只取"锚点通常可达"的那些：大幅缩小时量程塌缩，光标偏一边可能物理无解，
                    // 那种跳变会顶到 ±1 端点 —— 端点跳过要计数打印，不能藏进"通过"里。
                    foreach (var (z0, z1) in new[] { (1.0f, 2.0f), (2.0f, 4.0f), (4.0f, 8.0f),
                                                     (8.0f, 4.0f), (4.0f, 2.0f) })
                    {
                        _sync.SetViewTransform(z0, 0f, 0f);
                        await WaitForPresentedAsync(1500);
                        if (!panSession.ReadRenderTargetInfo(out var src0)) continue;

                        var u = (cursorX - src0.DestX) / (double)src0.DestWidth;
                        var v = (cursorY - src0.DestY) / (double)src0.DestHeight;
                        if (u is < 0.0 or > 1.0 || v is < 0.0 or > 1.0) continue; // 当次不在画面里

                        var panX = _3FCompare.Platform.ViewPan.AnchorPan(0f, u, z0, z1);
                        var panY = _3FCompare.Platform.ViewPan.AnchorPan(0f, v, z0, z1);
                        if (Math.Abs(panX) > 0.999f || Math.Abs(panY) > 0.999f)
                        {
                            clamped++;
                            Log($"   {z0:0.##}→{z1:0.##}：锚点需 pan=({panX:0.###},{panY:0.###}) 已顶到端点 ⇒ 跳过精确比对");
                            continue;
                        }

                        _sync.SetViewTransform(z1, panX, panY);
                        await WaitForPresentedAsync(1500);
                        if (!panSession.ReadRenderTargetInfo(out var src1)) continue;
                        anchorPoints++;

                        // 同一个源点在缩放后的屏幕位置（只用内核上报的盒子算，不看被测公式）
                        var afterX = src1.DestX + u * (double)src1.DestWidth;
                        var afterY = src1.DestY + v * (double)src1.DestHeight;
                        var err = Math.Max(Math.Abs(afterX - cursorX), Math.Abs(afterY - cursorY));
                        worstAnchor = Math.Max(worstAnchor, err);
                        Log($"   {z0:0.##}→{z1:0.##}：光标 ({cursorX:0.0},{cursorY:0.0}) u=({u:0.###},{v:0.###}) " +
                            $"⇒ pan=({panX:0.###},{panY:0.###}) ⇒ 该点现在 ({afterX:0.0},{afterY:0.0}) 偏差 {err:0.##}px");
                    }

                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);

                    // fail-closed：可达跳变一个都没测到就不判绿（端点跳过不等于通过）
                    if (anchorPoints < 1)
                        RecordSelfTestSkip($"单路锚点未验证：可达跳变 0 个（顶到端点跳过 {clamped} 个）");
                    // 容差 2px：内核画盒原点逐帧 lround，两端各差 1px。
                    else if (worstAnchor > 2.0)
                        throw new InvalidOperationException(
                            $"滚轮缩放不钉光标：{anchorPoints} 个可达跳变里最大偏差 {worstAnchor:0.##}px（判据 ≤2px，" +
                            $"另有 {clamped} 个因量程塌缩跳过）⇒ 改倍率时没按光标下那点补偿 pan，" +
                            "画面仍围绕拟合盒中心放缩（2026-09-29 用户报的'像素和光标固定不上'）");
                    else
                        Log($"   ✅ 单路锚点通过：{anchorPoints} 个跳变最大偏差 {worstAnchor:0.##}px（跳过 {clamped} 个塌缩）");
                }

                // ── 判据：zoom < 1 现在必须真的缩小（绝对比值域的下半段）──
                // 这一条原来是"只观测不判红"的：2026-09-29 实机量到下发 0.6/0.05 时内核 Dest 恒等
                // 拟合盒 ⇒ 记下"真 1:1 与『比适配更小』必须走内核入口，托管侧改不出来"。入口已落地
                // （3fp/kernel 的视口变换段放开到 |z−1|>1e-4，量程取绝对值、原点区间由 min/max 生成），
                // 所以这里从观测升成判据：盒子按 z 等比缩、且平移在黑边里真的走得动。
                // ⚠ 拿 old 内核跑这条会红，那是正确的：那份内核确实没有这个能力。
                _step = "缩放下限";
                if (haveFitBox)
                {
                    const float below = 0.6f;
                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                    var okFit = panSession.ReadRenderTargetInfo(out var atFit);
                    _sync.SetViewTransform(below, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                    var okBelow = panSession.ReadRenderTargetInfo(out var atBelow);
                    // 平移在黑边里走得动吗：pan=+1 应贴拟合盒左沿，pan=−1 应贴右沿。
                    _sync.SetViewTransform(below, 1f, 0f);
                    await WaitForPresentedAsync(1500);
                    var okLeft = panSession.ReadRenderTargetInfo(out var atLeft);
                    _sync.SetViewTransform(below, -1f, 0f);
                    await WaitForPresentedAsync(1500);
                    var okRight = panSession.ReadRenderTargetInfo(out var atRight);
                    _sync.SetViewTransform(0.05f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                    var okMin = panSession.ReadRenderTargetInfo(out var atMin);
                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);

                    if (!(okFit && okBelow && okLeft && okRight && okMin))
                        RecordSelfTestSkip("缩放下限未验证：内核有一次没上报 RenderTargetInfo");
                    else
                    {
                        var wantW = atFit.DestWidth * below;
                        var wantH = atFit.DestHeight * below;
                        var travelWant = atFit.DestWidth - wantW;   // 盒子可走的整段区间宽
                        if (Math.Abs(atBelow.DestWidth - wantW) > 2 || Math.Abs(atBelow.DestHeight - wantH) > 2)
                            throw new InvalidOperationException(
                                $"zoom={below} 没有真的缩小：Dest {atBelow.DestWidth}×{atBelow.DestHeight}，" +
                                $"期望 ≈{wantW:0}×{wantH:0}（z=1 时 {atFit.DestWidth}×{atFit.DestHeight}）" +
                                " ⇒ 内核仍把 z≤1 当 no-op（需要含『|z−1|>1e-4 才跳过』那段的 API 16 内核）");
                        if (Math.Abs(atLeft.DestX - atFit.DestX) > 2 ||
                            Math.Abs(atRight.DestX - (atFit.DestX + travelWant)) > 2)
                            throw new InvalidOperationException(
                                $"缩小态的平移量程不对：pan=+1 原点 {atLeft.DestX}（期望贴左 {atFit.DestX}）、" +
                                $"pan=−1 原点 {atRight.DestX}（期望贴右 {atFit.DestX + travelWant:0}）" +
                                " ⇒ 量程没取绝对值（max(·,0) 把 z<1 吞成 0），或原点区间在 z<1 时反向");
                        if (atMin.DestWidth < 1 || atMin.DestWidth >= atFit.DestWidth)
                            throw new InvalidOperationException(
                                $"zoom=0.05 的盒子尺寸不合理：{atMin.DestWidth}×{atMin.DestHeight}" +
                                $"（拟合盒 {atFit.DestWidth}×{atFit.DestHeight}，下限应 ≥1px 且严格小于拟合盒）");
                        Log($"   ✅ 缩放下限通过：z={below} ⇒ Dest {atBelow.DestWidth}×{atBelow.DestHeight}" +
                            $"（≈拟合盒 ×{below}）；黑边内平移全程 {travelWant:0}px，pan ±1 各贴一端；" +
                            $"z=0.05 ⇒ {atMin.DestWidth}×{atMin.DestHeight}（仍有下限兜底）");
                    }
                }

                // ── 判据：新内核入口把倍率域从"相对适配盒"换成绝对 屏幕:视频 比（#12）──
                // 上游语义下 zoom 乘的是"适配到窗口之后的视频盒"，于是同一个 ×2 在不同窗口
                // 尺寸下对应的真实放大率不同 ⇒ 倍率数字与像素对不上（2026-09-29 用户报的那条）。
                // 封顶打开后适配盒不超过源原生尺寸（VideoRenderer 走 CalculateVideoDestination
                // 已有的原生分支），恒等式变成 DestWidth/VideoWidth == zoom，且 zoom=1 是逐像素 1:1。
                // ⚠ 窗口比源小的时候封顶物理上不生效（内核条件就是 sourceW ≤ outputW），两态同形
                //   ⇒ 量不出差别只能 SKIP，绝不记成通过。4K 片源 + 1995 宽窗口正是这种情况。
                _step = "真1:1封顶";
                if (panSession.ReadMediaInfo() is not { VideoWidth: > 0, VideoHeight: > 0 } capMedia
                    || !panSession.ReadRenderTargetInfo(out var capOff))
                {
                    RecordSelfTestSkip("真 1:1 封顶未验证：读不到媒体尺寸或 RenderTargetInfo");
                }
                else if (capOff.SwapWidth < capMedia.VideoWidth || capOff.SwapHeight < capMedia.VideoHeight)
                {
                    RecordSelfTestSkip($"真 1:1 封顶未验证：后台缓冲装不下源原生尺寸" +
                        $"（缓冲 {capOff.SwapWidth}×{capOff.SwapHeight} < 源 {capMedia.VideoWidth}×{capMedia.VideoHeight}" +
                        " ⇒ 封顶前后同形，换更小片源或更大窗口才量得到）");
                }
                else
                {
                    panSession.SetFitLimitToNative(true);
                    _sync.SetViewTransform(1f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                    var oneOk = panSession.ReadRenderTargetInfo(out var atOne);
                    _sync.SetViewTransform(2f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                    var twoOk = panSession.ReadRenderTargetInfo(out var atTwo);
                    // 复位：后续判据都在上游语义（不封顶）下跑，不能让这条改动污染别人的场地
                    panSession.SetFitLimitToNative(false);
                    _sync.SetViewTransform(1f, 0f, 0f);
                    await WaitForPresentedAsync(1500);

                    if (!oneOk || !twoOk)
                        RecordSelfTestSkip("真 1:1 封顶未验证：封顶态下内核不再上报 RenderTargetInfo");
                    else
                    {
                        // 期望值用另一路算术（源尺寸 × 目标倍率），不拿被测实现自己算。
                        var oneErrX = Math.Abs((double)atOne.DestWidth - capMedia.VideoWidth);
                        var oneErrY = Math.Abs((double)atOne.DestHeight - capMedia.VideoHeight);
                        var twoRatio = (double)atTwo.DestWidth / capMedia.VideoWidth;
                        if (oneErrX > 1 || oneErrY > 1)
                            throw new InvalidOperationException(
                                $"封顶后 zoom=1 不是逐像素 1:1：Dest {atOne.DestWidth}×{atOne.DestHeight}，" +
                                $"源 {capMedia.VideoWidth}×{capMedia.VideoHeight}（宽差 {oneErrX:0.#}px，高差 {oneErrY:0.#}px）" +
                                " ⇒ SetFitLimitToNative 没接到 DrawCachedVideo 的适配盒参数上");
                        if (Math.Abs(twoRatio - 2.0) > 0.01)
                            throw new InvalidOperationException(
                                $"封顶后倍率不等于绝对 屏幕:视频 比：zoom=2 时 Dest 宽 {atTwo.DestWidth}，" +
                                $"比值 {twoRatio:0.###}（判据 |比−2|≤0.01）" +
                                " ⇒ zoom 仍在乘「相对适配盒」，适配盒没被封顶到源原生尺寸");
                        Log($"   ✅ 真 1:1 封顶通过：zoom=1 ⇒ Dest {atOne.DestWidth}×{atOne.DestHeight}" +
                            $"（源 {capMedia.VideoWidth}×{capMedia.VideoHeight}，比值 1.000）；" +
                            $"zoom=2 ⇒ 比值 {twoRatio:0.###} ⇒ 倍率数字就是 屏幕px/视频px");
                    }
                }

                // ── 观测：放大镜"按源像素取格"在实机比值下可不可得（#13 的决策数据）──
                // 全 app 只有后台缓冲这一条回读（TryReadPixel / TryReadPixelRegion，放大镜、差异面板、
                // 探针都走它），**没有解码帧导出** ⇒ "某个源像素的真值"只有在 屏幕px/视频px ≥ 1 时
                // 才物理存在于缓冲里；比值 < 1 时它已经被 GPU 混掉了，换任何取格方式都还原不回来。
                // 这里把比值、一个源像素占几个屏幕像素、取 12×12 源格要读多大一块、单次回读耗时量出来。
                // ⚠ 只记录、不判红：测的是能力边界与成本，不是对错。
                _step = "放大镜取格预算";
                if (haveFitBox && panSession.ReadMediaInfo() is { VideoWidth: > 0, VideoHeight: > 0 } benchMedia)
                {
                    const int Grid = MagnifierOverlay.ZoomGrid;   // 用真常量，不再抄一遍数字
                    foreach (var z in new[] { 1f, 2f, 4f })
                    {
                        _sync.SetViewTransform(z, 0f, 0f);
                        await WaitForPresentedAsync(1500);
                        if (!panSession.ReadRenderTargetInfo(out var rtZ)) continue;

                        var rx = (double)rtZ.DestWidth / benchMedia.VideoWidth;
                        var ry = (double)rtZ.DestHeight / benchMedia.VideoHeight;
                        var readW = Math.Max(1, (int)Math.Ceiling(Grid * rx));
                        var readH = Math.Max(1, (int)Math.Ceiling(Grid * ry));
                        var startX = Math.Max(0, rtZ.DestX);
                        var startY = Math.Max(0, rtZ.DestY);
                        var buffer = new float[readW * readH * 4];

                        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var okRead = panSession.TryReadPixelRegion(
                            startX, startY, readW, readH, buffer, out var benchDepth);
                        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0
                            / System.Diagnostics.Stopwatch.Frequency;

                        var verdict = rx >= 1.0
                            ? $"可得（按 {Math.Round(rx):0}px 步进取样，1 源像素 = {rx:F2} 屏幕px）"
                            : $"**不可得**（1 源像素只占 {rx:F2} 屏幕px，已被重采样混掉 ⇒ 要内核导出解码帧）";
                        Log($"   [观测] z={z:0.#} 比值=({rx:F3},{ry:F3}) 片源 {benchMedia.VideoWidth}×{benchMedia.VideoHeight} " +
                            $"Dest {rtZ.DestWidth}×{rtZ.DestHeight} ⇒ 取 {Grid}×{Grid} 源格需读 {readW}×{readH} 缓冲像素，" +
                            $"一次回读 {us / 1000.0:F2}ms{(okRead ? "" : "（回读失败）")} 位深={benchDepth} ⇒ {verdict}");
                    }
                    _sync.SetViewTransform(1.0f, 0f, 0f);
                    await WaitForPresentedAsync(1500);
                }
                else
                    RecordSelfTestSkip("放大镜取格预算未验证：读不到片源尺寸或拟合盒");
                _sync.SetViewTransform(1.0f, 0f, 0f);
            }

            // ── 视图变换跨会话重放（真机侧）── 离线侧已复现→修→变异回红（Core 用例
            // AddSlot_AfterSetViewTransform_NewSessionReceivesSameTransform），这里补真机读数。
            // 度量用各路 `DestWidth/SwapWidth`（与窗口大小无关，两路尺寸不同也能比），且**自带对照**：
            // z=1→2 时该比值必须逐路翻倍 —— 不翻倍就说明这把尺不随内核变换动，后面的断言全是空的。
            // 追加一路走生产 OpenPaths（同一份素材，不依赖第二个文件）：新那路必须与老路同比值，
            // 没有重放时它停在"铺满窗口"= 老路的一半。读不到 rt 就把各路 可见性/尺寸/状态/呈现代数
            // 一起打出来再记 SKIP —— 上一版只留一句"读不到"，害得下一轮重新查一遍为什么。
            _step = "视图变换跨会话重放";
            if (_realMode && _sync.Count >= 1)
            {
                double[] LaneRatios()
                {
                    var list = new System.Collections.Generic.List<double>();
                    foreach (var slot in _sync.Slots)
                        if (slot.Session.ReadRenderTargetInfo(out var rt) && rt.SwapWidth > 0)
                            list.Add((double)rt.DestWidth / rt.SwapWidth);
                    return list.ToArray();
                }

                string LaneDiag() => string.Join(" ", _sync.Slots.Select((s, i) =>
                {
                    var sv = Grid.GetSurface(i);
                    var dest = s.Session.ReadRenderTargetInfo(out var r) ? r.DestWidth.ToString() : "noRt";
                    var sn = s.Session.ReadSnapshot();
                    return $"#{i}={dest}/{sn?.State}/p{sn?.PresentedVideoFrames ?? -1}" +
                        $"/vis{(sv?.IsVisible ?? false)} {sv?.Bounds.Width ?? 0:F0}x{sv?.Bounds.Height ?? 0:F0}";
                }));

                async System.Threading.Tasks.Task SettleZoomAsync(float z)
                {
                    _viewZoom = z; _viewPanX = 0f; _viewPanY = 0f;
                    var before = _sync.ReadMasterSnapshot()?.PresentedVideoFrames ?? -1;
                    ApplyViewTransform();
                    var dl = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                    while (DateTime.UtcNow < dl &&
                           (_sync.ReadMasterSnapshot()?.PresentedVideoFrames ?? -1) < before + 2)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        await System.Threading.Tasks.Task.Delay(30);
                    }
                }

                var countAtStart = _sync.Count;
                _sync.Play(); SetPlaying(true);
                await SettleZoomAsync(1f);
                var fit = LaneRatios();
                await SettleZoomAsync(2f);
                var magn = LaneRatios();

                if (fit.Length < countAtStart)
                    RecordSelfTestSkip($"重放未验证：z=1 时只有 {fit.Length}/{countAtStart} 路报 rt（{LaneDiag()}）");
                else if (magn.Length != fit.Length)
                    RecordSelfTestSkip($"重放未验证：z=2 时能读到 rt 的路数变了（{magn.Length}≠{fit.Length}，{LaneDiag()}）");
                else
                {
                    for (var i = 0; i < fit.Length; i++)
                        if (Math.Abs(magn[i] - fit[i] * 2) > 0.05 * fit[i] + 0.02)
                            throw new InvalidOperationException(
                                $"重放判据的度量不成立：第{i}路 z=1→2 比值 {fit[i]:F3}→{magn[i]:F3}，没有翻倍");

                    // ⚠ 轮询里**绝不能再广播一次变换**：广播会把新那路补到 z=2，正好盖掉我要测的
                    // 缺陷（AddSlot 不重放）—— 首版就是这么假绿过的。只等它自己出帧、自己报 rt。
                    var replayDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
                    OpenPaths(new System.Collections.Generic.List<string> { videoPath });
                    _sync.Play(); SetPlaying(true);
                    var after = Array.Empty<double>();
                    while (DateTime.UtcNow < replayDeadline)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        await System.Threading.Tasks.Task.Delay(200);
                        after = LaneRatios();
                        if (after.Length >= countAtStart + 1) break;
                    }

                    if (after.Length < countAtStart + 1)
                        RecordSelfTestSkip(
                            $"重放未验证：追加一路后只有 {after.Length}/{countAtStart + 1} 路报 rt（{LaneDiag()}）");
                    else
                    {
                        var reference = after[0];
                        for (var i = 1; i < after.Length; i++)
                            if (Math.Abs(after[i] - reference) > 0.05 * reference + 0.02)
                                throw new InvalidOperationException(
                                    $"视图变换没有跨会话重放：z=2 时各路比值=({string.Join(", ", after.Select(x => x.ToString("F3")))})，" +
                                    "新加那一路停在铺满窗口（约为老路的一半）⇒ 多路看到的区域不一致，而屏幕上没有提示");
                        Log($"   z=1 ({string.Join(", ", fit.Select(x => x.ToString("F3")))}) → z=2 " +
                            $"({string.Join(", ", after.Select(x => x.ToString("F3")))})，{after.Length} 路一致 ✓");
                    }
                }
                // 收尾：把这条判据临时加的一路撤掉。本机 3 路以上"播着不涨帧/崩溃"是既有问题，
                // 而门禁还会在后面那步拖入第二个素材 ⇒ 不还原就是让后面的步骤多背一路 4K，
                // 平白多一分假红来源（也顺手覆盖 RemoveSlotAt 的真实撤路路径）。
                if (_sync.Count > countAtStart)
                {
                    _sync.RemoveSlotAt(_sync.Count - 1);
                    Grid.SetCount(Math.Max(1, _sync.Count), _realMode);
                    Log($"   [收尾] 已撤掉判据临时加的那一路 ⇒ 回到判据前的 {countAtStart} 路");
                }
                ResetViewTransform();
            }

            // C2：倍速切换不得跳位。伪变速的基准点必须在切换瞬间复位，且单次推进量有 2s 上限；
            // 否则"整个已播放时长"会被当成 1s 的增量一次性 Seek 走（30s 处切 2× 直接跳到 60s）。
            _step = "倍速切换";
            if (_realMode)
            {
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(1200);
                var p1 = _sync.GetMasterPosition100ns();
                if (!_transport.SetSpeed(2.0))
                    throw new InvalidOperationException("倍速档位 2.0 未命中（传输栏档位表已变动？）");
                await System.Threading.Tasks.Task.Delay(400);
                var p2 = _sync.GetMasterPosition100ns();
                Log($"倍速切换 1× {p1 / 10_000}ms → 2× {p2 / 10_000}ms（Δ={(p2 - p1) / 10_000}ms）");
                // 切换瞬间只允许正常播放推进（≤1.5s），不允许整段前跳或回退
                if (p2 - p1 > TimeSpan.TicksPerMillisecond * 1500 || p2 < p1 - TimeSpan.TicksPerMillisecond * 100)
                    throw new InvalidOperationException(
                        $"倍速切换跳位 {p1 / 10_000}ms → {p2 / 10_000}ms（Δ={(p2 - p1) / 10_000}ms，C2 回归）");
                _transport.SetSpeed(1.0); // 复位，避免影响后续步骤
                await System.Threading.Tasks.Task.Delay(200);
            }

            // ══════════ 嵌入式 UI 消息注入测试（真实走 WndProc → SurfaceWheel/SurfaceFilesDropped 分支） ══════════
            _step = "UI消息注入-滚轮缩放";
            {
                // 取得第 0 路真实子 HWND（NativeControlHost 的 D3D 输出窗口）
                var surface = Grid.GetSurface(0);
                var targetHwnd = surface?.Hwnd ?? nint.Zero;
                if (targetHwnd == nint.Zero)
                    throw new InvalidOperationException("无法取得 PlayerSurface 子 HWND");

                // 基线：初始 zoom 应为 1（上一压力测试已复位）
                var zoomBefore = _viewZoom;
                Log($"基线 zoom={zoomBefore:F3} hwnd=0x{targetHwnd:X}");

                // 注入 3 次 WM_MOUSEWHEEL（向前，delta=+120），走 PlayerSurface.SubclassedWndProc
                // 的 WM_MOUSEWHEEL → SurfaceWheel → OnSurfaceWheel → _viewZoom *= 1.15^3
                const int WM_MOUSEWHEEL = 0x020A;
                short delta = 120;
                for (var i = 0; i < 3; i++)
                {
                    var wParam = (nint)((long)(ushort)delta << 16);
                    PostMessageW(targetHwnd, WM_MOUSEWHEEL, wParam, nint.Zero);
                    await System.Threading.Tasks.Task.Delay(80);
                }
                // 等 UI 线程处理完 PostMessage
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await System.Threading.Tasks.Task.Delay(300);

                var zoomAfter = _viewZoom;
                var expected = zoomBefore * 1.15f * 1.15f * 1.15f;
                Log($"注入后 zoom={zoomAfter:F3} (期望≈{expected:F3})");
                if (Math.Abs(zoomAfter - expected) > 0.01f)
                    throw new InvalidOperationException(
                        $"滚轮缩放未生效：zoom {zoomBefore:F3} → {zoomAfter:F3}，期望 {expected:F3}");

                // 再注入向后滚动（缩小），确认双向都走通
                for (var i = 0; i < 3; i++)
                {
                    var wParam = unchecked((nint)((long)(ushort)(-120) << 16));
                    PostMessageW(targetHwnd, WM_MOUSEWHEEL, wParam, nint.Zero);
                    await System.Threading.Tasks.Task.Delay(80);
                }
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await System.Threading.Tasks.Task.Delay(300);
                Log($"缩小后 zoom={_viewZoom:F3} (期望≈{zoomBefore:F3})");
                if (Math.Abs(_viewZoom - zoomBefore) > 0.01f)
                    throw new InvalidOperationException(
                        $"滚轮缩小未复位：zoom={_viewZoom:F3}，期望 {zoomBefore:F3}");
            }

            // ══════════ 滚轮缩放节流补发（§2.1 回归） ══════════
            // ApplyViewTransform 的 16ms 节流是「直接丢弃」而非「合并补发」。滚轮事件
            // 停止后不会再有调用 ⇒ 被丢弃的那一次如果是最终值，就永久丢失且不自愈：
            // 画面停在旧缩放级别、与状态栏显示不符。触控板惯性滚动（间隔 8~16ms）极易命中。
            _step = "滚轮缩放-节流补发";
            {
                // 前置："至少有一次更新被节流丢弃"。只靠自然连发坐实并不确定——UI 线程被抢走时
                // 整串连发自己能跨过 16ms 窗口（2026-09-26 实测 32 轮里 1 次 dropped=0，那一轮
                // 连发耗时 90.30ms），那是**前置不成立**而不是节流失效，直接判红就是假红。
                // 兜底改成确定性前置：把节流时钟 _lastPanApplyTicks 伪造成"刚刚下发过一次"，
                // 紧接着的那次调用必然落在窗口内 —— 留给单次同步调用的余量是整个 16ms，
                // 而不是要求整串 4 次都挤进 16ms。于是判据不再依赖时序运气：
                // 伪装窗口内仍一次不丢 ⇒ 只能是节流失效 ⇒ 判红。不存在"跳过本步"的第三种结局。
                // ⚠ 这条兜底是被变异逼出来的：摘掉节流后每次调用都做完整下发，连发自己被拖慢到
                //   19~48ms ⇒ 自然连发永远"跨过窗口"，只重试会把它判成 SKIP（假绿）。
                const int BurstCalls = 4;
                const int MaxNaturalAttempts = 3;
                const int MaxStampedAttempts = 3;
                float zoomBefore = 0f;
                long dropped = 0;
                // 计时用 Stopwatch：DateTime.UtcNow 在 Windows 上粒度约 15.6ms，量 16ms 窗口不可信。
                var sw = System.Diagnostics.Stopwatch.StartNew();

                void Burst()
                {
                    zoomBefore = _viewZoom;
                    var droppedBefore = TransformDroppedCount;
                    sw.Restart();
                    for (var i = 0; i < BurstCalls; i++) OnSurfaceWheel(120);
                    sw.Stop();
                    dropped = TransformDroppedCount - droppedBefore;
                }

                void StampedBurst()
                {
                    zoomBefore = _viewZoom;
                    var droppedBefore = TransformDroppedCount;
                    sw.Restart();
                    for (var i = 0; i < BurstCalls; i++)
                    {
                        // 每次调用前重新盖戳 ⇒ 每个"单次调用↔盖戳"的间隔都被钉在窗口内，
                        // 整串耗时多久都不影响前置成立。（DispatcherTimer 与这里同线程，
                        // 中间没有消息泵，补发定时器插不进来。）
                        _lastPanApplyTicks = Environment.TickCount64;
                        OnSurfaceWheel(120);
                    }
                    sw.Stop();
                    dropped = TransformDroppedCount - droppedBefore;
                }

                // ① 自然连发：贴近真实用户节奏（触控板惯性滚动 8~16ms 一次），先按老口径试。
                //    预热：首调走冷路径（JIT / _transformFlushTimer 首次 Start / 首次日志落盘），
                //    ApplyViewTransform 在此只是"把当前未变的变换再下发一次"，不改状态语义。
                ApplyViewTransform();
                for (var attempt = 1; attempt <= MaxNaturalAttempts && dropped < 1; attempt++)
                {
                    await System.Threading.Tasks.Task.Delay(25);   // > 16ms 窗口：本次首调不被算作丢弃
                    Burst();
                    Log($"连发 {BurstCalls} 次〔自然 {attempt}/{MaxNaturalAttempts}〕：_viewZoom={_viewZoom:F3} " +
                        $"已下发={LastSentZoom:F3} 丢弃={dropped} 连发耗时={sw.Elapsed.TotalMilliseconds:F2}ms");
                }
                // ② 确定性前置兜底：只在自然连发没命中窗口时才走，命中过就一次都不跑。
                for (var attempt = 1; attempt <= MaxStampedAttempts && dropped < 1; attempt++)
                {
                    StampedBurst();
                    Log($"连发 {BurstCalls} 次〔伪装刚下发过 {attempt}/{MaxStampedAttempts}〕：_viewZoom={_viewZoom:F3} " +
                        $"已下发={LastSentZoom:F3} 丢弃={dropped} 连发耗时={sw.Elapsed.TotalMilliseconds:F2}ms");
                }
                if (dropped < 1)
                    throw new InvalidOperationException(
                        $"节流用例空转：{MaxNaturalAttempts} 次自然连发 + {MaxStampedAttempts} 次【伪装成刚刚下发过】的连发" +
                        "都没有一次被丢弃 ⇒ 节流本身失效（前置坐实不了 ⇒ 下面的补发断言恒绿、毫无意义）");

                // 等一次性补发定时器（16ms）在 UI 线程跑完
                await System.Threading.Tasks.Task.Delay(250);

                var expected = zoomBefore * 1.15f * 1.15f * 1.15f * 1.15f;
                Log($"补发后：_viewZoom={_viewZoom:F3} 已下发={LastSentZoom:F3}（期望≈{expected:F3}）");
                if (Math.Abs(_viewZoom - expected) > 0.01f)
                    throw new InvalidOperationException(
                        $"连发缩放未全部计入：_viewZoom={_viewZoom:F3}，期望 {expected:F3}");
                // 核心断言：UI 的最终值必须真的到了内核，而不是被节流吞掉
                if (Math.Abs(LastSentZoom - _viewZoom) > 0.001f ||
                    Math.Abs(LastSentPanX - _viewPanX) > 0.001f ||
                    Math.Abs(LastSentPanY - _viewPanY) > 0.001f)
                    throw new InvalidOperationException(
                        $"最终变换被节流吞掉：内核收到 zoom={LastSentZoom:F3}/pan=({LastSentPanX:F3},{LastSentPanY:F3})，" +
                        $"UI 为 zoom={_viewZoom:F3}/pan=({_viewPanX:F3},{_viewPanY:F3})（§2.1 回归）");
                Log("✅ 节流补发通过：被丢弃的最终值已补发到内核");

                // 复位，避免影响后续步骤
                ResetViewTransform();
                await System.Threading.Tasks.Task.Delay(100);
                if (Math.Abs(LastSentZoom - 1f) > 0.001f)
                    throw new InvalidOperationException(
                        $"重置视图也被节流吞掉：内核收到 zoom={LastSentZoom:F3}，期望 1.000（§2.1 回归）");
            }

            _step = "UI消息注入-文件拖入";
            if (dropVideoPath is not null && File.Exists(dropVideoPath))
            {
                var surface = Grid.GetSurface(0);
                var targetHwnd = surface?.Hwnd ?? nint.Zero;
                if (targetHwnd == nint.Zero)
                    throw new InvalidOperationException("无法取得 PlayerSurface 子 HWND");

                var countBefore = _sync.Count;
                Log($"拖入前路数={countBefore}");

                // 构造真实 HDROP 并 PostMessage WM_DROPFILES 到子 HWND（走 HandleDropFiles → SurfaceFilesDropped → OpenPaths）
                var hDrop = BuildHDrop(dropVideoPath);
                if (hDrop == nint.Zero)
                    throw new InvalidOperationException("HDROP 构造失败");
                PostMessageW(targetHwnd, WM_DROPFILES, hDrop, nint.Zero);

                // 等待拖入文件被解析、打开并加入 _sync
                var dropDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < dropDeadline && _sync.Count <= countBefore)
                    await System.Threading.Tasks.Task.Delay(200);

                if (_sync.Count <= countBefore)
                    throw new InvalidOperationException(
                        $"文件拖入未生效：路数仍为 {_sync.Count}（期望 > {countBefore}）");
                Log($"拖入后路数={_sync.Count} ✓ 拖入文件已打开");
                // 注意：HandleDropFiles 内部已 DragFinish(hDrop) 释放内存，这里不再重复释放
            }
            else
            {
                Log("未提供第二个视频，跳过文件拖入测试");
            }

            // ══════════ 故障注入自测：异常边界（docs/41 W2 的 #4 / #5） ══════════
            // **默认关闭**：只有 FC_SELFTEST_FAULT_INJECT=1 才执行。不设该变量时本段
            // 一行输出都不打印、连 _step 都不碰，因此既有门禁脚本（grep
            // `selftest\[|全部通过`、`magnifybench: (完成|放大后|.*SKIPPED)`）完全不受影响。
            if (Environment.GetEnvironmentVariable("FC_SELFTEST_FAULT_INJECT") == "1")
            {
                var stepBeforeInject = _step;
                var injectDone = false;
                try { await RunFaultInjectionAsync(videoPath); injectDone = true; }
                // 只在成功时还原 _step：失败时保留"异常注入"，让上面的 catch 打出
                // `selftest[步骤异常注入]: 失败 ✗ …`，而不是误指到上一个步骤。
                finally { if (injectDone) _step = stepBeforeInject; }
            }

            FlushSelfTestSkips();
            Log("全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest[步骤{_step}]: 失败 ✗ {ex.Message}");
            Console.Error.Flush();
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    // ══════════ 故障注入自测（默认关闭；docs/41 W2 #4 / #5） ══════════

    /// <summary>异常边界故障注入自测。**默认关闭**，仅 <c>FC_SELFTEST_FAULT_INJECT=1</c> 时执行。
    ///
    /// <para><b>为什么需要它</b>：docs/41 W2 修了两条 🔴，但异常路径在本仓库没有任何链路能
    /// 稳定触发，当时"修复有效"只是代码论证。本方法把该路径<b>主动注入</b>出来：临时给表面事件
    /// 挂一个必抛异常的订阅者，再复用上面"文件拖入"用例同款的 HDROP 注入链路
    /// （<see cref="BuildHDrop"/> + 投递 <c>WM_DROPFILES</c>）真实走一遍窗口过程。</para>
    ///
    /// <para><b>判据（能区分修复前后）</b>：
    /// <list type="number">
    /// <item><b>进程存活</b>（最核心）：异常若从 reverse P/Invoke 边界逃逸，进程会<b>直接终止</b>，
    /// 后面的代码一行都不会执行。所以"注入完还活着"本身就是硬信号。</item>
    /// <item><b>DragFinish 确被调用</b>（#4）：<c>DragFinish</c> 释放 HDROP 后，
    /// <c>GlobalFlags(hDrop)</c> 会变成 GMEM_INVALID_HANDLE(0x8000)。这是<b>直接判据</b>，
    /// 不是间接判据：修复前 <c>DragFinish</c> 排在 <c>Invoke</c> 之后，订阅者一抛就永远走不到
    /// ⇒ 句柄仍然有效。探针本身先跑一次正对照（自己 alloc → 自己 DragFinish → 必须报无效），
    /// 否则用例会因探针失灵而"假绿"。</item>
    /// <item><b>消息循环仍正常</b>：注入后重新投递一条滚轮消息（此时无故障订阅者），
    /// zoom 必须照常变化 —— 证明子 HWND 的窗口过程没被异常打坏。</item>
    /// </list></para>
    ///
    /// <para><b>为什么临时摘掉真实订阅者</b>：真实订阅者（<see cref="OnSurfaceFilesDropped"/>）
    /// 会真的去 OpenPaths 建会话；摘掉后故障订阅者是唯一订阅者，对应"<b>第一个</b>订阅者就抛"
    /// 这一最贴近 #4 描述的形态（修复前它会让 DragFinish 不可达），且不污染路数/会话状态。
    /// 订阅关系在 <c>finally</c> 里无条件还原。</para></summary>
    private async System.Threading.Tasks.Task RunFaultInjectionAsync(string videoPath)
    {
        _step = "异常注入";
        Log("FC_SELFTEST_FAULT_INJECT=1 已启用（默认关闭，仅自测用；正常门禁不设此变量）");

        var surface = Grid.GetSurface(0);
        var targetHwnd = surface?.Hwnd ?? nint.Zero;
        if (surface is null || targetHwnd == nint.Zero)
            throw new InvalidOperationException("无法取得 PlayerSurface 子 HWND");

        // ---- 探针正对照：先证明 GlobalFlags 真能看出 DragFinish 的释放 ----
        // 没有这一步，判据②可能在"探针本身就失灵"的情况下恒绿（假绿），等于没验证。
        var probeControl = BuildHDrop(videoPath);
        if (probeControl == nint.Zero)
            throw new InvalidOperationException("HDROP 构造失败（探针正对照）");
        var flagsAlloc = GlobalFlags(probeControl);
        DragFinish(probeControl);
        var flagsFreed = GlobalFlags(probeControl);
        Log($"探针正对照：GlobalAlloc 后=0x{flagsAlloc:X4} → DragFinish 后=0x{flagsFreed:X4}");
        // 判据只认"是否变成无效句柄"：实测本机 GlobalAlloc(GHND) 的 GlobalFlags 低字节是 0x0000
        // （现代 Windows 的全局内存已是堆支撑，不再回报 GMEM_MOVEABLE），所以不能断言等于 0x0002，
        // 只能断言"有效 ≠ 0x8000，释放后 == 0x8000"。
        if (flagsAlloc == GMEM_INVALID_HANDLE || flagsFreed != GMEM_INVALID_HANDLE)
            throw new InvalidOperationException(
                $"DragFinish/GlobalFlags 探针不可用（期望 有效≠0x{GMEM_INVALID_HANDLE:X4} → 0x{GMEM_INVALID_HANDLE:X4}，" +
                $"实际 0x{flagsAlloc:X4} → 0x{flagsFreed:X4}）：#4 判据不成立，拒绝假绿");

        // ---- #4 注入：SurfaceFilesDropped 的唯一订阅者必抛 ----
        var hDrop = BuildHDrop(videoPath);
        if (hDrop == nint.Zero)
            throw new InvalidOperationException("HDROP 构造失败（#4 注入）");

        // ⚠ 判据特异性（docs/41 复核必修）：只断言 GlobalFlags 会**必然假绿**。
        // 若 HDROP 没被解析出来（DragQueryFileW 返回 0 ⇒ count==0 ⇒ Invoke 整段被 if 跳过），
        // 订阅者压根不会被调用，而 finally 里的 DragFinish 照常执行 ⇒
        // 判据①（存活）与判据②（已释放）双双全绿，#4 的修复一点都没被验证。
        // 因此必须显式断言"故障订阅者确实被调用了恰好 1 次"——这是判据②成立的前提。
        var faultHitDrop = 0;
        // 模拟"真实订阅者 OnSurfaceFilesDropped → OpenPaths 抛异常"（docs/41 #4 的场景）。
        Action<System.Collections.Generic.IReadOnlyList<string>> faultyDrop = _ =>
        {
            System.Threading.Interlocked.Increment(ref faultHitDrop);
            throw new InvalidOperationException("故障注入：#4 订阅者故意抛异常（docs/41）");
        };

        surface.SurfaceFilesDropped -= OnSurfaceFilesDropped;
        surface.SurfaceFilesDropped += faultyDrop;
        try
        {
            // 用 SendMessageW 而不是 PostMessageW：同线程下它直接同步调用窗口过程，
            // 返回时 HandleDropFiles（含 finally 里的 DragFinish）**一定已执行完**，
            // 于是下面的 GlobalFlags 探测不存在"消息还没被处理"的竞态。
            SendMessageW(targetHwnd, WM_DROPFILES, hDrop, nint.Zero);
        }
        finally
        {
            surface.SurfaceFilesDropped -= faultyDrop;
            surface.SurfaceFilesDropped += OnSurfaceFilesDropped;
        }

        // 走到这里 = 异常没有逃出反向 P/Invoke 边界（判据①）。若 #4/#5 的兜底被摘掉，
        // 进程在上一行就已经终止，根本执行不到这里。
        const int expectDropHits = 1;
        var hitDrop = System.Threading.Volatile.Read(ref faultHitDrop);
        if (hitDrop != expectDropHits)
            throw new InvalidOperationException(
                $"#4 注入无效：故障订阅者被调用 {hitDrop} 次（期望 {expectDropHits}）——WM_DROPFILES 已投递但 " +
                "HDROP 未解析出文件（DragQueryFileW 返回 0）或 HandleDropFiles 未走到派发，" +
                "判据①/② 会无条件成立 ⇒ 拒绝在假绿前提下放行");
        var flagsDrop = GlobalFlags(hDrop);
        Log($"#4 注入后：订阅者已抛异常、进程存活；GlobalFlags(hDrop)=0x{flagsDrop:X4}" +
            $"（DragFinish 已释放 ⇒ 期望 0x{GMEM_INVALID_HANDLE:X4}）");
        if (flagsDrop != GMEM_INVALID_HANDLE)
            throw new InvalidOperationException(
                $"#4 回归：订阅者抛异常后 DragFinish 未执行（HDROP 未释放，GlobalFlags=0x{flagsDrop:X4}，" +
                $"期望 0x{GMEM_INVALID_HANDLE:X4}）—— docs/41 #4 的 finally 失效");

        // ---- #5 注入：SurfaceWheel 的唯一订阅者必抛 ----
        // 该事件在 WndProc 栈上同步触发，产品代码里**没有任何内层 try**，
        // 唯一的防线就是 SubclassedWndProc 的异常边界（docs/41 #5）。
        var zoomBefore = _viewZoom;
        // 与 #4 同理：必须证明订阅者真的被调到过，否则"进程存活"这条判据在任何情况下都成立。
        var faultHitWheel = 0;
        Action<short> faultyWheel = _ =>
        {
            System.Threading.Interlocked.Increment(ref faultHitWheel);
            throw new InvalidOperationException("故障注入：#5 订阅者故意抛异常（docs/41）");
        };

        surface.SurfaceWheel -= OnSurfaceWheel;
        surface.SurfaceWheel += faultyWheel;
        try
        {
            SendMessageW(targetHwnd, WM_MOUSEWHEEL, (nint)(120L << 16), nint.Zero);
        }
        finally
        {
            surface.SurfaceWheel -= faultyWheel;
            surface.SurfaceWheel += OnSurfaceWheel;
        }
        const int expectWheelHits = 1;
        var hitWheel = System.Threading.Volatile.Read(ref faultHitWheel);
        if (hitWheel != expectWheelHits)
            throw new InvalidOperationException(
                $"#5 注入无效：故障订阅者被调用 {hitWheel} 次（期望 {expectWheelHits}）——WM_MOUSEWHEEL 未派发到 " +
                "SurfaceWheel，判据不成立 ⇒ 拒绝在假绿前提下放行");
        Log($"#5 注入后：订阅者已抛异常、进程存活（该次滚轮未缩放：zoom={_viewZoom:F3}）");

        // ---- 判据③：消息循环仍正常 ----
        SendMessageW(targetHwnd, WM_MOUSEWHEEL, (nint)(120L << 16), nint.Zero);
        await System.Threading.Tasks.Task.Delay(80);
        Log($"注入后消息循环探针：zoom {zoomBefore:F3} → {_viewZoom:F3}（期望≈{zoomBefore * 1.15f:F3}）");
        if (Math.Abs(_viewZoom - zoomBefore * 1.15f) > 0.01f)
            throw new InvalidOperationException(
                $"异常注入后消息循环异常：滚轮未生效（zoom={_viewZoom:F3}，期望≈{zoomBefore * 1.15f:F3}）");
        ResetViewTransform();
        await System.Threading.Tasks.Task.Delay(100);

        Log("✅ 订阅者抛异常后进程存活且消息循环正常 ✓");
    }

    // ══════════ 多路同步压力测试（--multitest <video> [routes=4] [durationSec=30]） ══════════
    // 目的：验证 0009 视口扩容 zoom 在多路并行扇出下的稳定性——
    // 1) N 路打开同一视频，长时播放各路线程压力均衡
    // 2) 播放中并行 SetViewTransform（走 SyncController Parallel.For 扇出）无卡死
    // 3) 多路 position 漂移 ≤ 阈值（打开时统一 Pause 对齐 + 周期校正后的残余漂移）
    // 4) 全路 presented 持续增长（无单路解码/呈现停滞）
    private async System.Threading.Tasks.Task RunMultitestAsync(string videoPath, int routes, int durationSec)
    {
        routes = Math.Clamp(routes, 2, 9);
        durationSec = Math.Clamp(durationSec, 10, 120);
        StartDeadlineWatchdog("multitest", durationSec * 4 + 180);   // docs/41 #16

        // 判别实验（设备/交换链数量 vs 并发 Present 线程数）：FC_MULTITEST_ACTIVE = 实际播放的路数。
        // 默认 = 全部（既有行为完全不变）。设成 < routes 时，仍然打开全部 routes 路
        //（D3D device + swapchain 数量不变），只把 index >= ACTIVE 的路 Pause 掉，
        // 使 A/B 两臂的唯一差异只剩"同时在 Present 的线程数"。
        var activeRoutes = routes;
        var activeEnv = Environment.GetEnvironmentVariable("FC_MULTITEST_ACTIVE");
        if (!string.IsNullOrWhiteSpace(activeEnv))
        {
            if (int.TryParse(activeEnv, out var parsedActive))
                activeRoutes = Math.Clamp(parsedActive, 1, routes);
            else
                Console.Error.WriteLine($"multitest: FC_MULTITEST_ACTIVE=\"{activeEnv}\" 无法解析，按全部路播放");
        }
        _multitestActiveRoutes = activeRoutes;

        Console.WriteLine($"multitest: 多路={routes} 时长={durationSec}s 素材={videoPath}");
        Console.WriteLine(activeRoutes < routes
            ? $"multitest: 判别实验模式 FC_MULTITEST_ACTIVE={activeRoutes} → 打开 {routes} 路（设备/交换链不变），仅前 {activeRoutes} 路播放，其余 {routes - activeRoutes} 路 Pause"
            : $"multitest: 全部 {routes} 路播放（未设 FC_MULTITEST_ACTIVE）");

        // 崩溃现场追查：多路崩溃始终落在"复位后 presented 增长"与"帧步进"之间，
        // 而该区间会命中 SetViewTransform → 内核 ZoomViewport 的资源重建路径。
        // 异常处理器在场时换不出可用转储（本机 WER 也不写新转储了），
        // 所以先按"崩溃前一步"缩小范围，再决定是否上 stowed exception。
        var crashTraceIntervalMs = Environment.GetEnvironmentVariable("_3FC_CRASH_TRACE");
        System.IDisposable? crashTrace = null;
        if (!string.IsNullOrEmpty(crashTraceIntervalMs))
        {
            var interval = int.TryParse(crashTraceIntervalMs, out var iv) ? Math.Max(1, iv) : 50;
            crashTrace = TraceMultislotState($"mt-trace-{interval}ms", interval, verbose: true);
            Console.WriteLine($"multitest: 已启用崩溃现场追踪（每 {interval}ms 记录一次多路快照与线程数）");
        }

        var code = 2;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"multitest: 文件不存在 {videoPath}");
                ExitSelfTest(2);
            }

            // 打开 N 路：统一暂停对齐（与 OpenPaths 语义一致），全部就绪后手动 Play
            _step = "多路打开";
            Grid.SetCount(routes, _realMode);
            _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
            Console.WriteLine($"multitest: 已请求打开 {routes} 路，等待就绪...");

            // 等全部路就绪（≤20s）
            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < readyDeadline)
            {
                var snaps = _sync.ReadAllSnapshots();
                if (snaps.Count == routes && snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State)))
                    break;
                await System.Threading.Tasks.Task.Delay(200);
            }
            var readySnaps = _sync.ReadAllSnapshots();
            var readyCount = readySnaps.Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));
            if (readyCount != routes)
                throw new InvalidOperationException($"仅 {readyCount}/{routes} 路就绪");

            // 基线：全部对齐到 master（就绪时位置应为 0）
            var basePos = _sync.GetMasterPosition100ns();
            Console.WriteLine($"multitest: 全路就绪 ✓ 基线 pos={TimeSpan.FromTicks(basePos):g}");

            // 素材时长钳制：各阶段总长不得超过素材时长（视频播完 → Ended → presented 停涨）
            var mediaDurSec = _sync.GetMasterDuration100ns() / TimeSpan.TicksPerSecond;
            var budgetSec = Math.Min(durationSec, (int)Math.Max(5, mediaDurSec - 2));
            Console.WriteLine($"multitest: 素材时长 {mediaDurSec}s → 测试预算 {budgetSec}s");

            // 播放（不开循环：避免多路 TickLoop 并发 Seek 导致原生崩溃，短素材在时长内测试）
            _sync.Play();
            _isPlaying = true;

            // 判别实验：把 index >= ACTIVE 的路立刻 Pause —— 它们的 device/swapchain 仍在
            //（已随打开创建），但不再进入 Present。这样"并发 Present 线程数"从"设备数"里被拆开。
            // 注意 TickDrift 只校正 State==Playing 的路，被暂停的路不会被自动恢复播放。
            if (activeRoutes < routes)
            {
                var slots = _sync.Slots;
                for (var i = activeRoutes; i < routes && i < slots.Count; i++)
                {
                    try
                    {
                        slots[i].Session.Pause();
                        Console.WriteLine($"multitest: 第 {i} 路已 Pause（保留设备/交换链，不参与 Present）");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"multitest: 暂停第 {i} 路失败 {ex.Message}");
                    }
                }
            }

            await System.Threading.Tasks.Task.Delay(1500);

            // 采样基线（播放稳定后）
            var baseline = SnapshotTuple(_sync.ReadAllSnapshots());
            var baselineMaster = _sync.GetMasterPosition100ns();
            Console.WriteLine($"multitest: 播放基线 master={TimeSpan.FromTicks(baselineMaster):g}");

            // 判别实验取证：记录各路 SwapChainPresents 起点，收尾时打印增量。
            // 目的是**证明操纵有效**——被 Pause 的路若仍在 Present，B 臂就没有真正减少并发 Present。
            var swBaseline = _sync.ReadAllSnapshots()
                .Select(s => s?.SwapChainPresents ?? -1L).ToArray();

            // 阶段一：静置播放 budgetSec/3，观察无交互漂移
            _step = "多路静置播放";
            var idleSec = Math.Max(3, budgetSec / 3);
            Console.WriteLine($"multitest: 静置播放 {idleSec}s（无交互）...");
            await System.Threading.Tasks.Task.Delay(idleSec * 1000);
            CheckDrift(routes, "静置播放");

            // 阶段二：播放中并行 view transform 扇出（zoom 3 档循环 × 20 次，每次 50ms）
            _step = "多路并行扇出";
            Console.WriteLine("multitest: 并行扇出 20 次（zoom 1→3 循环）...");
            var beforeTransform = SnapshotTuple(_sync.ReadAllSnapshots());
            for (var i = 0; i < 20; i++)
            {
                var z = 1f + (i % 5) * 0.5f; // 1.0 → 3.0 循环
                // 组件日志：多路并行扇出（崩溃现场正是发生在该路径附近）。
                _3FCompare.Diagnostics.ComponentLog.Log(
                    _3FCompare.Diagnostics.Comp.Render, "SetViewTransform", -1,
                    $"src=multitestFanout i={i} z={z:0.###}");
                _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(2000); // 等管线消化
            await CheckPresentedGrowthAsync(beforeTransform, "并行扇出");
            CheckDrift(routes, "并行扇出后");

            // 阶段三：恢复 fit 继续播放，观察复位后稳定性
            _step = "多路复位稳定";
            _3FCompare.Diagnostics.ComponentLog.Log(
                _3FCompare.Diagnostics.Comp.Render, "SetViewTransform", -1, "src=multitestReset z=1");
            _sync.SetViewTransform(1f, 0f, 0f);
            var beforeReset = SnapshotTuple(_sync.ReadAllSnapshots());
            await CheckPresentedGrowthAsync(beforeReset, "复位后");
            CheckDrift(routes, "复位后");

            // 阶段四：全路帧步进同步（验证 StepFrames 多路广播）
            _step = "多路帧步进";
            if (_realMode)
            {
                _sync.Pause();
                await System.Threading.Tasks.Task.Delay(300);
                var beforeStep = _sync.GetMasterPosition100ns();
                // 同 sessiontest：UI 线程直调是自测路径的**刻意例外**（无用户可冻结 + 断言依赖
                // "返回后立刻读快照"的同步顺序），理由与代价论证见 RunSessiontestAsync 里那处注释。
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var afterStep = _sync.GetMasterPosition100ns();
                Console.WriteLine($"multitest: 帧步进 {TimeSpan.FromTicks(beforeStep):g} → {TimeSpan.FromTicks(afterStep):g}");
                if (afterStep < beforeStep)
                    throw new InvalidOperationException($"帧步进位置后退 {beforeStep} → {afterStep}");
                CheckDrift(routes, "帧步进后");
            }

            // 判别实验取证：各路 SwapChainPresents 增量（被 Pause 的路应≈0）
            var swEnd = _sync.ReadAllSnapshots()
                .Select(s => s?.SwapChainPresents ?? -1L).ToArray();
            var swDetail = string.Join(" ", Enumerable.Range(0, Math.Min(swBaseline.Length, swEnd.Length))
                .Select(i => $"L{i}:{(swEnd[i] < 0 || swBaseline[i] < 0 ? "?" : (swEnd[i] - swBaseline[i]).ToString())}"));
            Console.WriteLine($"multitest: SwapChainPresents 增量 [{swDetail}]（L0..L{activeRoutes - 1} 为播放路）");

            Console.WriteLine("multitest: 全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"multitest[步骤{_step}]: 失败 ✗ {ex.Message}");
            code = 1;
        }
        finally
        {
            crashTrace?.Dispose();
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    // ══════════ 崩溃现场追踪（诊断工具，默认关闭：设 _3FC_CRASH_TRACE=间隔ms 启用） ══════════
    // 多路崩溃无法用托管异常处理器捕获（是原生侧访问违例，直接终止进程），
    // 本机 WER 在 2026-09-14 15:00 之后也不再写新转储。
    // 因此改为"崩溃前最后一次成功采样"策略：把关注的状态高频写进文件并 flush，
    // 崩溃后再读文件的最后几行，即可把范围压到 ≤ 一个采样间隔。
    private System.IDisposable? TraceMultislotState(string tag, int intervalMs, bool verbose)
    {
        var tracePath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, $"crash_trace_{tag}.log");
        try { System.IO.File.WriteAllText(tracePath, $"[trace] 开始 {System.DateTime.Now:HH:mm:ss.fff}\n"); }
        catch { return null; }

        var cts = new System.Threading.CancellationTokenSource();
        var task = System.Threading.Tasks.Task.Run(async () =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var snaps = _sync.ReadAllSnapshots();
                    var sb = new System.Text.StringBuilder(256);
                    sb.Append(System.DateTime.Now.ToString("HH:mm:ss.fff"))
                      .Append(" #").Append(n++)
                      .Append(" 线程=").Append(System.Diagnostics.Process.GetCurrentProcess().Threads.Count)
                      .Append(" 句柄=").Append(System.Diagnostics.Process.GetCurrentProcess().HandleCount)
                      .Append(" 路数=").Append(snaps.Count).Append(" |");
                    for (var i = 0; i < snaps.Count; i++)
                    {
                        var s = snaps[i];
                        sb.Append(" L").Append(i).Append(':')
                          .Append(s is null ? "null"
                              : $"{(s.Position100ns / 10000)}ms/{s.PresentedVideoFrames}/{s.SwapChainPresents}/{s.State}");
                    }
                    System.IO.File.AppendAllText(tracePath, sb.Append('\n').ToString());
                    if (verbose && n % 20 == 1) Console.WriteLine($"[trace] {sb}");
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(tracePath, $"采样异常: {ex.Message}\n"); } catch { }
                }
                try { await System.Threading.Tasks.Task.Delay(intervalMs, cts.Token); }
                catch (System.OperationCanceledException) { break; }
            }
        });

        return new TraceScope(cts, task, tracePath);
    }

    private sealed class TraceScope(
        System.Threading.CancellationTokenSource cts,
        System.Threading.Tasks.Task task,
        string path) : System.IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            try { task.Wait(2000); } catch { }
            try
            {
                System.IO.File.AppendAllText(path,
                    $"[trace] 结束 {System.DateTime.Now:HH:mm:ss.fff}（未崩溃）\n");
            }
            catch { }
            Console.WriteLine($"[trace] 现场日志: {path}");
            cts.Dispose();
        }
    }

    /// <summary>断言多路 position 漂移 ≤ 阈值（master 为基准，考虑 Offset 后误差）。
    /// 8K 高码率下允许 ±2 帧（@24fps ≈ 83ms）容差，超过即判定漂移。
    /// 判别实验下 index ≥ <see cref="_multitestActiveRoutes"/> 的路已被 Pause，
    /// 位置天然不随 master 推进，必须排除在断言之外（否则 exit=1 是断言失败，会污染判读）。</summary>
    private void CheckDrift(int routes, string phase)
    {
        var snaps = _sync.ReadAllSnapshots();
        // docs/41 #12：原先 snaps.Count != routes 时直接 return —— 掉路/快照缺失时
        // 漂移断言**一路都没执行**，却照样打印"漂移 OK"并 exit=0，是典型的假绿入口。
        if (snaps.Count != routes)
            throw new InvalidOperationException(
                $"漂移断言无法执行：快照数 {snaps.Count} != 路数 {routes}（不能静默跳过）");
        var masterPos = _sync.GetMasterPosition100ns();
        // 250ms，不是 100ms。理由（2026-09-20 实测）：本项目既有的漂移实测值为 4K 双路静置 8s
        // 偏差 138~195ms，也就是说 100ms 阈值**低于正常抖动本身**，会稳定产出假失败——
        // 在当天的 A/B 复测里，8 次漂移失败有 7 次是 0.103~0.119s 的擦线，与被测变量无关，
        // 却把"任何失败"口径的对照结果完全抹平（6/12 vs 6/12）。
        // 250ms 仍远小于真正的严重失步（同批复测中出现的 6.21s 失步会被照常判红）。
        const long Tolerance = 250_0000; // 250ms
        var checkedRoutes = 0;
        for (var i = 0; i < snaps.Count; i++)
        {
            if (i >= _multitestActiveRoutes) continue;
            var snap = snaps[i];
            // docs/41 #12：Failed / null 的路原先被 continue 跳过 ⇒ 一路已经死了，
            // 整体仍报"漂移 OK（N 路）"，而 N 里不包含它。
            if (snap is null)
                throw new InvalidOperationException($"第 {i} 路快照为 null，漂移断言无法覆盖");
            // 复核修正：`Slots[i].Failed` 是**粘性**标志——由 PlaybackCoordinator 置 true 后
            // 从不复位，其中一处还是 15s 打开超时。慢盘/冷启下会出现"快照已就绪但 Failed=true"，
            // 直接判红会误伤正常情况。改判**活快照的当前状态**，粘性标志退化为提示。
            if (snap.State == PlayerState.Failed)
                throw new InvalidOperationException($"第 {i} 路引擎状态为 Failed，不能计入漂移通过");
            if (_sync.Slots[i].Failed)
                Console.WriteLine($"   ⚠ 第 {i} 路 Slots.Failed 为真（粘性标志，可能来自打开超时），以活快照状态 {snap.State} 为准");
            var expect = masterPos + _sync.Slots[i].Offset100ns;
            var drift = Math.Abs(snap.Position100ns - expect);
            if (drift > Tolerance)
                throw new InvalidOperationException(
                    $"第 {i} 路漂移 {TimeSpan.FromTicks(drift):g} > {Tolerance / 10_000}ms（pos={TimeSpan.FromTicks(snap.Position100ns):g} 期望 {TimeSpan.FromTicks(expect):g}）" +
                // 判红自带分诊（#42）：这一条红与"校正侧看到的 Δ"差 2–4 倍，光看数分不出是
                // "从路落到 Paused 被刻意排除"还是"整拍被 gate/master 早退跳过"。
                // 读数来自 SyncController 的跳过见证（纯观测，不改任何判定）。
                $"｜各路态={string.Join(",", snaps.Select(s => s is null ? "null" : s.State.ToString()))}" +
                $"｜master态={(snaps.Count > 0 && snaps[0] is { } m0 ? m0.State.ToString() : "?")} " +
                _sync.DriftSkipDiag());
            checkedRoutes++;
        }
        Console.WriteLine($"multitest[{phase}]: 漂移 OK（{checkedRoutes} 路 ≤{Tolerance / 10_000}ms）✓");
    }

    /// <summary>把 ReadAllSnapshots 的引用数组转成值元组副本（避免缓存复用导致前后对比同对象）。</summary>
    private static (long pos, long presented)[] SnapshotTuple(IReadOnlyList<EngineSnapshot?> snaps)
    {
        var result = new (long pos, long presented)[snaps.Count];
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            result[i] = s is null ? (0L, 0L) : (s.Position100ns, s.PresentedVideoFrames);
        }
        return result;
    }

    /// <summary>断言各路 presented 持续增长（无单路停滞）。
    /// 重试式：在窗口内（默认 4s）每 500ms 重新采样直到所有路都增长，避免
    /// 复位/扇出瞬间渲染器切换导致的瞬时零增长误报；窗口耗尽仍不涨=真停滞。
    /// 判别实验下 index ≥ <see cref="_multitestActiveRoutes"/> 的路已被 Pause，
    /// 不再 Present 是**预期行为**，故排除在停滞断言之外。</summary>
    private async System.Threading.Tasks.Task CheckPresentedGrowthAsync(
        (long pos, long presented)[] before, string phase, int maxWaitMs = 4000)
    {
        var deadline = System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(maxWaitMs);
        while (true)
        {
            var after = SnapshotTuple(_sync.ReadAllSnapshots());
            var stalled = new System.Collections.Generic.List<int>();
            var watched = 0;
            for (var i = 0; i < before.Length && i < after.Length; i++)
            {
                if (i >= _multitestActiveRoutes) continue;
                watched++;
                var b = before[i];
                var a = after[i];
                if (a.presented - b.presented <= 0)
                    stalled.Add(i);
            }
            if (stalled.Count == 0)
            {
                // docs/41 #13：watched==0（快照为空 / 路数错配 / before 为空）时原本也走这里，
                // 打印"0 路均 >0 ✓"并 return ⇒ 什么都没测到却判过。
                if (watched == 0)
                    throw new InvalidOperationException(
                        $"presented 增长断言未覆盖到任何一路（watched=0, before={before.Length}）——不能判为通过");
                Console.WriteLine($"multitest[{phase}]: presented 增长 OK（{watched} 路均 >0）✓");
                return;
            }
            if (System.DateTime.UtcNow >= deadline)
            {
                var detail = string.Join(", ", stalled.Select(i =>
                    $"路{i}:{before[i].presented}→{SnapshotTuple(_sync.ReadAllSnapshots())[i].presented}"));
                throw new InvalidOperationException(
                    $"第 {string.Join("/", stalled)} 路在[{phase}] presented 未增长（{detail}）");
            }
            await System.Threading.Tasks.Task.Delay(500);
        }
    }

    // ══════════ 多路对比覆盖层接线验证（--comparemodetest <video> [routes=3]） ══════════
    // 目的：在真机上一键验证 docs/26 阶段 3.1 的接线是否真的可用。以下几项都只与
    // 「覆盖窗能否创建、能否拿到逐像素透明、WM_NCHITTEST 钩子能否装上」有关，
    // 属 Win32/合成器行为，两个测试工程（只引用 Core）覆盖不到，故必须真机跑。
    //
    // 另外验证「格表确实驱动了画面排版」——这是本次改动的核心验收点：
    // 各路 PlayerSurface 的 Bounds 必须与 GridPartition.ComputeCells 的结果一致，
    // 且改变分割参数后 Bounds 必须跟着变（此前只有覆盖层的线条在动）。
    /// <summary>验收点隔离驱动器（切片 0：一条红不许掩掉后面的验收点）。
    ///
    /// <para><b>为什么要有这个</b>：<c>RunCompareModeTestAsync</c> 原本是**单一 try 的线性剧本**——
    /// 任何一条判据抛异常就直接 <c>ExitSelfTest(1)</c>，后面的验收点整批不执行。
    /// 后果不是"少看几条"，而是<b>那几条从未被执行过</b>：<c>docs/46</c> 记为"步名 0 命中"，
    /// 而 2026-09-26 三条历史判据（对齐模式前提、轮换用 Bounds 当换位代理、letterbox 容差大于信号）
    /// 全部是第一次被执行到的那一刻才判红的。</para>
    ///
    /// <para><b>边界（诚实说明）</b>：本切片只隔离"已经写成单行调用"的验收点。
    /// ① 起盘 / 进入模式这类<b>前置</b>仍走 fail-fast——它们失败时后面的判据没有意义，
    ///    把它们降级成一条记录会造假绿灯；② 流程里若干<b>内联</b>代码块（分屏入口、网格入口、
    ///    对比-退出、裁剪下发兜底）仍是抛了就中止，等夹具能按 <c>docs/49</c> 的 <c>Fixture</c>
    ///    自带化之后再逐块收进来。</para>
    ///
    /// <para>步骤名取当前的 <c>_step</c>（看门狗与逐条日志已在用它），重复出现时自动加
    /// <c>#2/#3</c> 序号，因此汇总表里的每一行仍可单独寻址。</para></summary>
    private readonly List<string> _stepOutcomes = new();
    private readonly HashSet<string> _usedStepIds = new();
    private int _stepFailures;

    private string NextStepId()
    {
        var baseId = string.IsNullOrEmpty(_step) ? "(未命名步骤)" : _step;
        if (_usedStepIds.Add(baseId)) return baseId;
        var n = 2;
        while (!_usedStepIds.Add($"{baseId}#{n}")) n++;
        return $"{baseId}#{n}";
    }

    /// <summary>验收点登记表（切片 2）：把 <c>_step</c> 基名映射到 docs/49 的目标部分，
    /// 这样汇总表能按"部分"分组回答"这一轮到底验了哪一块、哪一块被跳过"。
    /// <para>只登记已隔离的单元；未登记的按前缀兜底，绝不因为漏登记而丢条目。</para></summary>
    private static readonly (string Prefix, string Part)[] StepParts = new[]
    {
        // 宿主登记的单元 id（必须排在前面：PartOf 用 StartsWith，先匹配精确 id）
        ("lane.add-remove-contract", "P3 路数与 lane 契约"),
        ("layout.enter-ab",       "P4 对比布局与格表"),
        ("layout.mode-sweep",     "P4 对比布局与格表"),
        ("layout.split-relayout", "P5 分割线与覆盖层"),
        ("view.drag-commit",      "P5 分割线与覆盖层"),   // 覆盖层自己的合并/结算/回灌（此前无测量覆盖）
        ("cells.swap",            "P6 格→路映射"),
        ("cells.rotate",          "P6 格→路映射"),
        ("entry.grid-compare",    "P6 格→路映射"),   // 合并后取代旧 entry.split（别名仍收着"模式-分屏入口"）
        ("entry.grid",            "P6 格→路映射"),
        ("exit.cleanup",          "P6 格→路映射"),
        ("magnify.",              "P7 无缝放大与窗口区域"),
        ("align.modes",           "P8 分辨率对齐模式"),
        ("variants.shape",        "P4 对比布局与格表"),
        ("对比-加减路契约", "P3 路数与 lane 契约"),
        ("对比-切换",       "P4 对比布局与格表"),
        ("对比-模式变体",   "P4 对比布局与格表"),
        ("对比-分割",       "P5 分割线与覆盖层"),
        ("对比-左右互换",   "P6 格→路映射"),
        ("对比-轮换画面",   "P6 格→路映射"),
        ("模式-",           "P6 格→路映射"),
        ("对比-退出",       "P6 格→路映射"),
        ("无缝放大",        "P7 无缝放大与窗口区域"),
        ("对比-滚轮",       "P7 无缝放大与窗口区域"),
        ("放大闸门",        "P7 无缝放大与窗口区域"),
        ("对比-对齐模式",   "P8 分辨率对齐模式"),
        ("放大镜",          "P11 像素级取证"),
    };

    private static string PartOf(string baseId)
    {
        foreach (var (prefix, part) in StepParts)
            if (baseId.StartsWith(prefix, StringComparison.Ordinal)) return part;
        return "未归类（登记表待补）";
    }

    /// <summary>选择器（切片 2 的核心）：<c>FC_STEPS</c> 逗号分隔，条目支持 <c>*</c> 通配，
    /// 也支持 <c>part:P6</c> 按 docs/49 的目标部分整体选。空 = 全跑（默认，语义与今天逐字一致）。
    ///
    /// <para><b>为什么走环境变量而不是命令行参数</b>：新增 <c>--steps</c> 参数必须同时改
    /// <c>MainWindow.axaml.cs</c> 的自测模式白名单（漏改就会出现"测试进程用测试窗口状态
    /// 覆盖用户配置"那类事故），而该文件此刻正被另一路写者动着。选择器要解决的只是
    /// "别为了看一条判据跑完整轮"，用变量落地零风险，且与仓内既有 <c>FC_*</c> 旋钮同构；
    /// 等注册表覆盖全部流程后再统一收成一个命令行参数。</para></summary>
    private static readonly string[]? StepSelector = ParseSelector();

    private static string[]? ParseSelector()
    {
        var raw = Environment.GetEnvironmentVariable("FC_STEPS");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length > 0 ? list : null;
    }

    private static bool _selectorSuppressed;
    private int _cropTotal;                 // 宿主模式：所选单元累计下发的区域次数
    private bool _cropCapableSelected;      // 本轮选中的单元里是否包含"会产生裁剪"的那些

    private static bool Selected(string baseId)
    {
        if (_selectorSuppressed) return true;   // 宿主退回整轮剧本时解除筛选（见 RunSelectedUnitsAsync）
        if (StepSelector is not { Length: > 0 } sel) return true;
        if (_selectorSuppressed) return true;   // 宿主退回剧本时全量执行，见 RunSelectedUnitsAsync
        var part = PartOf(baseId);
        foreach (var entry in sel)
        {
            if (entry.StartsWith("part:", StringComparison.OrdinalIgnoreCase))
            {
                var want = entry[5..].Trim();
                if (part.StartsWith(want, StringComparison.OrdinalIgnoreCase) ||
                    part.Contains(want, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (entry.Contains('*'))
            {
                var rx = "^" + System.Text.RegularExpressions.Regex.Escape(entry).Replace("\\*", ".*") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(baseId, rx)) return true;
                continue;
            }
            if (baseId.Contains(entry, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private int _skippedUnits;

    /// <summary>宿主模式专用的记录器：按<b>给定 id</b> 记账，<b>不再</b>过一遍选择器。
    /// <para>为什么必须分开：宿主已经用别名把单元筛过一次，若再让 <c>StepAsync</c> 拿
    /// 新的英文 id 去比中文选择器，就会选中却判 SKIP —— 实测结果是"什么都没跑 + 全部通过 + exit 0"
    /// 的假绿。选择器的语义只属于整轮剧本那一条路。</para></summary>
    private async Task StepNamedAsync(string id, Func<Task> body)
    {
        _usedStepIds.Add(id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var savedStep = _step;   // 见 StepAsync 里的说明：判据体会改写 _step
        var skipsBefore = _selfTestSkips.Count;
        try
        {
            await body();
            // 跑了，但它自己登记了"有鉴别力的分支没跑" ⇒ 记 SKIP 而不是 PASS。
            // 否则 2 路下 `路启用态` 会打印「PASS 路启用态 (0 ms)」，而它其实什么都没判
            // ——"PASS"与"什么都没判"在汇总表上必须长得不一样。
            if (_selfTestSkips.Count > skipsBefore)
            {
                _skippedUnits++;
                _stepOutcomes.Add($"SKIP {id} {CostOf(sw)}（体内登记跳过，不算验过）");
            }
            else _stepOutcomes.Add($"PASS {id} {CostOf(sw)}");
        }
        catch (Exception ex)
        {
            _stepFailures++;
            _3FCompare.Diagnostics.ComponentLog.Log(
                _3FCompare.Diagnostics.Comp.Test, "UnitFail", -1, $"id={id}" + StepTag());
            _stepOutcomes.Add($"FAIL {id} {CostOf(sw)}: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"comparemodetest[单元 {id}]: 判红 ✗ {ex.Message}");
        }
        finally { _step = savedStep; }
    }

    private async Task<T> StepAsync<T>(Func<Task<T>> body)
    {
        var id = NextStepId();
        if (!Selected(id.Split('#')[0]))
        {
            // SKIP 不是 PASS：分母里必须看得见它没跑，否则"选择性跑"会伪装成"全验过"
            _skippedUnits++;
            _stepOutcomes.Add($"SKIP {id}");
            return default!;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ⚠ 跑完必须把 _step 还原：好几条判据会在自己体内改写 _step（放大判据写 "无缝放大 z=2"、
        // 放大镜写 "放大镜浮窗坐标"），而 NextStepId() 取的正是当前的 _step ——
        // 不还原就会把后面几行的读数记成<b>上一行的名字>（实测：cells 驱动 / crop / gates
        // 三行被记成"放大镜浮窗坐标#2/#3"，P7 的判据被归进 P11），汇总表就不可信了。
        var savedStep = _step;
        var skipsBefore = _selfTestSkips.Count;
        // 验收点边界进组件日志：崩溃带过去只能靠"第几次 SetWindowRgn"这种剂量去猜它落在哪个验收点里
        // （docs/50 §十九→§二十），有了 UnitBegin/UnitDone 就是直接读数。
        _3FCompare.Diagnostics.ComponentLog.Log(
            _3FCompare.Diagnostics.Comp.Test, "UnitBegin", -1, $"id={id}" + StepTag());
        try
        {
            var v = await body();
            _3FCompare.Diagnostics.ComponentLog.Log(
                _3FCompare.Diagnostics.Comp.Test, "UnitDone", -1, $"id={id}" + StepTag());
            // 同 StepNamedAsync：体内登记过跳过的行不记 PASS
            if (_selfTestSkips.Count > skipsBefore)
            {
                _skippedUnits++;
                _stepOutcomes.Add($"SKIP {id} {CostOf(sw)}（体内登记跳过，不算验过）");
            }
            else _stepOutcomes.Add($"PASS {id} {CostOf(sw)}");
            return v;
        }
        catch (Exception ex)
        {
            _stepFailures++;
            _3FCompare.Diagnostics.ComponentLog.Log(
                _3FCompare.Diagnostics.Comp.Test, "UnitFail", -1, $"id={id}" + StepTag());
            _stepOutcomes.Add($"FAIL {id} {CostOf(sw)}: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"comparemodetest[验收点 {id}]: 记录失败并继续 ✗ {ex.Message}");
            return default!;
        }
        finally { _step = savedStep; }
    }

    private Task StepAsync(Func<Task> body) =>
        StepAsync(async () => { await body(); return 0; });

    private Task StepAsync(Action body) =>
        StepAsync(() => { body(); return Task.FromResult(0); });

    // 每个验收点的实际耗时都要留档：拆分值不值，看的是"这条单元自己多贵"，
    // 而不是"整轮多贵"——只有前者能决定它该进 V2（0 素材）还是必须留在 V3（真解码）。
    private long _isolatedMs;
    private string CostOf(System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        _isolatedMs += sw.ElapsedMilliseconds;
        return $"({sw.ElapsedMilliseconds} ms)";
    }

    private void PrintStepSummary(string flow)
    {
        var sel = StepSelector;
        var header = sel is { Length: > 0 }
            ? $"选择器 FC_STEPS=[{string.Join(", ", sel)}] ⇒ 未匹配的单元记为 SKIP（**不算验过**）；" +
              "注意流程内的前置（起盘、进入模式、固定延时）不受选择器控制，仍会执行。"
            : "未设选择器：全部验收点均执行。";
        Console.WriteLine($"{flow}: ── 验收点汇总（{_stepOutcomes.Count} 项，" +
                          $"失败 {_stepFailures}，跳过 {_skippedUnits}）── {header}");
        // 按 docs/49 的目标部分分组：让人一眼看出"哪一块这轮根本没看"
        string? currentPart = null;
        foreach (var o in _stepOutcomes)
        {
            var baseId = o.Split(' ')[1].Split('#')[0];
            var part = PartOf(baseId);
            if (part != currentPart)
            {
                Console.WriteLine($"  【{part}】");
                currentPart = part;
            }
            Console.WriteLine($"    {o}");
        }
    }

    /// <summary>已"自带夹具"的验收点（切片 2 的核心：单跑不付整轮剧本的钱）。
    /// <para>登记一个单元 = 把它原先"在剧本里的位置"换成<b>它自己声明的前置</b>：
    /// <c>Routes</c> 要求的最小会话路数、<c>Shape</c> 要求钉住的分格朝向、<c>Body</c> 判据本体。
    /// 未登记的判据仍只能按整轮剧本跑，宿主会<b>明说</b>哪些没匹配，绝不静默少跑。</para>
    /// <para><c>Aliases</c> 收着旧的 <c>_step</c> 中文标签，所以 <c>FC_STEPS=对比-对齐模式</c>
    /// 和 <c>FC_STEPS=align.modes</c> 选到同一个单元。</para></summary>
    private sealed record Unit(string Id, int Routes, (int Cols, int Rows)? Shape, string[] Aliases, Func<Task> Body);

    /// <summary>自测夹具：进入网格对比，并把分格朝向钉到 <paramref name="cols"/>×<paramref name="rows"/>。
    /// <para>等价于用户按 G 进入后再按几下 C。权重退回均匀，让后续判据有确定的起点 ——
    /// 单元之间不该被上一个单元拖出来的列宽影响。</para></summary>
    private async Task EnterGridShapeAsync(int cols, int rows)
    {
        // 夹具自己先把不合法的构型拦下：合并的前提是"一路一格"，
        // 装不下就无从谈起（真实路径由 GridLayout.ResolveGrid 兜住，这里兜住的是夹具）。
        if (cols > 0 && rows > 0 && cols * rows < Grid.Count)
            throw new InvalidOperationException(
                $"夹具：{cols}x{rows} 装不下 {Grid.Count} 条 lane（网格对比的基准是一路一格）");
        EnterCompareMode();
        await System.Threading.Tasks.Task.Delay(150);
        if (!_compareActive)
            throw new InvalidOperationException(
                $"网格对比未能激活（路数 {Grid.Count} / 素材 {_sync.Count}；覆盖层降级也会走到这里）");
        if (cols > 0 && rows > 0)
        {
            _partition = GridPartition.Uniform(Grid.Count, cols, rows);
            _layoutOverlay?.Update(_partition);
            ApplyCompareLayout();
            ScheduleCompareCrop();
            await System.Threading.Tasks.Task.Delay(150);
        }
        UpdateLayout();
    }

    /// <summary>P5 单元：拖动分割手柄 ⇒ <c>PartitionChanged</c> ⇒ 重算格表 ⇒ 画面跟着迁。
    /// <para><b>断言形式勿改回"宽和高都要变"</b>：竖分割线只改宽度，AB 的第 0 格本就通高；
    /// 旧写法在 2 路下必然误报，而 4 路（十字）恰好掩盖了它。正确形式是用
    /// <c>partition.ComputeCells()</c> 在拖动前后各自<b>独立复算</b>期望矩形，断言各路实际
    /// <b>露出的那块</b>确实由前者迁到后者；并先坐实"这一下拖动真的改变格表"，否则断言退化为恒真（无鉴别力）。
    /// 拼合模式下"露出的那块"是窗口区域回读，不是 Bounds（各路 Bounds 恒等于整对比区）。</para>
    /// <para>拖动目标用 Core 的 <see cref="GridPartition.WithHandleDragged"/> 造，
    /// 于是"手柄 0 = 第一条竖线"这条索引语义也被顺带验了一遍。</para></summary>
    /// <returns>本单元真正下发的裁剪次数（供剧本累计"整轮必须下发过区域"的兜底）。</returns>
    private async Task<int> SplitRelayoutUnitAsync(int routes, int cols, int rows)
    {
        await EnterGridShapeAsync(cols, rows);

        var oldCells = _partition.ComputeCells();
        var dragged = _partition.WithHandleDragged(0, 0.7, 0.35);   // 第一条手柄：2 列时是竖线
        if (dragged.SameShape(_partition))
            throw new InvalidOperationException(
                $"自测的拖动不改变 {cols}x{rows} 的格表，断言将失去鉴别力");
        var newCells = dragged.ComputeCells();

        var cellChanged = false;
        for (var i = 0; i < newCells.Length; i++)
            if (!CellEquals(oldCells[i], newCells[i]))
                cellChanged = true;
        if (!cellChanged)
            throw new InvalidOperationException(
                $"拖动前后格表逐项相同（{cols}x{rows}），断言失去鉴别力");

        var areaW = Grid.Bounds.Width;
        var areaH = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var before = RouteVisibleRectPx(0, scaling, $"拖动前 {cols}x{rows}");
        var beforeExp = ExpectedBlockPx(CellOfRoute(0, oldCells, "拖动前"), areaW, areaH, scaling);
        if (!BlockClose(before, beforeExp))
            throw new InvalidOperationException(
                $"拖动前 {cols}x{rows} 第 0 路露出的块 {before} 与旧格表期望 {beforeExp} 不符");

        OnPartitionChanged(dragged);
        UpdateLayout();
        ApplyCompareCrop();   // 分割变化只排了一次队（Background 优先级），本单元要当场读到新块

        for (var i = 0; i < routes; i++)
        {
            if (i >= newCells.Length) continue; // 路数多于格数（拉动态才会出现）：多余的路已隐藏
            AssertRouteBlockAt(i, CellOfRoute(i, newCells, "分割驱动重排"),
                $"分割驱动重排（{cols}x{rows} 拖到 0.70 后）");
        }
        var after = RouteVisibleRectPx(0, scaling, "分割驱动重排");

        AssertCellsDriveLayout(routes, $"分割 0.70（{cols}x{rows}）");
        var crop = AssertCompareCrop(routes, "分割 0.70");
        Console.WriteLine(
            $"  分割驱动重排 ✓ {cols}x{rows} 第0路露出块 {before.Width}x{before.Height}px → " +
            $"{after.Width}x{after.Height}px（与 ComputeCells 复算的拖动前后期望一致，" +
            $"{(Grid.MosaicWindows ? "观测量=窗口区域回读" : "观测量=窗口 Bounds")}）");
        return crop;
    }

    /// <summary>P6 单元：合并后的「网格对比」入口（G / V / 菜单同一落点）。
    /// <para><b>这条单元就是合并本身的验收点</b>：旧分屏入口断言"5 路收敛到 ABCD、只显示前 4 路、
    /// 第 5 路必须隐藏"；合并后基准换成网格的渲染覆盖 ⇒ 判据<b>反过来</b>：
    /// <b>路数 ≡ 格数，一条 lane 都不许被挤掉</b>。若哪天回退成"固定 2~4 格"，这里立刻判红。</para>
    /// <para>第二半是幂等：已处于该态时再走一遍入口不得抹掉用户拖好的列宽。</para></summary>
    private async Task GridCompareEntryUnit(int routes)
    {
        EnterGridCompare();
        await System.Threading.Tasks.Task.Delay(150);

        if (!_compareActive)
            throw new InvalidOperationException(
                $"网格对比入口未激活：active={_compareActive}（{routes} 路，素材 {_sync.Count}）");

        // 一路一格：格数必须等于 lane 数，且每条 lane 都可见
        var cells = _partition.CellCount;
        if (cells != Grid.Count)
            throw new InvalidOperationException(
                $"网格对比入口的格数 {cells} ≠ lane 数 {Grid.Count}（合并的基准就是一路一格）");
        for (var i = 0; i < Grid.Count; i++)
            if (Grid.GetSurface(i)?.IsVisible != true)
                throw new InvalidOperationException(
                    $"网格对比入口：第 {i} 条 lane 不可见（{routes} 路应全部铺出，" +
                    $"实际形状 {_partition.Cols}x{_partition.Rows}）");

        AssertCellsDriveLayout(routes, "网格对比入口");
        Console.WriteLine(
            $"  网格对比入口 ✓ {routes} 路全部显示：形状 {_partition.Cols}x{_partition.Rows}，" +
            $"{_partition.HandleCount} 条可拖分割线，无路被隐藏或停用");

        // 幂等：拖过之后再按一次入口不得重置列宽（EnterCompareMode 对已激活态是空操作）
        var dragged = _partition.WithHandleDragged(0, 0.31, 0.62);
        OnPartitionChanged(dragged);
        UpdateLayout();
        EnterGridCompare();
        if (!_partition.SameShape(dragged))
            throw new InvalidOperationException(
                $"网格对比入口重进后列宽被重置：{_partition.ColumnWeights} ≠ 用户拖动的 {dragged.ColumnWeights}");
        Console.WriteLine("  网格对比入口幂等 ✓ 已在该态时未重置已拖好的列宽");
    }


    /// <summary>播放一小段，逐路记 <c>PresentedVideoFrames</c> 增量，断言
    /// <b>"没占格的路真的不干活、占格的路真的在干活"</b>。
    ///
    /// <para><b>正对照是必需的</b>：只断言"未占格路不涨"时，"整轮根本没在播"也能判绿。
    /// 所以占格路必须涨这条与它成对出现。</para>
    ///
    /// <para><b>改动前的读数</b>（2026-09-27，5 路进分屏，同一判据的观测版）：
    /// 未占格的第 5 路 1.2 秒内 presented <b>+41</b>，与看得见的四路（39/43/41/39）同速
    /// —— 这就是"只是隐藏"的代价，9 路用 ABCD 时等于 5 路在白烧解码与呈现线程。</para></summary>
    private async Task AssertRouteActivityAsync(int routes, int visibleCells, string label)
    {
        var wasPlaying = _isPlaying;
        var before = SnapshotTuple(_sync.ReadAllSnapshots());
        _sync.Play();
        await System.Threading.Tasks.Task.Delay(1200);
        var after = SnapshotTuple(_sync.ReadAllSnapshots());
        if (!wasPlaying) _sync.Pause();

        var bad = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < routes; i++)
        {
            var d = after[i].presented - before[i].presented;
            var shown = i < visibleCells;
            sb.Append($" 路{i}{(shown ? "" : "(未占格)")}={d}");
            if (_sync.IsRouteActive(i) != shown)
                bad.Add($"第 {i} 路启用态={_sync.IsRouteActive(i)}，占格={shown}");
            if (shown && d <= 0)
                bad.Add($"占格的第 {i} 路 presented 未增长（正对照失败：这 1.2 秒整体没在播，判据无效）");
            if (!shown && d > 0)
                bad.Add($"未占格的第 {i} 路仍在解码/呈现（+{d}）");
        }
        Console.WriteLine($"  {label} ✓ 1.2 秒 presented 增量:{sb}");
        if (bad.Count > 0) throw new InvalidOperationException($"{label}：{string.Join("；", bad)}");
    }

    /// <summary>P6 单元：多余路必须<b>停用</b>而不是只隐藏，且切回全部占格时要能恢复。
    /// <para>一趟走完"停用 → 重新启用"：① 制造"未占格的路" ⇒ 它们停用且不呈现；
    /// ② 回到全部占格 ⇒ 那些路重新启用、恢复呈现，并且位置落回规范时间
    /// （停用期间它不跟随 master，若不 Seek 回去，用户切回来会看到停在旧帧）。</para>
    ///
    /// <para><b>⚠ 本单元的"制造方式"已随 N 带语义改变（2026-10-07）</b>：
    /// 旧写法靠"左右拉动恒两格"把第 3 路起挤出去。N 带之后拉动<b>逐路都有带</b>
    /// （这正是修掉"加路失效"的那条改动），故拉动本身不再产生"未占格的路"。
    /// 现在改用它真正的来源：<b>用户经「选择路」把带表指定得比素材路数少</b>
    /// （<c>SetWipeRoutes</c>，即"只对比其中几路"这一正常用法）⇒ 未入选的路没有带、
    /// 自然未占格。鉴别力前提从"≥3 路"变为"≥3 路且带表少于路数"，判决口径一字未改。</para></summary>
    private async Task RouteActivityUnit(int routes)
    {
        if (routes < 3)
        {
            Console.WriteLine($"  路启用态：**跳过**（{routes} 路不足以让带表少于路数（需 ≥3），" +
                              "该判据会退化成恒真；宿主以 Routes=5 起盘时才验得到）");
            RecordSelfTestSkip($"路启用态：{routes} 路造不出「带表少于路数」，整条判据恒真");
            return;
        }

        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(200);
        if (!EnterCompareOverlay())
            throw new InvalidOperationException(
                $"路启用态：进不了左右拉动（素材 {routes} 路），造不出「未占格的路」");

        // 关键一步：把带表收窄到前 2 路（用户"只对比前两路"的真实操作），
        // 于是第 3 路起没有带 ⇒ 未占格 ⇒ 必须被停用。
        // 旧实现是"拉动天然只有 2 格"达到同样效果；N 带下必须显式指定。
        const int compared = 2;
        SetWipeRoutesForTest(MakeIdentityWipeRoutes(compared));
        await System.Threading.Tasks.Task.Delay(300);   // 占格集合变化 → Post 的延后一拍要落地
        await AssertRouteActivityAsync(routes, compared, "拉动停用");

        // 回到全部占格：此前停用的路必须重新启用并恢复呈现
        SetWipeRoutesForTest(MakeIdentityWipeRoutes(routes));
        await System.Threading.Tasks.Task.Delay(300);
        await AssertRouteActivityAsync(routes, routes, "网格恢复");

        // 重新启用是先 Seek 回规范时间再播的：**被本单元停用的那几路**应与 master 同位。
        // 本步原写法有三个错，2026-09-28 由整轮跑出的读数逐个抓出来（账本 §10.8/§10.9）：
        //   ① 取"全路最差"当判决 ⇒ 把别的单元留下的失步算到本单元头上。整轮里报红的从来是第 1/2/3 路
        //      （play_r1..r5、state_r1..r3、conv_r1..r6），而本单元停用/再启用的是第 4 路；
        //   ② 在上一步 helper 结尾的整组 `Pause()` 之后立刻读一次 ⇒ 被测对象被冻住，
        //      而 `TickDriftCore` 在 master 非 Playing 时整段早退 ⇒ "永不收敛"成为必然红
        //      （实测 40 次采样位置一毫秒都不动；`FC_TRACE_PAUSE=1` 点名的调用方=SelfTest.cs:2946 那个 helper）；
        //   ③ 只吃一次采样，没有"等产品确实追平"的等待。
        // 现在：只对**本单元停用/再启用的那几路**判决；采样只在播放态算数（非播放态由判据自己重新起播，
        //   并把那一枪记为作废）；轮询到收敛或超时。阈值 250ms 一字未改。
        const long Tolerance = 250_0000;   // 250ms —— 阈值一字未改
        const int SettleTimeoutMs = 6000;  // > 5 个校正节拍（DriftCheckIntervalMs=1000ms），留出 Seek + 追帧
        // 本单元停用/再启用的是"未被带表选中的那几路"= 第 compared 路起。
        // 旧实现硬编码 2（"拉动恒两格"），N 带下必须用实际收窄的带数。
        var firstReactivated = compared;
        var wasPlayingBeforeDrift = _isPlaying;
        _sync.Play();
        var trajectory = new System.Collections.Generic.List<string>();
        var worstTarget = 0L; var worstAll = 0L; var worstIdx = -1; var curWorstPos = 0L;
        var targetState = "?";
        var prevByRoute = new long[Math.Max(routes, 1)];
        var seenByRoute = new bool[Math.Max(routes, 1)];
        var validSamples = 0;   // 落在播放态的采样数 —— 只有这些有判决资格
        var replays = 0;        // 判据为守住"在播"这个前置，被迫重新起播几次
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(SettleTimeoutMs);
        while (true)
        {
            var snaps = _sync.ReadAllSnapshots();
            var masterPos = snaps[0]?.Position100ns ?? 0;
            worstTarget = 0;
            worstAll = 0;
            targetState = "?";
            worstIdx = -1;
            curWorstPos = 0;
            for (var i = 1; i < routes; i++)
            {
                var s = snaps[i] ?? throw new InvalidOperationException($"网格恢复：第 {i} 路无快照");
                var drift = Math.Abs(s.Position100ns - (masterPos + _sync.Slots[i].Offset100ns));
                worstAll = Math.Max(worstAll, drift);
                if (i >= firstReactivated)
                {
                    // 状态、位置、路号必须一起取**同一路**：原先 `targetPos` 无条件等于最后一路的位置，
                    // 而 `targetState` 是最差那一路的状态 ⇒ 轨迹能写出"Paused + 位置在动"这种两头话，
                    // 把归因往错方向带（2026-10-02 追 #42 时撞见；同一段还漏报了"这一路根本没启用"）。
                    if (drift > worstTarget)
                    {
                        worstTarget = drift;
                        targetState = s.State.ToString();
                        worstIdx = i;
                        curWorstPos = s.Position100ns;
                    }
                }
            }
            // 状态与位置增量都要记：Active=True 而 State=Paused = "占着格却停着"，而 TickDriftCore
            // 只校正 Playing/Ended ⇒ 这种路不可能自己追回来 —— 那是"没人管"，不是"追不上"。
            // 位置在动 + Playing = 解码追帧（慢但收敛）；Playing 而位置不动 = 校正没对这一路出手。
            var masterPlaying = snaps[0]?.State == PlayerState.Playing;
            var deltaText = worstIdx < 0 ? "无目标路"
                : !seenByRoute[worstIdx] ? "首次"
                : $"{(curWorstPos - prevByRoute[worstIdx]) / TimeSpan.TicksPerMillisecond}ms";
            var activeText = worstIdx < 0 ? "" : (_sync.IsRouteActive(worstIdx) ? "" : " 且Active=false!");
            if (worstIdx >= 0) { prevByRoute[worstIdx] = curWorstPos; seenByRoute[worstIdx] = true; }
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}]{worstTarget / TimeSpan.TicksPerMillisecond}ms" +
                $"@第{worstIdx}路={targetState}{activeText}(全路最差 {worstAll / TimeSpan.TicksPerMillisecond}ms " +
                $"该路位置增量 " + deltaText +
                $"/master={snaps[0]?.State}";
            if (masterPlaying) validSamples++;
            else { replays++; _sync.Play(); entry += " 作废：未在播放⇒已重新起播"; }
            trajectory.Add(entry + ")");
            if ((masterPlaying && worstTarget <= Tolerance) || DateTime.UtcNow >= deadline) break;
            await System.Threading.Tasks.Task.Delay(150);
        }
        var worstTargetAtExit = worstTarget;
        var validSamplesAtExit = validSamples;
        var replaysAtExit = replays;
        var trajectoryAtExit = string.Join(" → ", trajectory);
        // ⚠ 诊断计数必须在下面那次"按原意停回"之前取：NotPlayingStreak 随整组转"不想要播"立即清零，
        //   取晚了只剩一串 0，读不出这一路到底被请求过起播没有（那正是检测器的盲区所在）。
        string diagAtExit = "无目标路";
        if (worstIdx >= 0)
        {
            var d = _sync.LostPlayDiag(worstIdx);
            diagAtExit = $"第{worstIdx}路 Active={_sync.IsRouteActive(worstIdx)} Failed={d.Failed} " +
                         $"整组要播={d.Want} 已请求起播={d.Requests}次 连续未播={d.Streak}拍";
        }

        // 还原本轮原意：③"暂停态切回"那一相位**要求整组是停着的**（否则 presented 靠正常播放就会涨，
        // 那一枪变成恒真）。上面为了量同位把组开了起来，这里必须按原样停回去。
        if (!wasPlayingBeforeDrift) _sync.Pause();
        var preconditionNote =
            $"（有效采样 {validSamplesAtExit}/{trajectory.Count}，为守住\u201c在播\u201d前置重新起播 {replaysAtExit} 次）";
        if (validSamplesAtExit == 0)
        {
            // 一次都没落进播放态 ⇒ 这条判据**根本没量到东西**：既不判绿也不判红，登记 SKIP。
            RecordSelfTestSkip($"路启用态：{SettleTimeoutMs}ms 内 {trajectory.Count} 次采样全部不在播放态" +
                $"（重新起播 {replaysAtExit} 次仍被别的写者按回暂停；诊断 {diagAtExit}）⇒ 同位判据未验证");
            Console.WriteLine($"  路启用态：**跳过**（全程没落进播放态，未验证 ≠ 通过）{preconditionNote}");
        }
        else if (worstTargetAtExit > Tolerance)
            throw new InvalidOperationException(
                $"重新启用后第 {firstReactivated}..{routes - 1} 路与规范时间相差 " +
                $"{worstTargetAtExit / TimeSpan.TicksPerMillisecond}ms > 250ms，等满 {SettleTimeoutMs}ms 仍未收敛" +
                $"{preconditionNote}（诊断 {diagAtExit}）（轨迹 {trajectoryAtExit}；" +
                "停用期间它不跟随 master，切回来必须 Seek 回规范时间）");
        else
            Console.WriteLine($"  路启用态 ✓ {routes} 路：分屏停用第 {firstReactivated}..{routes - 1} 路、回网格全部恢复且同位" +
                              $"（重新启用的那几路 {worstTargetAtExit / TimeSpan.TicksPerMillisecond}ms 内追平{preconditionNote}）");

        // ③ **暂停态**下"停用 → 再启用"：Seek 只把位置挪对，画面是否真落下来是另一件事
        //   （本仓自记的内核契约：子窗口尺寸变了不 Redraw 就停 flips，见 SyncController.RedrawAll）。
        //   这一步全程不 Play；停滞看门狗只看第 0 路，看不见"切回来的那一路冻着"。
        // ⚠ 这一相位的**前提是整组真的停着** ⇒ 由本相位自己立起来（上一相位为了量同位把组开了起来，
        //   而它的"按原意还原"只在轮子本来就没在播时才停：`_isPlaying==true` 的整轮里旧代码根本不暂停，
        //   于是 presented 靠正常播放一直涨 ⇒ 这一枪恒真 —— 2026-09-28 新加的守卫当场把它抓出来了
        //   （fin_r2/r3：`相位③前置失效：整组仍在播放`）。 ⇒ 以前那种"绿"没量到东西。
        _sync.Pause();
        await System.Threading.Tasks.Task.Delay(200);   // 给 Pause 一点落地的时间，再验前置
        if (_sync.ReadMasterSnapshot()?.State == PlayerState.Playing)
            throw new InvalidOperationException(
                "相位③前置失效：整组仍在播放 ⇒ 无法把\u201c重新启用后才出帧\u201d与\u201c本来就在播\u201d区分开，这一枪会恒真");
        var beforeReEnable = SnapshotTuple(_sync.ReadAllSnapshots());
        // 合并后"进一次网格对比再退出"不再产生"停用 → 再启用"（网格本来就一路一格，两次都全部占格）
        // ⇒ 这一相位改由左右拉动制造那个 cycle：拉动铺满两格会把第 3~N 路挤出去，退出拉动再把它们
        //   放回来 —— 正是"暂停态下切回一整格尺寸"要观察的动作本身。
        if (!EnterCompareOverlay())
            throw new InvalidOperationException(
                $"相位③前置失效：进不了左右拉动（{routes} 路），造不出「暂停态下的再启用」");
        await System.Threading.Tasks.Task.Delay(300);
        ExitCompareOverlay();
        // ⚠ 又是一次性采样抢跑：再启用是 `Seek` + 补一次呈现，而** seek 出来的那一帧要等解码**，
        //   5 路 4K 下这常常超过原来那 400ms 的固定等待 ⇒ 同一份代码 me/rev 两批各 5/8 判红、
        //   0/10 判绿（`lane_r*`、`rev8_r*`、`fixed_r*`）——红与绿都不说明产品变了。
        //   改为**有界轮询到 presented 真的涨**（上限 2s）。要求本身没有放松：
        //   到点仍不涨就是"切回来的那格不落地"，照旧判红，并把每次读到的增量打出来。
        var last = routes - 1;
        var presentedWait = new System.Collections.Generic.List<string>();
        long presentedBefore = beforeReEnable[last].presented, presentedNow = presentedBefore;
        var presentedDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(2000);
        while (true)
        {
            var snapAll = SnapshotTuple(_sync.ReadAllSnapshots());
            presentedNow = snapAll[last].presented;
            presentedWait.Add($"{presentedNow - presentedBefore}");
            if (presentedNow > presentedBefore || DateTime.UtcNow >= presentedDeadline) break;
            await System.Threading.Tasks.Task.Delay(100);
        }
        var afterReEnable = SnapshotTuple(_sync.ReadAllSnapshots());
        if (presentedNow <= presentedBefore)
            throw new InvalidOperationException(
                $"暂停态下重新启用第 {last} 路后 2s 内没有重新出帧（presented {presentedBefore} → {presentedNow}，" +
                $"每 100ms 采一次的增量序列 {string.Join(",", presentedWait)}）：" +
                "Seek 只挪位置，切回格尺寸必须补一次 Redraw；补了还不落帧＝那一格停在旧帧上");
        Console.WriteLine($"  路启用态 ✓ 暂停态切回第 {last} 路重新出帧" +
                          $"（presented +{presentedNow - presentedBefore}，第 {presentedWait.Count} 次采样看到）");

        // ④ **暂停态**下还要量一次"同位"：这一路刚被 `Seek` 回规范时间，而**组是停着的** ⇒
        //   `TickDriftCore` 在 master 非 Playing 时整段早退，没有任何机制会把它拉回来 ——
        //   所以这是唯一能干净观察"再启用的那次 Seek 到底做没做"的时刻。
        //   为什么不在相位②做：那里组在播，漏掉的 Seek 会在下一个校正节拍被补上，
        //   判据只看到"最终同位"（实测：摘掉 Seek 后②判绿，见 §11.1 的变异）。
        var pausedDrift = new System.Collections.Generic.List<string>();
        long pausedWorst = 0;
        for (var sample = 1; sample <= 6; sample++)
        {
            var snapAll = _sync.ReadAllSnapshots();
            var mPos = snapAll[0]?.Position100ns ?? 0;
            pausedWorst = 0;
            for (var i = firstReactivated; i < routes; i++)
            {
                var s = snapAll[i] ?? throw new InvalidOperationException($"暂停态切回：第 {i} 路无快照");
                pausedWorst = Math.Max(pausedWorst,
                    Math.Abs(s.Position100ns - (mPos + _sync.Slots[i].Offset100ns)));
            }
            pausedDrift.Add($"{pausedWorst / TimeSpan.TicksPerMillisecond}ms");
            if (pausedWorst <= Tolerance) break;
            await System.Threading.Tasks.Task.Delay(100);
        }
        if (pausedWorst > Tolerance)
            throw new InvalidOperationException(
                $"暂停态下切回第 {firstReactivated}..{routes - 1} 路后停在离规范时间 " +
                $"{pausedWorst / TimeSpan.TicksPerMillisecond}ms 外（采样 {string.Join(",", pausedDrift)}）：" +
                "组是停着的 ⇒ 没有校正会来补，这一格播下去就永久错位；" +
                "再启用必须先 Seek 回规范时间（`SetActiveRoutes` 的启用分支）");
        Console.WriteLine($"  路启用态 ✓ 暂停态切回即已同位（{pausedWorst / TimeSpan.TicksPerMillisecond}ms ≤ 250ms，" +
                          $"组停着没人会补 ⇒ 这一枪直接盯 Seek）");
    }

    /// <summary>让<b>产品自己的</b>呈现停滞看门狗走一遍轻量恢复，并确认真的把整组唤醒了。</summary>
    /// <para><b>怎么稳定造出停滞</b>：只叫引擎侧 <c>_sync.Pause()</c>、<b>不</b>动 <c>_isPlaying</c>
    /// （正常按暂停键会两个一起改）。轮询于是看到"UI 认为在播、presented 却不再增长"——
    /// 正是 <c>RenderStallWatchdog</c> 的触发条件，阈值 1250ms 后必然给出 LightRecovery。</para>
    /// <para><b>为什么要它</b>：恢复序列 2026-09-28 改成"读回来再发下一步"（账本 §11.10）。
    /// 那处改动若写反（等待条件错、Play 没发出、补发分支没走到），症状正是"这一组再也醒不过来"。
    /// 本单元把这条路径变成常规验收，而不是等真机上偶发的停滞。</para>
    /// <para><b>牙</b>：判据是"master 的 presented 至少再涨 3 帧、且没有启用路还停在 Paused"。
    /// 恢复没发生时这两条永远不成立（<c>TickDriftCore</c> 刻意不校正 Paused 路 ⇒ 不会自愈），所以不是恒真判据；
    /// 要求 +3 帧是为了不让"Pause 落地前那一张在飞的帧"冒充恢复。</para>
    /// <para><b>前提被抢走时如实 SKIP，不判红也不判绿</b>：轮询每拍会把 <c>_isPlaying</c> 同步回引擎状态
    /// （<c>MainWindow.Playback.cs:545</c>），本单元每拍把它按回真以维持前提；若整窗都没触发看门狗，
    /// 那是<b>夹具没把前提立起来</b>，登记 SKIP 而不是指控产品 ——
    /// 第一版正是把这种情形写成了"恢复序列本身的缺陷"，属于过度归因
    /// （2026-09-28 07:48:41 实测红过一轮，读数在 <c>tests/output/stallrecovery-1.log</c>；
    /// 改后 07:50:47 通过：presented 12→19、1754ms、按回 _isPlaying 7 次）。</para>
    private async Task StallRecoveryUnit()
    {
        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(200);
        _sync.Play();
        await System.Threading.Tasks.Task.Delay(300);
        if (!_isPlaying)
            throw new InvalidOperationException("停滞唤醒：前置失效 —— _isPlaying=false，看门狗不会检测停滞");
        var before = _sync.ReadAllSnapshots()[0] ??
                     throw new InvalidOperationException("停滞唤醒：前置取不到 master 快照");
        var frozenAt = before.PresentedVideoFrames;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _sync.Pause();      // 只冻引擎、UI 仍认为在播 ⇒ 制造停滞

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        long best = frozenAt;
        var woke = false;
        var fought = 0;
        while (DateTime.UtcNow < deadline)
        {
            await System.Threading.Tasks.Task.Delay(250);
            // ⚠ 轮询每拍都会把 `_isPlaying` 同步回引擎状态（MainWindow.Playback.cs:545：
            //   `master 非 Playing 且 _isPlaying ⇒ SetPlaying(false)`），而停滞检测的前提正是
            //   "`_isPlaying` 为真而 presented 不涨"。2026-09-28 第一次跑这条单元时被它清掉，
            //   8s 内看门狗一次都没触发 —— **那是夹具的前提没成立，不是恢复序列坏了**（我当时把这句
            //   写进了断言消息，属于过度归因，已改）。这里每拍把 `_isPlaying` 按回真，
            //   是在**维持前提**，不是伪造恢复：presented 是否真的重新增长仍由内核读数决定。
            if (!_isPlaying) { SetPlaying(true); fought++; }
            var sn = _sync.ReadAllSnapshots();
            if (sn[0] is not { } m) continue;
            best = Math.Max(best, m.PresentedVideoFrames);
            var stillPaused = false;
            for (var i = 0; i < sn.Count && i < _sync.Slots.Count; i++)
                if (sn[i]?.State == PlayerState.Paused && _sync.Slots[i].Active && !_sync.Slots[i].Failed)
                    stillPaused = true;
            if (best >= frozenAt + 3 && m.State == PlayerState.Playing && !stillPaused) { woke = true; break; }
        }
        sw.Stop();
        if (!woke)
        {
            // 没触发就如实标 SKIP：这条路径要真验，得给停滞检测加一个注入旋钮（属另一批）。
            RecordSelfTestSkip(
                $"停滞唤醒：维持了 {fought} 拍 `_isPlaying=true`，8s 内 presented 仍停在 {best}（门槛 {frozenAt + 3}）" +
                "⇒ 看门狗没被触发，**本夹具没能把前提立起来**（不是恢复序列失败，产品未判）");
            return;
        }
        Log($"停滞唤醒：看门狗把整组唤醒，presented {frozenAt}→{best}（+{best - frozenAt}），" +
            $"用时 {sw.ElapsedMilliseconds}ms，期间把 _isPlaying 按回真 {fought} 次以维持前提");
    }

    /// <summary>#42 的受控探针：把真机上那一次触发（<c>漂移校正 路i 态=Playing Δ=-1590495 → Seek</c>，
    /// <see cref="SyncController"/> 的 1035 行）压成可重复的 5 轮实验。
    ///
    /// <para><b>问的问题只有一个</b>：从 <c>Playing</c> 出发的一次 Seek，会不会把这一路停在 <c>Paused</c>。
    /// 之所以要做"受控"，是因为 §11.8 的整轮 A/B 各 8 轮都撞不到冻结（0/8 + 0/8），再撞整轮只是掷骰子。</para>
    ///
    /// <para><b>为什么用"人为改 offset、让产品自己的校正器去 Seek"，而不是直接调 <c>Session.Seek()</c></b>：
    /// 校正器那一拍带着它的整段前置（<c>Active</c>、非 <c>Failed</c>、半帧阈值、<c>ClampToDuration</c>、
    /// 以及"Paused 刻意排除"这条被 multitest 依赖的语义）。绕过它们直接打 Seek，测的就不是同一条路径；
    /// 而照抄真机顺序正是这类夹具唯一能证伪假设的地方（见账本 §11.8 的教训）。</para>
    ///
    /// <para><b>牙在哪里</b>：判据前先跑一次<b>故意</b>的 <c>Pause()</c> 作为阳性对照 ——
    /// "停在 Paused"这个形状必须由本判据自己报出来，报不出来就是恒真判据，直接判失败。
    /// 这样即使 5 轮全绿，也说得清"这条判据今天确实能抓到那个形状"，而不是一把没转过的锁。</para>
    /// </summary>
    private async Task SeekParksRouteUnit()
    {
        const int Cycles = 5;
        const int PollMs = 50;
        const int WatchPolls = 40;          // 2s 观察窗
        const long Perturb = 1_000_0000;    // 1s，远大于半帧阈值 ⇒ 下一拍校正器必然发 Seek
        const long Converged = 1_500_0000;  // 150ms：只用来判"校正有没有落地"，不参与最终判决
        if (_sync.Count < 2)
        {
            Console.WriteLine($"  单路受控Seek：**跳过**（{_sync.Count} 路没有从路，漂移校正器只处理 i≥1 ⇒ 这条判据恒真）");
            RecordSelfTestSkip($"单路受控Seek：{_sync.Count} 路无从路 ⇒ 判据恒真");
            return;
        }
        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(200);

        const int lane = 1;
        var origOffset = _sync.Slots[lane].Offset100ns;
        var repro = new System.Collections.Generic.List<string>();
        var reproB = new System.Collections.Generic.List<string>();
        var reproC = new System.Collections.Generic.List<string>();
        var notes = new System.Collections.Generic.List<string>();
        string? phaseBSkipped = null;

        // 观察窗：返回 (窗口后半段是否一直停在 Paused 且位置不动, 呈现计数增量, 轨迹串)
        // ⚠ 必须 await，不能 .Wait()：本方法跑在 UI 线程上，阻塞它等于同时把
        //   `Dispatcher.UIThread.Post` 的引擎事件处理与呈现一起冻住 —— 那会变成夹具自己造出的假 stuck。
        async System.Threading.Tasks.Task<(bool stuck, long presented, string trace)> Watch(int which)
        {
            var first = _sync.ReadAllSnapshots()[which] ??
                        throw new InvalidOperationException($"受控Seek：观察窗起点第 {which} 路无快照");
            var seen = new System.Collections.Generic.List<(int i, PlayerState st, long pos)>();
            var trace = new System.Collections.Generic.List<string>();
            long posMin = long.MaxValue, posMax = long.MinValue;
            for (var i = 0; i < WatchPolls; i++)
            {
                await System.Threading.Tasks.Task.Delay(PollMs);
                var s = _sync.ReadAllSnapshots()[which] ??
                        throw new InvalidOperationException($"受控Seek：观察窗第 {i} 拍第 {which} 路无快照");
                seen.Add((i, s.State, s.Position100ns));
                posMin = Math.Min(posMin, s.Position100ns);
                posMax = Math.Max(posMax, s.Position100ns);
                if (i % 8 == 0 || i == WatchPolls - 1)
                    trace.Add($"{i}:{s.State}/{s.Position100ns / TimeSpan.TicksPerMillisecond}ms/p{s.PresentedVideoFrames}");
            }
            // 判决口径 = **窗口后半段全在 Paused 且位置不再前进**。
            // 不用"从第一拍起全部 Paused"：内核 Pause/Play 都是"守卫即时读状态、动作 Enqueue 后执行"，
            // 刚发完命令那一拍很可能还读到 Playing —— 实测阳性对照正是这样红过一次
            // (`0:Playing/0ms/p0 8:Paused/0ms/p1 …`)。旧口径会**把中途开始的冻结漏成绿灯**，
            // 那是往安全侧偏的错口径；新口径仍然有牙：短暂停了又起来 ⇒ 后半段不会全是 Paused。
            var tail = seen.Skip(seen.Count / 2);
            var pausedAtTail = tail.All(x => x.st == PlayerState.Paused);
            var frozen = posMax - posMin <= 2 * TimeSpan.TicksPerMillisecond;
            var after = _sync.ReadAllSnapshots()[which]!;
            var traceStr = string.Join(" ", trace) +
                $"（后半段 {seen.Count - seen.Count / 2} 拍全 Paused={pausedAtTail}、" +
                $"位置跨度 {(posMax - posMin) / TimeSpan.TicksPerMillisecond}ms）";
            // 再确认：#42 要抓的是**永久**冻住（现场是分钟级、偏差长到 6718ms），
            // 而"再启用后排空队列要 1~2 秒"是**已测到的正常慢**（16:50:58.617 补发起播成功 →
            // 16:51:00.211 才 `状态→Playing`，1.6s；5 路 4K 下更明显）。
            // 2s 窗口不足以区分这两者 —— 直接把"慢"判成缺陷是假阳性（实测 5 路时 B 2/5 就是这么红的）。
            // 所以窗口内看着像停住时，再等 4s 读一次：仍然 Paused 且位置没动才算 stuck。
            if (pausedAtTail && frozen)
            {
                await System.Threading.Tasks.Task.Delay(4000);
                var again = _sync.ReadAllSnapshots()[which]!;
                traceStr += $" ⇒ 再等 4s：态={again.State}、位置 " +
                            $"{again.Position100ns / TimeSpan.TicksPerMillisecond}ms" +
                            $"（窗口末 {after.Position100ns / TimeSpan.TicksPerMillisecond}ms）";
                var stillParked = again.State == PlayerState.Paused &&
                                  Math.Abs(again.Position100ns - after.Position100ns) <= 2 * TimeSpan.TicksPerMillisecond;
                return (stillParked, again.PresentedVideoFrames - first.PresentedVideoFrames, traceStr);
            }
            return (false, after.PresentedVideoFrames - first.PresentedVideoFrames, traceStr);
        }

        try
        {
            // ── 阳性对照（先证明这条判据有牙，再谈被测形状）────────────────────────
            // 故意把这一路按停：真机上"停在 Paused、位置不动"就是要抓的形状，本判据必须报 stuck。
            _sync.Play();
            await System.Threading.Tasks.Task.Delay(300);
            _sync.Slots[lane].Session.Pause();
            var ctl = await Watch(lane);
            if (!ctl.stuck)
                throw new InvalidOperationException(
                    $"受控Seek：阳性对照失败 —— 我**故意** Pause 了第 {lane} 路，2s 观察窗却没被判成" +
                    $"\"窗口后半段停在 Paused 且位置不动\"（轨迹 {ctl.trace}）⇒ 这条判据恒真，跑它没有任何意义");
            notes.Add($"阳性对照：故意 Pause ⇒ stuck=True、presented Δ={ctl.presented}（判据有牙 ✓）");

            for (var c = 0; c < Cycles; c++)
            {
                // 前置：master 与这一路都在播，且 master 位置够大（expected 才不会为负）
                _sync.Play();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                long masterPos = 0;
                PlayerState laneState = PlayerState.Closed;
                while (DateTime.UtcNow < deadline)
                {
                    var sn = _sync.ReadAllSnapshots();
                    masterPos = sn[0]?.Position100ns ?? 0;
                    laneState = sn[lane]?.State ?? PlayerState.Closed;
                    if (masterPos > 2 * Perturb && sn[0]?.State == PlayerState.Playing &&
                        laneState == PlayerState.Playing) break;
                    if (laneState != PlayerState.Playing) _sync.Play();
                    await System.Threading.Tasks.Task.Delay(150);
                }
                if (masterPos <= 2 * Perturb || laneState != PlayerState.Playing)
                    throw new InvalidOperationException(
                        $"受控Seek 第 {c} 轮前置取不到读数：master 位置 {masterPos / TimeSpan.TicksPerMillisecond}ms" +
                        $"（需 >{2 * Perturb / TimeSpan.TicksPerMillisecond}ms）、第 {lane} 路态={laneState} ⇒ " +
                        "这一轮的 Seek 根本不会被下发，判据无从观测，不能记成通过");

                // 人为把规范时间挪 1s：这一路立刻"落后/超前"半个帧阈值以上 ⇒ 校正器下一拍发 Seek
                _sync.Slots[lane].Offset100ns = origOffset - Perturb;
                var seeked = false;
                // 3s ≈ 3 个校正节拍（DriftCheckIntervalMs=1000ms），留出 Seek + 追帧的余量
                var corrDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (DateTime.UtcNow < corrDeadline)
                {
                    var sn = _sync.ReadAllSnapshots();
                    var expected = (sn[0]?.Position100ns ?? 0) + _sync.Slots[lane].Offset100ns;
                    if (sn[lane] is { } s1 && Math.Abs(s1.Position100ns - expected) <= Converged)
                    { seeked = true; break; }
                    await System.Threading.Tasks.Task.Delay(PollMs);
                }
                if (!seeked)
                    throw new InvalidOperationException(
                        $"受控Seek 第 {c} 轮没有等到校正：把 offset 挪 -1s 后 3s 内该路仍未回到规范时间" +
                        $"（态={_sync.ReadAllSnapshots()[lane]?.State}）⇒ 校正器没跑或这一路没被启用，" +
                        "本判据退化成\"等一个不会来的事件\"，不能记成通过");

                var w = await Watch(lane);
                if (w.stuck)
                    repro.Add($"第 {c} 轮：校正落地后 2s 内全程 Paused，轨迹 {w.trace}（presented Δ={w.presented}）");
                else if (w.presented <= 0)
                    notes.Add($"第 {c} 轮：态在播但 2s 内 presented 零增量，轨迹 {w.trace}（另一形状，登记）");
                else
                    notes.Add($"第 {c} 轮：Seek 后仍在播，presented Δ={w.presented}，{w.trace}");
                _sync.Slots[lane].Offset100ns = origOffset;
            }

            // ── 相位 B：真机 #42 的前置形状 —— **停用之后再启用**的那一路 ──────────────
            // 相位 A 只能证"从 Playing 出发的一次 Seek 不会停住"。而 §11.8 现场那一路是
            // **被 SetActiveRoutes 再启用**的（启用分支先 Seek 回规范时间，再由 WantPlaying 决定要不要 Play）。
            // 所以这一步直接问：再启用之后，它是不是停在 Paused 而 master 照常播 —— 这才是 #42 的形状。
            for (var c = 0; c < Cycles; c++)
            {
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(200);
                // 明确进**左右拉动**（恒两格）：3/4 路必然有未占格的路被停用。
                // 合并后的网格是一路一格、永远不会把路挤出去 ⇒ 只有拉动还能造出
                // "被停用 → 再启用"这个 #42 前置形状（原先这里写的是 AB 两格）。
                if (!EnterCompareOverlay())
                {
                    phaseBSkipped = $"第 {c} 轮起无法进入左右拉动（素材 {_sync.Count} 路）";
                    break;
                }
                await System.Threading.Tasks.Task.Delay(400);   // 占格集合是 Post 出去的，要落地
                var laneB = -1;
                for (var i = _sync.Count - 1; i >= 1; i--)
                    if (!_sync.Slots[i].Active) { laneB = i; break; }
                if (laneB < 0)
                {
                    phaseBSkipped = $"第 {c} 轮起就没有多余路可停用（{_sync.Count} 路在拉动两格下全占格）";
                    ExitCompareOverlay();
                    break;
                }
                ExitCompareOverlay();               // 再启用：回到网格后 SetActiveRoutes 的启用分支 Seek 回规范时间
                await System.Threading.Tasks.Task.Delay(400);
                if (!_sync.Slots[laneB].Active)
                    throw new InvalidOperationException(
                        $"受控Seek 相位B 第 {c} 轮前置失效：回到网格后第 {laneB} 路仍 Active=false ⇒ " +
                        "根本没发生“再启用”，这一枪恒真，不能记成通过");
                var wb = await Watch(laneB);
                var masterState = _sync.ReadAllSnapshots()[0]?.State;
                if (wb.stuck && masterState == PlayerState.Playing)
                    reproB.Add($"第 {c} 轮：再启用第 {laneB} 路后 2s 全程 Paused 而 master 在播（正是 #42），轨迹 {wb.trace}");
                else if (wb.stuck)
                    reproB.Add($"第 {c} 轮：再启用第 {laneB} 路后 2s 全程 Paused，但 master 态={masterState}（不是 #42 的形状）");
                else
                    notes.Add($"相位B 第 {c} 轮：再启用第 {laneB} 路后在播，presented Δ={wb.presented}，{wb.trace}");
            }
            // ── 相位 C：主动把"队列还没排空就又切回去"这个窗口撞开 ──────────────
            // 停用的 `Pause()` 与再启用的 `Play()` 都是**守卫即时读状态、动作排队执行**
            // （内核 PlayerSession::Play 只接受 Ready/Paused/Ended；托管 `Check()` 会把 InvalidState
            // 抛成 EngineException，被 SetActiveRoutes 的 catch 咽掉 ⇒ 这一次"要播"就**永久丢了**）。
            // 于是：先 SetActiveRoutes(前 2 路) 让各停用路排入 Pause，**不 await**，立刻切回全部路 ⇒
            // 再启用的 Play 调用时状态仍是 Playing ⇒ 被拒；随后那条 Pause 才落地
            // ⇒ 该路 Active=true、位置正确、却停在 Paused，而 `TickDriftCore` 刻意不校正 Paused ⇒ #42。
            // 相位 A/B 用的是"自然节奏"（中间有 400ms 让队列排空），撞不到这个窗口；这里专门撞它。
            for (var c = 0; c < Cycles; c++)
            {
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(200);
                var all = new System.Collections.Generic.HashSet<int>();
                for (var i = 0; i < _sync.Count; i++) all.Add(i);
                var firstTwo = new System.Collections.Generic.HashSet<int> { 0, 1 };
                _sync.SetActiveRoutes(firstTwo);      // 第 2/3 路排入 Pause
                _sync.SetActiveRoutes(all);           // 立刻切回：中间不 await ⇒ Play 可能撞上还在 Playing
                await System.Threading.Tasks.Task.Delay(2500);
                var sn = _sync.ReadAllSnapshots();
                var parked = new System.Collections.Generic.List<string>();
                for (var i = 1; i < sn.Count && i < _sync.Slots.Count; i++)
                    if (_sync.Slots[i].Active && !_sync.Slots[i].Failed && sn[i]?.State == PlayerState.Paused)
                        parked.Add($"路{i}={sn[i]!.Position100ns / TimeSpan.TicksPerMillisecond}ms");
                if (parked.Count > 0)
                {
                    await System.Threading.Tasks.Task.Delay(2000);   // 再等 2s：能不能自己醒过来？
                    var sn2 = _sync.ReadAllSnapshots();
                    var still = parked.Find(p =>
                    {
                        var idx = int.Parse(p.Substring(1, p.IndexOf('=') - 1));
                        return sn2[idx]?.State == PlayerState.Paused;
                    });
                    var masterState = sn2[0]?.State;
                    if (still is not null && masterState == PlayerState.Playing)
                        reproC.Add($"第 {c} 轮：连发两次切换后 {string.Join("、", parked)} 停在 Paused，" +
                                   $"再等 2s 仍未醒（master={masterState}）⇒ **正是 #42 的形状**");
                    else
                        notes.Add($"相位C 第 {c} 轮：{string.Join("、", parked)} 曾停在 Paused，" +
                                  $"2s 后={still ?? "全部醒了"}（master={masterState}）");
                }
            }
        }
        finally
        {
            _sync.Slots[lane].Offset100ns = origOffset;
            // 相位 C 会绕过网格直接改启用集合，收尾必须把口径交还给 UI（否则后面的单元看到的是错位状态）
            _sync.SetActiveRoutes(Grid.VisibleRouteIndices);
            _sync.Play();
        }

        foreach (var n in notes) Console.WriteLine($"  单路受控Seek：{n}");
        if (phaseBSkipped is not null)
            RecordSelfTestSkip($"单路受控Seek 相位B：{phaseBSkipped} ⇒ 该相位恒真，只判了相位A");
        if (repro.Count + reproB.Count + reproC.Count > 0)
            throw new InvalidOperationException(
                $"#42 复现：相位A {repro.Count}/{Cycles} 轮、相位B {reproB.Count}/{Cycles} 轮、" +
                $"相位C {reproC.Count}/{Cycles} 轮出现\"停在 Paused\"，" +
                $"A 样例：{(repro.Count > 0 ? repro[0] : "无")}；B 样例：{(reproB.Count > 0 ? reproB[0] : "无")}；" +
                $"C 样例：{(reproC.Count > 0 ? reproC[0] : "无")}。" +
                $"而 master 照常播放 ⇒ 那一格永久冻住，且 TickDriftCore 刻意排除 Paused 所以没有任何机制会恢复它。" +
                "归因线索：app 日志里这一路紧邻的 `状态→Paused（内核操作=…）` 就是写下这个状态的调用点，" +
                "以及 `启用第 N 路` 的 ReportRuntimeError（InvalidState 被 SetActiveRoutes 的 catch 咽掉）" +
                "（见 PlaybackCoordinator.RecordStateTransition / 账本 §11.10）。");
        Log($"单路受控Seek：相位A（从 Playing 出发的一次校正 Seek）{Cycles}/{Cycles} 轮、" +
            $"相位B（停用后再启用那一路）{(phaseBSkipped is null ? Cycles + "/" + Cycles + " 轮" : "未跑")}、" +
            $"相位C（两次切换之间不排队，主动撞守卫窗口）{Cycles}/{Cycles} 轮都回到播放态" +
            "（阳性对照已证明本判据能抓到 stuck 形状）⇒ 三条假设在受控形状下**均未复现**；" +
            "真机 #42 是偶发（整轮 16 次 A/B 也 0 复现），本探针只能否证\"每次都停\"，不能证明它不存在。");
    }

    /// <summary>P6 单元：「网格」入口必须退出对比模式，并把 2~9 路全部恢复成均匀网格。
    /// <para>自带前置（先进分屏再切网格），所以能单跑；排版期望值由 Core 纯函数
    /// <c>GridLayout.ComputeGrid</c> <b>独立复算</b>，不读 Grid 的内部预设状态。</para></summary>
    private async Task GridEntryUnit(int routes)
    {
        EnterGridCompare();
        await System.Threading.Tasks.Task.Delay(150);
        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(150);

        if (_compareActive || CompareOverlayActive)
            throw new InvalidOperationException("网格入口后仍处于对比 / 叠加模式");
        if (Grid.CellOverride is not null)
            throw new InvalidOperationException("网格入口后 Grid.CellOverride 未清空（均匀网格未恢复）");
        UpdateLayout();

        var (gCols, gRows) = GridLayout.ComputeGrid(routes, singleView: false);
        var gw = Grid.Bounds.Width;
        var gh = Grid.Bounds.Height;
        if (gw <= 0 || gh <= 0)
            throw new InvalidOperationException($"网格入口：对比区尺寸非法 {gw:F1}x{gh:F1}");
        var gcw = gw / gCols;
        var gch = gh / gRows;
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"网格入口：第 {i} 路缺失");
            if (!s.IsVisible)
                throw new InvalidOperationException(
                    $"网格入口：第 {i} 路仍不可见（{routes} 路未全部恢复显示）");
            // 与 CompareGridView.ArrangeOverride 的均匀路径同构：留 1px 缝隙、并钳到 0
            var exp = new Rect(i % gCols * gcw + 1, i / gCols * gch + 1,
                Math.Max(0, gcw - 2), Math.Max(0, gch - 2));
            if (!BoundsClose(s.Bounds, exp))
                throw new InvalidOperationException(
                    $"网格入口：第 {i} 路 Bounds={s.Bounds} 与 GridLayout.ComputeGrid({routes})=" +
                    $"{gCols}x{gRows} 的期望 {exp} 不符（均匀网格未恢复）");
        }
        AssertCompareCropCleared(routes, "网格入口");
        Console.WriteLine(
            $"  网格入口 ✓ {routes} 路全部恢复可见，排版与 GridLayout.ComputeGrid({routes})=" +
            $"{gCols}x{gRows} 独立复算一致");
    }

    /// <summary>「视图模式」一列：菜单列 ↔ 真实状态 ↔ 真实键盘路由，三者必须同步。
    ///
    /// <para><b>合并后这一列只有两项</b>：网格对比（G / V，一路一格 + 可拖分割线）与左右拉动（S）。
    /// 旧的第三项「A/B 可拖动对比」已并进第一项，所以这里同时钉住"第三项确实不在了"——
    /// 谁要是把它加回来，同组互斥变成三项，本用例的项数与勾选元组判据会立刻红。</para>
    ///
    /// <para><b>牙在哪</b>：① 按键一律走 <see cref="RaiseKeyOn"/> 的真实路由（隧道→目标→冒泡），
    /// 于是"菜单项的 <c>InputGesture</c> 被菜单处理器又执行一次"这类双触发会当场露出来 ——
    /// 每按一次状态必须<b>恰好翻转一次</b>（G 与 S 都是开关语义，按两次必回原态）；
    /// ② 勾选只读 <c>MenuItem.IsChecked</c>，判据不与 <see cref="RefreshCompareModeChecks"/> 同源复制；
    /// ③ 两项必须同父、同组、同为 Radio，且标注的键位与真实路由一致；
    /// ④ 未进入任何对比态时<b>两处都不打勾</b>：均匀网格是真实存在的中性态，
    /// 给它随便挂个勾就是在谎报模式（旧三项时代"其余即标准模式"的兜底已不适用）。</para></summary>
    private async Task ViewModeColumnUnit(int routes)
    {
        if (routes < 2)
            throw new InvalidOperationException($"视图模式列：{routes} 路不足以进入对比，宿主应按 2 路起盘");

        var two = new[] { MenuViewStandard, MenuViewWipe };
        var names = new[] { "网格对比", "左右拉动" };
        for (var i = 0; i < two.Length; i++)
        {
            if (two[i].ToggleType != global::Avalonia.Controls.MenuItemToggleType.Radio)
                throw new InvalidOperationException($"「{names[i]}」不是 Radio 型，画不出互斥单选圆点");
            if (two[i].GroupName != "ViewMode")
                throw new InvalidOperationException($"「{names[i]}」未落进 ViewMode 组（实际 {two[i].GroupName}）");
            if (two[i].Parent is null || !ReferenceEquals(two[i].Parent, two[0].Parent))
                throw new InvalidOperationException($"「{names[i]}」与另一项不同父，'一列'不成立");
        }

        // 菜单右侧标的键位必须就是真正路由到的那个键：标 G 却按了不生效（或反过来）
        // 都是"看着能按"的假入口，比没有入口更坏。
        if (two[0].InputGesture is not { Key: global::Avalonia.Input.Key.G })
            throw new InvalidOperationException(
                $"网格对比项标注的快捷键={two[0].InputGesture?.Key}，与真实路由的 G 不一致");
        if (two[1].InputGesture is not { Key: global::Avalonia.Input.Key.S })
            throw new InvalidOperationException(
                $"左右拉动项标注的快捷键={two[1].InputGesture?.Key}，与真实路由的 S 不一致");

        void ExpectChecked(string expect)
        {
            RefreshCompareModeChecks();
            var real = _compareOverlayActive ? "wipe" : _compareActive ? "grid" : "std";
            if (real != expect)
                throw new InvalidOperationException($"状态未切到 {expect}（实际 {real}）");
            var got = (two[0].IsChecked, two[1].IsChecked);
            var want = expect switch
            {
                "std" => (false, false),   // 中性态：两项都不勾，理由见方法注释 ④
                "grid" => (true, false),
                _ => (false, true),
            };
            if (got != want)
                throw new InvalidOperationException(
                    $"切到 {expect} 后勾选=网格{got.Item1} 拉动{got.Item2}，期望 {want}");
        }

        // 起点：均匀网格（ExitGridCompare 不在态时不动状态，与 G 的开关语义刻意不同）
        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("std");

        // V = 进入网格对比（合并后它是同一态的进入别名，不再是第二个态）
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.V);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("grid");

        // S = 左右拉动；再按 S 退出拉动，落点必须是**网格对比**而不是均匀网格
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.S);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("wipe");
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.S);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("grid");

        // G = 开关：先退出到均匀网格，再按一次回到网格对比
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.G);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("std");
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.G);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("grid");

        Log($"视图模式列 ✓ 两项同父互斥、键位标注与路由一致，G/V/S 真实路由逐次恰好翻转一次（{routes} 路）");
    }

    /// <summary>分割线的拖动命中区：网格里的<b>每一条线都是单轴线</b>，于是"沿轴放开到整条线"
    /// 适用于全部手柄 —— 竖线不论高低都能抓，横线不论左右都能抓。
    ///
    /// <para><b>为什么钉这个</b>：用户对"左右拉动对比"的说法是"<b>中间的线可以拉动</b>"。
    /// 判据改成整条线之前只有线正中间那个圆点（半径 14 DIP）能抓，窗口越高"瞄不准"越明显，
    /// 而这条差别<b>肉眼看不出来</b>（线画在那儿、圆点也画在那儿）。
    /// 合并后连"交叉点型手柄"也不存在了（2×2 的两条线各自单轴、各自一个手柄），
    /// 所以旧那句"同一条线的顶端不得命中"<b>反过来</b>成了本用例的判据：
    /// 竖线顶端必须命中竖线那个手柄，同时不能顺手把横线也拽走。</para>
    ///
    /// <para><b>还要拦住过宽</b>：离线 1/4 宽处必须不命中，否则整幅画面都成了拖拽区，
    /// 滚轮缩放与拖动平移会被覆盖层吞掉。</para>
    ///
    /// <para>读的是覆盖层自己的探针 <see cref="Controls.LayoutOverlayWindow.HitHandleIndexAt"/>，
    /// 也就是真实点击走的那一条判据（DIP 路径），不复制几何。</para></summary>
    private async Task SplitLineHitUnit(int routes)
    {
        // 形状按 lane 数枚（判据只关几何，与素材路数是两件事）：这样 2 条 lane 也能跑，
        // 不再需要旧实现那句"routes<3 就登记跳过"——那一句在门禁里永远算一次 SKIP。
        var lanes = Grid.Count;
        var shapes = GridPartition.ShapesFor(lanes);
        Console.WriteLine($"  分割线命中 覆盖范围（{routes} 路 / {lanes} 条 lane）：[" +
                          $"{string.Join(", ", shapes.Select(s => $"{s.Cols}x{s.Rows}"))}]");

        foreach (var (cols, rows) in shapes)
        {
            await EnterGridShapeAsync(cols, rows);
            var ov = _layoutOverlay ?? throw new InvalidOperationException("分割线命中：覆盖层未创建");
            if (!ov.IsOverlayVisible) throw new InvalidOperationException("分割线命中：覆盖层不可见");
            var w = ov.Bounds.Width;
            var h = ov.Bounds.Height;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException($"分割线命中：覆盖层尺寸非法 {w:F1}x{h:F1}");

            var handles = ov.HandlePositions();
            var expect = (cols - 1) + (rows - 1);
            if (handles.Length != expect)
                throw new InvalidOperationException($"{cols}x{rows} 手柄数 {handles.Length} ≠ 规则要求 {expect}");

            // 每条线在自己的轴上不论多远都能抓：竖线验顶端与底端，横线验左端与右端。
            var probes = new System.Collections.Generic.List<string>();
            for (var i = 0; i < handles.Length; i++)
            {
                var vertical = _partition.HandleAxisAt(i) == SplitAxis.X;
                var near = new Point(vertical ? handles[i].X * w : w * 0.05,
                                     vertical ? h * 0.05 : handles[i].Y * h);
                var far = new Point(vertical ? handles[i].X * w : w * 0.95,
                                    vertical ? h * 0.95 : handles[i].Y * h);
                var a = ov.HitHandleIndexAt(near);
                var b = ov.HitHandleIndexAt(far);
                if (a != i || b != i)
                    throw new InvalidOperationException(
                        $"{cols}x{rows} 第 {i} 条{(vertical ? "竖" : "横")}线没放开到整条线：" +
                        $"两端命中={a}/{b}（都应={i}）");
                probes.Add($"{(vertical ? "竖" : "横")}{i}@{(vertical ? handles[i].X : handles[i].Y):0.###}");
            }

            // 反面判据：离所有线都远的地方必须不命中，否则整幅画面成了拖拽区，
            // 滚轮缩放与拖动平移会被覆盖层吞掉。权重下界保证最靠边的线也在 5% 之外，
            // 于是 (0.02,0.02) 到最近那条线是 0.03·边长；对比区太窄时这个距离不足一个命中半径
            // ⇒ 那一支明写跳过，而不是悄悄不判。
            if (w * 0.03 <= 20 || h * 0.03 <= 20)
            {
                Console.WriteLine($"  分割线命中 {cols}x{rows}：负样本**跳过**（对比区 {w:F0}x{h:F0} DIP 太窄，" +
                                  "0.03·边长已小于命中半径，分不出「放开」与「过宽」）");
                RecordSelfTestSkip($"分割线命中 {cols}x{rows}：对比区过窄，负样本无鉴别力");
            }
            else
            {
                var miss = ov.HitHandleIndexAt(new Point(w * 0.02, h * 0.02));
                if (miss != -1)
                    throw new InvalidOperationException(
                        $"{cols}x{rows}：左上角离所有线都有 0.03 边长，却仍命中手柄 {miss}" +
                        "（命中区过宽，会吞掉画面上的滚轮与平移）");
            }

            // 线画在哪 == 拖动判据用哪一份：不同源时表现为"线在这儿、抓住却拖不动"
            var host = _partition.HandlePositions();
            for (var i = 0; i < handles.Length; i++)
                if (Math.Abs(handles[i].X - host[i].X) > GridPartition.Epsilon
                    || Math.Abs(handles[i].Y - host[i].Y) > GridPartition.Epsilon)
                    throw new InvalidOperationException(
                        $"{cols}x{rows} 覆盖层手柄 {i}={handles[i]} 与宿主分区 {host[i]} 不同源");

            Console.WriteLine($"  分割线命中 ✓ {cols}x{rows}：整条可抓 [{string.Join(", ", probes)}]");
        }
    }

    /// <summary>拼合（mosaic）语义的实机验收：<b>各路窗口铺满对比区，可见部分由 Win32 区域裁出来，
    /// 而裁出的这些块必须严丝合缝地拼回一整幅画面</b>。</summary>
    ///
    /// <para><b>为什么单独钉这一条</b>：这是本模式被纠正后的语义本身。旧实现排的是"窗口 == 格"，
    /// 于是每格显示的是<b>整幅画面的缩小版</b>，拖分割线只是在改每格的尺寸 —— 用户要的是
    /// "一个内容、不同区域渲染它的不同部分"。纠正后：窗口 == 整容器，"哪一块归哪一路"完全由
    /// <c>SetWindowRgn</c> 决定 ⇒ 一旦区域算错（留缝、重叠、越界），表现就是拼合处出现黑线、
    /// 两块画面叠在一起、或某一路盖住邻居。<b>肉眼在真机上很难看出 1px 的缝</b>，所以必须回读。</para>
    ///
    /// <para>判据全部走 Win32 回读（<c>GetWindowRgnBox</c>）+ 从分区<b>独立复算</b>的期望值，
    /// 不读生产代码的内部状态：四条各自对应一种坏法 ——
    /// ① 每路的区域 == 它那一格（算错块 ⇒ 画面错位）；
    /// ② 各块两两不重叠且并集 == 整容器（留缝 ⇒ 黑线；重叠 ⇒ 一路盖住另一路）；
    /// ③ 每块都严格小于窗口（等于窗口 ⇒ 该路根本没裁，会盖住全部邻居）；
    /// ④ 每格中心按生产入口 <c>HitSurfaceAt</c> 命中的正是占这一格的那一路
    ///    （漏了这条的话放大镜/滚轮锚点/指针映射会在拼合下恒读第 0 路，而画面看不出来）。</para>
    private void AssertMosaicTiling(int routes, string label)
    {
        UpdateLayout();
        ApplyCompareCrop();

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"{label}：拼合判据要求对比区尺寸有效，实际 {w:F1}x{h:F1}");
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = _partition.ComputeCells();
        if (cells.Length < 2)
            throw new InvalidOperationException(
                $"{label}：拼合判据需要 ≥2 块才有鉴别力（单块时无缝性恒成立），当前形状 {_partition.Cols}x{_partition.Rows}");
        if (!Grid.MosaicWindows)
        {
            // 对照臂（FC_NOMOSAIC=1）本来就是来量"关掉拼合会怎样"的 ⇒ 这条拼合专属判据**明写不适用**，
            // 不悄悄通过也不判红。其余判据各自按 Grid.MosaicWindows 换口径（Bounds / 区域回读）。
            if (BenchNoMosaic)
            {
                Console.WriteLine($"  拼合铺块 不适用（{label}：FC_NOMOSAIC=1 已把排布退回『窗口 == 格』，" +
                                  "本判据的对象不存在；对照臂要的是崩溃率与每拍像素，不是这条）");
                return;
            }
            throw new InvalidOperationException($"{label}：网格对比态应当处于拼合排布（Grid.MosaicWindows=false）");
        }

        // 回读各路实际区域（窗口相对物理像素），换算到容器坐标。
        // **按路循环 + 反查格**：互换 / 轮换之后"第 i 路"不再站第 i 格，按格号取第 i 路的
        // 区域会把判据钉在别人的块上（2026-10-01 实读判红：2x3 下第 0 路 Y=712 vs 期望 0）。
        var boxes = new System.Collections.Generic.List<Rect32>(routes);
        double covered = 0;
        for (var i = 0; i < routes; i++)
        {
            var cell = CellOfRoute(i, cells, label);
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");
            if (s.Hwnd == nint.Zero)
                throw new InvalidOperationException($"{label}：第 {i} 路子 HWND 未创建，区域无从回读");
            var type = ReadWindowRegion(s.Hwnd, out var box);
            if (type != RegionTypeSimple)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路的窗口区域不是单一矩形（type={type}；0=无区域、3=复杂区域）" +
                    "⇒ 要么根本没裁（会盖住整个对比区），要么裁成了非矩形");

            var windowPx = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
            // ③ 每块必须严格小于窗口：等于窗口就是"根本没裁"
            if (box.Width >= windowPx.Width && box.Height >= windowPx.Height)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路区域 {box} 不小于窗口 {windowPx} ⇒ 这一路铺满全屏、盖住邻居");

            // ① 与独立复算的格一致（容器坐标下逐像素比对，容差 1px = 取整残差）
            var exp = CompareCropPlanner.ToPhysicalRect(
                CompareCropPlanner.MosaicRegionDip(cell, w, h), scaling);
            var gotInContainer = new Rect32(windowPx.X + box.X, windowPx.Y + box.Y, box.Width, box.Height);
            if (Math.Abs(gotInContainer.X - exp.X) > 1 || Math.Abs(gotInContainer.Y - exp.Y) > 1 ||
                Math.Abs(gotInContainer.Width - exp.Width) > 1 || Math.Abs(gotInContainer.Height - exp.Height) > 1)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路回读区域 {gotInContainer}（容器坐标）与期望 {exp}" +
                    $"（它那一格是 {_partition.Cols}x{_partition.Rows} 的第 {Grid.CellIndexOfRoute(i)} 格）不符" +
                    $"（窗口 {windowPx}，缩放 {scaling}）");
            // 窗口本身应当铺满对比区（拼合排布的定义）
            var expWin = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, w, h), scaling);
            if (Math.Abs(windowPx.Width - expWin.Width) > 1 || Math.Abs(windowPx.Height - expWin.Height) > 1)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路窗口 {windowPx} 没铺满对比区 {expWin} ⇒ 拼合排布没生效，" +
                    "这一路显示的还是「缩小版整幅」而不是自己那一块");
            boxes.Add(gotInContainer);
            covered += cell.Width * cell.Height;
        }

        // ④ 命中测试必须跟着格走。拼合下各路窗口恒等于整对比区 ⇒ 任何"按 Bounds 命中"的旧路径
        //    都会**恒返回第一个可见表面（第 0 路）**：画面上完全看不出来，但放大镜、滚轮锚点、
        //    指针→源像素映射会一路都读第 0 路。这里对每一格取中心点，走生产同一个 HitSurfaceAt，
        //    断言拿到的正是占这一格的那一路（互换/轮换之后仍然是"占这格的那一路"）。
        for (var c = 0; c < cells.Length; c++)
        {
            var routeAtCell = Grid.RouteIndexOfCell(c);
            if (routeAtCell < 0 || routeAtCell >= routes) continue;
            var occ = Grid.GetSurface(routeAtCell);
            if (occ is null || !occ.IsVisible) continue;
            var block = CompareCropPlanner.MosaicRegionDip(cells[c], w, h);
            if (block.Width <= 0 || block.Height <= 0) continue;   // 零面积的格没中心可取
            var center = Grid.TranslatePoint(
                new Point(block.X + block.Width / 2, block.Y + block.Height / 2), this)
                ?? throw new InvalidOperationException(
                    $"{label}：第 {c} 格中心换算到窗口坐标失败（视觉树还没挂好？命中判据无法执行）");
            var hit = HitSurfaceAt(center);
            if (!ReferenceEquals(hit, occ))
                throw new InvalidOperationException(
                    $"{label}：第 {c} 格中心 ({center.X:F1},{center.Y:F1}) 应命中第 {routeAtCell} 路，实际" +
                    (hit is null ? "谁都没命中" : $"命中第 {hit.Index} 路") +
                    " ⇒ 命中测试还在按 Bounds 比（拼合下各路 Bounds 全等 ⇒ 恒返回第一个可见表面）");
        }

        // ⑤ 滚轮的退化锚必须落在"这一路自己那一块"的中心，而不是整窗（=整对比区）中心。
        //    拼合下各路窗口恒等于整对比区 ⇒ `Bounds.Center` 对所有路都是同一个点（组合画面中心），
        //    光标在哪个格都朝那一个点缩放。这里只把「选哪个点」这一半交给独立参照（上面的
        //    `MosaicRegionDip` 格表），映射算术仍走生产同一个 MapPointerToVideoFraction ——
        //    被钉的是点选择，映射本身另有单路九宫格判据。
        var anchorWorst = 0d;
        var anchorChecked = 0;
        var anchorDiscriminating = false;
        for (var c = 0; c < cells.Length; c++)
        {
            var anchorRoute = Grid.RouteIndexOfCell(c);
            if (anchorRoute < 0 || anchorRoute >= routes) continue;
            var surf = Grid.GetSurface(anchorRoute);
            if (surf is null || !surf.IsVisible) continue;
            if (_sync.Slots.ElementAtOrDefault(anchorRoute)?.Session is not { } sess) continue;
            var blk = CompareCropPlanner.MosaicRegionDip(cells[c], w, h);
            if (blk.Width <= 0 || blk.Height <= 0) continue;
            var blockCenter = new Point(blk.X + blk.Width / 2, blk.Y + blk.Height / 2);   // 拼合：窗口 == 容器
            if (MapPointerToVideoFraction(surf, sess, blockCenter) is not { } expect) continue;  // 整格在 letterbox 里 ⇒ 本就没有锚
            var actual = VisibleCenterFractions(surf);
            if (actual is null)
                throw new InvalidOperationException(
                    $"{label}：第 {anchorRoute} 路的格中心 ({blk.X + blk.Width / 2:F1},{blk.Y + blk.Height / 2:F1})DIP " +
                    "明明能映射到源画面，但 VisibleCenterFractions 返回 null ⇒ 退化锚整条不可用（滚轮落在黑边上就会乱跳）");
            anchorWorst = Math.Max(anchorWorst, Math.Max(
                Math.Abs(actual.Value.U - expect.U), Math.Abs(actual.Value.V - expect.V)));
            anchorChecked++;
            // 夹具自检：本形状下「整窗中心」这个旧口径与格中心差得够不够远。差得太小说明这一格
            // 本来就接近对比区中心，两条候选都会通过 ⇒ 该形状没有区分力，不许拿它当"验过了"。
            var naiveOk = MapPointerToVideoFraction(surf, sess, new Point(w / 2, h / 2)) is { } nb &&
                Math.Max(Math.Abs(nb.U - expect.U), Math.Abs(nb.V - expect.V)) > 0.02;
            anchorDiscriminating |= naiveOk || MapPointerToVideoFraction(
                surf, sess, new Point(w / 2, h / 2)) is null;   // 旧口径落到黑边 ⇒ 连值都取不出，也是一条差异
        }
        if (anchorChecked == 0)
            Console.WriteLine($"  滚轮退化锚 不适用：{label} 下没有一格既有会话又能映射到源画面");
        else if (!anchorDiscriminating)
            Console.WriteLine($"  滚轮退化锚 不适用：{label} 下「格中心」与「整窗中心」两个候选相差 <2% ⇒ 本形状无区分力");
        else if (anchorWorst > 2e-3)
            throw new InvalidOperationException(
                $"{label}：滚轮退化锚不跟本路那一块 —— {anchorChecked} 路里最大偏差 {anchorWorst:F4}（源归一化坐标）" +
                "⇒ 锚点仍取整窗/对比区中心：拼合下各路窗口恒等于整对比区，所有路共用同一个锚，" +
                "光标落在哪个格都朝那一个点缩放");
        else
            Console.WriteLine($"  滚轮退化锚 ✓ {anchorChecked} 路：锚 == 本格中心的源坐标（最大偏差 {anchorWorst:F6}；" +
                "本形状下旧口径「整窗中心」与之相差 >2%，判据有区分力）");

        // ⑥ 放大镜/探针"这一次算哪一路"必须以位置为准。旧实现走视觉树（e.Source），而拼合下
        //    各路 Bounds 全等且互相重叠 ⇒ 视觉树恒给同一顶层，与光标落在哪一格无关。
        //    夹具刻意把 source 喂成"视觉树最顶的那一个"（就是旧实现会给的答案），只有不看 source、
        //    按位置换算才可能判对 ⇒ 与④方向相反：④ 证明位置能定路，⑥ 证明 source 骗不过它。
        var laneChecked = 0;
        var laneDiscriminating = false;
        for (var c = 0; c < cells.Length; c++)
        {
            var laneRoute = Grid.RouteIndexOfCell(c);
            if (laneRoute < 0 || laneRoute >= routes) continue;
            var occ = Grid.GetSurface(laneRoute);
            if (occ is null || !occ.IsVisible) continue;
            var blk6 = CompareCropPlanner.MosaicRegionDip(cells[c], w, h);
            if (blk6.Width <= 0 || blk6.Height <= 0) continue;
            var pos6 = Grid.TranslatePoint(
                new Point(blk6.X + blk6.Width / 2, blk6.Y + blk6.Height / 2), this)
                ?? throw new InvalidOperationException(
                    $"{label}：第 {c} 格中心换算到窗口坐标失败，放大镜路归属判据无法执行");
            PlayerSurface? topmost = null;
            foreach (var cand in Grid.Surfaces) { if (cand.IsVisible) { topmost = cand; break; } }
            if (topmost is null) continue;
            if (!ReferenceEquals(topmost, occ)) laneDiscriminating = true;
            var got = LaneForPointerOverlay(topmost, pos6);
            if (!ReferenceEquals(got, occ))
                throw new InvalidOperationException(
                    $"{label}：第 {c} 格中心 ({pos6.X:F1},{pos6.Y:F1}) 应算在第 {laneRoute} 路头上，实际算到" +
                    (got is null ? "谁都没算" : $"第 {got.Index} 路") +
                    $"（喂给它的 source 是第 {topmost.Index} 路）⇒ 放大镜/探针还在信视觉树：" +
                    "拼合下各路 Bounds 全等且重叠，视觉树恒给同一顶层");
            laneChecked++;
        }

        // ⑥b 镜像断言：位置落在所有格之外（对比区之外）时**必须退回 source**，而不是返回 null。
        //     没有这一条，"位置优先"写成一意孤行的 return HitSurfaceAt(...) 也能过⑥ ——
        //     非对比态下面板留白里的移动就整个没有归属（放大镜会突然不跟）。
        var outsideDip = Grid.TranslatePoint(new Point(0, 0), this) is { } gridOrigin
            ? new Point(gridOrigin.X + w + 500, gridOrigin.Y + h + 500)
            : default;
        if (laneChecked > 0 && Grid.Surfaces.FirstOrDefault(s => s.IsVisible) is { } someLane)
        {
            if (LaneForPointerOverlay(someLane, outsideDip) is not { } fell)
                throw new InvalidOperationException(
                    $"{label}：位置落在对比区之外 ({outsideDip.X:F0},{outsideDip.Y:F0}) 时" +
                    "LaneForPointerOverlay 返回 null ⇒ 只剩「位置命中才算数」，视觉树那条退路被删了");
            if (!ReferenceEquals(fell, someLane))
                throw new InvalidOperationException(
                    $"{label}：对比区之外的位置应退回喂进去的 source（第 {someLane.Index} 路），" +
                    $"实际给第 {fell.Index} 路");
        }

        if (laneChecked == 0)
            Console.WriteLine($"  放大镜路归属 不适用：{label} 下没有可判定的格");
        else if (!laneDiscriminating)
            Console.WriteLine($"  放大镜路归属 不适用：{label} 下「视觉树最顶的那一路」恰好就是每格的占路 ⇒ 本形状区分不出两种实现");
        else
            Console.WriteLine($"  放大镜路归属 ✓ {laneChecked} 格：按位置定路（source 故意喂错那一路也扭不回来），" +
                "且对比区之外如期退回视觉树");

        // ② 两两不重叠 + 覆盖面积 == 分区算出来的那份
        // 期望值用<b>归一化占比 × 整幅面积</b>独立复算（不是把每格矩形再加一遍，那样与判据①同源）：
        // 路数 < 容量时（3 路用 2×2）本来就只有 3 格有内容，第 4 格是空的 ——
        // 拿"整幅"当期望会把这种正常状态读成缺陷（2026-10-01 第一次跑就是这样红的：
        // 回读 1,847,106 vs 整幅 2,462,808，比值正好 0.75 = 3/4 格）。
        var areaPx = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, w, h), scaling);
        long areaTotal = (long)areaPx.Width * areaPx.Height;
        long unionArea = 0;
        foreach (var b in boxes) unionArea += (long)b.Width * b.Height;
        // 期望覆盖率 = 各路**所站那一格**的归一化占比之和（在上面的按路循环里累加，来源是分区
        // 复算而非回读 ⇒ 与本判据不同源）。路数 < 格容量时（3 路用 2×2）它本就小于 1，
        // 空格没有内容，拿"整幅"当期望会把正常状态读成缺陷（第一次跑正是这样红的）。
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var ov = CompareCropPlanner.Intersect(ToDip(boxes[i]), ToDip(boxes[j]));
                // 判"二维重叠"必须两个维度同时看：相邻两块<b>共边</b>是正确状态，
                // 此时交集是 0 宽 × 满高（或满宽 × 0 高）—— 用 || 会把共边误判成重叠
                // （本判据第一次跑就是这样红的，回读数据显示两块其实严丝合缝：0..1153 与 1153..2306）。
                if (ov.Width > 1 && ov.Height > 1)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 块 {boxes[i]} 与第 {j} 块 {boxes[j]} 二维重叠 " +
                        $"{ov.Width}x{ov.Height}px ⇒ 两块画面叠在一起（Z 序决定谁露出来），拼合不成立");
            }
        // 面积和与"分区应覆盖的那部分"之差 ≤ 每块 2px 的取整残差（N 块各边最多各差 1px）
        var expectedCoverage = (long)(areaTotal * covered);
        var slack = 2L * (boxes.Count + 1) * (areaPx.Width + areaPx.Height);
        if (Math.Abs(unionArea - expectedCoverage) > slack)
            throw new InvalidOperationException(
                $"{label}：各块面积之和 {unionArea} 与分区期望 {expectedCoverage}" +
                $"（覆盖率 {covered:0.####} × 整幅 {areaTotal}）相差 {unionArea - expectedCoverage}，" +
                $"容许取整残差 {slack} ⇒ 拼合处有缝、有洞，或有块被裁错尺寸");

        Console.WriteLine(
            $"  拼合铺块 ✓ {label}：{boxes.Count} 块（首块 {boxes[0].Width}x{boxes[0].Height}px）" +
            $"窗口均铺满对比区 {areaPx.Width}x{areaPx.Height}，两两不重叠、面积和 {unionArea} " +
            $"= 分区期望 {expectedCoverage}（覆盖率 {covered:0.####}）");
    }

    private static Rect ToDip(Rect32 r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>M4：覆盖层<b>拖动热路径</b>的三条判据。此前的验收全部直接调宿主回调
    /// （<c>OnPartitionChanged</c> / <c>SplitRelayoutUnitAsync</c>），也就是**绕开了覆盖层自己那段
    /// 合并提交 / 结算 / 回灌**——那一段今天没有一条测量，改坏了没人报警。
    ///
    /// <list type="number">
    /// <item><description><b>合并比</b>：一次手势里「真的改变几何的 move 数」必须显著大于
    /// 「发给宿主的次数」。宿主每次提交要重排最多 9 路子 HWND + 一轮裁剪派发 + 组件日志，
    /// 高回报率鼠标每 move 都戳一遍（125+/s）不该由用户手感买单。</description></item>
    /// <item><description><b>拖动中宿主回灌不得把线拽回</b>：宿主收到提交后立刻把<b>同一份滞后值</b>
    /// 回灌（OnPartitionChanged → Update），覆盖层若照写，线每提交一次被回退一格 ⇒ 拖动抖动。
    /// 欠账同时必须还在（被清掉等于松手后宿主停在旧位置）。</description></item>
    /// <item><description><b>拖动中宿主换形状必须整份收下、欠账作废</b>：换形状只可能由宿主的权威
    /// 事件发起（加减路 / 网格预设 / 单屏 / C 键）。把这类回灌也一并挡掉，松手时那笔欠账就会把
    /// <b>旧形状</b>的分区灌回新形状 ⇒ 画面停在错格上、此后每拍都错（比抖动严重）。</description></item>
    /// </list>
    ///
    /// <para><b>扫幅为什么是「当前边界 ±5%」而不是 0.30~0.70</b>：后者在 ≥3 列时会被邻居的 5%
    /// 下界钳住，被钳住的那些 move 不改变几何、于是不计入分母 ⇒ 判据会读成「移动次数变少」
    /// 而不是「合并失效」，分母失真的绿不算绿。</para>
    ///
    /// <para><b>本判据不声称量过逐帧成本</b>：三臂都是确定性状态机驱动（零耗时循环里
    /// <c>TickCount64</c> 门限几乎不推进，正是「最坏情况全被挡下」那一臂）；
    /// 单次提交耗时与出帧率归 <c>--magnifybench</c>，日志见
    /// <c>tests/output/magnifybench-pancommit-z2-20260929.log</c>。</para></summary>
    private async Task DragCommitUnit(int routes)
    {
        var lanes = Grid.Count;
        var shapes = GridPartition.ShapesFor(lanes);
        if (shapes.Count == 0)
            throw new InvalidOperationException($"拖动热路径：{lanes} 条 lane 没有朝向候选，夹具摆不出可拖的线");
        var (cols, rows) = shapes[0];
        await EnterGridShapeAsync(cols, rows);
        var ov = _layoutOverlay ?? throw new InvalidOperationException("拖动热路径：覆盖层未创建");
        if (!ov.IsOverlayVisible)
            throw new InvalidOperationException("拖动热路径：覆盖层不可见，探针灌进去的移动不会被处理");
        if (ov.HandlePositions().Length == 0)
            throw new InvalidOperationException($"拖动热路径：{cols}x{rows} 没有可拖分割线");
        if (_partition.HandleAxisAt(0) != SplitAxis.X)
            throw new InvalidOperationException($"拖动热路径：本判据按「手柄 0 是竖线」扫 X，{cols}x{rows} 不满足");

        const int Moves = 120;        // ≈ 高回报率鼠标一次持续拖动 1 秒的量
        const double Span = 0.05;
        var x0 = _partition.HandlePositionAt(0).X;
        var targetX = x0 + Span;

        // ── ① 合并比 ──────────────────────────────────────────────────
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ov.ProbeGrabHandle(0);
        for (var i = 0; i < Moves; i++)
            ov.ProbeMoveTo(x0 - Span + 2 * Span * i / (Moves - 1), 0.5);
        sw.Stop();
        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        var gotMoves = ov.ProbeDragMoves;
        var commits = ov.ProbeHostCommits;
        if (gotMoves != Moves)
            throw new InvalidOperationException(
                $"拖动热路径夹具：{Moves} 次移动只有 {gotMoves} 次改变了几何" +
                $"（{cols}x{rows} 边界 {x0:0.###}±{Span:0.###}）⇒ 扫幅撞到权重下界、分母失真，" +
                "该收窄扫幅，不该放宽判据");
        if (commits < 1)
            throw new InvalidOperationException("拖动热路径：一次都没提交给宿主 ⇒ 拖动的通知线断了");
        if (commits * 2 > gotMoves)
            throw new InvalidOperationException(
                $"拖动合并未生效：{gotMoves} 次几何改变只被合并成 {commits} 次提交" +
                $"（门槛 {LayoutOverlayWindow.ProbeCommitIntervalMs}ms、整轮耗时 {elapsedMs:F1}ms）" +
                "⇒ 宿主在每个 move 上被重排一遍，门槛形同不存在");
        Console.WriteLine($"  拖动合并 ✓ {cols}x{rows}：{gotMoves} 次几何改变 → {commits} 次宿主提交" +
                          $"（门槛 {LayoutOverlayWindow.ProbeCommitIntervalMs}ms，整轮 {elapsedMs:F1}ms，" +
                          $"合并比 {commits * 1.0 / gotMoves:0.###}）");

        // ── ② 结算：松手后宿主必须等于覆盖层的乐观值，且落点是扫幅终点 ──
        if (!ov.IsDragging)
            throw new InvalidOperationException("拖动热路径：探针结束前 IsDragging 已为 false，结算臂的前提不成立");
        ov.ProbeRelease();
        if (ov.IsDragging)
            throw new InvalidOperationException("拖动热路径：ProbeRelease 后仍在拖动态 ⇒ 手势没结束");
        if (!PartitionWeightsEqual(_partition, ov.Partition))
            throw new InvalidOperationException(
                $"拖动热路径：松手后宿主与覆盖层不一致 宿主={WeightsText(_partition)} 覆盖层={WeightsText(ov.Partition)}" +
                "⇒ 被门限挡下的最后一段没有补发，宿主停在旧格表上（画面与线错位）");
        var settledX = _partition.HandlePositionAt(0).X;
        if (Math.Abs(settledX - targetX) > GridPartition.Epsilon)
            throw new InvalidOperationException(
                $"拖动热路径：宿主落点 {settledX:0.#####} ≠ 扫幅终点 {targetX:0.#####}" +
                "⇒ 补发的不是最后一次位置（结算值取错/晚了一拍）");
        Console.WriteLine($"  拖动结算 ✓ 松手后宿主边界={settledX:0.#####}（终点一致），" +
                          $"累计提交 {ov.ProbeHostCommits} 次（含收尾补发）");

        // ── ③ 拖动中宿主回灌（同一形状、权重滞后）不得把线拽回 ──────────
        // 重新摆回均匀权重：下面要把「拖动前的那一份」当回灌值用，它必须是同形状、不同权重。
        await EnterGridShapeAsync(cols, rows);
        var stale = _partition;
        ov.ProbeGrabHandle(0);
        for (var i = 1; i <= 4; i++)
            ov.ProbeMoveTo(x0 - Span * 0.5 * i, 0.5);      // 向左拖，与 ① 的方向相反
        var optimistic = ov.Partition;
        if (PartitionWeightsEqual(stale, optimistic))
            throw new InvalidOperationException(
                "拖动热路径：4 次移动后覆盖层仍等于拖动前的分区 ⇒ 探针没有真的拖到线，本臂无鉴别力");
        var commitsBeforeEcho = ov.ProbeHostCommits;
        ov.Update(stale);                                   // 宿主把它那份滞后值回灌（产品就这么做）
        if (!ReferenceEquals(ov.Partition, optimistic))
            throw new InvalidOperationException(
                $"拖动热路径：宿主回灌滞后值把线拽回了 覆盖层={WeightsText(ov.Partition)} 应为={WeightsText(optimistic)}" +
                "⇒ 每提交一次回退一格，用户读到的是拖动抖动（ApplyHostEcho 的拖动守卫失效）");
        if (!ov.ProbeHasPendingCommit)
            throw new InvalidOperationException(
                "拖动热路径：回灌把欠宿主的那一笔清掉了 ⇒ 松手后宿主停在被回退的位置上");
        if (ov.ProbeHostCommits != commitsBeforeEcho)
            throw new InvalidOperationException(
                "拖动热路径：收到回灌时又向宿主提交了一次 ⇒ 提交-回灌-提交的正反馈");
        ov.ProbeRelease();
        if (!PartitionWeightsEqual(_partition, optimistic))
            throw new InvalidOperationException(
                $"拖动热路径：回灌之后松手，宿主={WeightsText(_partition)} ≠ 覆盖层拖动终值 {WeightsText(optimistic)}");
        Console.WriteLine($"  拖动回灌不回拽 ✓ 滞后值 {WeightsText(stale)} 被挡下，乐观值保住并如期结算" +
                          $"（4 次移动 → 累计 {ov.ProbeHostCommits} 次提交）");

        // ── ④ 拖动中宿主换形状：必须整份收下，且旧几何的欠账当场作废 ──────
        var other = OtherShape(shapes, cols, rows, lanes);
        ov.ProbeGrabHandle(0);
        for (var i = 1; i <= 4; i++)
            ov.ProbeMoveTo(x0 + Span * 0.5 * i, 0.5);
        if (!ov.ProbeHasPendingCommit)
            throw new InvalidOperationException(
                $"拖动热路径：换形状臂的前置不成立——4 次移动没有留下欠账" +
                $"（每次提交耗时 >16ms 或合并已关闭，参见 ①）");
        var commitsBeforeReshape = ov.ProbeHostCommits;
        // 走宿主的真实权威入口（加/减路、网格预设、C 键最终都落到这一套动作上）
        await EnterGridShapeAsync(other.Cols, other.Rows);
        if (ov.Partition.Cols != other.Cols || ov.Partition.Rows != other.Rows)
            throw new InvalidOperationException(
                $"拖动热路径：宿主把形状换成 {other.Cols}x{other.Rows} 却被拖动守卫吞掉，" +
                $"覆盖层仍停在 {ov.Partition.Cols}x{ov.Partition.Rows}" +
                "⇒ 松手时那笔旧几何的欠账会把新形状拽回旧格表（画面停在错格上，此后每拍都错）");
        if (ov.ProbeHasPendingCommit)
            throw new InvalidOperationException(
                "拖动热路径：换形状后旧几何的欠账还在 ⇒ 松手会把它补发给新形状");
        ov.ProbeRelease();
        if (ov.ProbeHostCommits != commitsBeforeReshape)
            throw new InvalidOperationException(
                $"拖动热路径：换形状后松手仍补发了一次（{commitsBeforeReshape} → {ov.ProbeHostCommits}）" +
                "⇒ 旧形状的过期值覆盖了新形状");
        if (!PartitionWeightsEqual(_partition, ov.Partition) ||
            _partition.Cols != other.Cols || _partition.Rows != other.Rows)
            throw new InvalidOperationException(
                $"拖动热路径：收尾两侧不一致 宿主={_partition.Cols}x{_partition.Rows} {WeightsText(_partition)} " +
                $"覆盖层={ov.Partition.Cols}x{ov.Partition.Rows} {WeightsText(ov.Partition)}");
        Console.WriteLine($"  拖动中换形状 ✓ 换到 {other.Cols}x{other.Rows} 被整份收下、欠账作废，" +
                          "松手不再补发旧几何");

        await EnterGridShapeAsync(cols, rows);
    }

    /// <summary>给「拖动中换形状」那一臂挑一个<b>与当前不同、且装得下全部 lane</b>的朝向。
    ///
    /// <para>优先用该路数自己的朝向候选（C 键就给这些）；≥7 路只有 3×3 一个候选时退回
    /// <c>(cols+1, rows)</c> —— 真实的换形状来源之一是<b>网格布局预设</b>
    /// （<c>OnGridPreset → GridPartition.Resolve(presetCols, presetRows)</c>），
    /// 预设能给出不在朝向候选里的形状，所以这个靶子是生产里会出现的，不是夹具臆造的。</para></summary>
    private static (int Cols, int Rows) OtherShape(
        System.Collections.Immutable.ImmutableList<(int Cols, int Rows)> shapes, int cols, int rows, int lanes)
    {
        foreach (var s in shapes)
            if (s.Cols != cols || s.Rows != rows)
                return (s.Cols, s.Rows);
        var alt = (Cols: cols + 1, Rows: rows);
        if (alt.Cols * alt.Rows < lanes)
            throw new InvalidOperationException(
                $"拖动热路径：{lanes} 条 lane 找不到与 {cols}x{rows} 不同且装得下的朝向（试 {alt.Cols}x{alt.Rows}={alt.Cols * alt.Rows} 格）");
        return alt;
    }

    /// <summary>两份分区的形状与权重<b>逐位</b>相等（结算类判据用，刻意不给容差：
    /// 比较的就是同一个 double 值，容差只会掩盖「补发了另一拍」这种错）。</summary>
    private static bool PartitionWeightsEqual(GridPartition a, GridPartition b)
        => a.Cols == b.Cols && a.Rows == b.Rows
        && a.ColumnWeights.SequenceEqual(b.ColumnWeights)
        && a.RowWeights.SequenceEqual(b.RowWeights);

    private static string WeightsText(GridPartition p)
    {
        // 一律 InvariantCulture：这是给人读的权重表，zh-CN 下把小数点印成逗号会让读表人数错列数。
        string F(double w) => w.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
        return $"{p.Cols}x{p.Rows}[c={string.Join(",", p.ColumnWeights.Select(F))}," +
            $"r={string.Join(",", p.RowWeights.Select(F))}]";
    }

    /// <summary>P6 单元：对比模式下**再拖入一路**（占格集合没变、路数变了）时，新路必须以"停用"落地。
    ///
    /// <para><b>为什么单独钉这一条</b>：占格集合的发布门原先只比索引集 —— AB 是 2 格，
    /// 已经开着 2 路时集合就是 <c>{0,1}</c>，再拖入第 3 路它<b>还是</b> <c>{0,1}</c> ⇒ 不发事件 ⇒
    /// 新 slot 按 <c>SyncSlot.Active</c> 的默认值 true 落地，被一起解码 + 呈现。
    /// 这正是"路启用态"要消除的浪费，却藏在"集合没变"这条分支里：任何只看集合的测试都看不见它。
    /// 走的是用户真实入口 <see cref="OpenPaths"/>（追加，不重建会话），不是夹具自己造的。</para></summary>
    private async Task RouteAddedWhileSetUnchangedUnit(string videoPath)
    {
        await EnsureRoutesAsync(2, videoPath);
        // 合并后的网格是一路一格、没有"未占格的路"，能造出这个形状的只有左右拉动（恒两格铺满）。
        if (!EnterCompareOverlay())
            throw new InvalidOperationException(
                $"夹具：无法进入左右拉动（素材 {_sync.Count} 路），本判据要的「2 格 + 多出一路」摆不出来");
        await System.Threading.Tasks.Task.Delay(300);
        if (_sync.Count != 2 || !_sync.IsRouteActive(0) || !_sync.IsRouteActive(1))
            throw new InvalidOperationException(
                $"夹具：应为 2 路且前两路启用，实际 路数={_sync.Count} " +
                $"启用0={_sync.IsRouteActive(0)} 启用1={_sync.IsRouteActive(1)}");

        OpenPaths(new System.Collections.Generic.List<string> { videoPath });
        var deadline = System.DateTime.UtcNow + System.TimeSpan.FromSeconds(25);
        while (_sync.Count < 3 && System.DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(200);
        if (_sync.Count != 3)
            throw new InvalidOperationException($"夹具：追加第 3 路未生效，_sync.Count={_sync.Count}");
        await System.Threading.Tasks.Task.Delay(600);   // 新路就绪 + 延后一拍的启用态下发

        // 前提必须是"仍在左右拉动（2 格铺满）"：若打开新媒体把态切走了，这条判据的对象就不存在，
        // 要明说而不是让下面的断言去背这个锅。
        if (!_compareActive || !_compareOverlayActive)
        {
            Console.WriteLine($"  新路即停用：**跳过**（追加后 active={_compareActive} " +
                              $"拉动={_compareOverlayActive}，已不是 2 格铺满，没有\"多出的路\"可判）");
            RecordSelfTestSkip("新路即停用：追加后已不在左右拉动的两格里，无多余路可判");
            return;
        }
        if (_sync.IsRouteActive(2))
            throw new InvalidOperationException(
                "左右拉动（2 格铺满）下新加的第 3 路仍是启用态：发布门只比索引集时 {0,1} 加一路还是 {0,1}，" +
                "事件不发 ⇒ 新路按默认值 Active=true 落地并被一起解码+呈现");
        await AssertRouteActivityAsync(3, 2, "新路即停用");
        ExitGridCompare();
        await System.Threading.Tasks.Task.Delay(300);
    }

    private Unit[] BuildUnits(string videoPath)
    {
        var routes3 = Math.Max(3, _sync.Count);
        var shape3 = GridPartition.ShapesFor(routes3)[^1];   // 该路数下"格数最多"的朝向，替代旧 maxShape
        return new[]
        {
            new Unit("lane.add-remove-contract", 2, (2, 1),
                new[] { "对比-加减路契约" }, async () =>
                {
                    await AssertLaneAddRemoveContractAsync("单元");
                    // 不带对比模式时也要能跑：再验一遍非对比路径（无格表遮蔽）
                    ExitCompareMode();
                    await AssertLaneAddRemoveContractAsync("单元-非对比");
                }),
            new Unit("cells.swap", 3, (3, 1), new[] { "对比-左右互换" },
                async () => await AssertSwapCompareCellsAsync(Math.Max(3, _sync.Count))),
            new Unit("cells.rotate", 3, null, new[] { "对比-轮换画面" },
                async () => await AssertRotateCompareCellsAsync(Math.Max(3, _sync.Count))),
            new Unit("align.modes", 3, null, new[] { "对比-对齐模式" },
                async () => await AssertCompareAlignModesAsync(Math.Max(3, _sync.Count), shape3)),
            new Unit("magnify.seamless", 2, (2, 1), new[] { "无缝放大", "无缝放大 z=2" },
                async () =>
                {
                    var n = _sync.Count;
                    AssertCompareMagnifyGates(n);
                    // 拼合后每路窗口 == 整对比区 ⇒ 同样的 z 要占 N 倍像素，倍率按预算取
                    var zTest = AffordableMagnifyZoom(n);
                    AssertMagnifyBudgetRefusal(n, zTest);
                    var applied = await AssertCompareMagnifyAsync(n, (2, 1),
                        zoom: zTest, cropX: 0.25, cropY: 0.25);
                    // z>1 时窗口必然大于格 ⇒ 一次都没下发就是没接线，这条有牙
                    if (applied == 0) throw new InvalidOperationException("单元：没有任何一路真正进入放大状态");
                    _cropTotal += applied;
                    await AssertCompareMagnifyResetAsync(n);
                }),
            // ── 第二批：把剧本里"一行调用 + 它自己的前置"整体搬进单元体 ──
            new Unit("layout.enter-ab", 2, null, new[] { "对比-进入" },
                async () =>
                {
                    var n = _sync.Count;
                    EnterCompareMode();
                    await System.Threading.Tasks.Task.Delay(200);
                    var (c, r) = (_partition.Cols, _partition.Rows);
                    AssertCompareOverlay(n, c, r);
                    AssertCellsDriveLayout(n, "进入网格对比");
                    // 只是累计，不据此判红：不放大时"窗口恰好等于格"合法地一个区域都不用下发
                    //（AssertCompareCrop 内部会核对回读，0 次它自己会打"0/N 路设了区域"）。
                    _cropTotal += AssertCompareCrop(n, "进入网格对比");
                }),
            new Unit("layout.mode-sweep", 3, null, new[] { "对比-切换" },
                async () =>
                {
                    // 合并后"扫模式"= 扫该路数下的全部朝向候选（C 键的完整一圈）
                    var n = _sync.Count;
                    foreach (var (c, r) in GridPartition.ShapesFor(n))
                    {
                        await EnterGridShapeAsync(c, r);
                        AssertCompareOverlay(n, c, r);
                        AssertCellsDriveLayout(n, $"朝向 {c}x{r}");
                    }
                }),
            new Unit("variants.shape", 4, (2, 2), new[] { "对比-模式变体" },
                async () => await AssertCompareModeVariantsAsync(_sync.Count)),
            new Unit("magnify.wheel", 2, (2, 1), new[] { "对比-滚轮无缝放大" },
                async () => { _cropTotal += await AssertCompareWheelMagnifyAsync(_sync.Count, (2, 1)); }),
            new Unit("exit.cleanup", 2, null, new[] { "对比-退出" },
                async () =>
                {
                    var n = _sync.Count;
                    EnterCompareMode();
                    await System.Threading.Tasks.Task.Delay(150);
                    ExitCompareMode();
                    await System.Threading.Tasks.Task.Delay(150);
                    if (BenchKeepLayoutOverlayOnExit)
                        RecordSelfTestSkip("FC_KEEP_LAYOUTOVERLAY=1：本轮按住了 HideOverlay ⇒「退出后覆盖层已隐藏」未判");
                    else if (_layoutOverlay is null || _layoutOverlay.IsOverlayVisible)
                        throw new InvalidOperationException("单元：退出后覆盖层仍可见（HideOverlay 未生效）");
                    if (Grid.CellOverride is not null)
                        throw new InvalidOperationException("单元：退出后 CellOverride 未清空（非对比路径会被污染）");
                    for (var i = 0; i < n; i++)
                        if (Grid.GetSurface(i)?.IsVisible != true)
                            throw new InvalidOperationException($"单元：退出后第 {i} 路仍不可见（均匀网格未恢复）");
                    AssertCompareCropCleared(n, "退出");
                }),
            new Unit("layout.split-relayout", 3, null, new[] { "对比-分割驱动重排" },
                async () =>
                {
                    var (c, r) = GridPartition.ShapesFor(_sync.Count)[^1];
                    // 只累计、不据此判红：格与窗口恰好对齐时合法地 0 次下发
                    _cropTotal += await SplitRelayoutUnitAsync(_sync.Count, c, r);
                }),
            new Unit("entry.grid-compare", 5, null, new[] { "模式-分屏入口", "网格对比入口" },
                async () => await GridCompareEntryUnit(_sync.Count)),
            new Unit("entry.grid", 3, null, new[] { "模式-网格入口" },
                async () => await GridEntryUnit(_sync.Count)),
            new Unit("view.mode-column", 2, null, new[] { "视图模式列" },
                async () => await ViewModeColumnUnit(_sync.Count)),
            new Unit("view.split-line-hit", 3, null, new[] { "分割线命中" },
                async () => await SplitLineHitUnit(_sync.Count)),
            new Unit("view.mosaic-tile", 3, null, new[] { "拼合铺块", "分区拼合" },
                async () =>
                {
                    var (c, r) = GridPartition.ShapesFor(Grid.Count)[^1];
                    await EnterGridShapeAsync(c, r);
                    AssertMosaicTiling(_sync.Count, $"{c}x{r}");
                }),
            new Unit("view.drag-commit", 2, null, new[] { "拖动热路径", "分割线拖动合并提交" },
                async () => await DragCommitUnit(_sync.Count)),
            new Unit("view.route-activity", 5, null, new[] { "路启用态" },
                async () => await RouteActivityUnit(_sync.Count)),
            new Unit("sync.seek-parks", 4, null, new[] { "单路受控Seek" },
                async () => await SeekParksRouteUnit()),
            new Unit("sync.stall-recovery", 2, null, new[] { "停滞唤醒恢复" },
                async () => await StallRecoveryUnit()),
            new Unit("view.route-added-while-hidden", 2, null, new[] { "新路即停用" },
                async () => await RouteAddedWhileSetUnchangedUnit(videoPath)),
        };
    }

    /// <summary>把会话与网格摆成 <paramref name="routes"/> 路（幂等；已是目标状态就直接返回）。
    /// <para>沿用重建会话那条既有路径（全部 DetachSession → <c>_sync.Clear()</c> → SetCount(0) → SetCount(n) → OpenFiles），
    /// 不另起一套清场逻辑，避免"夹具自己"变成第二个漂移源。</para></summary>
    private async Task EnsureRoutesAsync(int routes, string videoPath)
    {
        if (_sync.Count == routes && Grid.Count == routes) return;
        _step = $"夹具-打开{routes}路";
        foreach (var s in Grid.Surfaces) s.DetachSession();
        _sync.Clear();
        Grid.SetCount(0, _realMode);
        Grid.SetCount(routes, _realMode);
        // 素材池为空时 BuildMediaList 与旧的 Enumerable.Repeat 逐字等价；FC_STEPS 单元宿主也走这里，
        // 所以多素材轮必须由**同一份**排布驱动，否则"完整剧本跑多素材、单元宿主跑单素材"会分叉。
        _coordinator.OpenFiles(BuildMediaList(videoPath, routes), autoPlay: false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);

        while (DateTime.UtcNow < deadline)
        {
            var snaps = _sync.ReadAllSnapshots();
            if (snaps.Count == routes &&
                snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State))) return;
            await System.Threading.Tasks.Task.Delay(200);
        }
        throw new InvalidOperationException($"夹具：{routes} 路未在 25s 内就绪（当前 _sync.Count={_sync.Count}）");
    }

    private static bool UnitMatches(string entry, Unit u)
    {
        // 必须与 Selected() 同一套语义。此前不认 part: ⇒ `FC_STEPS=part:P6` 在宿主侧恒 0 命中
        //（剧本侧却认），表现为"退回整轮剧本"而不是"跑 P6 那几个单元"。
        if (entry.StartsWith("part:", StringComparison.OrdinalIgnoreCase))
        {
            var want = entry[5..].Trim();
            var p = PartOf(u.Id);
            return p.StartsWith(want, StringComparison.OrdinalIgnoreCase) ||
                   p.Contains(want, StringComparison.OrdinalIgnoreCase);
        }
        var rx = entry.Contains('*')
            ? "^" + System.Text.RegularExpressions.Regex.Escape(entry).Replace("\\*", ".*") + "$"
            : null;
        bool One(string s) => rx is not null
            ? System.Text.RegularExpressions.Regex.IsMatch(s, rx)
            : s.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
              s.Contains(entry, StringComparison.OrdinalIgnoreCase);
        return One(u.Id) || u.Aliases.Any(One);
    }

    /// <summary>单元宿主模式：<c>FC_STEPS</c> 命中了已登记单元时，只跑"共同夹具 + 这些单元"，
    /// 整轮剧本一行都不执行。没命中任何登记单元 ⇒ 明说后退回整轮剧本（不静默少跑）。</summary>
    /// <returns>true 表示本轮已由宿主处理，调用方不得再跑剧本。</returns>
    private async Task<bool> RunSelectedUnitsAsync(string videoPath)
    {
        var sel = StepSelector;
        if (sel is not { Length: > 0 }) return false;
        var units = BuildUnits(videoPath);
        var picked = units.Where(u => sel.Any(e => UnitMatches(e, u))).ToList();
        if (picked.Count == 0)
        {
            // 退回整轮剧本 ⇒ 必须同时**解除选择器**，否则会跑出一条"全部 SKIP + 全部通过"的假绿
            //（实测就是这样：27 项全 SKIP，末尾照样打印 全部通过 ✓ exit 0）。
            _selectorSuppressed = true;
            Console.WriteLine($"comparemodetest: 选择器 [{string.Join(", ", sel)}] 没有命中任何已登记单元 ⇒ " +
                              "退回整轮剧本**全量执行**（这些判据尚未自带夹具，见 docs/49 切片 2）");
            return false;
        }
        var unmet = sel.Where(e => !units.Any(u => UnitMatches(e, u))).ToList();
        if (unmet.Count > 0)
            Console.WriteLine($"comparemodetest: ⚠ 选择器里这些条目未登记自带夹具，**本次不跑**：{string.Join(", ", unmet)}");

        _step = "宿主-起盘";
        await EnsureRoutesAsync(picked.Max(u => u.Routes), videoPath);
        var routes = _sync.Count;
        Console.WriteLine($"comparemodetest: 单元宿主 —— 夹具 {routes} 路，跑 {picked.Count} 个单元：" +
                          $"{string.Join(", ", picked.Select(u => u.Id))}");
        foreach (var u in picked)
        {
            if (u.Shape is { } sh) await EnterGridShapeAsync(sh.Cols, sh.Rows);
            _step = u.Id;
            if (u.Id.StartsWith("magnify.", StringComparison.Ordinal)) _cropCapableSelected = true;
            await StepNamedAsync(u.Id, u.Body);
        }
        PrintStepSummary("comparemodetest/单元宿主");
        // fail-closed：选中的每一个单元都必须留下一行**以自己 id 开头**的 PASS/FAIL 记录。
        // 只要求"日志里出现过这个 id"是不够的：另一个单元的 FAIL 行会把异常原文打全，
        // 里面可能正好包含这个串，从而把"根本没跑"洗成"跑过了"（独立审查指出）。
        var missing = picked.Where(u =>
            !_stepOutcomes.Any(o => o.StartsWith($"PASS {u.Id} ") || o.StartsWith($"FAIL {u.Id} "))).ToList();
        if (missing.Count > 0)
        {
            _stepFailures++;
            Console.Error.WriteLine($"comparemodetest: 单元宿主判决不可信 —— {missing.Count} 个选中单元没有留下读数：" +
                                    string.Join(", ", missing.Select(u => u.Id)));
        }
        // 剧本里有"整轮至少下发过一次区域"的聚合兜底；宿主里只有当**选中的单元里包含
        // 会产生裁剪的那些**时才要求 >0 —— 单跑一个与裁剪无关的单元时，>0 不是它该付的账。
        if (_cropCapableSelected)
        {
            Console.WriteLine($"comparemodetest: 所选单元下发窗口区域累计 {_cropTotal} 次" +
                              (_cropTotal > 0 ? " ✓" : " ✗ 选了裁剪相关单元却一次都没下发"));
            if (_cropTotal <= 0) _stepFailures++;
        }
        if (_stepFailures == 0 && missing.Count == 0)
            Console.WriteLine($"comparemodetest: 所选 {picked.Count} 个单元全部通过 ✓");
        else
            Console.Error.WriteLine($"comparemodetest: 单元宿主未通过 —— {_stepFailures} 项问题（逐条见上）");
        return true;
    }

    private async System.Threading.Tasks.Task RunCompareModeTestAsync(
        string videoPath, int routes, IReadOnlyList<string>? extraVideos = null)
    {
        routes = Math.Clamp(routes, 2, 9);
        // 多素材入口（#29）：路数之后的参数都当额外素材，按路轮转。校验一律 fail-closed ——
        // 传了却不存在的文件不能被静默忽略，那样各路会退化成单素材，而下一条判据会因此"合法地"记未判，
        // 读表的人以为跑了多素材、其实没跑。
        if (extraVideos is { Count: > 0 })
        {
            foreach (var v in extraVideos)
            {
                if (File.Exists(v)) continue;
                Console.Error.WriteLine($"comparemodetest: 额外素材不存在 {v}");
                ExitSelfTest(2);
                return;
            }
            _benchMediaPool = extraVideos;
        }
        else
        {
            _benchMediaPool = Array.Empty<string>();
        }

        // docs/41 #16；复核修正：原 300s 与 run_all.sh 的 `timeout 300` **完全相等** ⇒
        // 看门狗永远抢不到外部 kill 之前，等于没有兜底。取 240s 留出 60s 让看门狗
        // 打印卡住的步骤名并以 code=3 退出，排障信息才不会随外部 kill 一起丢失。
        StartDeadlineWatchdog("comparemodetest", 240);
        var code = 1;
        // 累计"生产路径真正下发过的裁剪次数"：0 说明裁剪接线从未被走到（静默失效）
        var cropApplied = 0;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"comparemodetest: 文件不存在 {videoPath}");
                ExitSelfTest(2);
                return;
            }

            // 对比模式要求 _sync.Count ≥ 2，而 _sync 只在 OpenFiles 成功建会话后才增长，
            // 故本测试与 multitest 一样必须真实打开多路（不能只 Grid.SetCount）。
            // 单元宿主：FC_STEPS 命中已登记单元就只跑那些单元 + 共同夹具，剧本一行不跑。
            // （切片 2 的目的：把"改一条判据也要跑完整轮"变成秒级。）
            if (await RunSelectedUnitsAsync(videoPath))
            {
                code = _stepFailures == 0 ? 0 : 1;
                return;
            }

            _step = "对比-打开多路";
            Grid.SetCount(routes, _realMode);
            _coordinator.OpenFiles(BuildMediaList(videoPath, routes), autoPlay: false);
            var mediaKinds = DistinctMediaCount(videoPath);
            Console.WriteLine($"comparemodetest: 请求打开 {routes} 路（{(_realMode ? "真实" : "演示")}），" +
                              $"素材 {mediaKinds} 种{(_benchMediaPool.Count > 0 ? "（多素材轮转）" : "（同一份重复）")}，等待就绪...");


            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < readyDeadline)
            {
                var snaps = _sync.ReadAllSnapshots();
                if (snaps.Count == routes &&
                    snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State)))
                    break;
                await System.Threading.Tasks.Task.Delay(200);
            }
            var readyCount = _sync.ReadAllSnapshots()
                .Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));
            if (readyCount != routes)
                throw new InvalidOperationException($"仅 {readyCount}/{routes} 路就绪");
            Console.WriteLine($"comparemodetest: {routes} 路就绪 ✓");

            // 覆盖层几何依赖 Grid.Bounds，等一次布局完成再进入
            await System.Threading.Tasks.Task.Delay(600);

            _step = "对比-进入";
            EnterCompareMode();
            // 覆盖窗 Show → OnOpened 装钩子发生在同一轮消息泵内，这里给一拍余量
            await System.Threading.Tasks.Task.Delay(300);
            var enterShape = (_partition.Cols, _partition.Rows);
            await StepAsync(() => { AssertCompareOverlay(routes, enterShape.Cols, enterShape.Rows); });
            await StepAsync(() => { AssertCellsDriveLayout(routes, "进入网格对比"); });
            // 裁剪（SetWindowRgn）接线：区域必须真的被下发且回读一致，Win32 路径另做端到端往返
            cropApplied += await StepAsync<int>(() => Task.FromResult(AssertCompareCrop(routes, "进入网格对比")));
            AssertCropWin32RoundTrip("进入网格对比");

            // 加路/减路契约（网格那一支：新 lane 一定多出一格 ⇒ 必须可见；只有拉动那一支才要求
            // "超出两格就隐藏 + 必须提示"，两条都在同一判据里各测一次）
            _step = "对比-加减路契约";
            await StepAsync(async () => { await AssertLaneAddRemoveContractAsync("comparemodetest"); });

            // 逐级扫过该路数下的全部朝向候选（= 用户按 C 键的一圈），
            // 验证"朝向 ↔ 格表 ↔ 覆盖层手柄"三者始终一致（旧实现扫的是 AvailableModes）。
            var shapes = GridPartition.ShapesFor(routes);
            Console.WriteLine($"comparemodetest: 路数={routes} 可用朝向=" +
                              $"[{string.Join(", ", shapes.Select(s => $"{s.Cols}x{s.Rows}"))}]");
            foreach (var sh in shapes)
            {
                _step = $"对比-切换 {sh.Cols}x{sh.Rows}";
                await EnterGridShapeAsync(sh.Cols, sh.Rows);
                await StepAsync(() => { AssertCompareOverlay(routes, sh.Cols, sh.Rows); });
                await StepAsync(() => { AssertCellsDriveLayout(routes, $"朝向 {sh.Cols}x{sh.Rows}"); });
            }
            var maxShape = shapes[^1];

            // 核心验收点：拖动分割手柄 ⇒ PartitionChanged ⇒ 重算格表 ⇒ 画面跟着变。
            // 直接走宿主回调（覆盖层的拖动最终就是调它）。
            //
            // ⚠ 断言形式（此处曾是有缺陷的写法，勿改回）：**不能**要求"宽和高都要变"。
            // 一条竖分割线只改宽度，被它隔开的那一列本就通高 ⇒ 格高不变是**正确**的。
            // 旧写法在 2 路下必然误报；4 路（十字）恰好掩盖了它，才一直没被暴露。
            //
            // 正确形式：用拖动前后的 partition.ComputeCells() 各自**独立复算**期望矩形，
            // 断言实际 Bounds 确实由前者迁到后者；对任何朝向一律成立，且只要"重排没生效"
            // （Bounds 停在旧值）就判红 —— 鉴别力不降。
            _step = "对比-分割驱动重排";
            cropApplied += await StepAsync<int>(async () =>
                await SplitRelayoutUnitAsync(routes, maxShape.Cols, maxShape.Rows));

            // #22：放大镜浮窗坐标。放在这里是因为**只有多格布局才具备鉴别力**——
            // 被测路（原点离容器最远的那一格）与容器原点差数百 DIP，"漏换算"必然判红；
            // 单路布局下这个差只有 1px（--selftest 里那条是同一断言的低鉴别力版本）。
            // 必须在"无缝放大"之前：放大后窗口尺寸被刻意放大，几何含义不同。
            _step = "放大镜浮窗坐标";
            await StepAsync(async () => { await AssertMagnifierFollowsCursorAsync(routes, $"对比 {maxShape.Cols}x{maxShape.Rows}"); });

            // ── 无缝放大（子窗口放大 + 偏移 + 裁剪）：默认关闭，这里显式启用后逐项验证 ──
            // 必须放在"退出"之前、且结束前复位：否则后续断言（期望 Bounds == 格）会失败。
            _step = "对比-放大闸门";
            await StepAsync(() => { AssertCompareMagnifyGates(routes); });
            _step = "对比-无缝放大";
            // 倍率按预算取：拼合后每路窗口 == 整对比区，9 路 z=2 是 88.6 Mpx，超预算就该被拒
            var zoomAffordable = AffordableMagnifyZoom(routes);
            await StepAsync(() => { AssertMagnifyBudgetRefusal(routes, zoomAffordable); });
            cropApplied += await StepAsync<int>(async () => await AssertCompareMagnifyAsync(routes, maxShape, zoom: zoomAffordable, cropX: 0.25, cropY: 0.25));
            _step = "对比-放大复位";
            await StepAsync(async () => { await AssertCompareMagnifyResetAsync(routes); });

            // ── 滚轮 → 无缝放大：生产 UI 的触发路径（此前内核完整但没有任何 UI 入口）──
            _step = "对比-滚轮无缝放大";
            cropApplied += await StepAsync<int>(async () => await AssertCompareWheelMagnifyAsync(routes, maxShape));

            // ── 左右互换 / 格→路映射 ──
            _step = "对比-左右互换";
            await StepAsync(async () => { await AssertSwapCompareCellsAsync(routes); });

            _step = "对比-轮换画面";
            await StepAsync(async () => { await AssertRotateCompareCellsAsync(routes); });

            // ── 分辨率对齐模式（相对 / 像素级）──
            _step = "对比-对齐模式";
            await StepAsync(async () => { await AssertCompareAlignModesAsync(routes, maxShape); });

            // ── 模式变体：AB 竖 / ABC 三列（含"三列有两条竖线 ⇒ 两个手柄"）──
            _step = "对比-模式变体";
            await StepAsync(async () => { await AssertCompareModeVariantsAsync(routes); });

            // ── 叠加模式（对标 ICAT Single Screen，docs/31 阶段 2）──
            // 放在放大复位之后、退出之前：进入叠加会切到 AB 并复位放大；退出叠加后回到 AB 左右分栏，
            // 因此后面既有的"退出对比模式"断言（含 AssertCompareCropCleared）不受影响。
            cropApplied += await StepAsync<int>(async () => await AssertCompareOverlayAsync(routes, "叠加"));

            // ── 布局编辑器（自由拼接）二级窗：**模态交互探针** ──
            // 为什么单列一步：本项目对"覆盖窗口上的交互控件"从未实测（docs/30:189 P6），
            // 编辑器整条路径都压在"模态窗上的按钮/画布点击能否正常交互"上 ⇒ 先量出读数再依赖。
            await StepAsync(async () => { await AssertLayoutEditorAsync(routes); });

            // ── 模式家族入口（docs/31 阶段 3）：菜单 / 快捷键的三项入口必须真的可用 ──
            // 这一段专盯用户点名的 5~9 路场景：
            //   ① 分屏入口按路数收敛（≥4 路 → ABCD），且**只显示前 4 路**（第 5~9 路隐藏）；
            //   ② 网格入口退出对比模式 ⇒ **全部路数恢复可见**，排版与 GridLayout.ComputeGrid
            //      独立复算的结果一致（9 路 = 3×3）；
            //   ③ 退出后区域 / 覆盖层复原（本段末尾 + 下面的"对比-退出"各钉一次）。
            // 期望值一律来自 Core 的纯函数（`GridPartition.CellCount` / `GridLayout.ComputeGrid`），不回读 Grid 内部状态。
            // （原文写的是 `CoerceMode`，那个方法已随视图模式合并撤除 —— 注释比代码旧会误导下一次改动。）
            _step = "模式-网格对比入口";
            await StepAsync(async () => await GridCompareEntryUnit(routes));

            // ── 网格入口：必须退出对比模式，并把 2~9 路全部恢复成均匀网格 ──
            _step = "模式-网格入口";
            await StepAsync(async () => await GridEntryUnit(routes));

            // ── #22 的第二臂：**均匀网格**（非对比态）下再跑一次放大镜坐标判据 ──
            // 拼合排布把各路窗口原点都放到容器原点 ⇒ 上面那条 Δ 核对按定义失去鉴别力（判据自己会
            // 打印"不适用"）。而它要抓的缺陷类（把 surface 局部坐标当容器坐标）只有在
            // **各路原点互不重合**的布局里才可表达 —— 网格入口之后正是这个构型（9 路 3×3，
            // 最后一格的原点离容器数百 DIP），所以牙齿挪到这里钉住。
            _step = "放大镜浮窗坐标-均匀网格";
            await StepAsync(async () =>
            {
                if (_compareActive) ExitCompareMode();   // 双保险：这一步必须在非对比态跑
                await AssertMagnifierFollowsCursorAsync(routes, $"均匀网格 {routes} 路");
            });

            // ── 视图模式一列（菜单顶部三项）：勾选 ↔ 真实状态 ↔ G/V/S 真实键盘路由 ──
            _step = "视图模式列";
            await StepAsync(async () => await ViewModeColumnUnit(routes));

            // ── 分割线命中区：单轴放开到整条线，交叉点型只放圆点 ──
            // 路数不足时由判据体自己登记 SKIP（汇总表里看得见它没判），不在外面 if 掉——
            // 外面 if 掉的后果是这一行<b>从表里消失</b>，读表的人不知道少了一条。
            _step = "分割线命中";
            await StepAsync(async () => await SplitLineHitUnit(routes));

            // ── 拖动热路径：覆盖层自己的「合并提交 / 松手结算 / 宿主回灌」三臂 ──
            // 上面所有拖动类判据都直接调宿主回调，绕开了这一段 ⇒ 这一段今天必须有测量。
            // ── 拼合铺块：各路窗口铺满对比区、区域裁出自己那一块，几块严丝合缝拼回整幅 ──
            // 这条是"网格对比"被纠正后的语义验收（旧排布每格显示缩小版整幅，拖线只在改尺寸）。
            _step = "拼合铺块";
            await StepAsync(async () =>
            {
                await EnterGridShapeAsync(maxShape.Cols, maxShape.Rows);
                AssertMosaicTiling(routes, $"{maxShape.Cols}x{maxShape.Rows}");
                AssertGdiHonorsRegion(routes, $"{maxShape.Cols}x{maxShape.Rows}");
                // 尺寸判据只钉住"裁没裁"；"每格画的是不是自己那一路的画面"要比内容，
                // 而内容判据在单素材下恒真 ⇒ 传了第二种素材才判，没传就登记未判（#29）。
                AssertRouteContentsDiffer(routes, $"{maxShape.Cols}x{maxShape.Rows}", videoPath);

            });

            _step = "拖动热路径";
            await StepAsync(async () => await DragCommitUnit(routes));

            // ── 路启用态：没占格的路必须停用（不解码/不呈现），切回全部占格时能恢复且同位 ──
            _step = "路启用态";
            await StepAsync(async () => await RouteActivityUnit(routes));

            _step = "对比-退出";
            ExitCompareMode();            await System.Threading.Tasks.Task.Delay(150);
            if (BenchKeepLayoutOverlayOnExit)
                RecordSelfTestSkip("FC_KEEP_LAYOUTOVERLAY=1：本轮按住了 HideOverlay ⇒「退出后覆盖层已隐藏」未判");
            else if (_layoutOverlay is null || _layoutOverlay.IsOverlayVisible)
                throw new InvalidOperationException("退出后覆盖层仍可见（HideOverlay 未生效）");
            // 退出后必须回到既有均匀网格：格表覆盖被清空，且此前被隐藏的路重新可见。
            if (Grid.CellOverride is not null)
                throw new InvalidOperationException("退出后 Grid.CellOverride 未清空（非对比路径会被污染）");
            UpdateLayout();
            for (var i = 0; i < routes; i++)
                if (Grid.GetSurface(i)?.IsVisible != true)
                    throw new InvalidOperationException($"退出后第 {i} 路仍不可见（均匀网格未恢复）");
            Console.WriteLine("comparemodetest: 退出后覆盖层已隐藏、均匀网格已恢复 ✓");
            await StepAsync(() => { AssertCompareCropCleared(routes, "退出"); });

            // 最后兜一刀：整轮跑完必须至少真的下发过一次区域。否则"裁剪接线"只是纸面通过 ——
            // 换算、时机、DPI、清除全对却一次都没走，等于没接线。
            // 最后兜一刀：整轮跑完必须至少真的下发过一次区域。否则"裁剪接线"只是纸面通过 ——
            // 换算、时机、DPI、清除全对却一次都没走，等于没接线。
            // ⚠ 只在"本轮真的跳过了产生裁剪的单元"时豁免（用 _skippedUnits 而不是"选择器非空"：
            //    未命中任何登记单元时剧本是全量跑的，那时 cropApplied==0 必须照判红）。
            if (cropApplied <= 0)
            {
                if (_skippedUnits > 0)
                    Console.WriteLine($"comparemodetest: 裁剪下发兜底 **本次不适用**（选择器跳过了 {_skippedUnits} 项，" +
                                      "cropApplied 恒为 0）——全量跑时该断言仍然生效。");
                else
                    throw new InvalidOperationException(
                        "整轮对比模式从未真正下发过窗口区域（裁剪接线未被走到，属静默失效）");
            }
            else Console.WriteLine($"comparemodetest: 裁剪下发累计 {cropApplied} 次（>0 表示生产路径确实被走到）✓");

            PrintStepSummary("comparemodetest");
            // "全部通过"必须同时满足：没有判红、且没有一项被跳过。
            // 带选择器跑时明写"另有 N 项未验"，不让一句 全部通过 冒充全量覆盖。
            if (_stepFailures == 0 && _skippedUnits == 0)
            {
                Console.WriteLine("comparemodetest: 全部通过 ✓");
                code = 0;
            }
            else if (_stepFailures == 0)
            {
                // 跳过有**两种**来源，以前一句"被选择器跳过"把它们混在一起 ⇒ 没设选择器的那一轮
                // 会同时打出"未设选择器：全部验收点均执行"和"1 项被选择器跳过"两句自相矛盾的话，
                // 而后者其实是"判据自己登记了没有鉴别力 / 素材不符"。两种都要点名报数。
                var skippedInBody = _stepOutcomes.Count(o =>
                    o.StartsWith("SKIP", StringComparison.Ordinal) && o.Contains("体内登记跳过"));
                var skippedBySelector = _skippedUnits - skippedInBody;
                Console.WriteLine($"comparemodetest: 已执行项全部通过 ✓（另有 {_skippedUnits} 项跳过：" +
                                  $"选择器未选 {skippedBySelector} 项、判据自报「没鉴别力/素材不符」{skippedInBody} 项，" +
                                  "**都不算验过**）");
                code = 0;
            }
            else
                Console.Error.WriteLine(
                    $"comparemodetest: 未通过 —— {_stepOutcomes.Count} 个验收点里 {_stepFailures} 个判红" +
                    "（逐条见上方汇总；有判红时「汇总」不等于「通过」）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"comparemodetest[步骤{_step}]: 失败 ✗ {ex.Message}");
            // 提前中止时也要交出已经跑到的那半张表 —— 今天最想看的读数正是
            // "红之前哪些验收点是绿的、红之后有多少条根本没被执行"。
            PrintStepSummary("comparemodetest（前置失败提前中止，以下为已执行部分）");
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    // ══════════ 无缝放大：真实视频下的显存/吞吐实测（--magnifybench <video> <routes> <zoom> [秒/阶段] [预算Mpx]） ══════════
    //
    // 为什么需要它：两道闸门（z ≤ 4 / 66.4 Mpx）的预算最初是**估算**的，而 --comparemodetest 只在
    // z=2 下停留几百毫秒：既测不到稳态显存，也覆盖不到 z=3/4。本模式把 z 固定住持续播放若干秒，
    // 并打印带 epoch 毫秒时间戳的阶段标记，由外部脚本（.review_pr/gpu_probe.ps1）按 PID 采样
    // GPU 专用显存与之对齐 —— 显存只能从进程外测（内核 D3D 设备在 FFF.Native 里，进程内拿不到
    // DXGI 适配器）。预算后经本模式实测校准：原 33.2 Mpx 偏保守约 3 倍，已改为 66.4 Mpx。
    //
    // ⚠ 路数会被钳到 [2, 9]（对比模式本就至少 2 路），即本方法内的 `Math.Clamp(routes, 2, 9)`。
    //   传 1 会静默变成 2 —— 曾导致一次"8K 1 路"实验实为 2 路、数据被误读。
    //   单路场景请改用 --selftest。
    //
    // 预算覆盖（第 5 个参数）：用于**越过闸门**探测硬件真实上限。SetCompareMagnify 的 pixelBudget
    // 参数本就是为此预留的。不给则走生产默认值（66.4 Mpx），此时能观察到"闸门拒绝"。
    //
    // 输出约定（供外部脚本解析，勿改格式）：
    //   #BENCHPHASE name=<baseline|magnified> zoom=<z> t0=<epochMs> t1=<epochMs> wall=<s> fps=<n>
    //               clock=<s> clockRatio=<0..> valid=<yes|NO> hitches=[..] presented=[..]
    //     clockRatio = 媒体时钟推进 / 墙钟。<0.8 时 valid=NO ⇒ 素材跑到末尾或播放被 park，
    //     那一档的 fps **不是吞吐读数**（2026-10-01：720p 派生件只有 12.0s，两阶段+预热就超，
    //     z=2 阶段报 wall=8.08/clock=1.34，fps 因此虚低到个位）。bench 此时报 exit=4 SKIPPED。
    //   #BENCHGEOM zoom=<z> gate=<accept|reject> totalMpx=<n> estMpx=<n> windowPx=<w>x<h> ...
    private async System.Threading.Tasks.Task RunMagnifyBenchAsync(
        string videoPath, int routes, double zoom, int secondsPerPhase, double budgetMpx)
    {
        // 先留一份用户原始请求：下方 clamp 会静默改值，钳过之后必须显式提示
        // （方法头注释已说明"传 1 会变成 2"，但注释只有读源码的人看得到，跑实验的人看的是输出）。
        var requestedRoutes = routes;
        routes = Math.Clamp(routes, 2, 9);
        secondsPerPhase = Math.Clamp(secondsPerPhase, 5, 60);
        // docs/41 #16（复核修正）：必须用**钳过之后**的 secondsPerPhase 计算时限。
        // 原先这行在 clamp 之前，传入超大值时 secondsPerPhase*6 溢出为负 ⇒
        // Task.Delay(负) 抛异常且无人观察 ⇒ 静默退回"无兜底"，正是 #16 要堵的洞。
        // 系数也收紧：外部脚本（run_all.sh 300s / run_pub_matrix.sh 320s）比旧值 468s 先超时，
        // 看门狗形同虚设。改为 3×+120 ⇒ 默认 8s→144s、钳满 60s→300s。
        // ⚠ 但 300s 与外部 timeout 300s **相等**（docs/41 复核必修）：两者同时到点，
        // 外部 kill 抢先 ⇒ 看门狗的诊断日志永远打不出来，退出码变成脚本的超时码。
        // 故再对 240s 取一次 min，保证看门狗一定先于外部超时给出结论。
        StartDeadlineWatchdog("magnifybench", Math.Min(secondsPerPhase * 3 + 120, 240));
        var budgetOverride = budgetMpx > 0 ? budgetMpx * 1e6 : 0;

        Console.WriteLine(
            $"magnifybench: 路数={routes} zoom={zoom} 每阶段={secondsPerPhase}s " +
            $"预算={(budgetOverride > 0 ? $"{budgetMpx:0.#}Mpx(覆盖)" : "默认33.2Mpx")} 素材={videoPath}");
        if (requestedRoutes != routes)
            Console.WriteLine($"magnifybench: ⚠ 路数已钳到 {routes}（请求 {requestedRoutes}，允许区间 [2,9]）");

        var code = 1;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"magnifybench: 文件不存在 {videoPath}");
                ExitSelfTest(2);
                return;
            }

            _step = "基准-打开多路";
            Grid.SetCount(routes, _realMode);
            _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
            Console.WriteLine($"magnifybench: 请求打开 {routes} 路（{(_realMode ? "真实" : "演示")}），等待就绪...");

            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < readyDeadline)
            {
                var snaps = _sync.ReadAllSnapshots();
                if (snaps.Count == routes &&
                    snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State)))
                    break;
                await System.Threading.Tasks.Task.Delay(200);
            }
            var readyCount = _sync.ReadAllSnapshots()
                .Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));
            if (readyCount != routes)
                throw new InvalidOperationException($"仅 {readyCount}/{routes} 路就绪");
            Console.WriteLine($"magnifybench: {routes} 路就绪 ✓");

            await System.Threading.Tasks.Task.Delay(600);
            var shapes = GridPartition.ShapesFor(Math.Max(routes, Grid.Count));

            // 闸门的像素预算 = 对比区面积 × z² ⇒ 窗口大小直接决定闸门会不会放行。
            // 默认按用户配置恢复的窗口跑（真实形态）；置 _3FC_BENCH_MAXIMIZE=1 则先最大化，
            // 用来覆盖"大窗口 + 高倍"这一最吃显存的形态（不设环境变量就不改窗口，避免污染）。
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_3FC_BENCH_MAXIMIZE")))
            {
                WindowState = WindowState.Maximized;
                await System.Threading.Tasks.Task.Delay(800);
                Console.WriteLine($"magnifybench: 已最大化（窗口 {Width:F0}x{Height:F0} DIP）");
            }

            _step = "基准-进入对比";
            await EnterGridShapeAsync(shapes[^1].Cols, shapes[^1].Rows);
            await System.Threading.Tasks.Task.Delay(400);

            // 播放（不循环）：素材够长即可；用与 multitest 相同的入口
            _sync.Play();
            _isPlaying = true;
            await System.Threading.Tasks.Task.Delay(2000);

            // ── 阶段一：基线 z=1（不放大）──
            ReportBenchGeometry(1.0, accepted: true, routes, budgetOverride);
            await SampleBenchPhaseAsync("baseline", 1.0, secondsPerPhase);

            // ── 阶段二：放大 z ──
            _step = $"放大 z={zoom}";
            // z ≤ 1 是"关闭放大"，没有第二个阶段可测（再跑一遍与基线等价，纯浪费被崩溃窗口吃掉的时间）
            if (zoom <= 1.0)
            {
                Console.WriteLine("magnifybench: z≤1（不放大）⇒ 只测基线阶段");
                Console.WriteLine("magnifybench: 完成 ✓");
                code = 0;
                return;
            }

            var accepted = SetCompareMagnify(zoom, 0.25, 0.25, budgetOverride);
            if (accepted)
            {
                UpdateLayout();
                await System.Threading.Tasks.Task.Delay(400);   // 等 AfterRender 的 ShowInBounds 落地
                UpdateLayout();
                ApplyCompareCrop();
                await System.Threading.Tasks.Task.Delay(400);
            }
            ReportBenchGeometry(zoom, accepted, routes, budgetOverride);

            if (!accepted)
            {
                // docs/41 #15：原先闸门拒绝也 code=0 并打印"完成 ✓" ⇒ 放大阶段**一个采样都没做**，
                // 与"跑完并通过"完全无法区分（闸门若因几何估算错误误拒，基准实验即为空跑成功）。
                // 改为专用退出码 4（SKIPPED ≠ PASS），措辞不再带 ✓。
                Console.WriteLine($"magnifybench: ⚠ SKIPPED 闸门拒绝 z={zoom}（未做放大阶段采样，不作为通过依据）");
                Console.WriteLine("magnifybench: 未完成（exit=4 SKIPPED）");
                code = 4;
                return;
            }

            var (deltas, fps, magnifiedClockOk) = await SampleBenchPhaseAsync("magnified", zoom, secondsPerPhase);

            // ── 平移提交成本实测（docs/26 那条"不要逐帧 SetWindowRgn"当初是估算，没有数）──
            // 对比模式当前拖动是"松手才一次提交"，所以"实时提交到底贵不贵"从来没有读数；
            // 没数就没资格改那个决定。这里在固定倍率下按 60Hz 连发 N 次微小 crop 变更
            // （正是拖动每帧会做的事），逐次计时，并用与 SampleBenchPhaseAsync **同一口径**
            // 统计这一段的出帧率（presented 取差值，不拿累计计数器相加）。
            // ⚠ 只打印、不判红：本模式的判据仍是"放大后是否持续出帧"，这条是喂决策的观测。
            _step = "平移提交成本";
            {
                const int CommitCalls = 60;
                var cropMax = CompareCropPlanner.MaxCropFraction(zoom);
                // 两个必须**不同**的 crop 交替下发：同值会被 SetCompareMagnify 的去重吞掉，
                // 那测出来就是"什么都没做"的空转时间（生效数会掉到 1，届时照实打印）。
                var cropA = _compareCropX;
                var cropB = cropA + 0.01 <= cropMax ? cropA + 0.01 : Math.Max(0.0, cropA - 0.01);
                var per = new double[CommitCalls];
                var applied = 0;
                var frameStart = SnapshotTuple(_sync.ReadAllSnapshots());
                var swAll = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < CommitCalls; i++)
                {
                    var swOne = System.Diagnostics.Stopwatch.StartNew();
                    if (SetCompareMagnify(zoom, i % 2 == 0 ? cropA : cropB, _compareCropY)) applied++;
                    swOne.Stop();
                    per[i] = swOne.Elapsed.TotalMilliseconds;
                    await System.Threading.Tasks.Task.Delay(16);
                }
                swAll.Stop();
                var frameEnd = SnapshotTuple(_sync.ReadAllSnapshots());
                var moved = new long[frameEnd.Length];
                for (var i = 0; i < frameEnd.Length && i < frameStart.Length; i++)
                    moved[i] = frameEnd[i].presented - frameStart[i].presented;
                var commitWall = Math.Max(0.001, swAll.Elapsed.TotalSeconds);
                var commitFps = moved.Length > 0 ? moved.Sum() / commitWall / moved.Length : 0.0;
                Array.Sort(per);
                Console.WriteLine(
                    $"magnifybench: [平移提交] z={zoom:0.#} n={CommitCalls} 生效={applied} " +
                    $"cropA={cropA:F4} cropB={cropB:F4} 单次 p50={per[CommitCalls / 2]:F2} " +
                    $"p95={per[(int)(CommitCalls * 0.95) - 1]:F2} max={per[^1]:F2} ms ⇒ " +
                    $"{CommitCalls / commitWall:F0} 次/秒 提交下 {commitFps:F2} fps/路（观测，不判红）");
                Console.Out.Flush();
            }

            // 可选：放大**生效之后**再把窗口拉大，验证「拉大窗口绕过闸门」这条被堵死
            // （闸门必须在真正分配之前复核，而不是只在入口检查一次）。置 _3FC_BENCH_MAXIMIZE_AFTER=1。
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_3FC_BENCH_MAXIMIZE_AFTER")))
            {
                _step = "放大后拉大窗口";
                WindowState = WindowState.Maximized;
                await System.Threading.Tasks.Task.Delay(1200);
                UpdateLayout();
                ApplyCompareCrop();
                await System.Threading.Tasks.Task.Delay(800);
                var reverted = Grid.CellMagnify is null;
                Console.WriteLine(
                    $"#BENCHRESIZE 拉大后 CellMagnify={(reverted ? "null(已回退)" : "仍生效(闸门可被绕过!)")} " +
                    $"estMpx={EstimateCompareMagnifyPixels(zoom) / 1e6:F1} " +
                    $"预算Mpx={_compareMagnifyBudget / 1e6:F1}");
                ReportBenchGeometry(zoom, accepted: !reverted, routes, budgetOverride);
                await SampleBenchPhaseAsync("after-resize", zoom, 3);
                if (!reverted)
                    throw new InvalidOperationException("拉大窗口后闸门未复核，放大仍生效（可绕过闸门）");
                Console.WriteLine("magnifybench: 拉大窗口后闸门复核并回退 ✓");
            }

            // 判据：放大后仍持续出帧。0 增长 = 渲染死了（卡死/崩溃前兆），如实报 1。
            // ⚠ 但"0 增长"有两种成因，必须分开：**媒体时钟也没走** ⇒ 素材跑到末尾（夹具问题，
            //   不是产品停滞）；**时钟照走而画面不出帧** ⇒ 真停滞。混在一起报会把 EOF 说成渲染故障。
            var stalledRoutes = deltas.Count(d => d <= 0);
            ResetCompareMagnify();
            UpdateLayout();
            await System.Threading.Tasks.Task.Delay(200);

            if (!magnifiedClockOk)
            {
                Console.WriteLine(
                    $"magnifybench: 放大后 {fps:F2} fps/路（**不采信**：媒体时钟没走满，见上面那条 ⚠）");
                Console.WriteLine("magnifybench: 未完成（exit=4 SKIPPED）");
                code = 4;
                return;
            }
            Console.WriteLine(
                $"magnifybench: 放大后平均 {fps:F2} fps/路，停滞路数={stalledRoutes}/{deltas.Length}");
            if (stalledRoutes > 0)
                Console.Error.WriteLine($"magnifybench: ✗ z={zoom} 下有 {stalledRoutes} 路停止出帧（渲染未持续）");
            else
                Console.WriteLine("magnifybench: 完成 ✓");
            code = stalledRoutes > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magnifybench[步骤{_step}]: 失败 ✗ {ex.Message}");
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    /// <summary>采样一个阶段（持续播放 <paramref name="seconds"/> 秒），逐路统计 presented 增量。
    /// <para>返回每路的 presented 增量与"每路平均 fps"。fps 由 <b>presented 帧数/墙钟</b>算出 ——
    /// 这是唯一能反映"真的在出帧"的指标（位置推进只说明解码线程在跑，可能是丢帧硬撑）。</para>
    /// <para>每 1s 采一次并检测"本区间内零增长"：单次零增长记一次 hitch（可能是渲染器切换的瞬时抖动），
    /// 而**整段零增长**才算停滞 —— 直接拿整段增量判 0 会把瞬时抖动误报成卡死。</para></summary>
    private async System.Threading.Tasks.Task<(long[] deltas, double fps, bool clockAdvanced)> SampleBenchPhaseAsync(
        string name, double zoom, int seconds)
    {
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var start = SnapshotTuple(_sync.ReadAllSnapshots());
        var last = start.Select(x => x.presented).ToArray();
        var hitches = new int[start.Length];
        var posStart = _sync.GetMasterPosition100ns();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await System.Threading.Tasks.Task.Delay(1000);
            var cur = SnapshotTuple(_sync.ReadAllSnapshots());
            for (var i = 0; i < last.Length && i < cur.Length; i++)
            {
                if (cur[i].presented <= last[i]) hitches[i]++;
                last[i] = cur[i].presented;
            }
        }

        var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var end = SnapshotTuple(_sync.ReadAllSnapshots());
        var posEnd = _sync.GetMasterPosition100ns();
        var wall = (t1 - t0) / 1000.0;
        var deltas = new long[end.Length];
        for (var i = 0; i < end.Length && i < start.Length; i++)
            deltas[i] = end[i].presented - start[i].presented;
        var avgFps = wall > 0 && deltas.Length > 0 ? deltas.Sum() / wall / deltas.Length : 0;
        var clockSec = (posEnd - posStart) / (double)TimeSpan.TicksPerSecond;
        var clockRatio = wall > 0 ? clockSec / wall : 0;
        // ⚠ **时钟没走满 ⇒ 这一档测的不是吞吐**：素材跑到末尾（本仓 720p 派生件正好 12.0s / 720 帧，
        //   两阶段 + 预热就超了）或播放被 park。此时 presented 停止增长，fps 会读成个位数，
        //   看起来像"放大让帧率崩了"。2026-10-01 实测：拼合与对照两臂的 z=2 阶段都报
        //   wall=8.08 / clock=1.34~1.43 ⇒ 那两个 10.69 / 8.89 fps 全是 EOF 假信号。
        //   它与"真停滞"要分开：**媒体时钟在走而画面不出帧**才是停滞（原判据），
        //   **时钟自己没走**是夹具耗尽 ⇒ 判 SKIPPED，不报 fps。
        var clockAdvanced = clockRatio >= 0.8;

        Console.WriteLine(
            $"#BENCHPHASE name={name} zoom={zoom} t0={t0} t1={t1} wall={wall:F2} fps={avgFps:F2} " +
            $"clock={clockSec:F2} clockRatio={clockRatio:F2} valid={(clockAdvanced ? "yes" : "NO")} " +
            $"hitches=[{string.Join(",", hitches)}] presented=[{string.Join(",", deltas)}]");
        if (!clockAdvanced)
            Console.WriteLine(
                $"magnifybench: ⚠ 阶段 {name} 的媒体时钟只推进 {clockSec:F2}s / 墙钟 {wall:F2}s" +
                " ⇒ 素材跑到末尾或播放被停，**这一档的 fps 不进任何结论**（换更长素材或缩短 -Seconds）");
        Console.Out.Flush();
        return (deltas, avgFps, clockAdvanced);
    }

    /// <summary>打印本阶段的实际几何：**按 GetWindowRect 累加各路子 HWND 的真实像素**（这才是
    /// swapchain 尺寸，也才是显存开销的来源），并与闸门用的估算值并列 —— 两者不一致就说明
    /// 估算函数与实际窗口尺寸脱节（闸门会因此失效）。</summary>
    private void ReportBenchGeometry(double zoom, bool accepted, int routes, double budgetOverride)
    {
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = _partition.ComputeCells();
        double totalMpx = 0;
        var sizes = new System.Collections.Generic.List<string>();
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || !s.IsVisible || s.Hwnd == nint.Zero) continue;
            var r = ReadWindowRectPx(s.Hwnd);
            if (!r.IsValid) continue;
            totalMpx += (double)r.Width * r.Height / 1e6;
            sizes.Add($"{r.Width}x{r.Height}");
        }
        var estMpx = EstimateCompareMagnifyPixels(zoom) / 1e6;
        Console.WriteLine(
            $"#BENCHGEOM zoom={zoom} gate={(accepted ? "accept" : "reject")} " +
            $"totalMpx={totalMpx:F1} estMpx={estMpx:F1} windows=[{string.Join(",", sizes)}] " +
            $"scaling={scaling:0.##} cells={cells.Length} " +
            $"compareArea={Grid.Bounds.Width:F0}x{Grid.Bounds.Height:F0}DIP " +
            $"window={Width:F0}x{Height:F0}DIP state={WindowState} " +
            $"budgetMpx={(budgetOverride > 0 ? budgetOverride / 1e6 : CompareMagnifyPixelBudget / 1e6):F1}");
        Console.Out.Flush();
    }

    /// <summary>断言覆盖层处于可用状态：已进入、可见、钩子已装、逐像素透明、分区形状与手柄数可读且相符。
    /// 任一不满足即抛 —— 这些正是"接线是否真的生效"的判据，静默通过等于没验证。</summary>
    private void AssertCompareOverlay(int routes, int cols, int rows)
    {
        if (!_compareActive)
            throw new InvalidOperationException("对比模式未进入（可能被 B3 fail-fast 自动退出，见上方日志）");
        var overlay = _layoutOverlay ?? throw new InvalidOperationException("覆盖层实例为空");
        if (!overlay.IsOverlayVisible) throw new InvalidOperationException("覆盖层不可见");
        if (!overlay.HitTestHookInstalled)
            throw new InvalidOperationException("WM_NCHITTEST 钩子未安装（覆盖层会吞掉对比区鼠标消息）");
        if (overlay.AchievedTransparency != WindowTransparencyLevel.Transparent)
            throw new InvalidOperationException($"未取得逐像素透明（实际 {overlay.AchievedTransparency}）");

        // Z 序闸门：覆盖层必须靠 Win32 owner 关系浮在主窗口（及其视频子 HWND）之上，而**不是**
        // 靠 Topmost 置顶带 —— 后者会连其他应用的窗口一起盖住，是真机体验缺陷。
        // 真机 Z 序无法在进程内断言，这两项是它的可自动判定的必要条件：owner 没挂上 ⇒ 可能盖不住
        // 视频画面（airspace 退化）；仍带 WS_EX_TOPMOST ⇒ 一定还浮在其他应用之上。
        if (!overlay.OwnerHwndAttached)
            throw new InvalidOperationException(
                "覆盖层未建立 Win32 owner 关系（GetWindow(GW_OWNER) ≠ 主窗口 HWND）⇒ 可能盖不住视频画面");
        if (overlay.HasTopmostStyle)
            throw new InvalidOperationException("覆盖层仍带 WS_EX_TOPMOST（会浮在其他应用窗口之上）");
        // airspace 硬要求：覆盖层必须在主窗口之上（⇒ 也在其视频子 HWND 之上），否则分割线被画面盖住。
        if (!overlay.IsAboveHostInZOrder)
            throw new InvalidOperationException(
                "覆盖层未排在主窗口之上（顶层 Z 序）⇒ 分割线会被视频子 HWND 盖住（airspace 退化）");

        // 覆盖层持有的必须就是宿主那份分区（两条路径共用一份几何；各算一份迟早漂移，
        // 表现是"线条在动、画面不动"）。形状与手柄数量按夹具独立算一遍再比，不回读宿主字段。
        var overlayPartition = overlay.Partition;
        if (overlayPartition.Cols != cols || overlayPartition.Rows != rows)
            throw new InvalidOperationException(
                $"覆盖层形状 {overlayPartition.Cols}x{overlayPartition.Rows} != 期望 {cols}x{rows}");
        var expectedHandles = Math.Max(0, cols - 1) + Math.Max(0, rows - 1);
        if (overlayPartition.HandleCount != expectedHandles)
            throw new InvalidOperationException(
                $"{cols}x{rows} 应有 {expectedHandles} 条可拖分割线，覆盖层报 {overlayPartition.HandleCount}");
        if (overlay.HandlePositions().Length != expectedHandles)
            throw new InvalidOperationException(
                $"覆盖层实际画了 {overlay.HandlePositions().Length} 个手柄，规则要求 {expectedHandles} 个");

        Console.WriteLine(
            $"comparemodetest: 分区={overlayPartition.Cols}x{overlayPartition.Rows} " +
            $"手柄={expectedHandles} 边界=[{string.Join(" ", overlay.HandlePositions().Select(
                hp => $"({hp.X:F3},{hp.Y:F3})"))}] " +
            $"透明={overlay.AchievedTransparency} 钩子={overlay.HitTestHookInstalled} 可见={overlay.IsOverlayVisible} " +
            $"owner={overlay.OwnerHwndAttached} topmost={overlay.HasTopmostStyle} " +
            $"高于主窗口={overlay.IsAboveHostInZOrder} ✓");
    }

    /// <summary>核心验收点：断言"格表确实驱动了画面排版"。
    ///
    /// <para>判据（对每一路逐一核对，不看聚合值）：</para>
    /// <list type="bullet">
    /// <item><description>第 i 路（i &lt; 格数）可见，且其 <c>Bounds</c> 等于
    /// <see cref="GridPartition.ComputeCells"/> 第 i 格乘上对比区实际尺寸（含与
    /// <c>ArrangeOverride</c> 相同的 1px 内缩）——容差 1 DIP，吸收浮点与像素对齐误差；</description>
    /// <item><description>合并态下格数 ≡ lane 数，所以"<b>每一路都可见</b>"本身就是判据：
    /// 一旦哪天又退回"固定 2~4 格、多余路隐藏"，这里立刻红。</description></item>
    /// </list>
    ///
    /// <para>期望值由 <see cref="GridPartition.ComputeCells"/> <b>独立复算</b>，不是回读 Grid 内部状态，
    /// 因此能真正发现"格表没被用于布局"（Bounds 仍是均匀网格）这类回归。</para></summary>
    private void AssertCellsDriveLayout(int routes, string label)
    {
        UpdateLayout(); // 强制一次布局，把 InvalidateMeasure 落地

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"{label}：对比区尺寸非法 {w:F1}x{h:F1}");

        if (_compareOverlayActive)
            throw new InvalidOperationException(
                $"{label}：左右拉动态下格表是「两路铺满」，不该走本判据（另有揭示区断言）");

        var cells = _partition.ComputeCells();
        // 拼合模式下"格"不再是窗口的尺寸，而是这一路该露出的**区域** ⇒ 各路窗口都铺满对比区，
        // Bounds 不再携带"显示在哪一块"。判据跟着语义换成两层：① 窗口 == 整容器（拼合排布生效）；
        // ② 回读窗口区域 == 它那一格（格表真的决定了露出哪一块）。②需要区域已下发 ⇒ 本判据
        // 自己走一次生产入口 ApplyCompareCrop（内部按区域去重，几何没变时是一次字典查询）。
        var mosaic = Grid.MosaicWindows;
        if (mosaic) ApplyCompareCrop();
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");

            if (i >= cells.Length)
            {
                // 合并态走不到这里：格数 ≡ lane 数。真走到了就是格数规则被改坏，必须点名。
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路超出 {_partition.Cols}x{_partition.Rows} 的 {cells.Length} 格 —— " +
                    "网格对比的基准是一路一格，不该有路被挤到格表之外");
            }

            if (!s.IsVisible)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路应可见（{_partition.Cols}x{_partition.Rows} 第 {i} 格）却不可见");

            if (mosaic)
            {
                var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
                var gotWin = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
                var expWin = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, w, h), scaling);
                if (!BlockClose(gotWin, expWin))
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路窗口 {gotWin} 未铺满对比区 {expWin} ⇒ 拼合排布没生效，" +
                        "这一路显示的还是「缩小版整幅」而不是自己那一块");
                AssertRouteBlockAt(i, CellOfRoute(i, cells, label), label);
                continue;
            }

            var exp = ExpectedBounds(CellOfRoute(i, cells, label), w, h);
            var got = s.Bounds;
            if (!BoundsClose(got, exp))
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路 Bounds={got} 与 ComputeCells 期望 {exp} 不符（格表未驱动布局）");
        }

        Console.WriteLine(
            $"comparemodetest: [{label}] 格表驱动布局 ✓ 形状={_partition.Cols}x{_partition.Rows} " +
            $"格数={cells.Length} 对比区={w:F1}x{h:F1} " +
            $"手柄={_partition.HandleCount} 边界=[{string.Join(" ", _partition.HandlePositions().Select(
                hp => $"({hp.X:F3},{hp.Y:F3})"))}] 各路{(mosaic ? "窗口铺满对比区且区域==格" : " Bounds 与 ComputeCells 一致")}");
    }

    /// <summary>把归一化格矩形换算成 <see cref="CompareGridView"/> 实际排布出的 DIP 矩形：
    /// 与 <c>ArrangeOverride</c> 一致地含 1px 内缩。供"独立复算"用，不读 Grid 内部状态。</summary>
    private static Rect ExpectedBounds(CellRect c, double areaW, double areaH) =>
        new(c.X * areaW + 1, c.Y * areaH + 1,
            Math.Max(0, c.Width * areaW - 2), Math.Max(0, c.Height * areaH - 2));

    // ══════════ 拼合（mosaic）下的统一观测量：第 i 路"实际露出的那块" ══════════
    //
    // 排布语义换了，判据的**观测量**必须跟着换：旧的是"窗口 == 格"，格表直接写在 Bounds 上；
    // 拼合是"窗口 == 整对比区 + 区域裁出自己那一块"，各路 Bounds 全都相同、不再携带位置信息。
    // 于是"这一路显示在哪一块"只能从 Win32 窗口区域回读 —— 下面三个 helper 是这一件事的
    // 唯一表述点，所有按格判定的单元都走它，避免每个单元各写一份回读。

    /// <summary>路 → 它应当占的那一格。必须用 <see cref="CompareGridView.CellIndexOfRoute"/>
    /// 反查（与生产 <c>ApplyCompareCrop</c> 同一个方向）：互换 / 轮换之后"格↔路"不再是恒等，
    /// 按路号直取 <c>cells[i]</c> 会把判据钉在错的块上（2026-10-01 的实读判红：2x3 下第 0 路
    /// 回读 Y=712、期望 Y=0）。</summary>
    private CellRect CellOfRoute(int route, CellRect[] cells, string label)
    {
        var index = Grid.CellIndexOfRoute(route);
        if (index < 0 || index >= cells.Length)
            throw new InvalidOperationException(
                $"{label}：第 {route} 路不在 {_partition.Cols}x{_partition.Rows} 的 {cells.Length} 格里" +
                $"（映射={(Grid.CellRoute is { } r ? string.Join(",", r) : "恒等")}）");
        return cells[index];
    }

    /// <summary>某一路<b>实际露出</b>的容器坐标物理矩形。
    /// <para>拼合：窗口原点 + <c>GetWindowRgnBox</c> 回读的区域（区域是唯一能表达"露出哪一块"的
    /// 观测量）；回读到"无区域"时原样返回窗口矩形 —— 拼合下那就是"这一路铺满全屏、盖住邻居"，
    /// 由调用方的判据点名，不在这里替它辩解。</para>
    /// <para>非拼合（左右拉动、或未来的整窗排布）：窗口本身就是那一块，区域只裁 Avalonia
    /// 向外取整的 ≤1px 外扩 ⇒ 直接返回窗口矩形。</para></summary>
    private Rect32 RouteVisibleRectPx(int route, double scaling, string label)
    {
        var s = Grid.GetSurface(route) ?? throw new InvalidOperationException($"{label}：第 {route} 路缺失");
        if (s.Hwnd == nint.Zero)
            throw new InvalidOperationException($"{label}：第 {route} 路子 HWND 未创建，区域无从回读");
        var win = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
        if (!Grid.MosaicWindows) return win;
        var type = ReadWindowRegion(s.Hwnd, out var box);
        return type is RegionTypeSimple or RegionTypeComplex
            ? new Rect32(win.X + box.X, win.Y + box.Y, box.Width, box.Height)
            : win;
    }

    /// <summary>某一路<b>应当露出</b>的容器坐标物理矩形 = 它那一格。
    /// 口径与生产同源但**分别复算**：拼合用不留缝的 <c>MosaicRegionDip</c>（留缝会在两块之间
    /// 露出父窗底色，拼合当场破功），其余用内缩 1px 的 <c>CellToContainerDip</c>。</summary>
    private Rect32 ExpectedBlockPx(CellRect cell, double areaW, double areaH, double scaling)
        => CompareCropPlanner.ToPhysicalRect(
            Grid.MosaicWindows ? CompareCropPlanner.MosaicRegionDip(cell, areaW, areaH)
                               : CompareCropPlanner.CellToContainerDip(cell, areaW, areaH), scaling);

    /// <summary>物理矩形比较，容差 1px：期望与实际都经 <see cref="CompareCropPlanner.ToPhysicalRect"/>
    /// 同一套取整，1px 只吸收 DIP→物理的取整残差，不吸收几何错（几何错在这里是整块级别）。</summary>
    private static bool BlockClose(Rect32 got, Rect32 exp) =>
        Math.Abs(got.X - exp.X) <= 1 && Math.Abs(got.Y - exp.Y) <= 1 &&
        Math.Abs(got.Width - exp.Width) <= 1 && Math.Abs(got.Height - exp.Height) <= 1;

    /// <summary>断言：第 <paramref name="route"/> 路实际露出的块 == 格表里它那一格。
    /// 拼合与非拼合共用（差异全在 <see cref="RouteVisibleRectPx"/> 与
    /// <see cref="ExpectedBlockPx"/> 两个口径里）。</summary>
    private void AssertRouteBlockAt(int route, CellRect cell, string label)
    {
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var got = RouteVisibleRectPx(route, scaling, label);
        var exp = ExpectedBlockPx(cell, Grid.Bounds.Width, Grid.Bounds.Height, scaling);
        if (!BlockClose(got, exp))
            throw new InvalidOperationException(
                $"{label}：第 {route} 路露出的块 {got} != 格表期望 {exp}" +
                $"（{(Grid.MosaicWindows ? "窗口区域没跟着格表迁" : "窗口没跟着格表挪")}）");
    }

    /// <summary>DIP 矩形比较：容差 1 DIP，吸收浮点与像素对齐误差。</summary>
    private static bool BoundsClose(Rect got, Rect exp) =>
        Math.Abs(got.X - exp.X) <= 1.0 && Math.Abs(got.Y - exp.Y) <= 1.0 &&
        Math.Abs(got.Width - exp.Width) <= 1.0 && Math.Abs(got.Height - exp.Height) <= 1.0;

    /// <summary>归一化格矩形是否相等（浮点容差远小于 <see cref="GridPartition.Epsilon"/> 量级）。</summary>
    private static bool CellEquals(CellRect a, CellRect b) =>
        Math.Abs(a.X - b.X) <= GridPartition.Epsilon && Math.Abs(a.Y - b.Y) <= GridPartition.Epsilon &&
        Math.Abs(a.Width - b.Width) <= GridPartition.Epsilon &&
        Math.Abs(a.Height - b.Height) <= GridPartition.Epsilon;

    // ══════════ #22：放大镜浮窗坐标换算验证 ══════════
    //
    // 缺陷形态：MagnifierOverlay.UpdateAt 把"surface 局部坐标"当成"父容器 CenterPanel 的坐标"
    // 来摆浮窗（代码注释自认"需要换算"但没做）⇒ **只有第 1 格正确**（它的原点≈(1,1)），
    // 2×2 的第 4 路、3×3 的第 9 路偏移可达数百 DIP，放大（CellMagnify）后更会飞出画面。
    //
    // 为什么必须用实机几何断言而不是单测：换算依赖**视觉树变换链**（TranslatePoint），
    // 离线造不出等价链路；单测只能覆盖 PlaceOverlay 的偏移/翻转/钳制算术（见 Platform.Tests）。
    //
    // ⚠ 为什么期望值不能只由 Bounds 推出（本仓库已知教训）：NativeControlHost 把 ShowInBounds
    //   排在 AfterRender，`UpdateLayout()` 不会执行它 ⇒ 只看 Bounds 可能在"窗口还没搬到
    //   排布位置"时判绿。故这里额外用 GetWindowRect 实测子 HWND 的**相对位移**，
    //   与 TranslatePoint 给出的相对位移对照 —— 两者一致才说明换算依赖的几何是真实落地的。

    /// <summary>本步的"焦点载体"：取窗口里第一个可聚焦的 Button（用来复现"空格被按钮吃掉"），
    /// 取不到就退回窗口自身。</summary>
    private global::Avalonia.Visual FocusedRouteSource =>
        (global::Avalonia.Visual?)FindFocusableButton(this) ?? this;

    private static global::Avalonia.Controls.Button? FindFocusableButton(global::Avalonia.Visual root)
    {
        foreach (var c in root.GetVisualDescendants())
            if (c is global::Avalonia.Controls.Button b && b.Focusable && b.IsEnabled) return b;
        return null;
    }

    /// <summary>底栏 tooltip 必须说真话：含**当前绑定的键位**与**当前步进量**，
    /// 且不许残留旧版写死的 <c>Shift+←</c> 之类文案（那正是这次要修的假话）。
    /// 只读 ToolTip 文本，不复制任何拼接逻辑。</summary>
    private void AssertTransportTipsAreTruthful()
    {
        var b = _settings.KeyBindings;
        var secPrev = _transport.TipTextOf(_transport.TipProbeTargets().secPrev);
        var framePrev = _transport.TipTextOf(_transport.TipProbeTargets().framePrev);
        var play = _transport.TipTextOf(_transport.TipProbeTargets().playPause);
        var secKey = global::_3FCompare.Services.TransportKeys.Display(
            global::_3FCompare.Services.TransportKeys.Get(
                global::_3FCompare.Services.TransportKeys.Slot.StepSecondBackward, b));
        var frameKey = global::_3FCompare.Services.TransportKeys.Display(
            global::_3FCompare.Services.TransportKeys.Get(
                global::_3FCompare.Services.TransportKeys.Slot.StepFrameBackward, b));
        Log($"tooltip 取证：秒退='{secPrev}' 帧退='{framePrev}' 播放='{play}'");
        var bad = new System.Collections.Generic.List<string>();
        if (!secPrev.Contains(secKey)) bad.Add($"秒退 tooltip 未含当前键位 '{secKey}'");
        if (!framePrev.Contains(frameKey)) bad.Add($"帧退 tooltip 未含当前键位 '{frameKey}'");
        if (!secPrev.Contains(_settings.SecondsStep.ToString("0.##"))) bad.Add("秒退 tooltip 未含当前秒数");
        if (!framePrev.Contains(_settings.FrameStep.ToString())) bad.Add("帧退 tooltip 未含当前帧数");
        foreach (var tip in new[] { secPrev, framePrev, play })
            if (tip.Contains("Shift+")) bad.Add($"残留旧版写死的组合键文案：'{tip}'");
        if (bad.Count > 0)
            throw new InvalidOperationException("底栏 tooltip 与实际设置不符：" + string.Join("；", bad));
    }

    /// <summary>把一次按键按<b>真实路由</b>（隧道→目标→冒泡）打进去，目标就是 <paramref name="target"/>。
    /// <para>为什么必须这样：这步原先直接调 <c>TogglePlay()</c>，绕过了整条键盘路由，于是
    /// "空格被焦点按钮吃掉"这类缺陷它<b>看不见</b>——2026-09-26 实测：把 RedrawAll 摘掉它照样判绿。
    /// 现在目标换成一个可聚焦按钮，判据才与用户真正敲键盘时同一条路。</para></summary>
    private void RaiseKeyOn(global::Avalonia.Visual target, global::Avalonia.Input.Key key)
    {
        var src = target as global::Avalonia.Input.IInputElement;
        var routed = target as global::Avalonia.Interactivity.Interactive ?? this;
        routed.RaiseEvent(new global::Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent,
            Key = key, KeyModifiers = global::Avalonia.Input.KeyModifiers.None, Source = src,
        });
        routed.RaiseEvent(new global::Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyUpEvent,
            Key = key, KeyModifiers = global::Avalonia.Input.KeyModifiers.None, Source = src,
        });
    }

    /// <summary>断言放大镜浮窗跟随光标，且用的是**光标在容器坐标系里的位置**。
    ///
    /// <para>挑"离容器原点最远"的那一路做被测对象：只有它的原点偏移足够大，
    /// "没做换算"这类缺陷才会被暴露出来。<b>鉴别力实测</b>：把换算摘掉后，
    /// 4 路对比模式（<c>--comparemodetest</c>）下误差 717.3 DIP 判红；
    /// 单路（<c>--selftest</c>，格原点只差 1.33 DIP）下误差 1.3 DIP 也判红 ——
    /// 后者能判红靠的是断言用了精确期望值（容差 0.5 DIP），但只有 1px 量级，
    /// 真正有说服力的是多格场景。</para></summary>
    private async System.Threading.Tasks.Task AssertMagnifierFollowsCursorAsync(int routes, string label)
    {
        UpdateLayout();
        // 等一拍：让 AfterRender 的 ShowInBounds 把子 HWND 搬到 Bounds 位置，
        // 否则下面的 GetWindowRect 核对会读到上一拍的窗口矩形。
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        var container = Magnifier.HostContainer as Visual
            ?? throw new InvalidOperationException($"{label}：放大镜未指定宿主容器（HostContainer），无法核对浮窗坐标");
        var containerSize = (container as Control)?.Bounds.Size ?? default;
        if (containerSize.Width <= MagnifierOverlay.WidthPx || containerSize.Height <= MagnifierOverlay.HeightPx)
            throw new InvalidOperationException(
                $"{label}：容器 {containerSize} 放不下放大镜 {MagnifierOverlay.WidthPx}×{MagnifierOverlay.HeightPx}，" +
                "钳制会掩盖坐标错误，本环境无法判别");

        // 选原点离容器原点最远的一路。⚠ 多路时**必须排除参照那一路本身**（下面按 GetSurface(0) 当参照）：
        // 拼合排布下所有路的原点都 == 容器原点，"最远"会退化成第 0 路，于是 target == reference、
        // 整个 Win32 核对被 ReferenceEquals 挡掉 ⇒ 这一步**静默什么都不判**。从第 1 路起选，
        // 两条路都会走到判据：均匀网格给出数百 DIP 的真核对，拼合给出"不适用"的明说。
        PlayerSurface? target = null;
        var targetOrigin = default(Point);
        for (var i = routes > 1 ? 1 : 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || !s.IsVisible || s.Bounds.Width <= 0 || s.Bounds.Height <= 0) continue;
            var o = s.TranslatePoint(new Point(0, 0), container);
            if (o is null) continue;
            if (target is null || o.Value.X + o.Value.Y > targetOrigin.X + targetOrigin.Y)
            {
                target = s;
                targetOrigin = o.Value;
            }
        }
        if (target is null)
            throw new InvalidOperationException($"{label}：没有可见且已挂树的表面，无法核对浮窗坐标");

        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;

        // ① 独立核对"surface 在容器里的原点"：TranslatePoint 的相对位移必须与 GetWindowRect
        //    实测的相对位移一致（相对量 ⇒ 不需要知道容器的屏幕原点，也就绕开了"没有 HWND 可查"）。
        var reference = Grid.GetSurface(0);
        if (reference is not null && reference.Hwnd != nint.Zero && target.Hwnd != nint.Zero &&
            !ReferenceEquals(reference, target))
        {
            var r0 = ReadWindowRectPx(reference.Hwnd);
            var r1 = ReadWindowRectPx(target.Hwnd);
            if (r0.IsValid && r1.IsValid)
            {
                var refOrigin = reference.TranslatePoint(new Point(0, 0), container) ?? default;
                var avDelta = targetOrigin - refOrigin;
                var winDelta = new Point((r1.X - r0.X) / scaling, (r1.Y - r0.Y) / scaling);
                // 位移足够大才具备鉴别力；太小（两路几乎重合）时只提示不判红。
                if (Math.Abs(avDelta.X) + Math.Abs(avDelta.Y) > 20)
                {
                    if (Math.Abs(avDelta.X - winDelta.X) > 3 || Math.Abs(avDelta.Y - winDelta.Y) > 3)
                    {
                        // 判红之前先补一段**只加读数、不改判决**的有界重测。
                        // 起因：这条红 10-03 一天里中了 4/24 轮（不是先前记的 1/36），
                        // 而且三轮的读数**逐字节相同**（Avalonia Δ=(1024.7,474.7) Win32 Δ=(-1.3,-1.3)）
                        // ⇒ 不像竞态，像确定性状态。但"判据读得太早（几何晚到）"与"产品真没落地"
                        // 是两种完全不同的修法，光看这一行分不开（docs/50 §三十一之四）。
                        // 红照旧判 —— 不拿"再等等就好了"把一个真缺陷买成绿灯。
                        var late = winDelta;
                        var landedAt = 0;
                        for (var pump = 1; pump <= 8; pump++)
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                            UpdateLayout();
                            var q0 = ReadWindowRectPx(reference.Hwnd);
                            var q1 = ReadWindowRectPx(target.Hwnd);
                            if (!q0.IsValid || !q1.IsValid) break;
                            late = new Point((q1.X - q0.X) / scaling, (q1.Y - q0.Y) / scaling);
                            if (Math.Abs(late.X - avDelta.X) <= 3 && Math.Abs(late.Y - avDelta.Y) <= 3)
                            { landedAt = pump; break; }
                        }
                        throw new InvalidOperationException(
                            $"{label}：TranslatePoint 与 GetWindowRect 给出的子窗口相对位移不符：" +
                            $"Avalonia Δ=({avDelta.X:F1},{avDelta.Y:F1}) Win32 Δ=({winDelta.X:F1},{winDelta.Y:F1})" +
                            $"｜再泵布局 8 次后 Win32 Δ=({late.X:F1},{late.Y:F1})" +
                            (landedAt > 0 ? $"（第 {landedAt} 次落地 ⇒ 几何晚到，判据读得太早）"
                                          : "（8 次仍未落地 ⇒ 子 HWND 真没按 Bounds 落地）") +
                            "；换算所依赖的几何不可信");
                    }
                    Log($"   Win32 核对 ✓ 子窗口相对位移 Avalonia=({avDelta.X:F1},{avDelta.Y:F1}) " +
                        $"== Win32=({winDelta.X:F1},{winDelta.Y:F1})");
                }
                else
                {
                    // 明写"不适用"而不是"跳过"：Δ≈0 在**拼合排布**下是构造性的（各路窗口原点 == 容器原点），
                    // 不是"这环境测不了"。那条缺陷类（把 surface 局部坐标当容器坐标）只有在各路原点
                    // 互不重合的布局里可表达 ⇒ 同一判据在剧本里另有一次跑在**均匀网格**上（那里有牙齿）。
                    Log($"   不适用：两路相对位移仅 ({avDelta.X:F1},{avDelta.Y:F1}) DIP —— " +
                        "拼合排布下各路窗口原点就是容器原点，本核对无鉴别力；" +
                        "牙齿在「放大镜浮窗坐标-均匀网格」那一步（各路原点相差数百 DIP）");
                }
            }
            else
            {
                Log("   ⚠ GetWindowRect 读失败，跳过 Win32 位移核对（浮窗坐标断言仍然执行）");
            }
        }

        // ② 浮窗坐标断言。
        // 放大镜已搬进独立顶层窗（P1-4），与容器不在同一视觉树 —— 跨树 TranslatePoint 会返回
        // null，故全程改用**屏幕坐标**口径：期望值由 PlaceOverlay 给出（CenterPanel DIP 坐标），
        // 实际值读独立窗口的 Position（物理像素）换算回 DIP，与 CenterPanel 的屏幕原点对照。
        var local = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var cursorInContainer = target.TranslatePoint(local, container)
            ?? throw new InvalidOperationException($"{label}：surface→container 的 TranslatePoint 返回 null");
        var expectedInContainer = MagnifierOverlay.PlaceOverlay(cursorInContainer, containerSize);

        // 采样不参与本断言；顺带把 session 绑到被测路（与生产路径一致），避免读到别的路。
        if (_sync.Slots.ElementAtOrDefault(target.Index)?.Session is { } ms)
            Magnifier.AttachSession(ms);
        Magnifier.UpdateAt(target, local, scaling);
        // 让布局跑到放大镜（UpdateAt 里刚把 IsVisible 置 true）：只有跑完才能读到真实原点，
        // 否则读到的可能是"隐藏态"的 (0,0,0,0) —— 那正是"只信 Bounds 会假通过"的入口。
        UpdateLayout();

        var got = Magnifier.DesiredPosition;
        var err = Math.Max(Math.Abs(got.X - expectedInContainer.X), Math.Abs(got.Y - expectedInContainer.Y));
        if (err > 0.5)
            throw new InvalidOperationException(
                $"{label}：放大镜浮窗坐标错 {err:F1} DIP —— 实际 {got} ≠ 期望 {expectedInContainer}；" +
                $"光标在容器内 {cursorInContainer}、被测路原点 {targetOrigin}（surface {target.Index}）。" +
                "疑为把 surface 局部坐标当成容器坐标（#22：漏了 TranslatePoint 换算）");
        // 独立窗口的实际落点也必须对上：Position（物理像素）→ 相对 CenterPanel 的 DIP 偏移。
        // 只验 DesiredPosition 不够 —— 它只是"计算值"，窗口是否真被摆过去要看 Win32 落点。
        var winPos = _magnifierWindow.Position;
        var panelScreen = container.PointToScreen(new Point(0, 0));
        var winInContainer = new Point((winPos.X - panelScreen.X) / scaling, (winPos.Y - panelScreen.Y) / scaling);
        var winErr = Math.Max(Math.Abs(winInContainer.X - expectedInContainer.X),
                              Math.Abs(winInContainer.Y - expectedInContainer.Y));
        if (winErr > 2.0)
            throw new InvalidOperationException(
                $"{label}：放大镜覆盖窗实际落点偏 {winErr:F1} DIP —— 实际 {winInContainer} ≠ 期望 {expectedInContainer}" +
                $"（DesiredPosition={got}，窗口 Position={winPos}，面板屏幕原点={panelScreen}）");
        // ③ 可见性判据（P1-4）：坐标对 ≠ 看得见。
        //    放大镜是 Avalonia 自绘内容，视频面是**子 HWND**——子 HWND 恒在父窗口自绘内容之上
        //    （airspace）。只核对几何的话，"位置算对了、但整块被视频盖住"依旧全绿。
        //    ⚠ 定罪手段必须是 **Z 序**（`IsAboveHostInZOrder`，与分割线覆盖层同源），
        //      不能是 `WindowFromPoint`：放大镜覆盖窗带 `WS_EX_TRANSPARENT`（鼠标穿透是硬需求，
        //      否则它会吞掉视频的拖拽/滚轮），命中测试因此**按设计**返回下层窗口 ⇒ 该探针对
        //      "覆盖层在不在上面"没有分辨力，反而会在修复真正生效时报告"被遮挡"。
        //      旧版正是拿 `hit == 视频面` 当充分证据，所以它恰好在修复生效的那一臂判红。
        //    WindowFromPoint 保留为**读数**：它与 Z 序结论相反本身就是信息（说明穿透样式在起作用）。
        //    取矩形与探点必须在**同一次快照**里做：视频子 HWND 的落位是异步的（AfterRender）。
        var scalingForProbe = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var magTopLeft = Magnifier.PointToScreen(new Point(0, 0));
        var magW = (int)Math.Round(MagnifierOverlay.WidthPx * scalingForProbe);
        var magH = (int)Math.Round(MagnifierOverlay.HeightPx * scalingForProbe);

        var probedAny = false;
        var probeHitVideo = 0;
        int lastPx = 0, lastPy = 0;
        var overlayHwnd = _magnifierWindow.OverlayHwnd;
        var mainHwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        var probeLog = new System.Text.StringBuilder();
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || s.Hwnd == nint.Zero) continue;
            var v = ReadWindowRectPx(s.Hwnd);           // 同拍：与 magTopLeft 一起构成一份快照
            if (!v.IsValid) continue;
            var ix = Math.Max(magTopLeft.X, v.X);
            var iy = Math.Max(magTopLeft.Y, v.Y);
            var ix2 = Math.Min(magTopLeft.X + magW, v.X + v.Width);
            var iy2 = Math.Min(magTopLeft.Y + magH, v.Y + v.Height);
            if (ix >= ix2 || iy >= iy2)
            {
                probeLog.Append($" r{i}:不相交(视频面={v.X},{v.Y},{v.Width}x{v.Height})");
                continue;                               // 与该路视频面不相交，无从遮挡
            }
            probedAny = true;
            var probe = new POINT { x = (ix + ix2) / 2, y = (iy + iy2) / 2 };
            lastPx = probe.x; lastPy = probe.y;
            var hit = WindowFromPoint(probe);
            var who = hit == s.Hwnd ? "视频面" :
                      hit == overlayHwnd ? "覆盖窗" :
                      hit == mainHwnd ? "主窗" : "其他";
            probeLog.Append($" r{i}:探点=({probe.x},{probe.y}) 交集={ix2 - ix}x{iy2 - iy} " +
                            $"命中=0x{hit:X}({who})");
            if (hit == s.Hwnd) probeHitVideo++;
        }
        // Z 序取证（定罪用）：owner 关系、是否误用置顶、以及自顶向下扫描谁先出现。
        var zOwner = _magnifierWindow.OwnerHwndAttached;
        var zTopmost = _magnifierWindow.HasTopmostStyle;
        var zPos = _magnifierWindow.QueryZOrderPosition();
        var zAbove = zPos == Controls.MagnifierOverlayWindow.ZOrderPosition.Above;
        // 时序判别（**纯观测，不改判决**）：Below 会不会下一帧就自己好。
        // 会好 ⇒ 断言采样过早，该修的是"等多久"（同浮条收敛轮询那一类）；
        // 一直不好 ⇒ 覆盖窗确实长时间排在主窗之下，才需要补插。
        // 判决仍按第一次读数：不能"等到它好了再断言"，那等于放宽判据。
        var zRetry = "";
        if (!zAbove)
        {
            for (var k = 0; k < 6; k++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await System.Threading.Tasks.Task.Delay(30);
                var again = _magnifierWindow.QueryZOrderPosition();
                zRetry += $" {k}:{again}";
                if (again == Controls.MagnifierOverlayWindow.ZOrderPosition.Above) break;
            }
        }
        Log($"   airspace 取证：放大镜屏幕矩形=({magTopLeft.X},{magTopLeft.Y}) {magW}x{magH} " +
            $"覆盖窗 HWND=0x{overlayHwnd:X} 主窗 HWND=0x{mainHwnd:X} " +
            $"Z序[owner={zOwner} topmost={zTopmost} 在宿主之上={zAbove} " +
            $"位置={zPos} win32可见={_magnifierWindow.Win32WindowVisible} " +
            $"avalonia可见={_magnifierWindow.IsVisible} 覆盖窗句柄已取={overlayHwnd != nint.Zero}" +
            $" 补插次数={_magnifierWindow.ZOrderReasserts} 主窗topmost={_magnifierWindow.HostHasTopmostStyle}]" +
            (zRetry.Length > 0 ? $" 重试链(每30ms,判决仍按首次)={zRetry}" : "") + " " +
            $"探到相交={probedAny} WindowFromPoint命中视频面={probeHitVideo}/{(probedAny ? routes : 0)}" +
            $"{probeLog}" +
            (probedAny ? $" 该点自顶向下顶层链={_magnifierWindow.DescribeZOrderAt(lastPx, lastPy)}" : "") +
            "（读数说明：覆盖窗带 WS_EX_TRANSPARENT ⇒ WindowFromPoint 命中下层属预期，" +
            "只作参考；定罪只认 Z 序那一项。）");
        if (!zOwner)
            throw new InvalidOperationException(
                $"{label}：放大镜覆盖窗未建立 Win32 owner 关系（GetWindow(GW_OWNER) 为空）⇒ " +
                "Z 序无来源，会被视频子 HWND 盖住（P1-4）。修法见 MagnifierOverlayWindow.ShowOverlay 的 Owner 赋值。");
        if (zTopmost)
            throw new InvalidOperationException(
                $"{label}：放大镜覆盖窗带 WS_EX_TOPMOST ⇒ 切到别的应用后仍浮在最上面，是真机体验缺陷。");
        if (!zAbove)
            throw new InvalidOperationException(
                $"{label}：放大镜覆盖窗在顶层 Z 序上**不**排在主窗之上（P1-4 成立）——" +
                $"坐标是对的（误差 {err:F1} DIP），但用户看不见。owner={zOwner} 覆盖窗 HWND=0x{overlayHwnd:X}。" +
                "排查方向：Owner 是否在 Show 之前挂好、有没有被别处 SetWindowPos 打回宿主之下。");
        if (!probedAny)
            Log("   ⚠ 放大镜与任何视频面矩形都不相交，本场景不判别 airspace 遮挡（不静默当通过）");

        // ④ 自愈验证：给"补插"装牙。
        //    ⚠ 测量档 <c>FC_SKIP_ZINJECT=1</c> 只把这一段拿掉（其余照跑）⇒ 单变量比崩溃产出率。
        //    跳过时**登记为未判**，绝不允许悄悄算通过。
        void SelfHealArm()
        {
            // ④ 自愈验证：给"补插"装牙。
            //    为什么要这一断言：条件补插上线后连跑 20 轮全绿、且 补插次数 恒为 0 ⇒ 现实的
            //    Z 序反转一次都没发生，"全绿"证明不了兜底路径可用（无牙/未生效一类）。
            //    夹具用**已知必然造成反转**的那次调用：SetWindowPos 的 hWndInsertAfter 语义是
            //    "插到谁之后（=之下）"，把主窗句柄传进去就是把覆盖窗亲手塞到主窗底下
            //    （实测：那样跑的批次 12 次读数 11 次 Below，对照基线 3/22）——比现实更苛刻。
            //    ⚠ 变异验牙：把 ReassertAboveOwner 里的 HWND_TOP 换成主窗句柄，本断言必须判红。
            var beforeReassert = _magnifierWindow.ZOrderReasserts;
            if (!SetWindowPos(overlayHwnd, mainHwnd, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE))
                throw new InvalidOperationException(
                    $"{label}：注入失败（SetWindowPos 返回 false）⇒ 夹具没落地，本断言无牙，不能算通过。");
            var injected = _magnifierWindow.QueryZOrderPosition();
            if (injected != Controls.MagnifierOverlayWindow.ZOrderPosition.Below)
                throw new InvalidOperationException(
                    $"{label}：注入后覆盖窗读到的不是 Below 而是 {injected} ⇒ 夹具失效，本断言无牙，不能算通过。");
            // 只走真实产品路径（指针移动 → UpdateAt → PresentationChanged → 宿主的 ShowOverlay），
            // 绝不在这里直接调 ReassertAboveOwner —— 那等于我自己叫我修，测不出接线是否成立。
            Magnifier.UpdateAt(target, local, scaling);
            Dispatcher.UIThread.RunJobs();
            var afterHeal = _magnifierWindow.QueryZOrderPosition();
            Log($"   自愈取证：注入后={injected} 走产品路径后={afterHeal} " +
                $"补插次数 {beforeReassert}→{_magnifierWindow.ZOrderReasserts}");
            if (afterHeal != Controls.MagnifierOverlayWindow.ZOrderPosition.Above)
                throw new InvalidOperationException(
                    $"{label}：覆盖窗被压到主窗之下后，走一遍产品路径仍未回到主窗之上（P1-4 会在真机发生）——" +
                    "查 MagnifierOverlayWindow.ReassertAboveOwner 的插入点（必须 HWND_TOP，不是 owner 句柄）。");
            if (_magnifierWindow.ZOrderReasserts == beforeReassert)
                throw new InvalidOperationException(
                    $"{label}：Z 序虽已回到主窗之上，但补插计数未动 ⇒ 是别处（或系统自己）修的序，" +
                    "本产品兜底路径并未生效，判据不算通过。");
        }
        if (BenchSkipMagnifierZInject)
            RecordSelfTestSkip("FC_SKIP_ZINJECT=1 ⇒ 不执行「把覆盖窗注入到主窗之下 + 走产品路径自愈」那一段"
                + "（§十八 的对照臂）；本步除它以外没有别的判别内容 ⇒ 记为未判，不是通过");
        else
            SelfHealArm();

        Magnifier.HideOverlay();
        Log($"✅ 放大镜浮窗坐标 ✓ {label}：surface {target.Index} 原点=({targetOrigin.X:F1},{targetOrigin.Y:F1}) " +
            $"光标(容器)=({cursorInContainer.X:F1},{cursorInContainer.Y:F1}) 浮窗={got}（误差 {err:F3} DIP）" +
            $"；容器={containerSize.Width:F1}×{containerSize.Height:F1} 窗口落点(容器)={winInContainer:F1}（误差 {winErr:F3} DIP）");
    }

    // ══════════ 裁剪（SetWindowRgn）接线验证 ══════════
    // 为什么必须有这一段：WindowRegionClipper 是 Win32 调用 + 物理像素坐标，两个测试工程
    // （只引用 Core / 只覆盖纯逻辑）都覆盖不到；而"区域设错"的后果是画面缺一块或整窗不可见，
    // 静默通过等于没验证。这里用 GetWindowRgn 回读，把"到底设没设、设成什么"钉死。

    /// <summary>断言：裁剪在对比模式下**确实被下发**，且区域正确（不打洞、不越界），回读一致。
    ///
    /// <para><b>为什么这里期望"有区域"而不是"没区域"</b>：Avalonia 会把排布矩形向外取整到物理像素
    /// （<c>PlayerSurface.Bounds</c> 实测比格表算出的矩形大 ≤1px），于是窗口比格大 ≤1px ——
    /// 这正是"窗口 ⊋ 格"的最小实例，裁剪会把这 ≤1px 的外扩裁掉。这是真实生效、且**不会**在格内
    /// 留洞的（区域与格在容器坐标下完全重合）。若哪一天这里变成"没区域"，说明换算或时机断了，
    /// 必须查清而不是当成通过。</para></summary>
    /// <returns>本轮真正被设上区域的路线数（供调用方累计，见 <c>RunCompareModeTestAsync</c> 的兜底断言）。</returns>
    private int AssertCompareCrop(int routes, string label)
    {
        UpdateLayout();      // 先把布局落地
        ApplyCompareCrop();  // 再同步走一次生产路径（不等延迟队列）

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = _partition.ComputeCells();
        var applied = 0;

        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");
            if (s.Hwnd == nint.Zero)
                throw new InvalidOperationException($"{label}：第 {i} 路子 HWND 未创建，裁剪无从验证");

            // 该路显示在哪一格由"格→路"映射决定（互换 / 轮换之后不是第 i 格）。
            // 按路号直取 cells[i] 会拿别的路的格当期望（2026-10-01 实读判红：2x3 下第 0 路
            // 回读 Y=712、期望 Y=0 —— 红的是判据，不是产品）。
            var cellIndex = Grid.CellIndexOfRoute(i);
            if (!s.IsVisible || cellIndex < 0 || cellIndex >= cells.Length)
            {
                var hidden = ReadWindowRegion(s.Hwnd, out var hiddenBox);
                if (hidden != RegionTypeError)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路已隐藏却仍有窗口区域 type={hidden} box={hiddenBox}");
                continue;
            }

            // 期望值独立复算（与生产代码同一条换算路径，但输入来自公开几何）
            // 拼合模式下区域口径是**不留缝**的 MosaicRegionDip（与生产同一函数，见其注释：
            // 留缝会在两块之间露出父窗底色，拼合当场破功）。
            var regionDip = Grid.MosaicWindows
                ? CompareCropPlanner.MosaicRegionDip(cells[cellIndex], w, h)
                : CompareCropPlanner.CellToContainerDip(cells[cellIndex], w, h);
            var cellPx = CompareCropPlanner.ToPhysicalRect(regionDip, scaling);
            var windowPx = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
            var plan = CompareCropPlanner.Plan(windowPx, cellPx);
            var type = ReadWindowRegion(s.Hwnd, out var box);

            if (!plan.ShouldApply)
            {
                if (type != RegionTypeError)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路窗口 == 格（{windowPx}），不该有区域，却有 type={type} box={box}");
                continue;
            }

            // 不打洞的构造性判据：窗口原点 + 区域 == 格（区域在容器坐标下与格完全重合）
            var r = plan.Region;
            if (windowPx.X + r.X != cellPx.X || windowPx.Y + r.Y != cellPx.Y ||
                r.Width != cellPx.Width || r.Height != cellPx.Height)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路区域 {r} 与格 {cellPx} 不重合（窗口 {windowPx}）——会在格内留洞");
            if (r.X < 0 || r.Y < 0 || r.X + r.Width > windowPx.Width || r.Y + r.Height > windowPx.Height)
                throw new InvalidOperationException($"{label}：第 {i} 路区域 {r} 越出窗口 {windowPx}");

            if (type is not (RegionTypeSimple or RegionTypeComplex))
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路应已裁剪（窗口 {windowPx} ⊋ 格 {cellPx}），回读却 type={type}（0=无区域）");
            if (box != r)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路回读区域 {box} != 期望 {r}（坐标或 DPI 换算不一致）");
            applied++;
        }

        Console.WriteLine(
            $"comparemodetest: [{label}] 裁剪接线已走通 ✓ {applied}/{cells.Length} 路设了区域且回读一致" +
            (Grid.MosaicWindows
                ? "（拼合：区域 == 本格，窗口越界部分全部裁掉 ⇒ 几块拼回一整幅）"
                : "（区域 == 格在窗口内的位置；0 路表示该几何下窗口恰好等于格、无需裁剪；" +
                  "被裁掉的是 Avalonia 向外取整产生的 ≤1px 外扩）") +
            $"；缩放={scaling:0.##} 对比区={w:F1}x{h:F1}DIP");
        return applied;
    }

    /// <summary>断言：退出对比模式后**没有任何一路**残留窗口区域（否则窗口被永久裁剪，只有重启才恢复）。</summary>
    private void AssertCompareCropCleared(int routes, string label)
    {
        UpdateLayout();
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || s.Hwnd == nint.Zero) continue;
            var type = ReadWindowRegion(s.Hwnd, out var box);
            if (type != RegionTypeError)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路退出对比模式后仍有窗口区域 type={type} box={box}（窗口会被永久裁剪）");
        }
        Console.WriteLine($"comparemodetest: [{label}] 退出后裁剪已清除 ✓（各路 GetWindowRgn 均为 ERROR）");
    }

    /// <summary>
    /// 在**真实子 HWND** 上把 SetWindowRgn 端到端跑一遍：构造"放大后的窗口"（格四周各外扩半个格，
    /// 即 2 倍尺寸、以格为中心）得到真实裁剪方案，下发 → <c>GetWindowRgn</c> 回读校验 →
    /// 清除 → 再回读确认已无区域。
    ///
    /// <para><b>为什么这样构造</b>：本机无 FFmpeg，自测跑在演示模式，且当前布局的窗口恰好等于格
    /// （裁剪被有意拒绝）——若只断言"没裁剪"，就完全没有验证到 SetWindowRgn 这条路。
    /// 这里把**窗口矩形**当作"已被放大"的输入喂给同一个换算函数与同一个 P/Invoke 封装，
    /// 于是"坐标是物理像素""区域等于格（不打洞）""句柄所有权正确（不泄漏也不误删）""Clear 真的还原"
    /// 四件事都被覆盖。窗口外扩半个格后区域原点非零（<c>(半个格, 半个格)</c>），正好也验证了
    /// 区域可以不为 (0,0)。测试结束前已 Clear，且不写生产状态 <c>_compareCropApplied</c>。</para>
    /// </summary>
    private void AssertCropWin32RoundTrip(string label)
    {
        var s = Grid.GetSurface(0) ?? throw new InvalidOperationException($"{label}：第 0 路缺失");
        var hwnd = s.Hwnd;
        if (hwnd == nint.Zero) throw new InvalidOperationException($"{label}：第 0 路子 HWND 未创建");

        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cellDip = s.Bounds;
        var halfW = cellDip.Width / 2;
        var halfH = cellDip.Height / 2;
        var bigDip = new Rect(cellDip.X - halfW, cellDip.Y - halfH,
                              cellDip.Width + halfW * 2, cellDip.Height + halfH * 2);
        var cellPx = CompareCropPlanner.ToPhysicalRect(cellDip, scaling);
        var bigPx = CompareCropPlanner.ToPhysicalRect(bigDip, scaling);

        var plan = CompareCropPlanner.Plan(bigPx, cellPx);
        if (!plan.ShouldApply)
            throw new InvalidOperationException(
                $"{label}：窗口放大到格的 2 倍后仍算出「不裁剪」（窗口={bigPx} 格={cellPx}）——换算有问题");

        var r = plan.Region;
        // 不打洞的构造性判据：窗口原点 + 区域 == 格（区域在容器坐标下与格完全重合）
        if (bigPx.X + r.X != cellPx.X || bigPx.Y + r.Y != cellPx.Y ||
            r.Width != cellPx.Width || r.Height != cellPx.Height)
            throw new InvalidOperationException(
                $"{label}：区域 {r} 与格 {cellPx} 不重合（窗口 {bigPx}）——会在格内留洞");
        // 区域必须真的在窗口内（SetWindowRgn 会静默裁掉越界部分，越界即"少露一块"）
        if (r.X < 0 || r.Y < 0 || r.X + r.Width > bigPx.Width || r.Y + r.Height > bigPx.Height)
            throw new InvalidOperationException($"{label}：区域 {r} 越出窗口 {bigPx}");

        if (!WindowRegionClipper.ApplyRect(hwnd, r.X, r.Y, r.Width, r.Height))
            throw new InvalidOperationException(
                $"{label}：SetWindowRgn 失败（Win32 错误码 {WindowRegionClipper.GetLastErrorCode()}）");

        var type = ReadWindowRegion(hwnd, out var box);
        if (type is not (RegionTypeSimple or RegionTypeComplex))
            throw new InvalidOperationException($"{label}：下发后回读失败 type={type}（期望 2/3）");
        if (box != r)
            throw new InvalidOperationException(
                $"{label}：回读区域 {box} != 下发区域 {r}（坐标或 DPI 换算不一致）");

        if (!WindowRegionClipper.Clear(hwnd))
            throw new InvalidOperationException($"{label}：Clear 失败（Win32 错误码 {WindowRegionClipper.GetLastErrorCode()}）");
        var cleared = ReadWindowRegion(hwnd, out var leftover);
        if (cleared != RegionTypeError)
            throw new InvalidOperationException($"{label}：Clear 后仍有区域 type={cleared} box={leftover}");

        // 本单元绕过了 ApplyCropRegion 去清 OS 区域，而 _compareCropApplied 还记着上一个方案 ⇒
        // 生产入口的去重会认为"已经设过了"而不再下发。旧的排布下这只是 ≤1px 的外扩回归（看不出来），
        // 拼合模式下"没有区域"= 这一路铺满整幅、盖住邻居，且后面每个按区域回读的单元都会跟着错。
        // 所以先把这条账抹掉，再走一次生产入口把区域补回本格。
        _compareCropApplied.Remove(0);
        ApplyCompareCrop();
        if (Grid.MosaicWindows && ReadWindowRegion(hwnd, out var restored) == RegionTypeError)
            throw new InvalidOperationException(
                $"{label}：端到端探针清完区域后，生产入口没有把第 0 路的区域补回来 " +
                "（拼合模式下这一路此后铺满整幅，后续单元的读数都不可信）");

        Console.WriteLine(
            $"comparemodetest: [{label}] SetWindowRgn 端到端 ✓ " +
            $"基准矩形（第 0 路当前窗口）{cellDip.Width:F0}x{cellDip.Height:F0}DIP → " +
            $"{cellPx.Width}x{cellPx.Height}px（缩放 {scaling:0.##}）；" +
            $"按其 2 倍造出放大窗口 {bigPx.Width}x{bigPx.Height}px → 下发区域 {r}（= 基准矩形在窗口内的位置）" +
            $"回读一致；Clear 后 type={cleared}(0=ERROR=无区域)");
    }

    // GetWindowRgn / GetRgnBox 的返回码（wingdi.h）
    private const int RegionTypeError = 0;    // 窗口没有区域
    private const int RegionTypeSimple = 2;   // 单一矩形
    private const int RegionTypeComplex = 3;  // 复杂区域

    // ══════════ 无缝放大（子窗口放大 + 偏移 + 裁剪）验证 ══════════
    //
    // 为什么必须真机验证：换算全是 DIP 的纯函数（已有单测），但"窗口真的被排到 z 倍并偏移"
    // 依赖 Avalonia 的 NativeControlHost 把 Arrange 的 Bounds 落到子 HWND 上，
    // "区域真的把越界部分裁掉"依赖 SetWindowRgn 对 flip-model 生效 —— 两件都是 Win32/合成器行为，
    // 两个测试工程覆盖不到。这里用 Bounds + GetWindowRgn 回读把它们钉死。
    //
    // 本机无 FFmpeg ⇒ 跑在演示模式：演示引擎的 ReadMediaInfo 返回 1920×1080，
    // 而 ABCD 的格宽高比通常不是 16:9 ⇒ letterbox 修正确实被走到（下面的断言会显式检查这一点）。

    /// <summary>断言两道性能闸门真的会拒绝：倍数上限、像素预算。
    /// <para>不静默爆显存是硬要求，因此"拒绝"必须被验证，而不是只验证"正常参数能启用"。</para></summary>
    private void AssertCompareMagnifyGates(int routes)
    {
        if (SetCompareMagnify(MaxCompareZoom + 1, 0, 0))
            throw new InvalidOperationException($"z={MaxCompareZoom + 1} 超过上限 {MaxCompareZoom} 却被接受（显存闸门失效）");
        if (CompareMagnifyActive)
            throw new InvalidOperationException("超过 z 上限被拒绝后仍处于放大状态");

        if (SetCompareMagnify(double.NaN, 0, 0))
            throw new InvalidOperationException("z=NaN 却被接受");

        // 预算闸门：传一个必然不够的预算（真实窗口尺寸下 z ≤ 4 撞不到该闸门）
        if (SetCompareMagnify(2.0, 0, 0, pixelBudget: 1))
            throw new InvalidOperationException("像素预算只有 1 却仍接受放大（显存闸门失效）");
        if (CompareMagnifyActive)
            throw new InvalidOperationException("超预算被拒绝后仍处于放大状态");

        // z = 1 是"关闭"，必须被接受且等价于现状
        if (!SetCompareMagnify(1.0, 0, 0))
            throw new InvalidOperationException("z=1（关闭放大）被拒绝");
        if (CompareMagnifyActive)
            throw new InvalidOperationException("z=1 之后仍报告处于放大状态");

        Console.WriteLine(
            $"comparemodetest: 放大闸门 ✓ z>{MaxCompareZoom} 拒绝 / NaN 拒绝 / " +
            $"超预算({CompareMagnifyPixelBudget / 1e6:0.#}Mpx) 拒绝 / z=1 等价关闭");
    }

    /// <summary>当前路数与对比区尺寸下<b>买得起</b>的放大档（封顶 2.0，低于 1.05 直接判红）。
    ///
    /// <para>推导与生产闸门同一条算术，但由判据<b>自己</b>从公开几何复算、不回读实现的中间量。
    /// 基准<b>随实际排布变</b>（这条在对照臂上错过一次：写死拼合口径会让非拼合臂判红"闸门失效"）：
    /// <b>拼合</b>下每路窗口 == 整个对比区 ⇒ <c>总像素 = 路数 × 对比区面积 × z²</c>；
    /// <b>非拼合</b>（对照臂 / 历史形态）每路窗口 == 自己那一格，而 Σ格 == 对比区 ⇒
    /// <c>总像素 = 对比区面积 × z²</c>，与路数无关。两者都须 ≤
    /// <see cref="CompareMagnifyPixelBudget"/> ⇒ 解出 z。乘 0.95 是安全系数：估算取的是整数面积，
    /// 真实排布有 ≤1px 的取整差，贴着边界跑会随窗口尺寸抖动而随机被拒。</para>
    ///
    /// <para><b>为什么判据的倍率要跟着预算走</b>：这几条单元验的是<b>机制</b>（窗口被排到 z 倍、
    /// 区域回读等于换算值、OS 窗口真的搬到那个尺寸、块落在正确那一格），不是某个写死的倍率。
    /// 拼合把基线像素抬了一个 N 倍 ⇒ 本机 9 路 z=2 需要 88.6 Mpx、超过 66.4 Mpx 出货预算，
    /// 那时"被闸门拒绝并写明原因"才是正确行为。所以几何判据按买得起的最大档跑，
    /// 另由 <see cref="AssertMagnifyBudgetRefusal"/> 钉住"买不起的那一档确实被拒"。</para>
    ///
    /// <para>各路等分辨率 ⇒ 相对/像素级对齐的有效倍率相同（z_i = z），本式对两者都成立；
    /// 夹具本来就是同一份素材开 N 路。若哪天夹具改成混分辨率，像素级对齐下还要逐路再乘
    /// <c>(W_i/minW)²</c>（见 <see cref="CompareCropPlanner.AlignedZoom"/>），届时这里必须跟着改。</para></summary>
    private double AffordableMagnifyZoom(int routes)
    {
        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (wDip <= 0 || hDip <= 0) return 2.0;   // 布局未落地：调用方自己会因尺寸非法判红，这里不替它决定
        var areaPx = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, wDip, hDip), CompareCropScaling());
        // **按实际排布算基准**（这条在对照臂上错过一次：写死拼合口径会让对照臂判红"闸门失效"）：
        //   拼合 ⇒ 每路窗口 == 整对比区 ⇒ 跨路总量 = 路数 × 面积 × z²；
        //   非拼合 ⇒ 每路窗口 == 自己那一格，而 Σ格 == 面积 ⇒ 总量 = 面积 × z²，与路数无关。
        var spanMpx = (double)areaPx.Width * areaPx.Height * (Grid.MosaicWindows ? Math.Max(1, routes) : 1);
        var cap = Math.Sqrt(CompareMagnifyPixelBudget / spanMpx);
        var z = Math.Min(2.0, 0.95 * cap);
        if (!(z > 1.05))
            throw new InvalidOperationException(
                $"{routes} 路 × 对比区 {areaPx.Width}x{areaPx.Height}px 下 z=1 就要 {spanMpx / 1e6:0.#} Mpx，" +
                $"连 1.05 倍都超出 {CompareMagnifyPixelBudget / 1e6:0.#} Mpx 预算 ⇒ " +
                "本判据在这个规模上没有可跑的放大档（不降标跑空转，直接判红）");
        return z;
    }

    /// <summary>闸门的牙：<b>买不起的那一档必须被拒</b>，且拒绝后不得留下放大状态。
    /// 若本规模本来就买得起 2.0（几何判据就是按 2.0 跑的），这一档没有"自然被拒"可言，
    /// 于是按 <see cref="ForcedRefusalBudget"/> 注入一个小预算把<b>同一条拒绝分支</b>逼出来 ——
    /// 否则"超预算时那句用户看得见的上限提示"在默认档上从没被执行过（判据写了 ≠ 判据在跑）。
    ///
    /// <para>注入走完必须把放大状态<b>原样退回并读回核对</b>（见 <see cref="RestoreMagnifyStateAndVerify"/>），
    /// 后面的几何判据才不是跑在被这条子判据动过的场地上。</para></summary>
    private void AssertMagnifyBudgetRefusal(int routes, double zTest)
    {
        // 拒绝分支有**两条入口**，两条都得跑，不然"被拒时那句上限提示"就有半边从没执行过：
        //   ① 自然拒绝 —— 路数够多 ⇒ z=2 本来就买不起（9 路档走这条）；
        //   ② 构造拒绝 —— 路数少、z=2 买得起 ⇒ 传一个小预算把同一条分支逼出来。
        // 之前只有 ①，于是这条判据在 5 路档恒打"不适用"，而**发布门禁 [7/11] 正是硬写 5 路**
        // （tools/发布门禁.ps1:492）⇒ 那句用户看得见的提示在门禁里等于从没被验过（2026-10-01 查明）。
        var natural = zTest < 2.0 - 1e-9;
        var testBudget = natural ? CompareMagnifyPixelBudget : ForcedRefusalBudget(routes);
        if (testBudget <= 0)
        {
            // fail-closed：连预算都算不出来 ⇒ 注入根本没发生，这条子判据本次什么都没量到。
            var none = $"放大预算闸门 未判：{routes} 路下算不出一个「z=2 超预算、但仍有放大余量」的预算值" +
                       $"（对比区 {Grid.Bounds.Width:F0}x{Grid.Bounds.Height:F0}DIP 尚未落地 ⇒ 预算 {testBudget:0.##}）";
            RecordSelfTestSkip(none);
            Console.WriteLine($"comparemodetest: {none}");
            return;
        }

        // 注入不是免费的：SetCompareMagnify 在**像素估算之前**就刷新 _compareAlign / _compareMagnifySources
        // （那是"像素级对齐低估显存"的前置修复，见 MainWindow.CompareCrop.cs:446 的注释），
        // 所以**被拒绝的那一次调用也会留痕**；一旦被接受（正是要防的分支漂移）连 zoom 与
        // _compareMagnifyBudget 都带着小预算落了地。⇒ 先快照，跑完无条件退回，末尾读回核对。
        var before = CaptureMagnifyState();
        bool accepted = false;
        bool activeAfterCall = false;
        double estimatedMpx = 0;
        string? userVisible = null;
        try
        {
            accepted = SetCompareMagnify(2.0, 0.25, 0.25, testBudget);
            estimatedMpx = EstimateCompareMagnifyPixels(2.0) / 1e6;   // 闸门刚比过的那两个数
            userVisible = StatusInfo.Text;                             // 拒绝的用户可见面（状态栏）
            activeAfterCall = CompareMagnifyActive;
        }
        finally
        {
            RestoreMagnifyStateAndVerify(before,
                $"放大预算闸门 z=2 注入（预算 {testBudget / 1e6:0.##} Mpx）");
        }

        if (accepted && natural)
        {
            var areaPx = CompareCropPlanner.ToPhysicalRect(
                new Rect(0, 0, Grid.Bounds.Width, Grid.Bounds.Height), CompareCropScaling());
            var spanMpx = (double)areaPx.Width * areaPx.Height *
                          (Grid.MosaicWindows ? Math.Max(1, routes) : 1);
            throw new InvalidOperationException(
                $"{routes} 路{(Grid.MosaicWindows ? "拼合" : "非拼合")}下 z=2 需要约 " +
                $"{spanMpx * 4.0 / 1e6:0.#} Mpx，已超出本次给的 {testBudget / 1e6:0.#} Mpx 预算却仍被接受" +
                " ⇒ 像素估算与真实窗口尺寸脱节（闸门形同虚设）");
        }
        if (activeAfterCall && !accepted && !(before.Zoom > 1.0))
            throw new InvalidOperationException("超预算被拒后仍处于放大状态（窗口会保持 z 倍大）");

        if (accepted)
        {
            // 构造档没能把拒绝分支逼出来（估算口径变了 / 那段检查搬了位）⇒ 那句提示本次仍未被执行过。
            // 记**未判**并带上两个观测值；绝不因为"z=2 确实被接受"而记绿（自然档的接受是判红，见上）。
            RecordSelfTestSkip(
                $"放大上限提示 未判：注入预算 {testBudget / 1e6:0.##} Mpx 下 z=2 的实估算只有 " +
                $"{estimatedMpx:0.#} Mpx（对比区 {Grid.Bounds.Width:F0}x{Grid.Bounds.Height:F0}DIP、" +
                $"{routes} 路{(Grid.MosaicWindows ? "拼合" : "非拼合")}）⇒ 没走到超预算拒绝分支，" +
                "用户看得见的上限提示本次没被执行");
            return;
        }

        Console.WriteLine($"comparemodetest: 放大预算闸门 ✓ {routes} 路下 z=2 被拒并写明原因" +
                          $"（{(natural ? "自然档：真实预算 " : "构造档：逼出同一条拒绝分支，预算 ")}" +
                          $"{testBudget / 1e6:0.##} Mpx、闸门实估算 {estimatedMpx:0.#} Mpx；" +
                          $"几何判据按买得起的 z={zTest:0.##} 跑）");
        AssertZoomCeilingHintAgainstOsWindows(testBudget, userVisible);
    }

    /// <summary>放大状态的完整快照 —— 只给 <see cref="RestoreMagnifyStateAndVerify"/> 用。
    ///
    /// <para><b>为什么连 align / 源尺寸数组 / 状态栏一起存</b>：<see cref="SetCompareMagnify"/>
    /// 在估算之前就先刷新 <c>_compareAlign</c> 与 <c>_compareMagnifySources</c>，
    /// <c>RejectCompareMagnify</c> 又一定写状态栏 ⇒ 即便本次调用被拒绝也已经留痕；
    /// 只回滚 zoom/budget 是**不完整**的退回。</para></summary>
    private sealed record MagnifySnapshot(
        double Zoom, double CropX, double CropY, double Budget, double RejectedZoom,
        CompareAlign Align, PixelSize[] Sources, string BudgetNote, string AlignNote,
        string Status, CellMagnify? GridParam);

    private MagnifySnapshot CaptureMagnifyState() =>
        new(_compareZoom, _compareCropX, _compareCropY, _compareMagnifyBudget, _compareMagnifyRejectedZoom,
            _compareAlign, _compareMagnifySources, _compareMagnifyBudgetNote, _compareAlignNote,
            StatusInfo.Text ?? string.Empty, Grid.CellMagnify);

    /// <summary>退回快照并<b>读回核对</b>（不假设"被拒绝就不必收尾"，也不假设"赋了值就等于退回"）。
    ///
    /// <para><c>Grid.CellMagnify</c> 必须<b>按引用</b>写回：<see cref="PushCompareMagnifyToGrid"/>
    /// 的按值去重 <c>SameMagnify</c> 在参数值相等时会**拒绝赋值**（ Sources 每次新建实例但逐元素相等 ⇒
    /// 判定为"没变"），于是排版可以一直拿着注入那一次的参数；而这里要的是"退回注入前那一份"，
    /// 只能直接写属性，再走一次生产入口把区域按退回后的几何重发。</para></summary>
    private void RestoreMagnifyStateAndVerify(MagnifySnapshot before, string label)
    {
        _compareZoom = before.Zoom;
        _compareCropX = before.CropX;
        _compareCropY = before.CropY;
        _compareMagnifyBudget = before.Budget;
        _compareAlign = before.Align;
        _compareMagnifySources = before.Sources;
        _compareMagnifyRejectedZoom = before.RejectedZoom;
        _compareMagnifyBudgetNote = before.BudgetNote;
        _compareAlignNote = before.AlignNote;
        if (!string.Equals(StatusInfo.Text, before.Status, StringComparison.Ordinal))
            StatusInfo.Text = before.Status;
        Grid.CellMagnify = before.GridParam;      // 按引用退回，见方法说明
        ScheduleCompareCrop();                    // 区域按退回后的几何重发一次（走生产入口，带去重）

        // 一次读回核对：字段本身 + 由字段派生的两个可见后果（是否处于放大态、排版拿到的参数）。
        // 残留一个小预算的后果是后面每条几何判据都在"z=2 被误拒"的场地上跑，所以这必须是判红不是提示。
        var drift = new System.Collections.Generic.List<string>();
        if (Math.Abs(_compareMagnifyBudget - before.Budget) > 1e-9)
            drift.Add($"预算残留：{_compareMagnifyBudget / 1e6:0.##} Mpx ≠ {before.Budget / 1e6:0.##} Mpx");
        if (Math.Abs(_compareZoom - before.Zoom) > 1e-12 ||
            Math.Abs(_compareCropX - before.CropX) > 1e-12 ||
            Math.Abs(_compareCropY - before.CropY) > 1e-12)
            drift.Add($"放大参数：{_compareZoom:0.##}@({_compareCropX:0.###},{_compareCropY:0.###}) " +
                      $"≠ {before.Zoom:0.##}@({before.CropX:0.###},{before.CropY:0.###})");
        if (CompareMagnifyActive != (before.Zoom > 1.0))
            drift.Add($"放大态：{CompareMagnifyActive} ≠ 注入前的 {before.Zoom > 1.0}");
        if (!ReferenceEquals(Grid.CellMagnify, before.GridParam))
            drift.Add($"排版参数没退回（Grid.CellMagnify={(Grid.CellMagnify is null ? "null" : "另一份实例")}）");
        if (!ReferenceEquals(_compareMagnifySources, before.Sources))
            drift.Add("源尺寸数组的引用变了");
        if (_compareAlign != before.Align)
            drift.Add($"对齐模式：{_compareAlign} ≠ {before.Align}");
        if (!string.Equals(StatusInfo.Text, before.Status, StringComparison.Ordinal))
            drift.Add($"状态栏停在「{StatusInfo.Text}」（注入前是「{before.Status}」）");
        if (drift.Count > 0)
            throw new InvalidOperationException(
                $"{label} 之后放大状态没退回原样：{string.Join("；", drift)} " +
                "⇒ 后面的几何判据跑在被这条子判据动过的场地上（小预算残留还会让 z=2 被误拒）");
    }

    /// <summary>给"路数少、z=2 买得起"的那一档算一个**构造预算**，把同一条超预算拒绝分支逼出来。
    ///
    /// <para>取值规则：令 <c>budget = 2 × 跨度</c>（跨度 = 排布口径下 z=1 的总像素）。于是
    /// z=2 需要 4×跨度 > budget ⇒ 必拒；而 <c>zMax = √(budget/跨度) = 1.414 > 1</c> ⇒ 提示句走
    /// "约可到 N 倍"那半边分支（不是"已没有放大余量"那半边）。两条半边都由
    /// <see cref="AssertZoomCeilingHintAgainstOsWindows"/> 按 OS 实测窗口面积对账。</para>
    ///
    /// <para>返回 0 表示算不出来（布局还没落地），调用方据此<b>登记未判</b>
    /// （<see cref="RecordSelfTestSkip"/>）而不是硬跑，也绝不记绿。</para></summary>
    private double ForcedRefusalBudget(int routes)
    {
        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (!(wDip > 0) || !(hDip > 0)) return 0;
        var areaPx = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, wDip, hDip), CompareCropScaling());
        var span = (double)areaPx.Width * areaPx.Height *
                   (Grid.MosaicWindows ? Math.Max(1, routes) : 1);
        if (!(span > 0)) return 0;
        return 2.0 * span;
    }

    /// <summary>把"约可到 N 倍"那句提示读实：它是拒绝消息的尾巴，只在闸门判红那一路才拼出来，
    /// 之前**从没被任何一次运行打印过**（写了没跑过的分支）。
    ///
    /// <para>对账用**独立参照**而不是同一套算术：提示里的块数取自格表/路数，这里改用
    /// <c>GetWindowRect</c> 实测的单路窗口像素 ×「有几个路真有窗口」反解 zMax。两者口径一致才放行
    /// ⇒ 谁把块数写回 1（正是本轮修掉的那个老估算缺陷），这句提示就会把上限吹高 √路数 倍，
    /// 而 OS 侧真实面积会当场把它拆穿。</para>
    ///
    /// <para><paramref name="userVisible"/> = 拒绝那一刻的状态栏原文（由调用方在退回之前取好）。
    /// 只有"格式化函数会输出这句"是不够的 —— 它必须真的出现在用户看得见的那条文案里，
    /// 这正是本判据要钉的东西（<c>RejectCompareMagnify</c> 在对比态一定写状态栏）。</para></summary>
    private void AssertZoomCeilingHintAgainstOsWindows(double budget, string? userVisible)
    {
        var hint = DescribeCompareZoomCeiling(budget);
        if (hint.Length == 0)
            throw new InvalidOperationException("超预算拒绝却给出空提示 ⇒ 用户只看到「超预算」三个字，不知道还能到几倍");

        var tail = hint.TrimStart('，');
        // 对比态外 RejectCompareMagnify 按设计不写状态栏 ⇒ 不拿它虚判，只核对提示本身。
        if (_compareActive)
        {
            if (userVisible is null)
                throw new InvalidOperationException("超预算拒绝却没取到状态栏原文 ⇒ 无法确认那句提示真的给了用户");
            if (!userVisible.Contains(tail, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"超预算拒绝却没把上限提示送到用户可见处：状态栏=「{userVisible}」，期望含「{tail}」" +
                    " ⇒ 拒绝文案与提示函数脱节（判据核对的这句从没出现在真实文案里）");
        }

        if (!Grid.MosaicWindows)
        {
            // 非拼合（FC_NOMOSAIC=1 对照臂）下每路窗口就是自己那一格，OS 面积反解不出提示里的块数。
            // 与 4260 那条拼合专属判据同一口径：**明写不适用**、不登记 SKIP —— 对照臂本来就是来看
            // "关掉拼合会怎样"的，登记 SKIP 会把整条 magnify 单元（其余四条几何断据真的判过了）抹成 SKIP。
            Console.WriteLine($"comparemodetest: 放大上限提示 不适用（非拼合）：{hint.TrimStart('，')}");
            return;
        }

        // OS 实测：z=1 被拒之后每路窗口就是整对比区，路数 × 单路面积 = 真实占用的像素块。
        var windows = 0;
        double unitPx = 0;
        for (var i = 0; i < Grid.Count; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || s.Hwnd == IntPtr.Zero) continue;
            var r = ReadWindowRectPx(s.Hwnd);
            if (!r.IsValid || r.Width <= 0 || r.Height <= 0) continue;
            windows++;
            unitPx = (double)r.Width * r.Height;
        }
        if (windows < 2 || unitPx <= 0)
        {
            // 拼合下本应有 N 个真窗口；连第 2 个都读不到 ⇒ 独立参照不存在，这条子判据只剩"自己验自己"。
            // fail-closed：登记未判并带上观测值，不因为"提示非空、格式也对"就记绿。
            var noOs = $"放大上限提示 未判：拼合下 OS 侧只读到 {windows} 个有效窗口" +
                       $"（Grid.Count={Grid.Count}、单路面积 {unitPx / 1e6:0.##} Mpx）⇒ 没有独立参照可反解 zMax，" +
                       $"待核对的提示是「{tail}」";
            RecordSelfTestSkip(noOs);
            Console.WriteLine($"comparemodetest: {noOs}");
            return;
        }

        var m = System.Text.RegularExpressions.Regex.Match(hint, @"约可到 ([0-9.]+) 倍");
        var claimsNoHeadroom = hint.Contains("没有放大余量");
        var implied = Math.Sqrt(budget / (windows * unitPx));
        if (claimsNoHeadroom)
        {
            if (implied > 1.05)
                throw new InvalidOperationException(
                    $"提示说「已没有放大余量」，但按 OS 实测的 {windows} 路 × {unitPx / 1e6:0.##} Mpx 反解还能到 {implied:0.##} 倍" +
                    "⇒ 提示用的面积比真实分配面积大（或块数口径不对）");
            Console.WriteLine($"comparemodetest: 放大上限提示 ✓ 「没有放大余量」与 OS 实测反解 {implied:0.##} 倍一致" +
                $"（{windows} 路 × {unitPx / 1e6:0.##} Mpx）");
            return;
        }
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var claimed))
            throw new InvalidOperationException($"提示句里取不出倍数：\"{hint.TrimStart('，')}\"");
        // 0.## 只留两位小数 ⇒ 量化误差 ≤0.005；给 12% 容差已经远小于"块数写成 1"造成的 √路数 倍偏差。
        if (Math.Abs(claimed - implied) > implied * 0.12)
            throw new InvalidOperationException(
                $"放大上限提示与 OS 实测不符：提示说 {claimed:0.##} 倍，按 {windows} 路 × {unitPx / 1e6:0.##} Mpx " +
                $"反解是 {implied:0.##} 倍（提示/实测 = {claimed / implied:0.##}）⇒ 提示里的块数或面积口径不对" +
                $"（把块数写回 1 就会正好差 √{windows} = {Math.Sqrt(windows):0.##} 倍）");
        Console.WriteLine($"comparemodetest: 放大上限提示 ✓ 「约可到 {claimed:0.##} 倍」与 OS 实测反解 {implied:0.##} 倍一致" +
            $"（{windows} 路 × {unitPx / 1e6:0.##} Mpx，预算 {budget / 1e6:0.#} Mpx）");
    }

    /// <summary>
    /// 启用无缝放大后逐路验证五件事（期望值全部由 <see cref="CompareCropPlanner.Magnify"/>
    /// <b>独立复算</b>，不是回读生产状态）：
    /// <list type="number">
    /// <item><description><b>窗口真的被放大并偏移</b>：Avalonia <c>Bounds</c> == 换算出的窗口矩形；</description></item>
    /// <item><description><b>OS 窗口真的被放大</b>：<c>GetWindowRect</c> 读回的尺寸 == 换算出的物理尺寸
    /// —— 这才是"内核按更高分辨率重渲染"的前提，只断言 Bounds 可能只是托管侧排布了；</description></item>
    /// <item><description><b>OS 窗口真的搬到了正确位置</b>：屏幕坐标 == 容器屏幕原点 + 换算出的窗口矩形
    /// —— 这才是"平移不受内核 <c>max(0,·)</c> 钳制"的判据；</description></item>
    /// <item><description><b>区域真的被下发且回读一致</b>：<c>GetWindowRgn</c> == 换算出的物理区域，
    /// 且窗口原点 + 区域原点 == 格原点（不打洞）、区域在窗口内；</description></item>
    /// <item><description><b>letterbox 修正生效</b>：画面被 letterbox 时，区域必须 != "把格当画面"的朴素换算。</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <b>为什么是 async</b>：Avalonia 的 <c>NativeControlHost</c> 把"把子 HWND 搬到新位置"
    /// （<c>ShowInBounds</c>）排在 <c>AfterRender</c> 优先级，而 <c>UpdateLayout()</c> 只跑排版、
    /// 不执行它 ⇒ 必须让出一拍给 Dispatcher，否则读到的是<b>还没搬</b>的窗口
    /// （实测就是如此：Bounds 已放大到 2792x782，HWND 仍是 996x560）。
    /// </remarks>
    /// <returns>本轮真正被设上区域的路线数（供调用方累计）。</returns>
    /// <param name="originOverride">容器屏幕原点的**外部覆盖值**：由调用方在放大真的发生之前取好传进来。
    /// <see cref="CaptureContainerScreenOrigin"/> 靠"某路的屏幕矩形 − 该路的容器坐标矩形"差分，其前提
    /// 是两者来自同一次已落地的排布（窗口 == 格的稳定态）。放大后 HWND 由 <c>AfterRender</c> 的
    /// <c>ShowInBounds</c> 搬运、<c>Bounds</c> 由排版给出，不同拍时差分数不可信 ⇒ 判据①c 会假红。
    /// null = 在本方法内自取（既有调用方的行为逐字不变）。</param>
    /// <param name="label">日志与步骤名里的用例名（默认"无缝放大"；滚轮那条传"滚轮无缝放大"）。
    /// 只影响显示，不影响任何一条判决。</param>
    private async System.Threading.Tasks.Task<int> AssertCompareMagnifyAsync(
        int routes, (int Cols, int Rows) shape, double zoom, double cropX, double cropY,
        PixelPoint? originOverride = null, string? label = null)
    {
        _step = $"{label ?? "无缝放大"} z={zoom}";

        // 放大前先记下"对比区左上角在屏幕上的物理坐标"：由某一路的屏幕矩形减去该路的容器坐标矩形得出。
        // 有了它，放大后就能把子 HWND 的屏幕位置与"容器坐标下的期望窗口"直接对比 —— 这是唯一能在
        // 应用内验证"窗口真的被平移到了正确位置"的办法（区域回读只证明区域被设上，证明不了窗口在哪）。
        var origin = originOverride ?? CaptureContainerScreenOrigin();

        if (!SetCompareMagnify(zoom, cropX, cropY))
            throw new InvalidOperationException(
                $"z={zoom} 在正常参数下被拒（窗口 {Grid.Bounds.Width:F0}x{Grid.Bounds.Height:F0} DIP）——闸门过于保守");

        UpdateLayout();                                 // 让放大后的排布落地
        await System.Threading.Tasks.Task.Delay(200);   // 让 Dispatcher 跑掉 AfterRender 的 ShowInBounds
        UpdateLayout();
        ApplyCompareCrop();                             // 再同步走一次生产路径（不等延迟队列）

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = _partition.ComputeCells();
        var applied = 0;
        var letterboxSeen = 0;
        // 有多少路"整格落在放大视野之外"（判据②的空交集分支）。0 也要如实报，
        // 否则读日志的人会以为那条判据每轮都判过。
        var outsideView = 0;
        // 本步实际看到的**最小** letterbox 修正量（DIP）。打它是为了不让"修正量小到判据看不见"
        // 冒充成绿灯 —— 那等于这一项在某种几何下静默失效。
        var minCorr = double.MaxValue;

        // 像素级对齐的基准源尺寸（各路最小值）—— 与生产 <c>CellMagnify.ZoomOf/CropOf</c> 同一基准。
        // 这里由判据**自己逐路读媒体信息复算**，不回读 <c>Grid.CellMagnify</c>（那等于回读生产状态，
        // 判据就退化成"自己验自己"）。
        var routeSources = new PixelSize[routes];
        for (var i = 0; i < routes; i++) routeSources[i] = ReadSourceSize(i);
        var minSrc = CompareCropPlanner.MinSourceSize(routeSources);
        // 判据⑥的前提：各路源尺寸**相同**（自测夹具就是同一份素材开 N 路）。不同时放大窗口本就
        // 各不相同（像素级对齐要按各自分辨率给倍率），那条一致性判据不成立。
        var sameSourceForAll = minSrc.Width > 0;
        for (var i = 0; i < routes && sameSourceForAll; i++)
            if (routeSources[i].Width > 0 && routeSources[i] != minSrc) sameSourceForAll = false;
        var osRects = new System.Collections.Generic.List<Rect32>();

        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"第 {i} 路缺失");
            if (s.Hwnd == nint.Zero) throw new InvalidOperationException($"第 {i} 路子 HWND 未创建");

            var cellIndex = Grid.CellIndexOfRoute(i);
            if (!s.IsVisible || cellIndex < 0 || cellIndex >= cells.Length)
            {
                var hidden = ReadWindowRegion(s.Hwnd, out var hiddenBox);
                if (hidden != RegionTypeError)
                    throw new InvalidOperationException($"第 {i} 路已隐藏却仍有窗口区域 type={hidden} box={hiddenBox}");
                continue;
            }

            // 期望值独立复算（输入取自公开几何 + 每路真实媒体信息）
            var src = routeSources[i];
            // 拼合 + 放大：放大作用在**整幅拼合视图**上（基准 = 整个对比区），再与本路那一格求交
            // ⇒ 各路仍只露出自己那一块，块与块的拼合关系不被放大破坏。
            // 非拼合（左右拉动不用放大，故这条实际是历史形态）：基准 = 本格，窗口 = 格的 z 倍。
            var mosaic = Grid.MosaicWindows;
            var cellDip = mosaic
                ? CompareCropPlanner.MosaicRegionDip(cells[cellIndex], w, h)
                : CompareCropPlanner.CellToContainerDip(cells[cellIndex], w, h);
            var baseDip = mosaic ? new Rect(0, 0, w, h) : cellDip;

            // ⚠ 入参的 zoom/cropX/cropY 是**共用**值，而像素级对齐下生产是逐路取
            //   <c>CellMagnify.ZoomOf(i)/CropOf(i)</c> 的（crop 的坐标域从"源归一化位置"变成
            //   "可平移范围内的视口位置"，即使各路等分辨率，crop_i = view·(1-1/z) ≠ view）。
            //   把共用值直接喂给 Magnify 会在该模式下算出与生产不同的期望区域 ⇒ 假红。
            //   这里按同一批公开纯函数复算逐路值（与 ApplyCompareCrop 同源不同路径，仍属独立复算）。
            //   相对对齐下 zi/ciX/ciY 与共用值**数值相同**，既有调用方的判据逐字不变。
            var zi = zoom;
            var ciX = cropX;
            var ciY = cropY;
            if (_compareAlign == CompareAlign.Pixel &&
                src.Width > 0 && src.Height > 0 && minSrc.Width > 0 && minSrc.Height > 0)
            {
                zi = CompareCropPlanner.AlignedZoom(zoom, src.Width, minSrc.Width);
                ciX = CompareCropPlanner.AlignedCrop(cropX, zoom, src.Width, minSrc.Width);
                ciY = CompareCropPlanner.AlignedCrop(cropY, zoom, src.Height, minSrc.Height);
            }

            var geom = CompareCropPlanner.Magnify(baseDip, zi, ciX, ciY, src.Width, src.Height);
            var cellPx = CompareCropPlanner.ToPhysicalRect(cellDip, scaling);
            var basePx = CompareCropPlanner.ToPhysicalRect(baseDip, scaling);
            // 期望窗口的物理矩形取**换算值**（而不是回读 Bounds）：下面要拿它去核对 OS 窗口，
            // 用 Bounds 会变成"自己验证自己"。
            var winPx = CompareCropPlanner.ToPhysicalRect(geom.WindowDip, scaling);
            // 放大后"露出的那块"（容器坐标 DIP）：窗口相对的区域平移到容器系，拼合再与格求交。
            var contentDip = new Rect(geom.RegionDip.X + geom.WindowDip.X,
                                      geom.RegionDip.Y + geom.WindowDip.Y,
                                      geom.RegionDip.Width, geom.RegionDip.Height);
            var visibleDip = mosaic ? CompareCropPlanner.Intersect(contentDip, cellDip) : contentDip;

            // 拼合下这一格可能与"放大后的视野"**完全没有交集**（例如某一列被拖到 5% 权重下界、
            // 窄于画面的 letterbox 黑边）。这时生产必须**清掉**这一路的区域：留着上一次的区域，
            // 各路窗口相同（相对对齐）时看着还是黑边无害，但像素级对齐/混分辨率下各路窗口不同 ⇒
            // 这一路显示的是别人视野里的那一块，拼合当场错位，而屏幕上看不出来。
            if (mosaic && (visibleDip.Width <= 0 || visibleDip.Height <= 0))
            {
                outsideView++;
                if (ReadWindowRegion(s.Hwnd, out var staleBox) != RegionTypeError)
                    throw new InvalidOperationException(
                        $"第 {i} 路的格 {cellDip} 整个在放大后的视野 {contentDip} 之外，" +
                        $"却仍带着区域 {staleBox} ⇒ 显示的是上一次方案裁出来的那块（错位且不重叠检查也会挂）");
                continue;
            }

            // ① 排版：窗口 == 换算出的放大窗口（放大真的生效）
            var got = s.Bounds;
            if (Math.Abs(got.X - geom.WindowDip.X) > 1.5 || Math.Abs(got.Y - geom.WindowDip.Y) > 1.5 ||
                Math.Abs(got.Width - geom.WindowDip.Width) > 1.5 || Math.Abs(got.Height - geom.WindowDip.Height) > 1.5)
                throw new InvalidOperationException(
                    $"第 {i} 路窗口未按放大方案排布：Bounds={got} 期望 {geom.WindowDip}（z={zoom}）");

            // 窗口尺寸必须是**基准**（拼合=整对比区 / 非拼合=本格）的 z 倍
            // （这是"内核按更高分辨率重渲染"的前提）
            if (Math.Abs(winPx.Width - basePx.Width * zi) > 2 || Math.Abs(winPx.Height - basePx.Height * zi) > 2)
                throw new InvalidOperationException(
                    $"第 {i} 路窗口尺寸 {winPx.Width}x{winPx.Height} 不是{(mosaic ? "对比区" : "格")} " +
                    $"{basePx.Width}x{basePx.Height} 的 {zi} 倍");

            // ①b 真正落到 OS 窗口上：GetWindowRect 读回的子 HWND 尺寸必须也是 z 倍。
            //     只断言 Avalonia 的 Bounds 不够 —— Bounds 是托管侧排布结果，
            //     而"swapchain 变大 ⇒ 内核按更高分辨率重渲染"取决于 Win32 窗口真的被 MoveWindow 放大。
            var actual = ReadWindowRectPx(s.Hwnd);
            // 有界等待而不是固定延迟：Avalonia 的 NativeControlHost 把 ShowInBounds 排在 AfterRender，
            // 上面 200ms 的固定等待在本机偶发不够（2026-09-26 实测 1/9 判红，且实得**恰为 1× 格尺寸**
            // ⇒ 读数是"还没落"，不是"被工作区钳"——被钳会给出 ≈ 工作区宽，不会给出格宽）。
            // 这里只在"确实落到期望尺寸"时才放行，两秒内始终不落仍按原文判红 ⇒ 判据未放宽。
            for (var wait = 0; wait < 40; wait++)
            {
                if (Math.Abs(actual.Width - winPx.Width) <= 2 && Math.Abs(actual.Height - winPx.Height) <= 2)
                    break;
                // 只让 Dispatcher 把 AfterRender 那一拍跑掉 —— **不**在这里再 UpdateLayout：
                // 反复标脏会连带触发裁剪下发，改变后面几步看到的窗口区域（实测会把判红点搬到别处）。
                await System.Threading.Tasks.Task.Delay(50);
                actual = ReadWindowRectPx(s.Hwnd);
            }

            if (Math.Abs(actual.Width - winPx.Width) > 2 || Math.Abs(actual.Height - winPx.Height) > 2)
            {
                // ── 判红前的一行**只记录**读数（2026-09-26 r=4 实测：期望 2792x1457px 已超过本机
                //    整屏 2560x1440 / 工作区 2560x1392 ⇒ 这条判据在该显示器上不可能成立）。
                //    两种解释此前分不开：① 产品没把尺寸落到子 HWND；② OS 按工作区把窗口钳小了。
                //    下面把"请求尺寸（DIP + 物理）/ 格尺寸 / 实得尺寸 / RenderScaling / 主屏工作区"
                //    一次打全，末尾直接给出"请求是否超出工作区"的比对。判决与消息原文一字未改。
                var screenBounds = Screens.Primary?.Bounds ?? default;
                var workArea = Screens.Primary?.WorkingArea ?? default;
                Log($"[读数] 放大窗口尺寸判据（仅用于区分『产品没落尺寸』与『OS 按工作区钳制』，不改判决）：" +
                    $"第 {i} 路 请求窗口 {geom.WindowDip.Width:F1}x{geom.WindowDip.Height:F1}DIP → " +
                    $"物理 {winPx.Width}x{winPx.Height}px（RenderScaling={scaling:0.##} 该路有效倍率 z={zi:0.##}）；" +
                    $"格 {cellDip.Width:F1}x{cellDip.Height:F1}DIP → 物理 {cellPx.Width}x{cellPx.Height}px；" +
                    $"子 HWND GetWindowRect 实得 {actual.Width}x{actual.Height} @({actual.X},{actual.Y})；" +
                    $"Grid.Count={Grid.Count} 格数={cells.Length} 对比区 {w:F1}x{h:F1}DIP；" +
                    $"主屏整屏 {screenBounds.Width}x{screenBounds.Height} 主屏工作区 {workArea.Width}x{workArea.Height}" +
                    $" ⇒ 请求尺寸{(winPx.Width > workArea.Width || winPx.Height > workArea.Height ? "已超出主屏工作区" : "未超出主屏工作区")}");
                throw new InvalidOperationException(
                    $"第 {i} 路子 HWND 实际尺寸 {actual.Width}x{actual.Height} != 期望 {winPx.Width}x{winPx.Height}" +
                    "（Avalonia 排布了但没落到 Win32 窗口 ⇒ 内核拿不到更大的 swapchain）");
            }

            osRects.Add(actual);   // 判据⑥要用：这一路的 OS 窗口矩形（已确认尺寸落到期望值）

            // ①c OS 窗口搬到了正确位置：屏幕坐标 == 容器屏幕原点 + 容器坐标下的期望窗口。
            //     这一条把"平移"真正钉死 —— 区域回读只证明区域被设上，证明不了窗口在哪。
            if (origin is { } o &&
                (Math.Abs(actual.X - (o.X + winPx.X)) > 2 || Math.Abs(actual.Y - (o.Y + winPx.Y)) > 2))
                throw new InvalidOperationException(
                    $"第 {i} 路子 HWND 屏幕位置 ({actual.X},{actual.Y}) != 容器原点 ({o.X},{o.Y}) + " +
                    $"期望窗口 ({winPx.X},{winPx.Y}) = ({o.X + winPx.X},{o.Y + winPx.Y})（平移未落地）");

            // ② 区域：回读块（窗口原点 + 区域，换算到容器坐标）== 期望块。
            //    比容器坐标而不是窗口相对坐标：拼合下期望块是"放大后的视野 ∩ 本格"，
            //    窗口原点本身是负的/放大的，两边各自取整后在容器系里比只差一个取整残差。
            var type = ReadWindowRegion(s.Hwnd, out var box);
            if (type is not (RegionTypeSimple or RegionTypeComplex))
                throw new InvalidOperationException($"第 {i} 路放大后应已裁剪，回读却 type={type}（0=无区域）");
            var expected = CompareCropPlanner.ToPhysicalRect(visibleDip, scaling);
            var gotBlock = new Rect32(winPx.X + box.X, winPx.Y + box.Y, box.Width, box.Height);
            if (mosaic)
            {
                if (!BlockClose(gotBlock, expected))
                    throw new InvalidOperationException(
                        $"第 {i} 路回读区域换算到容器坐标 {gotBlock} != 期望 {expected}" +
                        $"（窗口相对回读 {box}，窗口 {winPx}，放大后的视野 {contentDip}，格 {cellDip}）");
            }
            else
            {
                // 非拼合沿用**逐字相等**的窗口相对比较（期望 = Magnify 的 RegionDip 过 ToPhysical）：
                // 两侧是同一套取整，没有取整域差异可言，±1 的容差只会掩盖错位。
                var expectedRel = CompareCropPlanner.ToPhysicalRect(geom.RegionDip, scaling);
                if (box != expectedRel)
                    throw new InvalidOperationException(
                        $"第 {i} 路回读区域 {box} != 期望 {expectedRel}（换算或 DPI 不一致）");
            }

            // ③ 非拼合：区域必须**居中**落在格里（补丁前钉左上角，补丁后 (格-区域)/2，
            //    见 CompareCropPlanner.Magnify）。容差 2px：居中量在 DIP 域算，而窗口原点与区域
            //    原点各自经 ToPhysical 取整 ⇒ 两侧各差 1px 时合计可到 2px，这是取整域不是几何错。
            if (!mosaic)
            {
                var expX = cellPx.X + (cellPx.Width - box.Width) / 2.0;
                var expY = cellPx.Y + (cellPx.Height - box.Height) / 2.0;
                if (Math.Abs((winPx.X + box.X) - expX) > 2 || Math.Abs((winPx.Y + box.Y) - expY) > 2)
                    throw new InvalidOperationException(
                        $"第 {i} 路区域未居中落在格里：窗口原点 {winPx.X},{winPx.Y} + 区域原点 {box.X},{box.Y} " +
                        $"!= 格原点 {cellPx.X},{cellPx.Y} + 居中偏移 ({expX - cellPx.X:F1},{expY - cellPx.Y:F1})");
            }
            else
            {
                // ③ 拼合：块必须**在自己那一格内**，且视野覆盖到这一格时块必须**等于整格**。
                //    只看回读与公开几何（不取判据②的换算结果），所以它抓的是另一类坏法：
                //    越界 = 忘了与格求交，会盖住邻居；无端小于格 = 放大后的视野明明在，
                //    格内却露出一圈黑底。区域小于格本身不是缺陷（格超出视野那部分是正常的画面边缘）。
                if (gotBlock.X < cellPx.X - 1 || gotBlock.Y < cellPx.Y - 1 ||
                    gotBlock.X + gotBlock.Width > cellPx.X + cellPx.Width + 1 ||
                    gotBlock.Y + gotBlock.Height > cellPx.Y + cellPx.Height + 1)
                    throw new InvalidOperationException(
                        $"第 {i} 路放大后的块 {gotBlock} 越出它那一格 {cellPx} ⇒ 会盖住邻居的画面，拼合破功");
                var covered = contentDip.X <= cellDip.X + 1e-9 && contentDip.Y <= cellDip.Y + 1e-9 &&
                              contentDip.X + contentDip.Width >= cellDip.X + cellDip.Width - 1e-9 &&
                              contentDip.Y + contentDip.Height >= cellDip.Y + cellDip.Height - 1e-9;
                if (covered && (gotBlock.Width < cellPx.Width - 1 || gotBlock.Height < cellPx.Height - 1))
                    throw new InvalidOperationException(
                        $"第 {i} 路的视野 {contentDip} 已覆盖整格 {cellDip}，回读块却只有 " +
                        $"{gotBlock.Width}x{gotBlock.Height}（格 {cellPx}）⇒ 格内露出黑底");
            }

            // ④ 区域在窗口内（否则 SetWindowRgn 会静默裁掉越界部分）
            if (box.X < -1 || box.Y < -1 ||
                box.X + box.Width > winPx.Width + 1 || box.Y + box.Height > winPx.Height + 1)
                throw new InvalidOperationException($"第 {i} 路区域 {box} 越出窗口 {winPx}");

            // ⑤ letterbox 修正确实生效：只要画面在窗口里被 letterbox（fit ⊊ 窗口），
            //    区域就必须与"把格当成画面"的朴素换算不同 —— 否则宽高比不同的两路会露出不同源区间。
            //    注意不能只比宽度：宽度受限的 letterbox（fit.Width == 窗口宽）下区域宽度恰好等于格宽，
            //    差异体现在高度与偏移上。故与朴素矩形整体比较。
            var fitIsWindow = Math.Abs(geom.FitDip.Width - geom.WindowDip.Width) < 0.5 &&
                              Math.Abs(geom.FitDip.Height - geom.WindowDip.Height) < 0.5;
            if (!fitIsWindow)
            {
                letterboxSeen++;
                var naive = new Rect(ciX * baseDip.Width * zi, ciY * baseDip.Height * zi,
                                     baseDip.Width, baseDip.Height);
                // 两侧都是同一批公开纯函数的<b>浮点</b>结果（不涉及 OS 区域回读）⇒ 这里必须用
                // 浮点等值，不能沿用像素级容差：4 路 + 4K 素材下 2×2 的格宽高比几乎等于源的 16:9，
                // 修正量本身只有约 0.5 单位，用 ~1 的像素容差会把"修正已生效"读成"未生效"
                // （2026-09-26 r=4 那次判红的真实成因，读数里 663.478 vs 664 就是这个量级）。
                var corr = Math.Max(Math.Abs(geom.RegionDip.Width - naive.Width),
                                    Math.Abs(geom.RegionDip.X - naive.X));
                corr = Math.Max(corr, Math.Max(Math.Abs(geom.RegionDip.Height - naive.Height),
                                               Math.Abs(geom.RegionDip.Y - naive.Y)));
                if (corr < minCorr) minCorr = corr;
                if (corr < 1e-6)
                    throw new InvalidOperationException(
                        $"第 {i} 路画面被 letterbox（fit={geom.FitDip} 窗口={geom.WindowDip}），" +
                        $"区域 {geom.RegionDip} 与朴素换算 {naive} 逐值相同（差 {corr:0.#####}）" +
                        " —— letterbox 修正未生效");
            }

            applied++;
        }

        if (applied == 0)
            throw new InvalidOperationException($"z={zoom} 下没有任何一路真正进入放大状态（接线未走到）");

        // ⑥ **屏幕空间**的一致性：各路窗口的 OS 矩形必须两两相同（相对对齐 + 各路同分辨率时，
        //     换算给每一路的 WindowDip 是同一个矩形）。为什么单独钉这一条：判据②/③比的是
        //     "窗口原点 + 回读区域"，两侧都用同一套理想算术 ⇒ 只要 OS 将每一路**同样地**摆偏 1px，
        //     两边就对得上，而屏幕上拼合缝会整体离开分割线 1px（叠加模式有"线与接缝对齐"那条判据，
        //     拼合放大态此前没有对应读数）。这里直接比 GetWindowRect 实测值，不取理想算术。
        var mosaicNow = Grid.MosaicWindows;
        if (mosaicNow && sameSourceForAll)
        {
            var ref0 = osRects[0];
            for (var i = 1; i < osRects.Count; i++)
            {
                var r = osRects[i];
                if (!r.IsValid || !ref0.IsValid) continue;   // 读不到交给上面①b 的等待/判红
                if (Math.Abs(r.X - ref0.X) > 1 || Math.Abs(r.Y - ref0.Y) > 1 ||
                    Math.Abs(r.Width - ref0.Width) > 1 || Math.Abs(r.Height - ref0.Height) > 1)
                    throw new InvalidOperationException(
                        $"放大态各路窗口的 OS 矩形不一致：第 0 路 {ref0} vs 第 {i} 路 {r}" +
                        "⇒ 拼合缝会整体离开画出的分割线（同一份换算给出的明明是同一个矩形）");
            }
            Console.WriteLine($"comparemodetest: [{shape.Cols}x{shape.Rows}] 放大态窗口一致性 ✓ " +
                $"{osRects.Count} 路的 GetWindowRect 两两相同（{ref0.Width}x{ref0.Height} @({ref0.X},{ref0.Y})，容差 1px）");
        }
        else if (mosaicNow)
        {
            Console.WriteLine("comparemodetest: 放大态窗口一致性 不适用：各路源尺寸不同，窗口本就不同矩形");
        }

        Console.WriteLine(
            $"comparemodetest: [{shape.Cols}x{shape.Rows}] {label ?? "无缝放大"} ✓ z={zoom:0.###} 裁剪=({cropX:F2},{cropY:F2}) " +
            $"{applied} 路窗口放大且区域回读一致；整格在视野外（必须无区域）{outsideView} 路；" +
            $"对比区={w:F0}x{h:F0}DIP 缩放={scaling:0.##}；" +
            $"letterbox 修正生效 {letterboxSeen} 路（格宽高比 != 源宽高比" +
            (letterboxSeen > 0 ? $"，最小修正量 {minCorr:0.###}DIP" : string.Empty) + "）；" +
            $"OS 窗口尺寸/位置已按 GetWindowRect 核对{(origin is null ? "（容器屏幕原点未知，跳过位置核对）" : "")}");
        if (mosaicNow)
        {
            // 正常布局下 outsideView 恒为 0 ⇒ 上面那条"整格在视野外必须无区域"的分支写了没跑过。
            // 专门造一次极端分割把它跑实（内部会自己复位格表）。
            await AssertMagnifySliverOutsideViewAsync(label ?? "无缝放大");
        }
        return applied;
    }

    /// <summary>定向夹具：把第一列拖到权重下界，让那一格整个落到**放大后视野之外**，
    /// 要求这一路**没有区域**（留着上一次的区域 = 显示别人视野里的那块，拼合当场错位，而屏幕上看不出来）。
    ///
    /// <para><b>为什么要专门造</b>：正常布局里每格都与视野有交集（2026-10-01 实测 5 路 2x3 全部
    /// 有交集），于是判据里那条空交集分支属于"写了从没跑过"—— 与它同族的第一个漏网 bug
    /// （命中测试恒返回第 0 路）正是因为没跑过才没人发现。</para>
    /// <para><b>前置不成立就明写不适用</b>：拖完仍有交集（黑边比最窄格还窄）、或对齐模式不是相对
    /// （像素级下逐路倍率/裁剪不同，本夹具不复算那套换算）⇒ 打印说明，不冒充通过。</para></summary>
    private async System.Threading.Tasks.Task AssertMagnifySliverOutsideViewAsync(string label)
    {
        var saved = _partition;
        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (wDip <= 0 || hDip <= 0) return;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        try
        {
            if (saved.HandleCount == 0 || saved.HandleAxisAt(0) != SplitAxis.X)
            {
                Console.WriteLine($"  视野外格判据 不适用：{_partition.Cols}x{_partition.Rows} 的第一条手柄不是竖线");
                return;
            }
            if (_compareAlign != CompareAlign.Relative)
            {
                Console.WriteLine($"  视野外格判据 不适用：当前是对齐={_compareAlign}，本夹具不复算逐路倍率/裁剪");
                return;
            }

            // 把手柄 0 拖到最左：权重模型的下界是 MinSpanFraction（5%），所以这一列会被钳到
            // 最窄（≈77 DIP），而本机 letterbox 黑边半宽 ≈135 DIP ⇒ 该格整个落到视野之外。
            OnPartitionChanged(saved.WithHandleDragged(0, 0.0, 0.5));
            UpdateLayout();
            ApplyCompareCrop();
            await System.Threading.Tasks.Task.Delay(120);   // 让排在一拍后的裁剪/ShowInBounds 落地
            UpdateLayout();
            ApplyCompareCrop();

            var cells = _partition.ComputeCells();
            var sliverRoute = Grid.RouteIndexOfCell(0);
            var s = sliverRoute >= 0 ? Grid.GetSurface(sliverRoute) : null;
            if (cells.Length == 0 || s is null || s.Hwnd == nint.Zero || !s.IsVisible)
            {
                Console.WriteLine("  视野外格判据 不适用：拖窄第一列后没有可判的那一路");
                return;
            }

            // 与判据②同一套公开纯函数复算这一格的"应当露出的块"（只是换了几何，不是另一套算术）
            var src = ReadSourceSize(sliverRoute);
            var geom = CompareCropPlanner.Magnify(new Rect(0, 0, wDip, hDip), _compareZoom,
                _compareCropX, _compareCropY, src.Width, src.Height);
            var contentDip = new Rect(geom.RegionDip.X + geom.WindowDip.X,
                                      geom.RegionDip.Y + geom.WindowDip.Y,
                                      geom.RegionDip.Width, geom.RegionDip.Height);
            var cellDip = CompareCropPlanner.MosaicRegionDip(cells[0], wDip, hDip);
            var visible = CompareCropPlanner.Intersect(contentDip, cellDip);
            if (visible.Width > 0 && visible.Height > 0)
            {
                Console.WriteLine($"  视野外格判据 不适用：拖到最窄后该格 {cellDip.Width:F1}x{cellDip.Height:F1}DIP " +
                                  $"仍与视野 {contentDip.Width:F1}x{contentDip.Height:F1}DIP 有交集（黑边不够宽）");
                return;
            }

            var type = ReadWindowRegion(s.Hwnd, out var stale);
            if (type != RegionTypeError)
                throw new InvalidOperationException(
                    $"{label}·视野外格：第 {sliverRoute} 路的格 {cellDip} 整个在放大视野 {contentDip} 之外，" +
                    $"却仍带着区域 {stale}（type={type}）⇒ 它显示的是上一次方案裁出来的那块，" +
                    "各路窗口不同时就是别人的画面");
            Console.WriteLine($"  视野外格 ✓ 拖到最窄（{cellDip.Width:F1}x{cellDip.Height:F1}DIP 全在黑边里）" +
                              $"后第 {sliverRoute} 路无区域（GetWindowRgn=ERROR）");
        }
        finally
        {
            OnPartitionChanged(saved);
            UpdateLayout();
            ApplyCompareCrop();
        }
    }

    /// <summary>由第 0 路的"屏幕矩形 − 容器坐标矩形"反推对比区左上角在屏幕上的物理坐标。
    /// <para>调用时机必须是"该路窗口恰好等于格"的稳定状态（放大前）：此时 <c>Bounds</c> 与 HWND
    /// 的屏幕矩形是同一次排布的产物，相减即可消掉位置误差。</para>
    /// <para>返回 null 表示暂时算不出来（HWND 未创建 / 窗口尚未 Show）—— 调用方据此跳过位置核对，
    /// 而不是拿一个错误的原点去判失败。</para></summary>
    private PixelPoint? CaptureContainerScreenOrigin()
    {
        var s = Grid.GetSurface(0);
        if (s is null || s.Hwnd == nint.Zero) return null;
        var rect = ReadWindowRectPx(s.Hwnd);
        if (!rect.IsValid) return null;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var bounds = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
        if (!bounds.IsValid) return null;
        return new PixelPoint(rect.X - bounds.X, rect.Y - bounds.Y);
    }

    /// <summary>断言复位后各路窗口回到**该回的形态**、放大参数清空。
    /// <para>拼合模式：窗口回到整对比区（不再被放大到 z 倍）、区域回到各自那一格；
    /// 非拼合：窗口回到格尺寸。两种形态都必须让 OS 窗口真的搬回去 ——
    /// 否则内核仍按放大后的分辨率渲染，白烧显存。</para>
    /// <para>同样必须 async：<c>ResetCompareMagnify</c> 只改排版，把子 HWND 搬回去的是
    /// <c>AfterRender</c> 的 <c>ShowInBounds</c>，需要让出一拍 Dispatcher 才能观察到。</para></summary>
    private async System.Threading.Tasks.Task AssertCompareMagnifyResetAsync(int routes)
    {
        _step = "无缝放大-复位";
        ResetCompareMagnify();
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();
        ApplyCompareCrop();

        if (CompareMagnifyActive)
            throw new InvalidOperationException("复位后仍报告处于放大状态");
        if (Grid.CellMagnify is not null)
            throw new InvalidOperationException("复位后 Grid.CellMagnify 未清空（排版仍会放大窗口）");

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = _partition.ComputeCells();
        var mosaic = Grid.MosaicWindows;
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            var cellIndex = Grid.CellIndexOfRoute(i);
            if (s is null || !s.IsVisible || cellIndex < 0 || cellIndex >= cells.Length) continue;

            if (mosaic)
            {
                // 复位在拼合下的含义：窗口不再被放大到对比区的 z 倍（回到恰好铺满），
                // 区域回到本格（放大时它是"格 ∩ 放大后的视野"，通常小于格）。
                var win = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
                var expWin = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, w, h), scaling);
                if (!BlockClose(win, expWin))
                    throw new InvalidOperationException(
                        $"复位后第 {i} 路窗口 {win} 未回到对比区 {expWin}（放大尺寸没退掉 ⇒ 内核仍在按更大的 swapchain 渲染）");
                AssertRouteBlockAt(i, cells[cellIndex], "无缝放大-复位后");
                continue;
            }

            var exp = CompareCropPlanner.CellToContainerDip(cells[i], w, h);
            var got = s.Bounds;
            if (Math.Abs(got.Width - exp.Width) > 1.5 || Math.Abs(got.Height - exp.Height) > 1.5 ||
                Math.Abs(got.X - exp.X) > 1.5 || Math.Abs(got.Y - exp.Y) > 1.5)
                throw new InvalidOperationException($"复位后第 {i} 路窗口未回到格尺寸：Bounds={got} 期望 {exp}");

            // OS 窗口也必须回到格尺寸（否则内核仍按放大后的分辨率渲染，白烧显存）
            var expPx = CompareCropPlanner.ToPhysicalRect(exp, scaling);
            var actual = ReadWindowRectPx(s.Hwnd);
            if (Math.Abs(actual.Width - expPx.Width) > 2 || Math.Abs(actual.Height - expPx.Height) > 2)
                throw new InvalidOperationException(
                    $"复位后第 {i} 路子 HWND 尺寸 {actual.Width}x{actual.Height} != 格 {expPx.Width}x{expPx.Height}");
        }

        Console.WriteLine($"comparemodetest: 无缝放大复位 ✓ {(mosaic ? "窗口回到整对比区、区域回到各自那一格" : "子窗口回到格尺寸")}、放大参数已清空");
    }

    /// <summary>滚轮 → 无缝放大链路（生产 UI 的触发路径）。
    ///
    /// <para><b>为什么必须单独断言</b>：内核 <c>SetCompareMagnify</c> 早已完整且经实测有效，但
    /// <b>在它接上滚轮之前</b>全仓库只有自测 <c>--magnifybench</c> 会调它（docs/26 阶段的现状）；
    /// 今天的生产路径是 滚轮 → <c>TryHandleCompareWheel</c> → <c>SetCompareMagnify</c>
    /// （<c>MainWindow.axaml.cs:1501/1536</c>、<c>MainWindow.CompareCrop.cs:187</c>）。
    /// 而既有的滚轮断言只验单路 <c>_viewZoom</c>，对"滚轮是否接到了多路同步放大"毫无鉴别力 ——
    /// 接线与不接线都能过，这才是本步要补的鉴别力。</para>
    ///
    /// <para>三条判据：① 对比模式下滚轮被无缝放大<b>接管</b>；② 倍率真的变了，且<b>没有</b>
    /// 同时动单路 <c>_viewZoom</c>（两条缩放路径同时生效会互相打架）；③ 放大真的落到窗口区域上
    /// （复用<b>放大态</b>判据 <see cref="AssertCompareMagnifyAsync"/>，期望区域取
    /// <c>CompareCropPlanner.Magnify</c> 的 fit 框）；④ 缩到底能退出放大（不留残余放大窗口）。</para>
    ///
    /// <para><b>判据③此前用错了域（docs/46 §三.5 ②，2026-09-26 换掉）</b>：这里原来复用的是
    /// <b>z=1 未放大态</b>的 <see cref="AssertCompareCrop"/>，它的期望是"格在窗口坐标下的矩形"。
    /// 而放大态下生产下发的是 <c>Magnify</c> 算出的 <b>fit 框</b>（宽高比失配的格上 fit ⊊ 格，
    /// r=2 实测回读 995×559 vs 期望 1995×559，差一倍），两者本就不该相等 ⇒ 期望值与生产永远对不上，
    /// r=2/r=3 全线在 ~6.5s 判红并把后面的互换/轮换/对齐/变体/叠加一起带掉。
    /// 换判据不是"为了让它变绿"：放大态判据的五件套（排版 == 放大窗口、OS 尺寸、OS 位置、区域回读、
    /// 区域居中落在格里、letterbox 修正生效）逐条覆盖旧的裁剪判据，且期望值同样由公开纯函数独立复算，
    /// 判决强度不降。若换成正确判据后仍红，那就是真缺陷，保留红色。</para></summary>
    private async System.Threading.Tasks.Task<int> AssertCompareWheelMagnifyAsync(int routes, (int Cols, int Rows) shape)
    {
        await EnterGridShapeAsync(shape.Cols, shape.Rows);
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        if (CompareMagnifyActive)
            throw new InvalidOperationException("滚轮测试前置失败：进入对比模式后已处于放大状态");

        var zoomBefore = _compareZoom;
        var viewZoomBefore = _viewZoom;

        // 容器屏幕原点必须在这里、也就是**滚轮放大之前**取：滚轮一按窗口就被排成 z 倍大，
        // 而子 HWND 是 AfterRender 的 ShowInBounds 才搬过去的 —— 放大之后再差分"屏幕矩形 − Bounds"
        // 取到的原点不可信（判据①c 会假红）。未放大态下窗口恰好等于格，才是这个差分的适用前提。
        var originBeforeMagnify = CaptureContainerScreenOrigin();

        if (!TryHandleCompareWheel(120))
            throw new InvalidOperationException(
                "对比模式下滚轮未被无缝放大接管（TryHandleCompareWheel 返回 false）—— 生产触发路径未接线");

        if (!(_compareZoom > zoomBefore + 1e-9))
            throw new InvalidOperationException(
                $"滚轮未改变无缝放大倍率：{zoomBefore} → {_compareZoom}（接管了但没真的放大）");

        if (Math.Abs(_viewZoom - viewZoomBefore) > 1e-6)
            throw new InvalidOperationException(
                $"滚轮同时改了单路 _viewZoom（{viewZoomBefore} → {_viewZoom}）：两条缩放路径被同时触发，" +
                "窗口尺寸与内核视口会互相打架");

        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(250);
        ApplyCompareCrop();
        UpdateLayout();

        // 放大必须真的落到窗口区域上（否则只是改了个字段，画面毫无变化）。
        // 判据取**滚轮当前实到的倍率与裁剪位置**（放大态判据内部会再 SetCompareMagnify 一次，
        // 同参数下是幂等的：PushCompareMagnifyToGrid 按值比、ApplyCompareCrop 按区域去重）。
        // 还原 _step 是为了失败消息仍指向"步骤对比-滚轮无缝放大"，不被判据内部的步骤名顶掉。
        var stepBeforeAssert = _step;
        int applied;
        try
        {
            applied = await AssertCompareMagnifyAsync(
                routes, shape, _compareZoom, _compareCropX, _compareCropY,
                originOverride: originBeforeMagnify, label: "滚轮无缝放大");
        }
        finally
        {
            _step = stepBeforeAssert;
        }

        // ── 放大态拖动的"拖动中就提交"判据（2026-09-29 改的行为，之前只有成本实测没有功能判据）──
        // 走生产同一条链：置 _panDragging（原本由 OnSurfacePress 做）→ BeginComparePan → OnSurfaceMove。
        // **不调 OnSurfaceRelease** —— 本条要验的正是"没松手也已经提交"。
        // 先把 crop 播到一个已知中段值：贴 0 或贴上界时钳位会吞掉位移，那种"没变"是几何极限，
        // 会把判据变成随机红。dx 取正、dy 取 0 ⇒ 顺带验两轴独立（纵轴必须一动不动）。
        _step = "对比-拖动实时提交";
        if (!SetCompareMagnify(_compareZoom, 0.06, 0.06))
            throw new InvalidOperationException("拖动判据前置失败：SetCompareMagnify 拒绝把 crop 播到 (0.06,0.06)");
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(120);

        if (!GetCursorPos(out var dragSeed))
            throw new InvalidOperationException("拖动判据前置失败：GetCursorPos 取不到游标（本判据靠它的差值造位移）");

        var cropXSeed = _compareCropX;
        var cropYSeed = _compareCropY;
        _panDragging = true;
        BeginComparePan();
        // 假装"上一次"的游标在左边 24 物理像素处 ⇒ 本次 dx = +24（画面右移 = crop 左移）。取小位移是因为 140
        // 会把 crop 一口气推到 0 钳位，那时"变了"可能只是顶到头，分不清是不是真提交了。
        _panLastX = dragSeed.X - 24;
        _panLastY = dragSeed.Y;
        try
        {
            OnSurfaceMove(0, 0);
        }
        finally
        {
            _panDragging = false;
        }

        if (!(_compareCropX < cropXSeed - 1e-9))
            throw new InvalidOperationException(
                $"放大态拖动没有实时提交：一次 OnSurfaceMove(dx=+24) 后 crop 仍是 " +
                $"({_compareCropX:F4},{_compareCropY:F4})，播种值 ({cropXSeed:F4},{cropYSeed:F4})。" +
                "拖动中必须按 ~60Hz 合并提交一次（--magnifybench 的 [平移提交] 实测 p95 0.27ms、" +
                "34 次/秒零 hitch），否则用户看到的就是\"拖着不动、松手才跳\"");

        // ⚠ 这里原先还顺带断言"纵轴必须一动不动"，那是**建在操作系统游标上**的判据：
        // OnSurfaceMove 内部自己再读一次 GetCursorPos，于是 dy = "播种取值之后到本次调用之前"
        // 游标真的被挪了多少。RTSS/别的程序/人手动碰一下鼠标 ⇒ 偶发判红（2026-09-30 全轮跑出
        // cropY 0.0600→0.0624 一例；随后 12 次交错 A/B 两臂均 0 红 ⇒ 与当晚两处降增量改动无关）。
        // 那是 harness 自己在测量环境抖动，不是产品两轴串了 ⇒ 生产链路这一处只保留它真正能立的
        // 主张（"没松手就已经提交"）；两轴独立改由下面那条**不碰游标**的确定性判据来钉。
        if (Math.Abs(_compareCropY - cropYSeed) > 1e-9 && GetCursorPos(out var jitterCheck))
            Console.WriteLine(
                $"comparemodetest: 拖动实时提交：纵轴记到 Δcrop={_compareCropY - cropYSeed:+0.0000;-0.0000}" +
                $"（OnSurfaceMove 自读 GetCursorPos，播种后游标被移动就会产生它，只登记不判红；" +
                $"两轴独立由下一条判据验）");

        // 两轴独立：走同一条累积/提交实现，但**位移是传进来的**，不经过操作系统游标 ⇒ 确定性。
        // dx=+24、dy=0 ⇒ 纵轴必须逐位不动；若哪天换算把两轴混到一起（或除错分母），这里必红。
        var axisXSeed = _compareCropX;
        var axisYSeed = _compareCropY;
        AccumulateComparePan(24, 0);
        CommitComparePan();
        if (!(_compareCropX < axisXSeed - 1e-9))
            throw new InvalidOperationException(
                $"两轴独立判据前置失效：喂入 (dx=24, dy=0) 后横轴没动（{axisXSeed:F4}→{_compareCropX:F4}）" +
                "⇒ 这一枪会因'什么都没变'而恒真，不能当通过");
        if (Math.Abs(_compareCropY - axisYSeed) > 1e-12)
            throw new InvalidOperationException(
                $"拖动只该动横轴，纵轴却从 {axisYSeed:F4} 变成 {_compareCropY:F4} ⇒ 两轴换算串了" +
                "（本条不读游标，红了就是产品的换算错，不是环境抖动）");

        // ── 平移**幅度**判据（2026-10-01 新增，因为拼合改了基准）────────────────
        // 上面两条只判"动了没有 / 另一轴没动"，**不判动多少** ⇒ 换算系数错 2 倍也能全绿。
        // 而拼合恰好把这件事变成活的：`Magnify` 的基准现在是**整个对比区**（窗口 == 对比区），
        // 旧的"窗口 == 格"几何里除的是格宽。日志实证（2x3、z=1.15、scaling 1.5）：dx=24px 让
        // crop 走 0.0181，反解除数 884 DIP = (1537/2)×1.15 = 半宽格×z，正确应是 1537×1.15=1768
        // ⇒ 横轴快了 2 倍、纵轴（1/3 高格）快 3 倍，且钳位让"拖到底"只用掉一半行程。
        // 期望值只取公开几何（对比区宽 + 当前倍率 + 对齐模式的量程），不调生产的私有换算。
        // ⚠ 只在**拼合**下判：非拼合（对照臂 FC_NOMOSAIC / 历史形态）的基准是"光标所在那一格"，
        //    而光标位置不由本夹具控制 ⇒ 期望值算不出来，硬判就是随机红 ⇒ 明写不适用，不静默跳过。
        if (!Grid.MosaicWindows)
        {
            Console.WriteLine("  平移幅度 不适用：非拼合排布的基准是光标所在那一格，光标位置不可控");
        }
        else
        {
            var magZoom = _compareZoom;
            var magPixel = _compareAlign == CompareAlign.Pixel;
            // 播种到量程中段：贴 0 或贴上界时钳位会把两种换算洗成同一个结果（那是"顶到头"，不是判据）。
            var magMaxCrop = magPixel ? 1.0 : CompareCropPlanner.MaxCropFraction(magZoom);
            if (!SetCompareMagnify(magZoom, magMaxCrop * 0.5, magMaxCrop * 0.5))
                throw new InvalidOperationException("平移幅度判据前置失败：无法把 crop 播到量程中段");
            UpdateLayout();
            var magScale = CompareCropScaling();
            if (!(magScale > 0)) magScale = 1.0;
            const int MagDragPx = 60;                   // 物理像素，够小不至于一步推到钳位
            var magBaseDip = Grid.Bounds.Width;         // 拼合：窗口铺满对比区 ⇒ 基准就是对比区
            var magDivisor = magBaseDip * (magPixel ? magZoom - 1.0 : magZoom);
            var magExpected = (MagDragPx / magScale) / magDivisor;
            // 夹具自检：期望值必须与"按格宽换算"的那个候选**差得远**，否则这一枪没有鉴别力
            // （1 列 1 行时两者重合 ⇒ 明说"本形状判不了"，而不是让绿冒充验过）。
            var magCellDivisor = Math.Max(1, _partition.ComputeCells()[0].Width * Grid.Bounds.Width)
                                 * (magPixel ? magZoom - 1.0 : magZoom);
            if (magCellDivisor <= 0 || Math.Abs(magDivisor - magCellDivisor) / magExpected < 0.2)
            {
                Console.WriteLine($"  平移幅度 不适用：本形状（{_partition.Cols}x{_partition.Rows}）下" +
                                  "『按对比区』与『按格』两种基准相差 <20%，量不出差别");
            }
            else
            {
                var magXSeed = _compareCropX;
                AccumulateComparePan(MagDragPx, 0);
                CommitComparePan();
                var magActual = magXSeed - _compareCropX;
                if (_compareCropX <= 1e-9 || Math.Abs(_compareCropX - magMaxCrop) < 1e-9)
                    throw new InvalidOperationException(
                        $"平移幅度判据前置失效：这一枪把 crop 推到钳位边界（{magXSeed:F4}→{_compareCropX:F4}）" +
                        "⇒ 顶到头的差值不能当读数，请缩小位移或换倍率");
                if (Math.Abs(magActual - magExpected) > magExpected * 0.08)
                    throw new InvalidOperationException(
                        $"平移换算系数不对：喂入 dx={MagDragPx}px（= {MagDragPx / magScale:F1} DIP）应推进 crop " +
                        $"{magExpected:F5}（基准=对比区 {magBaseDip:F1} DIP × z{(magPixel ? "−1" : "")}" +
                        $"={magDivisor:F1}），实际推进 {magActual:F5}（{magXSeed:F4}→{_compareCropX:F4}，" +
                        $"是期望的 {magActual / magExpected:F2} 倍）" +
                        "⇒ 拼合下窗口==整对比区，基准取错就是『手拖 1px、画面走 N px』");
                Console.WriteLine(
                    $"  平移幅度 ✓ dx={MagDragPx}px → Δcrop={magActual:F5}（期望 {magExpected:F5}，" +
                    $"对比区 {magBaseDip:F1}DIP，z={magZoom:0.##}，" +
                    $"{(magPixel ? "像素级量程 z−1" : "相对量程 z")}，形状 {_partition.Cols}x{_partition.Rows}）");
            }
        }

        Console.WriteLine(
            $"comparemodetest: 拖动实时提交 ✓ 未松手即提交，crop ({cropXSeed:F4},{cropYSeed:F4}) → " +
            $"({_compareCropX:F4},{_compareCropY:F4})；两轴独立另测一条 (dx=24,dy=0)：横轴 " +
            $"{axisXSeed:F4}→{_compareCropX:F4}、纵轴 {axisYSeed:F4}→{_compareCropY:F4} 逐位未动");

        // 缩到底必须退出放大：连续反向滚动，倍率单调回落到 1
        var guard = 0;
        while (CompareMagnifyActive && guard++ < 60)
        {
            TryHandleCompareWheel(-120);
            await System.Threading.Tasks.Task.Delay(20);
        }
        if (CompareMagnifyActive)
            throw new InvalidOperationException(
                $"滚轮缩到底后仍处于放大状态（z={_compareZoom}）：退出路径未接通");

        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        ApplyCompareCrop();

        Console.WriteLine(
            $"comparemodetest: 滚轮→无缝放大 ✓ z {zoomBefore:0.##} → 放大 → 缩回 1.0，" +
            $"单路 _viewZoom 未被改动，裁剪区域下发 {applied} 次");
        return applied;
    }

    /// <summary>「左右互换」（格→路映射的最小入口）。
    ///
    /// <para><b>判据</b>：互换后<b>第 1 路占据原第 0 路的那一块、第 0 路占据原第 1 路的那一块</b>；
    /// 再点一次必须还原。期望值取自"互换前实测的<b>露出块</b>"（拼合模式 = 窗口原点 + 回读区域），
    /// 不是复算 —— 这里要验的正是"两路的位置真的被对调了"，复算会与实现同源。
    /// 互换前后各路窗口 Bounds 恒等于整对比区、毫无鉴别力，观测量只能是区域。</para>
    ///
    /// <para><b>前置：两格矩形必须不同</b>。默认 0.5/0.5 的 AB 两格等宽等高、只有 X 不同
    /// （仍有鉴别力），但拖成 0.35 后宽高也不同 ⇒ 断言更强，且能抓住"只换了宽没换位置"
    /// 这类错误。</para>
    ///
    /// <para><b>为什么不动路号语义</b>：互换只改"格 ↔ 路"的对应关系，不碰
    /// <c>surface↔slot</c> —— 否则第 1 路的偏移、选中态、媒体信息都会跟着搬走，
    /// 那是改数据而不是改呈现。</para></summary>
    private async System.Threading.Tasks.Task AssertSwapCompareCellsAsync(int routes)
    {
        if (routes < 2) throw new InvalidOperationException($"互换需要 ≥2 路，当前 {routes} 路");

        EnterCompareMode();
        await System.Threading.Tasks.Task.Delay(150);
        // 非对称分割：让相邻两格宽高不同，避免"只换位置没换尺寸"的错误逃过断言
        OnPartitionChanged(_partition.WithHandleDragged(0, 0.35, 0.6));
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
        ApplyCompareCrop();

        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var before0 = RouteVisibleRectPx(0, scaling, "互换前");
        var before1 = RouteVisibleRectPx(1, scaling, "互换前");

        if (BlockClose(before0, before1))
            throw new InvalidOperationException(
                $"互换断言空转：两路露出的块相同（{before0}），互换后无法区分" +
                "（拼合下观测量是窗口区域，它没跟着格表走 ⇒ 先查裁剪下发）");

        OnSwapCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);   // 让 OnSwapCompareCells 排的那一拍裁剪落地
        UpdateLayout();

        var after1 = RouteVisibleRectPx(1, scaling, "互换后");
        var after0 = RouteVisibleRectPx(0, scaling, "互换后");
        if (!BlockClose(after1, before0))
            throw new InvalidOperationException(
                $"互换后第 1 路未占据原第 0 路的块：{after1} 期望 {before0}");
        if (!BlockClose(after0, before1))
            throw new InvalidOperationException(
                $"互换后第 0 路未占据原第 1 路的块：{after0} 期望 {before1}");

        // 再点一次必须还原（幂等）—— 也顺带证明"互换是可逆的呈现操作"
        OnSwapCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        if (!BlockClose(RouteVisibleRectPx(0, scaling, "再次互换后"), before0))
            throw new InvalidOperationException(
                $"再次互换未还原：第 0 路露出的块 {RouteVisibleRectPx(0, scaling, "再次互换后")} 期望 {before0}");
        if (!BlockClose(RouteVisibleRectPx(1, scaling, "再次互换后"), before1))
            throw new InvalidOperationException(
                $"再次互换未还原：第 1 路露出的块 {RouteVisibleRectPx(1, scaling, "再次互换后")} 期望 {before1}");

        Console.WriteLine(
            $"comparemodetest: 左右互换 ✓ 两路露出的块对调后还原（{before0.Width}x{before0.Height}px ↔ " +
            $"{before1.Width}x{before1.Height}px，观测量=窗口区域回读）");
    }

    /// <summary>分辨率对齐模式（<see cref="CompareAlign"/>）的两条真机契约。
    ///
    /// <para><b>相对对齐（默认路径）</b>：逐路结果必须<b>恒等于</b>共用值 —— 这才是
    /// "新增对齐模式不得污染默认行为"的正确表述。</para>
    ///
    /// <para><b>像素级对齐</b>：各路露出的源像素区间必须等于按定义独立算出的参考值
    /// （起点 <c>p = view·baseSize·(1-1/z)</c>、宽度 <c>baseSize/z</c>），且区域不出画面。
    /// 本自测用同一素材复制多路 ⇒ 各路源分辨率相同 ⇒ <c>z_i = z</c>、<c>baseSize = W_i</c>。</para>
    ///
    /// <para>⚠ <b>历史上这里写错过一次判据</b>（2026-09-26 首次执行到本步时当场判红）：
    /// 原文是"等分辨率下像素级必须与相对逐值相同（<c>crop_i = crop</c>）"。那个前提<b>对任何 z&gt;1
    /// 都不成立</b> —— 像素级模式下共用 <c>CropX/Y</c> 的含义从"归一化位置"变成
    /// "可平移范围内的相对位置"（<c>AlignedCrop</c> 的文档写明），可平移范围恒为
    /// <c>baseSize·(1-1/z)</c>，所以等分辨率下 <c>crop_i = view·(1-1/z)</c> 而不等于 <c>view</c>
    /// （z=2、view=0.25 ⇒ 0.125）。纯算术层的 <c>CompareCropPlannerAlignTests</c>
    /// 一直把这个 (1-1/z) 因子钉着（含 z=1 时 crop 恒为 0 那一例），两套判据此前互相矛盾。
    /// 之所以一直没暴露：门禁此前总在这一步<b>之前</b>中止（docs/46 记为"步名 0 命中"）。</para>
    ///
    /// <para>另一半（各路分辨率<b>不同</b>时必须真的对齐）由 <c>CompareCropPlannerAlignTests</c>
    /// 在纯算术层穷举 —— 真机要凑不同分辨率的素材，且肉眼判断不了"露出的像素数是否相同"。</para></summary>
    private async System.Threading.Tasks.Task AssertCompareAlignModesAsync(int routes, (int Cols, int Rows) shape)
    {
        await EnterGridShapeAsync(shape.Cols, shape.Rows);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        // 前提检查（#29 多素材入口当场顶出来的新面）：像素级对齐下逐路**有效倍率** z_i = z·W_i/minW，
        // 源尺寸不同 ⇒ 按"各路同尺寸"估出来的 z 会被显存闸门**正确地**拒掉。
        // 实测（720p + 4K 两路、1x2 拼合、z=2）：4K 路等效 6 倍 ⇒ `对齐模式测试：像素级对齐下启用放大失败`。
        // 那不是缺陷，是本条判据的前提没了 —— 本节文档原文就写着"本自测用同一素材复制多路 ⇒ 各路源分辨率相同"。
        // ⇒ 混尺寸时登记**未判**：既不记红（闸门没错），也不记绿（这条没量到东西）。
        var srcSizes = ReadCompareMagnifySources();
        var distinctSrc = srcSizes.Select(p => $"{p.Width}x{p.Height}").Distinct().ToArray();
        if (distinctSrc.Length > 1)
        {
            RecordSelfTestSkip($"对齐模式：本轮混了 {distinctSrc.Length} 种源尺寸（{string.Join("/", distinctSrc)}）" +
                               "⇒ 前提「各路源分辨率相同」不成立，像素级下 z_i=z·W_i/minW 会把 z 顶到闸门之外 ⇒ 未判" +
                               "（不同分辨率的对齐已由 CompareCropPlannerAlignTests 在算术层穷举）");
            Console.WriteLine("  对比-对齐模式：**跳过**（混尺寸，未验证 ≠ 通过）");
            return;
        }

        var savedAlign = _settings.CompareAlign;
        // 本用例验的是"逐路参数与对齐模式"的算术，与具体倍率无关 ⇒ 倍率按预算取
        //（拼合后 9 路 z=2 超预算，被拒才是正确行为；见 AffordableMagnifyZoom）。
        var zoomAffordable = AffordableMagnifyZoom(routes);
        try
        {
            // 相对对齐（默认）：逐路结果必须等于共用值
            _settings.CompareAlign = (int)CompareAlign.Relative;
            if (!SetCompareMagnify(zoomAffordable, 0.25, 0.25))
                throw new InvalidOperationException(
                    $"对齐模式测试：相对对齐下启用放大失败（z={zoomAffordable:0.##} 被闸门拒绝）");
            UpdateLayout();
            await System.Threading.Tasks.Task.Delay(200);
            var rel = Grid.CellMagnify
                      ?? throw new InvalidOperationException("对齐模式测试：Grid.CellMagnify 为空");
            if (rel.Align != CompareAlign.Relative)
                throw new InvalidOperationException($"对齐模式未传到排版参数：{rel.Align}");

            // 相对对齐（默认路径）：逐路结果必须**恒等于**共用值 —— 这才是"新增模式不得污染
            // 默认行为"的正确表述。此前这一半只查了 Align 字段，逐路恒等在相对模式这边从未被钉过。
            for (var i = 0; i < routes; i++)
            {
                var s = rel.SourceOf(i);
                if (s.Width <= 0) continue;
                var rc = rel.CropOf(i);
                var rz = rel.ZoomOf(i);
                if (Math.Abs(rz - rel.Zoom) > 1e-9 ||
                    Math.Abs(rc.CropX - rel.CropX) > 1e-9 || Math.Abs(rc.CropY - rel.CropY) > 1e-9)
                    throw new InvalidOperationException(
                        $"相对对齐下第 {i} 路不再等于共用值：z {rz} vs {rel.Zoom}，" +
                        $"crop ({rc.CropX},{rc.CropY}) vs 共用 ({rel.CropX},{rel.CropY})");
            }

            // 像素级对齐：等分辨率 ⇒ 必须与上面完全一致
            _settings.CompareAlign = (int)CompareAlign.Pixel;
            if (!SetCompareMagnify(zoomAffordable, 0.25, 0.25))
                throw new InvalidOperationException(
                    $"对齐模式测试：像素级对齐下启用放大失败（z={zoomAffordable:0.##} 被闸门拒绝）");
            UpdateLayout();
            await System.Threading.Tasks.Task.Delay(200);
            UpdateLayout();

            var pix = Grid.CellMagnify
                      ?? throw new InvalidOperationException("对齐模式测试：Grid.CellMagnify 为空");
            if (pix.Align != CompareAlign.Pixel)
                throw new InvalidOperationException($"对齐模式未传到排版参数：{pix.Align}");

            // 读出各路真实源尺寸，确认真的是"等分辨率"（否则下面的断言前提不成立）
            var min = CompareCropPlanner.MinSourceSize(pix.Sources);
            if (min.Width <= 0)
                throw new InvalidOperationException(
                    "对齐模式测试空转：读不到任何一路的源尺寸（演示模式？），等分辨率前提无法坐实");

            // 参考值按**定义**独立算出（不读实现的中间量）：本用例请求的 view 就是共用裁剪分量。
            var viewX = pix.CropX;
            var viewY = pix.CropY;
            var refStartX = viewX * min.Width * (1.0 - 1.0 / pix.Zoom);
            var refStartY = viewY * min.Height * (1.0 - 1.0 / pix.Zoom);
            var refShownWidth = (double)min.Width / pix.Zoom;

            for (var i = 0; i < routes; i++)
            {
                var src = pix.SourceOf(i);
                if (src.Width <= 0) continue; // 未知路：退化路径，不参与
                if (src.Width != min.Width)
                    throw new InvalidOperationException(
                        $"对齐模式测试前提不成立：第 {i} 路源宽 {src.Width} ≠ 最小 {min.Width}" +
                        "（本用例只在各路等分辨率下断言：露出的源像素区间必须等于按定义算出的参考值）");

                var zi = pix.ZoomOf(i);
                if (Math.Abs(zi - pix.Zoom) > 1e-9)
                    throw new InvalidOperationException(
                        $"等分辨率下像素级对齐却改了第 {i} 路的有效倍率：{zi} ≠ 共用 {pix.Zoom}");

                // 像素级对齐的**定义**：各路露出同一段源像素区间，起点
                //   p = view·baseSize·(1 - 1/z)。期望值由本用例自己按定义独立算出
                //   （view 取自共用裁剪分量、baseSize 取自读到的真实源尺寸），不引用实现内部写法。
                // ⚠ 这里不能拿"与相对对齐逐值相等"当判据：共用 CropX/Y 在像素级模式下的含义
                //   是"可平移范围内的相对位置"（见 CompareCropPlanner.AlignedCrop 文档），
                //   故等分辨率下也必然满足 crop = view·(1-1/z)（z=2 时 0.25→0.125）。
                //   (1-1/z) 这个因子是数学必然：z=1 时可平移范围为 0，最小那一路也放不下任何偏移
                //   —— 那一点由 CompareCropPlannerAlignTests 单独钉住。
                var c = pix.CropOf(i);
                var startX = c.CropX * src.Width;
                var startY = c.CropY * src.Height;
                var shownWidth = src.Width / zi;
                if (Math.Abs(startX - refStartX) > 1e-4 || Math.Abs(startY - refStartY) > 1e-4)
                    throw new InvalidOperationException(
                        $"第 {i} 路露出的源像素起点与定义不符：({startX:0.####},{startY:0.####}) ≠ " +
                        $"参考 ({refStartX:0.####},{refStartY:0.####})" +
                        $"（view=({viewX},{viewY}) z={pix.Zoom} 基准 {min.Width}x{min.Height}）");
                if (Math.Abs(shownWidth - refShownWidth) > 1e-4)
                    throw new InvalidOperationException(
                        $"第 {i} 路露出的源像素宽度 {shownWidth:0.###} ≠ 基准路的 {refShownWidth:0.###}" +
                        "（像素级对齐的全部意义就是各路露出同样多的源像素）");

                // 边界：区域不得越出画面 —— 越界时 SetWindowRgn 会静默裁掉，用户只看到"缺一块"。
                if (c.CropX + 1.0 / zi > 1.0 + 1e-9)
                    throw new InvalidOperationException(
                        $"第 {i} 路的像素级对齐区域越出画面：crop={c.CropX} + 1/z_i={1.0 / zi} > 1");
            }

            Console.WriteLine(
                $"comparemodetest: 对齐模式 ✓ 相对逐路恒等共用值；像素级等分辨率（{min.Width}x{min.Height}）" +
                $"下各路起点/宽度等于定义值 p={refStartX:0.##}px 宽={refShownWidth:0.##}px，且区域不出界");
        }
        finally
        {
            ResetCompareMagnify();
            UpdateLayout();
            await System.Threading.Tasks.Task.Delay(150);
            ApplyCompareCrop();
            _settings.CompareAlign = savedAlign; // 自测不改用户配置
        }
    }

    /// <summary>「轮换各格画面」（格→路映射的循环入口）。
    ///
    /// <para><b>判据</b>：① 轮换一次后<b>第 1 路必须站到第 0 路原来那一格</b>（格数少于路数时，
    /// 掉出格表的第 0 路还必须转为不可见）—— 不能用"第 0 路的 Bounds 变了"当代理，
    /// 见下面 ① 处的说明；</para>
    /// ② 连续轮换 <b>路数</b> 次必须回到起点 —— 这是"路号在 [0, 路数) 上循环"的直接推论，
    /// 若实现写成"按格左移"（只在已显示的路之间转圈），格数 &lt; 路数时周期会变成"格数"
    /// 而不是"路数"，第 ③ 条随之判红；③ 轮换后每格仍显示不同的路（不得出现一路占两格）。</para>
    ///
    /// <para><b>为什么周期必须是"路数"而不是"格数"</b>：4 路用 AB 时，只有轮换到路号 2 / 3
    /// 才能看到第 3、4 路 —— 周期若是格数（2），用户永远看不到它们，"选源"就名不副实。</para></summary>
    private async System.Threading.Tasks.Task AssertRotateCompareCellsAsync(int routes)
    {
        if (routes < 2) throw new InvalidOperationException($"轮换需要 ≥2 路，当前 {routes} 路");

        EnterCompareMode();
        await System.Threading.Tasks.Task.Delay(150);
        OnPartitionChanged(_partition.WithHandleDragged(0, 0.35, 0.6));
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
        ApplyCompareCrop();

        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var s0 = Grid.GetSurface(0) ?? throw new InvalidOperationException("轮换：第 0 路缺失");
        var s1 = Grid.GetSurface(1) ?? throw new InvalidOperationException("轮换：第 1 路缺失");
        // 观测量 = 各路**实际露出的那块**（拼合下 = 窗口原点 + 回读区域）。Bounds 在此模式下
        // 恒等于整对比区，拿它判"谁搬到哪一格"完全没有鉴别力。
        var start0 = RouteVisibleRectPx(0, scaling, "轮换前");
        var start1 = RouteVisibleRectPx(1, scaling, "轮换前");

        OnRotateCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);   // 让轮换排的那一拍裁剪落地
        UpdateLayout();

        // ① 轮换生效的正确观测物：**第 1 路搬进了第 0 路原来的那一块**。
        // ⚠ 观测量必须是区域而不是 Bounds（拼合下后者恒定）；且被撤下的那一路会被 ClearCompareCrop
        //   清掉区域 ⇒ RouteVisibleRectPx 回落到整窗，与任何一格的期望都不相符，
        //   所以"掉出格表"这一支仍按可见性单独判（见下面的 routes > cellCount 分支）。
        var cellCount = _partition.CellCount;
        var after1 = RouteVisibleRectPx(1, scaling, "轮换后");
        var after0 = RouteVisibleRectPx(0, scaling, "轮换后");
        if (!BlockClose(after1, start0))
        {
            FailReadings($"轮换后第 1 路没有占据第 0 格（露出块={after1} 期望={start0}）：轮换未生效");
        }
        if (routes > cellCount)
        {
            // 掉出格表的那一路必须不可见（这就是"被撤下"的可观测后果）
            if (s0.IsVisible)
                FailReadings($"轮换后第 0 路仍在显示（格数={cellCount} < 路数={routes}，它不该有格）");
        }
        else if (BlockClose(after0, start0))
        {
            // 路数 == 格数时每一路都有格可站 ⇒ 位置必须真的变
            FailReadings("轮换后第 0 路位置未变（露出块没动）：轮换未生效（空转）");
        }

        void FailReadings(string why)
        {
            // 只记录读数、不改判决：OnRotateCompareCells 有四个提前 return 的闸门
            //（_compareActive / 叠加 / 格数<2 / 路数<2），红必须说清卡在哪个。
            Log($"[读数] 轮换判据：{why} ｜ Grid.Count={Grid.Count} _sync.Count={_sync.Count} " +
                $"_compareActive={_compareActive} 叠加={CompareOverlayActive} 形状={_partition.Cols}x{_partition.Rows} " +
                $"格数={cellCount} 路数={routes} 拼合={Grid.MosaicWindows} " +
                $"_compareRoute=[{(_compareRoute is null ? "null" : string.Join(",", _compareRoute))}] " +
                $"Grid.CellRoute=[{(Grid.CellRoute is null ? "null" : string.Join(",", Grid.CellRoute))}] " +
                $"第0路 前={start0} 后={after0} 可见={s0.IsVisible} ｜ " +
                $"第1路 前={start1} 后={after1} 可见={s1.IsVisible}");
            throw new InvalidOperationException(why);
        }
        // ③ 每一格的占用者必须是"格号 + 1 mod 路数"（映射是合法排列）。
        //    ⚠ 判据必须看"哪一路站到了这一格上"，不能拿两格的矩形互不相同当代理：
        //    格数 < 路数时被撤下的那一路没有区域 ⇒ 回落成整窗，与真正占着那一格的那一路
        //    读不出区别（原判据 2026-09-26 首次执行即判红："两格矩形相同 (1.33,1.33,464.67,748)"）。
        var cellRects = new[] { start0, start1 };
        for (var c = 0; c < cellRects.Length; c++)
        {
            var expectRoute = (c + 1) % routes;
            var occ = Grid.GetSurface(expectRoute)
                      ?? throw new InvalidOperationException($"轮换：第 {expectRoute} 路缺失");
            var occRect = RouteVisibleRectPx(expectRoute, scaling, "轮换逐格核对");
            if (!occ.IsVisible || !BlockClose(occRect, cellRects[c]))
                FailReadings($"轮换后第 {c} 格应由第 {expectRoute} 路占据：" +
                             $"该路露出块={occRect} 期望={cellRects[c]} 可见={occ.IsVisible}" +
                             "（映射不是合法排列，或被撤下的路没让位）");
        }

        // ② 再轮换 (routes-1) 次 ⇒ 共 routes 次，必须回到起点
        for (var i = 1; i < routes; i++)
            OnRotateCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        var back0 = RouteVisibleRectPx(0, scaling, "轮换整周期后");
        if (!BlockClose(back0, start0))
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 0 路未回到起点：露出块={back0} 期望 {start0}" +
                "（周期应为路数；若等于格数，则格数 < 路数时永远轮换不到没显示的路）");
        // "回到起点"必须是一次**真实的往返**而不是陈旧读数：格数 < 路数时第 0 路在周期中途
        // 是不可见的（①已判过），所以这里要它重新可见 —— 否则本条会因区域没跟着重下发而构造恒等。
        if (!s0.IsVisible)
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 0 路仍不可见：中途被撤下后没有真的回到格上");
        var back1 = RouteVisibleRectPx(1, scaling, "轮换整周期后");
        if (!BlockClose(back1, start1))
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 1 路未回到起点：露出块={back1} 期望 {start1}");

        Console.WriteLine($"comparemodetest: 轮换画面 ✓ 周期 = 路数 {routes}（可轮换到全部 {routes} 路）");
    }

    /// <summary>朝向变体（合并后 = 该 lane 数下 <see cref="GridPartition.ShapesFor"/> 的全部候选）。
    ///
    /// <para><b>为什么数据驱动的循环不够</b>：sweep 单元只验"各路 Bounds == 格表"，对每种朝向的
    /// <b>定义性形状</b>没有鉴别力 —— 1×2（上下两格）若被实现成左右两格，格数照样吻合、Bounds
    /// 照样与格表自洽。旧实现靠 <c>ComputeCells</c> 的分支画形状，漏改一支会静默落进
    /// <c>_ =&gt;</c> 兜底（C# 不报 CS8509）；新模型是一套权重公式，同类错误变成了
    /// "轴向给错"和"拖一条线串到另一条"，所以这里逐朝向钉三条：</para>
    /// <list type="number">
    /// <item><description>单行必须<b>通高</b>、单列必须<b>通宽</b>，且每行宽度之和 == 1（无缝）；</description></item>
    /// <item><description>手柄数量 = 内部竖线 + 内部横线，各落在自己那条线的<b>内部</b>边界上且互不重合
    /// —— 否则第二条线"可拖但看不见抓手"，或两条线共用一个抓手；</description></item>
    /// <item><description>拖第 0 条线时，<b>其余线一格都不位移</b>。旧的那份 <c>SplitParams</c>
    /// 只有两个自由度、两条竖线共用一份参数，"拖 A|B 的分隔条却改变了 B|C 的分界"是真实发生过的
    /// 缺陷形态；权重模型下这条必须由判据守住，而不是靠构造显然。</description></item>
    /// </list>
    /// <para>覆盖范围先打印出来：不写的话一句"朝向变体 ✓"会让人以为所有构型都跑过了。</para></summary>
    private async System.Threading.Tasks.Task AssertCompareModeVariantsAsync(int routes)
    {
        // 形状是关于"格 vs lane"的事，故按 lane 数枚候选（用路数会在别的单元留下空占位 lane 时
        // 出现"装不下"的假红，判据本身与素材路数无关）。
        var lanes = Grid.Count;
        var shapes = GridPartition.ShapesFor(lanes);
        Console.WriteLine($"  朝向变体 覆盖范围（{routes} 路 / {lanes} 条 lane）：[" +
                          $"{string.Join(", ", shapes.Select(s => $"{s.Cols}x{s.Rows}"))}]");

        foreach (var (cols, rows) in shapes)
        {
            await EnterGridShapeAsync(cols, rows);
            var label = $"{cols}x{rows}";
            AssertCellsDriveLayout(lanes, $"朝向 {label}");

            var cells = _partition.ComputeCells();
            if (cells.Length != Math.Min(lanes, cols * rows))
                throw new InvalidOperationException(
                    $"{label} 格数 {cells.Length} ≠ 应有 {Math.Min(lanes, cols * rows)}（lane {lanes}）");

            // ① 定义性形状
            if (rows == 1)
                for (var i = 0; i < cells.Length; i++)
                    if (Math.Abs(cells[i].Height - 1.0) > 1e-6)
                        throw new InvalidOperationException(
                            $"{label} 第 {i} 格未通高（{cells[i].Height:0.###}）—— 凭空多出一条横分割");
            if (cols == 1)
                for (var i = 0; i < cells.Length; i++)
                    if (Math.Abs(cells[i].Width - 1.0) > 1e-6)
                        throw new InvalidOperationException(
                            $"{label} 第 {i} 格未通宽（{cells[i].Width:0.###}）—— 上下两格被画成了左右两格");

            if (lanes >= cols * rows)
                for (var r = 0; r < rows; r++)
                {
                    var sumW = 0.0;
                    var right = 0.0;
                    for (var c = 0; c < cols; c++)
                    {
                        var cell = cells[r * cols + c];
                        sumW += cell.Width;
                        right = Math.Max(right, cell.X + cell.Width);
                    }
                    if (Math.Abs(sumW - 1.0) > 1e-6 || Math.Abs(right - 1.0) > 1e-6)
                        throw new InvalidOperationException(
                            $"{label} 第 {r} 行未铺满：列宽之和={sumW:0.#####} 最右端={right:0.#####}");
                }

            // ② 手柄数量、轴向、落点
            var handles = _layoutOverlay?.HandlePositions()
                          ?? throw new InvalidOperationException("覆盖层未创建");
            var expectedHandles = (cols - 1) + (rows - 1);
            if (handles.Length != expectedHandles)
                throw new InvalidOperationException(
                    $"{label} 应有 {expectedHandles} 条可拖分割线，覆盖层给了 {handles.Length} 个手柄");
            for (var i = 0; i < handles.Length; i++)
            {
                var isVertical = _partition.HandleAxisAt(i) == SplitAxis.X;
                if (isVertical != i < cols - 1)
                    throw new InvalidOperationException(
                        $"{label} 第 {i} 个手柄轴向错（前 {cols - 1} 个必须是竖线）");
                if (lanes >= cols * rows)
                {
                    var onEdge = cells.Any(cc => isVertical
                        ? Math.Abs(handles[i].X - (cc.X + cc.Width)) < 1e-6 && cc.X + cc.Width < 1 - 1e-9
                        : Math.Abs(handles[i].Y - (cc.Y + cc.Height)) < 1e-6 && cc.Y + cc.Height < 1 - 1e-9);
                    if (!onEdge)
                        throw new InvalidOperationException(
                            $"{label} 第 {i} 个手柄（{handles[i].X:0.####},{handles[i].Y:0.####}）" +
                            "不在任何一格的内部边界上 —— 线画在一处、抓手在另一处");
                }
            }
            for (var a = 0; a < handles.Length; a++)
                for (var b = a + 1; b < handles.Length; b++)
                    if (Math.Abs(handles[a].X - handles[b].X) < 1e-6
                        && Math.Abs(handles[a].Y - handles[b].Y) < 1e-6)
                        throw new InvalidOperationException($"{label} 的手柄 {a} 与 {b} 重合（无法分别抓住）");

            // ③ 拖第 0 条线不得串到别的线（≥3 列才有"下一条线"可串）
            if (cols >= 3)
            {
                var before = _partition.HandlePositions();
                var dragged = _partition.WithHandleDragged(0, 0.2, 0.5);
                var after = dragged.HandlePositions();
                if (Math.Abs(after[0].X - 0.2) > 1e-6)
                    throw new InvalidOperationException(
                        $"{label}：第 0 条线没跟到 0.2（实际 {after[0].X:0.####}）");
                for (var i = 1; i < before.Length; i++)
                    if (Math.Abs(before[i].X - after[i].X) > 1e-6 || Math.Abs(before[i].Y - after[i].Y) > 1e-6)
                        throw new InvalidOperationException(
                            $"{label}：拖第 0 条线后，第 {i} 条线从 {before[i]} 被带到 {after[i]}（拖动串线）");
            }

            Console.WriteLine($"comparemodetest: 朝向 {label} ✓ {cells.Length} 格、{handles.Length} 条线、" +
                              "通高/通宽与无缝均吻合" + (cols >= 3 ? "、拖动不串线" : ""));
        }
    }

    // ══════════ 叠加模式（ICAT Single Screen，docs/31 阶段 2）验证 ══════════
    //
    // 为什么必须真机验证：叠加的可见性完全由两件 Win32 / 合成器行为决定 ——
    // ① 两路子 HWND 都被排成"铺满整个对比区"（Avalonia 排布）；
    // ② 两路各自按 split 裁出**互补且互不重叠**的半区（SetWindowRgn，物理像素）。
    // 两者两个测试工程都覆盖不到，且任一失效的表现都是"用户看到的东西不对"，静默通过等于没验证。
    //
    // 判据为什么是"互补"而不是 Z 序：两路区域不重叠且并集为整窗 ⇒ 可见结果与容器 Z 序无关
    // （这正是本设计的目的）。于是真正会静默失效的只剩"区域算错"—— 重叠（Z 序重新决定可见性）
    // 或留缝（露出父窗口底色）。期望值一律独立复算（公开几何 + 显式算术），区域用
    // GetWindowRgn/GetRgnBox 回读；生产代码不维护任何区域状态，因此这个断言不是"自证"。

    /// <summary>断言叠加模式：① 两路都铺满；② 两路区域按 split <b>互补且互不重叠</b>（Win32 回读一致）；
    /// ③ 拖动分割线后仍互补；④ 垂直方向；⑤ 退出后两路区域均已清除并回到 AB 左右分栏。</summary>
    /// <returns>本轮核对通过的区域数（供调用方累计，见 RunCompareModeTestAsync 的兜底断言）。</returns>
    private async System.Threading.Tasks.Task<int> AssertCompareOverlayAsync(int routes, string label)
    {
        _step = "叠加-进入";
        if (!EnterCompareOverlay())
            throw new InvalidOperationException($"{label}：{routes} 路下进入叠加模式失败");
        await System.Threading.Tasks.Task.Delay(300); // 覆盖窗 Show + 布局落地
        UpdateLayout();

        if (!CompareOverlayActive)
            throw new InvalidOperationException($"{label}：进入后 CompareOverlayActive 仍为 false");
        if (!_compareActive)
            throw new InvalidOperationException(
                $"{label}：左右拉动必须建立在网格对比态之上（当前 active={_compareActive}）");

        // 用一个确定的揭示位置，再走生产路径下发区域（期望值见下）。
        // 拖动目标由 Core 的 WithHandleDragged 给出 —— 与用户真实拖动同一条换算。
        var split = _wipePartition.WithHandleDragged(0, 0.7, 0.5);
        OnPartitionChanged(split);
        UpdateLayout();
        ApplyCompareCrop();

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var a = Grid.GetSurface(0) ?? throw new InvalidOperationException($"{label}：第 0 路缺失");
        var b = Grid.GetSurface(1) ?? throw new InvalidOperationException($"{label}：第 1 路缺失");
        if (a.Hwnd == nint.Zero || b.Hwnd == nint.Zero)
            throw new InvalidOperationException($"{label}：子 HWND 未创建，叠加无从验证");

        // ① 每一路都铺满整个对比区（期望值由 ExpectedBounds 独立复算，不读 Grid 的内部格表）。
        //    "铺满"是拉动的载体语义：只有窗口是整幅，内核才把整幅渲染进来，各带才拼得回一整幅。
        //    ⚠ 旧实现在这里断言"第 2 路起必须不可见"—— 那正是被用户报为"加路失效"的行为
        //    （带表长度恒 2 ⇒ 多出来的 lane 被隐藏）。N 带语义下**每一条带都承载一路且都可见**，
        //    故本判据改为"逐路都铺满且都可见"，见下方 ①b。
        var full = ExpectedBounds(new CellRect(0, 0, 1, 1), w, h);
        if (!BoundsClose(a.Bounds, full))
            throw new InvalidOperationException($"{label}：A 路未铺满对比区：Bounds={a.Bounds} 期望 {full}");
        if (!BoundsClose(b.Bounds, full))
            throw new InvalidOperationException($"{label}：B 路未铺满对比区：Bounds={b.Bounds} 期望 {full}");

        // ①b N 带语义：参与对比的每一路都必须可见且铺满 —— 这是"加路不再失效"的直接判据。
        //     旧判据恰好相反（要求 2 路以上不可见），所以它长期把本缺陷判绿。
        for (var i = 2; i < routes; i++)
        {
            var s = Grid.GetSurface(i)
                ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失（N 带下每路都该有带）");
            if (!s.IsVisible)
                throw new InvalidOperationException(
                    $"{label}：左右拉动下第 {i} 路不可见 —— N 带必须逐路承载一路（这正是「加路失效」的旧行为）");
            if (!BoundsClose(s.Bounds, full))
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路未铺满对比区：Bounds={s.Bounds} 期望 {full}");
        }
        if (WipeBandCount != routes)
            throw new InvalidOperationException(
                $"{label}：拉动带数 {WipeBandCount} != 参与路数 {routes}（带表必须跟着路数走）");

        // ②③④ 各带区域互补（期望值独立复算 + Win32 回读核对）。各路窗口都铺满 ⇒ 物理矩形应当一致。
        var winA = CompareCropPlanner.ToPhysicalRect(a.Bounds, scaling);
        var winB = CompareCropPlanner.ToPhysicalRect(b.Bounds, scaling);
        if (Math.Abs(winA.Width - winB.Width) > 1 || Math.Abs(winA.Height - winB.Height) > 1)
            throw new InvalidOperationException(
                $"{label}：两路窗口物理尺寸不一致（A={winA.Width}x{winA.Height} B={winB.Width}x{winB.Height}）");

        // ⚠ N 带下判据从"两路互补"推广为"逐带铺满且两两不重叠"：
        //   · 旧写法假设只有两路且第 1 路的区域一直延伸到窗口末端（[split, span]），
        //     这在 N≥3 时不成立（第 1 路只占第 2 条带，后面还有其它带）。
        //   · 现在的观测物仍是 Win32 回读的区域（GetWindowRgn/GetRgnBox），期望值独立复算。
        var applied = AssertWipeBandsTile(label, "水平 split=0.70",
            split.PrimaryBoundary(), vertical: false);

        // 拖动分割线必须驱动揭示区 —— 这正是"滑块揭示"的链路（OnCompareSplitChanged 即覆盖层回调）
        var dragSplit = _wipePartition.WithHandleDragged(0, 0.25, 0.5);
        OnPartitionChanged(dragSplit);
        UpdateLayout();
        ApplyCompareCrop();
        applied += AssertWipeBandsTile(label, "拖动 split=0.25",
            dragSplit.PrimaryBoundary(), vertical: false);

        // 垂直揭示：同一份逻辑只换矩形（内部方向开关；两个方向都读同一条 PrimaryBoundary）
        SetCompareOverlayAxis(OverlayAxis.Vertical);
        UpdateLayout();
        ApplyCompareCrop();
        applied += AssertWipeBandsTile(label, "垂直 split=0.25",
            dragSplit.PrimaryBoundary(), vertical: true);

        SetCompareOverlayAxis(OverlayAxis.Horizontal);
        UpdateLayout();
        ApplyCompareCrop();

        // 画出来的分割线必须落在揭示接缝上。两者**不同源**：线由覆盖层拿自己的 DIP 宽度乘 split 画，
        // 接缝是子 HWND 物理矩形内的 px 分界（还要过 +1 内缩与取整）。差在 1px 级是几何必然，
        // 但"线在这儿、画面接缝在那儿"两道是分得出来的 —— 用 ≤1.5 DIP 把这条界钉住，
        // 真漂了就有读数，而不是留一条"可能差 1px"的口头账。
        //
        // ⚠ N 带语义下这条判据的**观测物要改**：旧写法读"B（第 1 路）区域的右边缘"当接缝 ——
        //   那在两路时成立（B 铺 [split,1]，右边缘即窗口右沿），N 带时**不成立**
        //   （第 1 路只占第 2 条带，它的右边缘是第 2 条带的末端，与第 1 条线无关）。
        //   现在改成：接缝 = 第 0 条带（第 0 路）区域的**右边缘**，与覆盖层画出的**第一条线**比对。
        //   这才是"线在这儿、画面接缝在那儿"那句话的原意。
        var seamType = ReadWindowRegion(a.Hwnd, out var seamBox);
        if (seamType is not (RegionTypeSimple or RegionTypeComplex))
            throw new InvalidOperationException(
                $"{label}：取线与接缝的对齐读数时第 0 路没有区域（type={seamType}）");
        // 区域是窗口相对坐标 ⇒ 加上窗口物理原点才是容器坐标
        var seamDip = (winA.X + seamBox.X + seamBox.Width) / scaling;
        var lineDip = _wipePartition.PrimaryBoundary() * (_layoutOverlay?.Bounds.Width
            ?? throw new InvalidOperationException($"{label}：覆盖层不存在，无从取得画线位置"));
        // 第一条线在覆盖层上的位置：N 带下就是 _wipePartition 的第一条内部边界
        if (Math.Abs(seamDip - lineDip) > 1.5)
            throw new InvalidOperationException(
                $"{label}：画出的分割线（{lineDip:F1} DIP）不在揭示接缝（{seamDip:F1} DIP）上，" +
                $"差 {Math.Abs(seamDip - lineDip):F1} DIP");
        Console.WriteLine($"{label}: 线与接缝对齐 ✓ 线={lineDip:F1} 接缝={seamDip:F1} DIP" +
                          $"（差 {Math.Abs(seamDip - lineDip):F2} DIP）");

        // ④ 退出叠加：必须**立刻**清除区域（残留 = 窗口被永久裁剪，只有重启才恢复），并回到 AB 左右分栏。
        //    这里刻意不 await / 不 UpdateLayout：ExitCompareOverlay 内部同步清了区域，而它排下的那一拍
        //    （恢复 AB 的 ≤1px 外扩裁剪）一旦跑起来，回读就不再是"无区域"，断言会失去意义。
        ExitCompareOverlay();
        if (CompareOverlayActive)
            throw new InvalidOperationException($"{label}：退出后 CompareOverlayActive 仍为 true");
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || s.Hwnd == nint.Zero) continue;
            var leftover = ReadWindowRegion(s.Hwnd, out var lbox);
            if (leftover != RegionTypeError)
                throw new InvalidOperationException(
                    $"{label}：退出叠加后第 {i} 路仍有窗口区域 type={leftover} box={lbox}（窗口会被永久裁剪）");
        }

        await System.Threading.Tasks.Task.Delay(150); // 让退出时排的那一拍落地
        UpdateLayout();
        ApplyCompareCrop();
        var abCells = _partition.ComputeCells();   // 退出拉动后回到网格的那一组格
        for (var i = 0; i < Math.Min(routes, abCells.Length); i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：退出叠加后第 {i} 路缺失");
            if (Grid.MosaicWindows)
            {
                // 拼合下窗口 == 整对比区，"回到网格"的可观测后果是**区域回到各自那一格**
                var win = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
                var expWin = CompareCropPlanner.ToPhysicalRect(new Rect(0, 0, w, h), scaling);
                if (!BlockClose(win, expWin))
                    throw new InvalidOperationException(
                        $"{label}：退出叠加后第 {i} 路窗口 {win} 未回到对比区 {expWin}");
                AssertRouteBlockAt(i, CellOfRoute(i, abCells, label), $"{label} 退出叠加后");
                continue;
            }
            var exp = ExpectedBounds(abCells[i], w, h);
            if (!BoundsClose(s.Bounds, exp))
                throw new InvalidOperationException(
                    $"{label}：退出叠加后第 {i} 路 Bounds {s.Bounds} != AB 左右分栏期望 {exp}");
        }
        Console.WriteLine(
            $"comparemodetest: [{label}] 退出后区域已清除、已回到 AB 左右分栏 ✓（各路 GetWindowRgn 均为 ERROR）");
        return applied;
    }

    /// <summary>布局编辑器（自由拼接）二级窗的<b>模态交互探针</b> + 几何往返判据。
    ///
    /// <para><b>为什么必须有这一步（P6 前置）</b>：本项目对"覆盖/无边框透明窗口上的交互控件"
    /// （<c>LayoutOverlayWindow</c> / <c>FloatingTransportWindow</c> 那一类 <c>WS_EX_NOACTIVATE</c>
    /// 窗口上的按钮与下拉）**从未实测过**能否正常交互 —— <c>docs/30:189</c> 列为 P6 待实测，
    /// 代码注释记着"这正是当初用『轮换』键代替自选源的原因"。
    /// 编辑器做成普通模态窗正是为了绕开它，但"绕开了"这件事本身也必须有读数，
    /// 否则就只是把未验证换了个地方。</para>
    ///
    /// <para><b>探针测什么</b>：程序化走<b>与用户同一条路径</b>（<c>SplitForTest</c> /
    /// <c>MergeForTest</c> / <c>SetRouteForTest</c> 都转调私有的生产处理器，不复制判断），
    /// 断言：① 窗口能建立并真的 <c>Show</c>（平台层没拒绝）；② 按钮可用态随选择变化；
    /// ③ 切分/合并/设路真的改变了几何；④ 确定后回传的布局与窗口内一致（往返不丢信息）；
    /// ⑤ 取消不改变宿主布局。</para>
    ///
    /// <para><b>为什么不驱动真实的鼠标事件</b>：<c>--selftest</c> 跑在无头意图的自动化里，
    /// 合成指针事件需要窗口有真实焦点，而本项目的自动化档刻意不抢焦点。
    /// 因此探针覆盖的是"控件存在 + 生产处理器可被驱动 + 几何正确"，
    /// <b>不</b>声称覆盖"真人用鼠标点中按钮"——那一条仍需人工目视（会记进 SKIP 说明）。</para></summary>
    private async System.Threading.Tasks.Task AssertLayoutEditorAsync(int routes)
    {
        var label = "布局编辑器";

        // 前置：必须在拉动态（编辑器是拉动的一种呈现形态）
        if (!_compareOverlayActive && !EnterCompareOverlay())
        {
            RecordSelfTestSkip($"{label}：进不了左右拉动（素材 {routes} 路），编辑器无从验证");
            return;
        }
        await System.Threading.Tasks.Task.Delay(200);

        var start = MosaicLayout.FromGridPartition(_wipePartition);
        var win = new Views.MosaicLayoutEditorWindow(start, Math.Max(1, Grid.Count));
        try
        {
            // ① 窗口能建立并显示（平台层没拒绝创建）
            win.Show(this);
            await System.Threading.Tasks.Task.Delay(200);
            if (!win.IsVisible)
                throw new InvalidOperationException($"{label}：窗口 Show 后 IsVisible=false（平台层拒绝了显示）");

            // ② 初始布局 == 传入的起始布局（往返的第一个方向）
            if (!win.CurrentLayout.SameAs(start))
                throw new InvalidOperationException(
                    $"{label}：编辑器初始布局与传入的不一致（块数 {win.CurrentLayout.Tiles.Length} vs {start.Tiles.Length}）");

            // ③ 未选块时不该给操作（避免"点了没反应"）；选中后应可用
            win.SelectTileForTest(-1);
            if (win.SplitEnabledForTest)
                throw new InvalidOperationException($"{label}：没有选中块时切分按钮仍可用（用户点了会没反应）");
            win.SelectTileForTest(0);
            if (!win.SplitEnabledForTest)
                throw new InvalidOperationException($"{label}：选中块后切分按钮仍不可用");

            // ④ 竖切：块数 +1，且新块拿到下一路（"切开后两块显示不同路"）
            var before = win.CurrentLayout.Tiles.Length;
            win.SplitForTest(SplitAxis.X);
            var afterSplit = win.CurrentLayout;
            if (afterSplit.Tiles.Length != before + 1)
                throw new InvalidOperationException(
                    $"{label}：竖切后块数 {before}→{afterSplit.Tiles.Length}（期望 +1）");
            if (afterSplit.Tiles[0].Route == afterSplit.Tiles[1].Route)
                throw new InvalidOperationException(
                    $"{label}：切开后两块显示同一路（等于「切了没反应」）");

            // ⑤ 几何不变式必须仍然成立（编辑器改布局的每一步都不能破坏它）
            AssertMosaicInvariants(afterSplit, $"{label} 竖切后");

            // ⑥ 横切：再 +1，且得到含横竖混排的形态（自由拼接的核心能力）
            win.SplitForTest(SplitAxis.Y);
            var afterSplit2 = win.CurrentLayout;
            if (afterSplit2.Tiles.Length != before + 2)
                throw new InvalidOperationException(
                    $"{label}：横切后块数 {before + 1}→{afterSplit2.Tiles.Length}（期望 +1）");
            AssertMosaicInvariants(afterSplit2, $"{label} 横切后");
            if (!afterSplit2.LogicalLines().Any(l => l.Axis == SplitAxis.X) ||
                !afterSplit2.LogicalLines().Any(l => l.Axis == SplitAxis.Y))
                throw new InvalidOperationException(
                    $"{label}：横竖各切一次后应同时存在竖线与横线（横竖自由组合的核心能力）");

            // ⑦ 设路：把第 0 块设为第 2 路（若路数够）
            if (Grid.Count >= 2)
            {
                win.SelectTileForTest(0);
                win.SetRouteForTest(1);
                if (win.CurrentLayout.Tiles[0].Route != 1)
                    throw new InvalidOperationException(
                        $"{label}：设路未生效（第 0 块 route={win.CurrentLayout.Tiles[0].Route}，期望 1）");
            }

            // ⑧ 合并：把第 1 块并回第 0 块（先让它们同路）
            win.SelectTileForTest(0);
            win.SetRouteForTest(0);
            win.SelectTileForTest(1);
            win.SetRouteForTest(0);
            var beforeMerge = win.CurrentLayout.Tiles.Length;
            win.SelectTileForTest(0);
            win.MergeForTest();
            if (win.CurrentLayout.Tiles.Length >= beforeMerge)
            {
                // 不合并是允许的（可能拼起来不是矩形）——但必须给出可见原因，不能静默
                if (string.IsNullOrWhiteSpace(win.StatusTextForTest))
                    throw new InvalidOperationException($"{label}：合并未生效且状态行没有任何说明（静默）");
            }
            else
            {
                AssertMosaicInvariants(win.CurrentLayout, $"{label} 合并后");
            }

            // ⑨ 确定 ⇒ 回传布局与窗口内一致（往返不丢信息）
            var expected = win.CurrentLayout;
            win.AcceptForTest();
            await System.Threading.Tasks.Task.Delay(150);
            if (!win.Accepted || win.Result is null)
                throw new InvalidOperationException($"{label}：确定后 Accepted/Result 未置位");
            if (!win.Result.SameAs(expected))
                throw new InvalidOperationException($"{label}：回传布局与窗口内不一致（往返丢信息）");

            // ⑩ 应用回宿主：宿主布局 == 编辑器结果，且区域真的重下发
            SetWipeMosaicForTest(win.Result);
            await System.Threading.Tasks.Task.Delay(150);
            UpdateLayout();
            ApplyCompareCrop();
            if (!WipeMosaicActive || WipeMosaic is null)
                throw new InvalidOperationException($"{label}：应用后宿主未进入自由拼接形态");
            if (!WipeMosaic.SameAs(win.Result))
                throw new InvalidOperationException($"{label}：宿主布局与编辑器结果不一致");
            AssertMosaicInvariants(WipeMosaic, $"{label} 应用到宿主后");

            // ⑪ 每一路都必须拿到区域（否则那一路画面不被裁 ⇒ 盖住别人）
            var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
            for (var r = 0; r < Grid.Count; r++)
            {
                var s = Grid.GetSurface(r);
                if (s is null || s.Hwnd == nint.Zero) continue;
                var participates = WipeMosaic.TilesOfRoute(r).Length > 0;
                var hasRegion = TryCompareVisibleBlocksPx(r, out var blocks) && blocks.Length > 0;
                if (participates && !hasRegion)
                    throw new InvalidOperationException(
                        $"{label}：第 {r} 路参与了拼接却没有下发区域（它不会被裁 ⇒ 会盖住其它路）");
                if (!participates && hasRegion)
                    throw new InvalidOperationException(
                        $"{label}：第 {r} 路没参与拼接却仍有区域（会露出一块不属于它的画面）");
            }

            Log($"{label} ✓ 模态窗可建立、按钮可用态随选择变化、切分/设路/合并走生产处理器、" +
                $"确定回传往返一致、应用后 {WipeMosaic.Tiles.Length} 块 / {WipeMosaic.RouteCount} 路均有区域");
            Console.WriteLine($"{label} ✓ {WipeMosaic.Tiles.Length} 块 / {WipeMosaic.RouteCount} 路；" +
                              "⚠ 真实鼠标点击按钮仍需人工目视（自动化档不合成指针事件、不抢焦点）");

            // ⑬ 自由拼接的**线段拖动**必须真的能改块表（此前 DragWipeLine 写了但没接线，
            //    表现为"线画出来了、能命中、却拖不动"—— 由审查轮查出）
            await AssertFreeformLineDragAsync();

            // ⑭ 「选择路」对话框（同一 P6 风险面，同样必须量出读数）
            // ⚠ 先撤掉自由拼接再测选择路：两个模态窗连着开、且中间让 6 路窗口同时铺满呈现，
            //    会把这个进程的**并发交换链数 N** 一直顶在高位 —— 而 docs/43 已定案
            //    "崩溃率 ∝ N"（RTSS 钩子竞态）。判据没必要为了省一步还原而抬高暴露面。
            SetWipeMosaicForTest(null);
            await System.Threading.Tasks.Task.Delay(200);
            UpdateLayout();
            ApplyCompareCrop();
            await AssertRoutePickerAsync(routes);
        }
        finally
        {
            try { win.Close(); } catch { /* 窗口已关或平台拒绝：不影响判据结论 */ }
            // 还原：退出自由拼接回到 N 带，避免影响后续单元
            SetWipeMosaicForTest(null);
            await System.Threading.Tasks.Task.Delay(100);
        }
    }

    /// <summary>自由拼接的<b>线段拖动</b>判据：拖动必须真的改变块表，且三条不变式仍成立。
    ///
    /// <para><b>为什么要单独一条</b>：审查轮发现 <c>DragWipeLine</c> / <c>HitWipeLineAt</c>
    /// 写了却**没有任何调用者** —— 自由拼接的线画出来了、能命中、却**拖不动**。
    /// 更关键的是：当时 <c>MosaicLayout.DragLine</c> 有 Core 单测覆盖，
    /// 于是"Core 逻辑正确"掩盖了"UI 层根本没接线"。本判据打在<b>生产入口</b>上
    /// （<c>OnFreeformLineDragged</c>，与真实拖动同一条路径），专门堵这个缺口。</para>
    ///
    /// <para><b>同时验保守取舍的可见后果</b>：<c>DragLine</c> 对"不干净的层"会拒绝移动
    /// （返回原布局）。若拖动被拒，判据要求**块表不变**（不能出现"拖动改了别处"的半吊子状态）。</para></summary>
    private async System.Threading.Tasks.Task AssertFreeformLineDragAsync()
    {
        const string label = "自由拼接拖动";

        // 造一个规整的拼接（两列），保证至少有一条"干净层"的线可拖
        var start = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        SetWipeMosaicForTest(start);
        await System.Threading.Tasks.Task.Delay(250);
        UpdateLayout();
        ApplyCompareCrop();

        if (!WipeMosaicActive || WipeMosaic is null)
            throw new InvalidOperationException($"{label}：未能进入自由拼接形态");

        var linesBefore = _layoutOverlay?.FreeformLineCount ?? 0;
        if (linesBefore == 0)
            throw new InvalidOperationException(
                $"{label}：覆盖层上报的自由拼接线段数为 0（线没画出来，也就无从拖）");

        var before = WipeMosaic!;
        var lineIndex = 0;
        var oldPos = before.LogicalLines()[lineIndex].Position;

        // 走生产入口（与真实拖动同一条）：→ DragWipeLine → 回灌线段表
        OnFreeformLineDragged(lineIndex, 0.75);
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();
        ApplyCompareCrop();

        var after = WipeMosaic!;
        if (after.SameAs(before))
            throw new InvalidOperationException(
                $"{label}：拖动第 {lineIndex} 条线（{oldPos:F3}→0.75）后块表**没有变化** —— " +
                "这条线拖不动（DragWipeLine 未被接线，或该层被判为不干净）");

        var newPos = after.LogicalLines()[lineIndex].Position;
        if (Math.Abs(newPos - 0.75) > 0.01)
            throw new InvalidOperationException(
                $"{label}：拖动后线位置 {newPos:F3} 与目标 0.75 相差过大");

        // 拖动不得破坏三条不变式
        AssertMosaicInvariants(after, $"{label} 拖动后");

        // 区域必须重下发（否则块表变了、画面没变）
        var anyRegion = false;
        for (var r = 0; r < Grid.Count && !anyRegion; r++)
            anyRegion = TryCompareVisibleBlocksPx(r, out var blks) && blks.Length > 0;
        if (!anyRegion)
            throw new InvalidOperationException($"{label}：拖动后没有任何路下发区域（画面不会跟着变）");

        Log($"{label} ✓ 线段可拖：第 {lineIndex} 条 {oldPos:F3}→{newPos:F3}，块表已变、不变式成立、区域已重下发");
        Console.WriteLine($"{label} ✓ 线 {lineIndex}: {oldPos:F3} → {newPos:F3}；" +
                          $"覆盖层线段数 {linesBefore} → {_layoutOverlay?.FreeformLineCount ?? 0}");

        // 收尾：回到 N 带，避免污染后续单元
        SetWipeMosaicForTest(null);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
        ApplyCompareCrop();
    }

    /// <summary>「选择路」对话框探针：窗口可建立、行数随带数、重复路被如实提示、确认回传映射。
    ///
    /// <para><b>为什么单列</b>：它与布局编辑器同一块未验证风险面（模态窗上的控件交互），
    /// 且它承载一个用户明确要求的能力（"手动指定多路"）。判据要证明的是
    /// **映射真的能生效**、以及**不相邻重复会被如实说明**（否则用户配好后看到只显示一处会以为坏了）。</para></summary>
    private async System.Threading.Tasks.Task AssertRoutePickerAsync(int routes)
    {
        const string label = "选择路";

        var bandCount = Math.Max(1, WipeBandCount);
        var dlg = new Views.RoutePickerWindow(null, bandCount, Math.Max(1, Grid.Count));
        try
        {
            dlg.Show(this);
            await System.Threading.Tasks.Task.Delay(200);
            if (!dlg.IsVisible)
                throw new InvalidOperationException($"{label}：窗口 Show 后 IsVisible=false（平台层拒绝了显示）");

            // ① 行数 == 带数
            if (dlg.RowCountForTest != bandCount)
                throw new InvalidOperationException(
                    $"{label}：行数 {dlg.RowCountForTest} != 带数 {bandCount}");

            // ② 初始为恒等 ⇒ 第 i 条带显示第 i 路
            var identity = dlg.CurrentRoutes();
            for (var i = 0; i < identity.Length; i++)
                if (identity[i] != Math.Min(i, Math.Max(0, Grid.Count - 1)))
                    throw new InvalidOperationException(
                        $"{label}：初始映射不是恒等（第 {i} 行 = 路 {identity[i]}）");

            // ③ 指定"两条带显示同一路"，确认回传必须**保留重复**（不得被静默去重）
            if (bandCount >= 2 && Grid.Count >= 1)
            {
                dlg.SetRowForTest(0, 0);
                dlg.SetRowForTest(1, 0);
                var r = dlg.CurrentRoutes();
                if (r[0] != 0 || r[1] != 0)
                    throw new InvalidOperationException(
                        $"{label}：重复路被改写（得到 [{string.Join(",", r)}]，期望前两项都是 0）");
            }

            // ④ 不相邻重复必须被**说明**（不能静默）
            if (bandCount >= 3)
            {
                dlg.SetRowForTest(0, 0);
                dlg.SetRowForTest(1, Math.Min(1, Grid.Count - 1));
                dlg.SetRowForTest(2, 0);
                var status = dlg.StatusTextForTest;
                if (string.IsNullOrWhiteSpace(status) || !status.Contains("不相邻"))
                    throw new InvalidOperationException(
                        $"{label}：第 1、3 条带同为一路（不相邻）时未给出说明（状态行='{status}'）");
            }

            // ⑤ 加/减带的行数边界（不静默：到边界时状态行要有说明）
            var before = dlg.RowCountForTest;
            dlg.AddRowForTest();
            if (dlg.RowCountForTest != Math.Min(before + 1, Core.Display.WipeBands.MaxBands))
                throw new InvalidOperationException(
                    $"{label}：加带后行数 {before}→{dlg.RowCountForTest}（期望 +1 或到上限 {Core.Display.WipeBands.MaxBands}）");

            // ⑥ 确认 ⇒ 结果非空且与窗口一致
            var expected = dlg.CurrentRoutes();
            dlg.AcceptForTest();
            await System.Threading.Tasks.Task.Delay(150);
            if (!dlg.Accepted || dlg.Result is null)
                throw new InvalidOperationException($"{label}：确认后 Accepted/Result 未置位");
            if (dlg.Result.Length != expected.Length)
                throw new InvalidOperationException(
                    $"{label}：回传映射长度 {dlg.Result.Length} != 窗口内 {expected.Length}");
            for (var i = 0; i < expected.Length; i++)
                if (dlg.Result[i] != expected[i])
                    throw new InvalidOperationException(
                        $"{label}：回传映射第 {i} 项 {dlg.Result[i]} != 窗口内 {expected[i]}");

            // ⑦ 应用到宿主：带数与映射真的生效，且区域跟着重下发
            SetWipeRoutesForTest(dlg.Result);
            await System.Threading.Tasks.Task.Delay(200);
            UpdateLayout();
            ApplyCompareCrop();
            var applied = WipeRoutes;
            if (applied is null || applied.Length != dlg.Result.Length)
                throw new InvalidOperationException(
                    $"{label}：应用到宿主后带数 {applied?.Length.ToString() ?? "null"} != {dlg.Result.Length}");
            for (var i = 0; i < applied.Length; i++)
                if (applied[i] != dlg.Result[i])
                    throw new InvalidOperationException(
                        $"{label}：宿主第 {i} 条带的路 {applied[i]} != 选定 {dlg.Result[i]}");

            // ⑧ 重复路下"同一路占两条带"必须两处都有区域（相邻时并集可表达）
            var dupRoute = -1;
            for (var i = 1; i < applied.Length; i++)
                if (applied[i] == applied[i - 1]) { dupRoute = applied[i]; break; }
            if (dupRoute >= 0 && TryCompareVisibleBlocksPx(dupRoute, out var dupBlocks))
            {
                if (dupBlocks.Length < 2)
                    throw new InvalidOperationException(
                        $"{label}：第 {dupRoute} 路占两条相邻带，却只下发 {dupBlocks.Length} 块区域");
            }

            // ⑨ 不相邻重复 ⇒ 必须真的建出**镜像槽**（否则那一路只能显示一处）
            await AssertWipeMirrorAsync(applied);

            Log($"{label} ✓ 窗口可建立、行数随带数、重复路不被去重、不相邻重复有说明、" +
                $"确认回传一致、应用到宿主 {applied.Length} 条带生效");
            Console.WriteLine($"{label} ✓ {applied.Length} 条带 / 映射 [{string.Join(",", applied)}]");
        }
        finally
        {
            try { dlg.Close(); } catch { /* 已关或平台拒绝 */ }
            // 还原恒等映射，避免影响后续单元
            SetWipeRoutesForTest(null);
            await System.Threading.Tasks.Task.Delay(100);
        }
    }

    /// <summary>镜像判据：不相邻重复必须真的建出镜像槽，且镜像与源路<b>同位</b>。
    ///
    /// <para><b>为什么这是本功能最关键的判据</b>：镜像的整个价值是"同一路出现在两条带上
    /// 仍显示<b>同一画面</b>"。若镜像漂移，用户看到的就变成"两条带显示同一路的不同时刻" ——
    /// 而那正是他要用来做细节对照的东西，错一帧这功能就是假的。</para>
    ///
    /// <para><b>观测物</b>：① 槽数（真的多了一个会话）；② 镜像槽的 <c>MirrorOf</c> 指向源路；
    /// ③ 两条带的承载槽<b>不同</b>（若相同就是"两条带抢一个 HWND"，镜像拿不到区域）；
    /// ④ 镜像与源路的<b>媒体位置一致</b>（核心）；⑤ 移除该映射后镜像被释放（不留孤儿）。</para></summary>
    private async System.Threading.Tasks.Task AssertWipeMirrorAsync(int[] routes)
    {
        const string label = "路镜像";

        // 找一组"不相邻重复"：同一路出现在被别的路隔开的两条带上
        var nonAdjacentRoute = -1;
        for (var i = 0; i < routes.Length; i++)
        {
            for (var j = i + 2; j < routes.Length; j++)
            {
                if (routes[i] != routes[j]) continue;
                var separated = false;
                for (var k = i + 1; k < j; k++) if (routes[k] != routes[i]) { separated = true; break; }
                if (separated) { nonAdjacentRoute = routes[i]; break; }
            }
            if (nonAdjacentRoute >= 0) break;
        }
        if (nonAdjacentRoute < 0)
        {
            RecordSelfTestSkip($"{label}：本轮带表 [{string.Join(",", routes)}] 没有不相邻重复，镜像无从验证");
            return;
        }

        // 触发镜像收敛（走生产入口）
        SetWipeRoutesForTest(routes);
        await System.Threading.Tasks.Task.Delay(600);   // 建会话 + 打开是异步的
        UpdateLayout();
        ApplyCompareCrop();

        var mirrors = _sync.Mirrors();
        var forRoute = System.Linq.Enumerable.Where(mirrors, m => m.Source == nonAdjacentRoute).ToArray();
        if (forRoute.Length == 0)
        {
            // ⚠ 建不出来**不是静默通过**：那一路只会显示一处。
            //   两件事都要断言：
            //   ① 状态栏必须说出来（否则用户看到空带却没有任何解释 —— 与用户最初报的
            //      "点了没反应"是同一类静默失效）；
            //   ② 而且这条说明必须**熬过随后那次状态栏重算**（ApplyRouteActivity 会调
            //      NotifyCompareOverlay 整条重写）。本判据第一版只判"有说明"，而
            //      当时 TryCreateMirror 直接写 StatusInfo —— 那次写入随后就被冲掉了。
            await System.Threading.Tasks.Task.Delay(400);   // 让延后那一拍（Post 的 ApplyRouteActivity）落地
            var statusNow = StatusInfo.Text ?? string.Empty;
            if (!statusNow.Contains("镜像") && !statusNow.Contains("留空") && !statusNow.Contains("mirror"))
                throw new InvalidOperationException(
                    $"{label}：第 {nonAdjacentRoute + 1} 路需镜像但未建成，**且状态栏没有任何说明**" +
                    $"（当前状态栏='{statusNow}'）—— 用户会看到一处空带却不知为何");
            Console.WriteLine($"{label} ⚠ 镜像未建成，但状态栏已如实说明（熬过了重算）：" +
                              $"'{statusNow.Substring(0, Math.Min(60, statusNow.Length))}…'");
            return;
        }

        // ③ 两条带的承载槽必须不同
        var bandA = System.Linq.Enumerable.First(System.Linq.Enumerable.Range(0, routes.Length)
            .Where(i => routes[i] == nonAdjacentRoute));
        var bandB = System.Linq.Enumerable.Last(System.Linq.Enumerable.Range(0, routes.Length)
            .Where(i => routes[i] == nonAdjacentRoute));
        var slotA = SlotOfBand(bandA);
        var slotB = SlotOfBand(bandB);
        if (slotA == slotB)
            throw new InvalidOperationException(
                $"{label}：第 {bandA + 1} 与第 {bandB + 1} 条带都解析到槽 {slotA}" +
                "（同一 HWND 只能在一个位置 ⇒ 必然有一处显示不出来）");

        // ④ 核心：镜像与源路媒体位置一致
        var srcSnap = _sync.Slots[nonAdjacentRoute].Session.ReadSnapshot();
        var mirSnap = _sync.Slots[slotB].Session.ReadSnapshot();
        if (srcSnap is null || mirSnap is null)
            throw new InvalidOperationException($"{label}：镜像或源路读不到快照");
        var diff = Math.Abs(srcSnap.Position100ns - mirSnap.Position100ns);
        if (diff > 30_0000)   // 30ms
            throw new InvalidOperationException(
                $"{label}：镜像与源路不同位（Δ={diff / 10000.0:F1}ms，源 {srcSnap.Position100ns} / 镜像 {mirSnap.Position100ns}）" +
                " —— 两条带会显示同一路的不同时刻，功能意义丧失");

        // ⑤ 两处都必须真的下发区域（否则有一处是空白）
        if (!TryCompareVisibleBlocksPx(slotA, out var blocksA) || blocksA.Length == 0)
            throw new InvalidOperationException($"{label}：第 {bandA + 1} 条带（槽 {slotA}）没有区域");
        if (!TryCompareVisibleBlocksPx(slotB, out var blocksB) || blocksB.Length == 0)
            throw new InvalidOperationException($"{label}：第 {bandB + 1} 条带（槽 {slotB}）没有区域");

        Log($"{label} ✓ 不相邻重复建出镜像槽 {slotB}（源 {nonAdjacentRoute}）、两条带槽不同、" +
            $"镜像与源路同位（Δ={diff / 10000.0:F2}ms）、两处均有区域");
        Console.WriteLine($"{label} ✓ 源路 {nonAdjacentRoute} / 镜像槽 {slotB}；" +
                          $"第 {bandA + 1}、{bandB + 1} 条带分别由槽 {slotA}、{slotB} 承载；Δ={diff / 10000.0:F2}ms");

        // ⑥ 撤回映射 ⇒ 镜像必须被释放（不留孤儿：那是永久泄漏 + 白烧解码）
        SetWipeRoutesForTest(MakeIdentityWipeRoutes(routes.Length));
        await System.Threading.Tasks.Task.Delay(400);
        var after = _sync.Mirrors();
        if (after.Length >= mirrors.Length)
            throw new InvalidOperationException(
                $"{label}：撤掉重复映射后镜像未被释放（{mirrors.Length} → {after.Length}）—— 会留下孤儿会话");
        Console.WriteLine($"{label} ✓ 撤回映射后镜像已释放（{mirrors.Length} → {after.Length}）");

        // ⑦ 还要把 **lane 数**收回去：镜像占的那条 lane 在镜像释放后不会自动消失，
        //    而后续单元（如「模式-网格对比入口」）判的是"格数 == lane 数"。
        //    留着多出来的一条（或几条）空 lane 会让它判红 —— 实测踩到过：
        //    `网格对比入口的格数 5 ≠ lane 数 6`。
        //    ⇒ 判据必须自己把夹具还原干净，不能指望"下一个单元会自己收敛"。
        //
        //    ⚠ 收回的目标是**素材路数**（_sync.Count 去掉镜像），不是 routes.Length：
        //      传进来的 routes 是"带表"（可能比路数长，例如选路后的 [0,1,0,3,4,4] 有 6 带
        //      而素材只有 5 路）⇒ 按 routes.Length 收会**收不干净**。
        //      本行第一版正是这么写的，于是打印出"已收回 6"却仍留着 6 条 lane。
        var targetLanes = _sync.Count - _sync.Mirrors().Length;
        if (targetLanes < 1) targetLanes = 1;
        if (Grid.Count != targetLanes)
        {
            Grid.SetCount(targetLanes, _realMode);
            UpdateLayout();
            ApplyCompareCrop();
            await System.Threading.Tasks.Task.Delay(150);
        }
        if (Grid.Count != targetLanes)
            throw new InvalidOperationException(
                $"{label}：探针收尾未把 lane 数收回 {targetLanes}（实际 {Grid.Count}）—— 会污染后续单元");
        Console.WriteLine($"{label} ✓ lane 数已收回 {targetLanes}（不污染后续单元）");
    }

    /// <summary>拼接的三条不变式（不重叠 / 铺满 / 每块为正）。Core 单测里也有一份，
    /// 但真机路径上必须再判一次 —— 编辑器与宿主是两条独立的写入路径，
    /// 只靠 Core 单测证明不了"宿主应用后仍然合法"。</summary>
    private static void AssertMosaicInvariants(MosaicLayout m, string what)
    {
        if (m.Tiles.Length == 0)
            throw new InvalidOperationException($"{what}：拼接为空");

        var area = 0.0;
        for (var i = 0; i < m.Tiles.Length; i++)
        {
            var a = m.Tiles[i].Rect;
            if (!(a.Width > 0) || !(a.Height > 0))
                throw new InvalidOperationException($"{what}：第 {i} 块尺寸非正 {a.Width}x{a.Height}");
            area += a.Width * a.Height;
            for (var j = i + 1; j < m.Tiles.Length; j++)
            {
                var b = m.Tiles[j].Rect;
                var overlap = a.X < b.X + b.Width - 1e-9 && b.X < a.X + a.Width - 1e-9 &&
                              a.Y < b.Y + b.Height - 1e-9 && b.Y < a.Y + a.Height - 1e-9;
                if (overlap)
                    throw new InvalidOperationException($"{what}：第 {i} 块 {a} 与第 {j} 块 {b} 重叠");
            }
        }
        if (Math.Abs(area - 1.0) > 1e-6)
            throw new InvalidOperationException($"{what}：面积和 {area:F9} != 1（有缝或越界）");
    }

    /// <summary>断言左右拉动下 <b>N 条带铺满且两两不重叠</b>，并逐条与 Win32 回读的区域核对。
    ///
    /// <para><b>为什么取代了旧的"两路互补"判据</b>：旧判据假设拉动恒两格，于是断言
    /// "B 铺 <c>[0, split]</c>、A 铺 <c>[split, span]</c>"。N 带之后第 1 路只占<b>第 2 条带</b>
    /// （其右侧还有其它带），旧期望值必然对不上。判据的<b>不变式本身没变</b>：
    /// 各带两两不重叠、并集恰好铺满整窗 —— 这正是"可见结果与 Z 序无关"的全部依据。</para>
    ///
    /// <para><b>期望值独立复算</b>：用公开的 <c>WipeBands.PartitionBoundaries</c> + 与生产同一套
    /// 取整/钳制算术重算各带矩形，再与 <c>GetWindowRgn</c>/<c>GetRgnBox</c> 回读的逐条核对。
    /// 生产代码不维护区域状态，故这不是自证。</para>
    ///
    /// <para><b>为什么逐路都读</b>：同一路可能占多条带（用户可指定重复路），
    /// 故一条带一条带地核对，并断言"每条带确实归它该归的那一路"。</para></summary>
    /// <returns>核对通过的带数（供调用方累计）。</returns>
    private int AssertWipeBandsTile(string label, string what, double ignoredSplit, bool vertical)
    {
        var areaW = Grid.Bounds.Width;
        var areaH = Grid.Bounds.Height;
        if (areaW <= 0 || areaH <= 0)
            throw new InvalidOperationException($"{label}[{what}]：对比区尺寸非法 {areaW}x{areaH}");

        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var axis = vertical ? WipeAxis.Vertical : WipeAxis.Horizontal;

        // 独立复算：从**实际分区**取界标（分区是本次判据的输入，不是生产的私有状态），
        // 再按与 WipeBands 同一套口径换算成窗口相对物理像素。
        var boundaries = WipeBands.PartitionBoundaries(_wipePartition, axis);
        var bandCount = boundaries.Length - 1;
        if (bandCount < 1)
            throw new InvalidOperationException($"{label}[{what}]：分区没有带（boundaries={boundaries.Length}）");

        var checkedBands = 0;

        // ★ 绝对期望：带数必须等于**参与对比的路数**（而不是"与分区自洽"）。
        //   为什么必须有这一条：下面逐带核对的期望值全部**从分区本身取出**，
        //   于是"分区只有 2 带"这种退化（正是用户报的「恒两格」旧行为）反而完全自洽、
        //   逐带核对照样全绿 —— 实测确认过：`nobands` 变异（Partition 恒返回 2）
        //   在只有"自洽"判据时**没有被判红**。故这里必须钉一个**不依赖分区**的期望。
        //
        //   ⚠ 判"是否恒等"不能写成 `WipeRoutes is null`：EnterCompareOverlay 会把
        //     `_wipeRoutes ??= MakeIdentityWipeRoutes(...)` 填成**非 null 的恒等表**，
        //     于是那个条件恒不成立（第一版就是这么写的，变异臂照样全绿）。
        //     改成逐项检查"第 i 条带的路号 == i"。
        var routesTable = WipeRoutes;
        var identity = routesTable is not { Length: > 0 };
        if (!identity)
        {
            identity = true;
            for (var i = 0; i < routesTable!.Length; i++)
                if (routesTable[i] != i) { identity = false; break; }
        }
        if (identity)
        {
            var expectBands = Math.Max(1, _sync.Count);
            if (bandCount != expectBands)
                throw new InvalidOperationException(
                    $"{label}[{what}]：恒等映射下带数 {bandCount} != 参与路数 {expectBands}" +
                    " —— 多出来的路会被隐藏（这正是用户报的「加路失效」旧行为）");
        }

        // 逐带核对：每条带应落在"显示它的那一路"的窗口区域里。
        for (var band = 0; band < bandCount; band++)
        {
            var route = WipeRouteOfBand(band);
            var s = Grid.GetSurface(route)
                ?? throw new InvalidOperationException($"{label}[{what}]：第 {band} 条带指向的路 {route} 不存在");

            // 该路实际下发的区域集合（生产路径写下的账）——同一条带可能只是它的其中一块。
            if (!TryCompareVisibleBlocksPx(route, out var blocks) || blocks.Length == 0)
                throw new InvalidOperationException(
                    $"{label}[{what}]：第 {band} 条带的路 {route} 没有任何区域下发（拉动下每路都必须被裁）");

            // ⚠ 区域的坐标语义是**窗口相对 + 物理像素**，所以尺寸基准必须取该路**窗口的物理矩形**，
            //    不能用 Grid.Bounds（那是 DIP，且是容器的而非窗口的）。
            //    本判据第一版就是拿 DIP 当物理用，于是期望 [0,466] 而实际 [2,2 698x1122]，
            //    把正确行为读成了"带与区域不符"。
            var winPx = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
            var span = vertical ? winPx.Height : winPx.Width;
            var ortho = vertical ? winPx.Width : winPx.Height;
            if (span <= 0 || ortho <= 0)
                throw new InvalidOperationException($"{label}[{what}]：第 {band} 条带的窗口物理尺寸非法 {winPx}");

            // 该带应有的**窗口相对**范围：界标 × 窗口在该轴上的物理尺寸
            var b0 = boundaries[band];
            var b1 = boundaries[band + 1];
            if (!double.IsFinite(b0)) b0 = 0;
            if (!double.IsFinite(b1)) b1 = 1;
            var px0 = (int)Math.Round(b0 * span, MidpointRounding.AwayFromZero);
            var px1 = (int)Math.Round(b1 * span, MidpointRounding.AwayFromZero);
            // 与生产同一套取整/钳制口径：整窗宽度小于带数时的钳制会让末段贴到 span
            if (px1 > span) px1 = span;

            // 只要"这一路确实覆盖了该带的起止范围"即算通过（取整差 ≤1px 属正常）。
            var covered = false;
            foreach (var blk in blocks)
            {
                if (!blk.IsValid) continue;
                var s0 = vertical ? blk.Y : blk.X;
                var s1 = s0 + (vertical ? blk.Height : blk.Width);
                var oLen = vertical ? blk.Width : blk.Height;
                if (Math.Abs(s0 - px0) <= 1 && Math.Abs(s1 - px1) <= 1 && Math.Abs(oLen - ortho) <= 1)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                throw new InvalidOperationException(
                    $"{label}[{what}]：第 {band} 条带（路 {route}）在其窗口 {winPx.Width}x{winPx.Height} 内" +
                    $"期望覆盖 [{px0},{px1}]×{ortho}，" +
                    $"实际区域集合 [{string.Join(", ", System.Linq.Enumerable.Select(blocks, x => $"[{x.X},{x.Y} {x.Width}x{x.Height}]"))}]" +
                    " —— 带与区域不符（重叠/留缝/裁错块）");

            checkedBands++;
        }

        // 铺满 + 不重叠：按**实际下发的区域**检查（不是拿界标自己跟自己对账 ——
        // 那样只能证明界标单调，证明不了 Win32 收到的区域真的铺满）。
        var cuts = new List<(int Start, int End)>();
        for (var band = 0; band < bandCount; band++)
        {
            var route = WipeRouteOfBand(band);
            if (!TryCompareVisibleBlocksPx(route, out var blocks) || blocks.Length == 0)
                throw new InvalidOperationException($"{label}[{what}]：第 {band} 条带的路 {route} 无区域，无法核对铺满");
            foreach (var blk in blocks)
            {
                if (!blk.IsValid) continue;
                var s0 = vertical ? blk.Y : blk.X;
                var len = vertical ? blk.Height : blk.Width;
                cuts.Add((s0, s0 + len));
            }
        }
        cuts.Sort((x, y) => x.Start.CompareTo(y.Start));

        var spanPx = vertical
            ? CompareCropPlanner.ToPhysicalRect(Grid.GetSurface(WipeRouteOfBand(0))!.Bounds, scaling).Height
            : CompareCropPlanner.ToPhysicalRect(Grid.GetSurface(WipeRouteOfBand(0))!.Bounds, scaling).Width;
        if (cuts.Count > 0)
        {
            if (cuts[0].Start != 0)
                throw new InvalidOperationException(
                    $"{label}[{what}]：首块不从窗口原点起（{cuts[0].Start}）—— 会露出父窗口底色");
            var lastEnd = cuts[0].End;
            for (var i = 1; i < cuts.Count; i++)
            {
                if (cuts[i].Start > lastEnd)
                    throw new InvalidOperationException(
                        $"{label}[{what}]：块之间留缝（{lastEnd} → {cuts[i].Start}）—— 会露出父窗口底色");
                if (cuts[i].Start < lastEnd)
                    throw new InvalidOperationException(
                        $"{label}[{what}]：块之间重叠（{lastEnd} vs {cuts[i].Start}）—— 重叠会让 Z 序重新决定可见性");
                if (cuts[i].End > lastEnd) lastEnd = cuts[i].End;
            }
            if (lastEnd != spanPx)
                throw new InvalidOperationException(
                    $"{label}[{what}]：块的并集到 {lastEnd} 而非窗口边缘 {spanPx} —— 没有铺满整窗");
        }

        Console.WriteLine(
            $"comparemodetest: [{label}/{what}] {bandCount} 条带铺满且两两不重叠 ✓（逐带核对 {checkedBands} 条）");
        return checkedBands;
    }

    /// <summary>断言叠加模式下两路区域<b>互补且互不重叠</b>：B 铺 <c>[0, splitPx]</c>、
    /// A 铺 <c>[splitPx, span]</c> —— 无缝、无重叠、并集 = 整窗。这正是"可见结果与 Z 序无关"的
    /// 全部依据：重叠会让 Z 序重新决定可见性，留缝会露出父窗口底色，两者都是用户直接看得见的不对。
    ///
    /// <para><b>⚠ 本方法只服务"恒两路"的旧形态</b>（<c>WipeBands.MaxBands</c> 之外的兼容路径与
    /// 历史读数）。N 带（≥3）请用 <see cref="AssertWipeBandsTile"/> —— 那里的不变式相同，
    /// 但期望值按带算，不假设第 1 路延伸到窗口末端。</para></summary>
    /// <returns>本轮核对通过的区域数（2：A 与 B 各一处）。</returns>
    private static int AssertOverlayComplement(string label, string what, nint hwndA, nint hwndB,
        Rect32 winA, Rect32 winB, double split, bool vertical)
    {
        var span = vertical ? winB.Height : winB.Width;   // 分割轴上的边长
        var px = (int)Math.Round(split * span, MidpointRounding.AwayFromZero);
        if (px < 1) px = 1;               // 与 WipeBands 同一套钳制（split=0 ⇒ B 只余 1px）
        if (px > span - 1) px = span - 1; // split=1 ⇒ A 只余 1px（此时 A 绝不能用 Clear）

        var expB = vertical ? new Rect32(0, 0, winB.Width, px) : new Rect32(0, 0, px, winB.Height);
        var expA = vertical
            ? new Rect32(0, px, winA.Width, span - px)
            : new Rect32(px, 0, span - px, winA.Height);

        var typeB = ReadWindowRegion(hwndB, out var boxB);
        var typeA = ReadWindowRegion(hwndA, out var boxA);
        if (typeB is not (RegionTypeSimple or RegionTypeComplex))
            throw new InvalidOperationException(
                $"{label}[{what}]：B 路应被裁出揭示区，回读却 type={typeB}（0=无区域）");
        if (typeA is not (RegionTypeSimple or RegionTypeComplex))
            throw new InvalidOperationException(
                $"{label}[{what}]：A 路应被裁出互补区（叠加下两路都裁，A 整窗可见会盖住 B），回读却 type={typeA}");
        if (boxB != expB)
            throw new InvalidOperationException(
                $"{label}[{what}]：B 路回读区域 {boxB} != 独立复算的期望 {expB}（窗口 {winB}，split={split:F2}）");
        if (boxA != expA)
            throw new InvalidOperationException(
                $"{label}[{what}]：A 路回读区域 {boxA} != 独立复算的期望 {expA}（窗口 {winA}，split={split:F2}）");

        // 互补性显式再判一次（不依赖上面的期望值比对）：分界处严丝合缝 + 并集铺满整窗。
        var seam = vertical ? boxB.Y + boxB.Height == boxA.Y : boxB.X + boxB.Width == boxA.X;
        var cover = vertical
            ? boxB.Y == 0 && boxA.Y + boxA.Height == span
            : boxB.X == 0 && boxA.X + boxA.Width == span;
        var crossAligned = vertical
            ? boxB.X == boxA.X && boxB.Width == boxA.Width
            : boxB.Y == boxA.Y && boxB.Height == boxA.Height;
        if (!seam || !cover || !crossAligned)
            throw new InvalidOperationException(
                $"{label}[{what}]：两路区域不互补（A={boxA} B={boxB}，窗口 {winB.Width}x{winB.Height}）" +
                "—— 重叠或留缝会让 Z 序重新决定可见性 / 露出父窗口底色");

        // 区域必须落在子 HWND 实际尺寸内（坐标或 DPI 换算不一致时会越界）
        var realB = ReadWindowRectPx(hwndB);
        if (boxB.X < 0 || boxB.Y < 0 ||
            boxB.X + boxB.Width > realB.Width + 1 || boxB.Y + boxB.Height > realB.Height + 1)
            throw new InvalidOperationException(
                $"{label}[{what}]：B 路区域 {boxB} 越出子 HWND 实际尺寸 {realB.Width}x{realB.Height}");

        var seamPx = vertical ? boxA.Y : boxA.X;
        Console.WriteLine(
            $"comparemodetest: [{label}] {what} ✓ 窗口 {winB.Width}x{winB.Height}px；" +
            $"B 区 {boxB} + A 区 {boxA}（{(vertical ? "垂直" : "水平")}分界 {seamPx}px = " +
            $"split {split:F2} × {span}px；并集 = 整窗 {winB.Width}x{winB.Height}，无重叠无缝隙）");
        return 2;
    }

    /// <summary>读第 <paramref name="index"/> 路源画面尺寸；读不到返回 (0,0)（换算时按"画面铺满窗口"处理）。</summary>
    private PixelSize ReadSourceSize(int index)
    {
        try
        {
            var slots = _sync.Slots;
            if (index >= slots.Count) return default;
            var media = slots[index].Session.ReadMediaInfo();
            var w = media?.VideoWidth ?? 0;
            var h = media?.VideoHeight ?? 0;
            return w > 0 && h > 0 ? new PixelSize(w, h) : default;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"comparemodetest: 第 {index} 路源尺寸读取失败（按未知处理）：{ex.Message}");
            return default;
        }
    }

    /// <summary>回读窗口区域：<c>GetWindowRgn</c> 把窗口区域拷进我们给的 hRgn（**不转移所有权**，
    /// 故必须自己 DeleteObject），再用 <c>GetRgnBox</c> 取外接矩形。
    /// 返回区域类型：0(ERROR) = 该窗口没有区域。</summary>
    /// <summary>断言：GDI 兜底路线在拼合下**认得**这一路的窗口区域 —— 抓回尺寸必须等于"这一路露出来的那一块"，
    /// 而不是整窗（整窗 == 整个对比区）。
    ///
    /// <para><b>为什么拼合下这条必须单独钉</b>：每路子窗口的矩形恒等于整个对比区，露出哪一块完全由
    /// <c>SetWindowRgn</c> 决定；而 <c>GdiFrameCapture</c> 两条子路径都只按 <c>GetWindowRect</c> 取图。
    /// ⇒ 不认区域时<b>各路返回的是同一张屏幕合成图</b>：导出得到"整幅拼图"而不是该路画面，
    /// 缩略图与逐路抓帧全被串成同一个内容。这是<b>静默</b>错误 —— 图本身是好的，只是不属于这一路。</para>
    ///
    /// <para><b>鉴别力从哪来</b>：期望值不从 GDI 自己那里拿，而是 <see cref="ReadWindowRegion"/> 走
    /// <c>GetWindowRgn</c>+<c>GetRgnBox</c> 回读的真实窗口区域；该回读的<b>坐标口径</b>已由
    /// <c>AssertCompareCrop</c> 拿独立复算的格表逐路对过（含"2x3 下第 0 路 Y=712、期望 0"那次真红），
    /// 所以这里只需再钉"抓回尺寸 == 那一块尺寸"，不重新推一遍换算。另外两处"会退化成恒真"的形态
    /// （没有区域 / 区域不小于窗口）都直接判红而不是跳过。</para>
    ///
    /// <para>⚠ 本判据<b>直接实例化</b> <c>GdiFrameCapture</c> 测兜底路线本身，因此不依赖
    /// <c>FC_FORCE_GDI_ROUTE</c>；那个测量档是给端到端人工复现留的。</para></summary>
    /// <summary>本轮验收档的**素材池**（任务 #29；口径见 `docs/50` §二十一"残余未证的一块"）。
    ///
    /// <para>只在 `--comparemodetest &lt;video&gt; &lt;routes&gt; &lt;video2 …&gt;` 的路数之后还给了文件时非空。
    /// 空 ⇒ 所有打开点退回"同一份素材按路数重复"，与既往行为逐字相同（既有 29 条判据的夹具不受影响）。</para>
    ///
    /// <para>为什么必须有多素材：账本里那条判据在单素材下**恒真**——各路本来就一样。
    /// 只有喂进不同内容，"各路交出同一张图"才既是可观察的、又是错的。</para></summary>
    private IReadOnlyList<string> _benchMediaPool = Array.Empty<string>();

    /// <summary>按素材池排每一路打开哪个文件；池为空时等价于旧的 <c>Enumerable.Repeat</c>。</summary>
    private List<string> BuildMediaList(string videoPath, int routes)
    {
        if (_benchMediaCountIncludingPrimary(videoPath) <= 1)
            return Enumerable.Repeat(videoPath, routes).ToList();
        var pool = new List<string> { videoPath };
        pool.AddRange(_benchMediaPool);
        var list = new List<string>(routes);
        for (var i = 0; i < routes; i++) list.Add(pool[i % pool.Count]);
        return list;
    }

    /// <summary>素材池里到底有几种**不同**素材（按完整路径归一，忽略大小写）。
    /// 判据拿它决定"能判"还是"必须记未判"——不许拿"传了几个参数"当"内容真的不同"。</summary>
    private int DistinctMediaCount(string videoPath) => _benchMediaCountIncludingPrimary(videoPath);

    private int _benchMediaCountIncludingPrimary(string videoPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            System.IO.Path.GetFullPath(videoPath)
        };
        foreach (var v in _benchMediaPool)
            set.Add(System.IO.Path.GetFullPath(v));
        return set.Count;
    }

    /// <summary>整张位图的**有效像素字节**（按 stride 逐行、丢掉行尾对齐字节）喂 SHA256，返回前 12 位。</summary>
    private static string PixelHashSha256Short(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bpp = System.Drawing.Bitmap.GetPixelFormatSize(data.PixelFormat) / 8;
            var useful = data.Width * bpp;
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < data.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                sha.TransformBlock(row, 0, useful, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return System.Convert.ToHexString(sha.Hash!).Substring(0, 12);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>是不是"整幅黑"（16×9 网格采样）。GDI 对 flip-model 内容本来就不可靠
    /// （<c>Platform/GdiFrameCapture.cs</c> 文件头的实测警告），所以黑帧必须**记未判**而不是判红：
    /// 全黑是"这台机器看不到内容"，不是"路号没参与"。</summary>
    private static bool LooksAllBlack(System.Drawing.Bitmap bmp)
    {
        var stepX = Math.Max(1, bmp.Width / 16);
        var stepY = Math.Max(1, bmp.Height / 9);
        for (var y = 0; y < bmp.Height; y += stepY)
            for (var x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R != 0 || c.G != 0 || c.B != 0) return false;
            }
        return true;
    }

    /// <summary>拼合下各路 GDI 抓回的**内容必须两两不同**（前提：本轮真的喂了 ≥2 种素材）。
    ///
    /// <para>它钉的是尺寸判据（<see cref="AssertGdiHonorsRegion"/>）钉不住的那一半：
    /// "兜底路线路号不参与 ⇒ 各路拿到同一张屏幕合成图"在尺寸上也能被抓，但
    /// **"渲染侧路号不参与"（每格都画同一份画面）**尺寸完全正常 ⇒ 只有内容能把它和正确形态分开。
    /// 单素材下这条恒真 ⇒ 必须 <c>RecordSelfTestSkip</c>，不许记 PASS（SKIP 不当 PASS 是本仓既有纪律）。</para></summary>
    private void AssertRouteContentsDiffer(int routes, string label, string videoPath)
    {
        if (!_compareActive || !Grid.MosaicWindows)
            throw new InvalidOperationException(
                $"GDI内容互异：当前不是拼合态（compare={_compareActive} mosaic={Grid.MosaicWindows}）⇒ 夹具断了");
        var kinds = DistinctMediaCount(videoPath);
        if (kinds < 2)
        {
            RecordSelfTestSkip($"GDI内容互异：本轮素材只有 {kinds} 种 ⇒ 各路内容本来就一样，这条判据恒真 ⇒ 未判" +
                               "（判它需要 --comparemodetest <video> <routes> <video2 …>）");
            Console.WriteLine($"  GDI内容互异：**跳过**（单素材恒真，未验证 ≠ 通过，{label}）");
            return;
        }

        var gdi = new _3FCompare.App.Capture.GdiFrameCapture();
        var seen = new List<(int idx, string hash, string size)>();
        var black = new List<int>();
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");
            if (!s.IsVisible || s.Hwnd == nint.Zero) continue;
            var bmp = gdi.Capture(s.Hwnd);
            if (bmp is null)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路 GDI 抓回 null ⇒ 无从比内容（成因见 app 日志的 [Capture] … why= 行）");
            try
            {
                if (bmp.Width < 16 || bmp.Height < 16)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路抓回 {bmp.Width}x{bmp.Height} 太小 ⇒ 采样无从鉴别，夹具断了");
                if (LooksAllBlack(bmp)) { black.Add(i); continue; }
                seen.Add((i, PixelHashSha256Short(bmp), $"{bmp.Width}x{bmp.Height}"));
            }
            finally { bmp.Dispose(); }
        }

        if (seen.Count < 2)
        {
            RecordSelfTestSkip($"GDI内容互异：{kinds} 种素材里只有 {seen.Count} 路能看到非黑内容" +
                               $"（黑帧路号 [{string.Join(",", black)}]；GDI 对 flip-model 本就不可靠）⇒ 未判");
            Console.WriteLine($"  GDI内容互异：**跳过**（可比路不足 2，{label}，未验证 ≠ 通过）");
            return;
        }

        for (var a = 0; a < seen.Count; a++)
            for (var b = a + 1; b < seen.Count; b++)
                if (seen[a].hash == seen[b].hash)
                    throw new InvalidOperationException(
                        $"{label}：第 {seen[a].idx} 路与第 {seen[b].idx} 路的 GDI 抓帧**逐字节相同**" +
                        $"（{seen[a].size}，SHA256 前 12 位 {seen[a].hash}）而本轮喂了 {kinds} 种不同素材" +
                        "⇒ 路号没参与取图/渲染：各路拿到的是同一张图（导出与缩略图会串成同一个内容）");

        Console.WriteLine($"  GDI内容互异 ✓ {seen.Count} 路两两内容哈希不同（{kinds} 种素材，{label}）；" +
                          "尺寸判据钉不到的「渲染侧串图」这一半也被盯住了");
    }

    private void AssertGdiHonorsRegion(int routes, string label)
    {
        if (!_compareActive || !Grid.MosaicWindows)
            throw new InvalidOperationException(
                $"GDI兜底-拼合裁块：当前不是拼合态（compare={_compareActive} mosaic={Grid.MosaicWindows}）⇒ 夹具断了");
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var gdi = new _3FCompare.App.Capture.GdiFrameCapture();
        var checkedAny = 0;
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");
            if (!s.IsVisible || s.Hwnd == nint.Zero) continue;

            var type = ReadWindowRegion(s.Hwnd, out var box);
            if (type == RegionTypeError)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路在拼合态却没有窗口区域 ⇒ 块没铺上，本判据无从鉴别");
            var windowPx = CompareCropPlanner.ToPhysicalRect(s.Bounds, scaling);
            if (box.Width >= windowPx.Width && box.Height >= windowPx.Height)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路区域 {box.Width}x{box.Height} 没小于整窗 {windowPx.Width}x{windowPx.Height}" +
                    "⇒ 等于没裁，这条判据会退化成恒真，不许当通过");

            var bmp = gdi.Capture(s.Hwnd);
            if (bmp is null)
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路 GDI 抓回 null（兜底路线本身坏了，不是裁没裁的问题）");
            try
            {
                if (bmp.Width != box.Width || bmp.Height != box.Height)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路 GDI 抓回 {bmp.Width}x{bmp.Height}，而该路露出的那一块是 " +
                        $"{box.Width}x{box.Height}（整窗 {windowPx.Width}x{windowPx.Height}）" +
                        "⇒ 兜底路线不认窗口区域，各路会拿到同一张屏幕合成图（导出与缩略图会串成同一个内容）");
            }
            finally { bmp.Dispose(); }
            checkedAny++;
        }
        if (checkedAny == 0)
            throw new InvalidOperationException(
                $"{label}：一条都没查（所有路都不可见或没有子 HWND）⇒ 这条判据根本没跑，不许记成通过");
        Console.WriteLine(
            $"comparemodetest: [GDI兜底-拼合裁块] ✓ {checkedAny} 路：抓回尺寸 == GetWindowRgn 回读的区域尺寸（{label}，整窗铺满对比区）");
    }

    private static int ReadWindowRegion(nint hwnd, out Rect32 box)
    {
        box = default;
        var hRgn = CreateRectRgn(0, 0, 1, 1);
        if (hRgn == nint.Zero) return RegionTypeError;
        try
        {
            var type = GetWindowRgn(hwnd, hRgn);
            if (type == RegionTypeError) return RegionTypeError;
            if (GetRgnBox(hRgn, out var r) == RegionTypeError) return RegionTypeError;
            box = new Rect32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return type;
        }
        finally
        {
            DeleteObject(hRgn);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RGNRECT { public int Left, Top, Right, Bottom; }

    // ══════════ 故障注入自测用 P/Invoke（docs/41 #4 / #5） ══════════

    /// <summary>同步投递窗口消息。故障注入必须用 Send 而不是 Post：
    /// 同线程下 SendMessageW 直接调用窗口过程，返回时被注入的那条消息（含
    /// <c>HandleDropFiles</c> 的 finally）**一定已经处理完**，DragFinish 的探测才没有竞态。
    /// 目标 HWND 由本线程创建，因此不会跨线程阻塞/死锁。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    /// <summary>查询全局内存对象标志。#4 判据的核心探针：<c>DragFinish</c> 释放 HDROP 后，
    /// 该句柄变成无效句柄，本函数返回 <see cref="GMEM_INVALID_HANDLE"/>。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GlobalFlags(nint hMem);

    /// <summary>GlobalFlags 对已释放/无效句柄的返回值。</summary>
    private const uint GMEM_INVALID_HANDLE = 0x8000;

    /// <summary>滚轮消息（故障注入复用"消息循环仍正常"探针；上面滚轮用例里的同名常量是块内局部量）。</summary>
    private const uint WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowRgn(nint hWnd, nint hRgn);

    /// <summary>屏幕坐标命中测试：返回指定点最上层的 HWND（POINT 用 MainWindow.axaml.cs 里已有的定义）。
    /// 用于"某块 UI 是否真的可见"这类判据——Avalonia 自绘内容被子 HWND（视频面）遮挡时，
    /// 布局树/Bounds 全都正常，只有问系统才知道上面盖着谁（P1-4）。</summary>
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT pt);

    /// <summary>Z 序夹具用：把覆盖窗插到 <c>hWndInsertAfter</c> **之后（=之下）**。
    /// 传主窗句柄 ⇒ 覆盖窗被压到主窗底下，这正是真机偶发反转的形态，用来给自愈断言装牙。
    /// 只在自测里用；生产侧的补插见 <see cref="MagnifierOverlayWindow"/>。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetRgnBox(nint hRgn, out RGNRECT lprc);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
