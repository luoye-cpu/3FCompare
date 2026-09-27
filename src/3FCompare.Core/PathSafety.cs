namespace _3FCompare.Core;

/// <summary>路径安全判据（Core 与 UI 共用的唯一真源）。
/// <para>放在 Core 而不是 UI：Core 是 net11.0、可被 UI 工程引用，反向引用不成立
/// （UI 是 net11.0-windows，Core 引用不到），所以共享判据只能落在 Core 侧。</para></summary>
public static class PathSafety
{
    /// <summary>是否为 UNC（网络）路径，或任何<b>无法安全归一化</b>的不可信路径。
    ///
    /// <para><b>为什么必须先 <see cref="Path.GetFullPath(string)"/> 再判</b>：
    /// .NET 里 <c>//server/share/x</c> 与 <c>\\server\share\x</c> 都被
    /// <see cref="Path.IsPathRooted(string)"/> 视为 rooted，但只有后者<b>以</b> <c>\\</c> 开头 ——
    /// 于是 <c>path.StartsWith(@"\\")</c> 这种"只看前缀"的判据会被 <c>//</c> 写法整体绕过，
    /// 而 Windows 访问该路径时会自动发起 NTLM 认证、把本机凭据送到对方服务器。
    /// <c>GetFullPath</c> 把 <c>//</c> 与 <c>\\</c> 归一成同一种形式（<c>\\server\share\x</c>），
    /// 归一化后形式唯一，判定才可靠。</para>
    ///
    /// <para><b>fail-closed</b>：空白/空串，以及 <c>GetFullPath</c> 自身抛异常
    /// （非法字符如 <c>\0</c>、路径超长、无效盘符等）的情况一律返回 <c>true</c>
    /// （视为不可信、调用方应拒绝）。<b>绝不能</b>吞掉异常后当成合法本地路径 ——
    /// 那正好是攻击者想要的"绕过校验、继续往下走"。</para>
    ///
    /// <para>返回 <c>true</c> 的情形含<b>三类互不相同的原因</b>，调用方的提示文案必须分开给：
    /// ① UNC（<c>\\server\share\…</c>）与设备命名空间（<c>\\?\…</c> 与 <c>//?/…</c> 归一后
    /// 同为 <c>\\?\…</c>，会跳过常规路径规范化）—— 这两类才是"网络/设备路径"；
    /// ② 空/空白串 —— 是<b>非法输入</b>，没有网络语义，说成"不接受 UNC 路径"会把排查方向带偏
    /// （调用方应先自行判空并给独立文案，如 <c>NativeRuntime.IsAcceptableFfmpegDirectory</c>）；
    /// ③ <c>GetFullPath</c> 归一化失败的串（非法字符、超长、无效盘符）。
    /// 相对路径归一化后是本地绝对路径 ⇒ 返回 <c>false</c>；"拒绝相对路径"由调用点的
    /// <see cref="Path.IsPathRooted(string)"/> 负责，两者语义不重叠。</para></summary>
    public static bool IsUncPath(string? path)
    {
        // 空/空白：无可信内容 ⇒ 拒绝（fail-closed）。注意这**不是**"UNC 路径"，
        // 只是与 UNC 共用同一个 fail-closed 返回值；调用方（见 NativeRuntime）会先单独判空，
        // 以免把"路径为空"报成"不接受 UNC/设备路径"这种与真实原因无关的提示。
        if (string.IsNullOrWhiteSpace(path)) return true; // 空/空白：无可信内容
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch
        {
            // 非法字符 / 超长 / 无效盘符：无法归一化 ⇒ 按不可信处理（fail-closed）
            return true;
        }
        return full.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
