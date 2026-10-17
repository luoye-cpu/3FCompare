using System.Runtime.InteropServices;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 通用动态元数据注入通道（2026-10-10）的两个原生结构——
/// <c>FFF3FPDynamicMetadataPacket</c>（40B）与 <c>FFF3FPHdrVividMetadataPayload</c>（68B）——
/// 的托管镜像必须与内核头文件逐字段一致。做法同 <see cref="HdrSignalDiagnosticsLayoutTests"/>：
/// 从内核头解析字段名/类型、按 C 的对齐规则算偏移，再与 <see cref="Marshal.OffsetOf"/> 逐个比。
///
/// <para>为什么不能只比总尺寸：真正的事故形态是<b>同尺寸、换字段顺序</b>，
/// 那时 size 闸门照样放行，字段却按错偏移读写（注入表悄悄错位）。</para>
///
/// <para>与那个文件相比这里多出三种字段形态，解析器必须逐一认，认不出就炸（不允许静默少测）：
/// 裸指针 <c>const void* payload</c>、内联数组 <c>float maxRGB[3]</c>、
/// 嵌套结构数组 <c>FFF3FPHdrVividMetadataWindow windows[3]</c>（托管侧已扁平化成 W0/W1/W2 前缀字段）。</para>
/// </summary>
public class DynamicMetadataLayoutTests
{
    private const string HeaderRelativePath =
        @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h";

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

    /// <summary>原生一个（可能由数组/嵌套展开而来的）标量项：C 字段名、偏移、字节宽。</summary>
    private sealed record NativeField(string Name, int Offset, int Size);

    private static (int Size, int Align) ScalarShape(string cType) => cType switch
    {
        "float" or "std::uint32_t" or "uint32_t" => (4, 4),
        "std::int64_t" or "std::uint64_t" or "int64_t" => (8, 8),
        "const void*" => (8, 8),
        _ => throw new InvalidOperationException($"无法解析的 C 字段类型：{cType}"),
    };

    /// <summary>从头文件切出某个 struct 的原始行（去掉注释与空行）。</summary>
    private static List<string> ReadStructBody(string structName)
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), HeaderRelativePath));
        var start = Array.FindIndex(lines, l => l.Contains($"struct {structName}"));
        Assert.True(start >= 0, $"头文件里没有 struct {structName}");
        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("};")) break;
            var trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0) continue;
            body.Add(trimmed);
        }
        // 嵌套的窗口结构（FFF3FPHdrVividMetadataWindow）真的只有 2 个成员，
        // 原来的 >= 4 会把合法的小结构误判成"解析器失效"。0 才是解析失败的信号；
        // 非 0 但内容不对会在 ParseStruct 的成员行断言上炸出来，不会静默。
        Assert.True(body.Count >= 1, $"{structName} 只解析出 {body.Count} 行，解析器失效了");
        return body;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx < 0 ? line : line[..idx];
    }

    /// <summary>
    /// 解析 struct 为扁平标量项列表。数组（内联或嵌套 struct）按元素展开，
    /// 名称用 <c>field[index]</c> 与 <c>field[index].sub</c> 表示——托管侧的扁平镜像
    /// 由调用方按各自约定映射，这里只保证原生侧算术正确。
    /// </summary>
    private static List<NativeField> ParseStruct(string structName)
    {
        var offset = 0;
        var result = new List<NativeField>();
        foreach (var line in ReadStructBody(structName))
        {
            // 1) 数组形态优先：`TYPE name[N];`（TYPE 是内联标量或另一个 struct）
            var array = System.Text.RegularExpressions.Regex.Match(
                line, @"^([\w:]+(?:\s*\*)?)\s+(\w+)\[(\d+)\];$");
            if (array.Success)
            {
                var type = array.Groups[1].Value;
                var name = array.Groups[2].Value;
                var count = int.Parse(array.Groups[3].Value);
                if (type is "float" or "std::uint32_t" or "uint32_t" or "std::uint64_t" or "uint64_t" or "std::int64_t")
                {
                    var (sz, al) = ScalarShape(type);
                    for (var i = 0; i < count; i++)
                    {
                        offset = (offset + al - 1) / al * al;
                        result.Add(new NativeField($"{name}[{i}]", offset, sz));
                        offset += sz;
                    }
                }
                else
                {
                    // 嵌套 struct 数组：先拿子结构布局，再按元素展开
                    var sub = ParseStruct(type);
                    var subSize = sub[^1].Offset + sub[^1].Size;
                    var subAlign = sub.Max(f => f.Size);
                    offset = (offset + subAlign - 1) / subAlign * subAlign;
                    for (var i = 0; i < count; i++)
                    {
                        foreach (var f in sub)
                            result.Add(new NativeField($"{name}[{i}].{f.Name}", offset + f.Offset, f.Size));
                        offset += subSize;
                    }
                }
                continue;
            }

            // 2) 标量/指针形态：`TYPE name;`
            // 类型允许多词（"const void*" 这类含空格的类型）：stray 星号必须挂在最后一个词尾，
            // 否则 FFF3FPDynamicMetadataPacket 的 payload 字段会让解析断言炸在解析器自己身上。
            var scalar = System.Text.RegularExpressions.Regex.Match(
                line, @"^((?:const\s+)?[\w:]+(?:\s+[\w:]+)*(?:\s*\*)?)\s+(\w+);$");
            Assert.True(scalar.Success,
                $"DynamicMetadataLayoutTests 解析 {structName} 时遇到无法解析的成员行：{line}");
            var (s, a) = ScalarShape(scalar.Groups[1].Value);
            offset = (offset + a - 1) / a * a;
            result.Add(new NativeField(scalar.Groups[2].Value, offset, s));
            offset += s;
        }
        return result;
    }

    /// <summary>逐字段断言托管镜像与解析出的原生布局一致，并核对字段集合与总尺寸。</summary>
    private static void AssertMirror(string structName, Type managedType, int expectedTotal,
        Func<NativeField, string> managedNameOf)
    {
        var fields = ParseStruct(structName);
        var mismatches = new List<string>();

        foreach (var f in fields)
        {
            var managedName = managedNameOf(f);
            var field = managedType.GetField(managedName);
            if (field is null)
            {
                mismatches.Add($"原生 {f.Name} 在托管侧没有对应字段（期望名 {managedName}）");
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
        var expectedNames = fields.Select(managedNameOf).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!managedNames.SequenceEqual(expectedNames))
        {
            mismatches.Add($"字段集合不同：托管侧 [{string.Join(",", managedNames)}] " +
                           $"原生侧 [{string.Join(",", expectedNames)}]");
        }

        var managedTotal = Marshal.SizeOf(managedType);
        if (managedTotal != expectedTotal)
            mismatches.Add($"总尺寸 原生 static_assert={expectedTotal} 托管={managedTotal}");

        Assert.True(mismatches.Count == 0,
            $"{structName} 托管镜像与内核头不一致：\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void 通用通道包镜像与内核布局逐字段一致()
    {
        AssertMirror(
            "FFF3FPDynamicMetadataPacket", typeof(Fff3FpDynamicMetadataPacket), 40,
            f => char.ToUpperInvariant(f.Name[0]) + f.Name[1..]);

        var header = File.ReadAllText(Path.Combine(FindRepositoryRoot(), HeaderRelativePath));
        Assert.Contains("static_assert(sizeof(FFF3FPDynamicMetadataPacket) == 40", header);
    }

    [Fact]
    public void Vivid载荷镜像与内核布局逐字段一致()
    {
        // 托管侧把 windows[3] 扁平成 W0/W1/W2 前缀字段（maxRGB[3] → MaxRGB0..2）。
        AssertMirror(
            "FFF3FPHdrVividMetadataPayload", typeof(Fff3FpHdrVividMetadataPayload), 68,
            f =>
            {
                var windows = System.Text.RegularExpressions.Regex.Match(
                    f.Name, @"^windows\[(\d+)\]\.(?:maxRGB\[(\d+)\]|averageMaxRGB)$");
                if (windows.Success)
                {
                    var w = windows.Groups[1].Value;
                    return windows.Groups[2].Success
                        ? $"W{w}MaxRGB{windows.Groups[2].Value}"
                        : $"W{w}AverageMaxRGB";
                }
                return char.ToUpperInvariant(f.Name[0]) + f.Name[1..];
            });

        var header = File.ReadAllText(Path.Combine(FindRepositoryRoot(), HeaderRelativePath));
        Assert.Contains("static_assert(sizeof(FFF3FPHdrVividMetadataPayload) == 68", header);
    }

    /// <summary>68 = 4×u32 头 + 3 窗口 × (3×float 峰值 + 1×float 均值) + float 目标峰值，
    /// 手算值与内核 static_assert 各钉一次，"只改一边"必须炸。</summary>
    [Fact]
    public void 尺寸手算与内核断言同一个数()
    {
        Assert.Equal(68, Marshal.SizeOf<Fff3FpHdrVividMetadataPayload>());
        Assert.Equal(40, Marshal.SizeOf<Fff3FpDynamicMetadataPacket>());
    }
}
