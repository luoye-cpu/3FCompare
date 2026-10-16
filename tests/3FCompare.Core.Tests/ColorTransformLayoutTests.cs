using System.Runtime.InteropServices;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// kind=3 外部色彩变换载荷（<c>FFF3FPColorTransformPayload</c>）的托管镜像对账。
///
/// <para>真源在 <c>3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h</c>。kind=3 这条线
/// （中性载体通道、外部色彩变换、帧边数据环）2026-10-13 已回填进出货树
/// <c>third_party/fff_project</c>，所以"托管镜像 vs 内核头"读的是内核工作树那一份，
/// 而"两棵树是不是同一份 ABI"由 <see cref="两棵内核树对kind3与边数据环给出同一份ABI"/> 单独判——
/// 出货 DLL 与宿主镜像分属两棵树，漂移不会编译报错，只会把像素读歪。</para>
///
/// <para>为什么不复用那个文件的通用解析器：本结构只有两种成员形态（4 个 uint32、
/// 2 个 float 数组），这里按 C 的对齐规则独立再算一遍，由"第二个实现"裁决布局——
/// 复用被测那侧的 helper 就等于没判。</para>
/// </summary>
public class ColorTransformLayoutTests
{
    private const string KernelHeaderRelativePath =
        @"3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h";

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, KernelHeaderRelativePath))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"找不到内核头文件 {KernelHeaderRelativePath}（从 {AppContext.BaseDirectory} 上溯 12 层）。" +
            "布局镜像测试不允许静默跳过：要么它在测真东西，要么它判红。");
    }

    private static string ReadKernelHeader() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), KernelHeaderRelativePath));

    /// <summary>取出 <c>struct NAME { ... }</c> 的体（剥注释、丢空行）。</summary>
    private static List<string> StructBody(string header, string structName)
    {
        var start = header.IndexOf("struct " + structName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"内核头里没有 struct {structName}");
        var open = header.IndexOf('{', start);
        var close = header.IndexOf("};", open, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"struct {structName} 没有闭合的花括号");
        var body = new List<string>();
        foreach (var raw in header[(open + 1)..close].Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length > 0) body.Add(line);
        }
        Assert.True(body.Count >= 1, $"struct {structName} 只解析出 {body.Count} 行，解析器失效了");
        return body;
    }

    /// <summary>把头里的数组维度（字面量，或 <c>a * b * c</c> 里混着 constexpr 符号）算成元素个数。</summary>
    private static int ArrayCount(string header, string expression)
    {
        var total = 1;
        foreach (var raw in expression.Split('*'))
        {
            var part = raw.Trim();
            if (int.TryParse(part, out var literal)) { total *= literal; continue; }
            // 符号形态：值回带头里取，不在测试里抄一份
            var match = System.Text.RegularExpressions.Regex.Match(
                header, @"constexpr\s+std::uint32_t\s+" +
                        System.Text.RegularExpressions.Regex.Escape(part) + @"\s*=\s*(\d+)\s*;");
            Assert.True(match.Success, $"数组维度里的 {part} 既不是字面量也不是头里的 constexpr");
            total *= int.Parse(match.Groups[1].Value);
        }
        return total;
    }

    private static string ManagedName(string cField) =>
        char.ToUpperInvariant(cField[0]) + cField[1..];

    /// <summary>
    /// 从任意一份内核头独立复算 <c>FFF3FPColorTransformPayload</c> 的字段表。
    /// 返回 (字段名, 起始偏移, 字节跨度) 与复算出的总尺寸。
    /// </summary>
    private static (List<(string Name, int Offset, int Bytes)> Fields, int Total)
        NativePayloadFields(string header)
    {
        var body = StructBody(header, "FFF3FPColorTransformPayload");

        // 独立复算：成员只有 float 与 std::uint32_t，都是 4 字节、4 对齐，
        // 于是布局就是顺序累加、无填充。认不出的成员行一律炸，不允许静默少测。
        // 头里长数组维度会换行（curveLut 的 res*res*res*3 跨了三行），所以先把
        // 声明按 ';' 切开、空白压平，再逐条认——按行认会把续行当成独立成员。
        var native = new List<(string Name, int Offset, int Bytes)>();
        var offset = 0;
        foreach (var rawDeclaration in string.Join(" ", body).Split(';'))
        {
            var line = System.Text.RegularExpressions.Regex.Replace(rawDeclaration.Trim(), @"\s+", " ");
            if (line.Length == 0) continue;
            var array = System.Text.RegularExpressions.Regex.Match(
                line, @"^(float|std::uint32_t|uint32_t)\s+(\w+)\[(.+)\]$");
            if (array.Success)
            {
                var count = ArrayCount(header, array.Groups[3].Value);
                native.Add((array.Groups[2].Value, offset, count * 4));
                offset += count * 4;
                continue;
            }
            var scalar = System.Text.RegularExpressions.Regex.Match(
                line, @"^(float|std::uint32_t|uint32_t)\s+(\w+)$");
            Assert.True(scalar.Success,
                $"解析 FFF3FPColorTransformPayload 时遇到认不出的成员声明：{line}");
            native.Add((scalar.Groups[2].Value, offset, 4));
            offset += 4;
        }
        return (native, offset);
    }

    /// <summary>头里钉死该结构尺寸的 static_assert 值。</summary>
    private static int PinnedPayloadSize(string header, string which)
    {
        var pinned = System.Text.RegularExpressions.Regex.Match(
            header, @"static_assert\(sizeof\(FFF3FPColorTransformPayload\)\s*==\s*(\d+)");
        Assert.True(pinned.Success, $"{which} 头里钉死 FFF3FPColorTransformPayload 尺寸的 static_assert 不见了");
        return int.Parse(pinned.Groups[1].Value);
    }

    [Fact]
    public void 色彩变换载荷镜像与内核头逐字段一致()
    {
        var header = ReadKernelHeader();
        var (native, offset) = NativePayloadFields(header);

        var managedType = typeof(Fff3FpColorTransformPayload);
        var managedTotal = Marshal.SizeOf(managedType);
        var mismatches = new List<string>();

        // 每个字段比"起始偏移"和"占用的字节跨度"。跨度由相邻字段的偏移差推出（末字段到总尺寸），
        // 这样定长内联数组也能量到——Marshal.SizeOf(fieldType) 对 float[] 是引用尺寸，量不出内联体。
        var managedOffsets = new List<(string Name, int Offset)>();
        foreach (var (name, _, _) in native)
        {
            var managedName = ManagedName(name);
            var field = managedType.GetField(managedName);
            if (field is null)
            {
                mismatches.Add($"原生 {name} 在托管侧没有对应字段（期望名 {managedName}）");
                continue;
            }
            managedOffsets.Add((managedName, Marshal.OffsetOf(managedType, managedName).ToInt32()));
        }
        for (var i = 0; i < native.Count && i < managedOffsets.Count; i++)
        {
            var (name, nativeOffset, nativeBytes) = native[i];
            var (managedName, managedOffset) = managedOffsets[i];
            if (managedOffset != nativeOffset)
                mismatches.Add($"{name}: 起始偏移 原生={nativeOffset} 托管={managedOffset}");
            var nativeEnd = i + 1 < native.Count ? native[i + 1].Offset : offset;
            var managedEnd = i + 1 < managedOffsets.Count ? managedOffsets[i + 1].Offset : managedTotal;
            if (nativeEnd - nativeOffset != managedEnd - managedOffset)
                mismatches.Add($"{name}: 字节跨度 原生={nativeEnd - nativeOffset} 托管={managedEnd - managedOffset}");
        }

        // 总尺寸三路对齐：独立复算、Marshal.SizeOf、以及头里那条钉死值。
        if (managedTotal != offset)
            mismatches.Add($"总尺寸 独立复算={offset} 托管 Marshal.SizeOf={managedTotal}");
        var pinnedValue = PinnedPayloadSize(header, "内核工作树");
        if (pinnedValue != offset)
            mismatches.Add($"钉死值与复算不符：static_assert={pinnedValue} 独立复算={offset}");
        if (managedTotal != pinnedValue)
            mismatches.Add($"托管镜像与钉死值不符：Marshal.SizeOf={managedTotal} static_assert={pinnedValue}");

        // 字段集合：托管侧多一个少一个都算不一致（把"悄悄加字段"这条路堵掉）。
        var managedNames = managedType.GetFields().Select(x => x.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expectedNames = native.Select(f => ManagedName(f.Name))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!managedNames.SequenceEqual(expectedNames))
            mismatches.Add($"字段集合不同：托管侧 [{string.Join(",", managedNames)}] " +
                           $"原生侧 [{string.Join(",", expectedNames)}]");

        Assert.True(mismatches.Count == 0,
            $"FFF3FPColorTransformPayload 托管镜像与内核头不一致：\n  {string.Join("\n  ", mismatches)}");
    }

    [Fact]
    public void 色彩变换LUT分辨率在两侧同为内核头那个常数()
    {
        var resolution = HeaderConstant(ReadKernelHeader(), "kColorTransformLutResolution");
        // 托管镜像把维度写在 MarshalAs 的 SizeConst 上：CurveLut 元素数必须等于 res³×3。
        var field = typeof(Fff3FpColorTransformPayload)
            .GetField(nameof(Fff3FpColorTransformPayload.CurveLut));
        Assert.True(field is not null, "托管侧没有 CurveLut 字段");
        var attribute = (MarshalAsAttribute?)field!
            .GetCustomAttributes(typeof(MarshalAsAttribute), false).SingleOrDefault();
        Assert.True(attribute is not null, "CurveLut 没有 MarshalAs 特性，就不是内联定长镜像");
        var elements = attribute!.SizeConst;
        Assert.True(elements == resolution * resolution * resolution * 3,
            $"CurveLut 元素数 托管={elements} 内核头={resolution}³×3={resolution * resolution * resolution * 3}");
    }

    /// <summary>头里的 <c>constexpr std::uint32_t NAME = 值;</c>。</summary>
    private static uint HeaderConstant(string header, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            header, @"constexpr\s+std::uint32_t\s+" + name + @"\s*=\s*(\d+)\s*;");
        Assert.True(match.Success, $"内核头里找不到 constexpr {name}，无法独立复算布局");
        return uint.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void kind3在两侧的枚举值同为三()
    {
        var header = ReadKernelHeader();
        var native = System.Text.RegularExpressions.Regex.Match(
            header, @"ExternalColorTransform\s*=\s*(\d+)");
        Assert.True(native.Success, "内核头里找不到 ExternalColorTransform 的取值");
        var value = Convert.ToUInt32(Fff3FpDynamicMetadataKind.ExternalColorTransform);
        Assert.True(uint.Parse(native.Groups[1].Value) == value,
            $"kind=3 两侧不同：内核={native.Groups[1].Value} 托管={value}");
    }

    [Fact]
    public void 结果码镜像与内核枚举逐项一致()
    {
        // FFFResult 在 FFF.Native.Api.h，托管侧是 FffResult。追加式枚举最容易漏抄：
        // 宿主拿到 -8 只会当成"某个失败"，而边数据环把 NotFound 当正常回答，漏了就分不开。
        var headerPath = Path.Combine(FindRepositoryRoot(),
            @"3fp\kernel\FFF.Native\3FR\Api\FFF.Native.Api.h");
        Assert.True(File.Exists(headerPath), $"找不到 {headerPath}");
        var header = File.ReadAllText(headerPath);
        var start = header.IndexOf("enum class FFFResult", StringComparison.Ordinal);
        var close = header.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && close > start, "FFF.Native.Api.h 里找不到 enum class FFFResult");
        var nativeValues = new Dictionary<string, int>();
        foreach (var raw in header[(start + "enum class FFFResult".Length)..close].Split('\n'))
        {
            // 成员行以逗号结尾（C++ 枚举分隔符），漏掉它会把九个成员解析成零个。
            var match = System.Text.RegularExpressions.Regex.Match(
                raw.Trim(), @"^(\w+)\s*=\s*(-?\d+),?$");
            if (match.Success) nativeValues[match.Groups[1].Value] = int.Parse(match.Groups[2].Value);
        }
        Assert.True(nativeValues.Count >= 9,
            $"FFFResult 只解析出 {nativeValues.Count} 个成员，解析器失效了（当前应有 9 个）");

        var managedValues = Enum.GetValues<FffResult>()
            .ToDictionary(v => v.ToString(), v => (int)v);
        var mismatches = nativeValues.Where(kv =>
                !managedValues.TryGetValue(kv.Key, out var mv) || mv != kv.Value)
            .Select(kv => $"{kv.Key}: 原生={kv.Value} " +
                          $"托管={(managedValues.TryGetValue(kv.Key, out var m) ? m.ToString() : "缺失")}")
            .ToList();
        mismatches.AddRange(managedValues.Keys.Where(n => !nativeValues.ContainsKey(n))
            .Select(n => $"托管侧多出 {n}（内核没有这个值）"));
        Assert.True(mismatches.Count == 0,
            $"结果码镜像与内核不一致：\n  {string.Join("\n  ", mismatches)}");
    }

    [Fact]
    public void 帧边数据环入口在托管侧存在且形参对齐()
    {
        // 只查签名形状；"这个导出在不在盘上 DLL 里"由
        // 3fc-followhand/tools/check_kernel_exports.py 的 REQUIRED 清单判（门禁那一头）。
        var method = typeof(Fff3FpNative).GetMethod("FFF3FP_CopyFrameSideDataAfter",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.True(method is not null, "托管侧没有 FFF3FP_CopyFrameSideDataAfter 声明");
        var parameters = method!.GetParameters();
        var expected = new[] { "IntPtr", "UInt32", "Int64", "IntPtr", "UInt32", "UInt32", "Int64" };
        var actual = parameters.Select(p => (p.ParameterType.IsByRef
            ? p.ParameterType.GetElementType()!.Name
            : p.ParameterType.Name)).ToArray();
        Assert.True(actual.SequenceEqual(expected),
            $"形参形态与内核声明不符：期望 [{string.Join(",", expected)}] 实际 [{string.Join(",", actual)}]");
        Assert.True(parameters[5].ParameterType.IsByRef && parameters[6].ParameterType.IsByRef,
            "writtenSize / foundFrameIndex 必须是出参（两趟式契约靠它们回报所需尺寸）");
    }

    /// <summary>上溯找文件，找不到就判红——不允许"这棵树不在"读成"两边一致"。</summary>
    private static string FindRepositoryFile(string relativePath, string which)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"找不到{which}（{relativePath}，从 {AppContext.BaseDirectory} 上溯 12 层）。" +
            "两棵树同水对账不许静默跳过：这一层要么在判东西，要么判红。");
    }

    /// <summary>把函数声明括号里的参数表压成"只看类型"的序列（丢掉参数名与 const/引用修饰的差异来源）。</summary>
    private static List<string> DeclarationParameterTypes(string header, string functionName)
    {
        var start = header.IndexOf(functionName + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"头里没有 {functionName} 的声明");
        var open = header.IndexOf('(', start + functionName.Length);
        var close = header.IndexOf(')', open);
        Assert.True(open >= 0 && close > open, $"{functionName} 的声明没闭合");
        var types = new List<string>();
        foreach (var raw in header[(open + 1)..close].Split(','))
        {
            var piece = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\s+", " ");
            if (piece.Length == 0) continue;
            // 去掉出参的 `*` 与尾随的参数名，只留类型词；`std::uint32_t` 这类带空格的类型保持完整。
            var type = System.Text.RegularExpressions.Regex.Replace(piece, @"[*&]", " ");
            type = System.Text.RegularExpressions.Regex.Replace(type, @"\s+\w+\s*$", "");
            types.Add(type.Trim());
        }
        Assert.True(types.Count >= 3, $"{functionName} 只解析出 {types.Count} 个参数，解析器失效了");
        return types;
    }

    [Fact]
    public void 两棵内核树对kind3与边数据环给出同一份ABI()
    {
        // 为什么要有这条：出货用的是 third_party/fff_project 那份内核，而托管镜像与其余
        // 测试钉的是 3fp/kernel 那份头。两棵树只要有一份悄悄漂移（多一个字段、少一个参数、
        // 枚举值不同），宿主按 A 布局写、DLL 按 B 布局读——不报错，直接把像素读歪。
        // 2026-10-13 kind=3 回填进出货树时就是靠这条把"字段逐条同水"变成常驻判据。
        var kernelHeader = ReadKernelHeader();
        var shippingHeader = File.ReadAllText(FindRepositoryFile(
            @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h", "出货树内核头"));

        var kernelFields = NativePayloadFields(kernelHeader);
        var shippingFields = NativePayloadFields(shippingHeader);
        var fieldDrift = new List<string>();
        if (kernelFields.Fields.Count != shippingFields.Fields.Count)
            fieldDrift.Add($"字段条数不同：内核树={kernelFields.Fields.Count} 出货树={shippingFields.Fields.Count}");
        for (var i = 0; i < Math.Min(kernelFields.Fields.Count, shippingFields.Fields.Count); i++)
        {
            var a = kernelFields.Fields[i];
            var b = shippingFields.Fields[i];
            if (a.Name != b.Name || a.Offset != b.Offset || a.Bytes != b.Bytes)
                fieldDrift.Add($"第 {i + 1} 个字段不同：内核树={a.Name}@{a.Offset}+{a.Bytes} " +
                               $"出货树={b.Name}@{b.Offset}+{b.Bytes}");
        }
        if (kernelFields.Total != shippingFields.Total)
            fieldDrift.Add($"复算总尺寸不同：内核树={kernelFields.Total} 出货树={shippingFields.Total}");
        var kernelPinned = PinnedPayloadSize(kernelHeader, "内核工作树");
        var shippingPinned = PinnedPayloadSize(shippingHeader, "出货树");
        if (kernelPinned != shippingPinned)
            fieldDrift.Add($"static_assert 钉死值不同：内核树={kernelPinned} 出货树={shippingPinned}");
        Assert.True(fieldDrift.Count == 0,
            $"FFF3FPColorTransformPayload 在两棵树里不同水（宿主会按一份布局读写另一份）：\n  " +
            string.Join("\n  ", fieldDrift));

        // kind 值与环入口签名：这两项决定了"同一个数字在两棵树上是不是同一件事"。
        var kernelKind = System.Text.RegularExpressions.Regex.Match(
            kernelHeader, @"ExternalColorTransform\s*=\s*(\d+)");
        var shippingKind = System.Text.RegularExpressions.Regex.Match(
            shippingHeader, @"ExternalColorTransform\s*=\s*(\d+)");
        Assert.True(kernelKind.Success && shippingKind.Success,
            "两棵树至少要有一份头里能读到 ExternalColorTransform 的取值，否则这条判据是空的");
        Assert.True(kernelKind.Groups[1].Value == shippingKind.Groups[1].Value,
            $"kind 值不同水：内核树={kernelKind.Groups[1].Value} 出货树={shippingKind.Groups[1].Value}");

        var kernelRing = DeclarationParameterTypes(kernelHeader, "FFF3FP_CopyFrameSideDataAfter");
        var shippingRing = DeclarationParameterTypes(shippingHeader, "FFF3FP_CopyFrameSideDataAfter");
        Assert.True(kernelRing.SequenceEqual(shippingRing),
            $"边数据环参数表不同水：内核树 [{string.Join(",", kernelRing)}] 出货树 [{string.Join(",", shippingRing)}]");

        // FFFResult 是两棵树各自的头（3FR/Api/FFF.Native.Api.h），NotFound=-8 这种追加值
        // 只要有一棵树没带上，宿主就会把"环里没有"读成"另一个失败码"。
        foreach (var (relative, which) in new[]
                 {
                     (@"3fp\kernel\FFF.Native\3FR\Api\FFF.Native.Api.h", "内核工作树"),
                     (@"third_party\fff_project\FFF.Native\3FR\Api\FFF.Native.Api.h", "出货树"),
                 })
        {
            var header = File.ReadAllText(FindRepositoryFile(relative, which));
            var match = System.Text.RegularExpressions.Regex.Match(header, @"NotFound\s*=\s*(-\d+)");
            Assert.True(match.Success, $"{which} 的 FFFResult 里没有 NotFound（边数据环靠它区分「没找到」和别的失败）");
            Assert.True(int.Parse(match.Groups[1].Value) == -8,
                $"{which} 的 NotFound 不是 -8：读到的值={match.Groups[1].Value}");
        }
    }
}
