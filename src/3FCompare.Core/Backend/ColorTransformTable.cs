using System.Runtime.InteropServices;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Backend;

/// <summary>kind=3 外部色彩变换表：一张 33³ 采样的立体 LUT 加一个线性光 3x3 矩阵，
/// 对应内核 <c>FFF3FPColorTransformPayload</c>（431296 字节，由内核头 static_assert 钉死）。
/// <para>内核只按数值消费它：形状校验见 <c>HdrProcessor.cpp</c> 的 <c>ValidColorTransform</c>
/// （尺寸必须<b>恰好</b>等于结构大小、version=1、resolution=33、domain≤1、所有 float 有限），
/// 内容一概不解释——所以"这张表画成什么样"完全是宿主的责任，内核不会替拦。</para>
/// <para>三条装载路径：<see cref="Identity"/>、<see cref="ChannelPermutation"/>、
/// <see cref="TryFromPayload"/>（外部模块 <c>analyzeFrame</c> 的输出字节）。
/// 表本身不可变，装载后只读，可安全跨线程复用。</para>
/// </summary>
public sealed class ColorTransformTable
{
    /// <summary>网格分辨率，与内核 <c>kColorTransformLutResolution</c> 同水
    /// （由 <c>ColorTransformLayoutTests</c> 对账内核头）。</summary>
    public const uint LutResolution = 33;

    /// <summary>行主序 3x3 的浮点个数（out = M * rgb）。</summary>
    public const int MatrixFloatCount = 9;

    /// <summary>平坦 LUT 的浮点个数：33³ 个 texel，每 texel 一个 float3 = 107811。</summary>
    public const int CurveLutFloatCount =
        (int)LutResolution * (int)LutResolution * (int)LutResolution * 3;

    /// <summary>域 0：表的输入轴是<b>编码分量</b>，输出直接顶替 YUV→RGB 矩阵（见着色器
    /// <c>TransformLut.SampleLevel(LinearSampler, float3(y,chroma), 0)</c>）。</summary>
    public const uint LutDomainCodedComponents = 0;

    /// <summary>域 1：表在内核自己的转换之后作用于<b>编码 RGB</b>（out = LUT(in) 逐通道）。</summary>
    public const uint LutDomainEncodedRgb = 1;

    /// <summary>载荷版本号，即包的 KindVersion（内核按 ==1 严格校验）。</summary>
    public const uint PayloadVersion = 1;

    private readonly float[] _outputMatrix;
    private readonly float[] _curveLut;

    private ColorTransformTable(uint lutDomain, float[] outputMatrix, float[] curveLut)
    {
        LutDomain = lutDomain;
        _outputMatrix = outputMatrix;
        _curveLut = curveLut;
    }

    /// <summary>表的域（0=替代编码分量→RGB，1=作用于编码 RGB）。</summary>
    public uint LutDomain { get; }

    /// <summary>行主序 3x3，作用于传函解码后的线性光信号。</summary>
    public IReadOnlyList<float> OutputMatrix => _outputMatrix;

    /// <summary>平坦 LUT，texel 索引 (z*res + y)*res + x，每 texel 三个 float（R,G,B）。</summary>
    public ReadOnlySpan<float> CurveLut => _curveLut;

    // 同程序集的换算层（ColorTransformPayloadMarshal）要整块读写这两个数组；
    // 私有字段跨类不可见，留两个 internal 视图，公开面仍是只读的。
    internal float[] MatrixStorage => _outputMatrix;
    internal float[] CurveLutStorage => _curveLut;

    /// <summary>恒等表：矩阵是单位阵，LUT 逐格填"输出=输入"。
    /// <para><b>全零 curveLut 不是恒等</b>——它把每个采样点都映到 (0,0,0)，画面压成黑；
    /// 内核的 <c>ValidColorTransform</c> 只查"有限"，全零表照样通过校验，所以这道坑没人拦。
    /// 只有 <c>outputMatrix</c> 全零才等于单位阵（内核在 <c>BuildColorTransformSettings</c>
    /// 里做归一）。这里矩阵仍写成显式单位阵，是为了不依赖那条归一分支——
    /// 两条树里少一处"靠约定"就少一处漂移面。</para>
    /// <para><paramref name="lutDomain"/> 默认取域 1（作用于编码 RGB），因为域 0 下恒等表的
    /// 输出会被当成 RGB 直接用，YUV 源上会得到 (Y,Cb,Cr) 当颜色的错画面——恒等只在域 1 是恒等。</para>
    /// </summary>
    public static ColorTransformTable Identity(uint lutDomain = LutDomainEncodedRgb) =>
        new(lutDomain, UnitMatrix(), IdentityCurveLut());

    /// <summary>恒等 LUT + 通道置换矩阵：<paramref name="redSource"/> 等给出输出的每一行
    /// 取哪个输入通道（0=R/1=G/2=B），三个索引必须互不相同（置换而非混合，混合请走
    /// <see cref="TryFromPayload"/> 或自己填矩阵）。
    /// <para>用途举例：把源里被反接的 R/B 两路换回来。矩阵按行主序写
    /// <c>M[row][redSource[row]] = 1</c>，于是 out.r = in[redSource]。</para></summary>
    public static ColorTransformTable ChannelPermutation(
        int redSource, int greenSource, int blueSource, uint lutDomain = LutDomainEncodedRgb)
    {
        var sources = new[] { redSource, greenSource, blueSource };
        var names = new[] { nameof(redSource), nameof(greenSource), nameof(blueSource) };
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i] < 0 || sources[i] > 2)
                throw new ArgumentOutOfRangeException(names[i], sources[i],
                    "通道索引必须落在 0..2");
        }
        if (sources[0] == sources[1] || sources[0] == sources[2] || sources[1] == sources[2])
            throw new ArgumentException($"通道置换要求三个来源互不相同（当前 {redSource},{greenSource},{blueSource}）");

        var matrix = new float[MatrixFloatCount];
        for (var row = 0; row < 3; row++) matrix[row * 3 + sources[row]] = 1.0f;
        return new ColorTransformTable(lutDomain, matrix, IdentityCurveLut());
    }

    /// <summary>装载外部模块 <c>analyzeFrame</c> 写出的载荷字节（kind=3 的原生布局，
    /// 逐字段镜像 <see cref="Fff3FpColorTransformPayload"/>）。
    /// <para>失败原因都在返回值里，不抛：<paramref name="error"/> 给中文说明。
    /// 这里把内核 <c>ValidColorTransform</c> 的五道门先在托管侧过一遍——内核的拒绝是
    /// <b>整批 InvalidArgument</b>，等到那时才失败会连同一批里的其他包一起丢，
    /// 而宿主手里正好有能解释"为什么这批被拒"的原始字节。</para></summary>
    public static bool TryFromPayload(ReadOnlySpan<byte> payload,
        out ColorTransformTable? table, out string? error)
    {
        table = null;
        error = null;
        var nativeSize = ColorTransformPayloadMarshal.NativePayloadByteCount;
        if (payload.Length != nativeSize)
        {
            error = $"载荷长度 {payload.Length} 不等于 FFF3FPColorTransformPayload 的 {nativeSize} 字节";
            return false;
        }

        var size = ColorTransformPayloadMarshal.ReadHeader(payload, out var version,
            out var resolution, out var domain);
        if (size != nativeSize)
        {
            error = $"载荷自报 size={size} 与结构大小 {nativeSize} 不符（内核对 size 是等号校验）";
            return false;
        }
        if (version != PayloadVersion)
        {
            error = $"载荷 version={version}，内核只认 {PayloadVersion}";
            return false;
        }
        if (resolution != LutResolution)
        {
            error = $"载荷 lutResolution={resolution}，内核只认 {LutResolution}";
            return false;
        }
        if (domain > LutDomainEncodedRgb)
        {
            error = $"载荷 lutDomain={domain} 超出 0..{LutDomainEncodedRgb}";
            return false;
        }

        var matrix = new float[MatrixFloatCount];
        ColorTransformPayloadMarshal.ReadFloats(payload,
            ColorTransformPayloadMarshal.MatrixOffset, matrix);
        var lut = new float[CurveLutFloatCount];
        ColorTransformPayloadMarshal.ReadFloats(payload,
            ColorTransformPayloadMarshal.CurveLutOffset, lut);

        for (var i = 0; i < matrix.Length; i++)
        {
            if (!float.IsFinite(matrix[i]))
            {
                error = $"outputMatrix[{i}] 非有限值 {matrix[i]}，内核会整批拒绝";
                return false;
            }
        }
        for (var i = 0; i < lut.Length; i++)
        {
            if (!float.IsFinite(lut[i]))
            {
                error = $"curveLut[{i}] 非有限值 {lut[i]}，内核会整批拒绝";
                return false;
            }
        }

        table = new ColorTransformTable(domain, matrix, lut);
        return true;
    }

    /// <summary>本帧的包：kind=3 + 匹配键 frameIndex（与 HDR10+ 注入同一条
    /// "最后一条 ≤ 当前帧"规则，见内核 <c>InstallInjectedTable</c>）。</summary>
    public DynamicMetadataPacket ToPacket(long frameIndex) => new()
    {
        Kind = DynamicMetadataKind.ExternalColorTransform,
        FrameIndex = frameIndex,
        Payload = this,
    };

    private static float[] UnitMatrix()
    {
        var m = new float[MatrixFloatCount];
        m[0] = 1.0f; m[4] = 1.0f; m[8] = 1.0f;
        return m;
    }

    /// <summary>逐格填"输出=输入"的恒等 LUT（平坦索引 (z*res+y)*res+x，步距 1/(res-1)）。
    /// 分辨率 33 ⇒ 步距 1/32 是二进制精确值，格点值就是 x/32、y/32、z/32，无舍入。</summary>
    private static float[] IdentityCurveLut()
    {
        var res = (int)LutResolution;
        var step = 1f / (res - 1);
        var lut = new float[CurveLutFloatCount];
        var i = 0;
        for (var z = 0; z < res; z++)
        {
            for (var y = 0; y < res; y++)
            {
                for (var x = 0; x < res; x++)
                {
                    lut[i * 3] = x * step;
                    lut[i * 3 + 1] = y * step;
                    lut[i * 3 + 2] = z * step;
                    i++;
                }
            }
        }
        return lut;
    }
}

/// <summary>kind=3 载荷 ↔ 钉住缓冲区的双向换算（派发层与装载层用）。
/// <para><b>为什么不直接 <c>GCHandle.Alloc(Fff3FpColorTransformPayload 实例, Pinned)</c></b>：
/// 该镜像结构的两个数组字段是 <c>UnmanagedType.ByValArray</c>，托管侧它们是有引用的
/// <c>float[]</c>（实例只有 32 字节：16 字节头 + 两个引用），只有跨越非托管边界时才摊平成
/// 内联数据。把盒子直接钉住交给内核，内核会从偏移 16 读到托管对象指针、再往后读 431244 字节
/// 堆垃圾——形状校验照样过（读到的多半是有限值），画面就歪了。所以这里显式按偏移把字段
/// 写进一块 pinned 数组，让缓冲区自身就是原生布局。</para>
/// <para>也不走 <c>Marshal.AllocHGlobal</c>：那是 421 KB 的非托管块，漏一次 Free 就是
/// 每帧一次的泄漏且没有终结器兜底。pinned 数组的句柄进调用方的 payloadHandles 集合，
/// 复用既有的 finally 释放路径。</para>
/// <para>字段偏移全部问 <c>Marshal.OffsetOf</c>，布局的事实源仍是 <see cref="Fff3FpColorTransformPayload"/>
/// 那一个镜像结构；该结构也<b>从不作为 P/Invoke 形参按值传递</b>（包里的 Payload 是 nint）。</para>
/// </summary>
internal static class ColorTransformPayloadMarshal
{
    /// <summary>原生载荷字节数，取自镜像结构（== 内核 static_assert 的 431296）。</summary>
    internal static int NativePayloadByteCount { get; } =
        Marshal.SizeOf<Fff3FpColorTransformPayload>();

    internal static int SizeOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.Size));

    internal static int VersionOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.Version));

    internal static int LutResolutionOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.LutResolution));

    internal static int LutDomainOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.LutDomain));

    internal static int MatrixOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.OutputMatrix));

    internal static int CurveLutOffset { get; } = (int)Marshal.OffsetOf<Fff3FpColorTransformPayload>(
        nameof(Fff3FpColorTransformPayload.CurveLut));

    /// <summary>表 → 原生布局的 pinned 缓冲区。调用方负责在原生调用返回后 Free 句柄。</summary>
    internal static byte[] BuildPinnedBuffer(ColorTransformTable table, out GCHandle handle)
    {
        ArgumentNullException.ThrowIfNull(table);
        var buffer = new byte[NativePayloadByteCount];
        var span = buffer.AsSpan();

        // 四个头字段先落成局部量：MemoryMarshal.Write 的形参是 in，
        // 常量与属性访问都取不到地址（CS8156）。
        uint size = (uint)NativePayloadByteCount;
        uint version = ColorTransformTable.PayloadVersion;
        uint resolution = ColorTransformTable.LutResolution;
        uint domain = table.LutDomain;
        MemoryMarshal.Write(span.Slice(SizeOffset), in size);
        MemoryMarshal.Write(span.Slice(VersionOffset), in version);
        MemoryMarshal.Write(span.Slice(LutResolutionOffset), in resolution);
        MemoryMarshal.Write(span.Slice(LutDomainOffset), in domain);
        WriteFloats(span, MatrixOffset, table.MatrixStorage);
        WriteFloats(span, CurveLutOffset, table.CurveLutStorage);

        handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        return buffer;
    }

    /// <summary>从原生布局缓冲区读出 9 个矩阵元素之外的三个头字段（size 单独返回）。</summary>
    internal static uint ReadHeader(ReadOnlySpan<byte> payload,
        out uint version, out uint lutResolution, out uint lutDomain)
    {
        version = ReadUInt(payload, VersionOffset);
        lutResolution = ReadUInt(payload, LutResolutionOffset);
        lutDomain = ReadUInt(payload, LutDomainOffset);
        return ReadUInt(payload, SizeOffset);
    }

    internal static uint ReadUInt(ReadOnlySpan<byte> payload, int offset) =>
        MemoryMarshal.Read<uint>(payload.Slice(offset, 4));

    internal static void ReadFloats(ReadOnlySpan<byte> payload, int offset, float[] destination)
    {
        var source = MemoryMarshal.Cast<byte, float>(
            payload.Slice(offset, destination.Length * sizeof(float)));
        source.CopyTo(destination);
    }

    private static void WriteFloats(Span<byte> payload, int offset, float[] source)
    {
        var target = MemoryMarshal.Cast<byte, float>(
            payload.Slice(offset, source.Length * sizeof(float)));
        source.AsSpan().CopyTo(target);
    }
}
