// ---------------------------------------------------------------------------
// MainWindow 的播放控制分部：倍速 / 帧步进 / 播放暂停 / A-B 循环 / 时间轴刮擦 /
// 快照轮询与停滞恢复 / 时间码换算。
//
// 从 MainWindow.axaml.cs 拆出（该文件曾接近 1800 行）。拆分纯粹是导航性重构，
// 没有任何行为改动：分部类成员互相可见，调用点不需调整。
// ---------------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;

namespace _3FCompare;

public partial class MainWindow
{
    // ── 播放状态 ──
    private bool _isPlaying;
    private double _playbackSpeed = 1.0;
    private long _lastShownPos;

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

    private long _lastPresentedCount;
    /// <summary>presented 计数停滞的持续计时器。
    /// <para>P1-6：原实现用"轮询次数 ≥5 × 250ms ≈ 1.25s"判定，但轮询间隔是动态的
    /// ——平移期间会被压到 83ms，5 次仅 ~415ms 就误判停滞并触发无谓的 Pause→Play。
    /// 改用真实计时，与轮询频率解耦。</para></summary>
    private readonly System.Diagnostics.Stopwatch _stallWatch = new();
    private bool _stalledAfterLightRecovery;
    /// <summary>presented 持续无增长多久判定为停滞。</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1250);
    // scrub 缩略图抓帧 in-flight 标志（0=空闲 1=占用）：同一时刻至多一个 BitBlt 任务，
    // 防止快速拖动时线程池堆积抓帧任务且回传乱序
    private int _captureInFlight;
    // 伪变速 Seek 节流（1s 最小间隔，见 PollSnapshotsCoreAsync）
    private long _lastSpeedSeekTicks;
    private long _speedBasePos;

    private async void PollSnapshots()
    {
        // async void 顶层兜底：任何未预期异常只记录，不崩进程（DispatcherTimer 回调）
        try
        {
            await PollSnapshotsCoreAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] PollSnapshots 异常: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task PollSnapshotsCoreAsync()
    {
        if (_sync.Count == 0) return;
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
                if (presented == _lastPresentedCount)
                {
                    if (!_stallWatch.IsRunning) _stallWatch.Start(); // 首次观察到无增长
                    // P1-6：按真实时长判定，与轮询间隔解耦（平移期间间隔会从 250ms 压到 83ms）
                    if (_stallWatch.Elapsed >= StallThreshold
                        && System.Threading.Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
                    {
                        // 第一级：轻量恢复 Pause→Play（重启呈现管线）
                        if (!_stalledAfterLightRecovery)
                        {
                            Console.Error.WriteLine($"[MainWindow] presented 停滞 ({presented})，轻量恢复 Pause→Play...");
                            try
                            {
                                _sync.Pause();
                                await Task.Delay(120);
                                _sync.Play();
                                Console.Error.WriteLine("[MainWindow] ✅ 轻量恢复完成");
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[MainWindow] 轻量恢复失败: {ex.Message}");
                            }
                            finally
                            {
                                System.Threading.Interlocked.Exchange(ref _recovering, 0);
                            }
                            _stalledAfterLightRecovery = true;
                            _stallWatch.Reset();
                        }
                        else
                        {
                            // 第二级：轻量恢复无效 → 完整会话重建（复用 Failed 路径）
                            Console.Error.WriteLine("[MainWindow] 轻量恢复无效，升级为完整重建...");
                            _ = RecoverFromFailedAsync();
                        }
                    }
                }
                else
                {
                    _stallWatch.Reset();
                    _stalledAfterLightRecovery = false; // 恢复增长后重置升级标志
                    _lastPresentedCount = presented;
                }
            }
            else
            {
                _stallWatch.Reset();
                if (master is not null) _lastPresentedCount = master.PresentedVideoFrames;
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
                if (advance > 0) SafeSeek(pos + advance);
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
            target = 1000; // 无会话：纯 keepalive
        // 3FCompare 优化项⑧：拖动释放后的 83ms 高刷豁免窗（否则被上面的覆盖逻辑立即改回，从未生效）
        if (Environment.TickCount64 - _lastPanApplyTicks < 800)
            target = Math.Min(target, 83);
        var current = _pollTimer.Interval.TotalMilliseconds;
        if (Math.Abs(current - target) > 1)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(target);
    }

    /// <summary>PR 时间码的秒内帧号（0 起；帧率由快照时间基估算，缺省 24）。
    /// <para>实现下沉到 <see cref="_3FCompare.Core.Display.Timecode"/>：帧号必须与显示的"秒"
    /// 同源于 <c>Position100ns</c>，否则非整数帧率下两套网格会相对滑移。</para></summary>
    private static int FrameInSecond(EngineSnapshot snap)
        => _3FCompare.Core.Display.Timecode.FrameInSecond(
            snap.Position100ns, SyncController.EstimateFps(snap));
}
