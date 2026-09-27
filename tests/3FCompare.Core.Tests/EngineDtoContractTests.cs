using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// DTO 默认值契约（docs/41 §4.5 #5）。
///
/// <para><b>为什么必须测</b>：这些默认值会被<b>静默</b>送进原生内核（<c>FFF3FP_Create</c>），
/// 写错不会有编译错误，只表现为"实机行为与预期不同"。三个最要紧的点：</para>
/// <list type="number">
/// <item><description><see cref="EngineSessionOptions.HardwareDecode"/> 默认必须是 <c>true</c>：
/// 若默认 false，所有未显式设置的调用点会悄悄退化成 CPU 解码，9 路 4K 直接跑不动。</description></item>
/// <item><description><see cref="EngineSessionOptions.PreferredAdapterIndex"/> 默认必须是 <c>-1</c>
/// （= 系统默认）：默认 0 会在双卡机器上把解码钉死在第一张卡上。</description></item>
/// <item><description><see cref="EngineMediaInfo.BitDepth"/> 默认必须是 <c>8</c>：
/// 老内核不返回位深字段，默认 10/16 会让"8bit 源"被当成高位深源走错色彩管线。</description></item>
/// </list>
///
/// <para><b>期望值来源（独立推算）</b>：全部人工对照声明处的属性初始化器与 XML 注释
/// （IPlayerEngine.cs:95-121 / :201 / :234-239）写成字面量，不调用任何被测成员。</para>
/// </summary>
public class EngineDtoContractTests
{
    /// <summary>默认构造出的会话选项必须与内核约定的"安全缺省"一致。
    /// 注意 <c>OutputWindow</c> 默认 0（无窗口）——它只表示"尚未绑定表面"。</summary>
    [Fact]
    public void 会话选项的默认值与内核约定的安全缺省一致()
    {
        var options = new EngineSessionOptions();

        Assert.Equal((nint)0, options.OutputWindow);
        Assert.True(options.HardwareDecode);            // 默认走 GPU 解码
        Assert.Equal(-1, options.PreferredAdapterIndex); // -1 = 系统默认适配器
        Assert.Equal(ColorMode.MapToSdr, options.ColorMode); // 默认不假设 HDR 显示器
        Assert.False(options.ForceHdrOutput);            // 不绕过显示器能力门控
        Assert.False(options.TearingPresent);            // 默认 VSync 锁定（盯帧推荐）
    }

    /// <summary>媒体信息 DTO 的位深默认值必须是 8：<c>BitDepth</c> 是"解码位深"，
    /// 缺字段时按 8bit 处理（IPlayerEngine.cs:201）。</summary>
    [Fact]
    public void 媒体信息位深默认值为8()
    {
        var info = new EngineMediaInfo { Path = "/x.mp4", Codec = "h264" };

        Assert.Equal(8, info.BitDepth);
        // 同一份默认构造里其余数值字段也应为 0 / false，避免"意外非零默认"
        Assert.Equal(0, info.VideoWidth);
        Assert.Equal(0, info.VideoHeight);
        Assert.Equal(0.0, info.FrameRate, 9);
        Assert.False(info.IsHdr);
        Assert.False(info.Interlaced);
        Assert.Null(info.PixelFormat);
        Assert.Null(info.HdrFormat);
    }

    /// <summary><see cref="EngineException.Result"/> 必须原样保留内核返回的 FFFResult 码，
    /// 且消息原样透传 —— 上层的"错误码→用户提示"映射全靠它（IPlayerEngine.cs:234-239）。</summary>
    [Fact]
    public void 引擎异常保留内核结果码与消息()
    {
        var ex = new EngineException(-2004287487, "FFmpeg: 打不开输入");

        Assert.Equal(-2004287487, ex.Result);
        Assert.Equal("FFmpeg: 打不开输入", ex.Message);
        Assert.IsAssignableFrom<Exception>(ex); // 必须可被通用 catch 捕获
    }

    /// <summary>Result 必须是<b>只读</b>属性：它对应一次失败的内核调用，可写就等于允许
    /// 事后篡改归因依据。这里用反射确认没有 setter（独立于被测代码的元数据断言）。</summary>
    [Fact]
    public void 引擎异常的结果码属性不可写()
    {
        var prop = typeof(EngineException).GetProperty(nameof(EngineException.Result));

        Assert.NotNull(prop);
        Assert.True(prop!.CanRead);
        Assert.False(prop.CanWrite);
    }
}
