using System.Text.Json;
using System.Text.Json.Serialization;

namespace _3FCompare.Core.Settings;

/// <summary>应用设置持久化（JSON，NativeAOT 兼容：源生成上下文，无反射）。
///
/// 配置文件**优先**保存在应用目录（与 exe 同目录），便于便携部署；
/// 只有该目录确实不可写时才回退到 <c>%LOCALAPPDATA%\3FCompare\</c>。
/// ⚠ 便携模式是本项目的明确设计目标，**绝不能**无条件迁到 %APPDATA%
/// （那样 U 盘版每次换机器都丢配置）。</summary>
public static class SettingsStore
{
    private static readonly object PathLock = new();
    private static string? _resolvedConfigPath;

    /// <summary>最近一次 <see cref="Save"/> 失败的原因（null = 还没失败过 / 最后一次成功）。
    ///
    /// 保存失败原先被 <c>catch { Console.Error.WriteLine(...) }</c> 静默吞掉：
    /// 用户以为"设置改好了"，重启后全部回退，且日志里只有一行没人看的 stderr（B1）。
    /// Core 层不碰 UI，所以这里只负责把事实留下来，将来 UI 可据此提示。</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>配置文件完整路径（首次访问时解析并缓存，之后不再变）。</summary>
    private static string GetConfigPath()
    {
        var cached = _resolvedConfigPath;
        if (cached is not null) return cached;
        lock (PathLock)
        {
            return _resolvedConfigPath ??= ResolveConfigPath();
        }
    }

    /// <summary>先按 exe 同目录（便携）试，不可写才回退 LocalApplicationData。</summary>
    private static string ResolveConfigPath()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "settings.json");

        // 便携优先：装在 U 盘 / 免安装目录 / 自己挑的目录时，配置就该跟着 exe 走。
        if (CanWriteTo(AppContext.BaseDirectory)) return portable;

        // 走到这里说明 exe 目录不可写：Program Files、只读介质、
        // 或当前用户对该共享目录没有写权限。此时"设置能不能存下来"比"存哪儿"重要，
        // 回退到当前用户专属目录，至少保证功能成立。
        var fallbackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3FCompare");
        if (CanWriteTo(fallbackDir))
        {
            // ⚠ 日志只记文件名：完整路径含用户名与目录结构，
            // 而日志文件本身落在多用户可读的目录里（B1）
            Diagnostics.AppLog.Warn("SettingsStore",
                $"应用目录不可写，配置改用用户目录 {Path.GetFileName(fallbackDir)}");
            return Path.Combine(fallbackDir, "settings.json");
        }

        // 两边都写不进去：仍返回便携路径，让后续 Save 失败时给出明确错误
        Diagnostics.AppLog.Warn("SettingsStore", "应用目录与用户目录均不可写，配置将无法保存");
        return portable;
    }

    /// <summary>写探针：真的建一个临时文件再删掉。
    ///
    /// 为什么不能用"目录是否存在"或 <c>Directory.GetAccessControl</c> 判断：
    /// 目录存在 ≠ 可写（Program Files 就存在），ACL 解析又慢又跨平台不一致。
    /// 直接写一次是唯一可靠的判据。<c>FileOptions.DeleteOnClose</c> 保证
    /// 即使中途抛异常也会由 OS 关掉句柄时删除，不会在 exe 目录留下垃圾。</summary>
    private static bool CanWriteTo(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;

        string probe;
        try
        {
            probe = Path.Combine(directory,
                "3fcompare_probe_" + Guid.NewGuid().ToString("N") + AtomicFile.TempFileExtension);
        }
        catch { return false; }

        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                                           FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    public static AppSettings Load()
    {
        AppSettings? result = null;
        try
        {
            var path = GetConfigPath();
            var dir = Path.GetDirectoryName(path);

            // 崩溃残留的 tmp（写一半被杀）不会自己消失，启动时顺手清一次（B4）
            if (!string.IsNullOrEmpty(dir))
            {
                try { AtomicFile.PurgeStaleTempFiles(dir); } catch { }
            }

            // 日志只用文件名：完整路径会把用户名和目录结构写进落盘日志（B1）
            var name = Path.GetFileName(path);
            Diagnostics.AppLog.Debug("SettingsStore", $"加载配置 {name}");

            if (!File.Exists(path))
            {
                // 首次运行是**正常路径**，不能记 Warn（否则每个新用户都被一条假告警吓到）
                Diagnostics.AppLog.Debug("SettingsStore", $"{name} 不存在，使用默认设置");
                return Defaults();
            }

            result = TryLoadFile(path);
            if (result is null)
            {
                // 主文件坏了 ⇒ 先试 .bak，再回默认。
                // 顺序不能反：直接回默认的话，下一次 Save 就把备份也覆盖成默认值，
                // 用户攒了很久的窗口布局/FFmpeg 目录就再也回不来了（B2）。
                var backup = path + AtomicFile.BackupFileExtension;
                result = File.Exists(backup) ? TryLoadFile(backup) : null;
                if (result is not null)
                {
                    Diagnostics.AppLog.Warn("SettingsStore",
                        $"主配置不可用，已回退备份 {Path.GetFileName(backup)}");
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("SettingsStore", $"加载配置失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (result is null)
        {
            Diagnostics.AppLog.Warn("SettingsStore", "配置不可用，回退出厂设置");
            return Defaults();
        }
        return result;
    }

    /// <summary>出厂设置。默认值本就在合法区间，仍走一遍 Normalize
    /// 以防将来新增的字段自带越界默认值。</summary>
    private static AppSettings Defaults()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }

    /// <summary>读单个配置文件：成功返回设置，任何异常返回 null（原因已记日志）。</summary>
    private static AppSettings? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Diagnostics.AppLog.Debug("SettingsStore", $"{Path.GetFileName(path)} 不存在");
                return null;
            }

            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize(json, JsonAotContext.Default.AppSettings)
                    ?? new AppSettings();

            // ⚠ 必须在迁移 / 收敛**之前**看原始版本号：
            // MigrateLegacy 会把 legacy 的版本号改写成 CurrentVersion，之后就分不清了。
            // 来自更新版的文件（Version > CurrentVersion）不会被任何迁移分支处理，
            // 旧代码按旧语义读新结构，Save 时再把高版本号悄悄降级——必须留痕（B1）。
            if (s.Version > AppSettings.CurrentVersion)
            {
                Diagnostics.AppLog.Warn("SettingsStore",
                    $"配置版本 {s.Version} 高于本程序支持的 {AppSettings.CurrentVersion}" +
                    "（可能来自更新版本），将按当前结构重新收敛");
            }

            MigrateLegacy(s, json);
            // 收敛越界值：合法 JSON 里的 FrameStep=int.MaxValue / ColorMode=99 之类
            // 不会被反序列化拦下，会一路传到内核（docs/15 §4.3）
            s.Normalize();

            Diagnostics.AppLog.Debug("SettingsStore",
                $"{Path.GetFileName(path)} 读取成功，FfmpegDirectory='{s.FfmpegDirectory}'");
            return s;
        }
        catch (Exception ex)
        {
            // 只记文件名 + 异常类型：message 里常带完整路径（含用户名），
            // 落盘日志在多用户机器上是全局可读的（B1）
            Diagnostics.AppLog.Warn("SettingsStore",
                $"解析 {Path.GetFileName(path)} 失败：{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>把旧版 settings.json 的窗口几何写法迁移到新语义（可用性 P0-2 配套）。
    /// 旧格式：WindowX/Y 用 -1 哨兵表示"未设置"，WindowWidth/Height 为非空 int，
    /// 最大化状态另存布尔量 WindowMaximized。
    /// 新格式：坐标/尺寸/状态均为可空类型，null 才表示"未设置"。
    ///
    /// 两点必须迁移，不能只靠反序列化：
    /// ① -1 在新语义下是**合法坐标**（副显示器位于主屏左侧时坐标为负），
    ///    旧配置的 -1 若原样保留，会被当成真实坐标去恢复，窗口可能跑到屏幕外；
    /// ② WindowMaximized 已从 AppSettings 移除，源生成 JSON 会静默忽略未映射成员，
    ///    只能从原始 JSON 文本里单独读，否则老用户的最大化状态会丢。
    /// </summary>
    public static void MigrateLegacy(AppSettings s, string json)
    {
        // 只有**老文件**（缺 Version 字段 → 反序列化为 0）才做迁移。
        // 无条件执行的后果：新语义下 -1 是**合法坐标**（副显示器在主屏左侧时坐标为负），
        // 每次启动都被当成哨兵清成 null ⇒ 用户把窗口停在负坐标上，位置每次都丢
        //（docs/15 §4.2）。
        var isLegacy = s.Version < AppSettings.CurrentVersion;

        // ① -1 哨兵 → null（尺寸同理：非正值一律视为无效）
        if (isLegacy)
        {
            if (s.WindowX == -1) s.WindowX = null;
            if (s.WindowY == -1) s.WindowY = null;
            if (s.WindowWidth is <= 0) s.WindowWidth = null;
            if (s.WindowHeight is <= 0) s.WindowHeight = null;
            s.Version = AppSettings.CurrentVersion;

            // ② WindowMaximized（已移除字段）→ WindowState
            // 这是老格式独有的字段，同样只在 legacy 下有意义。
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("WindowMaximized", out var maximized)
                    && maximized.ValueKind == JsonValueKind.True
                    && s.WindowState is null)
                {
                    s.WindowState = 2; // Avalonia WindowState.Maximized（0=Normal 1=Minimized 2=Maximized）
                }
            }
            catch (Exception ex)
            {
                // 迁移失败不影响启动，退化成"无几何记忆"
                Diagnostics.AppLog.Warn("SettingsStore",
                    $"WindowMaximized 迁移跳过：{ex.GetType().Name}");
            }
        }
    }

    /// <summary>保存设置。</summary>
    /// <returns>是否成功。失败原因见 <see cref="LastSaveError"/>。
    ///
    /// 为什么改成返回 bool 而不是继续 void + 吞异常：
    /// 保存失败的代价是"用户以为改了、重启全丢"，静默吞掉等于把故障完全藏起来（B1）。
    /// 返回值不改变既有调用点的写法（<c>SettingsStore.Save(x);</c> 仍然合法），
    /// 想检查的调用方可以检查。</returns>
    public static bool Save(AppSettings settings)
    {
        try
        {
            var path = GetConfigPath();
            // 写入时盖上当前结构版本：读回时据此判断是否需要迁移
            settings.Version = AppSettings.CurrentVersion;
            var json = JsonSerializer.Serialize(settings, JsonAotContext.Default.AppSettings);
            // 原子写（tmp + fsync + Move）：File.WriteAllText 会先截断目标文件，
            // 写入失败就把 settings.json 变成半截 JSON ⇒ 静默回退默认（docs/15 §4.1）。
            // createBackup：留一份上一版，供下次 Load 在主文件损坏时回退（B2）。
            AtomicFile.WriteAllText(path, json, writeBom: false, createBackup: true);

            LastSaveError = null;
            Diagnostics.AppLog.Debug("SettingsStore",
                $"配置已写入 {Path.GetFileName(path)}（{json.Length} 字符）");
            return true;
        }
        catch (Exception ex)
        {
            // 完整异常信息留在内存里的 LastSaveError（将来给 UI 用），
            // 落盘日志只记类型名——ex.Message 常含完整路径，会泄用户名（B1）。
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            Diagnostics.AppLog.Warn("SettingsStore", $"保存配置失败：{ex.GetType().Name}");
            return false;
        }
    }
}
