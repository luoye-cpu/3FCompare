using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="ColorModeHelper.Resolve"/>（docs/41 §4.5 #1）。
///
/// <para><b>为什么必须测</b>：它是"设置里的色彩模式 → 内核 ColorMode"的唯一转换点。
/// <see cref="ColorModeSetting"/> 与 <see cref="ColorMode"/> 是<b>两套不同的枚举</b>，
/// 且前者多出一个 Auto=3 —— 3 在后者里根本不存在。任何"直接强转"的写法在 Auto 下都会
/// 造出 <c>(ColorMode)3</c> 这个越界值喂给内核。</para>
///
/// <para><b>期望值来源（独立推算）</b>：全部是目标枚举 <see cref="ColorMode"/> 自身的常量
/// （MapToSdr=0 / RawHdrAsSdr=1 / MapToHdr=2，见 IPlayerEngine.cs:141-146），
/// 由人工读源码后写成字面量，<b>不是</b>用 Resolve 或它的兄弟函数算出来的。
/// 分支规则（Auto 看 Supported、null 视为无 HDR）来自 AppSettings.cs:214-216 的文档注释。</para>
/// </summary>
public class ColorModeHelperTests
{
    /// <summary>Auto + 显示器上报 Supported=true ⇒ 内核 MapToHdr。
    /// 同时断言 int 值 2：证明结果是按语义选出来的，而不是把 Auto(3) 原样强转。</summary>
    [Fact]
    public void Auto模式且显示器支持HDR时解析为MapToHdr()
    {
        var caps = new DisplayLuminanceCapabilities
        {
            Supported = true,
            MaximumNits = 1000f,
            MinimumNits = 0.05f,
            FullFrameNits = 600f,
        };

        var resolved = ColorModeHelper.Resolve(ColorModeSetting.Auto, caps);

        Assert.Equal(ColorMode.MapToHdr, resolved);
        Assert.Equal(2, (int)resolved);
    }

    /// <summary>Auto + caps=null（旧系统 / DXGI&lt;1.6 / 读取失败）⇒ 必须落到 SDR，不能抛。</summary>
    [Fact]
    public void Auto模式且能力读取失败时解析为MapToSdr()
    {
        var resolved = ColorModeHelper.Resolve(ColorModeSetting.Auto, null);

        Assert.Equal(ColorMode.MapToSdr, resolved);
        Assert.Equal(0, (int)resolved);
    }

    /// <summary>Auto + 显示器明确上报 Supported=false ⇒ SDR。
    /// 注意这里 MaximumNits 给足 1000 却仍须走 SDR —— 判据是 Supported 而不是亮度，
    /// 所以"亮度高就当 HDR"的写法会在这里判红。</summary>
    [Fact]
    public void Auto模式且显示器不支持HDR时解析为MapToSdr()
    {
        var caps = new DisplayLuminanceCapabilities
        {
            Supported = false,
            MaximumNits = 1000f,
            MinimumNits = 0.05f,
            FullFrameNits = 600f,
        };

        Assert.Equal(ColorMode.MapToSdr, ColorModeHelper.Resolve(ColorModeSetting.Auto, caps));
    }

    /// <summary>非 Auto 的三个取值必须原样映射，且<b>不受显示器能力影响</b>。
    /// 期望值 = ColorMode 常量本身（int 0/1/2），人工对照两套枚举的定义写死。</summary>
    [Theory]
    [InlineData(ColorModeSetting.MapToSdr, ColorMode.MapToSdr, 0)]
    [InlineData(ColorModeSetting.RawHdrAsSdr, ColorMode.RawHdrAsSdr, 1)]
    [InlineData(ColorModeSetting.MapToHdr, ColorMode.MapToHdr, 2)]
    public void 非Auto设置原样映射且忽略显示器能力(
        ColorModeSetting setting, ColorMode expected, int expectedInt)
    {
        // 两种能力取值都必须给出同一结果 —— 非 Auto 路径不得偷看 caps
        var hdrCaps = new DisplayLuminanceCapabilities { Supported = true };

        Assert.Equal(expected, ColorModeHelper.Resolve(setting, hdrCaps));
        Assert.Equal(expected, ColorModeHelper.Resolve(setting, null));
        Assert.Equal(expectedInt, (int)ColorModeHelper.Resolve(setting, hdrCaps));
    }
}
