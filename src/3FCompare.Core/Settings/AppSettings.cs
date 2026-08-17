namespace _3FCompare.Core.Settings;

/// <summary>应用设置（对应二级设置窗口 F25，序列化到 JSON）。</summary>
public sealed class AppSettings
{
    public bool HardwareDecode { get; set; } = true;

    /// <summary>默认解码 GPU（-1=系统默认）。</summary>
    public int PreferredAdapterIndex { get; set; } = -1;

    public ColorModeSetting ColorMode { get; set; } = ColorModeSetting.MapToSdr;

    /// <summary>按帧步进步长（F12），默认 1。</summary>
    public int FrameStep { get; set; } = 1;

    /// <summary>按秒步进步长（F12），默认 1。</summary>
    public double SecondsStep { get; set; } = 1.0;

    public bool StartFullscreen { get; set; }

    public bool HideChromeInFullscreen { get; set; } = true;

    public int DefaultGridCols { get; set; } = 2;

    public int DefaultGridRows { get; set; } = 1;

    /// <summary>窗口记忆：上次位置/尺寸/最大化状态（F27 窗口模式管理）。</summary>
    public int WindowX { get; set; } = -1;

    public int WindowY { get; set; } = -1;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 800;

    public bool WindowMaximized { get; set; }
}

public enum ColorModeSetting
{
    MapToSdr = 0,
    RawHdrAsSdr = 1,
    MapToHdr = 2,
}