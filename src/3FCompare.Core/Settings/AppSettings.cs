namespace _3FCompare.Core.Settings;

/// <summary>应用设置（对应二级设置窗口 F25，序列化到 JSON）。</summary>
public sealed class AppSettings
{
    public bool HardwareDecode { get; set; } = true;

    /// <summary>默认解码 GPU（-1=系统默认）。</summary>
    public int PreferredAdapterIndex { get; set; } = -1;

    /// <summary>手动指定的 FFmpeg DLL 目录（null/空白 = 自动检测：FFMPEG_DIR → PATH → 应用目录）。</summary>
    public string? FfmpegDirectory { get; set; }

    public ColorModeSetting ColorMode { get; set; } = ColorModeSetting.MapToHdr;

    /// <summary>按帧步进步长（F12），默认 1。</summary>
    public int FrameStep { get; set; } = 1;

    /// <summary>按秒步进步长（F12），默认 1。</summary>
    public double SecondsStep { get; set; } = 1.0;

    public bool StartFullscreen { get; set; }

    public bool HideChromeInFullscreen { get; set; } = true;

    public int DefaultGridCols { get; set; } = 2;

    public int DefaultGridRows { get; set; } = 1;

    /// <summary>VRR 低延迟呈现（内核扩展，F27）：tearing=true 时 Present(0, ALLOW_TEARING)，
    /// 让 G-SYNC/FreeSync 显示器按自身节奏扫描输出。默认 false = VSync 锁定（无撕裂，
    /// 盯帧对比推荐）。显示器链不支持时自动回退 VSync。</summary>
    public bool VrrTearingPresent { get; set; }

    /// <summary>媒体率呈现节奏（内核扩展 A9）：pacing=true 时抑制叠加层固定周期重翻转，
    /// 使呈现节奏跟随源视频帧率。需 VrrTearingPresent=true 发挥完整效果。</summary>
    public bool VrrPacingEnabled { get; set; }

    /// <summary>窗口记忆：上次位置/尺寸/最大化状态（F27 窗口模式管理）。</summary>
    public int WindowX { get; set; } = -1;

    public int WindowY { get; set; } = -1;

    public int WindowWidth { get; set; } = 1600;

    public int WindowHeight { get; set; } = 900;

    public bool WindowMaximized { get; set; }

    /// <summary>界面语言（0=中文，1=英文）。</summary>
    public int Language { get; set; } = 0;
}

public enum ColorModeSetting
{
    MapToSdr = 0,
    RawHdrAsSdr = 1,
    MapToHdr = 2,
}