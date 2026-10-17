using System.Runtime.InteropServices;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 托管 interop 结构必须与内核头文件里的原生布局逐字段一致。
///
/// 为什么不是把数字再抄一遍：抄两份常数只能挡住"两边同时改错同一处"以外的情况，
/// 而真正的事故形态是**同尺寸、换字段顺序** —— 那时 `Marshal.SizeOf` 仍然相等，
/// 内核的 size 闸门照样放行，字段却按错偏移读出（诊断值全错却没人报错）。
/// 所以这里从原生头解析出字段名/类型，按 C 的对齐规则算偏移，再与
/// `Marshal.OffsetOf` 逐个比。头文件读不到必须判红，不能跳过。
/// </summary>
public class HdrSignalDiagnosticsLayoutTests
{
    private const string HeaderRelativePath =
        @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h";
    private const string StructName = "FFF3FPHdrSignalDiagnostics";

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

    private static List<(string Name, string CType)> ParseNativeStruct()
    {
        var header = Path.Combine(FindRepositoryRoot(), HeaderRelativePath);
        var lines = File.ReadAllLines(header);
        var start = Array.FindIndex(lines, l => l.Contains($"struct {StructName}"));
        Assert.True(start >= 0, $"头文件里没有 struct {StructName}");
        var fields = new List<(string, string)>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("};")) break;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
            // 形如：std::uint64_t dynamicCurveFramesApplied;   / std::uint32_t size;
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^(std::uint(?:32|64)_t|uint32_t|uint64_t)\s+(\w+)\s*;\s*(?://.*)?$");
            if (!match.Success)
            {
                // 出现没认出来的字段形态时必须炸，而不是悄悄少测一个字段
                throw new InvalidOperationException($"{StructName} 里有无法解析的成员行：{trimmed}");
            }
            fields.Add((match.Groups[2].Value, match.Groups[1].Value));
        }
        Assert.True(fields.Count >= 8, $"{StructName} 只解析出 {fields.Count} 个字段，解析器失效了");
        return fields;
    }

    private static int NativeSize(string cType) => cType.EndsWith("64_t") ? 8 : 4;
    private static int NativeAlign(string cType) => cType.EndsWith("64_t") ? 8 : 4;

    [Fact]
    public void ManagedStructMirrorsNativeLayout()
    {
        var fields = ParseNativeStruct();

        // 按 C 的对齐规则算原生偏移
        var expected = new List<(string Name, int Offset, int Size)>();
        var offset = 0;
        foreach (var (name, cType) in fields)
        {
            var size = NativeSize(cType);
            var align = NativeAlign(cType);
            offset = (offset + align - 1) / align * align;
            expected.Add((name, offset, size));
            offset += size;
        }
        var nativeTotal = (offset + 7) / 8 * 8;   // 最大对齐 8 ⇒ 尾部补齐

        var managedType = typeof(Fff3FpHdrSignalDiagnostics);
        var mismatches = new List<string>();

        foreach (var (name, expOffset, expSize) in expected)
        {
            var managedName = char.ToUpperInvariant(name[0]) + name[1..];
            var field = managedType.GetField(managedName);
            if (field is null)
            {
                mismatches.Add($"原生 {name} 在托管侧没有对应字段（期望名 {managedName}）");
                continue;
            }
            var actualOffset = Marshal.OffsetOf(managedType, managedName).ToInt32();
            var actualSize = Marshal.SizeOf(field.FieldType);
            if (actualOffset != expOffset)
                mismatches.Add($"{name}: 偏移 原生={expOffset} 托管={actualOffset}");
            if (actualSize != expSize)
                mismatches.Add($"{name}: 宽度 原生={expSize} 托管={actualSize}");
        }

        var managedNames = managedType.GetFields()
            .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expectedNames = expected.Select(e =>
        {
            var n = e.Name;
            return char.ToUpperInvariant(n[0]) + n[1..];
        }).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!managedNames.SequenceEqual(expectedNames))
        {
            mismatches.Add($"字段集合不同：托管独有 [{string.Join(",", managedNames.Except(expectedNames))}] " +
                           $"原生独有 [{string.Join(",", expectedNames.Except(managedNames))}]");
        }

        var managedTotal = Marshal.SizeOf<Fff3FpHdrSignalDiagnostics>();
        if (managedTotal != nativeTotal)
            mismatches.Add($"总尺寸 原生={nativeTotal} 托管={managedTotal}");

        Assert.True(mismatches.Count == 0,
            $"{StructName} 托管镜像与内核头不一致：\n  " + string.Join("\n  ", mismatches));
    }

    /// <summary>
    /// 尺寸必须与内核头文件里那句 static_assert 同一个数。两边各自钉一次，
    /// 是为了让"只改一边"在编译期或测试期就炸，而不是等运行期读出错位。
    /// </summary>
    [Fact]
    public void StructSizeIsPinnedToTheKernelAssertion()
    {
        Assert.Equal(64, Marshal.SizeOf<Fff3FpHdrSignalDiagnostics>());

        var header = Path.Combine(FindRepositoryRoot(), HeaderRelativePath);
        var text = File.ReadAllText(header);
        Assert.Contains($"static_assert(sizeof({StructName}) == 64", text);
    }
}
