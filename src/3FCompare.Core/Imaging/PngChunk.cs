namespace _3FCompare.Core.Imaging;

/// <summary>
/// PNG chunk 读写与 CRC32（纯计算，可单测）。
///
/// 原先这些代码散在 <c>MainWindow.Capture.cs</c>，而 UI 层**够不着**测试工程
/// （测试工程只引用 Core）——写错就是安静地产出损坏的 PNG，只有实机 screentest
/// 里"文件大于 1000 字节"这一条能兜底。下沉后可用单测钉死（docs/14 §4.2）。
///
/// 只处理 <c>byte[]</c>，不依赖 System.Drawing，因此 Core 保持跨平台。
/// </summary>
public static class PngChunk
{
    /// <summary>PNG 文件签名（8 字节）长度。</summary>
    public const int SignatureLength = 8;

    /// <summary>chunk 头部之外的固定开销：4(长度) + 4(类型) + 4(CRC)。</summary>
    private const int ChunkOverhead = 12;

    /// <summary>被视为"已有色彩信息"的 chunk 类型。</summary>
    private static readonly string[] ColorChunkTypes = { "sRGB", "gAMA", "iCCP", "cHRM" };

    /// <summary>从大端序读 32 位整数（PNG 的长度字段是大端）。</summary>
    public static int ReadBigEndianInt32(byte[] b, int offset)
        => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>CRC-32（PNG 规范使用的 IEEE 802.3 多项式 0xEDB88320）。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>写入一个完整 chunk：长度(大端) + 类型 + 数据 + CRC(覆盖类型与数据)。</summary>
    public static void Write(Stream s, string type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(data);
        if (type.Length != 4)
            throw new ArgumentException("PNG chunk 类型必须是 4 个 ASCII 字符", nameof(type));

        WriteBigEndianInt32(s, (uint)data.Length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        // CRC 覆盖 type + data（**不含**长度字段）
        var crcInput = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
        WriteBigEndianInt32(s, Crc32(crcInput));
    }

    private static void WriteBigEndianInt32(Stream s, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, 4);
    }

    /// <summary>遍历 chunk 的辅助：依次给出每个 chunk 的类型与数据区间。
    /// 结构异常（长度越界）时停止枚举。</summary>
    public static IEnumerable<(string Type, int DataOffset, int Length)> Enumerate(byte[] png)
    {
        if (png is null || png.Length < SignatureLength) yield break;

        var pos = SignatureLength;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);
            var total = ChunkOverhead + len;
            if (len < 0 || total < 0 || pos + total > png.Length) yield break;

            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            yield return (type, pos + 8, len);
            pos += total;
            if (type == "IEND") yield break;
        }
    }

    /// <summary>是否已含色彩信息 chunk（sRGB / gAMA / iCCP / cHRM）。
    /// 已有时不该再插 sRGB，否则与既有色彩描述冲突。</summary>
    public static bool HasColorChunk(byte[] png)
        => png is not null && Enumerate(png).Any(c => ColorChunkTypes.Contains(c.Type));

    /// <summary>
    /// 在 IHDR 之后插入 sRGB chunk（PNG 规范要求 sRGB 位于第一个 IDAT 之前）。
    /// 已有色彩信息、或结构异常时原样返回输入，不冒险改写。
    /// </summary>
    /// <param name="renderingIntent">0=Perceptual（默认）。</param>
    public static byte[] InsertSrgbAfterIhdr(byte[] png, byte renderingIntent = 0)
    {
        if (png is null || png.Length < SignatureLength) return png ?? Array.Empty<byte>();
        if (HasColorChunk(png)) return png;

        using var ms = new MemoryStream(png.Length + ChunkOverhead + 1);
        ms.Write(png, 0, Math.Min(SignatureLength, png.Length));

        var pos = SignatureLength;
        var inserted = false;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);
            var total = ChunkOverhead + len;
            if (len < 0 || total < 0 || pos + total > png.Length) break;

            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            ms.Write(png, pos, total);
            pos += total;

            if (type == "IHDR")
            {
                Write(ms, "sRGB", new[] { renderingIntent });
                inserted = true;
            }
            if (type == "IEND") break;
        }
        // 结构异常时把剩余字节兜底补齐，避免静默丢数据
        if (pos < png.Length) ms.Write(png, pos, png.Length - pos);

        return inserted ? ms.ToArray() : png;
    }
}
