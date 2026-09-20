using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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
    private void ExitSelfTest(int code)
    {
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

        var col = MainArea.ColumnDefinitions[0].Width;
        var expect = _sidebar.Collapsed ? Controls.ToolsSidebar.RailWidth : _sidebar.ExpandedWidth;
        if (!col.IsAbsolute || Math.Abs(col.Value - expect) > 0.5)
            throw new InvalidOperationException($"侧栏宽度异常：实际={col.Value:0.#} 期望={expect:0.#}");

        Log($"布局 ✓ 时间轴({timeline:0.#}) → 传输栏({transport:0.#}) → 状态栏({status:0.#})，侧栏 {col.Value:0.#}px{( _sidebar.Collapsed ? "（折叠）" : string.Empty)}");
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
            Width = widthBefore + 120;
            await System.Threading.Tasks.Task.Delay(400);
            AssertFloatingTransportGeometry(win, "resize 后");
            Width = widthBefore;
            await System.Threading.Tasks.Task.Delay(400);
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

    private async System.Threading.Tasks.Task RunSelftestAsync(string videoPath, string? dropVideoPath = null)
    {
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
                    // 给一个不依赖 UI 线程的硬兜底：5s 内没退出就强制结束进程。
                    // 走到这里时日志已打印出 [_step]，排障信息不会丢。
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(5000);
                        Console.Error.WriteLine($"selftest: 看门狗兜底硬退出（Post 未被执行，步骤 [{last}]）");
                        Console.Error.Flush();
                        Environment.Exit(3);
                    });
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
            _step = "探针映射";
            {
                var surface = Grid.GetSurface(0);
                if (surface is null || media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0)
                {
                    Log("⚠ 无表面/媒体信息，跳过探针映射断言");
                }
                else
                {
                    var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                    var sw = surface.Bounds.Width * scale;
                    var sh = surface.Bounds.Height * scale;
                    if (sw <= 0 || sh <= 0)
                        throw new InvalidOperationException("表面尚未布局（尺寸为 0），无法验证探针映射");

                    var q = _3FCompare.Core.Display.VideoPixelMap.MapFallback(
                        sw * 0.25, sh * 0.25, sw, sh, media.VideoWidth, media.VideoHeight);
                    if (q is null) throw new InvalidOperationException("探针兜底映射返回 null");
                    var ex = (int)(media.VideoWidth * 0.25);
                    var ey = (int)(media.VideoHeight * 0.25);
                    Log($"探针兜底映射 1/4 处 → ({q.Value.X},{q.Value.Y}) 期望≈({ex},{ey}) 源 {media.VideoWidth}x{media.VideoHeight}");
                    if (Math.Abs(q.Value.X - ex) > 2 || Math.Abs(q.Value.Y - ey) > 2)
                        throw new InvalidOperationException(
                            $"探针兜底映射异常 ({q.Value.X},{q.Value.Y})，期望≈({ex},{ey})（C4 回归：分母又退化了？）");

                    // 主路径（有 RenderTargetInfo）：中心点必须落在源范围内（letterbox 时返回 null）
                    var c = MapPointerToVideoPixel(surface, _sync.Slots[0].Session,
                        new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2));
                    if (c is null)
                        Log("⚠ 中心点落在 letterbox 或无诊断信息，跳过主路径断言");
                    else
                    {
                        Log($"探针主路径映射 中心 → ({c.Value.X},{c.Value.Y})");
                        if (c.Value.X < 0 || c.Value.X >= media.VideoWidth ||
                            c.Value.Y < 0 || c.Value.Y >= media.VideoHeight)
                            throw new InvalidOperationException($"探针主路径映射越界 ({c.Value.X},{c.Value.Y})");

                        // 坐标域回归（2026-09-16 实测新发现）：
                        // 内核 FFF3FP_ReadVideoPixel 的坐标域是**后台缓冲**，不是片源分辨率
                        // （4K 与 720p 片源下越界边界恒等于缓冲尺寸）。片源中心换算后必须落在
                        // destination 矩形中心；若把片源坐标直传内核，会落到缓冲右下角读成黑。
                        if (_sync.Slots[0].Session.ReadRenderTargetInfo(out var rt2) &&
                            rt2.DestWidth > 0 && rt2.DestHeight > 0 && rt2.SwapWidth > 0)
                        {
                            var bc = _3FCompare.Core.Backend.PixelReadback.SourceToBackBuffer(
                                c.Value.X, c.Value.Y, media.VideoWidth, media.VideoHeight, rt2);
                            if (bc is null)
                                throw new InvalidOperationException("源→缓冲坐标换算返回 null");
                            var expX = (int)(rt2.DestX + rt2.DestWidth / 2);
                            var expY = (int)(rt2.DestY + rt2.DestHeight / 2);
                            Log($"坐标域换算 源中心({c.Value.X},{c.Value.Y}) → 缓冲({bc.Value.X},{bc.Value.Y}) " +
                                $"期望≈({expX},{expY}) 缓冲 {rt2.SwapWidth}x{rt2.SwapHeight}");
                            if (Math.Abs(bc.Value.X - expX) > 2 || Math.Abs(bc.Value.Y - expY) > 2)
                                throw new InvalidOperationException(
                                    $"探针读取坐标域错误：换算到 ({bc.Value.X},{bc.Value.Y})，期望≈({expX},{expY})" +
                                    $"（又把片源坐标直传内核了？）");
                            if (bc.Value.X < 0 || bc.Value.X >= rt2.SwapWidth ||
                                bc.Value.Y < 0 || bc.Value.Y >= rt2.SwapHeight)
                                throw new InvalidOperationException(
                                    $"探针读取坐标越界 ({bc.Value.X},{bc.Value.Y})，缓冲 {rt2.SwapWidth}x{rt2.SwapHeight}");
                        }
                    }
                }
            }

            // 放大镜 vs 探针 交叉验证（docs/15 §2.2 遗留的"未实测"项）
            // 两者是"取光标下像素"的两份实现。本轮 P0 缺陷正是放大镜坐标算错
            // 而探针一直是对的；只验证坐标公式不够（公式对了也可能传错参数），
            // 用**真实像素值**比对才能证明放大镜显示的就是光标下的内容。
            // 单路即可做，不依赖多路媒体，因此不受已知的多路崩溃阻塞。
            _step = "放大镜-探针一致性";
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
                        Magnifier.UpdateAt(cand.Local, scale);
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
                else if (midState != PlayerState.Playing)
                {
                    Log($"⚠ 播放状态变为 {midState}（非卡死）");
                }
                else
                {
                    Log($"✅ 压力测试通过：20 次快速变换后视频继续播放 (presented +{presentDelta}/秒)");
                }

                // 恢复正常视图并继续播放
                _sync.SetViewTransform(1.0f, 0f, 0f);
                _sync.Play();
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
                var zoomBefore = _viewZoom;
                var droppedBefore = TransformDroppedCount;

                // 同一 tick 内连发 4 次（无 await）：第 1 次立即下发，后 3 次间隔 0ms < 16ms
                // 必然被丢弃。刻意不用 Task.Delay(5)——Windows 上其实际分辨率约 15ms，
                // 用例会变成时灵时不灵。同步连发是确定性的。
                for (var i = 0; i < 4; i++) OnSurfaceWheel(120);

                var dropped = TransformDroppedCount - droppedBefore;
                Log($"连发 4 次：_viewZoom={_viewZoom:F3} 已下发={LastSentZoom:F3} 丢弃={dropped}");
                // 前置条件坐实：没有一次被丢弃的话，下面的断言恒绿、毫无意义
                if (dropped < 1)
                    throw new InvalidOperationException(
                        "节流用例空转：没有更新被丢弃（连发被改成异步间隔了？断言已失效）");

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
        if (snaps.Count != routes) return;
        var masterPos = _sync.GetMasterPosition100ns();
        const long Tolerance = 100_0000; // 100ms
        var checkedRoutes = 0;
        for (var i = 0; i < snaps.Count; i++)
        {
            if (i >= _multitestActiveRoutes) continue;
            var snap = snaps[i];
            if (snap is null || _sync.Slots[i].Failed) continue;
            var expect = masterPos + _sync.Slots[i].Offset100ns;
            var drift = Math.Abs(snap.Position100ns - expect);
            if (drift > Tolerance)
                throw new InvalidOperationException(
                    $"第 {i} 路漂移 {TimeSpan.FromTicks(drift):g} > 100ms（pos={TimeSpan.FromTicks(snap.Position100ns):g} 期望 {TimeSpan.FromTicks(expect):g}）");
            checkedRoutes++;
        }
        Console.WriteLine($"multitest[{phase}]: 漂移 OK（{checkedRoutes} 路 ≤100ms）✓");
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
    private async System.Threading.Tasks.Task RunCompareModeTestAsync(string videoPath, int routes)
    {
        routes = Math.Clamp(routes, 2, 9);
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
            AssertCompareOverlay(routes, CompareMode.Ab);
            AssertCellsDriveLayout(routes, CompareMode.Ab, "进入 AB");
            // 裁剪（SetWindowRgn）接线：区域必须真的被下发且回读一致，Win32 路径另做端到端往返
            cropApplied += AssertCompareCrop(routes, "进入 AB");
            AssertCropWin32RoundTrip("进入 AB");

            // 逐级切到可用集合中的每个模式，验证"按路数自动收敛"与"格表随模式改变"
            var modes = CompareLayout.AvailableModes(routes);
            Console.WriteLine($"comparemodetest: 路数={routes} 可用模式=[{string.Join(", ", modes)}]");
            foreach (var m in modes)
            {
                _step = $"对比-切换 {m}";
                EnterCompareMode(m);
                await System.Threading.Tasks.Task.Delay(150);
                AssertCompareOverlay(routes, m);
                AssertCellsDriveLayout(routes, m, $"模式 {m}");
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
            EnterCompareMode(maxMode);
            await System.Threading.Tasks.Task.Delay(150);
            UpdateLayout();

            var oldSplit = _compareSplit;                 // 进入模式后 = 该模式的默认初值
            var newSplit = new SplitParams(0.7, 0.35);
            var oldCells = CompareLayout.ComputeCells(maxMode, oldSplit);
            var newCells = CompareLayout.ComputeCells(maxMode, newSplit);

            // 前提：这组新旧 split 必须真的改变格表，否则下面的断言退化为"恒真"（无鉴别力）。
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

            // 逐路核对：实际 Bounds 必须整体从"旧 split 期望"迁到"新 split 期望"。
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
            // 分割参数变化是裁剪的必设时机之一：确认它被真的走了一遍
            cropApplied += AssertCompareCrop(routes, "分割 0.70/0.35");
            Console.WriteLine(
                $"comparemodetest: 分割驱动重排 ✓ {maxMode} 第0路 Bounds {before.Width:F1}x{before.Height:F1} → " +
                $"{after.Width:F1}x{after.Height:F1}（与 ComputeCells 复算的 {oldSplit} → {newSplit} 期望一致）");

            // ── 无缝放大（子窗口放大 + 偏移 + 裁剪）：默认关闭，这里显式启用后逐项验证 ──
            // 必须放在"退出"之前、且结束前复位：否则后续断言（期望 Bounds == 格）会失败。
            AssertCompareMagnifyGates(routes);
            cropApplied += await AssertCompareMagnifyAsync(routes, maxMode, zoom: 2.0, cropX: 0.25, cropY: 0.25);
            await AssertCompareMagnifyResetAsync(routes);

            // ── 叠加模式（对标 ICAT Single Screen，docs/31 阶段 2）──
            // 放在放大复位之后、退出之前：进入叠加会切到 AB 并复位放大；退出叠加后回到 AB 左右分栏，
            // 因此后面既有的"退出对比模式"断言（含 AssertCompareCropCleared）不受影响。
            cropApplied += await AssertCompareOverlayAsync(routes, "叠加");

            // ── 模式家族入口（docs/31 阶段 3）：菜单 / 快捷键的三项入口必须真的可用 ──
            // 这一段专盯用户点名的 5~9 路场景：
            //   ① 分屏入口按路数收敛（≥4 路 → ABCD），且**只显示前 4 路**（第 5~9 路隐藏）；
            //   ② 网格入口退出对比模式 ⇒ **全部路数恢复可见**，排版与 GridLayout.ComputeGrid
            //      独立复算的结果一致（9 路 = 3×3）；
            //   ③ 退出后区域 / 覆盖层复原（本段末尾 + 下面的"对比-退出"各钉一次）。
            // 期望值一律来自 Core 的纯函数（CoerceMode / CellCount / ComputeGrid），不回读 Grid 内部状态。
            _step = "模式-分屏入口";
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
            for (var i = splitCells; i < routes; i++)
                if (Grid.GetSurface(i)?.IsVisible != false)
                    throw new InvalidOperationException(
                        $"分屏入口：{routes} 路收敛到 {splitTarget}（{splitCells} 格），第 {i} 路应隐藏却仍可见");
            Console.WriteLine(
                $"comparemodetest: 分屏入口 ✓ {routes} 路收敛到 {splitTarget}（显示前 {splitCells} 路，其余隐藏）");

            // 幂等：已处于目标分屏模式时再点"分屏"不得重进（EnterCompareMode 会重置分割参数，
            // 用户拖好的位置不该被抹掉）。这里模拟一次用户拖动，再走一遍入口。
            var draggedSplit = new SplitParams(0.31, 0.62);
            OnCompareSplitChanged(draggedSplit);
            UpdateLayout();
            EnterSplitMode();
            // 直接比 X / Y（CellEquals 只比 CellRect，SplitParams 是另一类型）
            if (Math.Abs(_compareSplit.X - draggedSplit.X) > CompareLayout.Epsilon ||
                Math.Abs(_compareSplit.Y - draggedSplit.Y) > CompareLayout.Epsilon)
                throw new InvalidOperationException(
                    $"分屏入口重进后分割参数被重置：{_compareSplit} ≠ 用户拖动后的 {draggedSplit}");
            Console.WriteLine("comparemodetest: 分屏入口幂等 ✓ 已在该模式时未重置分割参数");

            // ── 网格入口：必须退出对比模式，并把 2~9 路全部恢复成均匀网格 ──
            _step = "模式-网格入口";
            EnterGridMode();
            await System.Threading.Tasks.Task.Delay(150);

            if (_compareActive || CompareOverlayActive)
                throw new InvalidOperationException("网格入口后仍处于对比 / 叠加模式");
            if (Grid.CellOverride is not null)
                throw new InvalidOperationException("网格入口后 Grid.CellOverride 未清空（均匀网格未恢复）");
            UpdateLayout();

            // 排版期望值**独立复算**：GridLayout.ComputeGrid 是 Core 纯函数，不读 Grid 的内部预设状态。
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
                $"comparemodetest: 网格入口 ✓ {routes} 路全部恢复可见，排版与 GridLayout.ComputeGrid({routes})=" +
                $"{gCols}x{gRows} 独立复算一致");

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
            AssertCompareCropCleared(routes, "退出");

            // 最后兜一刀：整轮跑完必须至少真的下发过一次区域。否则"裁剪接线"只是纸面通过 ——
            // 换算、时机、DPI、清除全对却一次都没走，等于没接线。
            if (cropApplied <= 0)
                throw new InvalidOperationException(
                    "整轮对比模式从未真正下发过窗口区域（裁剪接线未被走到，属静默失效）");
            Console.WriteLine($"comparemodetest: 裁剪下发累计 {cropApplied} 次（>0 表示生产路径确实被走到）✓");

            Console.WriteLine("comparemodetest: 全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"comparemodetest[步骤{_step}]: 失败 ✗ {ex.Message}");
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
                Console.WriteLine($"magnifybench: 闸门拒绝 z={zoom}（未做放大阶段采样）");
                Console.WriteLine("magnifybench: 完成 ✓");
                code = 0;
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
    private async System.Threading.Tasks.Task<int> AssertCompareMagnifyAsync(
        int routes, CompareMode mode, double zoom, double cropX, double cropY)
    {
        _step = $"无缝放大 z={zoom}";

        // 放大前先记下"对比区左上角在屏幕上的物理坐标"：由某一路的屏幕矩形减去该路的容器坐标矩形得出。
        // 有了它，放大后就能把子 HWND 的屏幕位置与"容器坐标下的期望窗口"直接对比 —— 这是唯一能在
        // 应用内验证"窗口真的被平移到了正确位置"的办法（区域回读只证明区域被设上，证明不了窗口在哪）。
        var origin = CaptureContainerScreenOrigin();

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

            // 期望值独立复算（输入取自公开几何 + 演示引擎的媒体信息）
            var src = ReadSourceSize(i);
            var cellDip = CompareCropPlanner.CellToContainerDip(cells[i], w, h);
            var geom = CompareCropPlanner.Magnify(cellDip, zoom, cropX, cropY, src.Width, src.Height);
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
            if (Math.Abs(winPx.Width - cellPx.Width * zoom) > 2 || Math.Abs(winPx.Height - cellPx.Height * zoom) > 2)
                throw new InvalidOperationException(
                    $"第 {i} 路窗口尺寸 {winPx.Width}x{winPx.Height} 不是格 {cellPx.Width}x{cellPx.Height} 的 {zoom} 倍");

            // ①b 真正落到 OS 窗口上：GetWindowRect 读回的子 HWND 尺寸必须也是 z 倍。
            //     只断言 Avalonia 的 Bounds 不够 —— Bounds 是托管侧排布结果，
            //     而"swapchain 变大 ⇒ 内核按更高分辨率重渲染"取决于 Win32 窗口真的被 MoveWindow 放大。
            var actual = ReadWindowRectPx(s.Hwnd);
            if (Math.Abs(actual.Width - winPx.Width) > 2 || Math.Abs(actual.Height - winPx.Height) > 2)
                throw new InvalidOperationException(
                    $"第 {i} 路子 HWND 实际尺寸 {actual.Width}x{actual.Height} != 期望 {winPx.Width}x{winPx.Height}" +
                    "（Avalonia 排布了但没落到 Win32 窗口 ⇒ 内核拿不到更大的 swapchain）");

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

            // ③ 不打洞：窗口原点 + 区域原点 == 格原点（容差 1px：DIP→物理各自取整）
            if (Math.Abs((winPx.X + box.X) - cellPx.X) > 1 || Math.Abs((winPx.Y + box.Y) - cellPx.Y) > 1)
                throw new InvalidOperationException(
                    $"第 {i} 路区域与格不重合：窗口原点 {winPx.X},{winPx.Y} + 区域原点 {box.X},{box.Y} " +
                    $"!= 格原点 {cellPx.X},{cellPx.Y} —— 会在格内留洞");

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
                var naive = new Rect(cropX * cellDip.Width * zoom, cropY * cellDip.Height * zoom,
                                     cellDip.Width, cellDip.Height);
                if (RectApproximately(geom.RegionDip, naive))
                    throw new InvalidOperationException(
                        $"第 {i} 路画面被 letterbox（fit={geom.FitDip} 窗口={geom.WindowDip}），" +
                        $"区域 {geom.RegionDip} 却等于朴素换算 {naive} —— letterbox 修正未生效");
            }

            applied++;
        }

        if (applied == 0)
            throw new InvalidOperationException($"z={zoom} 下没有任何一路真正进入放大状态（接线未走到）");

        Console.WriteLine(
            $"comparemodetest: [{mode}] 无缝放大 ✓ z={zoom} 裁剪=({cropX:F2},{cropY:F2}) " +
            $"{applied} 路窗口放大且区域回读一致；对比区={w:F0}x{h:F0}DIP 缩放={scaling:0.##}；" +
            $"letterbox 修正生效 {letterboxSeen} 路（格宽高比 != 源宽高比）；" +
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowRgn(nint hWnd, nint hRgn);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetRgnBox(nint hRgn, out RGNRECT lprc);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
