using System.Text.Json;

namespace _3FCompare.Core.Backend;

/// <summary>
/// 3FCompare 动态元数据交换文件（JSON v1）的解析与校验——动态元数据注入通道的"载入侧"。
/// </summary>
/// <para>内核已有通用注入通道 <c>FFF3FP_SetDynamicMetadata</c>（托管封装
/// <see cref="IPlayerSession.SetDynamicMetadata"/>），缺的是"元数据从哪来"：本类把外部
/// JSON 文件解析成 <see cref="DynamicMetadataPacket"/> 列表，交给既有通道装载。</para>
///
/// <para><b>校验口径</b>：逐条对齐内核 <c>HdrProcessor.cpp</c> 的
/// <c>BuildInjectedWindow</c>(:496) / <c>BuildInjectedState</c>(:620) /
/// <c>BuildInjectedStateVivid</c>(:572) / <c>SetInjectedMetadataPackets</c>(:671)。
/// 目的不是"挑刺"，而是让<b>会被内核整批拒绝的表在载入时就带着可读的中文错误失败</b>，
/// 而不是送进内核后静默返回 false（UI 只看得到"设置失败"，且旧表续任）。</para>
///
/// <para><b>为什么是 public 而非 internal</b>：Core 只对 <c>3FCompare.Core.Tests</c>
/// 开了 InternalsVisibleTo（见 Testability.cs），UI 工程引用不到 internal；
/// 而"打开文件 → 载入"这条链路要由 UI 层调用本类。</para>
///
/// <para>文件形态（v1）：</para>
/// <code>
/// { "format":"3fc-dynamic-metadata", "version":1, "kind":"Hdr10Plus",
///   "entries":[ { "frameIndex":0, "targetedDisplayNits":1000,
///     "windows":[ { "upperLeft":[0,0], "lowerRight":[1,1], "maxScl":[.8,.75,.7],
///                   "averageMaxRgb":.5, "toneMappingFlag":1,
///                   "kneePoint":[.65,.85], "curveAnchors":[.85,.9,.95,1] } ] } ] }
/// </code>
/// <para>字段名不区分大小写；<c>targetedDisplayNits</c>/<c>fractionBrightPixels</c>/
/// <c>kneePoint</c>/<c>curveAnchors</c> 可省略（缺省 0/0/(0,0)/空数组）。</para>
public static class DynamicMetadataFile
{
    /// <summary>单文件可注入的条目上限（内核 kMaxInjectedEntries，HdrProcessor.cpp:33）。</summary>
    public const int MaxEntries = 4096;

    /// <summary>窗口数上限（内核 kHdrMaxProcessingWindows = 3，ST 2094-40/30 同规）。</summary>
    private const int MaxWindows = 3;

    /// <summary>曲线锚点上限（内核 kHdrMaxBezierAnchors = 15）。</summary>
    private const int MaxAnchors = 15;

    /// <summary>内核判断"kneePointY 骑到 kneePointX 之上"的容差（HdrProcessor.cpp:544 的 1e-4）。</summary>
    private const float KneeSlack = 1e-4f;

    /// <summary>格式标识（不区分大小写比对）。</summary>
    private const string FormatId = "3fc-dynamic-metadata";

    /// <summary>UTF-8 严格解码：非法字节直接抛，好让"非 UTF-8"落到"读文件失败"这条中文分支
    /// 而不是被替换字符悄悄带进 JSON 报错（那样用户看不出是编码问题）。</summary>
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>解析 3FCompare 动态元数据交换格式（JSON v1）。
    /// 失败时 packets 为 null 且 error 是一条可直接显示给用户、带字段路径的中文说明。</summary>
    /// <param name="json">文件内容（UTF-8 解码后的文本）。</param>
    /// <param name="packets">成功时为条目列表（保持文件中的原始顺序）；失败时为 null。</param>
    /// <param name="error">成功时为 null；失败时为带字段路径的中文说明。</param>
    public static bool TryParse(string json, out IReadOnlyList<DynamicMetadataPacket>? packets, out string? error)
    {
        packets = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "文件内容为空，不是合法的动态元数据文件";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            error = ParseRoot(doc.RootElement, out var parsed);
            if (error != null) return false;
            packets = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            // 语法错误不抛给 UI：文件路径已在调用方手里，这里只要给一条能显示的中文原因。
            error = $"不是合法的 JSON：{ex.Message}";
            return false;
        }
    }

    /// <summary>从文件读取后解析（等价于 File.ReadAllText + TryParse）。
    /// 读文件失败（不存在/无权限/非 UTF-8）也算解析失败，error 给中文原因，不抛异常给 UI。</summary>
    /// <param name="path">文件路径；null 属编程错误，抛 <see cref="ArgumentNullException"/>。</param>
    /// <param name="packets">同 <see cref="TryParse"/>。</param>
    /// <param name="error">同 <see cref="TryParse"/>。</param>
    public static bool TryLoad(string path, out IReadOnlyList<DynamicMetadataPacket>? packets, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);

        packets = null;
        string text;
        try
        {
            text = File.ReadAllText(path, StrictUtf8);
        }
        catch (Exception ex) when (ex is System.Text.DecoderFallbackException or IOException
                                      or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 不存在/无权限/路径非法/非 UTF-8 都收敛成"读不到"这一条理由。
            error = $"读取动态元数据文件失败：{ex.Message}";
            return false;
        }

        return TryParse(text, out packets, out error);
    }

    // ---------------- 文件级 ----------------

    /// <summary>解析根节点；返回 null 表示成功（<paramref name="packets"/> 即结果）。</summary>
    private static string? ParseRoot(JsonElement root, out List<DynamicMetadataPacket>? packets)
    {
        packets = null;

        if (root.ValueKind != JsonValueKind.Object)
            return "根节点不是 JSON 对象";

        // format：缺失与错值分开报，方便用户一眼看出是"忘了写"还是"写错了"。
        if (!TryProp(root, "format", out var formatElement))
            return $"缺少 format 标识（应为 \"{FormatId}\"）";
        if (formatElement.ValueKind != JsonValueKind.String ||
            !string.Equals(formatElement.GetString(), FormatId, StringComparison.OrdinalIgnoreCase))
            return $"format 标识必须为 \"{FormatId}\"（当前为 {Brief(formatElement)}）";

        if (!TryProp(root, "version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetDouble(out var version) || version != 1.0)
            return $"version 必须为 1（当前为 {Brief(versionElement)}），仅支持 version=1";

        if (!TryProp(root, "kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            return "kind 必须为 Hdr10Plus 或 HdrVivid";
        var kindText = kindElement.GetString() ?? string.Empty;
        DynamicMetadataKind kind;
        if (string.Equals(kindText, "Hdr10Plus", StringComparison.OrdinalIgnoreCase))
            kind = DynamicMetadataKind.Hdr10Plus;
        else if (string.Equals(kindText, "HdrVivid", StringComparison.OrdinalIgnoreCase))
            kind = DynamicMetadataKind.HdrVivid;
        else
            return $"kind 必须为 Hdr10Plus 或 HdrVivid（当前为 \"{kindText}\"）";

        if (!TryProp(root, "entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
            return "entries 必须是数组";
        var entryCount = entriesElement.GetArrayLength();
        if (entryCount == 0)
            return "entries 不能为空（至少需要 1 条）";
        if (entryCount > MaxEntries)
            return $"entries 条目数 {entryCount} 超过上限 {MaxEntries}";

        var list = new List<DynamicMetadataPacket>(entryCount);
        var index = 0;
        foreach (var entryElement in entriesElement.EnumerateArray())
        {
            string? failure;
            object payload;
            long frameIndex;
            if (kind == DynamicMetadataKind.Hdr10Plus)
            {
                failure = ParseHdr10PlusEntry(entryElement, index, out var entry);
                if (failure != null) return failure;
                frameIndex = entry!.FrameIndex;
                payload = entry;
            }
            else
            {
                failure = ParseVividEntry(entryElement, index, out var vivid);
                if (failure != null) return failure;
                frameIndex = ReadFrameIndexValue(entryElement);
                payload = vivid!;
            }

            // 顺序原样保留：内核 InstallInjectedTable（HdrProcessor.cpp:715）自己按
            // frameIndex 升序排序并在查找时取"最后一条 ≤ 当前帧"，所以顺序对语义无影响；
            // 但解析器再排一次就是多余的约定，且会让"文件改了哪里"不可追溯 ⇒ 不排序、不去重。
            list.Add(new DynamicMetadataPacket { Kind = kind, FrameIndex = frameIndex, Payload = payload });
            index++;
        }

        packets = list;
        return null;
    }

    // ---------------- HDR10+ 条目 ----------------

    private static string? ParseHdr10PlusEntry(JsonElement element, int index, out HdrDynamicMetadataEntry? entry)
    {
        entry = null;
        var path = $"entries[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
            return $"{path} 必须是 JSON 对象";

        var failure = ReadFrameIndex(element, path, out var frameIndex);
        if (failure != null) return failure;
        failure = ReadNits(element, path, out var nits);
        if (failure != null) return failure;

        if (!TryProp(element, "windows", out var windowsElement) || windowsElement.ValueKind != JsonValueKind.Array)
            return $"{path}.windows 必须是数组（1..{MaxWindows} 个窗口）";
        var windowCount = windowsElement.GetArrayLength();
        if (windowCount < 1 || windowCount > MaxWindows)
            return $"{path}.windows 数量必须为 1..{MaxWindows}（当前 {windowCount}）";

        var windows = new List<HdrDynamicMetadataWindow>(windowCount);
        var w = 0;
        foreach (var windowElement in windowsElement.EnumerateArray())
        {
            failure = ParseHdr10PlusWindow(windowElement, $"{path}.windows[{w}]", out var window);
            if (failure != null) return failure;
            windows.Add(window);
            w++;
        }

        entry = new HdrDynamicMetadataEntry
        {
            FrameIndex = frameIndex,
            Windows = windows,
            TargetedDisplayNits = nits,
        };
        return null;
    }

    private static string? ParseHdr10PlusWindow(JsonElement element, string path, out HdrDynamicMetadataWindow window)
    {
        window = new HdrDynamicMetadataWindow
        {
            UpperLeftX = 0, UpperLeftY = 0, LowerRightX = 0, LowerRightY = 0,
            MaxSclR = 0, MaxSclG = 0, MaxSclB = 0, AverageMaxRgb = 0,
            FractionBrightPixels = 0, ToneMappingFlag = 0, KneePointX = 0, KneePointY = 0,
            CurveAnchors = Array.Empty<float>(),
        };
        if (element.ValueKind != JsonValueKind.Object)
            return $"{path} 必须是 JSON 对象";

        // upperLeft / lowerRight：归一化画面坐标，有限性是内核的第一个闸门
        // （BuildInjectedWindow:500-505），NaN/±∞ 会让窗口选择逻辑失去意义。
        if (!TryProp(element, "upperLeft", out var upperLeftElement) ||
            !TryFixedNumbers(upperLeftElement, 2, out var upperLeft) ||
            !AllInRange(upperLeft, 0f, float.MaxValue))
            return $"{path}.upperLeft 必须是 2 个有限且 ≥ 0 的数";
        if (!TryProp(element, "lowerRight", out var lowerRightElement) ||
            !TryFixedNumbers(lowerRightElement, 2, out var lowerRight) ||
            !AllInRange(lowerRight, float.MinValue, 1f))
            return $"{path}.lowerRight 必须是 2 个有限且 ≤ 1 的数";
        // 退化/倒置矩形选不中任何像素，曲线就永远不会被走到（内核 :506-510 直接拒）。
        if (lowerRight[0] <= upperLeft[0] || lowerRight[1] <= upperLeft[1])
            return $"{path} 的矩形退化或倒置：lowerRight 必须严格大于 upperLeft";

        if (!TryProp(element, "maxScl", out var maxSclElement) ||
            !TryFixedNumbers(maxSclElement, 3, out var maxScl) || !AllInRange(maxScl, 0f, 1f))
            return $"{path}.maxScl 必须是 3 个位于 0..1 的数";
        if (!TryProp(element, "averageMaxRgb", out var averageElement) ||
            !TryFloatInRange(averageElement, 0f, 1f, out var averageMaxRgb))
            return $"{path}.averageMaxRgb 必须是位于 0..1 的有限数";

        // fractionBrightPixels 是**唯一按"宽容"口径处理**的字段，与内核一致：
        // BuildInjectedWindow（:527-529）只在值有限且落在 [0,1] 时才采用，越界/NaN 直接
        // 保持窗口默认 0，**不构成拒绝理由**。这不是漏掉的校验，别"顺手改成拒绝"。
        var fraction = 0f;
        if (TryProp(element, "fractionBrightPixels", out var fractionElement) &&
            TryFloatInRange(fractionElement, 0f, 1f, out var fractionValue))
            fraction = fractionValue;

        // toneMappingFlag 缺省按 0（只约束峰值、不提供曲线）处理：与"省略 kneePoint/curveAnchors"
        // 同一族宽容口径，且 flag=0 时内核本就不看曲线块（:534-539）。
        uint flag = 0;
        if (TryProp(element, "toneMappingFlag", out var flagElement))
        {
            if (flagElement.ValueKind != JsonValueKind.Number ||
                !flagElement.TryGetInt64(out var flagValue) || (flagValue != 0 && flagValue != 1))
                return $"{path}.toneMappingFlag 必须是 0 或 1（当前为 {Brief(flagElement)}）";
            flag = (uint)flagValue;
        }

        float kneeX = 0, kneeY = 0;
        float[] anchors = Array.Empty<float>();
        if (flag == 1)
        {
            // kneePoint：内核要求 0 ≤ x < 1、0 ≤ y ≤ 1，且 y 不得骑到 x 之上（:541-546）。
            if (!TryProp(element, "kneePoint", out var kneeElement) ||
                !TryFixedNumbers(kneeElement, 2, out var knee) || !AllFinite(knee) ||
                knee[0] < 0f || knee[0] >= 1f || knee[1] < 0f || knee[1] > 1f ||
                knee[1] > knee[0] + KneeSlack)
                return $"{path}.kneePoint 必须是 2 个有限数，且满足 0 ≤ kneePointX < 1、" +
                       $"0 ≤ kneePointY ≤ 1、kneePointY ≤ kneePointX + 1e-4";
            kneeX = knee[0];
            kneeY = knee[1];

            if (TryProp(element, "curveAnchors", out var anchorsElement))
            {
                if (anchorsElement.ValueKind != JsonValueKind.Array)
                    return $"{path}.curveAnchors 必须是数组（最多 {MaxAnchors} 个锚点）";
                if (!TryNumberList(anchorsElement, out anchors))
                    return $"{path}.curveAnchors 必须全部是数";
                if (anchors.Length > MaxAnchors)
                    return $"{path}.curveAnchors 长度 {anchors.Length} 超过上限 {MaxAnchors}";

                // ⚠ 内核的单调比较从 kneePointY 起步（HdrProcessor.cpp:552 的
                // `float previous = source.kneePointY;`）——也就是说**首锚点必须 ≥ kneePointY**，
                // 这条极易漏：只查"锚点之间单调不减"会放过首锚点低于拐点的表。
                var previous = kneeY;
                for (var i = 0; i < anchors.Length; i++)
                {
                    var anchor = anchors[i];
                    if (!float.IsFinite(anchor) || anchor < 0f || anchor > 1f)
                        return $"{path}.curveAnchors[{i}] 必须是位于 0..1 的有限数";
                    if (anchor < previous)
                        return i == 0
                            ? $"{path}.curveAnchors[0] 必须不小于 kneePointY（{kneeY}）"
                            : $"{path}.curveAnchors[{i}] 必须不小于前一个锚点（单调不减）";
                    previous = anchor;
                }
            }
            // 长度 0 允许：内核只是降级为 TooFewAnchors（:563-567），不算错。
        }
        // flag == 0：kneePoint/curveAnchors 即使给了也忽略（内核直接 return，不看曲线块）。

        window = new HdrDynamicMetadataWindow
        {
            UpperLeftX = upperLeft[0],
            UpperLeftY = upperLeft[1],
            LowerRightX = lowerRight[0],
            LowerRightY = lowerRight[1],
            MaxSclR = maxScl[0],
            MaxSclG = maxScl[1],
            MaxSclB = maxScl[2],
            AverageMaxRgb = averageMaxRgb,
            FractionBrightPixels = fraction,
            ToneMappingFlag = flag,
            KneePointX = kneeX,
            KneePointY = kneeY,
            CurveAnchors = anchors,
        };
        return null;
    }

    // ---------------- HDR Vivid 条目 ----------------

    private static string? ParseVividEntry(JsonElement element, int index, out HdrVividMetadataPayload? payload)
    {
        payload = null;
        var path = $"entries[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
            return $"{path} 必须是 JSON 对象";

        // 帧号在这里只校验（真正填进包的是 ParseRoot 读到的同一个值）。
        var failure = ReadFrameIndex(element, path, out _);
        if (failure != null) return failure;
        failure = ReadNits(element, path, out var nits);
        if (failure != null) return failure;

        if (!TryProp(element, "windows", out var windowsElement) || windowsElement.ValueKind != JsonValueKind.Array)
            return $"{path}.windows 必须是数组（1..{MaxWindows} 个窗口）";
        var windowCount = windowsElement.GetArrayLength();
        if (windowCount < 1 || windowCount > MaxWindows)
            return $"{path}.windows 数量必须为 1..{MaxWindows}（当前 {windowCount}）";

        var windows = new List<(float MaxR, float MaxG, float MaxB, float AverageMaxRgb)>(windowCount);
        var w = 0;
        foreach (var windowElement in windowsElement.EnumerateArray())
        {
            // Vivid 窗口不接受 upperLeft/lowerRight/kneePoint/curveAnchors：
            // 内核 BuildInjectedStateVivid（:602-608）把几何硬写成全幅 (0,0)-(1,1)，
            // 曲线块完全不解释（CUVA 005.1 曲线不公开）。因此这些字段出现时**忽略而不拒绝**——
            // 拒了只会让"多写几个字段"变成整表装载失败，而它们本来就不携带语义。
            var windowPath = $"{path}.windows[{w}]";
            if (windowElement.ValueKind != JsonValueKind.Object)
                return $"{windowPath} 必须是 JSON 对象";
            if (!TryProp(windowElement, "maxRgb", out var maxRgbElement) ||
                !TryFixedNumbers(maxRgbElement, 3, out var maxRgb) || !AllInRange(maxRgb, 0f, 1f))
                return $"{windowPath}.maxRgb 必须是 3 个位于 0..1 的数";
            if (!TryProp(windowElement, "averageMaxRgb", out var averageElement) ||
                !TryFloatInRange(averageElement, 0f, 1f, out var averageMaxRgb))
                return $"{windowPath}.averageMaxRgb 必须是位于 0..1 的有限数";

            windows.Add((maxRgb[0], maxRgb[1], maxRgb[2], averageMaxRgb));
            w++;
        }

        payload = new HdrVividMetadataPayload
        {
            Windows = windows,
            TargetedDisplayNits = nits,
        };
        return null;
    }

    // ---------------- 字段读取小工具 ----------------

    /// <summary>读取帧号；返回 null 表示成功。</summary>
    private static string? ReadFrameIndex(JsonElement element, string path, out long frameIndex)
    {
        frameIndex = 0;
        if (!TryProp(element, "frameIndex", out var value))
            return $"{path}.frameIndex 缺失，必须是 ≥ 0 的整数";
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out frameIndex) || frameIndex < 0)
            return $"{path}.frameIndex 必须是 ≥ 0 的整数（当前为 {Brief(value)}）";
        return null;
    }

    /// <summary>只取帧号数值（Vivid 分支已校验过，这里只为填包的 FrameIndex）。</summary>
    private static long ReadFrameIndexValue(JsonElement element)
        => TryProp(element, "frameIndex", out var value) && value.TryGetInt64(out var frameIndex) ? frameIndex : 0;

    /// <summary>读取目标峰值（可省略 → 0 = 由窗口峰值推导）；返回 null 表示成功。</summary>
    private static string? ReadNits(JsonElement element, string path, out float nits)
    {
        nits = 0f;
        if (!TryProp(element, "targetedDisplayNits", out var value)) return null;
        // 内核对 >0 的值 clamp 到 [1,10000]（:611-614 / :636-639），超界不拒；
        // 但负数与非有限数没有物理意义，载入期就挡掉。
        if (!TryFloatInRange(value, 0f, float.MaxValue, out nits))
            return $"{path}.targetedDisplayNits 必须是 ≥ 0 的有限数（0 = 由窗口峰值推导）";
        return null;
    }

    /// <summary>属性查找（不区分大小写）：<c>JsonElement.TryGetProperty</c> 是大小写敏感的，
    /// 而交换格式允许书写差异，故先精确匹配、再线性忽略大小写匹配。</summary>
    private static bool TryProp(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>读数字。非 Number 返回 false；超范围的字面量（如 1e999）给出 NaN，
    /// 由调用方的 <see cref="float.IsFinite(float)"/> 检查兜住（内核用 std::isfinite 同口径）。</summary>
    private static bool TryFloat(JsonElement element, out float value)
    {
        value = float.NaN;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetDouble(out var parsed)) value = (float)parsed;
        return true;
    }

    private static bool TryFloatInRange(JsonElement element, float min, float max)
        => TryFloat(element, out var value) && float.IsFinite(value) && value >= min && value <= max;

    private static bool TryFloatInRange(JsonElement element, float min, float max, out float value)
        => TryFloat(element, out value) && float.IsFinite(value) && value >= min && value <= max;

    /// <summary>读定长数字数组（只管形状：数组 + 长度 + 元素是数；数值范围留给调用方）。</summary>
    private static bool TryFixedNumbers(JsonElement element, int length, out float[] values)
    {
        values = Array.Empty<float>();
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != length) return false;
        var buffer = new float[length];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryFloat(item, out buffer[i])) return false;
            i++;
        }
        values = buffer;
        return true;
    }

    /// <summary>读任意长度的数字数组（曲线锚点用，长度另行校验）。</summary>
    private static bool TryNumberList(JsonElement element, out float[] values)
    {
        values = Array.Empty<float>();
        if (element.ValueKind != JsonValueKind.Array) return false;
        var buffer = new float[element.GetArrayLength()];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryFloat(item, out buffer[i])) return false;
            i++;
        }
        values = buffer;
        return true;
    }

    private static bool AllFinite(float[] values)
    {
        foreach (var value in values)
            if (!float.IsFinite(value)) return false;
        return true;
    }

    private static bool AllInRange(float[] values, float min, float max)
    {
        foreach (var value in values)
            if (!float.IsFinite(value) || value < min || value > max) return false;
        return true;
    }

    /// <summary>把出错的 JSON 片段缩短后写进错误文案（大数组/长字符串会把消息撑爆）。</summary>
    private static string Brief(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return "（缺失）";
        var raw = element.GetRawText();
        return raw.Length <= 24 ? raw : string.Concat(raw.AsSpan(0, 24), "…");
    }
}
