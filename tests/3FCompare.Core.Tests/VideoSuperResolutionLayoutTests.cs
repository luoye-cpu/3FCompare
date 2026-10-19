using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// RTX VSR 状态结构的托管镜像 ↔ 内核布局对账。
///
/// <para>为什么不是把数字再抄一遍：抄两份常数只能挡住"两边同时改错同一处"以外的情况，
/// 而真正的事故形态是**同尺寸、换字段顺序** —— 那时 <c>Marshal.SizeOf</c> 仍然相等，
/// 内核的 size 闸门照样放行，字段却按错偏移读出（诊断值全错却没人报错）。所以字段序列
/// 从**出货内核头**解析，逐个偏移与 <c>Marshal.OffsetOf</c> 比。</para>
///
/// <para>本文件早期不是这个形状：<c>FFF3FPVideoSuperResolutionStatus</c> 当时只存在于上游
/// PR #19 的头里，出货头连这个类型都没有，于是只能"自钉 44 字节 + 断言出货头仍然没有它"。
/// 2026-10-20 该类型随 PR #19 一族合并进上游、出货树取到同一个提交，那条记账断言的前提结束，
/// 按它自己写明的处置换成了现在的按头解析交叉校验。</para>
///
/// <para>头文件读不到、或出现解析不了的成员行/未登记的类型，一律抛，不许静默少测一个字段。</para>
/// </summary>
public class VideoSuperResolutionLayoutTests
{
    private const string ShippingHeaderRelativePath =
        @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h";

    private const string NativeStructName = "FFF3FPVideoSuperResolutionStatus";

    [Fact]
    public void MirroredStructMatchesTheKernelFieldOrderAndWidths()
    {
        var native = ParseNativeFields();
        var managed = typeof(Fff3FpVideoSuperResolutionStatus).GetFields()
            .ToDictionary(f => f.Name, f => Marshal.OffsetOf<Fff3FpVideoSuperResolutionStatus>(f.Name).ToInt32());

        var problems = new List<string>();
        var expectedOffset = 0;
        foreach (var (name, size) in native)
        {
            var managedName = char.ToUpperInvariant(name[0]) + name[1..];
            if (!managed.TryGetValue(managedName, out var offset))
            {
                problems.Add($"托管缺字段 {managedName}（内核头 {name} @{expectedOffset}）");
            }
            else if (offset != expectedOffset)
            {
                problems.Add($"{managedName} 偏移 {offset} ≠ 内核头推导的 {expectedOffset}（顺序或宽度漂了）");
            }
            expectedOffset += size;
        }

        var extra = managed.Keys
            .Except(native.Select(f => char.ToUpperInvariant(f.Name[0]) + f.Name[1..]))
            .ToList();
        if (extra.Count > 0) problems.Add("托管多出字段： " + string.Join(",", extra));

        // 内核按 `size < sizeof || version != 20` 拒过小的调用方，所以总尺寸也要相等才谈得上偏移对账。
        var total = Marshal.SizeOf<Fff3FpVideoSuperResolutionStatus>();
        if (total != expectedOffset)
            problems.Add($"总尺寸 托管={total} 内核头推导={expectedOffset}");

        Assert.True(problems.Count == 0,
            $"{NativeStructName} 镜像与内核头不一致：\n  {string.Join("\n  ", problems)}");
    }

    /// <summary>版本戳必须跟托管侧唯一的 API 水位同值：内核那边是硬比 <c>!= 20</c>，
    /// 而不是"不低于"。这一条把"改了 ConfigVersion 忘了这里"变成测试期就炸。</summary>
    [Fact]
    public void VersionStampFollowsTheSingleApiWater()
    {
        Assert.Equal(20u, Fff3FpEngine.ConfigVersion);
        Assert.Equal(4, Marshal.OffsetOf<Fff3FpVideoSuperResolutionStatus>("Version").ToInt32());
    }

    /// <summary>解析内核头里的字段序列（原生名, 字节宽度）。两个 enum 字段按内核默认底层 int = 4 B；
    /// 其余形态一律抛。</summary>
    private static List<(string Name, int Size)> ParseNativeFields()
    {
        var header = Path.Combine(FindRepositoryRoot(), ShippingHeaderRelativePath);
        var lines = File.ReadAllLines(header);
        var start = Array.FindIndex(lines, l => l.Contains($"struct {NativeStructName}"));
        Assert.True(start >= 0, $"头文件里没有 struct {NativeStructName} ⇒ VSR 布局对账不能空跑");

        var fields = new List<(string, int)>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("};")) break;
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^([A-Za-z_][\w:]*)\s+(\w+)\s*(?:=[^;]*)?;\s*(?://.*)?$");
            if (!match.Success)
                throw new InvalidOperationException($"{NativeStructName} 里有无法解析的成员行：{trimmed}");

            var size = match.Groups[1].Value switch
            {
                "std::uint32_t" or "uint32_t" => 4,
                "std::uint64_t" or "uint64_t" => 8,
                "FFF3FPVideoSuperResolution" or "FFF3FPVideoSuperResolutionReason" => 4,
                _ => throw new InvalidOperationException(
                    $"{NativeStructName} 出现未登记的成员类型 {match.Groups[1].Value}（{trimmed}）")
            };
            fields.Add((match.Groups[2].Value, size));
        }

        Assert.True(fields.Count >= 11,
            $"{NativeStructName} 只解析出 {fields.Count} 个字段，解析器失效了");
        return fields;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ShippingHeaderRelativePath))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"找不到内核头文件 {ShippingHeaderRelativePath}（从 {AppContext.BaseDirectory} 上溯 12 层）。" +
            "布局镜像测试不允许静默跳过：要么它在测真东西，要么它判红。");
    }
}
