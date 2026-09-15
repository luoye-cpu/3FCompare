using System.IO;
using System.Linq;
using _3FCompare.Core.Imaging;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// PNG chunk 读写与 CRC32 回归。
///
/// 这些逻辑原先写在 <c>MainWindow.Capture.cs</c>，而测试工程只引用 Core ⇒ 完全够不着。
/// 写错的表现是安静地产出损坏的 PNG，实机只有 screentest 的"文件 > 1000 字节"能兜底
/// （docs/14 §4.2）。下沉到 Core 后在这里钉死。
///
/// CRC32 期望值用 zlib 独立核对过，不是照抄实现。
/// </summary>
public class PngChunkTests
{
    private static readonly byte[] Signature =
        { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("abc", 0x352441C2u)]
    [InlineData("123456789", 0xCBF43926u)]   // CRC-32/ISO-HDLC 标准校验值
    public void Crc32_已知向量(string input, uint expected)
        => Assert.Equal(expected, PngChunk.Crc32(System.Text.Encoding.ASCII.GetBytes(input)));

    /// <summary>写入的 chunk 必须自带正确 CRC：CRC 覆盖 type+data，不含长度字段。</summary>
    [Fact]
    public void Write_生成的CRC可被PNG规范校验()
    {
        using var ms = new MemoryStream();
        PngChunk.Write(ms, "IHDR", new byte[] { 0, 0, 1, 0 });
        var buf = ms.ToArray();

        Assert.Equal(16, buf.Length);                       // 4(len) + 4(type) + 4(data) + 4(crc)
        Assert.Equal(4, PngChunk.ReadBigEndianInt32(buf, 0));

        var covered = buf.Skip(4).Take(8).ToArray();        // type + data
        var expected = PngChunk.Crc32(covered);
        var actual = (uint)PngChunk.ReadBigEndianInt32(buf, 12);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Write_类型必须是四个字符()
    {
        using var ms = new MemoryStream();
        Assert.Throws<System.ArgumentException>(() => PngChunk.Write(ms, "IHDRX", new byte[1]));
    }

    private static byte[] BuildMinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);
        PngChunk.Write(ms, "IHDR", new byte[13]);
        PngChunk.Write(ms, "IDAT", new byte[] { 1, 2, 3 });
        PngChunk.Write(ms, "IEND", System.Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void Enumerate_按顺序列出全部chunk()
    {
        var types = PngChunk.Enumerate(BuildMinimalPng()).Select(c => c.Type).ToArray();
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
    }

    /// <summary>sRGB 必须紧跟 IHDR、且在第一个 IDAT 之前（PNG 规范要求）。</summary>
    [Fact]
    public void InsertSrgbAfterIhdr_插在IHDR之后且早于IDAT()
    {
        var png = BuildMinimalPng();
        Assert.False(PngChunk.HasColorChunk(png));

        var result = PngChunk.InsertSrgbAfterIhdr(png);
        var types = PngChunk.Enumerate(result).Select(c => c.Type).ToArray();

        Assert.Equal(new[] { "IHDR", "sRGB", "IDAT", "IEND" }, types);
        Assert.True(PngChunk.HasColorChunk(result));
    }

    /// <summary>已有色彩信息时不得再插，否则与既有描述冲突。</summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("gAMA")]
    [InlineData("iCCP")]
    [InlineData("cHRM")]
    public void InsertSrgbAfterIhdr_已有色彩chunk时原样返回(string existing)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);
        PngChunk.Write(ms, "IHDR", new byte[13]);
        PngChunk.Write(ms, existing, new byte[4]);
        PngChunk.Write(ms, "IEND", System.Array.Empty<byte>());
        var png = ms.ToArray();

        Assert.True(PngChunk.HasColorChunk(png));
        Assert.Same(png, PngChunk.InsertSrgbAfterIhdr(png));   // 同一引用 = 未改写
    }

    /// <summary>结构异常（长度越界）时不得抛异常，也不得静默吞掉已解析的部分。</summary>
    [Fact]
    public void Enumerate_长度越界时安全停止()
    {
        var broken = new byte[Signature.Length + 8];
        Signature.CopyTo(broken, 0);
        // 声明一个远超实际长度的长度字段
        broken[Signature.Length] = 0x7F;
        broken[Signature.Length + 1] = 0xFF;
        broken[Signature.Length + 2] = 0xFF;
        broken[Signature.Length + 3] = 0xFF;

        Assert.Empty(PngChunk.Enumerate(broken));
        Assert.False(PngChunk.HasColorChunk(broken));
    }

    [Fact]
    public void InsertSrgbAfterIhdr_长度不足时原样返回()
    {
        var tiny = new byte[] { 0x89, (byte)'P' };
        Assert.Same(tiny, PngChunk.InsertSrgbAfterIhdr(tiny));
    }
}
