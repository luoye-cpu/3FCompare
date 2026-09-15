namespace _3FCompare.Core.Settings;

/// <summary>应用设置（对应二级设置窗口 F25，序列化到 JSON）。</summary>
public sealed class AppSettings
{
    /// <summary>设置结构版本号。**没有它就无法区分"老格式"与"新格式"**，
    /// 迁移逻辑只能无条件执行——例如把 -1 哨兵当坐标迁移走，
    /// 而 -1 在新语义下是合法坐标（副屏在主屏左侧），结果用户每次启动都丢窗口位置
    ///（docs/15 §4.2）。老文件缺该字段时反序列化为 0，据此判定为 legacy。
    ///
    /// 升级设置结构时：① 递增此常量；② 在 SettingsStore.MigrateLegacy 里
    /// 增加对应分支，并只在旧版本号下执行。</summary>
    public const int CurrentVersion = 1;

    /// <summary>写入本文件的结构版本。
    ///
    /// ⚠ **默认值必须是 0 而不是 CurrentVersion**：System.Text.Json 反序列化时会先跑
    /// 属性初始化器，再覆盖 JSON 里存在的属性。若默认给 CurrentVersion，
    /// 那么"JSON 里根本没有 Version 字段"的老文件也会被赋成当前版本，
    /// 迁移判定就永远认为它不是 legacy——版本号形同虚设（本机单测当场验出）。
    ///
    /// 0 或缺失 = 引入本字段之前的旧文件；写出时由 SettingsStore.Save 置为
    /// <see cref="CurrentVersion"/>。</summary>
    public int Version { get; set; }

    /// <summary>把反序列化得到的值收敛到合法区间（docs/15 §4.3）。
    ///
    /// 畸形 JSON 会被 <c>SettingsStore.Load</c> 的 catch 整体兜住（回退默认），
    /// 但**合法 JSON 里的越界值**会一路直传：
    /// <c>ColorMode=99</c> 会被直接转成枚举喂给内核；
    /// <c>FrameStep=int.MaxValue</c> 会让 <c>d * FrameStep</c> 溢出成负数，
    /// 于是"下一帧"实际变成"上一帧"。
    /// 所以读取后必须统一收敛一次，不能指望写文件的人是善意的。
    ///
    /// 范围刻意与 UI 控件（SettingsWindow 的 NumericUpDown、侧栏拖拽）保持一致，
    /// 避免出现"设置界面不让输、手改配置文件却生效"的割裂。
    /// </summary>
    public void Normalize()
    {
        // 枚举合法性：**不要**用 Enum.IsDefined——它是反射，在 NativeAOT 下不保证可用
        if ((int)ColorMode < 0 || (int)ColorMode > 3) ColorMode = ColorModeSetting.Auto;

        FrameStep = Math.Clamp(FrameStep, 1, 999);          // 与设置窗口 NumericUpDown 一致
        SecondsStep = Math.Clamp(SecondsStep, 0.001, 60.0);
        DefaultGridCols = Math.Clamp(DefaultGridCols, 1, 3); // 3x3 是网格上限
        DefaultGridRows = Math.Clamp(DefaultGridRows, 1, 3);
        PreferredAdapterIndex = Math.Clamp(PreferredAdapterIndex, -1, 15); // -1=系统默认

        // 侧栏宽度：非正或大得离谱就当作没设置（null = 用默认）
        if (SidebarWidth is <= 0 or > 2000) SidebarWidth = null;

        // 窗口状态只恢复 Normal(0) / Maximized(2)：
        // Minimized(1) 无意义，FullScreen(3) 会让用户莫名全屏
        if (WindowState is not (null or 0 or 2)) WindowState = null;
    }

    /// <summary>窗口状态记忆（可用性 P0-2）：上次关闭时的位置/尺寸/状态。
    /// 均为 null = 首次运行或值无效，使用默认窗口。注意区分"未设置"与合法值 0，
    /// 所以 Position 不用 -1 哨兵（-1 在多显示器负坐标布局下是合法坐标）。</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    /// <summary>Avalonia WindowState 枚举 int 值。**注意枚举顺序不是直觉的数字**：
    /// 0=Normal, 1=Minimized, 2=Maximized, 3=FullScreen。只在 Normal/Maximized 时恢复
    /// （Minimized 无意义，FullScreen 会让用户莫名全屏）。</summary>
    public int? WindowState { get; set; }

    /// <summary>工具侧栏展开宽度（DIP）。null = 用默认 264。
    /// 拖拽分隔条后写入，下次启动恢复（主流工具侧栏均为可拖拽 + 可记忆）。</summary>
    public int? SidebarWidth { get; set; }

    /// <summary>工具侧栏是否处于折叠（图标导航栏）状态。</summary>
    public bool SidebarCollapsed { get; set; }

    public bool HardwareDecode { get; set; } = true;

    /// <summary>默认解码 GPU（-1=系统默认）。</summary>
    public int PreferredAdapterIndex { get; set; } = -1;

    /// <summary>手动指定的 FFmpeg DLL 目录（null/空白 = 自动检测：FFMPEG_DIR → PATH → 应用目录）。</summary>
    public string? FfmpegDirectory { get; set; }

    /// <summary>色彩模式：Auto=0 表示根据显示器能力自动选择 HDR/SDR。
    /// 旧值：MapToSdr=0→新 Auto=0 冲突，故 Auto 设为 3 保持向后兼容。</summary>
    public ColorModeSetting ColorMode { get; set; } = ColorModeSetting.Auto;

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

    /// <summary>时间轴拖动缩略图预览（默认开启）：拖动时每 150ms 抓帧显示弹窗。
    /// 低配设备可关闭，关闭后仅更新时间码和播放头，不触发 BitBlt 屏幕抓取。</summary>
    public bool ScrubPreviewEnabled { get; set; } = true;

    /// <summary>缩放小地图（默认开启）：缩放 > 1 时在表面右下角显示缩略视口指示器。</summary>
    public bool MinimapEnabled { get; set; } = true;

    /// <summary>界面语言（0=中文，1=英文）。</summary>
    public int Language { get; set; } = 0;
}

public enum ColorModeSetting
{
    MapToSdr = 0,
    RawHdrAsSdr = 1,
    MapToHdr = 2,
    /// <summary>根据显示器能力自动选择 HDR 或 SDR（默认值，兼容旧序列化值 3）。</summary>
    Auto = 3,
}

public static class ColorModeHelper
{
    /// <summary>将 ColorModeSetting 解析为内核 ColorMode。
    /// Auto 模式下根据显示器能力自动选择：HDR 显示器→MapToHdr，否则 MapToSdr。
    /// displayCaps 为 null 时视为无 HDR 能力。</summary>
    public static _3FCompare.Core.Backend.ColorMode Resolve(
        ColorModeSetting setting, _3FCompare.Core.Display.DisplayLuminanceCapabilities? displayCaps)
    {
        if (setting != ColorModeSetting.Auto)
            return (_3FCompare.Core.Backend.ColorMode)setting;
        return displayCaps?.Supported == true ? _3FCompare.Core.Backend.ColorMode.MapToHdr : _3FCompare.Core.Backend.ColorMode.MapToSdr;
    }
}