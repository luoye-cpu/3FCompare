namespace _3FCompare.App.Utils;

/// <summary>3FCompare 应用程序主题配置。</summary>
public static class AppTheme
{
    /// <summary>主题颜色定义。</summary>
    public static class Colors
    {
        /// <summary>主背景色（最浅）。</summary>
        public const int BgR = 24, BgG = 24, BbB = 28;
        public static readonly Color Background = Color.FromArgb(BgR, BgG, BbB);

        /// <summary>面板背景色（中等）。</summary>
        public const int PanelR = 30, PanelG = 30, PanelB = 36;
        public static readonly Color PanelBackground = Color.FromArgb(PanelR, PanelG, PanelB);

        /// <summary>主文本颜色。</summary>
        public static readonly Color TextPrimary = Color.White;

        /// <summary>次要文本颜色。</summary>
        public const int TextSecR = 200, TextSecG = 200, TextSecB = 210;
        public static readonly Color TextSecondary = Color.FromArgb(TextSecR, TextSecG, TextSecB);

        /// <summary>强调色（用于选中/高亮）。</summary>
        public const int AccentR = 255, AccentG = 200, AccentB = 64;
        public static readonly Color Accent = Color.FromArgb(AccentR, AccentG, AccentB);

        /// <summary>成功颜色。</summary>
        public const int SuccessR = 100, SuccessG = 200, SuccessB = 100;
        public static readonly Color Success = Color.FromArgb(SuccessR, SuccessG, SuccessB);

        /// <summary>警告颜色。</summary>
        public const int WarningR = 255, WarningG = 180, WarningB = 50;
        public static readonly Color Warning = Color.FromArgb(WarningR, WarningG, WarningB);

        /// <summary>错误颜色。</summary>
        public const int ErrorR = 255, ErrorG = 100, ErrorB = 100;
        public static readonly Color Error = Color.FromArgb(ErrorR, ErrorG, ErrorB);

        /// <summary>画布背景色（视频/播放区域）。</summary>
        public const int CanvasR = 18, CanvasG = 18, CanvasB = 20;
        public static readonly Color CanvasBackground = Color.FromArgb(CanvasR, CanvasG, CanvasB);

        /// <summary>深画布背景色（网格容器/差异叠加层）。</summary>
        public const int CanvasDarkR = 10, CanvasDarkG = 10, CanvasDarkB = 12;
        public static readonly Color CanvasBackgroundDark = Color.FromArgb(CanvasDarkR, CanvasDarkG, CanvasDarkB);

        /// <summary>控件背景色（按钮/列表等标准控件）。</summary>
        public const int CtrlR = 40, CtrlG = 40, CtrlB = 46;
        public static readonly Color ControlBackground = Color.FromArgb(CtrlR, CtrlG, CtrlB);

        /// <summary>控件浅背景色（强调按钮）。</summary>
        public const int CtrlLightR = 45, CtrlLightG = 45, CtrlLightB = 52;
        public static readonly Color ControlBackgroundLight = Color.FromArgb(CtrlLightR, CtrlLightG, CtrlLightB);

        /// <summary>输入框背景色。</summary>
        public const int InputR = 36, InputG = 36, InputB = 42;
        public static readonly Color InputBackground = Color.FromArgb(InputR, InputG, InputB);

        /// <summary>输入框背景色（浅）。</summary>
        public const int InputAltR = 50, InputAltG = 50, InputAltB = 58;
        public static readonly Color InputBackgroundAlt = Color.FromArgb(InputAltR, InputAltG, InputAltB);

        /// <summary>激活/主按钮背景色。</summary>
        public const int ActiveR = 60, ActiveG = 90, ActiveB = 60;
        public static readonly Color ButtonActive = Color.FromArgb(ActiveR, ActiveG, ActiveB);

        /// <summary>次要按钮背景色。</summary>
        public const int SecR = 60, SecG = 60, SecB = 66;
        public static readonly Color ButtonSecondary = Color.FromArgb(SecR, SecG, SecB);

        /// <summary>边框颜色。</summary>
        public const int BorderR = 80, BorderG = 80, BorderB = 90;
        public static readonly Color Border = Color.FromArgb(BorderR, BorderG, BorderB);

        /// <summary>弱化文本颜色（提示/次要信息）。</summary>
        public const int MutedR = 140, MutedG = 140, MutedB = 150;
        public static readonly Color TextMuted = Color.FromArgb(MutedR, MutedG, MutedB);
    }

    /// <summary>字体定义。</summary>
    public static class Fonts
    {
        /// <summary>标题字体（加粗）。</summary>
        public static readonly Font TitleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);

        /// <summary>正文字体。</summary>
        public static readonly Font BodyFont = new Font("Microsoft YaHei UI", 9f);

        /// <summary>等宽字体（用于代码/数值显示）。</summary>
        public static readonly Font MonospaceFont = new Font("Consolas", 9f);

        /// <summary>等宽中号字体（时间/偏移等数值显示）。</summary>
        public static readonly Font MonospaceMediumFont = new Font("Consolas", 10f);

        /// <summary>等宽大号字体（探针 RGB 值等核心数值）。</summary>
        public static readonly Font MonospaceLargeFont = new Font("Consolas", 12f, FontStyle.Bold);

        /// <summary>小号说明文字字体。</summary>
        public static readonly Font CaptionFont = new Font("Microsoft YaHei UI", 8.5f);

        /// <summary>标题字体（大号）。</summary>
        public static readonly Font LargeTitleFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
    }

    /// <summary>间距定义（单位：像素）。</summary>
    public static class Spacing
    {
        /// <summary>标准间距。</summary>
        public static readonly Padding Standard = new Padding(8);

        /// <summary>紧凑间距。</summary>
        public static readonly Padding Compact = new Padding(4);

        /// <summary>大间距。</summary>
        public static readonly Padding Large = new Padding(16);

        /// <summary>控件之间的标准间距。</summary>
        public static readonly int ControlSpacing = 8;

        /// <summary>面板内部的标准间距。</summary>
        public static readonly int PanelInnerSpacing = 12;
    }

    /// <summary>尺寸常量。</summary>
    public static class Sizes
    {
        /// <summary>菜单栏高度（估算）。</summary>
        public static readonly int MenuHeight = 28;

        /// <summary>工具栏高度。</summary>
        public static readonly int ToolbarHeight = 44;

        /// <summary>时间轴高度。</summary>
        public static readonly int TimelineHeight = 34;

        /// <summary>状态栏高度。</summary>
        public static readonly int StatusBarHeight = 24;

        /// <summary>工具面板宽度。</summary>
        public static readonly int ToolsPanelWidth = 240;

        /// <summary>探针面板宽度。</summary>
        public static readonly int ProbePanelWidth = 230;

        /// <summary>书签面板宽度。</summary>
        public static readonly int BookmarkPanelWidth = 240;

        /// <summary>A-B滑块面板高度。</summary>
        public static readonly int AbSliderHeight = 480;
    }

    /// <summary>窗口管理常量。</summary>
    public static class Window
    {
        /// <summary>最小宽度。</summary>
        public const int MinWidth = 640;

        /// <summary>最小高度。</summary>
        public const int MinHeight = 400;

        /// <summary>主窗口默认宽度。</summary>
        public const int DefaultWidth = 1600;

        /// <summary>主窗口默认高度。</summary>
        public const int DefaultHeight = 900;

        /// <summary>窗口保存的配置键前缀。</summary>
        public const string ConfigPrefix = "Window";
    }

    /// <summary>工具提示配置。</summary>
    public static class ToolTips
    {
        /// <summary>自动显示延迟（毫秒）。</summary>
        public static readonly int AutoPopDelay = 5000;

        /// <summary>初始显示延迟（毫秒）。</summary>
        public static readonly int InitialDelay = 300;

        /// <summary>重新显示延迟（毫秒）。</summary>
        public static readonly int ReshowDelay = 100;

        /// <summary>工具提示文本样式。</summary>
        public static readonly string Format = "{0} ({1})";
    }
}
