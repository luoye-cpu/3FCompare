using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <c>FFF3FPSnapshot</c> 托管镜像与出货树内核头的<b>逐字段偏移对撞</b>。
///
/// <para><b>为什么必须存在这个测试</b>（2026-10-14 事故）：工作区做本体中性化时，把快照里的
/// 五个 <c>hdrDp*</c> DEPRECATED 槽位<b>整体删除</b>，而内核同期做的是"改名 + 恒零 +
/// DEPRECATED 注释"的保留。结果镜像 376 &lt; 内核 392，内核
/// <c>PlayerSession.cpp:1918</c> 的 <c>size &lt; sizeof(FFF3FPSnapshot)</c> 闸门直接返回
/// <c>InvalidArgument</c>，<c>GetSnapshot</c> 整体失效。
/// 更阴的是：即使把尺寸常数硬抬到 392 蒙过闸门，<c>dynamicHdrMetadataActive</c> 之后
/// 每个字段仍会少读 20 B——尺寸相等拦不住"中段少 20、尾部多 20"这种巧合。
/// ⇒ 所以这里比的是<b>每一个字段的偏移</b>，不只是总尺寸。</para>
///
/// <para><b>期望尺寸从内核头推导，不写死</b>：内核尾追加字段后本测试自动跟着走，
/// 不会因为一个硬编码的 392 在下次增长时变成假绿或假红。</para>
///
/// <para><b>只跟出货树（third_party\fff_project）</b>：那是进包的那棵树。内核树
/// （3fp\kernel）当前是 376 布局（有 videoUpload100ns / audioDelay100ns，
/// 没有 dynamicMetadata* 五个），两树 <c>GetApiVersion</c> 同为 20 却布局分叉——
/// 那条分叉由 <c>发布门禁</c> 的 pairing 尺寸闸负责，不归本测试管。</para>
/// </summary>
public class SnapshotLayoutTests
{
    private const string HeaderRelativePath =
        @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h";

    private const string KernelTreeHeaderRelativePath =
        @"3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h";

    private sealed record NativeField(string Name, int Offset, int Size, int Align);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, HeaderRelativePath))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"找不到内核头文件 {HeaderRelativePath}（从 {AppContext.BaseDirectory} 上溯 12 层）。" +
            "布局镜像测试不允许静默跳过：要么它在测真东西，要么它判红。");
    }

    private static string[] ReadHeader(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"内核头文件不存在：{path}");
        return File.ReadAllLines(path);
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx < 0 ? line : line[..idx];
    }

    /// <summary>解析 <c>enum class X : std::uint32_t</c> 的底层类型。</summary>
    private static int EnumWidth(string[] lines, string enumName)
    {
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            var m = Regex.Match(line, $@"^enum\s+class\s+{Regex.Escape(enumName)}\s*:\s*([\w:]+)\s*\{{");
            if (!m.Success) continue;
            return m.Groups[1].Value switch
            {
                "std::uint32_t" or "uint32_t" or "std::int32_t" or "int" => 4,
                "std::uint64_t" or "std::int64_t" => 8,
                "std::uint16_t" => 2,
                "std::uint8_t" => 1,
                _ => throw new InvalidOperationException(
                    $"枚举 {enumName} 的底层类型 {m.Groups[1].Value} 未知，解析器需要扩表"),
            };
        }
        throw new InvalidOperationException(
            $"头文件里找不到 enum class {enumName} 的定义——" +
            "要么它被改名了（内核 ABI 变动，必须有人确认），要么解析器失效了。");
    }

    private static (int Size, int Align) ScalarShape(string[] lines, string cType)
    {
        // 枚举类型：底层宽度决定对齐与尺寸
        if (Regex.IsMatch(cType, @"^enum\s+class\b") || !cType.Contains("::") && !cType.Contains('*'))
        {
            if (cType.StartsWith("enum", StringComparison.Ordinal)) cType = cType["enum class ".Length..].Trim();
            if (cType is not ("std::uint32_t" or "std::int32_t" or "std::uint64_t" or "std::int64_t"
                or "std::uint16_t" or "std::uint8_t" or "float" or "double" or "bool"))
            {
                var w = EnumWidth(lines, cType);
                return (w, w);
            }
        }
        return cType switch
        {
            "float" or "std::uint32_t" or "uint32_t" or "std::int32_t" or "int32_t" => (4, 4),
            "std::int64_t" or "std::uint64_t" or "int64_t" or "uint64_t" or "double" => (8, 8),
            "std::uint16_t" => (2, 2),
            "bool" or "std::uint8_t" => (1, 1),
            _ => throw new InvalidOperationException($"无法解析的 C 字段类型：{cType}"),
        };
    }

    private static List<string> ReadStructBody(string[] lines, string structName)
    {
        var start = Array.FindIndex(lines, l => l.Contains($"struct {structName}"));
        Assert.True(start >= 0, $"头文件里没有 struct {structName}");
        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("};")) break;
            var trimmed = StripComment(lines[i]).Trim();
            if (trimmed.Length == 0) continue;
            body.Add(trimmed);
        }
        Assert.True(body.Count >= 1, $"{structName} 只解析出 {body.Count} 行，解析器失效了");
        return body;
    }

    private static List<NativeField> ParseStruct(string[] lines, string structName)
    {
        var offset = 0;
        var result = new List<NativeField>();
        foreach (var line in ReadStructBody(lines, structName))
        {
            var scalar = Regex.Match(line, @"^((?:const\s+)?[\w:]+(?:\s+[\w:]+)*(?:\s*\*)?)\s+(\w+);$");
            Assert.True(scalar.Success,
                $"SnapshotLayoutTests 解析 {structName} 时遇到无法解析的成员行：{line}");
            var type = scalar.Groups[1].Value;
            var name = scalar.Groups[2].Value;
            var (sz, al) = ScalarShape(lines, type);
            offset = (offset + al - 1) / al * al;
            result.Add(new NativeField(name, offset, sz, al));
            offset += sz;
        }
        return result;
    }

    /// <summary>原生结构按自身最大对齐补齐后的总尺寸。</summary>
    private static int NativeSizeOf(List<NativeField> fields)
    {
        var last = fields[^1];
        var end = last.Offset + last.Size;
        var align = fields.Max(f => f.Align);
        return (end + align - 1) / align * align;
    }

    /// <summary>
    /// 原生 camelCase → 托管 PascalCase。内核头里少数字段名在托管侧有历史别名，
    /// 一律登记在这张表里；映射不出来就炸，不允许静默跳过字段。
    /// </summary>
    private static string ManagedNameOf(NativeField f) => f.Name switch
    {
        "decodeMode" => "Decoder",   // 托管侧历史命名，内核侧叫 decodeMode
        _ => char.ToUpperInvariant(f.Name[0]) + f.Name[1..],
    };

    [Fact]
    public void 快照镜像与出货树内核头逐字段偏移一致()
    {
        var lines = ReadHeader(HeaderRelativePath);
        var fields = ParseStruct(lines, "FFF3FPSnapshot");
        var expectedSize = NativeSizeOf(fields);

        var mismatches = new List<string>();
        var managedType = typeof(Fff3FpSnapshot);

        foreach (var f in fields)
        {
            var managedName = ManagedNameOf(f);
            var field = managedType.GetField(managedName);
            if (field is null)
            {
                mismatches.Add($"原生 {f.Name}（偏移 {f.Offset}）在托管侧没有对应字段（期望名 {managedName}）");
                continue;
            }
            var actualOffset = Marshal.OffsetOf(managedType, managedName).ToInt32();
            var actualSize = Marshal.SizeOf(field.FieldType);
            if (actualOffset != f.Offset)
                mismatches.Add($"{f.Name}: 偏移 原生={f.Offset} 托管={actualOffset}");
            if (actualSize != f.Size)
                mismatches.Add($"{f.Name}: 宽度 原生={f.Size} 托管={actualSize}");
        }

        var managedNames = managedType.GetFields().Select(x => x.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expectedNames = fields.Select(ManagedNameOf)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!managedNames.SequenceEqual(expectedNames))
        {
            mismatches.Add("字段集合不同：多出 ["
                + string.Join(",", managedNames.Except(expectedNames)) + "] 缺少 ["
                + string.Join(",", expectedNames.Except(managedNames)) + "]");
        }

        var managedTotal = Marshal.SizeOf(managedType);
        if (managedTotal != expectedSize)
            mismatches.Add($"总尺寸 内核头推导={expectedSize} 托管={managedTotal}");

        Assert.True(mismatches.Count == 0,
            $"FFF3FPSnapshot 托管镜像与 {HeaderRelativePath} 不一致：\n  "
            + string.Join("\n  ", mismatches));
    }

    /// <summary>
    /// 尺寸闸专项：内核 <c>PlayerSession.cpp:1918</c> 用 <c>size &lt; sizeof(FFF3FPSnapshot)</c>
    /// 拒绝过小的调用方。镜像只要比内核短一个字节，<c>GetSnapshot</c> 就整体 <c>InvalidArgument</c>。
    /// 这里把它单独钉成一条，失败时能一眼看出"是尺寸不够"而不是"某个字段偏了"。
    /// </summary>
    [Fact]
    public void 镜像尺寸不得小于内核快照尺寸()
    {
        var lines = ReadHeader(HeaderRelativePath);
        var expectedSize = NativeSizeOf(ParseStruct(lines, "FFF3FPSnapshot"));
        var managedTotal = Marshal.SizeOf<Fff3FpSnapshot>();

        Assert.True(managedTotal >= expectedSize,
            $"托管镜像 {managedTotal} B < 内核 sizeof(FFF3FPSnapshot) {expectedSize} B ⇒ " +
            "GetSnapshot 会被内核的 size 闸门判为 InvalidArgument，快照链路整体失效。");
        Assert.Equal(expectedSize, managedTotal);
    }

    /// <summary>
    /// 五个 DEPRECATED 槽位必须存在。这是 2026-10-14 事故的回归钉：
    /// 中性化的正确动作是"改名 + 恒零"，<b>不是删字段</b>——删了就等于 ABI 破坏。
    /// </summary>
    [Theory]
    [InlineData("HdrDpProfile")]
    [InlineData("HdrDpLevel")]
    [InlineData("HasHDRdpRpu")]
    [InlineData("HasHDRdpEnhancementLayer")]
    [InlineData("HdrDpEnhancementLayer")]
    public void 中性化不得删除DEPRECATED占位槽(string managedName)
    {
        var field = typeof(Fff3FpSnapshot).GetField(managedName);
        Assert.NotNull(field);
        Assert.Equal(typeof(uint), field!.FieldType);

        // 内核头里也必须还在（两棵树都在），否则是内核侧先动了 ABI，需要人工确认。
        foreach (var relative in new[] { HeaderRelativePath, KernelTreeHeaderRelativePath })
        {
            var text = string.Join("\n", ReadHeader(relative));
            var nativeName = char.ToLowerInvariant(managedName[0]) + managedName[1..];
            Assert.Contains(nativeName, text);
        }
    }

    /// <summary>
    /// 两棵树同为 API 20 但布局分叉（出货树 392 / 内核树 376）这件事，本测试只负责
    /// <b>记录并断言"镜像跟的是出货树"</b>——镜像里不能有内核树独有的那两个 100ns 字段。
    /// 真正的"按 DLL 实测尺寸"闸在发布门禁的 pairing 步，不归单元测试管。
    /// </summary>
    [Fact]
    public void 镜像跟随出货树而非内核树()
    {
        var managedNames = typeof(Fff3FpSnapshot).GetFields().Select(x => x.Name).ToList();
        Assert.DoesNotContain("VideoUpload100ns", managedNames);
        Assert.DoesNotContain("AudioDelay100ns", managedNames);
        Assert.Contains("DynamicMetadataWindows", managedNames);
        Assert.Contains("AudioBinaural", managedNames);

        // 内核树那侧确实还留着这两个字段 ⇒ 分叉仍然真实存在，别把这条断言读成"两树已对齐"。
        var kfText = string.Join("\n", ReadHeader(KernelTreeHeaderRelativePath));
        Assert.Contains("videoUpload100ns", kfText);
        Assert.Contains("audioDelay100ns", kfText);
    }
}
