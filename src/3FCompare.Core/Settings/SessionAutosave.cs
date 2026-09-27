using System.Globalization;

namespace _3FCompare.Core.Settings;

/// <summary>
/// 崩溃自愈用的「上次会话」快照：只服务一个目的——进程被原生异常打死时，
/// 重启后能把用户正在看的东西找回来。
///
/// <para><b>为什么必须"边看边写"而不是"退出时写"</b>：这是本类存在的全部理由。
/// 原生访问违规 / 非法指令（<c>0xC0000005</c> / <c>0xC000001D</c>）是<b>即时终止</b>：
/// 不跑 <c>finally</c>、不跑 <c>AppDomain.ProcessExit</c>、不跑 <c>ApplicationExit</c>。
/// 任何"关闭时保存"的钩子在崩溃面前都等于不存在。所以保存必须发生在<b>崩溃之前</b>，
/// 而且要高频、廉价、写完即落盘。</para>
///
/// <para><b>为什么单独一个文件、不复用 settings.json</b>：两者生命周期相反。
/// 设置是"用户显式改了才写、写错要能回滚"；本快照是"每几秒无条件覆写、
/// 正常退出就删掉"。塞进同一个文件会让设置被高频重写拖累，也会让"正常退出"
/// 多出一次必须成功的删除操作。分开后，各自的成功条件都变简单。</para>
///
/// <para><b>与手动"保存会话"的关系</b>：共用 <see cref="SessionSnapshot"/> 这一份结构，
/// 所以崩溃恢复和用户显式保存/加载走的是<b>同一条</b>重建路径
/// （<c>LoadSessionSnapshot</c>），不会出现"恢复出来的布局和手动加载的不一样"。</para>
/// </summary>
public static class SessionAutosave
{
    /// <summary>快照文件名（放在配置目录，与 settings.json 同级）。</summary>
    public const string FileName = "last-session.3fcs";

    /// <summary>隔离后缀：判定为"反复崩溃的元凶"时改名成这个名字，不再被自动恢复。</summary>
    public const string QuarantineExtension = ".bad";

    /// <summary>超过这个年龄的快照视为陈旧，不再恢复。
    ///
    /// <para>作用是兜底：正常退出时本文件会被删掉，理论上不会残留。
    /// 但"进程被强杀 / 断电 / 删除失败"都会让它留下；隔了很久再启动，
    /// 用户早已不记得崩溃这回事，突然弹出一周前的 8 路 4K 只会造成困惑。</para></summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static string? _lastWritten;

    /// <summary>测试专用路径重定向（仿 <see cref="Diagnostics.AppLog"/> 的 <c>ResetForTests</c> 既有模式）。
    ///
    /// <para><b>为什么需要</b>：<see cref="FilePath"/> 默认落在真实配置目录，
    /// 若测试直接写它，跑一次测试就会在用户配置目录里留下一份快照，
    /// 下一次真实启动会"恢复"出一批测试素材——正是本类要防的那类污染。
    /// 所以测试必须能把路径指到临时目录。</para></summary>
    internal static string? OverridePath { get; set; }

    /// <summary>测试夹具：重定向路径并清空"内容未变则跳过写入"的缓存。
    /// 传 null 恢复默认路径。必须每个用例 <c>try/finally</c> 还原，否则污染同集合的其它用例。</summary>
    internal static void ResetForTests(string? path)
    {
        OverridePath = path;
        _lastWritten = null;
    }

    /// <summary>快照完整路径。</summary>
    public static string FilePath => OverridePath ?? Path.Combine(SettingsStore.ConfigDirectory, FileName);

    /// <summary>是否存在可恢复的快照（存在且未过期）。</summary>
    public static bool HasRecoverable()
    {
        var path = FilePath;
        if (!File.Exists(path)) return false;
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= MaxAge;
        }
        catch
        {
            // 拿不到时间戳（文件被占用 / 权限）：宁可不恢复，也不要拿一份不知来历的状态
            return false;
        }
    }

    /// <summary>写入快照。原子写（临时文件 → fsync → 移动），
    /// 写到一半被杀时磁盘上仍是上一份完整快照。
    ///
    /// <para>内容未变化时跳过物理写入：播放位置每帧都在动，但序列化结果按秒级精度
    /// 可能连续多次相同，没必要每秒打一次磁盘（<see cref="AtomicFile"/> 每次都 fsync）。
    /// 缓存只在进程内有效，重启后必然写一次，不影响正确性。</para></summary>
    public static void Write(SessionSnapshot snapshot)
    {
        var json = snapshot.ToJson();
        if (string.Equals(json, _lastWritten, StringComparison.Ordinal)) return;
        AtomicFile.WriteAllText(FilePath, json);
        _lastWritten = json;
    }

    /// <summary>读取快照；不存在 / 过期 / 解析失败一律返回 null（fail-closed）。</summary>
    public static SessionSnapshot? Read()
    {
        if (!HasRecoverable()) return null;
        try
        {
            var snapshot = SessionSnapshot.FromJson(File.ReadAllText(FilePath));
            // 空会话没有恢复价值，还会让界面进入"什么都没打开但提示已恢复"的怪状态
            return snapshot is { Items.Count: > 0 } ? snapshot : null;
        }
        catch (Exception ex)
        {
            // 返回 null 是对的（一份读坏的自动存档不该把启动带崩），但**静默**是错的：
            // 用户看到的是"上次没关的会话怎么没恢复"，而日志里一个字都没有，
            // 事后无从区分"文件不存在 / 内容损坏 / 反序列化抛了 / 权限读不到"。
            _3FCompare.Core.Diagnostics.AppLog.Warn("Session",
                $"自动存档读取失败，本次不恢复：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>删除快照（正常退出时调用）。
    /// 失败不影响退出——删不掉最坏结果是下次启动时多问一次，不是数据损坏。</summary>
    public static void Clear()
    {
        _lastWritten = null;
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略：见本方法说明
        }
    }

    /// <summary>隔离快照：改名保留，且不再参与自动恢复。
    ///
    /// <para><b>为什么必须隔离而不是留着</b>：如果崩溃是"打开某组文件就必崩"，
    /// 那么每次自愈重启都会重新打开这组文件、再次崩溃，形成自激循环。
    /// 隔离后下次启动是干净的，用户还能手动把 <c>.bad</c> 改回来取回内容
    /// （所以是改名，不是删除）。</para></summary>
    public static bool Quarantine()
    {
        _lastWritten = null;
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return false;
            var target = path + QuarantineExtension;
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
            return true;
        }
        catch (Exception ex)
        {
            // 隔离失败 ⇒ 下次启动还会去读同一份坏文件、再失败一次。留一行才知道在循环什么。
            _3FCompare.Core.Diagnostics.AppLog.Warn("Session",
                $"隔离自动存档失败（下次仍会尝试读取它）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>供日志/诊断用的简短描述（不含完整路径，避免把目录结构写进多用户可读的日志）。</summary>
    public static string Describe()
    {
        var path = FilePath;
        if (!File.Exists(path)) return $"{FileName} (无)";
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return string.Format(CultureInfo.InvariantCulture,
                "{0} (存在, {1:F0} 秒前{2})", FileName, age.TotalSeconds,
                age <= MaxAge ? "" : ", 已过期");
        }
        catch
        {
            return $"{FileName} (存在)";
        }
    }
}
