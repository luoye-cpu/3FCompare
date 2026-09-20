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

    /// <summary>PNG 规范的 8 字节文件签名。全部是常量 ⇒ Roslyn 会把它编译成
    /// 只读数据段引用，不产生堆分配，NativeAOT 下同样安全。</summary>
    private static ReadOnlySpan<byte> PngSignature =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>被视为"已有色彩信息"的 chunk 类型。</summary>
    private static readonly string[] ColorChunkTypes = { "sRGB", "gAMA", "iCCP", "cHRM" };

    /// <summary>从大端序读 32 位整数（PNG 的长度字段是大端）。
    ///
    /// 越界抛 <see cref="ArgumentOutOfRangeException"/> 而不是让它一路走到
    /// <c>IndexOutOfRangeException</c>：后者看起来像本类的 bug，
    /// 实际上几乎总是"调用方给的 offset 不对"，异常类型必须说真话（B12）。</summary>
    public static int ReadBigEndianInt32(byte[] b, int offset)
    {
        ArgumentNullException.ThrowIfNull(b);
        // ⚠ 用 b.Length - 4 而不是 offset + 4 > b.Length：后者在 offset 接近
        // int.MaxValue 时自身就溢出了，检查会失效
        if (offset < 0 || offset > b.Length - 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset,
                $"需要 4 个字节，offset={offset} 超出数组范围（长度 {b.Length}）");
        }

        return (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
    }

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

    /// <summary>是否以 PNG 的 8 字节签名开头（B12）。
    ///
    /// 不校验签名会把任意二进制（甚至一张 JPEG）当成 PNG 遍历：偏移 8 起的 4 个字节
    /// 被当成"第一个 chunk 的长度"，随后可能在完全错误的偏移上插入 sRGB，
    /// 产出一个谁都打不开的文件，且全程不报错。</summary>
    public static bool HasPngSignature(byte[] png)
    {
        if (png is null || png.Length < SignatureLength) return false;
        return png.AsSpan(0, SignatureLength).SequenceEqual(PngSignature);
    }

    /// <summary>遍历 chunk 的辅助：依次给出每个 chunk 的类型与数据区间。
    /// 结构异常（长度越界 / CRC 校验失败）时停止枚举。</summary>
    public static IEnumerable<(string Type, int DataOffset, int Length)> Enumerate(byte[] png)
        => Enumerate(png, verifyCrc: false);

    /// <summary>遍历 chunk，可选校验每个 chunk 的 CRC。</summary>
    /// <param name="png">完整 PNG 字节。</param>
    /// <param name="verifyCrc">
    /// true = 逐个校验 CRC，遇到第一个校验失败的 chunk 即停止枚举。
    ///
    /// 默认 false 是**性能**取舍：读路径（HasColorChunk）只想扫一眼有没有色彩 chunk，
    /// 为它跑一遍全文件 CRC 不划算（几 MB 的截图要几十毫秒）。
    /// 但**写路径必须先确认真的是一份完好的 PNG**——见 <see cref="IsCrcValid"/>。
    /// </param>
    public static IEnumerable<(string Type, int DataOffset, int Length)> Enumerate(byte[] png, bool verifyCrc)
    {
        if (png is null || png.Length < SignatureLength) yield break;
        if (!HasPngSignature(png)) yield break;

        var pos = SignatureLength;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);

            // ⚠ 必须全程 long。原先 `var total = ChunkOverhead + len` 是 int 运算：
            // len ∈ [0x7FFFFFF5, 0x7FFFFFFF] 时 total 溢出成负数，
            // 继而 `pos + total` 也溢出成负数 ⇒ `> png.Length` 判假 ⇒ **越界检查被整个绕过**，
            // 后面就会拿一个荒谬的 total 去读/写（B12）。
            long total = ChunkOverhead + (long)len;

            // PNG 规范：长度字段的最高位必须为 0，即 len ∈ [0, 0x7FFFFFFF]；
            // 读成负数就意味着源文件不合法（或根本不是 PNG）
            if (len < 0 || pos + total > png.Length) yield break;

            // 到这里 len 已被上界约束，4 + len 与 pos + 8 + len 都不会溢出
            if (verifyCrc)
            {
                var stored = (uint)ReadBigEndianInt32(png, pos + 8 + len);
                var actual = Crc32(new ReadOnlySpan<byte>(png, pos + 4, 4 + len));
                if (stored != actual) yield break;   // CRC 不符 ⇒ 停止，不猜测后续结构
            }

            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            yield return (type, pos + 8, len);
            pos += (int)total;
            if (type == "IEND") yield break;
        }
    }

    /// <summary>整份文件的结构与 CRC 是否完好（走到 IEND 且沿途每个 chunk 的 CRC 都对）。
    ///
    /// 读路径默认不校验 CRC（省一次全文件扫描），但**改写前必须先确认输入是好的**：
    /// 在一份已经损坏的文件上再插一个 chunk，只会把"打不开"变成"更打不开"，
    /// 而且和原先一样全程静默——用户拿到的是一张废图（B12）。</summary>
    public static bool IsCrcValid(byte[] png)
    {
        if (png is null || !HasPngSignature(png)) return false;

        var last = string.Empty;
        foreach (var chunk in Enumerate(png, verifyCrc: true))
            last = chunk.Type;

        // 没走到 IEND ⇒ 中途结构断裂或 CRC 失败，文件不完整
        return last == "IEND";
    }

    /// <summary>是否已含色彩信息 chunk（sRGB / gAMA / iCCP / cHRM）。
    /// 已有时不该再插 sRGB，否则与既有色彩描述冲突。</summary>
    public static bool HasColorChunk(byte[] png)
        => png is not null && HasPngSignature(png) && Enumerate(png).Any(c => ColorChunkTypes.Contains(c.Type));

    /// <summary>
    /// 在 IHDR 之后插入 sRGB chunk（PNG 规范要求 sRGB 位于第一个 IDAT 之前）。
    /// 已有色彩信息、签名不符、结构异常或 CRC 校验失败时原样返回输入，不冒险改写。
    /// </summary>
    /// <param name="renderingIntent">0=Perceptual（默认）。</param>
    public static byte[] InsertSrgbAfterIhdr(byte[] png, byte renderingIntent = 0)
    {
        if (png is null || png.Length < SignatureLength) return png ?? Array.Empty<byte>();
        if (!HasPngSignature(png)) return png;
        if (HasColorChunk(png)) return png;

        // 改写前先确认整份文件是完好的。
        // 自产 PNG（bmp.Save）的 CRC 必然正确，所以这一条**不会**改变既有路径的
        // 输出字节；只有真正损坏 / 截断的输入才会被拒绝改写（B12）。
        if (!IsCrcValid(png)) return png;

        // png.Length 接近 int.MaxValue 时 `png.Length + ChunkOverhead + 1` 会溢出成负数，
        // MemoryStream 直接抛 ArgumentOutOfRangeException（B12）。用 long 算完再钳制；
        // 钳制只影响**初始容量**（MemoryStream 会按需扩容），不影响最终字节。
        long capacity = (long)png.Length + ChunkOverhead + 1;
        if (capacity > int.MaxValue) capacity = int.MaxValue;

        using var ms = new MemoryStream((int)capacity);
        ms.Write(png, 0, Math.Min(SignatureLength, png.Length));

        var pos = SignatureLength;
        var inserted = false;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);
            long total = ChunkOverhead + (long)len;   // 同上：必须 long，否则溢出绕过检查
            if (len < 0 || pos + total > png.Length) break;

            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            ms.Write(png, pos, (int)total);
            pos += (int)total;

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
