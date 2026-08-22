using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using global::Avalonia.Platform.Storage;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;

namespace _3FCompare.Avalonia.Views;

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
    private readonly CheckBox _vrrPacing = new();
    private readonly ComboBox _colorMode = new();
    private readonly NumericUpDown _cols = new() { Minimum = 1, Maximum = 3, Increment = 1 };
    private readonly NumericUpDown _rows = new() { Minimum = 1, Maximum = 3, Increment = 1 };
    private readonly TextBox _ffmpegDir = new();
    private readonly TextBlock _ffmpegStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>OK 且有变更时为 true；新值在 Result。</summary>
    public bool Changed { get; private set; }
    public AppSettings? Result { get; private set; }

    /// <summary>FFmpeg 路径是否被修改（调用方需提示重启）。</summary>
    public bool FfmpegChanged { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        _orig = current;
        Title = LanguageManager.T("Settings_DialogTitle");
        Width = 720; Height = 760;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));

        // 初始值
        _lang.Items.Add("中文");
        _lang.Items.Add("English");
        _lang.SelectedIndex = current.Language == 1 ? 1 : 0;
        _hwDecode.Content = LanguageManager.T("Hardware_EnableHardwareDecode");
        _hwDecode.IsChecked = current.HardwareDecode;
        foreach (var a in GpuEnumeration.Enumerate())
            _gpu.Items.Add(a.Description);
        _gpu.SelectedIndex = Math.Max(0, current.PreferredAdapterIndex + 1);
        _frameStep.Value = current.FrameStep;
        _secStep.Value = (decimal)current.SecondsStep;
        _startFullscreen.Content = LanguageManager.T("Window_StartFullscreen");
        _startFullscreen.IsChecked = current.StartFullscreen;
        _hideChrome.Content = LanguageManager.T("Window_HideChrome");
        _hideChrome.IsChecked = current.HideChromeInFullscreen;
        _vrrTearing.Content = LanguageManager.T("Vrr_TearingPresent");
        ToolTip.SetTip(_vrrTearing, LanguageManager.T("Vrr_TearingHint"));
        _vrrTearing.IsChecked = current.VrrTearingPresent;
        _vrrPacing.Content = LanguageManager.T("Vrr_PacingEnabled");
        _vrrPacing.IsChecked = current.VrrPacingEnabled;
        ToolTip.SetTip(_vrrPacing, LanguageManager.T("Vrr_PacingHint"));
        _colorMode.Items.Add(LanguageManager.T("Color_Auto"));
        _colorMode.Items.Add(LanguageManager.T("Color_SDR"));
        _colorMode.Items.Add(LanguageManager.T("Color_HDRAuto"));
        _colorMode.SelectedIndex = current.ColorMode == ColorModeSetting.Auto ? 0
            : current.ColorMode == ColorModeSetting.MapToHdr ? 2 : 1;
        _cols.Value = current.DefaultGridCols;
        _rows.Value = current.DefaultGridRows;
        _ffmpegDir.Text = current.FfmpegDirectory ?? string.Empty;
        UpdateFfmpegStatus();

        var scroll = new ScrollViewer();
        var stack = new StackPanel { Margin = new global::Avalonia.Thickness(16), Spacing = 6 };

        stack.Children.Add(Section(LanguageManager.T("Menu_Settings_Lang"), _lang));
        stack.Children.Add(Section(LanguageManager.T("Hardware_EnableHardwareDecode"),
            Row(_hwDecode, Label(LanguageManager.T("Hardware_DecodeGPU")), _gpu)));
        stack.Children.Add(Section(LanguageManager.T("Status_Steps"),
            Row(Label(LanguageManager.T("Stepping_StepByFrame")), _frameStep,
                Label(LanguageManager.T("Stepping_StepBySecond")), _secStep)));
        stack.Children.Add(Section(LanguageManager.T("Window_StartFullscreen"),
            Row(_startFullscreen, _hideChrome)));
        stack.Children.Add(Section(LanguageManager.T("Vrr_SectionTitle"),
            _vrrTearing, _vrrPacing,
            Hint(LanguageManager.T("Vrr_TearingHint")), Hint(LanguageManager.T("Vrr_PacingHint"))));
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
        if (forceValidate && !string.IsNullOrEmpty(dir))
        {
            var validated = NativeRuntime.ValidateFfmpegDirectory(dir);
            var valid = !string.IsNullOrEmpty(validated);
            _ffmpegStatus.Text = valid
                ? LanguageManager.T("Msg_ValidateSuccess")
                : $"{LanguageManager.T("Msg_ValidateFailed")}（{dir}）";
            _ffmpegStatus.Foreground = valid
                ? new SolidColorBrush(Color.FromRgb(100, 200, 100))
                : new SolidColorBrush(Color.FromRgb(255, 100, 100));
            return;
        }
        if (string.IsNullOrEmpty(dir))
        {
            var auto = NativeRuntime.IsFfmpegAvailable();
            _ffmpegStatus.Text = auto
                ? LanguageManager.T("Msg_AutoDetectSuccess") + NativeRuntime.AutoDetectFfmpegDirectory()
                : LanguageManager.T("Msg_AutoDetectFailed");
        }
        else
        {
            _ffmpegStatus.Text = LanguageManager.T("Msg_AutoDetect");
        }
        _ffmpegStatus.Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150));
    }

    private void Accept()
    {
        var lang = _lang.SelectedIndex;
        var hw = _hwDecode.IsChecked == true;
        var adapter = _gpu.SelectedIndex - 1;
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
        var vrrPacing = _vrrPacing.IsChecked == true;
        var ffmpeg = string.IsNullOrWhiteSpace(_ffmpegDir.Text) ? null : _ffmpegDir.Text.Trim();

        var changed = lang != _orig.Language || hw != _orig.HardwareDecode || adapter != _orig.PreferredAdapterIndex
            || frame != _orig.FrameStep || Math.Abs(sec - _orig.SecondsStep) > 0.001
            || startFs != _orig.StartFullscreen || hideChrome != _orig.HideChromeInFullscreen
            || color != _orig.ColorMode || cols != _orig.DefaultGridCols || rows != _orig.DefaultGridRows
            || vrrTearing != _orig.VrrTearingPresent || vrrPacing != _orig.VrrPacingEnabled
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
                VrrPacingEnabled = vrrPacing,
                WindowX = _orig.WindowX, WindowY = _orig.WindowY,
                WindowWidth = _orig.WindowWidth, WindowHeight = _orig.WindowHeight,
                WindowMaximized = _orig.WindowMaximized,
                Language = lang,
            };
        }
        Close();
    }

    // ---- 构建辅助 ----

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
    };

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
    };

    private static Control Section(string title, params Control[] children)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 90)),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(4),
            Padding = new global::Avalonia.Thickness(10),
            Margin = new global::Avalonia.Thickness(0, 6, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title, FontSize = 13, FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 64)),
                    },
                },
            },
        };
        var inner = (StackPanel)((Border)border).Child!;
        foreach (var c in children) inner.Children.Add(c);
        return border;
    }
}
