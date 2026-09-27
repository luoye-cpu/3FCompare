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
using _3FCompare.App;
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

        // ── ④ 对比模式：格表之外的新 lane 必然不可见，且必须提示、不得改模式 ──
        if (_compareActive)
        {
            var modeBefore = _compareMode;
            var cells = CompareLayout.CellCount(_compareMode);
            var statusBefore = StatusInfo.Text;
            AddSlotPlaceholder();
            await System.Threading.Tasks.Task.Delay(150);   // IsVisible 由 Arrange 决定，等一次布局
            var hidden = Grid.Count > cells;
            var addedLane = Grid.GetSurface(Grid.Count - 1);
            if (hidden && addedLane is { IsVisible: true })
                throw new InvalidOperationException(
                    $"{label}: 第 {Grid.Count} 条 lane 超出 {cells} 格表却仍然可见（格表短于路数时应隐藏）");
            if (hidden && StatusInfo.Text == statusBefore)
                throw new InvalidOperationException(
                    $"{label}: 新 lane 被 {modeBefore} 格表隐藏，状态栏却没有任何提示（用户看到的是「点了没反应」）");
            if (_compareMode != modeBefore)
                throw new InvalidOperationException(
                    $"{label}: 加路改掉了对比模式 {modeBefore}→{_compareMode}（模式只按素材路数收敛）");
            RemoveLastSlot();
            await System.Threading.Tasks.Task.Delay(150);
        }

        // ── ③ D1..D9 只加不减 + 上限：触顶后加路既不得缩格、也必须给可见反馈 ──
        GrowLanes(9);
        if (Grid.Count != 9)
            throw new InvalidOperationException($"{label}: D9 未把网格扩到 9 路 网格 {lane0}→{Grid.Count}");
        GrowLanes(2);   // 已在 9 路时按 D2：承诺是"只加不减"，不得反过来撤掉 7 条空 lane
        if (Grid.Count != 9)
            throw new InvalidOperationException(
                $"{label}: D2 在 9 路时撤了空 lane 9→{Grid.Count}（违反「只加不减」）");
        var statusAtLimit = StatusInfo.Text;
        AddSlotPlaceholder();
        if (Grid.Count != 9)
            throw new InvalidOperationException(
                $"{label}: 已达 9 路上限时加路反而缩了网格 9→{Grid.Count}");
        if (StatusInfo.Text == statusAtLimit)
            throw new InvalidOperationException($"{label}: 加路触顶时无任何可见反馈");
        if (_sync.Count != route0)
            throw new InvalidOperationException(
                $"{label}: 加路触顶却改动了素材路数 {route0}→{_sync.Count}");

        Grid.SetCount(lane0, _realMode);                     // 还原（撤的全是空 lane）
        if (Grid.Count != lane0 || _sync.Count != route0)
            throw new InvalidOperationException(
                $"{label}: 还原失败 网格={Grid.Count}（期望 {lane0}）路数={_sync.Count}（期望 {route0}）");
        Log($"{label} 加减路契约 ✓ 加路只扩格({lane0}→{lane0 + 1})、撤空 lane 不动素材" +
            $"(路数恒 {route0})、9 路上限不缩格且有反馈");
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
            await SettleFloatingTransportAsync(win, "resize 后", syncsBeforeResize);
            AssertFloatingTransportGeometry(win, "resize 后");
            var syncsBeforeRestore = win.GeometrySyncs;
            Width = widthBefore;
            await SettleFloatingTransportAsync(win, "resize 还原后", syncsBeforeRestore);
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
        Controls.FloatingTransportWindow win, string label, int sinceSyncs = 0, int timeoutMs = 3000)
    {
        var deadline = System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(timeoutMs);
        // 第一段：等"重算确实发生过"。只要求数值连续不变会在**还没轮到重算**的那段时间上过早收敛——
        // 实测 resize 还原后那轮耗时 558ms，轮询采到的 3 次稳定值全是上一次的 1448 ⇒ 假红。
        // 超时不抛，交给后面的原判据，真不重算照样红（不削弱断言）。
        while (System.DateTime.UtcNow < deadline && win.GeometrySyncs <= sinceSyncs)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(15);
        }
        var synced = win.GeometrySyncs > sinceSyncs;
        // 第二段：再等几何稳定（宽度与 Y 连续 3 次采样都不变）。
        var lastW = win.Bounds.Width;
        var lastY = (double)win.Position.Y;
        var stable = 0;
        while (System.DateTime.UtcNow < deadline && stable < 3)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(30);
            var nowW = win.Bounds.Width;
            var nowY = (double)win.Position.Y;
            stable = System.Math.Abs(nowW - lastW) < 0.01 && System.Math.Abs(nowY - lastY) < 0.5
                ? stable + 1
                : 0;
            lastW = nowW;
            lastY = nowY;
        }
        if (!synced)
            Log($"{label}：{timeoutMs}ms 内未观测到几何重算（GeometrySyncs 仍为 {win.GeometrySyncs}），仍按原判据断言");
        else if (stable < 3)
            Log($"{label}：浮条几何在 {timeoutMs}ms 内未收敛（最后观测 宽={lastW:0.#} Y={lastY:0}），仍按原判据断言");
    }

    /// <summary>浮条几何闸门：窗口宽 = 视频区宽 - 2×边距 + 2×留白，栏体底边 = 视频区底边 - 边距。
    /// 全部按物理像素比对，容差 2px。</summary>
    private void AssertFloatingTransportGeometry(Controls.FloatingTransportWindow win, string label)
    {
        var h = CenterPanel.Bounds.Height;
        if (h <= 0) throw new InvalidOperationException($"{label}：视频区高度为 0，无法校验浮条位置");

        PixelPoint tl, br;
        try
        {
            tl = CenterPanel.PointToScreen(new Point(0, 0));
            br = CenterPanel.PointToScreen(new Point(CenterPanel.Bounds.Width, h));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label}：视频区 PointToScreen 失败 {ex.Message}");
        }

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        const double pad = Controls.FloatingTransportWindow.Pad;
        const double margin = Controls.FloatingTransportWindow.EdgeMargin;

        var expectWidthDip = (br.X - tl.X) / scaling - 2 * margin + 2 * pad;
        var barBottom = win.Position.Y + (win.Bounds.Height - pad) * scaling;
        var expectBottom = br.Y - margin * scaling;

        if (Math.Abs(win.Bounds.Width - expectWidthDip) > 2)
            throw new InvalidOperationException(
                $"{label}：浮条宽度不符 实际={win.Bounds.Width:0.#} 期望={expectWidthDip:0.#} DIP");
        if (Math.Abs(barBottom - expectBottom) > 2)
            throw new InvalidOperationException(
                $"{label}：浮条底边不符 实际={barBottom:0.#} 期望={expectBottom:0.#}（视频区底边 {br.Y}）");

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

            // VRR 呈现路径覆盖：开启撕裂模式并记录支持状态（不支持则静默回退 VSync）
            _step = "VRR 呈现";
            var vrrSupported = _sync.Slots[0].Session.SetPresentConfig(true);
            Log($"VRR 撕裂呈现: {(vrrSupported ? "显示器链支持 ✓" : "不支持 → 保持 VSync 锁定")}");

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
                                $"（§2.2 回归：放大镜显示的不是光标下的内容）");
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
                _sync.SetViewTransform(1.0f, 0f, 0f);
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
                // 前置条件预热（2026-09-26 实测：32 轮里出现 1 次 dropped=0 的假红）：
                // 本用例靠"同一 tick 内连发 4 次、至少 1 次被节流丢弃"坐实前置，但**首调走冷路径**
                // （JIT 首编译 / _transformFlushTimer 首次创建启动 / 首次日志落盘）时，整串连发
                // 自己就能跨过 16ms 的节流窗口 ⇒ 后几次合法地不被丢弃 ⇒ dropped=0 ⇒ 前置判红。
                // 所以先把冷路径跑热一次，再等越过 16ms 窗口，之后取的基线与连发才落在同一口径上。
                // ApplyViewTransform 在此处是"把当前（未变的）变换再下发一次"，不改任何状态语义。
                // ⚠ 判决、阈值、错误消息的语义一字未改（下面 连发耗时= 的读数保留）。
                ApplyViewTransform();
                await System.Threading.Tasks.Task.Delay(25);   // > 16ms 节流窗口，确保第一次连发不被算作丢弃

                var zoomBefore = _viewZoom;
                var droppedBefore = TransformDroppedCount;

                // 同一 tick 内连发 4 次（无 await）：第 1 次立即下发，后 3 次间隔 0ms < 16ms
                // 必然被丢弃。刻意不用 Task.Delay(5)——Windows 上其实际分辨率约 15ms，
                // 用例会变成时灵时不灵。同步连发是确定性的。
                // ⚠ 上面那句"确定性"2026-09-26 被实测翻掉一次（32 轮里 dropped=0 出现 1 次）：
                //   节流比较的是 Environment.TickCount64，若整串连发自己就跨过了 16ms（首调走冷路径），
                //   后几次就合法地不被丢弃 ⇒ 前置失效。已由上面的**预热 + 越过窗口**修掉前置，
                //   耗时读数继续保留（只记录、不改判决），下次翻红时能直接分清是哪种。
                var burstStart = System.DateTime.UtcNow;
                for (var i = 0; i < 4; i++) OnSurfaceWheel(120);
                var burstMs = (System.DateTime.UtcNow - burstStart).TotalMilliseconds;

                var dropped = TransformDroppedCount - droppedBefore;
                Log($"连发 4 次：_viewZoom={_viewZoom:F3} 已下发={LastSentZoom:F3} 丢弃={dropped} " +
                    $"连发耗时={burstMs:F2}ms（判据只看【丢弃】计数，这个数仅用于归因）");
                // 前置条件坐实：没有一次被丢弃的话，下面的断言恒绿、毫无意义
                if (dropped < 1)
                    throw new InvalidOperationException(
                        "节流用例空转：没有更新被丢弃（连发被改成异步间隔了？断言已失效）" +
                        $"本次连发耗时 {burstMs:F2}ms —— 若 ≥16ms 则是【整串连发跨过了节流窗口】，" +
                        "不是节流失效");

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
                    $"第 {i} 路漂移 {TimeSpan.FromTicks(drift):g} > {Tolerance / 10_000}ms（pos={TimeSpan.FromTicks(snap.Position100ns):g} 期望 {TimeSpan.FromTicks(expect):g}）");
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
    // 各路 PlayerSurface 的 Bounds 必须与 CompareLayout.ComputeCells 的结果一致，
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
        ("cells.swap",            "P6 格→路映射"),
        ("cells.rotate",          "P6 格→路映射"),
        ("entry.split",           "P6 格→路映射"),
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
        try
        {
            var v = await body();
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
    /// <c>Routes</c> 要求的最小会话路数、<c>Mode</c> 要求进入的对比模式、<c>Body</c> 判据本体。
    /// 未登记的判据仍只能按整轮剧本跑，宿主会<b>明说</b>哪些没匹配，绝不静默少跑。</para>
    /// <para><c>Aliases</c> 收着旧的 <c>_step</c> 中文标签，所以 <c>FC_STEPS=对比-对齐模式</c>
    /// 和 <c>FC_STEPS=align.modes</c> 选到同一个单元。</para></summary>
    private sealed record Unit(string Id, int Routes, CompareMode? Mode, string[] Aliases, Func<Task> Body);

    /// <summary>P5 单元：拖动分割手柄 ⇒ <c>SplitChanged</c> ⇒ 重算格表 ⇒ 画面跟着迁。
    /// <para>从整轮剧本里原样搬出，<b>那段断言形式的说明一并搬来，勿改回</b>：
    /// 不能要求"宽和高都要变"—— AB 的第 0 格是通高的（<c>ComputeCells</c> 给 A 的 Height 恒为 1），
    /// 拖竖直分割线只改 X。旧写法在 2 路下必然误报，而 4 路（十字分割，宽高都变）恰好掩盖了它。
    /// 正确形式是用 <c>ComputeCells(mode, 旧split)</c> 与 <c>ComputeCells(mode, 新split)</c> 各自独立复算
    /// 期望矩形，断言实际 Bounds 确实由前者迁到后者；并先坐实"这组 split 真的改变格表"，
    /// 否则断言退化为恒真（无鉴别力）。</para></summary>
    /// <returns>本单元真正下发的裁剪次数（供剧本累计"整轮必须下发过区域"的兜底）。</returns>
    private async Task<int> SplitRelayoutUnitAsync(int routes, CompareMode maxMode)
    {
        EnterCompareMode(maxMode);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        var oldSplit = _compareSplit;                 // 进入模式后 = 该模式的默认初值
        var newSplit = new SplitParams(0.7, 0.35);
        var oldCells = CompareLayout.ComputeCells(maxMode, oldSplit);
        var newCells = CompareLayout.ComputeCells(maxMode, newSplit);

        var cellChanged = false;
        for (var i = 0; i < newCells.Length; i++)
            if (!CellEquals(oldCells[i], newCells[i]))
                cellChanged = true;
        if (!cellChanged)
            throw new InvalidOperationException(
                $"自测选取的分割参数不改变 {maxMode} 格表（{oldSplit} → {newSplit}），断言将失去鉴别力");

        var areaW = Grid.Bounds.Width;
        var areaH = Grid.Bounds.Height;
        var before = Grid.GetSurface(0)?.Bounds ?? default;
        var beforeExp = ExpectedBounds(oldCells[0], areaW, areaH);
        if (!BoundsClose(before, beforeExp))
            throw new InvalidOperationException(
                $"拖动前 {maxMode} 第 0 路 Bounds {before} 与旧 split {oldSplit} 的期望 {beforeExp} 不符");

        OnCompareSplitChanged(newSplit);
        UpdateLayout();

        for (var i = 0; i < routes; i++)
        {
            if (i >= newCells.Length) continue; // 格数 < 路数：多余的路已被隐藏，另有断言覆盖
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"分割驱动重排：第 {i} 路缺失");
            var exp = ExpectedBounds(newCells[i], areaW, areaH);
            if (!BoundsClose(s.Bounds, exp))
                throw new InvalidOperationException(
                    $"分割参数变化未驱动画面重排：{maxMode} 第 {i} 路 Bounds {s.Bounds} ≠ " +
                    $"新 split {newSplit} 的期望 {exp}");
        }
        var after = Grid.GetSurface(0)?.Bounds ?? default;

        AssertCellsDriveLayout(routes, maxMode, $"分割 0.70/0.35（{maxMode}）");
        var crop = AssertCompareCrop(routes, "分割 0.70/0.35");
        Console.WriteLine(
            $"  分割驱动重排 ✓ {maxMode} 第0路 Bounds {before.Width:F1}x{before.Height:F1} → " +
            $"{after.Width:F1}x{after.Height:F1}（与 ComputeCells 复算的 {oldSplit} → {newSplit} 期望一致）");
        return crop;
    }

    /// <summary>P6 单元：菜单/快捷键的「分屏」入口 —— 按路数收敛模式、只显示前 N 格、
    /// 且"已在目标模式时重进"不得抹掉用户拖好的分割参数。从剧本里的内联块原样搬出。
    /// <para><b>鉴别力前提（搬出时补上的洞）</b>：需要 ≥5 路。4 路及以下"格数 == 路数"，
    /// 下面那段"第 splitCells..routes 路应隐藏"的循环<b>一次都不执行</b> ⇒ 单元退化成恒真，
    /// 看着绿其实什么都没验。所以这里显式判红，并且单元自己声明 <c>Routes=5</c>。</para></summary>
    private async Task SplitEntryUnit(int routes)
    {
        EnterSplitMode();
        await System.Threading.Tasks.Task.Delay(150);

        var splitTarget = CompareLayout.CoerceMode(CompareMode.Abcd, routes);
        if (!_compareActive || _compareMode != splitTarget)
            throw new InvalidOperationException(
                $"分屏入口未按路数收敛：active={_compareActive} mode={_compareMode} " +
                $"期望 {splitTarget}（{routes} 路，CoerceMode 复算）");

        AssertCellsDriveLayout(routes, splitTarget, "分屏入口");

        // 显式钉住"第 5~9 路隐藏"：AssertCellsDriveLayout 已按格数断言，这里再点名一次，
        // 以免将来格数规则变化时这条验收点被静默带过（9 路 → 只应显示前 4 路）。
        var splitCells = CompareLayout.CellCount(splitTarget);
        if (splitCells >= routes)
        {
            // ≤4 路时"格数 == 路数"，下面这个循环一次都不执行 ⇒ 该子断言没有鉴别力。
            // 按规矩（docs/49 R4）明写跳过、不伪装成验过；单元宿主按声明的 5 路起盘时才会真正执行。
            Console.WriteLine($"  分屏入口：子断言「多余路应被隐藏」**跳过**" +
                              $"（{routes} 路恰好 {splitCells} 格，没有多余路 ⇒ 该分支恒真，" +
                              "宿主以 Routes=5 起盘时才验得到）");
            RecordSelfTestSkip($"分屏入口：{routes} 路无多余路，「多余路应被隐藏」子断言恒真");
        }
        else
        {
            for (var i = splitCells; i < routes; i++)
                if (Grid.GetSurface(i)?.IsVisible != false)
                    throw new InvalidOperationException(
                        $"分屏入口：{routes} 路收敛到 {splitTarget}（{splitCells} 格），第 {i} 路应隐藏却仍可见");
            Console.WriteLine(
                $"  分屏入口 ✓ 第 {splitCells}..{routes - 1} 路确被隐藏（显示前 {splitCells} 路）");
        }
        Console.WriteLine(
            $"  分屏入口 ✓ {routes} 路收敛到 {splitTarget}（显示前 {splitCells} 路）");

        // 幂等：已处于目标分屏模式时再点「分屏」不得重进（EnterCompareMode 会重置分割参数，
        // 用户拖好的位置不该被抹掉）。模拟一次用户拖动，再走一遍入口。
        var draggedSplit = new SplitParams(0.31, 0.62);
        OnCompareSplitChanged(draggedSplit);
        UpdateLayout();
        EnterSplitMode();
        // 直接比 X / Y（CellEquals 只比 CellRect，SplitParams 是另一类型）
        if (Math.Abs(_compareSplit.X - draggedSplit.X) > CompareLayout.Epsilon ||
            Math.Abs(_compareSplit.Y - draggedSplit.Y) > CompareLayout.Epsilon)
            throw new InvalidOperationException(
                $"分屏入口重进后分割参数被重置：{_compareSplit} ≠ 用户拖动后的 {draggedSplit}");
        Console.WriteLine("  分屏入口幂等 ✓ 已在该模式时未重置分割参数");
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
    /// <para>一趟走完"停用 → 重新启用"：① 分屏（ABCD 4 格 / 5 路）⇒ 第 5 路停用且不呈现；
    /// ② 回网格（全部占格）⇒ 第 5 路重新启用、恢复呈现，并且位置落回规范时间
    /// （停用期间它不跟随 master，若不 Seek 回去，用户切回来会看到停在旧帧）。</para>
    /// <para>鉴别力前提：≥5 路。4 路及以下"格数 == 路数"，没有任何路会被停用，
    /// 整条判据退化成恒真 ⇒ 按 docs/49 R4 明写跳过，单元自己声明 Routes=5。</para></summary>
    private async Task RouteActivityUnit(int routes)
    {
        if (routes < 5)
        {
            Console.WriteLine($"  路启用态：**跳过**（{routes} 路 ≤ ABCD 的 4 格，没有多余路可停用，" +
                              "该判据会退化成恒真；宿主以 Routes=5 起盘时才验得到）");
            RecordSelfTestSkip($"路启用态：{routes} 路没有多余路可停用，整条判据恒真");
            return;
        }

        EnterGridMode();
        await System.Threading.Tasks.Task.Delay(200);
        EnterSplitMode();
        await System.Threading.Tasks.Task.Delay(300);   // 占格集合变化 → Post 的延后一拍要落地
        await AssertRouteActivityAsync(routes, CompareLayout.CellCount(CompareMode.Abcd), "分屏停用");

        // 回到全部占格：此前停用的路必须重新启用并恢复呈现
        EnterGridMode();
        await System.Threading.Tasks.Task.Delay(300);
        await AssertRouteActivityAsync(routes, routes, "网格恢复");

        // 重新启用是先 Seek 回规范时间再播的：跑完一段后仍应与 master 同位（与 CheckDrift 同阈值）
        const long Tolerance = 250_0000;   // 250ms
        var snaps = _sync.ReadAllSnapshots();
        var masterPos = snaps[0]?.Position100ns ?? 0;
        for (var i = 1; i < routes; i++)
        {
            var s = snaps[i] ?? throw new InvalidOperationException($"网格恢复：第 {i} 路无快照");
            var drift = Math.Abs(s.Position100ns - (masterPos + _sync.Slots[i].Offset100ns));
            if (drift > Tolerance)
                throw new InvalidOperationException(
                    $"重新启用后第 {i} 路与规范时间相差 {TimeSpan.FromTicks(drift):g} > 250ms" +
                    $"（停用期间它不跟随 master，切回来必须 Seek 回规范时间）");
        }
        Console.WriteLine($"  路启用态 ✓ {routes} 路：分屏停用第 4..{routes - 1} 路、回网格全部恢复且同位");

        // ③ **暂停态**下"停用 → 再启用"：Seek 只把位置挪对，画面是否真落下来是另一件事
        //   （本仓自记的内核契约：子窗口尺寸变了不 Redraw 就停 flips，见 SyncController.RedrawAll）。
        //   这一步全程不 Play；停滞看门狗只看第 0 路，看不见"切回来的那一路冻着"。
        var beforeReEnable = SnapshotTuple(_sync.ReadAllSnapshots());
        EnterSplitMode();
        await System.Threading.Tasks.Task.Delay(300);
        EnterGridMode();
        await System.Threading.Tasks.Task.Delay(400);
        var afterReEnable = SnapshotTuple(_sync.ReadAllSnapshots());
        var last = routes - 1;
        if (afterReEnable[last].presented <= beforeReEnable[last].presented)
            throw new InvalidOperationException(
                $"暂停态下重新启用第 {last} 路后没有重新出帧（presented {beforeReEnable[last].presented} → " +
                $"{afterReEnable[last].presented}）：Seek 只挪位置，子窗口从 0×0 改回格尺寸必须补一次 Redraw");
        Console.WriteLine($"  路启用态 ✓ 暂停态切回第 {last} 路重新出帧" +
                          $"（presented +{afterReEnable[last].presented - beforeReEnable[last].presented}）");
    }

    /// <summary>P6 单元：「网格」入口必须退出对比模式，并把 2~9 路全部恢复成均匀网格。
    /// <para>自带前置（先进分屏再切网格），所以能单跑；排版期望值由 Core 纯函数
    /// <c>GridLayout.ComputeGrid</c> <b>独立复算</b>，不读 Grid 的内部预设状态。</para></summary>
    private async Task GridEntryUnit(int routes)
    {
        EnterSplitMode();
        await System.Threading.Tasks.Task.Delay(150);
        EnterGridMode();
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
    /// <para><b>为什么钉这一列</b>：三项原本埋在「对比模式」子菜单里、名字是"叠加 / 分屏 / 网格"，
    /// 现在提到视图菜单顶部做成一列单选。用户唯一的确认途径就是"圆点在哪一项"—— 勾错比不勾更糟
    /// （会让人以为切换没生效）。而菜单只是入口之一：G / V / S 与 C 循环改的是同一份状态。</para>
    ///
    /// <para><b>牙在哪</b>：① 按键一律走 <see cref="RaiseKeyOn"/> 的真实路由（隧道→目标→冒泡），
    /// 于是"菜单项的 <c>InputGesture</c> 被菜单处理器又执行一次"这类双触发会当场露出来 ——
    /// 每按一次状态必须<b>恰好翻转一次</b>（S 是开关语义，按两次必回到原态）；
    /// ② 勾选只读 <c>MenuItem.IsChecked</c>，判据不与 <see cref="RefreshCompareModeChecks"/> 同源复制；
    /// ③ 三项必须同父、同组、同为 Radio，否则"一列互斥"在视觉上根本不成立。</para></summary>
    private async Task ViewModeColumnUnit(int routes)
    {
        if (routes < 2)
            throw new InvalidOperationException($"视图模式列：{routes} 路不足以进入对比，宿主应按 2 路起盘");

        var three = new[] { MenuViewStandard, MenuViewAbSplit, MenuViewWipe };
        var names = new[] { "标准模式", "A/B 可拖动", "左右拉动" };
        for (var i = 0; i < three.Length; i++)
        {
            if (three[i].ToggleType != global::Avalonia.Controls.MenuItemToggleType.Radio)
                throw new InvalidOperationException($"「{names[i]}」不是 Radio 型，画不出互斥单选圆点");
            if (three[i].GroupName != "ViewMode")
                throw new InvalidOperationException($"「{names[i]}」未落进 ViewMode 组（实际 {three[i].GroupName}）");
            if (three[i].Parent is null || !ReferenceEquals(three[i].Parent, three[0].Parent))
                throw new InvalidOperationException($"「{names[i]}」与其余两项不同父，'一列'不成立");
        }

        // 勾选与真实状态是否同源：真实状态只有一个来源（两个 bool），勾选必须由它推出。
        void ExpectChecked(string expect)
        {
            RefreshCompareModeChecks();
            var real = _compareOverlayActive ? "wipe" : _compareActive ? "ab" : "std";
            if (real != expect)
                throw new InvalidOperationException($"状态未切到 {expect}（实际 {real}）");
            var got = (MenuViewStandard.IsChecked, MenuViewAbSplit.IsChecked, MenuViewWipe.IsChecked);
            var want = expect switch
            {
                "std" => (true, false, false),
                "ab" => (false, true, false),
                _ => (false, false, true),
            };
            if (got != want)
                throw new InvalidOperationException(
                    $"切到 {expect} 后勾选=标准{got.Item1} A/B{got.Item2} 拉动{got.Item3}，期望 {want}");
        }

        // 起点：标准模式（网格入口是幂等的，已在该态时不动状态）
        EnterGridMode();
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("std");

        // V = A/B 可拖动
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.V);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("ab");

        // S = 左右拉动（键是"开关"：若 InputGesture 让菜单项再执行一次"进入拉动"，此处仍会停在拉动态）
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.S);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("wipe");
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.S);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("ab");

        // G = 回标准
        RaiseKeyOn(FocusedRouteSource, global::Avalonia.Input.Key.G);
        await System.Threading.Tasks.Task.Delay(150);
        ExpectChecked("std");

        Log($"视图模式列 ✓ 三项同父互斥，G/V/S 真实路由逐次恰好翻转一次（{routes} 路）");
    }

    /// <summary>分割线的拖动命中区：单轴手柄（AB 竖线 / AB 竖横线 / ABC 三列的竖线）沿轴放开到<b>整条线</b>，
    /// 交叉点型手柄（ABC / ABCD 的 <c>SplitAxis.Both</c>）仍只放开一个圆点。
    ///
    /// <para><b>为什么钉这个</b>：用户对"左右拉动对比"的说法是"<b>中间的线可以拉动</b>"。
    /// 判据改成整条线之前，只有线正中间那个圆点（半径 14 DIP）能抓 —— 窗口越高，"瞄不准"越明显，
    /// 而这条差别<b>肉眼看不出来</b>（线画在那儿、圆点也画在那儿）。
    /// 交叉点型刻意不放开：那种手柄一次改两个分量，沿整条线抓会把另一轴一起拽到指针高度，
    /// 表现为"没抓的那条线跳了"。</para>
    ///
    /// <para>读的是覆盖层自己的探针 <see cref="Controls.LayoutOverlayWindow.HitHandleIndexAt"/>，
    /// 也就是真实点击走的那一条判据（DIP 路径），不复制几何。</para></summary>
    private async Task SplitLineHitUnit(int routes)
    {
        if (routes < 3)
        {
            // 不判红也不静默：登记跳过 ⇒ 汇总表里这一行是 SKIP，而不是"凭空消失"，也不是 PASS。
            // （隔离单元 view.split-line-hit 自带 Routes=3，单跑它一定真执行。）
            Console.WriteLine($"  分割线命中：**跳过**（{routes} 路摆不出 ABC 交叉点，" +
                              "该判据要 AB + ABC 两种模式各测一次）");
            RecordSelfTestSkip($"分割线命中：{routes} 路摆不出 ABC 交叉点");
            return;
        }

        EnterCompareMode(CompareMode.Ab);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
        var ov = _layoutOverlay ?? throw new InvalidOperationException("分割线命中：覆盖层未创建");
        if (!ov.IsOverlayVisible) throw new InvalidOperationException("分割线命中：覆盖层不可见");
        var w = ov.Bounds.Width;
        var h = ov.Bounds.Height;
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"分割线命中：覆盖层尺寸非法 {w:F1}x{h:F1}");

        var lineX = _compareSplit.X * w;
        // 线的顶端与底端：离"线中间的圆点"半个窗口以上，只有放开到整条线才可能命中
        var top = ov.HitHandleIndexAt(new Point(lineX, h * 0.05));
        var bottom = ov.HitHandleIndexAt(new Point(lineX, h * 0.95));
        if (top != 0 || bottom != 0)
            throw new InvalidOperationException(
                $"AB 竖线未放开到整条线：线上端命中={top} 下端命中={bottom}（都应=0）");
        // 离线 1/4 宽处必须不命中 —— 否则整幅画面都成了拖拽区，滚轮与平移会被吞
        var off = ov.HitHandleIndexAt(new Point(lineX + w * 0.25, h * 0.5));
        if (off != -1)
            throw new InvalidOperationException($"AB：离线 1/4 宽处仍命中手柄 {off}（命中区过宽，会吞掉画面上的滚轮/平移）");
        Console.WriteLine($"  分割线命中 ✓ AB 竖线整条可抓（上端/下端=0），离线 1/4 宽处={off}");

        // 交叉点型：只放圆点
        EnterCompareMode(CompareMode.Abc);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
        var (cx, cy) = _compareSplit;
        var cross = ov.HitHandleIndexAt(new Point(cx * w, cy * h));
        var farUp = ov.HitHandleIndexAt(new Point(cx * w, h * 0.05));
        if (cross != 0)
            throw new InvalidOperationException($"ABC 交叉点本身抓不住（命中={cross}）");
        if (farUp != -1)
            throw new InvalidOperationException(
                $"ABC 的竖线顶端也被判成手柄（命中={farUp}）：交叉点型沿整条线放开会把 Y 一起拽到指针高度");
        Console.WriteLine($"  分割线命中 ✓ ABC 交叉点可抓（={cross}），同一条线的顶端不命中（={farUp}）");

        // 线画的位置必须就是拖动判据用的那一份：两者不同源时表现为"线在这儿、抓住却拖不动"
        var handle = ov.HandlePositions()[0];
        if (System.Math.Abs(handle.X - cx) > CompareLayout.Epsilon || System.Math.Abs(handle.Y - cy) > CompareLayout.Epsilon)
            throw new InvalidOperationException(
                $"覆盖层手柄位置 {handle} 与分割参数 ({cx},{cy}) 不同源");
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
        EnterCompareMode(CompareMode.Ab);
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

        // 前提必须是"仍在 AB（2 格）"：若打开新媒体把模式改了，这条判据的对象就不存在，
        // 要明说而不是让下面的断言去背这个锅。
        if (!_compareActive || _compareMode != CompareMode.Ab)
        {
            Console.WriteLine($"  新路即停用：**跳过**（追加后模式变为 active={_compareActive} " +
                              $"mode={_compareMode}，已不是 2 格的 AB，没有\"多出的路\"可判）");
            RecordSelfTestSkip("新路即停用：追加后已不在 2 格的 AB，无多余路可判");
            return;
        }
        if (_sync.IsRouteActive(2))
            throw new InvalidOperationException(
                "AB（2 格）下新加的第 3 路仍是启用态：发布门只比索引集时 {0,1} 加一路还是 {0,1}，" +
                "事件不发 ⇒ 新路按默认值 Active=true 落地并被一起解码+呈现");
        await AssertRouteActivityAsync(3, 2, "新路即停用");
        EnterGridMode();
        await System.Threading.Tasks.Task.Delay(300);
    }

    private Unit[] BuildUnits(string videoPath)
    {
        var routes3 = Math.Max(3, _sync.Count);
        var maxMode3 = CompareLayout.AvailableModes(routes3)[^1];
        return new[]
        {
            new Unit("lane.add-remove-contract", 2, CompareMode.Ab,
                new[] { "对比-加减路契约" }, async () =>
                {
                    await AssertLaneAddRemoveContractAsync("单元");
                    // 不带对比模式时也要能跑：再验一遍非对比路径（AB 之外没有格表遮蔽）
                    ExitCompareMode();
                    await AssertLaneAddRemoveContractAsync("单元-非对比");
                }),
            new Unit("cells.swap", 3, CompareMode.Ab, new[] { "对比-左右互换" },
                async () => await AssertSwapCompareCellsAsync(Math.Max(3, _sync.Count))),
            new Unit("cells.rotate", 3, null, new[] { "对比-轮换画面" },
                async () => await AssertRotateCompareCellsAsync(Math.Max(3, _sync.Count))),
            new Unit("align.modes", 3, null, new[] { "对比-对齐模式" },
                async () => await AssertCompareAlignModesAsync(Math.Max(3, _sync.Count), maxMode3)),
            new Unit("magnify.seamless", 2, CompareMode.Ab, new[] { "无缝放大 z=2" },
                async () =>
                {
                    var n = _sync.Count;
                    AssertCompareMagnifyGates(n);
                    var applied = await AssertCompareMagnifyAsync(n, CompareMode.Ab,
                        zoom: 2.0, cropX: 0.25, cropY: 0.25);
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
                    EnterCompareMode(CompareMode.Ab);
                    await System.Threading.Tasks.Task.Delay(200);
                    AssertCompareOverlay(n, CompareMode.Ab);
                    AssertCellsDriveLayout(n, CompareMode.Ab, "进入 AB");
                    // 只是累计，不据此判红：AB 下"窗口恰好等于格"时合法地一个区域都不用下发
                    //（AssertCompareCrop 内部会核对回读，0 次它自己会打"0/N 路设了区域"）。
                    _cropTotal += AssertCompareCrop(n, "进入 AB");
                }),
            new Unit("layout.mode-sweep", 3, null, new[] { "对比-切换" },
                async () =>
                {
                    var n = _sync.Count;
                    foreach (var m in CompareLayout.AvailableModes(n))
                    {
                        EnterCompareMode(m);
                        await System.Threading.Tasks.Task.Delay(150);
                        AssertCompareOverlay(n, m);
                        AssertCellsDriveLayout(n, m, $"模式 {m}");
                    }
                }),
            new Unit("variants.shape", 4, CompareMode.Abcd, new[] { "对比-模式变体" },
                async () => await AssertCompareModeVariantsAsync(_sync.Count)),
            new Unit("magnify.wheel", 2, CompareMode.Ab, new[] { "对比-滚轮无缝放大" },
                async () => { _cropTotal += await AssertCompareWheelMagnifyAsync(_sync.Count, CompareMode.Ab); }),
            new Unit("exit.cleanup", 2, null, new[] { "对比-退出" },
                async () =>
                {
                    var n = _sync.Count;
                    EnterCompareMode(CompareMode.Ab);
                    await System.Threading.Tasks.Task.Delay(150);
                    ExitCompareMode();
                    await System.Threading.Tasks.Task.Delay(150);
                    if (_layoutOverlay is null || _layoutOverlay.IsOverlayVisible)
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
                    var ms = CompareLayout.AvailableModes(_sync.Count);
                    // 只累计、不据此判红：格与窗口恰好对齐时合法地 0 次下发
                    _cropTotal += await SplitRelayoutUnitAsync(_sync.Count, ms[^1]);
                }),
            new Unit("entry.split", 5, null, new[] { "模式-分屏入口" },
                async () => await SplitEntryUnit(_sync.Count)),
            new Unit("entry.grid", 3, null, new[] { "模式-网格入口" },
                async () => await GridEntryUnit(_sync.Count)),
            new Unit("view.mode-column", 2, null, new[] { "视图模式列" },
                async () => await ViewModeColumnUnit(_sync.Count)),
            new Unit("view.split-line-hit", 3, null, new[] { "分割线命中" },
                async () => await SplitLineHitUnit(_sync.Count)),
            new Unit("view.route-activity", 5, null, new[] { "路启用态" },
                async () => await RouteActivityUnit(_sync.Count)),
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
        _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
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
            if (u.Mode is { } m) { EnterCompareMode(m); await System.Threading.Tasks.Task.Delay(150); }
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

    private async System.Threading.Tasks.Task RunCompareModeTestAsync(string videoPath, int routes)
    {
        routes = Math.Clamp(routes, 2, 9);
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
            _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
            Console.WriteLine($"comparemodetest: 请求打开 {routes} 路（{(_realMode ? "真实" : "演示")}），等待就绪...");

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
            EnterCompareMode(CompareMode.Ab);
            // 覆盖窗 Show → OnOpened 装钩子发生在同一轮消息泵内，这里给一拍余量
            await System.Threading.Tasks.Task.Delay(300);
            await StepAsync(() => { AssertCompareOverlay(routes, CompareMode.Ab); });
            await StepAsync(() => { AssertCellsDriveLayout(routes, CompareMode.Ab, "进入 AB"); });
            // 裁剪（SetWindowRgn）接线：区域必须真的被下发且回读一致，Win32 路径另做端到端往返
            cropApplied += await StepAsync<int>(() => Task.FromResult(AssertCompareCrop(routes, "进入 AB")));
            AssertCropWin32RoundTrip("进入 AB");

            // 加路/减路契约（AB 下的那一支：新 lane 落在 2 格表之外 ⇒ 必然不可见 + 必须有提示）
            _step = "对比-加减路契约";
            await StepAsync(async () => { await AssertLaneAddRemoveContractAsync("comparemodetest"); });

            // 逐级切到可用集合中的每个模式，验证"按路数自动收敛"与"格表随模式改变"
            var modes = CompareLayout.AvailableModes(routes);
            Console.WriteLine($"comparemodetest: 路数={routes} 可用模式=[{string.Join(", ", modes)}]");
            foreach (var m in modes)
            {
                _step = $"对比-切换 {m}";
                EnterCompareMode(m);
                await System.Threading.Tasks.Task.Delay(150);
                await StepAsync(() => { AssertCompareOverlay(routes, m); });
                await StepAsync(() => { AssertCellsDriveLayout(routes, m, $"模式 {m}"); });
            }

            // 核心验收点：拖动分割手柄 ⇒ SplitChanged ⇒ 重算格表 ⇒ 画面跟着变。
            // 直接走宿主回调（覆盖层的拖动最终就是调它）。
            //
            // ⚠ 断言形式（此处曾是有缺陷的写法，勿改回）：**不能**要求"宽和高都要变"。
            // AB 的第 0 格是**通高**的（ComputeCells 给 A 的 Height 恒为 1，见 CompareLayout），
            // 拖动竖直分割线只改 X ⇒ 只有格宽变，格高本就不应该变。旧写法在 2 路下必然误报；
            // 4 路（十字分割，宽高都变）恰好掩盖了它，才一直没被暴露。
            //
            // 正确形式：用 ComputeCells(mode, 旧split) 与 ComputeCells(mode, 新split) 各自**独立复算**
            // 出期望矩形，断言实际 Bounds 确实由前者迁到后者。对 Ab/Abc/Abcd 一律成立，
            // 且只要"重排没生效"（Bounds 停在旧 split 的值）就判红 —— 鉴别力不降。
            _step = "对比-分割驱动重排";
            var maxMode = modes[modes.Count - 1];
            cropApplied += await StepAsync<int>(async () => await SplitRelayoutUnitAsync(routes, maxMode));

            // #22：放大镜浮窗坐标。放在这里是因为**只有多格布局才具备鉴别力**——
            // 被测路（原点离容器最远的那一格）与容器原点差数百 DIP，"漏换算"必然判红；
            // 单路布局下这个差只有 1px（--selftest 里那条是同一断言的低鉴别力版本）。
            // 必须在"无缝放大"之前：放大后窗口尺寸被刻意放大，几何含义不同。
            _step = "放大镜浮窗坐标";
            await StepAsync(async () => { await AssertMagnifierFollowsCursorAsync(routes, $"对比 {maxMode}"); });

            // ── 无缝放大（子窗口放大 + 偏移 + 裁剪）：默认关闭，这里显式启用后逐项验证 ──
            // 必须放在"退出"之前、且结束前复位：否则后续断言（期望 Bounds == 格）会失败。
            _step = "对比-放大闸门";
            await StepAsync(() => { AssertCompareMagnifyGates(routes); });
            _step = "对比-无缝放大";
            cropApplied += await StepAsync<int>(async () => await AssertCompareMagnifyAsync(routes, maxMode, zoom: 2.0, cropX: 0.25, cropY: 0.25));
            _step = "对比-放大复位";
            await StepAsync(async () => { await AssertCompareMagnifyResetAsync(routes); });

            // ── 滚轮 → 无缝放大：生产 UI 的触发路径（此前内核完整但没有任何 UI 入口）──
            _step = "对比-滚轮无缝放大";
            cropApplied += await StepAsync<int>(async () => await AssertCompareWheelMagnifyAsync(routes, maxMode));

            // ── 左右互换 / 格→路映射 ──
            _step = "对比-左右互换";
            await StepAsync(async () => { await AssertSwapCompareCellsAsync(routes); });

            _step = "对比-轮换画面";
            await StepAsync(async () => { await AssertRotateCompareCellsAsync(routes); });

            // ── 分辨率对齐模式（相对 / 像素级）──
            _step = "对比-对齐模式";
            await StepAsync(async () => { await AssertCompareAlignModesAsync(routes, maxMode); });

            // ── 模式变体：AB 竖 / ABC 三列（含"三列有两条竖线 ⇒ 两个手柄"）──
            _step = "对比-模式变体";
            await StepAsync(async () => { await AssertCompareModeVariantsAsync(routes); });

            // ── 叠加模式（对标 ICAT Single Screen，docs/31 阶段 2）──
            // 放在放大复位之后、退出之前：进入叠加会切到 AB 并复位放大；退出叠加后回到 AB 左右分栏，
            // 因此后面既有的"退出对比模式"断言（含 AssertCompareCropCleared）不受影响。
            cropApplied += await StepAsync<int>(async () => await AssertCompareOverlayAsync(routes, "叠加"));

            // ── 模式家族入口（docs/31 阶段 3）：菜单 / 快捷键的三项入口必须真的可用 ──
            // 这一段专盯用户点名的 5~9 路场景：
            //   ① 分屏入口按路数收敛（≥4 路 → ABCD），且**只显示前 4 路**（第 5~9 路隐藏）；
            //   ② 网格入口退出对比模式 ⇒ **全部路数恢复可见**，排版与 GridLayout.ComputeGrid
            //      独立复算的结果一致（9 路 = 3×3）；
            //   ③ 退出后区域 / 覆盖层复原（本段末尾 + 下面的"对比-退出"各钉一次）。
            // 期望值一律来自 Core 的纯函数（CoerceMode / CellCount / ComputeGrid），不回读 Grid 内部状态。
            _step = "模式-分屏入口";
            await StepAsync(async () => await SplitEntryUnit(routes));

            // ── 网格入口：必须退出对比模式，并把 2~9 路全部恢复成均匀网格 ──
            _step = "模式-网格入口";
            await StepAsync(async () => await GridEntryUnit(routes));

            // ── 视图模式一列（菜单顶部三项）：勾选 ↔ 真实状态 ↔ G/V/S 真实键盘路由 ──
            _step = "视图模式列";
            await StepAsync(async () => await ViewModeColumnUnit(routes));

            // ── 分割线命中区：单轴放开到整条线，交叉点型只放圆点 ──
            // 路数不足时由判据体自己登记 SKIP（汇总表里看得见它没判），不在外面 if 掉——
            // 外面 if 掉的后果是这一行<b>从表里消失</b>，读表的人不知道少了一条。
            _step = "分割线命中";
            await StepAsync(async () => await SplitLineHitUnit(routes));

            // ── 路启用态：没占格的路必须停用（不解码/不呈现），切回全部占格时能恢复且同位 ──
            _step = "路启用态";
            await StepAsync(async () => await RouteActivityUnit(routes));

            _step = "对比-退出";
            ExitCompareMode();            await System.Threading.Tasks.Task.Delay(150);
            if (_layoutOverlay is null || _layoutOverlay.IsOverlayVisible)
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
                Console.WriteLine($"comparemodetest: 已执行项全部通过 ✓（另有 {_skippedUnits} 项被选择器跳过，**未验**）");
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
    //   #BENCHPHASE name=<baseline|magnified> zoom=<z> t0=<epochMs> t1=<epochMs> wall=<s> fps=<n> ...
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
            var modes = CompareLayout.AvailableModes(routes);

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
            EnterCompareMode(modes[modes.Count - 1]);
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

            var (deltas, fps) = await SampleBenchPhaseAsync("magnified", zoom, secondsPerPhase);

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
            var stalledRoutes = deltas.Count(d => d <= 0);
            ResetCompareMagnify();
            UpdateLayout();
            await System.Threading.Tasks.Task.Delay(200);

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
    private async System.Threading.Tasks.Task<(long[] deltas, double fps)> SampleBenchPhaseAsync(
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

        Console.WriteLine(
            $"#BENCHPHASE name={name} zoom={zoom} t0={t0} t1={t1} wall={wall:F2} fps={avgFps:F2} " +
            $"clock={clockSec:F2} hitches=[{string.Join(",", hitches)}] presented=[{string.Join(",", deltas)}]");
        Console.Out.Flush();
        return (deltas, avgFps);
    }

    /// <summary>打印本阶段的实际几何：**按 GetWindowRect 累加各路子 HWND 的真实像素**（这才是
    /// swapchain 尺寸，也才是显存开销的来源），并与闸门用的估算值并列 —— 两者不一致就说明
    /// 估算函数与实际窗口尺寸脱节（闸门会因此失效）。</summary>
    private void ReportBenchGeometry(double zoom, bool accepted, int routes, double budgetOverride)
    {
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
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

    /// <summary>断言覆盖层处于可用状态：已进入、可见、钩子已装、逐像素透明、模式/分割可读。
    /// 任一不满足即抛 —— 这些正是"接线是否真的生效"的判据，静默通过等于没验证。</summary>
    private void AssertCompareOverlay(int routes, CompareMode expected)
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

        if (overlay.Mode != expected)
            throw new InvalidOperationException($"模式不符：{overlay.Mode} != {expected}");

        var available = CompareLayout.AvailableModes(routes);
        if (!available.Contains(overlay.Mode))
            throw new InvalidOperationException($"{routes} 路下 {overlay.Mode} 不在可用集合 [{string.Join(", ", available)}]");

        Console.WriteLine(
            $"comparemodetest: 模式={overlay.Mode} 分割=({overlay.Split.X:F3},{overlay.Split.Y:F3}) " +
            $"透明={overlay.AchievedTransparency} 钩子={overlay.HitTestHookInstalled} 可见={overlay.IsOverlayVisible} " +
            $"owner={overlay.OwnerHwndAttached} topmost={overlay.HasTopmostStyle} " +
            $"高于主窗口={overlay.IsAboveHostInZOrder} ✓");
    }

    /// <summary>核心验收点：断言"格表确实驱动了画面排版"。
    ///
    /// <para>判据（对每一路逐一核对，不看聚合值）：</para>
    /// <list type="bullet">
    /// <item><description>第 i 路（i &lt; 格数）可见，且其 <c>Bounds</c> 等于
    /// <see cref="CompareLayout.ComputeCells"/> 第 i 格乘上对比区实际尺寸（含与
    /// <c>ArrangeOverride</c> 相同的 1px 内缩）——容差 1 DIP，吸收浮点与像素对齐误差；</description></item>
    /// <item><description>第 i 路（i ≥ 格数）被隐藏 —— 这是"格数 &lt; 路数时只显示前 N 路"策略的断言。</description></item>
    /// </list>
    ///
    /// <para>期望值由 <see cref="CompareLayout.ComputeCells"/> <b>独立复算</b>，不是回读 Grid 内部状态，
    /// 因此能真正发现"格表没被用于布局"（Bounds 仍是均匀网格）这类回归。</para></summary>
    private void AssertCellsDriveLayout(int routes, CompareMode mode, string label)
    {
        UpdateLayout(); // 强制一次布局，把 InvalidateMeasure 落地

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"{label}：对比区尺寸非法 {w:F1}x{h:F1}");

        var cells = CompareLayout.ComputeCells(mode, _compareSplit);
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");

            if (i >= cells.Length)
            {
                if (s.IsVisible)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路超出 {mode} 的 {cells.Length} 格，应被隐藏却仍可见");
                continue;
            }

            if (!s.IsVisible)
                throw new InvalidOperationException($"{label}：第 {i} 路应可见（{mode} 第 {i} 格）却不可见");

            var c = cells[i];
            var exp = ExpectedBounds(c, w, h);
            var got = s.Bounds;
            if (!BoundsClose(got, exp))
                throw new InvalidOperationException(
                    $"{label}：第 {i} 路 Bounds={got} 与 ComputeCells 期望 {exp} 不符（格表未驱动布局）");
        }

        Console.WriteLine(
            $"comparemodetest: [{label}] 格表驱动布局 ✓ 格数={cells.Length} 对比区={w:F1}x{h:F1} " +
            $"分割=({_compareSplit.X:F3},{_compareSplit.Y:F3}) 各路 Bounds 与 ComputeCells 一致");
    }

    /// <summary>把归一化格矩形换算成 <see cref="CompareGridView"/> 实际排布出的 DIP 矩形：
    /// 与 <c>ArrangeOverride</c> 一致地含 1px 内缩。供"独立复算"用，不读 Grid 内部状态。</summary>
    private static Rect ExpectedBounds(CellRect c, double areaW, double areaH) =>
        new(c.X * areaW + 1, c.Y * areaH + 1,
            Math.Max(0, c.Width * areaW - 2), Math.Max(0, c.Height * areaH - 2));

    /// <summary>DIP 矩形比较：容差 1 DIP，吸收浮点与像素对齐误差。</summary>
    private static bool BoundsClose(Rect got, Rect exp) =>
        Math.Abs(got.X - exp.X) <= 1.0 && Math.Abs(got.Y - exp.Y) <= 1.0 &&
        Math.Abs(got.Width - exp.Width) <= 1.0 && Math.Abs(got.Height - exp.Height) <= 1.0;

    /// <summary>归一化格矩形是否相等（浮点容差远小于 <see cref="CompareLayout.Epsilon"/> 量级）。</summary>
    private static bool CellEquals(CellRect a, CellRect b) =>
        Math.Abs(a.X - b.X) <= CompareLayout.Epsilon && Math.Abs(a.Y - b.Y) <= CompareLayout.Epsilon &&
        Math.Abs(a.Width - b.Width) <= CompareLayout.Epsilon &&
        Math.Abs(a.Height - b.Height) <= CompareLayout.Epsilon;

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

        // 选原点离容器原点最远的一路
        PlayerSurface? target = null;
        var targetOrigin = default(Point);
        for (var i = 0; i < routes; i++)
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
                        throw new InvalidOperationException(
                            $"{label}：TranslatePoint 与 GetWindowRect 给出的子窗口相对位移不符：" +
                            $"Avalonia Δ=({avDelta.X:F1},{avDelta.Y:F1}) Win32 Δ=({winDelta.X:F1},{winDelta.Y:F1})" +
                            "（子 HWND 未按 Bounds 落地 ⇒ 换算所依赖的几何不可信）");
                    Log($"   Win32 核对 ✓ 子窗口相对位移 Avalonia=({avDelta.X:F1},{avDelta.Y:F1}) " +
                        $"== Win32=({winDelta.X:F1},{winDelta.Y:F1})");
                }
                else
                {
                    Log($"   ⚠ 两路相对位移仅 ({avDelta.X:F1},{avDelta.Y:F1}) DIP，过小无法判别，跳过 Win32 核对");
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
        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        var applied = 0;

        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：第 {i} 路缺失");
            if (s.Hwnd == nint.Zero)
                throw new InvalidOperationException($"{label}：第 {i} 路子 HWND 未创建，裁剪无从验证");

            // 格数 < 路数时（如 4 路用 AB）多余的路被隐藏，它们不该有区域。
            if (!s.IsVisible || i >= cells.Length)
            {
                var hidden = ReadWindowRegion(s.Hwnd, out var hiddenBox);
                if (hidden != RegionTypeError)
                    throw new InvalidOperationException(
                        $"{label}：第 {i} 路已隐藏却仍有窗口区域 type={hidden} box={hiddenBox}");
                continue;
            }

            // 期望值独立复算（与生产代码同一条换算路径，但输入来自公开几何）
            var cellPx = CompareCropPlanner.ToPhysicalRect(
                CompareCropPlanner.CellToContainerDip(cells[i], w, h), scaling);
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
            $"（区域 == 格在窗口内的位置；0 路表示该几何下窗口恰好等于格、无需裁剪；" +
            $"被裁掉的是 Avalonia 向外取整产生的 ≤1px 外扩）；缩放={scaling:0.##} 对比区={w:F1}x{h:F1}DIP");
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

        Console.WriteLine(
            $"comparemodetest: [{label}] SetWindowRgn 端到端 ✓ " +
            $"格={cellDip.Width:F0}x{cellDip.Height:F0}DIP → {cellPx.Width}x{cellPx.Height}px（缩放 {scaling:0.##}）；" +
            $"放大窗口 {bigPx.Width}x{bigPx.Height}px → 下发区域 {r}（= 格在窗口内的位置）回读一致；" +
            $"Clear 后 type={cleared}(0=ERROR=无区域)");
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
        int routes, CompareMode mode, double zoom, double cropX, double cropY,
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
        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        var applied = 0;
        var letterboxSeen = 0;
        // 本步实际看到的**最小** letterbox 修正量（DIP）。打它是为了不让"修正量小到判据看不见"
        // 冒充成绿灯 —— 那等于这一项在某种几何下静默失效。
        var minCorr = double.MaxValue;

        // 像素级对齐的基准源尺寸（各路最小值）—— 与生产 <c>CellMagnify.ZoomOf/CropOf</c> 同一基准。
        // 这里由判据**自己逐路读媒体信息复算**，不回读 <c>Grid.CellMagnify</c>（那等于回读生产状态，
        // 判据就退化成"自己验自己"）。
        var routeSources = new PixelSize[routes];
        for (var i = 0; i < routes; i++) routeSources[i] = ReadSourceSize(i);
        var minSrc = CompareCropPlanner.MinSourceSize(routeSources);

        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"第 {i} 路缺失");
            if (s.Hwnd == nint.Zero) throw new InvalidOperationException($"第 {i} 路子 HWND 未创建");

            if (!s.IsVisible || i >= cells.Length)
            {
                var hidden = ReadWindowRegion(s.Hwnd, out var hiddenBox);
                if (hidden != RegionTypeError)
                    throw new InvalidOperationException($"第 {i} 路已隐藏却仍有窗口区域 type={hidden} box={hiddenBox}");
                continue;
            }

            // 期望值独立复算（输入取自公开几何 + 每路真实媒体信息）
            var src = routeSources[i];
            var cellDip = CompareCropPlanner.CellToContainerDip(cells[i], w, h);

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

            var geom = CompareCropPlanner.Magnify(cellDip, zi, ciX, ciY, src.Width, src.Height);
            var cellPx = CompareCropPlanner.ToPhysicalRect(cellDip, scaling);
            // 期望窗口的物理矩形取**换算值**（而不是回读 Bounds）：下面要拿它去核对 OS 窗口，
            // 用 Bounds 会变成"自己验证自己"。
            var winPx = CompareCropPlanner.ToPhysicalRect(geom.WindowDip, scaling);

            // ① 排版：窗口 == 换算出的放大窗口（放大真的生效）
            var got = s.Bounds;
            if (Math.Abs(got.X - geom.WindowDip.X) > 1.5 || Math.Abs(got.Y - geom.WindowDip.Y) > 1.5 ||
                Math.Abs(got.Width - geom.WindowDip.Width) > 1.5 || Math.Abs(got.Height - geom.WindowDip.Height) > 1.5)
                throw new InvalidOperationException(
                    $"第 {i} 路窗口未按放大方案排布：Bounds={got} 期望 {geom.WindowDip}（z={zoom}）");

            // 窗口尺寸必须是格的 z 倍（这是"内核按更高分辨率重渲染"的前提）
            if (Math.Abs(winPx.Width - cellPx.Width * zi) > 2 || Math.Abs(winPx.Height - cellPx.Height * zi) > 2)
                throw new InvalidOperationException(
                    $"第 {i} 路窗口尺寸 {winPx.Width}x{winPx.Height} 不是格 {cellPx.Width}x{cellPx.Height} 的 {zi} 倍");

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

            // ①c OS 窗口搬到了正确位置：屏幕坐标 == 容器屏幕原点 + 容器坐标下的期望窗口。
            //     这一条把"平移"真正钉死 —— 区域回读只证明区域被设上，证明不了窗口在哪。
            if (origin is { } o &&
                (Math.Abs(actual.X - (o.X + winPx.X)) > 2 || Math.Abs(actual.Y - (o.Y + winPx.Y)) > 2))
                throw new InvalidOperationException(
                    $"第 {i} 路子 HWND 屏幕位置 ({actual.X},{actual.Y}) != 容器原点 ({o.X},{o.Y}) + " +
                    $"期望窗口 ({winPx.X},{winPx.Y}) = ({o.X + winPx.X},{o.Y + winPx.Y})（平移未落地）");

            // ② 区域：回读 == 期望物理区域
            var expected = CompareCropPlanner.ToPhysicalRect(geom.RegionDip, scaling);
            var type = ReadWindowRegion(s.Hwnd, out var box);
            if (type is not (RegionTypeSimple or RegionTypeComplex))
                throw new InvalidOperationException($"第 {i} 路放大后应已裁剪，回读却 type={type}（0=无区域）");
            if (box != expected)
                throw new InvalidOperationException(
                    $"第 {i} 路回读区域 {box} != 期望 {expected}（换算或 DPI 不一致）");

            // ③ 区域必须**居中**落在格里：窗口原点 + 区域原点 == 格原点 + 居中偏移。
            //    补丁前偏移恒为 0（钉在左上角），补丁后 = (格 - 区域)/2（见 CompareCropPlanner.Magnify）。
            //    ⚠ 容差取 2px 而不是 1px：居中量是在 **DIP** 域算的，而窗口原点与区域原点各自
            //    经 ToPhysical 独立取整 ⇒ 两侧各差 1px 时合计可到 2px，这不是几何错、是取整域。
            //    区域小于格本身不是缺陷（宽高比失配时 fit/zoom < 格），那一圈由
            //    CompareGridView.Render 补成黑底，别把它当"留洞"。
            var expX = cellPx.X + (cellPx.Width - box.Width) / 2.0;
            var expY = cellPx.Y + (cellPx.Height - box.Height) / 2.0;
            if (Math.Abs((winPx.X + box.X) - expX) > 2 || Math.Abs((winPx.Y + box.Y) - expY) > 2)
                throw new InvalidOperationException(
                    $"第 {i} 路区域未居中落在格里：窗口原点 {winPx.X},{winPx.Y} + 区域原点 {box.X},{box.Y} " +
                    $"!= 格原点 {cellPx.X},{cellPx.Y} + 居中偏移 ({expX - cellPx.X:F1},{expY - cellPx.Y:F1})");

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
                var naive = new Rect(ciX * cellDip.Width * zi, ciY * cellDip.Height * zi,
                                     cellDip.Width, cellDip.Height);
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

        Console.WriteLine(
            $"comparemodetest: [{mode}] {label ?? "无缝放大"} ✓ z={zoom} 裁剪=({cropX:F2},{cropY:F2}) " +
            $"{applied} 路窗口放大且区域回读一致；对比区={w:F0}x{h:F0}DIP 缩放={scaling:0.##}；" +
            $"letterbox 修正生效 {letterboxSeen} 路（格宽高比 != 源宽高比" +
            (letterboxSeen > 0 ? $"，最小修正量 {minCorr:0.###}DIP" : string.Empty) + "）；" +
            $"OS 窗口尺寸/位置已按 GetWindowRect 核对{(origin is null ? "（容器屏幕原点未知，跳过位置核对）" : "")}");
        return applied;
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

    /// <summary>断言复位后子窗口回到格尺寸、放大参数清空 —— 否则退出对比模式后放大窗口会盖住相邻路。
    /// <para>同样必须 async：<c>ResetCompareMagnify</c> 只改排版，把子 HWND 搬回格尺寸的是
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
        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        for (var i = 0; i < routes; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || !s.IsVisible || i >= cells.Length) continue;
            var exp = CompareCropPlanner.CellToContainerDip(cells[i], w, h);
            var got = s.Bounds;
            if (Math.Abs(got.Width - exp.Width) > 1.5 || Math.Abs(got.Height - exp.Height) > 1.5 ||
                Math.Abs(got.X - exp.X) > 1.5 || Math.Abs(got.Y - exp.Y) > 1.5)
                throw new InvalidOperationException($"复位后第 {i} 路窗口未回到格尺寸：Bounds={got} 期望 {exp}");

            // OS 窗口也必须回到格尺寸（否则内核仍按放大后的分辨率渲染，白烧显存）
            var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
            var expPx = CompareCropPlanner.ToPhysicalRect(exp, scaling);
            var actual = ReadWindowRectPx(s.Hwnd);
            if (Math.Abs(actual.Width - expPx.Width) > 2 || Math.Abs(actual.Height - expPx.Height) > 2)
                throw new InvalidOperationException(
                    $"复位后第 {i} 路子 HWND 尺寸 {actual.Width}x{actual.Height} != 格 {expPx.Width}x{expPx.Height}");
        }

        Console.WriteLine("comparemodetest: 无缝放大复位 ✓ 子窗口回到格尺寸、放大参数已清空");
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
    private async System.Threading.Tasks.Task<int> AssertCompareWheelMagnifyAsync(int routes, CompareMode mode)
    {
        EnterCompareMode(mode);
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
                routes, mode, _compareZoom, _compareCropX, _compareCropY,
                originOverride: originBeforeMagnify, label: "滚轮无缝放大");
        }
        finally
        {
            _step = stepBeforeAssert;
        }

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
    /// <para><b>判据</b>：互换后<b>第 1 路占据原第 0 路的矩形、第 0 路占据原第 1 路的矩形</b>；
    /// 再点一次必须还原。期望值取自"互换前实测的 Bounds"，不是复算 —— 这里要验的正是
    /// "两路的位置真的被对调了"，复算会与实现同源。</para>
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

        EnterCompareMode(CompareMode.Ab);
        await System.Threading.Tasks.Task.Delay(150);
        // 非对称分割：让两格的宽高也不同，避免"只换位置没换尺寸"的错误逃过断言
        OnCompareSplitChanged(new SplitParams(0.35, 0.6));
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        var s0 = Grid.GetSurface(0) ?? throw new InvalidOperationException("互换：第 0 路缺失");
        var s1 = Grid.GetSurface(1) ?? throw new InvalidOperationException("互换：第 1 路缺失");
        var before0 = s0.Bounds;
        var before1 = s1.Bounds;

        if (BoundsClose(before0, before1))
            throw new InvalidOperationException(
                $"互换断言空转：两格矩形相同（{before0}），互换后无法区分（请检查分割参数是否生效）");

        OnSwapCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        if (!BoundsClose(s1.Bounds, before0))
            throw new InvalidOperationException(
                $"互换后第 1 路未占据原第 0 路的矩形：Bounds={s1.Bounds} 期望 {before0}");
        if (!BoundsClose(s0.Bounds, before1))
            throw new InvalidOperationException(
                $"互换后第 0 路未占据原第 1 路的矩形：Bounds={s0.Bounds} 期望 {before1}");

        // 再点一次必须还原（幂等）—— 也顺带证明"互换是可逆的呈现操作"
        OnSwapCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        if (!BoundsClose(s0.Bounds, before0))
            throw new InvalidOperationException(
                $"再次互换未还原：第 0 路 Bounds={s0.Bounds} 期望 {before0}");
        if (!BoundsClose(s1.Bounds, before1))
            throw new InvalidOperationException(
                $"再次互换未还原：第 1 路 Bounds={s1.Bounds} 期望 {before1}");

        Console.WriteLine(
            $"comparemodetest: 左右互换 ✓ 两路矩形对调后还原（{before0.Width:F0}x{before0.Height:F0} ↔ " +
            $"{before1.Width:F0}x{before1.Height:F0}）");
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
    private async System.Threading.Tasks.Task AssertCompareAlignModesAsync(int routes, CompareMode mode)
    {
        EnterCompareMode(mode);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        var savedAlign = _settings.CompareAlign;
        try
        {
            // 相对对齐（默认）：逐路结果必须等于共用值
            _settings.CompareAlign = (int)CompareAlign.Relative;
            if (!SetCompareMagnify(2.0, 0.25, 0.25))
                throw new InvalidOperationException("对齐模式测试：相对对齐下启用放大失败");
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
            if (!SetCompareMagnify(2.0, 0.25, 0.25))
                throw new InvalidOperationException("对齐模式测试：像素级对齐下启用放大失败");
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

        EnterCompareMode(CompareMode.Ab);
        await System.Threading.Tasks.Task.Delay(150);
        OnCompareSplitChanged(new SplitParams(0.35, 0.6));
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();

        var s0 = Grid.GetSurface(0) ?? throw new InvalidOperationException("轮换：第 0 路缺失");
        var s1 = Grid.GetSurface(1) ?? throw new InvalidOperationException("轮换：第 1 路缺失");
        var start0 = s0.Bounds;
        var start1 = s1.Bounds;

        OnRotateCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        // ① 轮换生效的正确观测物：**第 1 路搬进了第 0 路原来的那一格**。
        // ⚠ 这里不能再拿"第 0 路的 Bounds 变没变"当代理 —— 格数少于路数时（3 路用 AB）第 0 路
        //   会掉出格表，而 Avalonia 对 `IsVisible=false` 的控件**不回写 Bounds**：
        //   ArrangeOverride 里那句 `s.Arrange(new Rect(0,0,0,0))` 不落地，Bounds 停在旧值
        //   （2026-09-26 首次执行到本步的判红读数：映射确实是 [1,2]、Grid.CellRoute 也是 [1,2]，
        //   只有第 0 路的 Bounds 没动 ⇒ 空转的是判据，不是产品）。
        var cellCount = CompareLayout.CellCount(_compareMode);
        if (!BoundsClose(s1.Bounds, start0))
        {
            FailReadings($"轮换后第 1 路没有占据第 0 格（s1.Bounds={s1.Bounds} 期望={start0}）：轮换未生效");
        }
        if (routes > cellCount)
        {
            // 掉出格表的那一路必须不可见（这就是"被撤下"的可观测后果）
            if (s0.IsVisible)
                FailReadings($"轮换后第 0 路仍在显示（格数={cellCount} < 路数={routes}，它不该有格）");
        }
        else if (BoundsClose(s0.Bounds, start0))
        {
            // 路数 == 格数时每一路都有格可站 ⇒ 位置必须真的变
            FailReadings($"轮换后第 0 路位置未变（Bounds={s0.Bounds}）：轮换未生效（空转）");
        }

        void FailReadings(string why)
        {
            // 只记录读数、不改判决：OnRotateCompareCells 有四个提前 return 的闸门
            //（_compareActive / 叠加 / 格数<2 / 路数<2），红必须说清卡在哪个。
            Log($"[读数] 轮换判据：{why} ｜ Grid.Count={Grid.Count} _sync.Count={_sync.Count} " +
                $"_compareActive={_compareActive} 叠加={CompareOverlayActive} 模式={_compareMode} " +
                $"格数={cellCount} 路数={routes} " +
                $"_compareRoute=[{(_compareRoute is null ? "null" : string.Join(",", _compareRoute))}] " +
                $"Grid.CellRoute=[{(Grid.CellRoute is null ? "null" : string.Join(",", Grid.CellRoute))}] " +
                $"第0路 前={start0} 后={s0.Bounds} 可见={s0.IsVisible} ｜ 第1路 前={start1} 后={s1.Bounds}");
            throw new InvalidOperationException(why);
        }
        // ③ 每一格的占用者必须是"格号 + 1 mod 路数"（映射是合法排列）。
        //    ⚠ 判据必须看"哪一路站到了这一格上"，不能看"两格的矩形互不相同"：
        //    格数 < 路数时被撤下的那一路带着**陈旧 Bounds**（见 ① 的说明），
        //    于是它与真正占着那一格的那一路读出同一个矩形 ⇒ 旧写法在 3/4 路假红
        //    （原判据 2026-09-26 首次执行即判红："两格矩形相同 (1.33,1.33,464.67,748)"）。
        var cellRects = new[] { start0, start1 };
        for (var c = 0; c < cellRects.Length; c++)
        {
            var expectRoute = (c + 1) % routes;
            var occ = Grid.GetSurface(expectRoute)
                      ?? throw new InvalidOperationException($"轮换：第 {expectRoute} 路缺失");
            if (!BoundsClose(occ.Bounds, cellRects[c]))
                FailReadings($"轮换后第 {c} 格应由第 {expectRoute} 路占据：" +
                             $"该路 Bounds={occ.Bounds} 期望={cellRects[c]}（映射不是合法排列，或被撤下的路没让位）");
        }

        // ② 再轮换 (routes-1) 次 ⇒ 共 routes 次，必须回到起点
        for (var i = 1; i < routes; i++)
            OnRotateCompareCells(this, new Avalonia.Interactivity.RoutedEventArgs());
        UpdateLayout();
        await System.Threading.Tasks.Task.Delay(200);
        UpdateLayout();

        if (!BoundsClose(s0.Bounds, start0))
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 0 路未回到起点：Bounds={s0.Bounds} 期望 {start0}" +
                "（周期应为路数；若等于格数，则格数 < 路数时永远轮换不到没显示的路）");
        // "回到起点"必须是一次**真实的往返**而不是陈旧读数：格数 < 路数时第 0 路在周期中途
        // 是不可见的（①已判过），所以这里要它重新可见 —— 否则本条会因 Bounds 不回写而构造恒等。
        if (!s0.IsVisible)
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 0 路仍不可见：中途被撤下后没有真的回到格上");
        if (!BoundsClose(s1.Bounds, start1))
            throw new InvalidOperationException(
                $"轮换 {routes} 次后第 1 路未回到起点：Bounds={s1.Bounds} 期望 {start1}");

        Console.WriteLine($"comparemodetest: 轮换画面 ✓ 周期 = 路数 {routes}（可轮换到全部 {routes} 路）");
    }

    /// <summary>模式变体（AB 竖 / ABC 三列）。
    ///
    /// <para><b>为什么数据驱动的模式循环不够</b>：<c>AvailableModes</c> 的循环确实会切到这两个变体，
    /// 但 <c>AssertCellsDriveLayout</c> 只验"各路 Bounds == 格表"，对<b>变体的定义性形状</b>没有
    /// 鉴别力 —— 若 <c>ComputeCells</c> 漏改分支（落进 default 画成左右两格），格数仍然吻合，
    /// 那条断言<b>照样判绿</b>（C# 对带 <c>_ =&gt;</c> 兜底的 switch 不报 CS8509，编译期也没有保护）。
    /// 故这里补两条"形状"断言：竖分必须<b>通宽</b>、三列必须<b>通高</b>。</para>
    ///
    /// <para>再加一条覆盖层判据：三列必须有<b>两个</b>手柄（两条竖线各一个），
    /// 且手柄必须落在格的右边界上 —— 否则第二条线"可拖但看不见抓手"。</para></summary>
    private async System.Threading.Tasks.Task AssertCompareModeVariantsAsync(int routes)
    {
        var available = CompareLayout.AvailableModes(routes);

        // 覆盖范围先说清楚：ABC 三列那一支在 2 路下不会执行（AvailableModes 不含它）。
        // 不写出来的话，一句 "对比-模式变体 ✓" 会让人以为两个变体都验过了。
        Console.WriteLine(
            $"  模式变体 覆盖范围：AB 竖={(available.Contains(CompareMode.AbVertical) ? "跑" : "**跳过**")}" +
            $" + ABC 三列={(available.Contains(CompareMode.AbcColumns) ? "跑" : "**跳过**")}" +
            $"（{routes} 路可用模式=[{string.Join(", ", available)}]）");

        if (available.Contains(CompareMode.AbVertical))
        {
            EnterCompareMode(CompareMode.AbVertical);
            await System.Threading.Tasks.Task.Delay(200);
            UpdateLayout();
            AssertCellsDriveLayout(routes, CompareMode.AbVertical, "AB 竖");

            var cells = CompareLayout.ComputeCells(CompareMode.AbVertical, _compareSplit);
            if (cells.Length != 2) throw new InvalidOperationException($"AB 竖应是 2 格，实际 {cells.Length}");
            // 定义性形状：两格通宽、上下相接 ⇒ 漏改成"左右分"时宽度不是 1，立刻判红
            if (Math.Abs(cells[0].Width - 1.0) > 1e-6 || Math.Abs(cells[1].Width - 1.0) > 1e-6)
                throw new InvalidOperationException(
                    $"AB 竖的两格未通宽（{cells[0].Width:0.###} / {cells[1].Width:0.###}）—— 被画成了左右分栏");
            if (Math.Abs(cells[0].Height + cells[1].Height - 1.0) > 1e-6)
                throw new InvalidOperationException("AB 竖的两格高度之和 != 1（未上下相接）");

            if (CompareLayout.HandleAxisAt(CompareMode.AbVertical, 0) != SplitAxis.Y)
                throw new InvalidOperationException("AB 竖的手柄轴不是横分割（SplitAxis.Y）");

            var handles = _layoutOverlay?.HandlePositions()
                          ?? throw new InvalidOperationException("覆盖层未创建");
            if (handles.Length != 1)
                throw new InvalidOperationException($"AB 竖应只有 1 个手柄，实际 {handles.Length}");
            if (Math.Abs(handles[0].X - 0.5) > 1e-6)
                throw new InvalidOperationException($"AB 竖的手柄不在横线中点：X={handles[0].X:0.###}");

            Console.WriteLine("comparemodetest: AB 竖 ✓ 两格通宽上下相接、手柄在横线中点");
        }

        if (available.Contains(CompareMode.AbcColumns))
        {
            EnterCompareMode(CompareMode.AbcColumns);
            await System.Threading.Tasks.Task.Delay(200);
            UpdateLayout();
            AssertCellsDriveLayout(routes, CompareMode.AbcColumns, "ABC 三列");

            var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, _compareSplit);
            if (cells.Length != 3) throw new InvalidOperationException($"ABC 三列应是 3 格，实际 {cells.Length}");
            // 定义性形状：三格通高、左右相接
            for (var i = 0; i < cells.Length; i++)
                if (Math.Abs(cells[i].Height - 1.0) > 1e-6)
                    throw new InvalidOperationException(
                        $"ABC 三列第 {i} 格未通高（{cells[i].Height:0.###}）—— 被画成了非三列布局");
            if (Math.Abs(cells[0].X) > 1e-6 ||
                Math.Abs(cells[0].X + cells[0].Width - cells[1].X) > 1e-6 ||
                Math.Abs(cells[1].X + cells[1].Width - cells[2].X) > 1e-6 ||
                Math.Abs(cells[2].X + cells[2].Width - 1.0) > 1e-6)
                throw new InvalidOperationException("ABC 三列的三格未左右相接铺满");

            // 两条竖线 ⇒ 两个手柄，且都落在对应格的右边界上（与几何同源）
            var handles = _layoutOverlay?.HandlePositions()
                          ?? throw new InvalidOperationException("覆盖层未创建");
            if (handles.Length != 2)
                throw new InvalidOperationException(
                    $"ABC 三列应有 2 个手柄（两条竖线），实际 {handles.Length}");
            if (Math.Abs(handles[0].X - handles[1].X) < 1e-6)
                throw new InvalidOperationException("ABC 三列的两个手柄重合（用户无法分别抓住）");
            for (var i = 0; i < 2; i++)
            {
                var edge = cells[i].X + cells[i].Width;
                if (Math.Abs(handles[i].X - edge) > 1e-6)
                    throw new InvalidOperationException(
                        $"ABC 三列第 {i} 个手柄（X={handles[i].X:0.###}）与第 {i} 列右边界（{edge:0.###}）不重合");
            }

            Console.WriteLine(
                $"comparemodetest: ABC 三列 ✓ 三格通高左右相接、两个手柄分别在 " +
                $"{handles[0].X:0.###} / {handles[1].X:0.###}");
        }

        // 变体断言会切模式，末尾回到本段的起始模式（maxMode 由调用方决定），
        // 避免后续"退出对比模式"那一段拿到的模式与它进入时的不一致。
        EnterCompareMode(available[available.Count - 1]);
        await System.Threading.Tasks.Task.Delay(150);
        UpdateLayout();
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
        if (!_compareActive || _compareMode != CompareMode.Ab)
            throw new InvalidOperationException(
                $"{label}：叠加必须建立在对比模式 AB 之上（当前 active={_compareActive} mode={_compareMode}）");

        // 用一个确定的分割位置，再走生产路径下发区域（期望值见下）
        var split = new SplitParams(0.7, 0.5);
        OnCompareSplitChanged(split);
        UpdateLayout();
        ApplyCompareCrop();

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        var a = Grid.GetSurface(0) ?? throw new InvalidOperationException($"{label}：第 0 路缺失");
        var b = Grid.GetSurface(1) ?? throw new InvalidOperationException($"{label}：第 1 路缺失");
        if (a.Hwnd == nint.Zero || b.Hwnd == nint.Zero)
            throw new InvalidOperationException($"{label}：子 HWND 未创建，叠加无从验证");

        // ① 两路都铺满整个对比区（期望值由 ExpectedBounds 独立复算，不读 Grid 的内部格表）
        var full = ExpectedBounds(new CellRect(0, 0, 1, 1), w, h);
        if (!BoundsClose(a.Bounds, full))
            throw new InvalidOperationException($"{label}：A 路未铺满对比区：Bounds={a.Bounds} 期望 {full}");
        if (!BoundsClose(b.Bounds, full))
            throw new InvalidOperationException($"{label}：B 路未铺满对比区：Bounds={b.Bounds} 期望 {full}");
        for (var i = 2; i < routes; i++)
            if (Grid.GetSurface(i)?.IsVisible != false)
                throw new InvalidOperationException($"{label}：叠加模式只呈现前 2 路，第 {i} 路却仍可见");

        // ②③④ 两路区域互补（期望值独立复算 + Win32 回读核对）。两路铺满 ⇒ 窗口物理矩形应当一致。
        var winA = CompareCropPlanner.ToPhysicalRect(a.Bounds, scaling);
        var winB = CompareCropPlanner.ToPhysicalRect(b.Bounds, scaling);
        if (Math.Abs(winA.Width - winB.Width) > 1 || Math.Abs(winA.Height - winB.Height) > 1)
            throw new InvalidOperationException(
                $"{label}：两路窗口物理尺寸不一致（A={winA.Width}x{winA.Height} B={winB.Width}x{winB.Height}）");

        var applied = AssertOverlayComplement(label, "水平 split=0.70", a.Hwnd, b.Hwnd, winA, winB,
            split.X, vertical: false);

        // 拖动分割线必须驱动揭示区 —— 这正是"滑块揭示"的链路（OnCompareSplitChanged 即覆盖层回调）
        var dragSplit = new SplitParams(0.25, 0.5);
        OnCompareSplitChanged(dragSplit);
        UpdateLayout();
        ApplyCompareCrop();
        applied += AssertOverlayComplement(label, "拖动 split=0.25", a.Hwnd, b.Hwnd, winA, winB,
            dragSplit.X, vertical: false);

        // 垂直揭示：同一份逻辑只换矩形（内部方向开关；两个方向都由 _compareSplit.X 驱动）
        SetCompareOverlayAxis(OverlayAxis.Vertical);
        UpdateLayout();
        ApplyCompareCrop();
        applied += AssertOverlayComplement(label, "垂直 split=0.25", a.Hwnd, b.Hwnd, winA, winB,
            dragSplit.X, vertical: true);

        SetCompareOverlayAxis(OverlayAxis.Horizontal);
        UpdateLayout();
        ApplyCompareCrop();

        // 画出来的分割线必须落在揭示接缝上。两者**不同源**：线由覆盖层拿自己的 DIP 宽度乘 split 画，
        // 接缝是子 HWND 物理矩形内的 px 分界（还要过 +1 内缩与取整）。差在 1px 级是几何必然，
        // 但"线在这儿、画面接缝在那儿"两道是分得出来的 —— 用 ≤1.5 DIP 把这条界钉住，
        // 真漂了就有读数，而不是留一条"可能差 1px"的口头账。
        var seamType = ReadWindowRegion(b.Hwnd, out var seamBox);
        if (seamType is not (RegionTypeSimple or RegionTypeComplex))
            throw new InvalidOperationException($"{label}：取线与接缝的对齐读数时 B 路没有区域（type={seamType}）");
        var seamDip = (winB.X + seamBox.X + seamBox.Width) / scaling;
        var lineDip = _compareSplit.X * (_layoutOverlay?.Bounds.Width
            ?? throw new InvalidOperationException($"{label}：覆盖层不存在，无从取得画线位置"));
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
        var abCells = CompareLayout.ComputeCells(CompareMode.Ab, _compareSplit);
        for (var i = 0; i < Math.Min(routes, abCells.Length); i++)
        {
            var s = Grid.GetSurface(i) ?? throw new InvalidOperationException($"{label}：退出叠加后第 {i} 路缺失");
            var exp = ExpectedBounds(abCells[i], w, h);
            if (!BoundsClose(s.Bounds, exp))
                throw new InvalidOperationException(
                    $"{label}：退出叠加后第 {i} 路 Bounds {s.Bounds} != AB 左右分栏期望 {exp}");
        }
        Console.WriteLine(
            $"comparemodetest: [{label}] 退出后区域已清除、已回到 AB 左右分栏 ✓（各路 GetWindowRgn 均为 ERROR）");
        return applied;
    }

    /// <summary>断言叠加模式下两路区域<b>互补且互不重叠</b>：B 铺 <c>[0, splitPx]</c>、
    /// A 铺 <c>[splitPx, span]</c> —— 无缝、无重叠、并集 = 整窗。这正是"可见结果与 Z 序无关"的
    /// 全部依据：重叠会让 Z 序重新决定可见性，留缝会露出父窗口底色，两者都是用户直接看得见的不对。
    ///
    /// <para>期望值独立复算（窗口物理矩形 + split 比例取整 + 与生产同一套 <c>[1, span-1]</c> 钳制），
    /// 再与 <c>GetWindowRgn</c>/<c>GetRgnBox</c> 回读的矩形逐项核对。生产代码不维护区域状态，
    /// 故这不是自证。</para></summary>
    /// <returns>本轮核对通过的区域数（恒为 2：A 与 B 各一处）。</returns>
    private static int AssertOverlayComplement(string label, string what, nint hwndA, nint hwndB,
        Rect32 winA, Rect32 winB, double split, bool vertical)
    {
        var span = vertical ? winB.Height : winB.Width;   // 分割轴上的边长
        var px = (int)Math.Round(split * span, MidpointRounding.AwayFromZero);
        if (px < 1) px = 1;               // 与 OverlayRevealPlan 同一套钳制（split=0 ⇒ B 只余 1px）
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
