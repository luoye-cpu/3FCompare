using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;
using _3FCompare.Core.Tests.Infrastructure;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="SyncController.AddMirror"/> 的判据：路镜像（同一路占多条不相邻的带）。
///
/// <para><b>为什么必须有这些判据</b>：镜像的整个价值取决于"它与源路<b>永远同位</b>"。
/// 若镜像漂移，"同一路出现在两条带上"就变成"两条带显示同一路的不同时刻" ——
/// 而用户要的正是<b>同一画面</b>的细节对照，错一帧这个功能就是假的。
/// 所以核心判据是 <c>镜像与源路位置必须一致</c>，而不是"镜像存在"。</para>
///
/// <para><b>为什么走替身</b>：见 <c>SyncControllerIntegrationTests</c> 的类注释
/// （真实挂钟推进会让断言只能写成宽泛上界，机器卡顿即假红）。替身把位置变成
/// 用例可脚本化的静态量，于是这里可以断言<b>精确相等</b>。</para></summary>
public class SyncMirrorTests
{
    private const long Duration = 10 * TimeSpan.TicksPerSecond;
    private const double Fps = 24.0;

    private static (SyncController sync, IPlayerEngine engine) CreateSync(int count, IClock? clock = null)
    {
        var engine = new FakeEngine { DefaultDuration100ns = Duration, DefaultFrameRate = Fps };
        var sync = new SyncController(clock);
        for (var i = 0; i < count; i++)
        {
            var session = (FakeSession)engine.CreateSession(
                new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
            session.OpenAsync($"test{i}.mp4").GetAwaiter().GetResult();
            sync.AddSlot(session, $"test{i}.mp4");
        }
        return (sync, engine);
    }

    private static IPlayerSession NewMirrorSession(IPlayerEngine engine, string path = "mirror.mp4")
    {
        var s = (FakeSession)engine.CreateSession(new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
        s.OpenAsync(path).GetAwaiter().GetResult();
        return s;
    }

    // ────────── 基本语义 ──────────

    [Fact]
    public void 镜像被追加到尾部且标记了源路()
    {
        var (sync, engine) = CreateSync(3);
        Assert.Equal(3, sync.Count);

        Assert.True(sync.AddMirror(NewMirrorSession(engine), "mirror.mp4", sourceIndex: 1));

        Assert.Equal(4, sync.Count);
        var mirrors = sync.Mirrors();
        Assert.Single(mirrors);
        Assert.Equal(3, mirrors[0].Index);      // 追加在尾部
        Assert.Equal(1, mirrors[0].Source);     // 源路 = 第 1 路
    }

    [Fact]
    public void 镜像源路越界时被拒绝而不是留下孤儿()
    {
        var (sync, engine) = CreateSync(2);
        Assert.False(sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 5));
        Assert.False(sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: -1));
        Assert.Equal(2, sync.Count);            // 没有追加
        Assert.Empty(sync.Mirrors());
    }

    [Fact]
    public void 镜像不参与master选举_第0路仍是原master()
    {
        var (sync, engine) = CreateSync(2);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        // master 恒为下标 0；镜像一律追加在尾部 ⇒ 不可能占 0 号位。
        // 判据用"镜像下标 > 0"直接表达这一点（而不是绕道读快照数）。
        var mirrors = sync.Mirrors();
        Assert.Single(mirrors);
        Assert.True(mirrors[0].Index > 0, $"镜像下标必须 > 0（master 恒为 0），实际 {mirrors[0].Index}");
        Assert.Equal(3, sync.ReadAllSnapshots().Count);
    }

    // ────────── 核心：镜像与源路必须永远同位 ──────────

    [Fact]
    public void 镜像初始偏移等于源路偏移()
    {
        var (sync, engine) = CreateSync(3);
        sync.Slots[1].Offset100ns = 2_000_0000;   // 第 1 路设 2s 偏移

        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        Assert.Equal(2_000_0000, sync.Slots[3].Offset100ns);
    }

    [Fact]
    public void 源路偏移变化后镜像重新对齐_而不是跟master()
    {
        var (sync, engine) = CreateSync(3);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        // 用户改源路偏移（A/B 对齐）⇒ 镜像必须以**源路**为准
        sync.Slots[1].Offset100ns = 1_500_0000;
        sync.ResyncMirrorOffsets();

        Assert.Equal(1_500_0000, sync.Slots[3].Offset100ns);
    }

    [Fact]
    public void 组Seek后镜像与源路落到同一媒体位置()
    {
        var (sync, engine) = CreateSync(3);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        sync.SeekTo(3 * TimeSpan.TicksPerSecond);

        // 两条会话都被 Seek 到同一目标（源路偏移 0 ⇒ masterPos + 0）
        var src = sync.Slots[1].Session.ReadSnapshot()!;
        var mir = sync.Slots[3].Session.ReadSnapshot()!;
        Assert.Equal(src.Position100ns, mir.Position100ns);
        Assert.Equal(3 * TimeSpan.TicksPerSecond, mir.Position100ns);
    }

    [Fact]
    public void 带偏移的源路_镜像落到相同媒体位置而不是master位置()
    {
        var (sync, engine) = CreateSync(3);
        sync.Slots[1].Offset100ns = 1 * TimeSpan.TicksPerSecond;
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        sync.SeekTo(2 * TimeSpan.TicksPerSecond);

        // 源路在 canonical 2s + 自身偏移 1s = 3s；镜像必须同样在 3s
        var src = sync.Slots[1].Session.ReadSnapshot()!;
        var mir = sync.Slots[3].Session.ReadSnapshot()!;
        Assert.Equal(3 * TimeSpan.TicksPerSecond, src.Position100ns);
        Assert.Equal(src.Position100ns, mir.Position100ns);
    }

    [Fact]
    public void 组播放与暂停都覆盖镜像()
    {
        var (sync, engine) = CreateSync(2);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 0);

        sync.Play();
        Assert.Equal(PlayerState.Playing, sync.Slots[2].Session.ReadSnapshot()!.State);

        sync.Pause();
        Assert.Equal(PlayerState.Paused, sync.Slots[2].Session.ReadSnapshot()!.State);
    }

    /// <summary>漂移校正必须把镜像当普通 follower 拉回 —— 这是"不需要为镜像新写同步代码"的证据。
    /// <para>用注入时钟推进到漂移检测节拍，再把镜像<b>人为拨错</b>，断言校正把它拉回
    /// <c>masterPos + 源路偏移</c>。</para></summary>
    [Fact]
    public void 漂移校正把镜像拉回源路位置()
    {
        var clock = new ManualClock();
        var (sync, engine) = CreateSync(2, clock);
        sync.Slots[1].Offset100ns = 500_0000;      // 源路 0.5s 偏移
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);

        sync.Play();
        // 人为把镜像拨到错误位置（模拟"它自己跑偏了"）
        sync.Slots[2].Session.Seek(4 * TimeSpan.TicksPerSecond);

        var masterPos = sync.Slots[0].Session.ReadSnapshot()!.Position100ns;
        var expected = masterPos + 500_0000;

        // 推进到漂移检测节拍（间隔 1000ms）后显式跑一拍
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        sync.TickDrift();

        var mir = sync.Slots[2].Session.ReadSnapshot()!;
        Assert.True(Math.Abs(mir.Position100ns - expected) <= 30_0000,
            $"镜像未被校正回源路位置：实际 {mir.Position100ns}，期望 {expected}");
    }

    // ────────── 生命周期：不留孤儿、不泄漏 ──────────

    [Fact]
    public void 移除源路时其镜像一并移除()
    {
        var (sync, engine) = CreateSync(3);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 1);
        Assert.Equal(4, sync.Count);

        sync.RemoveSlotAt(1);       // 移除源路

        // 镜像必须一起走：留下它就是一个"镜像不存在的路"的孤儿槽，
        // 仍在解码 + Present（白烧资源），且漂移校正会拿越界源路号算期望位置。
        Assert.Equal(2, sync.Count);
        Assert.Empty(sync.Mirrors());
    }

    [Fact]
    public void 移除源路之前的槽_其余镜像的源路号跟着平移()
    {
        var (sync, engine) = CreateSync(4);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 2);   // 镜像 → 第 2 路

        sync.RemoveSlotAt(0);      // 删掉 master ⇒ 原第 2 路变成第 1 路

        var mirrors = sync.Mirrors();
        Assert.Single(mirrors);
        Assert.Equal(1, mirrors[0].Source);   // 源路号必须跟着 -1，否则镜像会指到邻居身上
    }

    [Fact]
    public void 镜像被移除时不连带移除源路()
    {
        var (sync, engine) = CreateSync(2);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 0);
        Assert.Equal(3, sync.Count);

        sync.RemoveSlotAt(2);       // 移除镜像本身

        Assert.Equal(2, sync.Count);
        Assert.Empty(sync.Mirrors());
        // 源路（第 0 路）必须还在
        Assert.NotNull(sync.Slots[0].Session.ReadSnapshot());
    }

    [Fact]
    public void 多镜像同一路时全部同位()
    {
        var (sync, engine) = CreateSync(2);
        sync.Slots[1].Offset100ns = 1 * TimeSpan.TicksPerSecond;
        sync.AddMirror(NewMirrorSession(engine), "m1.mp4", sourceIndex: 1);
        sync.AddMirror(NewMirrorSession(engine), "m2.mp4", sourceIndex: 1);

        sync.SeekTo(2 * TimeSpan.TicksPerSecond);

        var src = sync.Slots[1].Session.ReadSnapshot()!.Position100ns;
        Assert.Equal(3 * TimeSpan.TicksPerSecond, src);
        Assert.Equal(src, sync.Slots[2].Session.ReadSnapshot()!.Position100ns);
        Assert.Equal(src, sync.Slots[3].Session.ReadSnapshot()!.Position100ns);
    }

    // ────────── 段计数：决定"需要几个镜像" ──────────

    /// <summary>"同一路出现在几条<b>不相邻</b>的带上就需要几个镜像"这条算术的判据。
    ///
    /// <para><b>为什么放在这里、以及它的边界</b>：该规则的生产实现在
    /// <c>MainWindow.SyncWipeMirrors</c>（UI 层，依赖 Grid/Sync/窗口 ⇒ 无法直接单测）。
    /// 生产实现若漂移，**本判据不会自动判红** —— 这是刻意的取舍：
    /// 与其让 UI 层的算术完全没有覆盖，不如把规则在这里钉成可读的规范
    /// （两侧必须一致），真机侧再由 <c>--comparemodetest</c> 的镜像探针覆盖
    /// "真的建出了镜像、且与源路同位"。</para></summary>
    [Theory]
    // routes 里路 0 的"连续段数"− 1 = 需要的镜像数
    [InlineData(new[] { 0, 1 }, 0)]              // 无重复
    [InlineData(new[] { 0, 0 }, 0)]              // 相邻重复 ⇒ 区域并集可表达，不需要镜像
    [InlineData(new[] { 0, 1, 0 }, 1)]           // 两段 ⇒ 1 个镜像
    [InlineData(new[] { 0, 0, 1, 0, 0 }, 1)]     // 两段（各两带）⇒ 1 个镜像
    [InlineData(new[] { 0, 1, 0, 1, 0 }, 2)]     // 三段 ⇒ 2 个镜像
    [InlineData(new[] { 0, 1, 2, 0 }, 1)]        // 两段 ⇒ 1 个镜像
    [InlineData(new[] { 0, 1, 1, 0, 1, 1 }, 1)]  // 路 0 两段 ⇒ 1
    public void 段计数决定需要几个镜像(int[] routes, int expectedForRoute0)
    {
        var positions = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
        for (var i = 0; i < routes.Length; i++)
        {
            if (!positions.TryGetValue(routes[i], out var list))
                positions[routes[i]] = list = new System.Collections.Generic.List<int>();
            list.Add(i);
        }

        // 复刻生产实现（MainWindow.SyncWipeMirrors）的段计数
        var neededFor0 = 0;
        if (positions.TryGetValue(0, out var p0) && p0.Count >= 2)
        {
            var segments = 1;
            for (var k = 1; k < p0.Count; k++)
                if (p0[k] != p0[k - 1] + 1) segments++;
            if (segments > 1) neededFor0 = segments - 1;
        }

        Assert.Equal(expectedForRoute0, neededFor0);
    }

    // ────────── 边界：镜像链与越界（审查轮补充） ──────────

    /// <summary>镜像的源**必须是一个真实路**，不能是另一个镜像。
    ///
    /// <para><b>为什么这条重要</b>：<c>AddMirror</c> 只做了 <c>[0, _slots.Count)</c> 的范围检查，
    /// 而镜像槽本身也在 <c>_slots</c> 里 ⇒ 传一个镜像槽的号是**能通过范围检查的**。
    /// 那会造出"镜像链"：m2 的偏移跟随 m1，而 m1 跟随真实源路；
    /// 于是 <c>ResyncMirrorOffsets</c> 若在中间层未对齐时读它，m2 就与真实源路错开 ——
    /// 而"同一路错开"正是镜像最不能出的错。</para>
    ///
    /// <para>本判据钉住<b>期望行为</b>：拒绝把镜像当源（返回 false、不追加）。</para></summary>
    [Fact]
    public void 拒绝把镜像当作镜像源_不允许镜像链()
    {
        var (sync, engine) = CreateSync(3);
        Assert.True(sync.AddMirror(NewMirrorSession(engine), "m1.mp4", sourceIndex: 1));   // 源是真实路
        var mirrorSlot = sync.Mirrors()[0].Index;

        var ok = sync.AddMirror(NewMirrorSession(engine), "m2.mp4", sourceIndex: mirrorSlot);

        Assert.False(ok, "不应允许以镜像槽为源（会形成镜像链，偏移跟错层）");
        Assert.Single(sync.Mirrors());
    }

    /// <summary>移除槽之后，所有镜像的 <c>MirrorOf</c> 必须仍在 <c>[0, Count)</c> 内。
    ///
    /// <para>越界的后果：漂移校正每拍拿它算 <c>masterPos + 源路偏移</c>，
    /// 而 <c>_slots[越界]</c> 会抛 <c>ArgumentOutOfRangeException</c>；
    /// 那条异常在 <c>try</c> 之外会冒到轮询顶层被吞，表现为"位置/时间码停更"。
    /// 这里遍历多个删点（含 master）逐个检查不变量。</para></summary>
    [Theory]
    [InlineData(0)]   // 删 master（触发偏移重基准）
    [InlineData(1)]   // 删中间的普通路
    [InlineData(4)]   // 删源路本身（镜像应一并消失）
    public void 移除槽后镜像源恒不越界(int removeIndex)
    {
        var (sync, engine) = CreateSync(5);
        sync.AddMirror(NewMirrorSession(engine), "m.mp4", sourceIndex: 4);

        sync.RemoveSlotAt(removeIndex);

        foreach (var (idx, src) in sync.Mirrors())
        {
            Assert.True(idx >= 0 && idx < sync.Count, $"镜像槽号越界：{idx}（共 {sync.Count} 槽）");
            Assert.True(src >= 0 && src < sync.Count,
                $"镜像源越界：槽 {idx} 的源 {src}（共 {sync.Count} 槽）—— 漂移校正会拿它索引而抛异常");
        }
    }

    /// <summary>反复增删镜像后，镜像与源路仍必须同位（生命周期 + 同步的联合判据）。
    ///
    /// <para>只判"移除源路时镜像也没了"不够：真实使用里用户会反复改「选择路」，
    /// 镜像被反复建/删。这里模拟"建 → 校验同位 → 删 → 再建"，每轮都验同位。</para></summary>
    [Fact]
    public void 反复增删镜像后仍与源路同位()
    {
        var (sync, engine) = CreateSync(3);

        for (var round = 0; round < 3; round++)
        {
            Assert.True(sync.AddMirror(NewMirrorSession(engine), $"m{round}.mp4", sourceIndex: 2),
                $"第 {round} 轮 AddMirror 应成功");
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);

            var src = sync.Slots[2].Session.ReadSnapshot()!.Position100ns;
            var mir = sync.Mirrors()[0];
            Assert.Equal(src, sync.Slots[mir.Index].Session.ReadSnapshot()!.Position100ns);

            sync.RemoveSlotAt(mir.Index);       // 模拟"选择路"改回不重复
            Assert.Empty(sync.Mirrors());
            Assert.Equal(3, sync.Count);
        }
    }
}
