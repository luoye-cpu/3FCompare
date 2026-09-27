using System.Text.Json;

namespace _3FCompare.Core.Settings;

/// <summary>会话快照：保存各路文件、偏移、布局、当前帧、循环区间（F23），JSON 序列化。</summary>
public sealed class SessionSnapshot
{
    /// <summary>单路偏移的合理上限（±24 小时，100ns 单位 = 8.64e11）。
    /// 外部 .3fcs 里的 <see cref="SessionItem.Offset100ns"/> 是**未校验的 long**，
    /// 极端值（如 long.MinValue）会让"位置 − 期望"溢出抛 OverflowException，
    /// 把整个位置轮询打断（位置/时间码停更、界面形似卡死，docs/45 P1-9）。
    /// 加载侧负责钳制、比较侧负责过滤，两边都以此为准。</summary>
    public const long MaxOffset100ns = 24L * 3600 * 10_000_000;

    /// <summary>快照格式版本号。**默认必须给 0**：System.Text.Json 会先跑属性初始化器
    /// 再用 JSON 覆盖，没有该字段的旧 .3fcs 反序列化后就落在初始值上 ⇒
    /// 只有初始值是 0 时才能区分"旧格式（0）"与"新格式"。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }

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
    {
        Version = CurrentVersion; // 仅在落盘时定版：反序列化侧靠"0"识别旧格式
        return JsonSerializer.Serialize(this, JsonAotContext.Default.SessionSnapshot);
    }

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

    /// <summary>保存到文件（原子写）。
    /// 会话文件保存失败的代价是"上次会话整个丢失"，所以同样不能用
    /// 会先截断的 File.WriteAllText（docs/15 §4.1）。</summary>
    public static void SaveToFile(string path, SessionSnapshot snapshot)
        => AtomicFile.WriteAllText(path, snapshot.ToJson());

    public static SessionSnapshot? LoadFromFile(string path)
        => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
}