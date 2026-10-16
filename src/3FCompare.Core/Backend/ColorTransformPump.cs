using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Core.Backend;

/// <summary>把"帧边数据环 → 外部模块 → kind=3 注入"这条通路串起来的泵。
/// <para>每一帧（或每次取表时机）跑一次 <see cref="PumpOnce"/>：游标取一条边数据 →
/// 交给换算器（产品路径就是 <see cref="ColorTransformModule.TryAnalyzeFrame"/>，
/// 见 <see cref="TryAnalyzeSideData"/> 那条接缝）→ 拿到表就注入。
/// 模块缺席 / 内核没有环导出 / 本帧无据，三种情况都只是"什么都不做"，
/// 与今天的行为逐位一致（既定口径：不装模块 = 回落 HDR10 基底且不提示）。</para>
/// <para><b>一路会话一个泵、一个模块实例</b>：模块的 <c>reset()</c> 是"丢弃逐流状态"，
/// 多路对比共用一个实例时，一路的 seek 会把别的路的模块侧状态也清掉。
/// <see cref="ColorTransformModule"/> 的缓冲区是实例级的，所以实例级隔离也是必需的。</para>
/// <para><b>注入是整表替换语义</b>：内核 <c>InstallInjectedTable</c> 用这一批把现有表整体换掉，
/// 所以本泵装 kind=3 时会把宿主另外装载的 HDR10+/Vivid 表一起重发（见
/// <see cref="CompanionPackets"/>）——只发一条就等于悄悄清空别人的表。</para>
/// <para>本泵只保留<b>最新一条</b> kind=3 条目：内核的匹配规则是"最后一条 ≤ 当前帧"，
/// 游标又只前进，旧条目永远不会再被选中，留着只占内核的 16 条 kind=3 配额
/// （<c>kMaxExternalTransformEntries</c>，超了整批 InvalidArgument）。</para>
/// </summary>
public sealed class ColorTransformPump
{
    /// <summary>FFmpeg <c>AV_FRAME_DATA_DYNAMIC_HDR_PLUS</c> 的实测数值（见 Fff3FpNative 注释）。</summary>
    public const uint SideDataTypeHdr10Plus = 17;

    /// <summary><c>AV_FRAME_DATA_DOVI_RPU_BUFFER</c>。</summary>
    public const uint SideDataTypeDoviRpuBuffer = 23;

    /// <summary><c>AV_FRAME_DATA_DOVI_METADATA</c>：外部模块当前吃的类型。</summary>
    public const uint SideDataTypeDoviMetadata = 24;

    /// <summary><c>AV_FRAME_DATA_DYNAMIC_HDR_VIVID</c>。</summary>
    public const uint SideDataTypeHdrVivid = 25;

    private readonly IPlayerSession _session;
    private readonly TryAnalyzeSideData? _analyze;
    private readonly Action? _resetAnalyzer;
    private readonly FrameSideDataCursor? _cursor;
    private readonly uint _sideDataType;

    /// <summary>逐帧换算的可替换接缝：产品路径绑 <see cref="ColorTransformModule.TryAnalyzeFrame"/>，
    /// 测试路径绑脚本化的假分析器。<b>理由</b>：模块实例只能从盘上的 DLL 装载出来，
    /// 而"取边数据 → 换算 → 注入 kind=3"这条编排里真正需要被钉住的是后半段
    /// （整表替换语义、伴报表、计数器、游标推进），不该因为缺一个可选件就测不到。</summary>
    internal delegate bool TryAnalyzeSideData(ReadOnlySpan<byte> sideData, long frameIndex,
        out ColorTransformTable? table, out string? error);

    public ColorTransformPump(IPlayerSession session, ColorTransformModule? module,
        uint sideDataType = SideDataTypeDoviMetadata)
        : this(session,
               module is null ? null : new TryAnalyzeSideData(module.TryAnalyzeFrame),
               sideDataType,
               module is null ? null : new Action(module.Reset))
    {
    }

    /// <summary>接缝构造：换算器与它的 reset 都由调用方给（测试专用）。</summary>
    internal ColorTransformPump(IPlayerSession session, TryAnalyzeSideData? analyze,
        uint sideDataType, Action? resetAnalyzer = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _analyze = analyze;
        _resetAnalyzer = resetAnalyzer;
        _sideDataType = sideDataType;
        // 会话不是 3FP 后端（演示模式）或盘上内核没有环导出时，游标建不起来：
        // 这不是错误，是"这条路不存在"，泵此后恒不注入。
        _cursor = session is IFrameSideDataRing ring ? new FrameSideDataCursor(ring) : null;
    }

    /// <summary>这条通路是否可用（换算器在、会话提供环）。false 时 PumpOnce 恒返回 false。</summary>
    public bool IsSupported => _analyze is not null && _cursor is not null;

    /// <summary>游标位置（内部可见：测试用它钉"只前进"与"复位"两件事）。
    /// -2 表示这条会话根本没有环。</summary>
    internal long CursorFromFrameIndex => _cursor?.FromFrameIndex ?? -2L;

    /// <summary>宿主自己的 HDR10+/Vivid 表：本泵每次注入时把它们与 kind=3 条目一并重发，
    /// 以免"整表替换"把别人的表清掉。null/空表示宿主没有别的表。</summary>
    public IReadOnlyList<DynamicMetadataPacket>? CompanionPackets { get; set; }

    /// <summary>最近一次成功注入所针对的 presentation 帧号（-1 = 还没注过）。</summary>
    public long LastInjectedFrameIndex { get; private set; } = -1;

    /// <summary>累计成功注入的帧数（诊断用：与快照里的 dyn 计数对照可归因"表在跑但画面没变"）。</summary>
    public uint InjectedFrames { get; private set; }

    /// <summary>累计"取到边数据但这一帧没有形成新注入"的帧数。
    /// <para>三种原因都记在这里，读数本身分不开，只能靠日志那一行分：
    /// ① 模块答 NoMetadata（本帧无据）；② 模块/换算失败；③ 内核拒绝整批注入。
    /// 三者都不构成"通路坏了"，但也都不该被读成"这帧做了正确的事"。</para></summary>
    public uint SkippedFrames { get; private set; }

    /// <summary>累计"换算出表但内容与上一条逐位相同 ⇒ 没有重发"的帧数。
    /// <para>为什么必须有这一格：载荷是 431 296 字节，按帧重发等于每秒几十兆的大对象分配
    /// （<c>ColorTransformPayloadMarshal.BuildPinnedBuffer</c> 每一次都要新开并钉住一整块），
    /// 而"内核里已经躺着内容相同的一条 ≤ 当前帧"时重发在语义上是恒等操作，
    /// 只会把 GC 与 P/Invoke 的成本换成同一个画面。多这一格也让"模块在产出但画面没变"
    /// 与"模块压根没在产出"变成两个可读的数——不加的话它们都落在 SkippedFrames 里。</para>
    /// <para>默认关闭：单测钉的是"每取到一条就注入一次"这个形状。</para></summary>
    public bool SkipUnchangedPayload { get; set; }

    /// <summary>累计"内容与上一条一致而未重发"的帧数（<see cref="SkipUnchangedPayload"/> 打开时才有意义）。</summary>
    public uint UnchangedFrames { get; private set; }

    private ColorTransformTable? _lastInjectedTable;
    private string? _lastLoggedError;

    /// <summary>计数器的一句话摘要（会话收尾时写一条日志，让"通路到底跑没跑"可读）。</summary>
    public string DescribeCounters() =>
        $"注入 {InjectedFrames} 帧，未变 {UnchangedFrames} 帧，跳过 {SkippedFrames} 帧，末帧 {LastInjectedFrameIndex}";

    /// <summary>跑一趟。返回 true 表示本帧注入了新表。
    /// <paramref name="error"/> 只在"模块报了真失败"时非空——环里没有、本帧无据都是正常回答，
    /// 不该留痕（否则每帧一条日志会把日志刷满）。</summary>
    public bool PumpOnce(out string? error)
    {
        error = null;
        if (!IsSupported || _cursor is null) return false;

        if (!_cursor.TryReadNext(_sideDataType, out var frameIndex, out var bytes))
            return false;

        if (_analyze is null) return false;
        if (!_analyze(bytes, frameIndex, out var table, out error))
        {
            SkippedFrames++;
            LogOnce(error, frameIndex);
            return false;
        }
        if (table is null) return false;   // 防御：换算器成功时不会给 null

        // 内容未变 ⇒ 不重发：见 SkipUnchangedPayload 的注释。
        // 表里那条旧条目的帧号仍在"最后一条 ≤ 当前帧"的匹配范围里 ⇒ 画面与重发完全相同。
        if (SkipUnchangedPayload && SameContent(table, _lastInjectedTable))
        {
            UnchangedFrames++;
            return false;
        }

        if (!TryInject(_session, table, frameIndex, CompanionPackets, out error))
        {
            SkippedFrames++;
            LogOnce(error, frameIndex);
            return false;
        }

        _lastInjectedTable = table;
        LastInjectedFrameIndex = frameIndex;
        InjectedFrames++;
        // 首次注入是"解锁态下整条链路真的跑通"的唯一正证据，值得一条 Info：
        // 环里取到条目 → 模块换算出 431296 字节的表 → 内核收下这一批。
        // 之后的每一帧都不再留痕（否则每秒几十行）。
        if (InjectedFrames == 1)
            AppLog.Info("ColorTransformPump", $"kind=3 首次注入成功（帧 {frameIndex}，载荷 {table.LutDomain} 域）");
        return true;
    }

    /// <summary>两张表是否逐位相同（域 + 矩阵 + LUT）。表不可变，故只比内容不比引用。</summary>
    internal static bool SameContent(ColorTransformTable candidate, ColorTransformTable? previous) =>
        previous is not null
        && candidate.LutDomain == previous.LutDomain
        && candidate.MatrixStorage.AsSpan().SequenceEqual(previous.MatrixStorage)
        && candidate.CurveLutStorage.AsSpan().SequenceEqual(previous.CurveLutStorage);

    /// <summary>把一张表装成"伴报表 + 这一条 kind=3 条目"并注入。
    /// <para>拆成独立方法是因为它是整条通路唯一会改内核状态的一步，
    /// 而"注入是整表替换"这个语义必须能被单独钉住（见 ColorTransformPumpTests）。</para></summary>
    internal static bool TryInject(IPlayerSession session, ColorTransformTable table,
        long frameIndex, IReadOnlyList<DynamicMetadataPacket>? companion, out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        error = null;

        var packets = new List<DynamicMetadataPacket>((companion?.Count ?? 0) + 1);
        if (companion is not null) packets.AddRange(companion);
        packets.Add(table.ToPacket(frameIndex));

        // SetDynamicMetadata 的返回语义：false = 内核不支持该导出或整批被拒（旧表续任）。
        // 被拒是模块产出与内核契约的分歧，值得留痕——但**留痕由调用方按"同一原因只记一次"
        // 决定**：内核会连着每一帧都拒同一张表，这里直接记就是把日志刷满。
        if (!session.SetDynamicMetadata(packets))
        {
            error = $"内核拒绝了 kind=3 注入（帧 {frameIndex}）";
            return false;
        }
        return true;
    }

    /// <summary>同一句失败原因只在第一次出现时留痕。
    /// <para>理由：授权中途失效（Unauthorized）、内核整批拒绝这类情况会在**之后每一帧**
    /// 报同一句话，逐条记等于用一条复读把同时段真正有用的行埋掉。换原因了才再记一条。</para>
    /// <para>换算失败与注入失败共用这一个字段是有意的：两条路不会同时给出同一帧的原因，
    /// 而复读抑制要防的是"同一段字"刷屏，不是防它来自哪一站。</para></summary>
    private void LogOnce(string? error, long frameIndex)
    {
        if (error is null || string.Equals(error, _lastLoggedError, StringComparison.Ordinal)) return;
        _lastLoggedError = error;
        AppLog.Debug("ColorTransformPump",
            $"帧 {frameIndex} 未形成注入：{error}（同一原因的后续帧不再逐条记）");
    }

    /// <summary>流状态重置：seek、SeekFrame、Stop、换文件、解码器重建之后必须调。
    /// <para>两件事缺一不可：游标复位（presentation index 从那里重来，内核同时把环丢了）、
    /// 模块侧的逐流状态丢弃（<c>reset()</c> 契约：任何时刻都安全）。</para>
    /// <para>漏掉这一手的表现不是崩，是"注入静默停摆"——游标停在一个已经不存在的
    /// 高帧号上，之后每一趟都答 NotFound。</para></summary>
    public void OnStreamRestart()
    {
        _cursor?.Reset();
        _resetAnalyzer?.Invoke();
        LastInjectedFrameIndex = -1;
        // 重启之后必须**重新注入一次**，即使模块算出的表与重启前逐位相同：
        // 注入是整表替换，内核里留着的是带旧帧号的那一条；presentation index 从 0 重来
        // ⇒ 旧条目的帧号大于当前帧，永远选不中 ⇒ 画面会掉回基底，直到下一次内容变化。
        // （把它清掉，重启后的第一帧就一定重发一次，把帧号带回到当前区间。）
        _lastInjectedTable = null;
    }
}
