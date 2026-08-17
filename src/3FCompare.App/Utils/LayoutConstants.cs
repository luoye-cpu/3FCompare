namespace _3FCompare.App.Utils;

/// <summary>布局相关常量定义。</summary>
public static class LayoutConstants
{
    /// <summary>网格布局相关。</summary>
    public static class Grid
    {
        /// <summary>最小路数。</summary>
        public const int MinCount = 1;

        /// <summary>最大路数。</summary>
        public const int MaxCount = 9;

        /// <summary>默认路数。</summary>
        public const int DefaultCount = 2;

        /// <summary>默认网格列数（多屏）。</summary>
        public const int DefaultCols = 3;

        /// <summary>默认网格行数（多屏）。</summary>
        public const int DefaultRows = 1;

        /// <summary>单屏模式下的列数。</summary>
        public const int SingleViewCols = 1;

        /// <summary>单屏模式下的行数。</summary>
        public const int SingleViewRows = 1;
    }

    /// <summary>播放控制相关。</summary>
    public static class Playback
    {
        /// <summary>默认播放速度（倍）。</summary>
        public const double DefaultSpeed = 1.0;

        /// <summary>最小播放速度。</summary>
        public const double MinSpeed = 0.5;

        /// <summary>最大播放速度。</summary>
        public const double MaxSpeed = 4.0;

        /// <summary>速度选项列表。</summary>
        public static readonly double[] SpeedOptions = { 0.5, 1.0, 2.0, 4.0 };

        /// <summary>默认色彩模式索引。</summary>
        public const int DefaultColorMode = 1; // MapToSdr

        /// <summary>色彩模式选项。</summary>
        public static readonly string[] ColorModeOptions = { "MapToSdr", "RawHdrAsSdr", "MapToHdr" };
    }

    /// <summary>时间轴相关。</summary>
    public static class Timeline
    {
        /// <summary>默认高度。</summary>
        public const int DefaultHeight = 34;

        /// <summary>最小高度。</summary>
        public const int MinHeight = 30;

        /// <summary>最大高度（通常不设置上限）。</summary>
        public const int MaxHeight = 100;

        /// <summary>拖动阈值（像素）。</summary>
        public const int DragThreshold = 5;

        /// <summary>A、B点标记宽度。</summary>
        public const int MarkerWidth = 3;

        /// <summary>A点颜色。</summary>
        public static readonly Color MarkerA = Color.FromArgb(255, 100, 100);

        /// <summary>B点颜色。</summary>
        public static readonly Color MarkerB = Color.FromArgb(100, 100, 255);
    }

    /// <summary>播放表面相关。</summary>
    public static class Surface
    {
        /// <summary>默认高度。</summary>
        public const int DefaultHeight = 480;

        /// <summary>最小高度。</summary>
        public const int MinHeight = 240;

        /// <summary>最小宽度。</summary>
        public const int MinWidth = 320;

        /// <summary>选中边框颜色。</summary>
        public const int SelectedBorderR = 64, SelectedBorderG = 160, SelectedBorderB = 255;
        public static readonly Color SelectedBorder = Color.FromArgb(SelectedBorderR, SelectedBorderG, SelectedBorderB);

        /// <summary>未选中边框颜色。</summary>
        public const int UnselectedBorderR = 60, UnselectedBorderG = 60, UnselectedBorderB = 70;
        public static readonly Color UnselectedBorder = Color.FromArgb(UnselectedBorderR, UnselectedBorderG, UnselectedBorderB);

        /// <summary>模拟模式背景渐变起始色。</summary>
        public const int SimBgStartR = 10, SimBgStartG = 10, SimBgStartB = 12;
        public static readonly Color SimBackgroundStart = Color.FromArgb(SimBgStartR, SimBgStartG, SimBgStartB);

        /// <summary>模拟模式背景渐变结束色。</summary>
        public const int SimBgEndR = 15, SimBgEndG = 15, SimBgEndB = 18;
        public static readonly Color SimBackgroundEnd = Color.FromArgb(SimBgEndR, SimBgEndG, SimBgEndB);
    }

    /// <summary>工具面板相关。</summary>
    public static class Tools
    {
        /// <summary>默认宽度。</summary>
        public const int DefaultWidth = 240;

        /// <summary>最小宽度。</summary>
        public const int MinWidth = 200;

        /// <summary>最大宽度。</summary>
        public const int MaxWidth = 400;

        /// <summary>工具切换按钮高度。</summary>
        public const int ToggleButtonHeight = 32;

        /// <summary>工具面板切换按钮宽度。</summary>
        public const int ToggleButtonWidth = 44;
    }

    /// <summary>窗口管理相关。</summary>
    public static class Window
    {
        /// <summary>最小宽度。</summary>
        public const int MinWidth = 640;

        /// <summary>最小高度。</summary>
        public const int MinHeight = 400;

        /// <summary>窗口位置偏移（避免重叠）。</summary>
        public const int Offset = 20;

        /// <summary>设置对话框默认宽度。</summary>
        public const int SettingsDialogWidth = 520;

        /// <summary>设置对话框默认高度。</summary>
        public const int SettingsDialogHeight = 460;

        /// <summary>全屏标志键名。</summary>
        public const string MaximizedKey = "Maximized";

        /// <summary>位置X键名。</summary>
        public const string PositionXKey = "PositionX";

        /// <summary>位置Y键名。</summary>
        public const string PositionYKey = "PositionY";

        /// <summary>宽度键名。</summary>
        public const string WidthKey = "Width";

        /// <summary>高度键名。</summary>
        public const string HeightKey = "Height";
    }

    /// <summary>探针面板相关。</summary>
    public static class Probe
    {
        /// <summary>默认宽度。</summary>
        public const int DefaultWidth = 230;

        /// <summary>最小宽度。</summary>
        public const int MinWidth = 200;

        /// <summary>最大宽度。</summary>
        public const int MaxWidth = 300;

        /// <summary>数值显示高度。</summary>
        public const int ValueDisplayHeight = 60;

        /// <summary>坐标显示高度。</summary>
        public const int CoordDisplayHeight = 20;

        /// <summary>模式显示高度。</summary>
        public const int ModeDisplayHeight = 20;
    }

    /// <summary>按钮相关。</summary>
    public static class Buttons
    {
        /// <summary>默认按钮高度。</summary>
        public const int DefaultHeight = 26;

        /// <summary>默认按钮宽度。</summary>
        public const int DefaultWidth = 36;

        /// <summary>工具栏按钮高度。</summary>
        public const int ToolbarButtonHeight = 32;

        /// <summary>工具栏按钮宽度。</summary>
        public const int ToolbarButtonWidth = 36;

        /// <summary>小按钮宽度（如减路按钮）。</summary>
        public const int SmallButtonWidth = 28;

        /// <summary>按钮圆角。</summary>
        public const int CornerRadius = 4;
    }

    /// <summary>文本框相关。</summary>
    public static class TextBox
    {
        /// <summary>默认高度。</summary>
        public const int DefaultHeight = 24;

        /// <summary>小文本框高度。</summary>
        public const int SmallHeight = 20;

        /// <summary>文本框圆角。</summary>
        public const int CornerRadius = 3;
    }
}
