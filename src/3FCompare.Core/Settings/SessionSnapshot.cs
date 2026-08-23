using System.Text.Json;

namespace _3FCompare.Core.Settings;

/// <summary>会话快照：保存各路文件、偏移、布局、当前帧、循环区间（F23），JSON 序列化。</summary>
public sealed class SessionSnapshot
{
    public List<SessionItem> Items { get; set; } = new();

    public int GridLayout { get; set; } = 1; // 0=自动, 1=单屏, 2=2x2, 3=3x3

    public long Position100ns { get; set; }

    public bool LoopEnabled { get; set; }

    public long LoopStart100ns { get; set; } = -1;

    public long LoopEnd100ns { get; set; } = -1;

    public sealed class SessionItem
    {
        public string? Path { get; set; }
        public long Offset100ns { get; set; }
        public bool HardwareDecode { get; set; } = true;
        public int AdapterIndex { get; set; } = -1;
    }

    public string ToJson()
        => JsonSerializer.Serialize(this, JsonAotContext.Default.SessionSnapshot);

    public static SessionSnapshot? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, JsonAotContext.Default.SessionSnapshot);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveToFile(string path, SessionSnapshot snapshot)
        => File.WriteAllText(path, snapshot.ToJson());

    public static SessionSnapshot? LoadFromFile(string path)
        => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
}