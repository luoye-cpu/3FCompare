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
            var result = JsonSerializer.Deserialize(json, JsonAotContext.Default.AppSettings);
            Console.Error.WriteLine($"[SettingsStore] Deserialized, FfmpegDirectory='{result?.FfmpegDirectory}'");
            return result ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsStore] Load FAILED: {ex.GetType().Name}: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = GetConfigPath();
            Console.Error.WriteLine($"[SettingsStore] Save to {path}");
            var json = JsonSerializer.Serialize(settings, JsonAotContext.Default.AppSettings);
            Console.Error.WriteLine($"[SettingsStore] Serialized {json.Length} chars");
            File.WriteAllText(path, json);
            Console.Error.WriteLine($"[SettingsStore] File written OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}