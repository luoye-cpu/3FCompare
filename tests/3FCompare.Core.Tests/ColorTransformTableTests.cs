using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// kind=3 色彩变换表（<see cref="ColorTransformTable"/>）的构造与序列化对账。
///
/// <para><b>这组测试存在的首要理由是那条"全零 LUT ≠ 恒等"的坑</b>：内核的
/// <c>ValidColorTransform</c> 只查形状（尺寸等号、version、分辨率、domain、有限性），
/// 内容一概不解释。把 curveLut 清零得到的是一张<b>合法</b>的表，采样结果处处 (0,0,0)
/// ⇒ 画面压成黑，而调用链上没有任何一环会报错。所以恒等必须逐格判"输出=输入"，
/// 并且这里额外把"全零表"当作一条真表装载进来，证明那条断言不是自证式的空壳。</para>
///
/// <para><b>第二条线是布局</b>：宿主写进 pinned 缓冲区的字节就是内核 memcpy 进去的字节，
/// 判据用运行期自己的 <see cref="Marshal.StructureToPtr"/> 当独立实现来对撞
/// （同一个镜像结构、两条完全不同的写入路径），而不是再手抄一遍偏移量。</para>
/// </summary>
public class ColorTransformTableTests
{
    private const int Resolution = (int)ColorTransformTable.LutResolution;   // 33
    private const int Texels = Resolution * Resolution * Resolution;         // 35937
    private const int PayloadBytes = 431296;

    /// <summary>按原生偏移写一个 uint（<c>MemoryMarshal.Write</c> 的 in 形参要可取地址，
    /// 测试里直接传字面量会被 CS8156 挡下）。</summary>
    private static void WriteUInt(byte[] buffer, int offset, uint value) =>
        MemoryMarshal.Write(buffer.AsSpan(offset, 4), in value);

    private static void WriteFloat(byte[] buffer, int offset, float value) =>
        MemoryMarshal.Write(buffer.AsSpan(offset, 4), in value);

    /// <summary>一张只有 16 字节头合法、其余全零的载荷，用来单独判某一道形状门。</summary>
    private static byte[] HeaderBlob(uint size, uint version, uint resolution, uint domain)
    {
        var blob = new byte[PayloadBytes];
        WriteUInt(blob, 0, size);
        WriteUInt(blob, 4, version);
        WriteUInt(blob, 8, resolution);
        WriteUInt(blob, 12, domain);
        return blob;
    }

    /// <summary>逐格判恒等：texel (x,y,z) 的三个分量必须等于 (x,y,z)/(res-1)。
    /// 步距 1/32 是二进制精确值，所以这里要求<b>逐位相等</b>，不留容差。
    /// <para> mismatches 只收前若干条并整体断言：35937 格逐格 Assert 一旦判红，
    /// 屏幕上只会有第一条，而"错位一格"这类故障需要看成片的偏移才有用。</para></summary>
    [Fact]
    public void 恒等表_逐格输出等于输入()
    {
        var table = ColorTransformTable.Identity();
        var lut = table.CurveLut;

        Assert.Equal(ColorTransformTable.CurveLutFloatCount, lut.Length);
        Assert.Equal(Texels * 3, lut.Length);

        var step = 1f / (Resolution - 1);
        var mismatches = new List<string>();
        var nonZeroCells = 0;
        var i = 0;
        for (var z = 0; z < Resolution; z++)
        {
            for (var y = 0; y < Resolution; y++)
            {
                for (var x = 0; x < Resolution; x++, i++)
                {
                    var r = lut[i * 3];
                    var g = lut[i * 3 + 1];
                    var b = lut[i * 3 + 2];
                    if (r != x * step || g != y * step || b != z * step)
                    {
                        if (mismatches.Count < 12)
                            mismatches.Add($"格点 ({x},{y},{z}) 期望 ({x * step},{y * step},{z * step}) " +
                                           $"实际 ({r},{g},{b})");
                    }
                    else if (r != 0f || g != 0f || b != 0f) nonZeroCells++;
                }
            }
        }
        Assert.True(mismatches.Count == 0,
            $"恒等表 {mismatches.Count} 个格点不等于输入：\n  {string.Join("\n  ", mismatches)}");
        // 除 (0,0,0) 这一格外全部非零：把"清零当恒等"这条退路也在这里判掉。
        Assert.Equal(Texels - 1, nonZeroCells);
    }

    /// <summary>恒等表的矩阵是显式单位阵（不靠内核"全零归一"那条分支）。</summary>
    [Fact]
    public void 恒等表_矩阵是显式单位阵()
    {
        var m = ColorTransformTable.Identity().OutputMatrix;
        Assert.Equal(9, m.Count);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }, m.ToArray());
    }

    [Fact]
    public void 恒等表_默认落在编码RGB域()
    {
        // 域 0 下恒等表的输出会被当成 RGB 直接用，YUV 源上得到 (Y,Cb,Cr) 当颜色的错画面，
        // 所以"恒等"只有域 1 才真是恒等 ⇒ 默认值必须是 1。
        Assert.Equal(ColorTransformTable.LutDomainEncodedRgb, ColorTransformTable.Identity().LutDomain);
        Assert.Equal(ColorTransformTable.LutDomainCodedComponents,
            ColorTransformTable.Identity(ColorTransformTable.LutDomainCodedComponents).LutDomain);
    }

    /// <summary>这条是"全零即恒等"那个错假设的反证：
    /// 一张 curveLut 全零、形状合法的表会被内核照收，但它把每格都映到黑。
    /// 如果哪天有人把恒等实现"优化"成清零，前一条逐格测试会红，这条会解释为什么。</summary>
    [Fact]
    public void 全零LUT是合法表但绝不等于恒等表()
    {
        // 矩阵区保持全零 —— 内核确实把全零矩阵归一成单位阵（VideoRenderer 的
        // BuildColorTransformSettings），这部分"零即单位"是对的；错的只有 LUT。
        var blob = HeaderBlob(PayloadBytes, ColorTransformTable.PayloadVersion,
            ColorTransformTable.LutResolution, ColorTransformTable.LutDomainEncodedRgb);

        Assert.True(ColorTransformTable.TryFromPayload(blob, out var zeroTable, out var error),
            $"全零载荷的形状是合法的，不该被拒：{error}");
        Assert.NotNull(zeroTable);

        var identity = ColorTransformTable.Identity();
        Assert.NotEqual(identity.CurveLut.ToArray(), zeroTable!.CurveLut.ToArray());
        // 逐格看：最后一个格点 (32,32,32) 在恒等表里是 1.0，在全零表里是 0.0 ⇒ 采样端直接压黑。
        Assert.Equal(1f, identity.CurveLut[^3]);
        Assert.Equal(0f, zeroTable.CurveLut[^3]);
        // 而矩阵那一侧，全零==单位阵这个约定成立，别把两条坑混为一谈。
        Assert.Equal(0f, zeroTable.OutputMatrix[0]);
    }

    [Fact]
    public void 通道置换_矩阵按行落位且LUT仍恒等()
    {
        // R/B 反接的源：输出 r 取输入第 2 路、b 取第 0 路。
        var table = ColorTransformTable.ChannelPermutation(redSource: 2, greenSource: 1, blueSource: 0);
        Assert.Equal(0f, table.OutputMatrix[0]);
        Assert.Equal(0f, table.OutputMatrix[1]);
        Assert.Equal(1f, table.OutputMatrix[2]);
        Assert.Equal(0f, table.OutputMatrix[3]);
        Assert.Equal(1f, table.OutputMatrix[4]);
        Assert.Equal(0f, table.OutputMatrix[5]);
        Assert.Equal(1f, table.OutputMatrix[6]);
        Assert.Equal(0f, table.OutputMatrix[7]);
        Assert.Equal(0f, table.OutputMatrix[8]);
        Assert.Equal(ColorTransformTable.Identity().CurveLut.ToArray(), table.CurveLut.ToArray());
    }

    [Theory]
    [InlineData(0, 0, 1)]   // 两路同源不是置换，是混合：会静默丢掉一路颜色
    [InlineData(1, 1, 1)]
    [InlineData(3, 0, 1)]   // 越界
    [InlineData(-1, 0, 1)]
    public void 通道置换_非法索引一律抛(int r, int g, int b)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ColorTransformTable.ChannelPermutation(r, g, b));
    }

    [Fact]
    public void 通道置换_每行恰一个1()
    {
        var m = ColorTransformTable.ChannelPermutation(1, 2, 0).OutputMatrix;
        for (var row = 0; row < 3; row++)
        {
            var ones = 0;
            for (var col = 0; col < 3; col++) ones += m[row * 3 + col] == 1f ? 1 : 0;
            Assert.Equal(1, ones);   // 真置换，不是投影
        }
    }

    /// <summary>装载 → 序列化 → 再装载：一张表绕一圈原生布局后逐字段不变。</summary>
    [Fact]
    public void 从载荷字节装载与写出互逆()
    {
        var source = ColorTransformTable.ChannelPermutation(2, 1, 0,
            ColorTransformTable.LutDomainCodedComponents);
        var buffer = ColorTransformPayloadMarshal.BuildPinnedBuffer(source, out var handle);
        try
        {
            Assert.True(ColorTransformTable.TryFromPayload(buffer, out var reloaded, out var error),
                $"自己写出的载荷被自己拒了：{error}");
            Assert.NotNull(reloaded);
            Assert.Equal(source.LutDomain, reloaded!.LutDomain);
            Assert.Equal(source.OutputMatrix.ToArray(), reloaded.OutputMatrix.ToArray());
            Assert.Equal(source.CurveLut.ToArray(), reloaded.CurveLut.ToArray());
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>钉住缓冲区必须与运行期 marshaller 的产物<b>逐字节</b>相同。
    /// <para>这是本文件最要紧的一条：宿主写进包里的指针指向的是自己拼的缓冲区，
    /// 偏移错一个字节，内核 memcpy 走的就是一张错位表——形状校验照样全过，
    /// 而像素彻底歪掉。<see cref="Marshal.StructureToPtr"/> 是同一份布局的第二个实现，
    /// 拿它当独立裁判比再抄一遍偏移量强。</para></summary>
    [Fact]
    public void 钉住缓冲区的字节布局与marshaller产物一致()
    {
        var table = ColorTransformTable.Identity();
        var size = Marshal.SizeOf<Fff3FpColorTransformPayload>();
        Assert.Equal(PayloadBytes, size);   // 与内核 static_assert 同数（布局线由 ColorTransformLayoutTests 钉）

        var mirror = new Fff3FpColorTransformPayload
        {
            Size = (uint)size,
            Version = ColorTransformTable.PayloadVersion,
            LutResolution = ColorTransformTable.LutResolution,
            LutDomain = table.LutDomain,
            OutputMatrix = table.OutputMatrix.ToArray(),
            CurveLut = table.CurveLut.ToArray(),
        };
        var unmanaged = Marshal.AllocHGlobal(size);
        var buffer = ColorTransformPayloadMarshal.BuildPinnedBuffer(table, out var handle);
        try
        {
            Marshal.StructureToPtr(mirror, unmanaged, false);
            var reference = new byte[size];
            Marshal.Copy(unmanaged, reference, 0, size);
            Assert.True(reference.AsSpan().SequenceEqual(buffer),
                "自己按偏移写的载荷与运行期 marshaller 的产物不一致 ⇒ 偏移或尺寸漂了");

            // 头字段按原生偏移读回（内核等号校验的就是这四个数）。
            Assert.Equal((uint)size, MemoryMarshal.Read<uint>(buffer));
            Assert.Equal(1u, MemoryMarshal.Read<uint>(buffer.AsSpan(4)));
            Assert.Equal(33u, MemoryMarshal.Read<uint>(buffer.AsSpan(8)));
            Assert.Equal(table.LutDomain, MemoryMarshal.Read<uint>(buffer.AsSpan(12)));
            // 矩阵与 LUT 的落点也必须问镜像结构自己，不能抄字面量。
            Assert.Equal(16, ColorTransformPayloadMarshal.MatrixOffset);
            Assert.Equal(52, ColorTransformPayloadMarshal.CurveLutOffset);
        }
        finally
        {
            handle.Free();
            Marshal.DestroyStructure<Fff3FpColorTransformPayload>(unmanaged);
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    /// <summary>派发层为什么不能直接 <c>GCHandle.Alloc(结构实例, Pinned)</c>：
    /// 该镜像的两个数组字段是 <c>ByValArray</c>，托管侧它们是有引用的数组
    /// ⇒ 实例只有 32 字节，内联摊平只发生在跨非托管边界时。
    /// 这条测试钉住那个前提本身；前提一变（比如有人改成 fixed 缓冲），
    /// <see cref="ColorTransformPayloadMarshal"/> 的按偏移写入就可以简化，而这条会先告诉我们。</summary>
    [Fact]
    public void 结构的托管实例尺寸远小于原生尺寸()
    {
        var managed = Unsafe.SizeOf<Fff3FpColorTransformPayload>();
        var native = Marshal.SizeOf<Fff3FpColorTransformPayload>();
        Assert.True(managed < native,
            $"托管实例 {managed} 字节 vs 原生 {native} 字节：如果两者不再相差，" +
            "说明字段形态变了，ColorTransformPayloadMarshal 的按偏移写入需要重新评估");
    }

    [Theory]
    [InlineData(0u, 431296u, 33u, 0u)]     // size 字段不等于结构大小：内核等号门
    [InlineData(431296u, 2u, 33u, 0u)]     // version
    [InlineData(431296u, 1u, 65u, 0u)]     // 分辨率
    [InlineData(431296u, 1u, 33u, 2u)]     // domain 越界
    public void 装载层把内核会整批拒绝的形状先判掉(uint size, uint version, uint resolution, uint domain)
    {
        var blob = HeaderBlob(size, version, resolution, domain);

        Assert.False(ColorTransformTable.TryFromPayload(blob, out var table, out var error));
        Assert.Null(table);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 装载层拒绝非有限值()
    {
        var buffer = ColorTransformPayloadMarshal.BuildPinnedBuffer(
            ColorTransformTable.Identity(), out var handle);
        handle.Free();

        // 非有限值：内核 ValidColorTransform 逐元素查 isfinite，一条 NaN 就整批 InvalidArgument。
        var withNaN = (byte[])buffer.Clone();
        WriteFloat(withNaN, ColorTransformPayloadMarshal.CurveLutOffset + 3 * 4, float.NaN);
        Assert.False(ColorTransformTable.TryFromPayload(withNaN, out _, out var nanError));
        Assert.Contains("非有限", nanError);

        var withBadMatrix = (byte[])buffer.Clone();
        WriteFloat(withBadMatrix, ColorTransformPayloadMarshal.MatrixOffset + 4, float.PositiveInfinity);
        Assert.False(ColorTransformTable.TryFromPayload(withBadMatrix, out _, out var matrixError));
        Assert.Contains("outputMatrix", matrixError);
    }

    [Fact]
    public void 装载层拒绝长度不符的载荷()
    {
        var buffer = ColorTransformPayloadMarshal.BuildPinnedBuffer(
            ColorTransformTable.Identity(), out var handle);
        handle.Free();

        // 长度：多一字节少一字节都拒（内核是等号，不是前缀放行）。
        Assert.False(ColorTransformTable.TryFromPayload(
            buffer.AsSpan(0, buffer.Length - 1), out _, out var shortError));
        Assert.Contains("载荷长度", shortError);

        var longer = new byte[buffer.Length + 4];
        buffer.CopyTo(longer, 0);
        Assert.False(ColorTransformTable.TryFromPayload(longer, out _, out var longError));
        Assert.Contains("载荷长度", longError);
    }

    [Fact]
    public void 表能给出自己的kind3包()
    {
        var packet = ColorTransformTable.Identity().ToPacket(1234L);
        Assert.Equal(DynamicMetadataKind.ExternalColorTransform, packet.Kind);
        Assert.Equal(1234L, packet.FrameIndex);
        Assert.IsType<ColorTransformTable>(packet.Payload);
    }
}
