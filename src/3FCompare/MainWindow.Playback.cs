// ---------------------------------------------------------------------------
// MainWindow 的播放控制分部：倍速 / 帧步进 / 播放暂停 / A-B 循环 / 时间轴刮擦 /
// 快照轮询与停滞恢复 / 时间码换算。
//
// 从 MainWindow.axaml.cs 拆出（该文件曾接近 1800 行）。拆分纯粹是导航性重构，
// 没有任何行为改动：分部类成员互相可见，调用点不需调整。
// ---------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;
using _3FCompare.Diagnostics;

namespace _3FCompare;

public partial class MainWindow
{
    // ── 播放状态 ──
    private bool _isPlaying;
    private double _playbackSpeed = 1.0;
    private long _lastShownPos;

    // ── 心跳 Δpresented 基线（P1 取证：回答"崩前 presented 是否还在增长"） ──
    //
    // 只在 UI 线程、1 秒一次的心跳里读写，因此不需要任何同步；数组是固定长度，
    // 不在心跳路径上分配（Array/List 的扩容会让"1 秒一条"变成 1 秒一次 GC 压力）。
    // 长度取 Grid 的路数上限 9（CompareGridView.SetCount 的 clamp 上界）。
    private const int MaxHeartbeatRoutes = 9;
    private readonly long[] _hbPrevPresented = new long[MaxHeartbeatRoutes];
    private readonly bool[] _hbPrevValid = new bool[MaxHeartbeatRoutes];

    /// <summary>帧步进（异步入口）。
    /// <para>P1-4：StepFrames 内含 50ms 复测等待（Thread.Sleep），在 UI 线程同步调用会让
    /// 每次逐帧步进冻结界面 50ms，连按时手感明显卡顿——而逐帧是盯帧场景最高频的操作。
    /// 键盘与传输栏两个入口统一走这里，避免任一处遗漏。</para></summary>
    private void StepFramesAsync(int frames)
        => System.Threading.Tasks.Task.Run(() => _sync.StepFrames(frames));

    private void ToggleLoop(bool on)
    {
        _sync.LoopEnabled = on;
        _transport.SetLoop(on);
        // 关闭时同步清除时间轴视觉区间（否则绿色 A-B 区间残留）
        if (!on)
            _timeline.SetLoopRange(0, 0, false);
    }

    private void TogglePlay()
    {
        if (_sync.Count == 0) return;
        var snap = _sync.ReadMasterSnapshot();
        var playing = snap is { State: PlayerState.Playing };
        if (playing) _sync.Pause();
        else _sync.Play();
        SetPlaying(!playing);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        _transport.SetPlaying(playing);
    }

    /// <summary>安全的 Seek：捕获异常避免反复拖动导致崩溃。</summary>
    private void SafeSeek(long pos)
    {
        try { _sync.SeekTo(pos); }
        catch (Exception ex) { Console.Error.WriteLine($"Seek 异常: {ex.Message}"); }
    }

    // ---- 时间轴拖动缩略图预览 ----

    private ThumbnailPopup? _thumbnail;
    private readonly DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private long _scrubTarget, _preScrubPos;
    private bool _scrubbing;

    private void OnScrubPreview(long pos)
    {
        if (!_scrubbing)
        {
            _scrubbing = true;
            _preScrubPos = _sync.GetMasterPosition100ns();
            _thumbnail ??= new ThumbnailPopup();
        }
        _scrubTarget = pos;
        if (!_scrubTimer.IsEnabled) _scrubTimer.Start();
    }

// 3FCompare 优化项⑤：Scrub 预览降载 —— 抓帧移出 UI 线程 + 缩放目标，
        // 避免拖动时间轴时 UI 线程被顶层窗口 BitBlt（4K 下 ~10-30ms）周期性阻塞。
        private void OnScrubTimerTick(object? sender, EventArgs e)
        {
            if (!_scrubbing) { _scrubTimer.Stop(); return; }
            SafeSeek(_scrubTarget);
            // 缩略图预览可关闭（低配设备）：关闭时仅 Seek，不触发 BitBlt 屏幕抓取
            if (!_settings.ScrubPreviewEnabled) return;
            try
            {
                if (_thumbnail is null) return;
                var surface = Grid.GetSurface(0);
                if (surface is null || surface.Hwnd == 0) return;
                var hwnd = surface.Hwnd;
                var target = _scrubTarget;
                var dur = _sync.GetMasterDuration100ns();
                var ratio = dur > 0 ? (double)target / dur : 0;
                var screen = _timeline.PointToScreen(new Point(ratio * _timeline.Bounds.Width, 0));
                // 后台抓帧：BitBlt 顶层窗口并缩放到预览尺寸（~480px 宽），完成后回 UI 线程展示。
                // in-flight 节流：同一时刻至多一个抓帧任务（快速拖动时旧任务未完成则跳过本轮，
                // 下一 tick 重试），避免线程池堆积与回传乱序。
                if (System.Threading.Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) return;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        using var bmp = _3FCompare.App.Capture.ScreenFrameCapture.CaptureWindowFrame(hwnd);
                        if (bmp is null) return;
                        var preview = ThumbnailPopup.ScaleTo(bmp, 480);
                        if (preview is null) return;
                        Dispatcher.UIThread.Post(() =>
                        {
                            // P0-5 修复：preview 在两条分支里都必须被释放。
                            // ShowAt 内部把像素复制进 WriteableBitmap 后会 Dispose 它；
                            // 若走不到 ShowAt（拖动已结束 / 弹窗已销毁）则在此释放。
                            // 修复前只有 !_scrubbing 分支会释放，拖动期间每 tick 泄漏一张位图。
                            try
                            {
                                if (_scrubbing && _thumbnail is not null)
                                    _thumbnail.ShowAt(screen, preview);
                                else
                                    preview.Dispose();
                            }
                            catch { preview.Dispose(); }
                        });
                    }
                    catch { /* 后台抓帧失败静默降级 */ }
                    finally { System.Threading.Interlocked.Exchange(ref _captureInFlight, 0); }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"Scrub capture 异常: {ex.Message}"); }
        }

    private void EndScrubPreview()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        _scrubTimer.Stop();
        _thumbnail?.Hide();
    }

    /// <summary>设置 A/B 循环点（自动补全另一点：设 A 时若 B 未设则 B=结尾，反之亦然）。</summary>
    private void SetLoopPoint(long pos, bool isA)
    {
        var dur = _sync.GetMasterDuration100ns();
        if (isA)
        {
            _sync.LoopStart100ns = pos;
            if (_sync.LoopEnd100ns <= pos) _sync.LoopEnd100ns = dur;
        }
        else
        {
            _sync.LoopEnd100ns = pos;
            if (_sync.LoopStart100ns >= pos) _sync.LoopStart100ns = 0;
        }
        _sync.LoopEnabled = true;
        _transport.SetLoop(true);
        _timeline.SetLoopRange(_sync.LoopStart100ns, _sync.LoopEnd100ns, true);
    }

    // ══════════ 轮询（WinForms PollSnapshots 移植） ══════════

    /// <summary>呈现停滞看门狗（判定逻辑已下沉到 Core.Sync.RenderStallWatchdog，
    /// 便于单测；历史上 P1-5/P1-6 两个缺陷都出在判定上）。</summary>
    private readonly _3FCompare.Core.Sync.RenderStallWatchdog _stallWatch =
        new(TimeSpan.FromMilliseconds(1250));
    /// <summary>presented 持续无增长多久判定为停滞。</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1250);
    /// <summary>看门狗用的单调时钟（P1-6：按真实时长判定，与轮询间隔解耦）。</summary>
    private readonly System.Diagnostics.Stopwatch _stallClock =
        System.Diagnostics.Stopwatch.StartNew();
    // scrub 缩略图抓帧 in-flight 标志（0=空闲 1=占用）：同一时刻至多一个 BitBlt 任务，
    // 防止快速拖动时线程池堆积抓帧任务且回传乱序
    private int _captureInFlight;
    // 伪变速 Seek 节流（1s 最小间隔，见 PollSnapshotsCoreAsync）
    private long _lastSpeedSeekTicks;
    private long _speedBasePos;
    // 漂移校正日志节流（与 Core 的 1s 冷却对齐，见 LogDriftCorrection）
    private long _lastDriftLogTicks;

    /// <summary>轮询重入互锁（0=空闲，1=正在执行）。见 <see cref="PollSnapshots"/>。</summary>
    private int _polling;

    private async void PollSnapshots()
    {
        // D3 可重入保护：本方法是 async void 且由 DispatcherTimer 驱动，而轻量恢复分支里
        // 有 `await Task.Delay(120)`；同时"拖动释放后的高刷豁免窗"会把轮询间隔压到 83ms
        // （SetPollInterval）。83ms < 120ms ⇒ 上一轮还没跑完下一轮就进来了，
        // 两次 Pause→Play 交错执行，轻则重复重启呈现管线，重则状态错位卡死。
        // 已在执行则直接丢弃这一拍——下一拍（≤250ms）自然会补上，不会丢状态。
        if (System.Threading.Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            // async void 顶层兜底：任何未预期异常只记录，不崩进程（DispatcherTimer 回调）
            await PollSnapshotsCoreAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] PollSnapshots 异常: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>设定轮询间隔（含"拖动释放后的高刷豁免窗"）。</summary>
    private void SetPollInterval(int target)
    {
        // 3FCompare 优化项⑧：拖动释放后的 83ms 高刷豁免窗
        //（否则会被调用方的覆盖逻辑立即改回，从未生效）
        if (Environment.TickCount64 - _lastPanApplyTicks < 800)
            target = Math.Min(target, 83);
        var current = _pollTimer.Interval.TotalMilliseconds;
        if (Math.Abs(current - target) > 1)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(target);
        }
    }

    private async System.Threading.Tasks.Task PollSnapshotsCoreAsync()
    {
        // 1 秒低频心跳：本方法是 UI 线程上唯一的周期性节拍，借它把"崩前各组件在做什么"
        // 压进组件日志（节奏由 ComponentLog.HeartbeatDue 内部节流，这里每拍调用一次即可）。
        // 刻意放在两处 early-return（无会话 / 会话重建中）**之前**——恰恰是这两种状态最需要留痕。
        if (ComponentLog.HeartbeatDue()) EmitHeartbeat();

        if (_sync.Count == 0)
        {
            // ⚠ 必须在这里先把频率降下来再返回：下面的"自适应三档"位于本方法**末尾**，
            // 若直接 return，"无会话 → 1000ms"那一档永远执行不到，定时器会一直保持
            // 16ms 的初值 ⇒ 空闲时（启动默认态）UI 线程被 62Hz 空转唤醒。
            // 这一档此前等同于死代码（docs/15 §P2）。
            SetPollInterval(1000);
            return;
        }
        if (_recovering != 0) return; // 会话重建中跳过，防止读取中间状态导致崩溃

        var snaps = _sync.ReadAllSnapshots();
        for (var i = 0; i < snaps.Count && i < Grid.Count; i++)
            Grid.GetSurface(i)?.UpdateSnapshot(snaps[i]);

        var master = snaps.Count > 0 ? snaps[0] : null;
        if (master is not null)
        {
            // 捕获到局部变量：跨 await 后编译器无法证明 master 仍非空（CS8602）
            var m = master;
            // 检测引擎 Failed 状态（窗口最大化时 SwapChain 重建失败导致 D3D11 设备丢失）
            // 3FCompare (F-LOG)：重建风暴抑制——连续重建失败 ≥3 次后放弃，
            // 避免死循环（日志显示曾 7+ 轮无限重建）
            if (m.State == PlayerState.Failed && _realMode && _sync.Count > 0
                && System.Threading.Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
            {
                var attempts = System.Threading.Interlocked.Increment(ref _recoveryAttempts);
                if (attempts > 3)
                {
                    // 只在首次放弃时打日志：放弃后轮询仍在跑，每个 tick 一行会让日志无限增长。
                    if (!_recoveryAbandoned)
                    {
                        _recoveryAbandoned = true;
                        Console.Error.WriteLine(
                            $"[MainWindow] 重建已尝试 {attempts} 次仍失败，放弃避免死循环（重新打开媒体会重置计数）");
                    }
                    System.Threading.Interlocked.Exchange(ref _recovering, 0);
                }
                else
                {
                    Console.Error.WriteLine($"[MainWindow] 引擎状态=Failed，尝试重建会话 (第 {attempts} 次)...");
                    _ = RecoverFromFailedAsync();
                }
            }

            // P3 轻量恢复：Playing 但 presented 停滞 = 解码/呈现线程锁死（高码率 HDR + 缩放时出现）。
            // 连续 N 次轮询无增长则 Pause→Play 重启呈现管线，比全会话重建快一个数量级。
            // Ready/Paused 且 UI 认为在播放：还原后 presented 不涨的停滞场景，同样轻量恢复。
            // P1-5：原判据 `shouldPlay = _isPlaying || Ready/Paused` 是错的——
            // 暂停时它恒为 true，而暂停状态下 presented 本就不该增长，于是空转累加；
            // 等用户恢复播放时早已越过阈值，立刻误触发一次 Pause→Play，
            // 还把 _stalledAfterLightRecovery 置位，使下一次真实停滞被跳级成完整会话重建。
            // 正确语义：只有"UI 认为在播放"时才检测（兼容刚点播放、状态仍是 Ready/Paused 的过渡期）。
            if (_realMode && _isPlaying
                && m.State is PlayerState.Playing or PlayerState.Ready or PlayerState.Paused)
            {
                var presented = m.PresentedVideoFrames;
                var action = _stallWatch.Update(
                    uiPlaying: true, engineActive: true, presented, _stallClock.ElapsedMilliseconds);

                if (action != _3FCompare.Core.Sync.StallAction.None
                    && System.Threading.Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
                {
                    // 组件日志：停滞判定命中（presented 无增长）。这是"呈现管线锁死"的直接证据，
                    // 也是字幕/presenter 线程相关崩溃最常见的伴随事件。
                    ComponentLog.Log(Comp.Sync, "StallDetected", -1,
                        $"presented={presented} action={action} elapsedMs={_stallClock.ElapsedMilliseconds}");
                    if (action == _3FCompare.Core.Sync.StallAction.LightRecovery)
                    {
                        ComponentLog.Log(Comp.Sync, "LightRecoveryBegin", -1, "pause→play→redraw");
                        Console.Error.WriteLine($"[MainWindow] presented 停滞 ({presented})，轻量恢复 Pause→Play...");
                        try
                        {
                            _sync.Pause();
                            await Task.Delay(120);
                            _sync.Play();
                            // 尺寸变化（最大化 / 还原 / 拖动分隔条）导致的停滞，
                            // 根因是交换链停止 flips —— 内核要求由应用调 Redraw
                            // 才会继续呈现，光 Pause→Play 唤不醒（见 SyncController.RedrawAll）。
                            // ⚠ 疗效本身仍未复现验证（docs/16：12 次尝试未复现）。
                            // 组件日志：RedrawAll 是"强迫交换链恢复 flips"的动作，
                            // 必须留痕——它正是 Present 路径上的高风险调用点。
                            ComponentLog.Log(Comp.Render, "RedrawAll", -1, "src=LightRecovery");
                            _sync.RedrawAll();
                            ComponentLog.Log(Comp.Sync, "LightRecoveryEnd", -1, "ok");
                            Console.Error.WriteLine("[MainWindow] ✅ 轻量恢复完成（Pause→Play→Redraw）");
                        }
                        catch (Exception ex)
                        {
                            ComponentLog.Log(Comp.Sync, "LightRecoveryEnd", -1, $"fail={ex.GetType().Name}");
                            Console.Error.WriteLine($"[MainWindow] 轻量恢复失败: {ex.Message}");
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _recovering, 0);
                        }
                    }
                    else
                    {
                        // 第二级：轻量恢复无效 → 完整会话重建（复用 Failed 路径）
                        ComponentLog.Log(Comp.Sync, "UpgradeFullRebuild", -1, "lightRecoveryIneffective");
                        Console.Error.WriteLine("[MainWindow] 轻量恢复无效，升级为完整重建...");
                        // ⚠ 必须在这里归还 _recovering：本分支是 fire-and-forget，
                        // 没有 finally 兜底（轻量恢复分支的 finally 只覆盖它自己）。
                        // 漏还会让 _recovering 永久停在 1 ⇒ 此后所有恢复动作都被
                        // CompareExchange 挡掉，看门狗从此彻底失效。
                        // 放到调用之后，避免重建内的同步段与本标志竞争。
                        try
                        {
                            _ = RecoverFromFailedAsync();
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _recovering, 0);
                        }
                    }
                }
            }
            else
            {
                // 不该呈现（暂停 / 引擎状态不符）⇒ 复位看门狗，避免恢复播放时立刻误触发
                _stallWatch.Reset(m.PresentedVideoFrames);
            }

            _timeline.SetDuration(m.Duration100ns);
            if (!_timeline.IsScrubbing)
                _timeline.SetPosition(m.Position100ns);
            _transport.SetTime(
                TimeSpan.FromTicks(m.Position100ns),
                TimeSpan.FromTicks(m.Duration100ns),
                FrameInSecond(m));
        }

        // 播放状态回显（若被原生事件改变）
        if (master is { State: PlayerState.Playing } && !_isPlaying) SetPlaying(true);
        else if (master is not null and { State: not PlayerState.Playing } && _isPlaying) SetPlaying(false);

        // 循环回绕
        if (_sync.LoopEnabled) _sync.TickLoop();

        // 漂移检测与校正（docs/15 §3.3）：各路独立时钟，播放中周期对齐。
        // 内部自带 1s 节流，所以这里每拍调用即可。
        if (_sync.Count > 1) _sync.TickDrift();

        // 伪变速：真实模式下按速度节流 Seek（A2 落地前的临时方案）。
        // 3FCompare 优化：Seek 最小间隔 1s——每次 Seek 是 9 路 av_seek_frame + 双解码器
        // flush（CPU 尖峰），250ms 一次会造成周期性顿挫；1s 粒度下跳变仍平滑可接受。
        if (_isPlaying && _realMode && Math.Abs(_playbackSpeed - 1.0) > 0.01 && master is not null)
        {
            var now = Environment.TickCount64;
            var pos = master.Position100ns;
            if (_lastShownPos == 0) _lastShownPos = pos;
            if (now - _lastSpeedSeekTicks >= 1000)
            {
                _lastSpeedSeekTicks = now;
                // 推进量计算下沉到 Core（含 2s 上限钳制）：正常每 tick 只增长 ≤1s，
                // 若基准异常（首次进入 / 会话刚重建 / 被外部 Seek），mediaElapsed 会等于
                // 整个已播放时长——此时宁可跳过本次跳变，也不要把位置推走。
                var advance = _3FCompare.Core.Sync.PlaybackSpeed
                    .SeekAdvanceTicks(pos - _speedBasePos, _playbackSpeed);
                if (advance > 0)
                {
                    // 组件日志：伪变速的周期性 Seek（1s 一次，非逐帧）
                    ComponentLog.Log(Comp.Sync, "Seek", -1,
                        $"src=speed advance={advance} speed={_playbackSpeed:0.###}");
                    SafeSeek(pos + advance);
                }
                _speedBasePos = master.Position100ns;
            }
            _lastShownPos = pos;
        }
        else if (_lastShownPos != 0 && master is not null)
        {
            _lastShownPos = master.Position100ns;
        }

// 自适应频率三档：播放 250ms / 暂停有会话 250ms / 空闲无会话 1000ms
        int target;
        if (_isPlaying && _sync.Count > 0)
            target = 250;  // 播放中：4Hz 刷新时间码（降低Avalonia重绘/GC/P-Invoke频率）
        else if (_sync.Count > 0)
            target = 250;  // 暂停有会话：保持状态同步
        else
            target = 1000; // 无会话：纯 keepalive（实际由方法开头的 early-return 承担）
        SetPollInterval(target);
    }

    /// <summary>
    /// 心跳载荷：把"崩溃前各组件正在做什么"写成一行结构化事件（组件 <see cref="Comp.Sync"/>，
    /// 事件名 <c>Heartbeat</c>）。
    ///
    /// <para><b>字段全部取自既有运行时状态，不做任何推算</b>；某路快照缺失就写"无"，
    /// 拿不到的值绝不编造。含：路数 / 对比区与主窗口尺寸 / 缩放比 / 对比模式与 zoom /
    /// 线程与句柄数 / 每路的状态·位置·presented·swapchain Present 计数·surface HWND，
    /// 以及 <b>Δpresented</b>（本路本次心跳与上次心跳之间的 presented 增量）。</para>
    ///
    /// <para><b>为什么这些字段</b>：<c>presented</c> 与 <c>swap</c> 是内核快照里唯一能拿到的
    /// presenter 侧计数（停滞判定就基于前者）；<c>hwnd</c> 是视频表面生命周期标识——
    /// 两次心跳间 HWND 变了即说明表面被重建（交换链随之重建）；尺寸字段对应
    /// "窗口尺寸变化 → 交换链动作"这条怀疑链。</para>
    ///
    /// <para><b>为什么单看 <c>pres</c> 不够、必须加 <c>dp</c></b>：<c>pres</c> 是单调累计值，
    /// 它大只说明"历史上涨过"，说明不了"此刻还在涨"——而后者才是崩溃前唯一要判断的事。
    /// <c>dp</c> 是两次心跳之间的净增量，除以两次心跳的间隔（由行首时间戳得出）就是
    /// 该路的实时呈现速率；<c>dp=0</c> 即"这一秒一帧都没出"（presenter 已停），
    /// <c>dp</c> 为负即"计数倒退"（呈现管线被重建/复位）。
    /// 首个样本没有基线，记 <c>?</c>，绝不编造。</para>
    ///
    /// <para>仅在 UI 线程、1 秒一次调用，字符串拼接开销可忽略（不在渲染热路径上）。</para>
    /// </summary>
    private void EmitHeartbeat()
    {
        if (!ComponentLog.IsEnabled) return;

        var snaps = _sync.ReadAllSnapshots();
        var sb = new System.Text.StringBuilder(320);
        sb.Append("routes=").Append(_sync.Count);
        // 对比区 / 主窗口尺寸（DIP，随 RenderScaling 换算成物理像素）
        sb.Append(" grid=").Append(Grid.Bounds.Width.ToString("0", CultureInfo.InvariantCulture))
          .Append('x').Append(Grid.Bounds.Height.ToString("0", CultureInfo.InvariantCulture));
        sb.Append(" win=").Append(ClientSize.Width.ToString("0", CultureInfo.InvariantCulture))
          .Append('x').Append(ClientSize.Height.ToString("0", CultureInfo.InvariantCulture));
        sb.Append(" scale=").Append(RenderScaling.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" mode=").Append(_compareMode)
          .Append(" cmp=").Append(_compareActive ? 1 : 0)
          .Append(" zoom=").Append(_viewZoom.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(" single=").Append(Grid.SingleView ? 1 : 0);
        // 线程/句柄数：docs/33 §四"路数强相关、负载无关"指向每路线程数线性增长（实测约 80/路）
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            sb.Append(" thr=").Append(proc.Threads.Count).Append(" hnd=").Append(proc.HandleCount);
        }
        catch { sb.Append(" thr=无 hnd=无"); }

        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            sb.Append(" | L").Append(i).Append('=');
            if (s is null) { sb.Append("无"); continue; }
            var hwnd = Grid.GetSurface(i)?.Hwnd ?? 0;
            sb.Append(s.State)
              .Append("/pos=").Append(s.Position100ns / TimeSpan.TicksPerMillisecond).Append("ms")
              .Append("/pres=").Append(s.PresentedVideoFrames);
            // Δpresented：与上次心跳的差。越界（路数 > MaxHeartbeatRoutes，理论上不可达）
            // 只丢 Δ 一项，绝不让诊断路径抛异常把播放器带崩。
            sb.Append("/dp=");
            if (i < MaxHeartbeatRoutes && _hbPrevValid[i])
                sb.Append((s.PresentedVideoFrames - _hbPrevPresented[i])
                          .ToString(CultureInfo.InvariantCulture));
            else
                sb.Append('?');
            if (i < MaxHeartbeatRoutes)
            {
                _hbPrevPresented[i] = s.PresentedVideoFrames;
                _hbPrevValid[i] = true;
            }
            sb.Append("/swap=").Append(s.SwapChainPresents)
              .Append("/hwnd=0x").Append(hwnd.ToString("X", CultureInfo.InvariantCulture));
        }

        ComponentLog.Log(Comp.Sync, "Heartbeat", -1, sb.ToString());
    }

    /// <summary>漂移校正观测（<b>只读</b>，不参与任何控制逻辑，也不改变 Core 行为）。
    ///
    /// <para>判据复刻自 <c>SyncController.TickDriftCore</c>：从路位置与
    /// <c>master 位置 + 该路偏移</c> 之差超过<b>半帧</b>即触发一次从路 Seek。
    /// Core 侧那条 <c>AppLog.Debug</c> 走的是后台队列（崩溃可能丢），这里补一份
    /// 逐条 flush 的组件日志，使"漂移校正 Seek"在硬崩后仍可查。</para>
    ///
    /// <para>自带 1s 节流，与 Core 的冷却一致；只在真的越过阈值时输出。</para>
    /// </summary>
    private void LogDriftCorrection(
        System.Collections.Generic.IReadOnlyList<EngineSnapshot?> snaps, EngineSnapshot? master)
    {
        if (!ComponentLog.IsEnabled || master is null || master.State != PlayerState.Playing) return;
        var now = Environment.TickCount64;
        if (now - _lastDriftLogTicks < 1000) return;
        _lastDriftLogTicks = now;

        var fps = SyncController.EstimateFps(master);
        // 阈值 = 半帧；拿不到帧率时退化为 20ms（与 Core 完全一致）
        var threshold = fps > 0
            ? (long)(TimeSpan.TicksPerSecond / (2.0 * fps))
            : 20 * TimeSpan.TicksPerMillisecond;

        var slots = _sync.Slots;
        for (var i = 1; i < snaps.Count && i < slots.Count; i++)
        {
            var s = snaps[i];
            if (s is null || s.State != PlayerState.Playing || slots[i].Failed) continue;
            var dev = s.Position100ns - (master.Position100ns + slots[i].Offset100ns);
            if (Math.Abs(dev) <= threshold) continue;
            ComponentLog.Log(Comp.Sync, "DriftCorrectSeek", i,
                $"devMs={dev / 10000.0:0.#} thresholdMs={threshold / 10000.0:0.#}");
        }
    }

    /// <summary>PR 时间码的秒内帧号（0 起；帧率由快照时间基估算，缺省 24）。
    /// <para>实现下沉到 <see cref="_3FCompare.Core.Display.Timecode"/>：帧号必须与显示的"秒"
    /// 同源于 <c>Position100ns</c>，否则非整数帧率下两套网格会相对滑移。</para></summary>
    private static int FrameInSecond(EngineSnapshot snap)
        => _3FCompare.Core.Display.Timecode.FrameInSecond(
            snap.Position100ns, SyncController.EstimateFps(snap));
}
