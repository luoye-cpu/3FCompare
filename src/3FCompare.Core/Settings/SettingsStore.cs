using System.Text.Json;
using System.Text.Json.Serialization;

namespace _3FCompare.Core.Settings;

/// <summary>应用设置持久化（JSON，NativeAOT 兼容：源生成上下文，无反射）。</summary>
public static class SettingsStore
{
    private static string GetConfigPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3FCompare");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, JsonAotContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = GetConfigPath();
            var json = JsonSerializer.Serialize(settings, JsonAotContext.Default.AppSettings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 写失败不致命（如只读目录），静默忽略
        }
    }
}