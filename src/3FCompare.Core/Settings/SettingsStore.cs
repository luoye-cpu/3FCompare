using System.Text.Json;
using System.Text.Json.Serialization;

namespace _3FCompare.Core.Settings;

/// <summary>应用设置持久化（JSON，NativeAOT 兼容：源生成上下文，无反射）。
/// 配置文件保存在应用目录（与 exe 同目录），便于便携部署。</summary>
public static class SettingsStore
{
    private static string GetConfigPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetConfigPath();
            Console.Error.WriteLine($"[SettingsStore] Load from {path}");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"[SettingsStore] File not found, returning defaults");
                return new AppSettings();
            }
            var json = File.ReadAllText(path);
            Console.Error.WriteLine($"[SettingsStore] Read {json.Length} chars");
            var result = JsonSerializer.Deserialize(json, JsonAotContext.Default.AppSettings) ?? new AppSettings();
            MigrateLegacy(result, json);
            // 收敛越界值：合法 JSON 里的 FrameStep=int.MaxValue / ColorMode=99 之类
            // 不会被反序列化拦下，会一路传到内核（docs/15 §4.3）
            result.Normalize();
            Console.Error.WriteLine($"[SettingsStore] Deserialized, FfmpegDirectory='{result.FfmpegDirectory}'");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsStore] Load FAILED: {ex.GetType().Name}: {ex.Message}");
            return new AppSettings();
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
                Console.Error.WriteLine($"[SettingsStore] WindowMaximized migration skipped: {ex.GetType().Name}");
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = GetConfigPath();
            Console.Error.WriteLine($"[SettingsStore] Save to {path}");
            // 写入时盖上当前结构版本：读回时据此判断是否需要迁移
            settings.Version = AppSettings.CurrentVersion;
            var json = JsonSerializer.Serialize(settings, JsonAotContext.Default.AppSettings);
            Console.Error.WriteLine($"[SettingsStore] Serialized {json.Length} chars");
            // 原子写（tmp + fsync + Move）：File.WriteAllText 会先截断目标文件，
            // 写入失败就把 settings.json 变成半截 JSON ⇒ 静默回退默认（docs/15 §4.1）
            AtomicFile.WriteAllText(path, json);
            Console.Error.WriteLine($"[SettingsStore] File written OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}