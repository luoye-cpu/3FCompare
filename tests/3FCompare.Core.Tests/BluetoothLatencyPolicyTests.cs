using _3FCompare.Core.Settings;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>蓝牙延迟策略的纯算术断言。
///
/// <para>核心契约：<c>d = L_disp - L_BT</c>，正值=音频更晚。蓝牙让音频晚到，
/// 所以正常情况下结果应为负；若有人把符号写反（只补 <c>-L_BT</c> 或把
/// <c>L_disp</c> 漏掉），下面 60Hz/144Hz 两组用例都会失败。</para></summary>
public sealed class BluetoothLatencyPolicyTests
{
    private static BluetoothAudioInfo A2dp(int low = 150, int high = 300) => new()
    {
        IsBluetooth = true,
        IsConnected = true,
        Profile = BluetoothAudioProfile.A2dp,
        ProfileName = "A2DP",
        LatencyLowMs = low,
        LatencyHighMs = high,
        DeviceName = "Test Buds",
    };

    [Fact]
    public void Disabled_ReturnsZero()
    {
        Assert.Equal(0, BluetoothLatencyPolicy.ComputeDelay100ns(
            enabled: false, A2dp(), refreshHz: 60, extraLeadMs: 0));
    }

    [Fact]
    public void NonBluetooth_ReturnsZero()
    {
        Assert.Equal(0, BluetoothLatencyPolicy.ComputeDelay100ns(
            enabled: true, BluetoothAudioInfo.NonBluetooth, refreshHz: 60, extraLeadMs: 0));
        Assert.Equal(0, BluetoothLatencyPolicy.ComputeDelay100ns(
            enabled: true, info: null, refreshHz: 60, extraLeadMs: 0));
    }

    [Fact]
    public void Disconnected_ReturnsZero()
    {
        var info = A2dp() with { IsConnected = false };
        Assert.Equal(0, BluetoothLatencyPolicy.ComputeDelay100ns(
            enabled: true, info, refreshHz: 60, extraLeadMs: 0));
    }

    [Fact]
    public void A2dp_On60Hz_IsNegative_AndIncludesDisplayLatency()
    {
        var d = BluetoothLatencyPolicy.ComputeDelay100ns(
            enabled: true, A2dp(), refreshHz: 60, extraLeadMs: 0);
        // L_disp=38.33ms, L_BT=225ms -> -186.67ms
        Assert.InRange(d, -1_870_000, -1_860_000);
        // 只补 -L_BT 会得到 -225ms；含 L_disp 后必须明显更靠近 0。
        Assert.True(d > -2_250_000);
    }

    [Fact]
    public void A2dp_On144Hz_CompensatesMoreThan60Hz()
    {
        var d60 = BluetoothLatencyPolicy.ComputeDelay100ns(true, A2dp(), 60, 0);
        var d144 = BluetoothLatencyPolicy.ComputeDelay100ns(true, A2dp(), 144, 0);
        // 144Hz 的 L_disp 更小（18.9 vs 38.3）⇒ 需要更长的提前量（更负）。
        Assert.True(d144 < d60);
        Assert.InRange(d144, -2_070_000, -2_050_000);
    }

    [Fact]
    public void ExtraLead_AddsToAdvance()
    {
        var baseline = BluetoothLatencyPolicy.ComputeDelay100ns(true, A2dp(), 60, 0);
        var earlier = BluetoothLatencyPolicy.ComputeDelay100ns(true, A2dp(), 60, 50);
        Assert.Equal(baseline - 500_000, earlier); // 50ms = 500,000 * 100ns
    }

    [Fact]
    public void LeAudio_IsLessNegativeThanA2dp()
    {
        var le = new BluetoothAudioInfo
        {
            IsBluetooth = true,
            IsConnected = true,
            Profile = BluetoothAudioProfile.LeAudio,
            LatencyLowMs = 30,
            LatencyHighMs = 100,
        };
        var dLe = BluetoothLatencyPolicy.ComputeDelay100ns(true, le, 60, 0);
        var dA2dp = BluetoothLatencyPolicy.ComputeDelay100ns(true, A2dp(), 60, 0);
        Assert.True(dLe > dA2dp);
        Assert.InRange(dLe, -270_000, -260_000); // 38.3 - 65 = -26.7ms
    }

    [Fact]
    public void UnknownProfile_FallsBackToConservativeRange()
    {
        var unknown = new BluetoothAudioInfo
        {
            IsBluetooth = true,
            IsConnected = true,
            Profile = BluetoothAudioProfile.Unknown,
            LatencyLowMs = 0,
            LatencyHighMs = 0,
        };
        var d = BluetoothLatencyPolicy.ComputeDelay100ns(true, unknown, 60, 0);
        Assert.InRange(d, -1_870_000, -1_860_000); // 与 A2DP 中位 225ms 同档
    }

    [Fact]
    public void EstimateDisplayLatency_Clamps()
    {
        Assert.Equal(10.0, BluetoothLatencyPolicy.EstimateDisplayLatencyMs(500)); // 9ms -> 钳到 10
        Assert.Equal(60.0, BluetoothLatencyPolicy.EstimateDisplayLatencyMs(30));  // 71.7ms -> 钳到 60
    }
}
