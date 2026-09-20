namespace _3FCompare.Core.Settings;

// 别名：本类的属性 SidebarMode 与枚举 SidebarMode 同名，直接写 `SidebarMode.Hidden`
// 会先命中属性（可空枚举）而不是类型 —— 用别名把两者彻底分开，避免踩 Color-Color 规则的边界。
using SidebarModeEnum = _3FCompare.Core.Settings.SidebarMode;

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

        // 三态侧栏：越界整数收敛成 null（= 未设置 → 由 SidebarCollapsed 兜底推导）。
        // 与上面 ColorMode 同因：合法 JSON 里的 SidebarMode=99 会一路直传进 UI 状态机，
        // 而 switch 的 default 分支会把"未知态"静默当成某一态（表现为"手改配置文件后侧栏乱跳"）。
        // **不要**用 Enum.IsDefined —— 反射在 NativeAOT 下不保证可用（同上）。
        if (SidebarMode is { } sm && ((int)sm < 0 || (int)sm > (int)SidebarModeEnum.Hidden))
            SidebarMode = null;

        // 窗口状态只恢复 Normal(0) / Maximized(2)：
        // Minimized(1) 无意义，FullScreen(3) 会让用户莫名全屏
        if (WindowState is not (null or 0 or 2)) WindowState = null;

        // 界面语言只有 0=中文 / 1=英文 两个取值。少收敛这一条的话，
        // 手改配置文件写 Language=99 ⇒ 走 else 分支显示中文（看起来"能工作"），
        // 随后又被 Save 原样写回，脏值就长久留在盘上且无人知晓。
        Language = (Language == 1) ? 1 : 0;

        // 版本号同样要收敛，而且**上界比下界更要紧**：
        // 用户从新版回退到旧版时，盘上文件带着 Version=2 进来，
        // MigrateLegacy 的 `Version < CurrentVersion` 判不出它是"未来版本"（2 > 1），
        // 迁移被整体跳过，旧代码按旧语义读新结构，最后 Save 再把 2 静默降级成 1。
        // 收敛到 [0, CurrentVersion] 至少让版本号不会往外漂；
        // "来自更新版"这件事本身由 SettingsStore.Load 显式告警。
        //
        // ⚠ 下界必须是 0：System.Text.Json 反序列化先跑属性初始化器再覆盖，
        // 老文件缺 Version 字段时读到的就是初始化器给的 0 —— 0 是"legacy"的唯一判据，
        // 这里绝不能把 0 收敛成 CurrentVersion（会把迁移判定彻底废掉）。
        Version = Math.Clamp(Version, 0, CurrentVersion);
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

    /// <summary>工具侧栏是否处于折叠（图标导航栏）状态。
    ///
    /// <para>⚠ 自引入 <see cref="SidebarMode"/> 后本字段<b>降级为兼容字段</b>：只用于读老配置文件
    /// （老文件没有 SidebarMode，null 时由它推导三态），写盘时仍与 SidebarMode 同步维护，
    /// 让用户回退到旧版本时不至于丢掉"折叠"这一半信息。</para></summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>工具侧栏三态（展开 / 图标栏 / 完全隐藏）。
    ///
    /// <para><b>为什么是可空枚举而不是加一个 bool 或非空枚举</b>：</para>
    /// <list type="bullet">
    /// <item><description>非空枚举的默认值 Expanded(0) 与"老文件里根本没有这个字段"不可区分 ——
    /// 老用户明明折叠着侧栏，升级后会被判成"显式展开"，偏好被静默丢掉。
    /// 可空让 null 专门表示"未设置"，与同文件的 <see cref="SidebarWidth"/> 同一套约定。</description></item>
    /// <item><description>相比"再加一个 bool 表示是否隐藏"，两个 bool 有四种组合而只有三种合法，
    /// 必然要额外写一套"哪种组合优先"的消歧规则 —— 状态机被摊平后反而更容易写错。</description></item>
    /// </list>
    ///
    /// <para>消费端（MainWindow.RestoreSidebarGeometry）把 null 解析为
    /// <c>SidebarCollapsed ? Rail : Expanded</c>；写盘时总是写入已解析出的确定值。</para></summary>
    public SidebarMode? SidebarMode { get; set; }

    /// <summary>底部时间轴是否被用户折叠（docs/31 阶段 4）。默认 false = 展开。
    /// 与 <see cref="SidebarCollapsed"/> 同风格：bool 无越界值，故不必进 <see cref="Normalize"/>，
    /// 序列化由 <c>JsonAotContext</c> 的 <c>[JsonSerializable(typeof(AppSettings))]</c> 自动覆盖。</summary>
    public bool TimelineCollapsed { get; set; }

    /// <summary>底部状态栏是否被用户折叠（docs/31 阶段 4）。默认 false = 展开。</summary>
    public bool StatusBarCollapsed { get; set; }

    /// <summary>传输栏是否改为<b>悬浮自动隐藏</b>（docs/31 阶段 4.3）。默认 false = 既有常驻布局。
    ///
    /// <para>默认关闭是刻意的：开启后传输栏由 owned 顶层窗承载（逐像素透明 + <c>WM_NCHITTEST</c>
    /// 穿透），是本次改动里唯一依赖真机合成能力的路径；保持默认关闭可让"既有常驻布局"完全不受影响，
    /// 由用户在「视图」菜单显式选择。</para>
    ///
    /// <para>与 <see cref="TimelineCollapsed"/> 同风格：bool 无越界值，故不必进 <see cref="Normalize"/>。</para></summary>
    public bool FloatingTransport { get; set; }

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

    /// <summary>时间轴拖动缩略图预览（默认开启）：拖动时每 150ms 抓帧显示弹窗。
    /// 低配设备可关闭，关闭后仅更新时间码和播放头，不触发 BitBlt 屏幕抓取。</summary>
    public bool ScrubPreviewEnabled { get; set; } = true;

    /// <summary>缩放小地图（默认开启）：缩放 > 1 时在表面右下角显示缩略视口指示器。</summary>
    public bool MinimapEnabled { get; set; } = true;

    /// <summary>界面语言（0=中文，1=英文）。</summary>
    public int Language { get; set; } = 0;
}

/// <summary>工具侧栏三态。
///
/// <para>取值顺序刻意与"信息量递减"一致（Expanded 最全 → Rail 只剩图标 → Hidden 什么都不留），
/// 于是 <see cref="AppSettings.Normalize"/> 只需一个区间检查就能收敛越界值，
/// 快捷键循环也只是 <c>(mode + 1) % 3</c>。</para>
///
/// <para>序列化为 int（System.Text.Json 默认行为，无自定义转换器），
/// 故**不要**在中间插入新成员 —— 那会改变已有数字的含义。</para></summary>
public enum SidebarMode
{
    /// <summary>完整侧栏：标题 + 导航 + 内容区 + 放大镜。</summary>
    Expanded = 0,
    /// <summary>图标导航栏（48px）：仅 5 个面板入口 + 放大镜，内容区整块不可见。</summary>
    Rail = 1,
    /// <summary>完全隐藏：列宽 0，侧栏与分割条都不可见。</summary>
    Hidden = 2,
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