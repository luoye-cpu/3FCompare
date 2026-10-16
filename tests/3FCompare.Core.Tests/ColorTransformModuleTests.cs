using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 外部色彩变换模块的可选装载（<see cref="ColorTransformModule.TryOpen"/>）。
///
/// <para><b>这组测试的靶心是"不得抛"</b>：既定口径是不装模块 = 回落 HDR10 基底且不提示，
/// 装载层唯一的硬约束就是任何失败都只把 false 交回去。文件缺失、目录不存在、
/// 路径上同名的是个目录（不是可装载模块）、目录名畸形——四条都要走到"缺席"
/// 而不是把异常带到调用方手里。</para>
///
/// <para>模块产物在库外独立仓（<c>C:\PLAN\ColorTransform</c>），不在本仓的测试输出目录里，
/// 所以这里只测"缺席"那一路；真装上一版模块的通路要靠实机自测覆盖（未在本仓接线）。</para>
/// </summary>
public class ColorTransformModuleTests
{
    [Fact]
    public void 目录里没有模块时返回缺席而不是抛()
    {
        using var dir = new TempDir("colortransform_absent");

        // 若哪天打包脚本把模块放进了测试输出目录，这条会红——
        // 那时要改的是判据的构造方式（比如把搜索面隔离进临时目录），不是把这条删掉。
        Assert.False(ColorTransformModule.TryOpen(out var module, out var reason, dir.Path));
        Assert.Null(module);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 目录不存在时返回缺席而不是抛()
    {
        using var dir = new TempDir("colortransform_missing_sub");
        var notThere = System.IO.Path.Combine(dir.Path, "no_such_subdir");

        Assert.False(ColorTransformModule.TryOpen(out var module, out var reason, notThere));
        Assert.Null(module);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 名字存在但不是模块文件时返回缺席而不是抛()
    {
        using var dir = new TempDir("colortransform_notamodule");
        // 用"同名目录"构造"路径存在但不是可装载模块"：本机临时目录不允许测试进程
        // 新建 .dll（防病毒策略直接回 UnauthorizedAccessException），写文件这条路在这台机器上不可用。
        Directory.CreateDirectory(dir.File_(ColorTransformAbi.ModuleFileName));

        Assert.False(ColorTransformModule.TryOpen(out var module, out var reason, dir.Path));
        Assert.Null(module);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 畸形目录名不抛只缺席()
    {
        // 非法路径字符：候选枚举要跳过，不能把 ArgumentException 甩给调用方。
        Assert.False(ColorTransformModule.TryOpen(
            out var module, out var reason, "a*b?c|d"));
        Assert.Null(module);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 模块容量常量与内核载荷结构同水()
    {
        // 模块 ABI 与内核 kind=3 载荷分属两个仓：模块头里的 constexpr 431296 与内核
        // static_assert 的 431296 只要有一份漂了，每一次 analyzeFrame 都会撞内核的等号门。
        // TryOpen 在装载时判这个，测试在这里判——不依赖盘上有没有模块。
        Assert.Equal(ColorTransformAbi.PayloadCapacity,
            (uint)Marshal.SizeOf<Fff3FpColorTransformPayload>());
        Assert.Equal(ColorTransformAbi.PayloadCapacity,
            (uint)ColorTransformPayloadMarshal.NativePayloadByteCount);
        Assert.Equal(3u, ColorTransformAbi.PayloadKind);
        Assert.Equal(1u, ColorTransformAbi.PayloadKindVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void 返回码常量与模块头的顺序一致(int code)
    {
        // NoMetadata 必须是 1（宿主按它分流"跳过注入"与"报错留痕"），
        // 这条把六个码的相对顺序钉成"Success=0 起连续"，模块头加值时会先在这里显形。
        // 5 = Unauthorized（模块 ABI 版本 2 新增）：漏了它，宿主会把"授权失效"
        // 归进 default 的"未知码"，症状对但诊断信息丢。
        var codes = new[]
        {
            ColorTransformAbi.ResultSuccess,
            ColorTransformAbi.ResultNoMetadata,
            ColorTransformAbi.ResultInvalidArgument,
            ColorTransformAbi.ResultTooSmall,
            ColorTransformAbi.ResultUnsupported,
            ColorTransformAbi.ResultUnauthorized,
        };
        Assert.Equal(code, codes[code]);
        Assert.True(codes.Distinct().Count() == codes.Length);
    }

    [Fact]
    public void 模块ABI镜像的x64布局是文档那个形状()
    {
        // 真源是模块自己那份未公开的 ABI 契约头（不进本仓测试路径），
        // 这里按 x64 C 对齐规则独立复算一遍：4+4 头之后是指针（8 对齐），
        // 于是三个结构都是 32 字节、无空洞。
        Assert.Equal(32, Marshal.SizeOf<FffColorTransformInput>());
        Assert.Equal(0, Marshal.OffsetOf<FffColorTransformInput>(nameof(FffColorTransformInput.Size)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<FffColorTransformInput>(nameof(FffColorTransformInput.Version)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<FffColorTransformInput>(nameof(FffColorTransformInput.Metadata)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FffColorTransformInput>(nameof(FffColorTransformInput.MetadataSize)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<FffColorTransformInput>(nameof(FffColorTransformInput.FrameIndex)).ToInt32());

        Assert.Equal(32, Marshal.SizeOf<FffColorTransformOutput>());
        Assert.Equal(12, Marshal.OffsetOf<FffColorTransformOutput>(nameof(FffColorTransformOutput.PayloadSize)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FffColorTransformOutput>(nameof(FffColorTransformOutput.Payload)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<FffColorTransformOutput>(nameof(FffColorTransformOutput.PayloadCapacity)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<FffColorTransformOutput>(nameof(FffColorTransformOutput.Reserved)).ToInt32());

        Assert.Equal(32, Marshal.SizeOf<FffColorTransformApiTable>());
        Assert.Equal(8, Marshal.OffsetOf<FffColorTransformApiTable>(nameof(FffColorTransformApiTable.TransformVersion)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FffColorTransformApiTable>(nameof(FffColorTransformApiTable.AnalyzeFrame)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<FffColorTransformApiTable>(nameof(FffColorTransformApiTable.Reset)).ToInt32());
    }

    [Fact]
    public void 泵在模块缺席时什么都不做也不抛()
    {
        // 这是"不装模块 = 今天的行为"那条口径的可执行判据：泵建得起来、跑得了、
        // 恒不注入、不抛，而且演示模式（没有环的会话）也不会被当成故障。
        var session = new FakeSession(new EngineSessionOptions { OutputWindow = nint.Zero });
        var pump = new ColorTransformPump(session, module: null);

        Assert.False(pump.IsSupported);
        Assert.False(pump.PumpOnce(out var error));
        Assert.Null(error);
        Assert.Equal(0u, pump.InjectedFrames);
        Assert.Equal(-1L, pump.LastInjectedFrameIndex);
        pump.OnStreamRestart();   // 不得抛
    }

    [Fact]
    public void 载荷快路只在逐字节全等时判未变()
    {
        // 快路（复用上一枚表、省掉每帧 431 KB 换算）的错误方向是**静默**的：
        // 多比一次只是白分配，少比一次就是把变了的表说成没变 ⇒ 画面停在旧色调且无痕迹。
        // 所以这里三态都要打到：全等 / 一个字节不同 / 长度不同 / 还没有上一帧。
        var baseline = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
        var same = (byte[])baseline.Clone();
        Assert.True(ColorTransformModule.PayloadMatches(same, baseline));

        var oneBit = (byte[])baseline.Clone();
        oneBit[^1] ^= 0x01;                       // 末位一个 bit：LUT 尾部的最小扰动
        Assert.False(ColorTransformModule.PayloadMatches(oneBit, baseline));

        var shorter = new byte[4095];
        Array.Copy(baseline, shorter, 4095);
        Assert.False(ColorTransformModule.PayloadMatches(shorter, baseline));   // 不做前缀比较
        Assert.False(ColorTransformModule.PayloadMatches(Array.Empty<byte>(), baseline));
        Assert.False(ColorTransformModule.PayloadMatches(baseline, null));      // 首帧没有可比的上一帧
    }
}
