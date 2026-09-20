using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using global::Avalonia.Platform.Storage;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Diagnostics;

namespace _3FCompare.Views;

/// <summary>设置窗口（WinForms SettingsDialog 对应，7 节）：
/// 语言/硬件加速+GPU/步进/窗口全屏/解码色彩/布局/FFmpeg 路径+检测。
/// OK 时差异检测构建新 AppSettings（Changed=true，Result）。</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _orig;
    private readonly ComboBox _lang = new();
    private readonly CheckBox _hwDecode = new();
    private readonly ComboBox _gpu = new();
    private readonly NumericUpDown _frameStep = new() { Minimum = 1, Maximum = 999, Increment = 1 };
    private readonly NumericUpDown _secStep = new() { Minimum = 1, Maximum = 1200, Increment = 0.5m, FormatString = "0.#" };
    private readonly CheckBox _startFullscreen = new();
    private readonly CheckBox _hideChrome = new() { IsChecked = true };
    private readonly CheckBox _vrrTearing = new();
    private readonly CheckBox _scrubPreview = new() { IsChecked = true };
    private readonly CheckBox _minimap = new() { IsChecked = true };
    private readonly ComboBox _colorMode = new();
    private readonly NumericUpDown _cols = new() { Minimum = 1, Maximum = 3, Increment = 1 };
    private readonly NumericUpDown _rows = new() { Minimum = 1, Maximum = 3, Increment = 1 };
    private readonly TextBox _ffmpegDir = new();
    private readonly TextBlock _ffmpegStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _theme = new();

    /// <summary>打开设置窗口时的主题偏好：取消/关闭时据此还原（本窗口的主题是实时预览的）。</summary>
    private readonly ThemePreference _origTheme = ThemeManager.Preference;

    /// <summary>是否走了「确定」。用于区分「确定后关闭」与「取消/X 关闭」，后者要还原主题预览。</summary>
    private bool _accepted;

    protected override void OnClosed(EventArgs e)
    {
        // 实时预览过的主题在未确认时还原：否则用户点「取消」却留下了改过的外观，
        // 而配置里存的仍是旧值 —— 下次启动又变回去，看起来像"设置没保存"。
        if (!_accepted) ThemeManager.Apply(_origTheme, persist: false);
        base.OnClosed(e);
    }

    /// <summary>GPU 下拉框背后的适配器列表（与 _gpu.Items 一一对应）。
    /// 保存 AdapterInfo 本体而不是只存显示文本：真正要写进设置的是
    /// <see cref="AdapterInfo.Index"/>（DXGI 适配器序号），不是列表位置。</summary>
    private readonly List<AdapterInfo> _adapters = new();

    /// <summary>OK 且有变更时为 true；新值在 Result。</summary>
    public bool Changed { get; private set; }
    public AppSettings? Result { get; private set; }

    /// <summary>FFmpeg 路径是否被修改（调用方需提示重启）。</summary>
    public bool FfmpegChanged { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        // 深拷贝：避免 _orig 与外部 _settings 共享引用，否则 CopySettings 后
        // changed 比较失效，SettingsStore.Save 不再被调用
        _orig = new AppSettings
        {
            HardwareDecode = current.HardwareDecode,
            PreferredAdapterIndex = current.PreferredAdapterIndex,
            FfmpegDirectory = current.FfmpegDirectory,
            ColorMode = current.ColorMode,
            FrameStep = current.FrameStep,
            SecondsStep = current.SecondsStep,
            StartFullscreen = current.StartFullscreen,
            HideChromeInFullscreen = current.HideChromeInFullscreen,
            DefaultGridCols = current.DefaultGridCols,
            DefaultGridRows = current.DefaultGridRows,
            VrrTearingPresent = current.VrrTearingPresent,
            ScrubPreviewEnabled = current.ScrubPreviewEnabled,
            MinimapEnabled = current.MinimapEnabled,
            WindowX = current.WindowX,
            WindowY = current.WindowY,
            WindowWidth = current.WindowWidth,
            WindowHeight = current.WindowHeight,
            WindowState = current.WindowState,
            SidebarWidth = current.SidebarWidth,
            SidebarCollapsed = current.SidebarCollapsed,
            SidebarMode = current.SidebarMode,
            TimelineCollapsed = current.TimelineCollapsed,
            StatusBarCollapsed = current.StatusBarCollapsed,
            FloatingTransport = current.FloatingTransport,
            Language = current.Language,
        };
        Title = LanguageManager.T("Settings_DialogTitle");
        Width = 720; Height = 760;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemePalette.SetBrush(this, BackgroundProperty, "BgBrush");

        // 初始值
        _lang.Items.Add("中文");
        _lang.Items.Add("English");
        _lang.SelectedIndex = current.Language == 1 ? 1 : 0;
        _hwDecode.Content = LanguageManager.T("Hardware_EnableHardwareDecode");
        _hwDecode.IsChecked = current.HardwareDecode;
        _adapters.AddRange(GpuEnumeration.Enumerate());
        foreach (var a in _adapters)
            _gpu.Items.Add(a.Description);
        if (_gpu.Items.Count > 0)
        {
            // 用 AdapterInfo.Index 匹配，而不是"列表位置 - 1"：
            // 列表首项固定是"系统默认"(Index = -1)，但退化路径下（DXGI 不可用时只有这一项）
            // 按位置换算会得到越界下标 1，ComboBox 把它纠正成 -1 ⇒ Accept 算出 adapter = -2，
            // 与 AppSettings.Normalize() 的 [-1,15] 不一致，重启后又被默默改回 -1。
            var pos = _adapters.FindIndex(a => a.Index == current.PreferredAdapterIndex);
            if (pos < 0) pos = 0; // 选中的卡这次没枚举到（换显卡/驱动变化）→ 退回"系统默认"
            // 仍要钳一次：FindIndex 的结果必然在范围内，但 SelectedIndex 越界会被静默纠正
            _gpu.SelectedIndex = Math.Clamp(pos, 0, _gpu.Items.Count - 1);
        }
        ToolTip.SetTip(_gpu, LanguageManager.T("Hardware_GpuHint"));
        _frameStep.Value = current.FrameStep;
        _secStep.Value = (decimal)current.SecondsStep;
        _startFullscreen.Content = LanguageManager.T("Window_StartFullscreen");
        _startFullscreen.IsChecked = current.StartFullscreen;
        _hideChrome.Content = LanguageManager.T("Window_HideChrome");
        _hideChrome.IsChecked = current.HideChromeInFullscreen;
        _vrrTearing.Content = LanguageManager.T("Vrr_TearingPresent");
        ToolTip.SetTip(_vrrTearing, LanguageManager.T("Vrr_TearingHint"));
        _vrrTearing.IsChecked = current.VrrTearingPresent;
        _scrubPreview.Content = LanguageManager.T("Scrub_PreviewEnabled");
        _scrubPreview.IsChecked = current.ScrubPreviewEnabled;
        ToolTip.SetTip(_scrubPreview, LanguageManager.T("Scrub_PreviewHint"));
        _minimap.Content = LanguageManager.T("Zoom_Minimap");
        _minimap.IsChecked = current.MinimapEnabled;
        _colorMode.Items.Add(LanguageManager.T("Color_Auto"));
        _colorMode.Items.Add(LanguageManager.T("Color_SDR"));
        _colorMode.Items.Add(LanguageManager.T("Color_HDRAuto"));
        _colorMode.SelectedIndex = current.ColorMode == ColorModeSetting.Auto ? 0
            : current.ColorMode == ColorModeSetting.MapToHdr ? 2 : 1;
        _cols.Value = current.DefaultGridCols;
        _rows.Value = current.DefaultGridRows;
        _ffmpegDir.Text = current.FfmpegDirectory ?? string.Empty;
        UpdateFfmpegStatus();

        // 外观：主题 = 跟随系统 / 浅色 / 深色。改选即生效（实时预览），
        // 点「确定」才落盘；取消/直接关窗由 OnClosed 还原。
        _theme.Items.Add(LanguageManager.T("Theme_System"));
        _theme.Items.Add(LanguageManager.T("Theme_Light"));
        _theme.Items.Add(LanguageManager.T("Theme_Dark"));
        _theme.SelectedIndex = (int)ThemeManager.Preference;
        _theme.SelectionChanged += (_, _) =>
        {
            if (_theme.SelectedIndex >= 0)
                ThemeManager.Apply((ThemePreference)_theme.SelectedIndex, persist: false);
        };

        var scroll = new ScrollViewer();
        var stack = new StackPanel { Margin = new global::Avalonia.Thickness(16), Spacing = 6 };

        stack.Children.Add(Section(LanguageManager.T("Theme_SectionTitle"),
            Row(Label(LanguageManager.T("Theme_Mode")), _theme),
            Hint(LanguageManager.T("Theme_Hint"))));
        stack.Children.Add(Section(LanguageManager.T("Menu_Settings_Lang"), _lang));
        stack.Children.Add(Section(LanguageManager.T("Hardware_EnableHardwareDecode"),
            Row(_hwDecode, Label(LanguageManager.T("Hardware_DecodeGPU")), _gpu)));
        stack.Children.Add(Section(LanguageManager.T("Status_Steps"),
            Row(Label(LanguageManager.T("Stepping_StepByFrame")), _frameStep,
                Label(LanguageManager.T("Stepping_StepBySecond")), _secStep)));
        stack.Children.Add(Section(LanguageManager.T("Window_StartFullscreen"),
            Row(_startFullscreen, _hideChrome)));
        stack.Children.Add(Section(LanguageManager.T("Vrr_SectionTitle"),
            _vrrTearing,
            Hint(LanguageManager.T("Vrr_TearingHint"))));
        stack.Children.Add(Section(LanguageManager.T("Scrub_SectionTitle"),
            _scrubPreview, _minimap, Hint(LanguageManager.T("Scrub_PreviewHint"))));
        stack.Children.Add(Section(LanguageManager.T("Status_Color"),
            Row(_colorMode)));
        stack.Children.Add(Section(LanguageManager.T("Layout_DefaultCols"),
            Row(Label(LanguageManager.T("Layout_DefaultCols")), _cols,
                Label(LanguageManager.T("Layout_DefaultRows")), _rows)));

        var browse = new Button { Content = LanguageManager.T("FFmpeg_Browse"), Height = 26 };
        browse.Click += async (_, _) =>
        {
            var dir = await StorageProvider.OpenFolderPickerAsync(new global::Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = LanguageManager.T("Msg_FolderTitle"),
            });
            if (dir is { Count: > 0 })
            {
                _ffmpegDir.Text = dir[0].TryGetLocalPath() ?? _ffmpegDir.Text;
                UpdateFfmpegStatus();
            }
        };
        var test = new Button { Content = LanguageManager.T("FFmpeg_Test"), Height = 26 };
        test.Click += (_, _) => UpdateFfmpegStatus(forceValidate: true);
        stack.Children.Add(Section(LanguageManager.T("FFmpeg_Path"),
            _ffmpegDir, Row(browse, test),
            Hint(LanguageManager.T("FFmpeg_Hint")), _ffmpegStatus));

        var ok = new Button { Content = LanguageManager.T("Settings_Ok"), Width = 90, Height = 30 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = LanguageManager.T("Settings_Cancel"), Width = 90, Height = 30 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        scroll.Content = stack;
        Content = scroll;
    }

    private void UpdateFfmpegStatus(bool forceValidate = false)
    {
        var dir = _ffmpegDir.Text?.Trim();
        if (!string.IsNullOrEmpty(dir))
        {
            // ValidateFfmpegDirectory 约定：null=有效，非 null=错误原因
            var error = NativeRuntime.ValidateFfmpegDirectory(dir);
            if (error is not null)
            {
                _ffmpegStatus.Text = $"{LanguageManager.T("Msg_ValidateFailed")}（{error}）";
                ThemePalette.SetBrush(_ffmpegStatus, TextBlock.ForegroundProperty, "ErrorBrush");
                return;
            }
            if (!forceValidate)
            {
                _ffmpegStatus.Text = LanguageManager.T("Msg_ValidateSuccess");
                ThemePalette.SetBrush(_ffmpegStatus, TextBlock.ForegroundProperty, "SuccessBrush");
                return;
            }
            // 测试探测：目录结构有效（保存后重启生效，不在此处预加载 DLL 避免文件系统副作用）
            _ffmpegStatus.Text = LanguageManager.T("Msg_ValidateSuccess");
            ThemePalette.SetBrush(_ffmpegStatus, TextBlock.ForegroundProperty, "SuccessBrush");
            return;
        }

        // 目录留空：展示自动检测的真实结果（FFMPEG_DIR / PATH / 应用目录）
        var auto = NativeRuntime.AutoDetectFfmpegDirectory();
        _ffmpegStatus.Text = auto is not null
            ? LanguageManager.T("Msg_AutoDetectSuccess") + auto
            : LanguageManager.T("Msg_AutoDetectFailed");
        ThemePalette.SetBrush(_ffmpegStatus, TextBlock.ForegroundProperty,
            auto is not null ? "SuccessBrush" : "TextMutedBrush");
    }

    /// <summary>当前选中的 DXGI 适配器序号（-1 = 系统默认，跟随窗口所在显示器）。</summary>
    private int SelectedAdapterIndex()
    {
        var i = _gpu.SelectedIndex;
        // 取列表项自带的 Index，而不是 i - 1：退化路径下列表可能不足两项，
        // i - 1 会算出 -2（越出 AppSettings.Normalize() 的 [-1,15]，重启后被悄悄纠正为 -1）。
        if (i < 0 || i >= _adapters.Count) return -1;
        var idx = _adapters[i].Index;
        // 兜底钳制：与 AppSettings.Normalize() 的取值范围保持一致
        return idx is < -1 or > 15 ? -1 : idx;
    }

    private void Accept()
    {
        var lang = _lang.SelectedIndex;
        var hw = _hwDecode.IsChecked == true;
        var adapter = SelectedAdapterIndex();
        var frame = (int)(_frameStep.Value ?? 1);
        var sec = (double)(_secStep.Value ?? 1.0m);
        var startFs = _startFullscreen.IsChecked == true;
        var hideChrome = _hideChrome.IsChecked == true;
        var color = _colorMode.SelectedIndex switch
        {
            0 => ColorModeSetting.Auto,
            2 => ColorModeSetting.MapToHdr,
            _ => ColorModeSetting.MapToSdr,
        };
        var cols = (int)(_cols.Value ?? 2);
        var rows = (int)(_rows.Value ?? 1);
        var vrrTearing = _vrrTearing.IsChecked == true;
        var scrubPreview = _scrubPreview.IsChecked == true;
        var miniMap = _minimap.IsChecked == true;
        var ffmpeg = string.IsNullOrWhiteSpace(_ffmpegDir.Text) ? null : _ffmpegDir.Text.Trim();

        // 目录非空但校验失败 ⇒ 阻止保存。旧实现允许带着无效目录"确定"并弹重启询问，
        // 重启后被后端 IsAcceptableFfmpegDirectory 拒绝、回退自动检测，只在日志里留一行，
        // 用户因此困在"设置→重启→无效"的循环里。留空 = 自动检测，是合法值，不拦截。
        if (ffmpeg is not null)
        {
            var ffmpegError = NativeRuntime.ValidateFfmpegDirectory(ffmpeg);
            if (ffmpegError is not null)
            {
                _ffmpegStatus.Text =
                    $"{LanguageManager.T("Msg_ValidateFailed")}（{ffmpegError}）\n{LanguageManager.T("Msg_AutoDetect")}";
                ThemePalette.SetBrush(_ffmpegStatus, TextBlock.ForegroundProperty, "ErrorBrush");
                _ffmpegDir.Focus();
                _ffmpegDir.SelectAll();
                return; // 不关窗、不保存
            }
        }

        var changed = lang != _orig.Language || hw != _orig.HardwareDecode || adapter != _orig.PreferredAdapterIndex
            || frame != _orig.FrameStep || Math.Abs(sec - _orig.SecondsStep) > 0.001
            || startFs != _orig.StartFullscreen || hideChrome != _orig.HideChromeInFullscreen
            || color != _orig.ColorMode || cols != _orig.DefaultGridCols || rows != _orig.DefaultGridRows
            || vrrTearing != _orig.VrrTearingPresent
            || scrubPreview != _orig.ScrubPreviewEnabled || miniMap != _orig.MinimapEnabled
            || ffmpeg != _orig.FfmpegDirectory;
        FfmpegChanged = ffmpeg != _orig.FfmpegDirectory;

        if (changed)
        {
            Changed = true;
            Result = new AppSettings
            {
                HardwareDecode = hw,
                PreferredAdapterIndex = adapter,
                FfmpegDirectory = ffmpeg,
                ColorMode = color,
                FrameStep = frame,
                SecondsStep = sec,
                StartFullscreen = startFs,
                HideChromeInFullscreen = hideChrome,
                DefaultGridCols = cols,
                DefaultGridRows = rows,
                VrrTearingPresent = vrrTearing,
                ScrubPreviewEnabled = scrubPreview,
                MinimapEnabled = miniMap,
                WindowX = _orig.WindowX, WindowY = _orig.WindowY,
                WindowWidth = _orig.WindowWidth, WindowHeight = _orig.WindowHeight,
                WindowState = _orig.WindowState,
                SidebarWidth = _orig.SidebarWidth,
                SidebarCollapsed = _orig.SidebarCollapsed,
                SidebarMode = _orig.SidebarMode,
                TimelineCollapsed = _orig.TimelineCollapsed,
                StatusBarCollapsed = _orig.StatusBarCollapsed,
                FloatingTransport = _orig.FloatingTransport,
                Language = lang,
            };
        }

        // 主题在改选时已实时生效，这里只负责落盘（Cancel/X 关闭由 OnClosed 还原）。
        // 放在最后：上面的 FFmpeg 校验失败会 return 且不关窗，那不算「确定」。
        ComponentLog.Log(Comp.Theme, "Apply", -1,
            $"pref={(ThemePreference)Math.Max(0, _theme.SelectedIndex)} persist=True");
        ComponentLog.Log(Comp.Settings, "Accepted", -1, "src=settingsWindow");
        ThemeManager.Apply((ThemePreference)Math.Max(0, _theme.SelectedIndex), persist: true);
        _accepted = true;
        Close();
    }

    // ---- 构建辅助 ----
    // 颜色一律经 ThemePalette.SetBrush 绑到令牌：构造时固化画刷会在主题切换后停在旧值。

    private static TextBlock Label(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ThemePalette.SetBrush(tb, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return tb;
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }

    private static TextBlock Hint(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        ThemePalette.SetBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
        return tb;
    }

    private static Control Section(string title, params Control[] children)
    {
        var border = new Border
        {
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(4),
            Padding = new global::Avalonia.Thickness(10),
            Margin = new global::Avalonia.Thickness(0, 6, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { TitleBlock(title) },
            },
        };
        ThemePalette.SetBrush(border, Border.BorderBrushProperty, "BorderBrush");
        var inner = (StackPanel)((Border)border).Child!;
        foreach (var c in children) inner.Children.Add(c);
        return border;
    }

    /// <summary>节标题：强调色文字用 <c>AccentTextBrush</c>（浅色下比图形强调色更深，保证 13px 粗体可读）。</summary>
    private static TextBlock TitleBlock(string title)
    {
        var tb = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.Bold };
        ThemePalette.SetBrush(tb, TextBlock.ForegroundProperty, "AccentTextBrush");
        return tb;
    }
}
