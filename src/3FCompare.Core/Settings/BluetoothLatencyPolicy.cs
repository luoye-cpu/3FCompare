namespace _3FCompare.Core.Settings;

/// <summary>当前默认输出端点的蓝牙音频画像（由平台层探测填充，Core 只做纯计算）。
///
/// <para><b>为什么把"探测"和"补偿算法"分开</b>：探测依赖 Windows SetupAPI/注册表/COM，
/// 只能在 App 工程里做；而补偿量是纯算术，必须能在无 Windows 的测试里断言。
/// 本记录只承载事实（profile / 区间 / 设备名 / 状态），不承载任何 UI 文案。</para></summary>
public sealed record BluetoothAudioInfo
{
    /// <summary>默认输出端点是否是蓝牙音频设备。</summary>
    public bool IsBluetooth { get; init; }

    /// <summary>端点当前是否可播放（DeviceState=1）。未连接/禁用时为 false。</summary>
    public bool IsConnected { get; init; }

    /// <summary>profile 类别。None = 非蓝牙；Unknown = 是蓝牙但服务未识别。</summary>
    public BluetoothAudioProfile Profile { get; init; }

    /// <summary>展示用 profile 名（A2DP / HFP / LE-Audio / Bluetooth）。</summary>
    public string ProfileName { get; init; } = "";

    /// <summary>耳机侧单程延迟下界（ms，不含主机缓冲）。0 = 未知。</summary>
    public int LatencyLowMs { get; init; }

    /// <summary>耳机侧单程延迟上界（ms，不含主机缓冲）。0 = 未知。</summary>
    public int LatencyHighMs { get; init; }

    /// <summary>设备型号（优先取 BTHENUM 节点 FriendlyName 括号内的型号）。</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>MMDevices 端点名（例如"耳机"）。</summary>
    public string EndpointName { get; init; } = "";

    /// <summary>设备 MAC（AA:BB:CC:DD:EE:FF），取不到为空。</summary>
    public string Mac { get; init; } = "";

    /// <summary>MMDevices 端点 ID（SWD\MMDEVAPI\{...}）。</summary>
    public string EndpointId { get; init; } = "";

    /// <summary>MMDevices DeviceState 原值：1=ACTIVE，2=DISABLED，4=NOTPRESENT，8=UNPLUGGED。</summary>
    public int EndpointState { get; init; }

    /// <summary>探测细节/失败原因，供状态行与日志使用。</summary>
    public string Detail { get; init; } = "";

    /// <summary>取区间中位作为估计值。未知 profile 返回 0（由策略层兜底）。</summary>
    public int EstimatedLatencyMs =>
        LatencyLowMs > 0 && LatencyHighMs > 0
            ? (LatencyLowMs + LatencyHighMs) / 2
            : 0;

    public static readonly BluetoothAudioInfo NonBluetooth = new()
    {
        IsBluetooth = false,
        IsConnected = false,
        Profile = BluetoothAudioProfile.None,
        ProfileName = "",
        Detail = "当前默认输出不是蓝牙端点",
    };
}

public enum BluetoothAudioProfile
{
    None = 0,
    A2dp = 1,
    Hfp = 2,
    LeAudio = 3,
    Unknown = 4,
}

/// <summary>蓝牙延迟补偿的纯算术层。
///
/// <para>补偿方向来自内核契约：<c>FFF3FP_SetAudioDelay</c> 的
/// <b>正值 = 音频比视频晚播</b>（`FFF.Player.Api.h:981`）。蓝牙让声音晚到
/// <c>L_BT</c>，而屏幕让画面晚到 <c>L_disp</c>，所以要让两者同刻：
/// <c>d = L_disp - L_BT</c>（通常为负 = 音频提前）。</para>
///
/// <para><b>为什么不能只补 <c>-L_BT</c></b>：Present 到像素发光还有 DWM 合成 + 扫描 +
/// 面板响应。4K/60Hz 约 25–50ms，4K/144Hz 约 10–25ms。漏掉这一项会在 60Hz 屏上
/// 多提前 25–50ms，表现为"声音反而领先画面"。</para>
///
/// <para><c>L_disp</c> 的估计刻意用<b>刷新周期</b>而不是实测：设置页打开时还没有
/// 播放会话，拿不到 Present 统计；2 个刷新周期 + 5ms 是保守上界，足以覆盖
/// DWM 一帧 + 扫描半帧 + 像素响应，误差由设置页的"额外提前量"微调吸收。</para></summary>
public static class BluetoothLatencyPolicy
{
    /// <summary>内核允许的音频延迟上下限：±10 分钟（100ns 单位）。
    /// 10 min = 600 s × 10,000,000 = 6,000,000,000。</summary>
    public const long MaxAbsDelay100ns = 6_000_000_000;

    /// <summary>未知蓝牙的保守区间：按 A2DP/SBC 上界兜底。</summary>
    public const int UnknownLowMs = 150;
    public const int UnknownHighMs = 300;

    /// <summary>profile 级延迟区间（耳机侧，不含主机 60 ms 缓冲）。</summary>
    public static (int LowMs, int HighMs) RangeFor(BluetoothAudioProfile profile) => profile switch
    {
        BluetoothAudioProfile.LeAudio => (30, 100),
        BluetoothAudioProfile.Hfp => (100, 250),
        BluetoothAudioProfile.A2dp => (150, 300),
        BluetoothAudioProfile.Unknown => (UnknownLowMs, UnknownHighMs),
        _ => (0, 0),
    };

    /// <summary>按刷新率估计"媒体时钟到期 → 像素发光"的软件侧延迟。
    /// 2 个刷新周期（DWM 合成 + 扫描）+ 5 ms（面板响应/调度余量），钳到 [10, 60] ms。</summary>
    public static double EstimateDisplayLatencyMs(int refreshHz)
    {
        var hz = refreshHz is >= 24 and <= 500 ? refreshHz : 60;
        var ms = 2.0 * 1000.0 / hz + 5.0;
        return Math.Clamp(ms, 10.0, 60.0);
    }

    /// <summary>计算应下发的 <c>SetAudioDelay</c> 值（100ns）。
    ///
    /// <param name="enabled">设置页总开关。</param>
    /// <param name="info">默认输出探测结果；null/非蓝牙/未连接时返回 0。</param>
    /// <param name="refreshHz">窗口所在显示器刷新率；非法值按 60。</param>
    /// <param name="extraLeadMs">用户额外提前量（正=音频更早），钳到 [-100,100]。</param>
    /// </summary>
    public static long ComputeDelay100ns(
        bool enabled, BluetoothAudioInfo? info, int refreshHz, int extraLeadMs)
    {
        if (!enabled || info is null || !info.IsBluetooth || !info.IsConnected)
            return 0;

        var low = info.LatencyLowMs;
        var high = info.LatencyHighMs;
        if (low <= 0 || high <= 0 || high < low)
        {
            (low, high) = RangeFor(info.Profile == BluetoothAudioProfile.None
                ? BluetoothAudioProfile.Unknown
                : info.Profile);
        }
        if (low <= 0 || high <= 0) return 0;

        var btMs = (low + high) / 2.0;
        var displayMs = EstimateDisplayLatencyMs(refreshHz);
        var trim = Math.Clamp(extraLeadMs, -100, 100);

        // 正值=音频更晚；蓝牙通常让音频晚到，所以结果为负（音频提前）。
        var delayMs = displayMs - btMs - trim;
        var delay100ns = (long)Math.Round(delayMs * 10_000.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(delay100ns, -MaxAbsDelay100ns, MaxAbsDelay100ns);
    }

    /// <summary>把延迟值格式化为带符号毫秒（仅用于日志/诊断，不产生 UI 文案）。</summary>
    public static string FormatMs(long delay100ns)
        => $"{(delay100ns / 10_000.0):+0.0;-0.0;0} ms";
}
