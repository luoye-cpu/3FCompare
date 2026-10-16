using System.Reflection;
using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 派发层（<see cref="Fff3FpEngine.Fff3FpSession.BuildNativePackets"/>）对 kind=3 的处理。
///
/// <para>这里<b>不真打内核</b>：判据是"送进 P/Invoke 的那一批长什么样"，
/// 而内核对 kind=3 的 <c>payloadSize</c> 是<b>等号</b>校验（多一字节整批 InvalidArgument），
/// 所以尺寸这一格必须由测试钉死；顺带把 pinned 指针背后的字节读回来验内容，
/// 避免"指针非空就算对"。</para>
/// </summary>
public class ColorTransformDispatchTests
{
    /// <summary>用 seam 建包，返回调用方持有的句柄列表（测试负责 Free，
    /// 与产品路径里 finally 的同一份生命周期集合是同一个对象）。</summary>
    private static Fff3FpDynamicMetadataPacket[] Build(
        IReadOnlyList<DynamicMetadataPacket> packets, List<GCHandle> handles) =>
        Fff3FpEngine.Fff3FpSession.BuildNativePackets(packets, handles);

    [Fact]
    public void kind3派发时payloadSize恰好是内核结构尺寸()
    {
        var handles = new List<GCHandle>();
        try
        {
            var native = Build(new[] { ColorTransformTable.Identity().ToPacket(7L) }, handles);

            Assert.Single(native);
            var packet = native[0];
            Assert.Equal(3u, packet.Kind);                                  // FFF3FPDynamicMetadataKind
            Assert.Equal(431296u, packet.PayloadSize);                       // 内核等号门
            Assert.Equal(1u, packet.Version);
            Assert.Equal(1u, packet.KindVersion);
            Assert.Equal(7L, packet.FrameIndex);
            Assert.Equal(0u, packet.Reserved);
            Assert.Equal((uint)Marshal.SizeOf<Fff3FpDynamicMetadataPacket>(), packet.Size);
            Assert.NotEqual(nint.Zero, packet.Payload);

            // 指针背后必须真是那张表：把原生布局读回来逐字段核对。
            var back = Marshal.PtrToStructure<Fff3FpColorTransformPayload>(packet.Payload);
            Assert.Equal(431296u, back.Size);
            Assert.Equal(1u, back.Version);
            Assert.Equal(33u, back.LutResolution);
            Assert.Equal(ColorTransformTable.LutDomainEncodedRgb, back.LutDomain);
            Assert.NotNull(back.CurveLut);
            Assert.Equal(ColorTransformTable.CurveLutFloatCount, back.CurveLut!.Length);
            Assert.NotNull(back.OutputMatrix);
            Assert.Equal(9, back.OutputMatrix!.Length);
            Assert.Equal(1f, back.OutputMatrix[0]);
            Assert.Equal(1f, back.CurveLut[^3]);   // 格点 (32,32,32) 的 B 分量
            Assert.Equal(0f, back.CurveLut[0]);    // 格点 (0,0,0)
            Assert.Equal(1f / 32f, back.CurveLut[3]);
        }
        finally
        {
            Assert.Single(handles);
            Assert.True(handles[0].IsAllocated, "派发层没把句柄放进生命周期集合");
            foreach (var h in handles) if (h.IsAllocated) h.Free();
        }
    }

    [Fact]
    public void 混合批次_每包按各自kind报各自尺寸()
    {
        var handles = new List<GCHandle>();
        try
        {
            var packets = new[]
            {
                new DynamicMetadataPacket
                {
                    Kind = DynamicMetadataKind.Hdr10Plus,
                    FrameIndex = 0L,
                    Payload = new HdrDynamicMetadataEntry
                    {
                        FrameIndex = 0,
                        Windows = Array.Empty<HdrDynamicMetadataWindow>(),
                        TargetedDisplayNits = 0f,
                    },
                },
                ColorTransformTable.Identity().ToPacket(1L),
            };
            var native = Build(packets, handles);

            Assert.Equal(2, native.Length);
            Assert.Equal(1u, native[0].Kind);
            Assert.Equal((uint)Marshal.SizeOf<Fff3FpHdrDynamicMetadataEntry>(), native[0].PayloadSize);
            Assert.Equal(3u, native[1].Kind);
            Assert.Equal(431296u, native[1].PayloadSize);
            Assert.NotEqual(nint.Zero, native[0].Payload);
            Assert.NotEqual(nint.Zero, native[1].Payload);
            Assert.NotEqual(native[0].Payload, native[1].Payload);
            Assert.Equal(2, handles.Count);   // 每个载荷一块，谁都不能漏
        }
        finally
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
        }
    }

    [Fact]
    public void kind3载荷类型不对必须抛()
    {
        var handles = new List<GCHandle>();
        // kind=3 配 HDR10+ 载荷：内核会把 payloadSize 与结构对不上号，但那是"整批被拒"的哑账；
        // 宿主必须在进 P/Invoke 之前就拦住，报错才有归因。
        var wrongForThree = new DynamicMetadataPacket
        {
            Kind = DynamicMetadataKind.ExternalColorTransform,
            FrameIndex = 0L,
            Payload = new HdrDynamicMetadataEntry
            {
                FrameIndex = 0,
                Windows = Array.Empty<HdrDynamicMetadataWindow>(),
                TargetedDisplayNits = 0f,
            },
        };
        var ex = Assert.Throws<ArgumentException>(() => Build(new[] { wrongForThree }, handles));
        Assert.Contains("ColorTransformTable", ex.Message);

        // 反向：kind=1 配色彩变换表
        var wrongForOne = ColorTransformTable.Identity().ToPacket(0L) with
        {
            Kind = DynamicMetadataKind.Hdr10Plus,
        };
        Assert.Throws<ArgumentException>(() => Build(new[] { wrongForOne }, handles));

        Assert.Empty(handles);   // 抛之前一块都没钉住
    }

    [Theory]
    [InlineData(0u)]     // None：只有 Clear 用得到，派发层不收
    [InlineData(4u)]     // 内核没有的 kind
    [InlineData(99u)]
    public void 未知kind仍然进不了PInvoke(uint kind)
    {
        var handles = new List<GCHandle>();
        var packet = ColorTransformTable.Identity().ToPacket(0L) with
        {
            Kind = (DynamicMetadataKind)kind,
        };
        var ex = Assert.Throws<ArgumentException>(() => Build(new[] { packet }, handles));
        Assert.Contains("未知 DynamicMetadataKind", ex.Message);
        Assert.Empty(handles);
    }

    [Fact]
    public void 空载荷抛而不是把空指针送进去()
    {
        var handles = new List<GCHandle>();
        // Payload 是 required init，只能靠显式赋 null 走到这条路（内核 payload==nullptr 会整批拒）。
        var packet = new DynamicMetadataPacket
        {
            Kind = DynamicMetadataKind.ExternalColorTransform,
            FrameIndex = 0L,
            Payload = null!,
        };
        Assert.Throws<ArgumentException>(() => Build(new[] { packet }, handles));
        Assert.Empty(handles);
    }

    [Fact]
    public void 色彩变换结构从不作为PInvoke形参按值传()
    {
        // 421 KB 的结构一旦出现在某个 LibraryImport 的形参表里，源生成器就会为它生成
        // 按值封送（栈上拷 421 KB 或直接编译失败），而包里的 Payload 本来就是 nint。
        // 这条把"该结构只作为指针存在"这个前提常驻化。
        var offenders = new List<string>();
        foreach (var method in typeof(Fff3FpNative).GetMethods(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var parameter in method.GetParameters())
            {
                var parameterType = parameter.ParameterType;
                if (parameterType == typeof(Fff3FpColorTransformPayload) ||
                    parameterType == typeof(Fff3FpColorTransformPayload[]))
                {
                    offenders.Add($"{method.Name}({parameter.Name}: {parameterType.Name})");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            $"421 KB 的载荷结构被当成形参传了：{string.Join(", ", offenders)}");

        // 而包结构里的 Payload 必须是裸指针。
        var payload = typeof(Fff3FpDynamicMetadataPacket).GetField(
            nameof(Fff3FpDynamicMetadataPacket.Payload))!;
        Assert.Equal(typeof(nint), payload.FieldType);
    }

    [Fact]
    public void 公共枚举的kind3与内核镜像同值()
    {
        // 宿主公共面（DynamicMetadataKind）与内部镜像（Fff3FpDynamicMetadataKind）
        // 分属两个类型，任何一侧漂移都会让派发层把 kind=3 当未知 kind 抛掉。
        Assert.Equal(3u, (uint)DynamicMetadataKind.ExternalColorTransform);
        Assert.Equal((uint)DynamicMetadataKind.ExternalColorTransform,
            (uint)Fff3FpDynamicMetadataKind.ExternalColorTransform);
        Assert.Equal(0u, (uint)DynamicMetadataKind.None);
        Assert.Equal(1u, (uint)DynamicMetadataKind.Hdr10Plus);
        Assert.Equal(2u, (uint)DynamicMetadataKind.HdrVivid);
    }
}
