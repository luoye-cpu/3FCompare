using System.Reflection;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;
using _3FCompare.Services;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="PlaybackCoordinator"/> 的<b>打开编排契约</b>（docs/41 §4.5 第 11 项）。
///
/// <para><b>为什么这些用例能跑起来（而不用构造 Avalonia 窗口）</b>：本类只测<b>不经过</b>
/// <c>PlayerSurface</c> 的那几条路径 —— surface 为 null / 探针抛异常 / 已达 9 路 / 窗口已关闭。
/// 它们全部在"拿到 surface 并 <c>AttachSession</c>"之前就分叉了，所以只要注入
/// <c>Func&lt;int, PlayerSurface?&gt;</c> 就能覆盖，一个真实控件都不需要（docs/41 §4.5 把它列为
/// "需最小 seam"而不是"C 类只能冒烟"，正是因为这几条分支的可达性）。</para>
///
/// <para><b>期望值来源（独立推算，不同源互证）</b>：见每条用例内的"期望值"注释。
/// 核心手法是 ① 路数钳制用 <b>min(请求数, 9 − 已有数)</b> 这个定义式手算；
/// ② "配额是否悬挂"不看任何返回值，而是反射读私有的在飞批次表<b>长度必须为 0</b>；
/// ③ 错误文案用 <b>拼接式字面量</b>（<c>"打开失败：" + ex.Message</c>）而不是从实现里取。</para>
/// </summary>
public class PlaybackCoordinatorQuotaTests
{
    /// <summary>剩余槽位与上限共同决定实际打开路数。期望值 = 手算的 <c>min(请求, 9 − 已有)</c>。
    /// 6 行覆盖：零请求 / 额度内 / 超额 / 只剩 1 路 / 已满 / 越界（已有 > 9，实际不该出现，
    /// 此时必须给出 ≤ 0 让调用方早退，而不是"负数当 0 用"）。</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 0, 3)]
    [InlineData(20, 0, 9)]
    [InlineData(20, 8, 1)]
    [InlineData(5, 9, 0)]
    [InlineData(2, 10, -1)]
    public void 打开路数受剩余槽位与九路上限钳制(int requested, int existing, int expected)
        => Assert.Equal(expected, PlaybackCoordinator.ComputeOpenCount(requested, existing));

    /// <summary>surface 为 null（第 N 路没有可用面板）：必须<b>记录错误</b>而不是静默跳过或抛异常
    /// （<c>OpenFiles</c> 是 async void，抛出即击穿进程），且本批的配额/回调必须当场结清。
    ///
    /// <para><b>期望值</b>：① 错误串含"没有可用的播放面板"（契约用语）；② 只在第 1 路探过一次
    /// surface（<c>_surfaceAt(_sync.Count)</c> 且 count=0）；③ <c>onAllOpened</c> 必须<b>没</b>被调用
    /// （会话未就绪时不该 Seek）；④ 私有在飞批次表长度为 <b>0</b>（配额不悬挂）。</para></summary>
    [Fact]
    public void surface为空时记错且不悬挂批次配额()
    {
        var engine = new StubEngine();
        var sync = new SyncController();
        var probed = new List<int>();
        var stateChanges = 0;
        var onAllOpenedRan = false;

        var coordinator = new PlaybackCoordinator(engine, sync, new AppSettings(),
            i => { probed.Add(i); return null; });
        coordinator.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        coordinator.OpenFiles(new[] { "a.mp4", "b.mp4" }, autoPlay: true,
            onAllOpened: () => onAllOpenedRan = true);

        WaitUntil(() => Volatile.Read(ref stateChanges) > 0, "surface 为空路径应发出一次状态通知");

        Assert.NotNull(coordinator.LastOpenError);
        Assert.Contains("没有可用的播放面板", coordinator.LastOpenError);
        Assert.Equal(new[] { 0 }, probed);
        Assert.False(onAllOpenedRan, "本批已作废，不得触发 onAllOpened");
        Assert.Equal(0, PendingBatchCount(coordinator));
        Assert.Equal(0, sync.Count);
    }

    /// <summary>已达 9 路时再拖入 20 个文件：必须<b>立即返回</b>，不探面板、不算错误、路数不变。
    ///
    /// <para><b>期望值</b>：<c>min(20, 9−9) = 0 ⇒ 早退</c>（同 #2 的定义式）；因此
    /// <c>_surfaceAt</c> 调用次数 = 0；<c>LastOpenError</c> 必须仍为 null —— 上限是正常状态、
    /// 不是打开失败；<c>_sync.Count</c> 仍为 9。</para></summary>
    [Fact]
    public void 已达九路时立即返回且不探面板()
    {
        var engine = new StubEngine();
        var sync = new SyncController();
        for (var i = 0; i < 9; i++) sync.AddSlot(engine.NewSession(), $"slot{i}.mp4");
        Assert.Equal(9, sync.Count);

        var probeCalls = 0;
        var stateChanges = 0;
        var coordinator = new PlaybackCoordinator(engine, sync, new AppSettings(),
            _ => { probeCalls++; return null; });
        coordinator.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        coordinator.OpenFiles(Enumerable.Range(0, 20).Select(i => $"x{i}.mp4").ToArray());

        WaitUntil(() => Volatile.Read(ref stateChanges) > 0, "上限分支应发出一次状态通知");

        Assert.Equal(0, probeCalls);
        Assert.Null(coordinator.LastOpenError);
        Assert.Equal(9, sync.Count);
        Assert.Equal(0, PendingBatchCount(coordinator));
    }

    /// <summary>窗口已关闭（<see cref="PlaybackCoordinator.Close"/>）后调用 <c>OpenFiles</c>：
    /// 必须什么都不做 —— 不探面板、不记错、不发通知、不 Play。
    ///
    /// <para><b>期望值</b>：<c>OpenFilesCore</c> 的首行守卫即 <c>if (_closed) return;</c>，
    /// 故 ① 面板探针 0 次；② <c>LastOpenError</c> 仍为 null（若守卫被绕过，探针会返回 null
    /// 从而写入"没有可用的播放面板"，本断言立刻判红 —— 这就是它的鉴别力所在）；
    /// ③ 状态通知 0 次；④ 两路替身的 <c>PlayCount</c> 之和 = 0（不得在关窗后启动播放）。</para></summary>
    [Fact]
    public void 关窗后打开不探面板也不播放()
    {
        var engine = new StubEngine();
        var sync = new SyncController();
        var first = engine.NewSession();
        var second = engine.NewSession();
        sync.AddSlot(first, "a.mp4");
        sync.AddSlot(second, "b.mp4");

        var probeCalls = 0;
        var stateChanges = 0;
        var coordinator = new PlaybackCoordinator(engine, sync, new AppSettings(),
            _ => { probeCalls++; return null; });
        coordinator.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        coordinator.Close();
        Assert.True(coordinator.IsClosed);

        coordinator.OpenFiles(new[] { "c.mp4", "d.mp4" }, autoPlay: true);

        Assert.Equal(0, probeCalls);
        Assert.Null(coordinator.LastOpenError);
        Assert.Equal(0, stateChanges);
        Assert.Equal(0, first.PlayCount + second.PlayCount);
        Assert.Equal(2, sync.Count);
        Assert.Equal(0, PendingBatchCount(coordinator));
    }

    /// <summary>面板探针抛异常时，<c>async void</c> 的兜底必须把它转成状态通知（而不是击穿进程）。
    ///
    /// <para><b>期望值</b>：<c>OpenFiles</c> 的 catch 写入的字面量拼接式是
    /// <c>"打开失败：" + ex.Message</c>，而本用例注入的异常消息是自己写的字面量
    /// ⇒ 期望整串 <c>"打开失败：surface 探针炸了"</c>（不是从实现里读的）；
    /// 同时 ① 状态通知恰好 1 次；② 批次表长度为 0（本批必须结清，否则后续打开永远排在闸门后）。</para></summary>
    [Fact]
    public void 面板探针抛异常时由兜底转成状态通知()
    {
        var engine = new StubEngine();
        var sync = new SyncController();
        var stateChanges = 0;

        var coordinator = new PlaybackCoordinator(engine, sync, new AppSettings(),
            _ => throw new InvalidOperationException("surface 探针炸了"));
        coordinator.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        // 不得抛出：本行能执行完就是断言的一部分（async void 抛出会击穿测试进程）。
        coordinator.OpenFiles(new[] { "a.mp4" });

        WaitUntil(() => Volatile.Read(ref stateChanges) > 0, "兜底分支应发出一次状态通知");

        Assert.Equal("打开失败：surface 探针炸了", coordinator.LastOpenError);
        Assert.Equal(1, stateChanges);
        Assert.Equal(0, PendingBatchCount(coordinator));
        Assert.Equal(0, sync.Count);
    }

    // ──────── 夹具 ────────

    /// <summary>等一个条件成立（上限 5s）。<c>OpenFiles</c> 是 async void，只能靠"可观测副作用"
    /// 判定它跑完了 —— 用信号而不是固定 <c>Task.Delay</c> 猜时间（docs/41 §4.10 列的 flake 源）。</summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(5);
        }
        Assert.Fail($"等待超时：{what}");
    }

    /// <summary>反射读私有的在飞批次表长度。这是"配额有没有悬挂"的<b>直接</b>观测点：
    /// 只看返回值/错误串无法区分"结清了"与"留着 Remaining&gt;0 的批次"。</summary>
    private static int PendingBatchCount(PlaybackCoordinator coordinator)
    {
        var field = typeof(PlaybackCoordinator).GetField("_batches",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var batches = (System.Collections.ICollection)field!.GetValue(coordinator)!;
        return batches.Count;
    }
}

/// <summary>只实现打开编排用得到的部分：引擎替身（<c>CreateSession</c> 只记账）。</summary>
internal sealed class StubEngine : IPlayerEngine
{
    public List<StubSession> Sessions { get; } = new();

    public IReadOnlyList<AdapterInfo> EnumerateAdapters() => Array.Empty<AdapterInfo>();

    public IPlayerSession CreateSession(EngineSessionOptions options) => NewSession();

    public StubSession NewSession()
    {
        var session = new StubSession();
        Sessions.Add(session);
        return session;
    }
}

/// <summary>会话替身：只让 <c>PlayCount</c> / <c>DisposeCount</c> 可观测，其余一律空实现。</summary>
internal sealed class StubSession : IPlayerSession
{
    public int PlayCount { get; private set; }
    public int DisposeCount { get; private set; }

    public event EventHandler<EngineEvent>? EngineEvent;

    public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Play() => PlayCount++;
    public void Pause() { }
    public void Stop() { }
    public void Seek(long position100ns) { }
    public void SeekFrame(long frameIndex) { }
    public void StepFrame(int direction) { }
    public void SelectAudioStream(int streamIndex) { }
    public void SelectVideoStream(int streamIndex) { }
    public void SetVolume(float volume, bool muted) { }
    public void SetColorMode(ColorMode mode, bool? contentIsHdr = null) { }
    public bool SetPresentConfig(bool tearing) => true;
    public void SetViewTransform(float zoom, float panX, float panY) { }
    public EngineSnapshot ReadSnapshot() => new()
    {
        Position100ns = 0,
        Duration100ns = 0,
        FrameIndex = 0,
        State = PlayerState.Ready,
    };
    public EngineMediaInfo? ReadMediaInfo() => null;
    public bool TryReadPixel(int x, int y, out PixelSample sample) { sample = default; return false; }
    public bool TryReadPixelRegion(int x, int y, int width, int height, float[] buffer,
        out uint outputBitDepth) { outputBitDepth = 8; return false; }
    public void Redraw() { }
    public bool ReadRenderTargetInfo(out RenderTargetInfo info) { info = default; return false; }
    public void Dispose() => DisposeCount++;

    /// <summary>触发一次引擎事件（保留：既避免 CS0067，也供后续用例复用）。</summary>
    public void Raise(EngineEvent e) => EngineEvent?.Invoke(this, e);
}
