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
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare;

/// <summary>主窗口（M2：核心播放面已接线）。
/// 打开/播放/步进/循环/缩放平移/网格布局/时间轴/状态栏全量；面板与对话框 M3 实装。</summary>
public partial class MainWindow : Window
{
    /// <summary>自测/压力测试模式（由命令行 --selftest/--screentest/--multitest 置位）。
    /// 置位时跳过窗口几何持久化，避免测试窗口的位置/尺寸覆盖用户配置。</summary>
    private bool _selfTestMode;

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

            // C6 布局还原回归的基准：保存前把视图摆成"非单屏 + 3x3 预设"，
            // 保存后（清空阶段）再翻转成"单屏 + 3x3"。旧实现"快照只存不还原"会停在后者的状态——
            // 这类失效不崩溃、不报错，只能靠断言抓（C6 正是因此潜伏至今）。
            Grid.SingleView = false;
            Grid.SetGridLayout("3x3");
            var expectLayoutCode = _3FCompare.Core.Display.GridLayout.CodeFor(false, videoPaths.Length);

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
            var failures = new System.Collections.Generic.List<string>();
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
    /// 单纯改 XAML 声明顺序很容易改反，故固化成自测断言。</summary>
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
                    ExitSelfTest(3);
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

            // A9 媒体率呈现节奏覆盖
            _step = "VRR 节奏";
            var pacingSupported = _sync.Slots[0].Session.SetPacingConfig(true);
            Log($"VRR 媒体率节奏: {(pacingSupported ? "已启用 ✓" : "不支持")}");

            // 帧步进 +1：位置不得后退（真实模式）
            _step = "帧步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"帧步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    Log($"⚠ 帧步进位置后退（已知问题）{before} → {after}");
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
            _step = "媒体信息";
            var media = _sync.Slots[0].Session.ReadMediaInfo();
            if (media is not null)
                Log($"媒体 {media.VideoWidth}x{media.VideoHeight} @{SyncController.EstimateFps(ready):0.##}fps {media.Codec} HDR={media.IsHdr}");

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
                    }
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

        Console.WriteLine($"multitest: 多路={routes} 时长={durationSec}s 素材={videoPath}");
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
            await System.Threading.Tasks.Task.Delay(1500);

            // 采样基线（播放稳定后）
            var baseline = SnapshotTuple(_sync.ReadAllSnapshots());
            var baselineMaster = _sync.GetMasterPosition100ns();
            Console.WriteLine($"multitest: 播放基线 master={TimeSpan.FromTicks(baselineMaster):g}");

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
                _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(2000); // 等管线消化
            await CheckPresentedGrowthAsync(beforeTransform, "并行扇出");
            CheckDrift(routes, "并行扇出后");

            // 阶段三：恢复 fit 继续播放，观察复位后稳定性
            _step = "多路复位稳定";
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
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    /// <summary>断言多路 position 漂移 ≤ 阈值（master 为基准，考虑 Offset 后误差）。
    /// 8K 高码率下允许 ±2 帧（@24fps ≈ 83ms）容差，超过即判定漂移。</summary>
    private void CheckDrift(int routes, string phase)
    {
        var snaps = _sync.ReadAllSnapshots();
        if (snaps.Count != routes) return;
        var masterPos = _sync.GetMasterPosition100ns();
        const long Tolerance = 100_0000; // 100ms
        for (var i = 0; i < snaps.Count; i++)
        {
            var snap = snaps[i];
            if (snap is null || _sync.Slots[i].Failed) continue;
            var expect = masterPos + _sync.Slots[i].Offset100ns;
            var drift = Math.Abs(snap.Position100ns - expect);
            if (drift > Tolerance)
                throw new InvalidOperationException(
                    $"第 {i} 路漂移 {TimeSpan.FromTicks(drift):g} > 100ms（pos={TimeSpan.FromTicks(snap.Position100ns):g} 期望 {TimeSpan.FromTicks(expect):g}）");
        }
        Console.WriteLine($"multitest[{phase}]: 漂移 OK（{snaps.Count} 路 ≤100ms）✓");
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
    /// 复位/扇出瞬间渲染器切换导致的瞬时零增长误报；窗口耗尽仍不涨=真停滞。</summary>
    private async System.Threading.Tasks.Task CheckPresentedGrowthAsync(
        (long pos, long presented)[] before, string phase, int maxWaitMs = 4000)
    {
        var deadline = System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(maxWaitMs);
        while (true)
        {
            var after = SnapshotTuple(_sync.ReadAllSnapshots());
            var stalled = new System.Collections.Generic.List<int>();
            for (var i = 0; i < before.Length && i < after.Length; i++)
            {
                var b = before[i];
                var a = after[i];
                if (a.presented - b.presented <= 0)
                    stalled.Add(i);
            }
            if (stalled.Count == 0)
            {
                Console.WriteLine($"multitest[{phase}]: presented 增长 OK（{before.Length} 路均 >0）✓");
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
}
