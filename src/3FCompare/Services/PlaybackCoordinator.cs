using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare.Services;

/// <summary>多路打开编排器（WinForms MainForm.OpenFiles / OpenSlotAsync /
/// WaitForOpenCompletionAsync / TryAutoPlayAfterOpen / HandleEngineEvent 的移植）。
///
/// 关键时序（真实模式）：FFF3FP_Open 仅把 DoOpen 入队即返回 Success，OpenAsync 完成
/// 时后端仍是 Opening，此阶段 Play() 得 InvalidState；必须轮询快照至
/// Ready/Playing/Paused（≤15s）再由 TryAutoPlayAfterOpen 统一启动播放（首帧渲染契约）。</summary>
public sealed class PlaybackCoordinator
{
    private readonly IPlayerEngine _engine;
    private readonly SyncController _sync;
    private readonly AppSettings _settings;

    /// <summary>最近一次打开被取消/降级的原因（UI 状态栏读取；null = 无待显示错误）。
    /// OpenFiles 是 async void，异常无人接住会击穿进程，因此一切失败都走状态通知而非 throw。</summary>
    public string? LastOpenError { get; private set; }

    /// <summary>UI 展示完打开错误后调用（N1 消费链：展示 → 立即清除）。</summary>
    public void ConsumeLastOpenError() => LastOpenError = null;
    private readonly bool _realMode;
    private readonly Func<int, PlayerSurface?> _surfaceAt;

    private int _pendingAutoPlay;
    private readonly Queue<Action> _onAllOpenedCallbacks = new();
    private bool _closed;

    public PlaybackCoordinator(IPlayerEngine engine, SyncController sync, AppSettings settings,
        Func<int, PlayerSurface?> surfaceAt)
    {
        _engine = engine;
        _sync = sync;
        _settings = settings;
        _surfaceAt = surfaceAt;
        _realMode = engine is Fff3FpEngine;
    }

    public bool RealMode => _realMode;
    public SyncController Sync => _sync;

    /// <summary>窗口已关闭（自动化流程终止标志）。</summary>
    public bool IsClosed => _closed;

    /// <summary>状态变化通知（失败路变化/事件到达等，UI 据此刷新状态栏）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>打开文件（≤9 路总量钳制）。autoPlay：全部就绪后统一 Play；
    /// onAllOpened：播放前回调队列（会话恢复 Seek 等）。</summary>
    public async void OpenFiles(IReadOnlyList<string> files, bool autoPlay = false, Action? onAllOpened = null)
    {
        // async void 的异常无人接住会击穿进程：全部逻辑下沉到 OpenFilesCore（async Task），
        // 这里只做兜底——任何漏网异常转为状态通知。
        try
        {
            await OpenFilesCore(files, autoPlay, onAllOpened);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Coordinator", $"OpenFiles 兜底捕获: {ex.GetType().Name}: {ex.Message}");
            LastOpenError = $"打开失败：{ex.Message}";
            _pendingAutoPlay = 0;
            _onAllOpenedCallbacks.Clear();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task OpenFilesCore(IReadOnlyList<string> files, bool autoPlay = false, Action? onAllOpened = null)
    {
        if (_closed) return;
        var count = Math.Min(files.Count, 9 - _sync.Count);
        if (count <= 0)
        {
            // 已达 9 路上限：不得同步触发 onAllOpened（会话未就绪时 Play 无意义）
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (autoPlay) _pendingAutoPlay += count;
        if (onAllOpened is not null) _onAllOpenedCallbacks.Enqueue(onAllOpened);

        for (var i = 0; i < count; i++)
        {
            if (_closed) { _pendingAutoPlay = 0; return; }
            var path = files[i];
            var surface = _surfaceAt(_sync.Count);
            if (surface is null)
            {
                // 不可静默跳过：会打破 pending 配额与回调队列的收支平衡。
                // 也不要 throw——OpenFiles 是 async void，异常无人接住会击穿进程；
                // 降级为状态通知（LastOpenError + StateChanged）。
                LastOpenError = $"第 {_sync.Count + 1} 路没有可用的播放面板（surface 为空），已取消本次打开";
                AppLog.Warn("Coordinator", LastOpenError);
                _pendingAutoPlay = 0;
                _onAllOpenedCallbacks.Clear();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            surface.FileName = Path.GetFileName(path);
            surface.IsFailed = false;
            surface.ErrorText = string.Empty;

            try
            {
                // 真实模式需要子 HWND 作为输出窗口：等待 NativeControlHost 附件创建
                nint hwnd = 0;
                if (_realMode)
                {
                    hwnd = await surface.EnsureHwndAsync();
                    if (hwnd == nint.Zero)
                        throw new InvalidOperationException("输出窗口 HWND 未创建");
                }

                // 解析 Auto 色彩模式：根据显示器能力自动选择 HDR/SDR
                var resolvedColorMode = _3FCompare.Core.Settings.ColorModeHelper.Resolve(
                    _settings.ColorMode,
                    hwnd != 0 ? _3FCompare.Core.Display.DisplayCapabilities.ReadForWindow(hwnd) : null);
                var session = _engine.CreateSession(new EngineSessionOptions
                {
                    OutputWindow = hwnd,
                    HardwareDecode = _settings.HardwareDecode,
                    PreferredAdapterIndex = _settings.PreferredAdapterIndex,
                    ColorMode = resolvedColorMode,
                    TearingPresent = _settings.VrrTearingPresent,
                    PacingEnabled = _settings.VrrPacingEnabled,
                });
                surface.AttachSession(session);
                _sync.AddSlot(session, path);

                _ = OpenSlotAsync(_sync.Slots[^1], surface, path);
            }
            catch (Exception ex)
            {
                // P0-3 修复：在此处抛出的路（CreateSession 失败 / HWND 未创建）不会进入 OpenSlotAsync，
                // 也就不会调 TryAutoPlayAfterOpen 归还配额。而 _pendingAutoPlay 是在循环前一次性记账的，
                // 漏还就会让配额永远回不到 0 → 自动播放与 onAllOpened（会话恢复 Seek / 循环区间）永不执行，
                // 表现为"文件都打开了但就是不动"。
                if (autoPlay && _pendingAutoPlay > 0)
                {
                    _pendingAutoPlay--;
                    // 归零说明没有任何一路能走异步完成路径（可能全部失败）：
                    // 清掉悬挂的回调队列，避免它污染下一次打开；不在此触发 Play（无路可播）。
                    if (_pendingAutoPlay == 0) _onAllOpenedCallbacks.Clear();
                }
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenSlotAsync(SyncController.SyncSlot slot, PlayerSurface surface, string path)
    {
        try
        {
            // 引擎事件（原生工作线程）→ UI 线程
            slot.Session.EngineEvent += (_, evt) =>
            {
                if (_closed) return;
                try { Dispatcher.UIThread.Post(() => HandleEngineEvent(slot, surface, evt)); }
                catch { /* 窗口已关闭 */ }
            };

            await slot.Session.OpenAsync(path);
            if (!_closed)
            {
                surface.FileName = Path.GetFileName(path);
                if (_realMode)
                    await WaitForOpenCompletionAsync(slot, surface);
                // 播放中拖入新视频：同步到主时间轴当前位置
                if (!slot.Failed && _sync.Count > 1)
                {
                    var masterPos = _sync.GetMasterPosition100ns();
                    if (masterPos > 0)
                        slot.Session.Seek(masterPos + slot.Offset100ns);
                }
                TryAutoPlayAfterOpen();
            }
        }
        catch (Exception ex)
        {
            slot.Failed = true;
            slot.Error = ex.Message;
            if (!_closed)
            {
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
                TryAutoPlayAfterOpen(); // 失败路也计入完成，避免卡住
            }
            else if (_pendingAutoPlay > 0)
            {
                _pendingAutoPlay = 0;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>真实模式等待 3FP 后端真正就绪；失败/超时(15s)也视为完成（标记失败）。</summary>
    private async Task WaitForOpenCompletionAsync(SyncController.SyncSlot slot, PlayerSurface surface)
    {
        // 就绪通知改事件驱动（08 计划 §4.2）：内核 OpenCompleted 到达即完成等待，
        // 替代 100ms×15s 的轮询空转；轮询保留为兜底（事件丢失时仍按原超时逻辑）。
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEngineEvent(object? _, EngineEvent evt)
        {
            if (evt.Type == EngineEventType.OpenCompleted) readyTcs.TrySetResult(true);
        }
        slot.Session.EngineEvent += OnEngineEvent;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        try
        {
            await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            slot.Session.EngineEvent -= OnEngineEvent;
        }

        // 验证/兜底轮询：事件先行时几乎立即命中 Ready；无事件时等效原轮询语义
        while (DateTime.UtcNow < deadline && !_closed)
        {
            try
            {
                var snap = slot.Session.ReadSnapshot();
                if (IsReadyState(snap.State)) return;
                if (snap.State == PlayerState.Failed)
                {
                    slot.Failed = true;
                    slot.Error = "内核打开失败";
                    surface.IsFailed = true;
                    surface.ErrorText = slot.Error;
                    return;
                }
            }
            catch
            {
                // 快照读取失败：继续等
            }
            await Task.Delay(100);
        }
        if (!_closed)
        {
            slot.Failed = true;
            slot.Error = "打开超时（后端未就绪）";
            surface.IsFailed = true;
            surface.ErrorText = slot.Error;
        }
    }

    /// <summary>全部就绪后：先跑恢复回调，再统一 Play（跳过 Failed 槽）。</summary>
    private void TryAutoPlayAfterOpen()
    {
        if (_pendingAutoPlay <= 0) return;
        if (--_pendingAutoPlay > 0) return;

        while (_onAllOpenedCallbacks.Count > 0)
            _onAllOpenedCallbacks.Dequeue().Invoke();

        _sync.Play();
    }

    private void HandleEngineEvent(SyncController.SyncSlot slot, PlayerSurface surface, EngineEvent evt)
    {
        switch (evt.Type)
        {
            case EngineEventType.Error:
                // 用 System.Text.Json 解析 state 字段，避免字符串包含匹配的脆弱性
                var isFailure = false;
                const int FailureState = (int)PlayerState.Failed; // 内核 Failed=6
                if (!string.IsNullOrEmpty(evt.DetailJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(evt.DetailJson);
                        if (doc.RootElement.TryGetProperty("state", out var stateEl) &&
                            stateEl.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            stateEl.GetInt32() == FailureState)
                            isFailure = true;
                        else if (doc.RootElement.TryGetProperty("reason", out var reasonEl) &&
                                 reasonEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                                 (reasonEl.GetString()?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true))
                            isFailure = true;
                    }
                    catch
                    {
                        // JSON 解析失败时回退到字符串匹配
                        isFailure = evt.DetailJson.Contains($"\"state\":{FailureState}", StringComparison.OrdinalIgnoreCase) ||
                                    evt.DetailJson.Contains("fail", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (isFailure)
                {
                    slot.Failed = true;
                    slot.Error = $"内核错误: {evt.DetailJson}";
                    surface.IsFailed = true;
                    surface.ErrorText = slot.Error;
                }
                break;
            case EngineEventType.PlaybackEnded:
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case EngineEventType.DeviceChanged:
                // 显卡/显示器变更：不自动重建（易误触发），提示用户手动恢复
                AppLog.Warn("Coordinator", $"[{Path.GetFileName(slot.Path)}] 显卡/显示器变更: {evt.DetailJson}");
                surface.ErrorText = "检测到显卡/显示器变更，如画面异常请右键该路重试";
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case EngineEventType.ColorModeChanged:
                // HDR/SDR 模式切换：刷新诊断面板（快照会带出新的 colorMode）
                AppLog.Info("Coordinator", $"[{Path.GetFileName(slot.Path)}] 色彩模式切换: {evt.DetailJson}");
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public static bool IsReadyState(PlayerState state)
        => state is PlayerState.Ready or PlayerState.Playing or PlayerState.Paused;

    public void Close()
    {
        _closed = true;
        _pendingAutoPlay = 0;
        _onAllOpenedCallbacks.Clear();
    }
}
