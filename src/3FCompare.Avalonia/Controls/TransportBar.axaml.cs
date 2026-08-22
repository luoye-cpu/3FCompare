using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using _3FCompare.App;

namespace _3FCompare.Avalonia.Controls;

/// <summary>传输栏（WinForms TransportBar 对应）：播放/停止/双步进/循环/加减路/倍速/色彩模式/时间码。</summary>
public partial class TransportBar : UserControl
{
    private static readonly double[] Speeds = { 0.5, 1.0, 2.0, 4.0 };
    private bool _suppressComboEvents;

    public event EventHandler? PlayPauseClicked;
    public event EventHandler? StopClicked;
    public event EventHandler<int>? FrameStepClicked;      // ±1
    public event EventHandler<double>? SecondsStepClicked; // ±seconds
    public event EventHandler<bool>? LoopToggled;
    public event EventHandler? AddClicked;
    public event EventHandler? RemoveClicked;
    public event EventHandler<double>? SpeedChanged;
    public event EventHandler<int>? ColorModeChanged;      // 0=SDR,1=HDR

    public double CurrentSpeed => BtnPlayPause.Tag is double d ? d : 1.0;
    public int CurrentColorMode => ComboColorMode.SelectedIndex;

    public TransportBar()
    {
        InitializeComponent();
        foreach (var s in Speeds)
            ComboSpeed.Items.Add($"{s:0.#}x");
        ComboSpeed.SelectedIndex = 1;
        ComboColorMode.Items.Add("SDR");
        ComboColorMode.Items.Add("HDR");
        ComboColorMode.SelectedIndex = 0;
        LanguageManager.LanguageChanged += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLanguage);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        ToolTip.SetTip(BtnPlayPause, LanguageManager.T("Tb_Play"));
        ToolTip.SetTip(BtnStop, LanguageManager.T("Tb_Stop"));
        ToolTip.SetTip(BtnFramePrev, LanguageManager.T("Tb_FramePrev"));
        ToolTip.SetTip(BtnFrameNext, LanguageManager.T("Tb_FrameNext"));
        ToolTip.SetTip(BtnSecPrev, LanguageManager.T("Tb_SecPrev"));
        ToolTip.SetTip(BtnSecNext, LanguageManager.T("Tb_SecNext"));
        ToolTip.SetTip(BtnLoop, LanguageManager.T("Tb_LoopOff"));
        ToolTip.SetTip(BtnAdd, LanguageManager.T("Tb_Add"));
        ToolTip.SetTip(BtnRemove, LanguageManager.T("Tb_Remove"));
        ToolTip.SetTip(ComboSpeed, LanguageManager.T("Tb_Speed"));
        ToolTip.SetTip(ComboColorMode, LanguageManager.T("Tb_ColorMode"));
    }

    // ---------- 状态回显 ----------

    public void SetPlaying(bool playing)
    {
        BtnPlayPause.Content = playing ? "⏸" : "▶";
        ToolTip.SetTip(BtnPlayPause, LanguageManager.T(playing ? "Tb_Pause" : "Tb_Play"));
    }

    public void SetLoop(bool on)
    {
        BtnLoop.Background = on
            ? new SolidColorBrush(Color.FromRgb(60, 90, 60))
            : null;
        ToolTip.SetTip(BtnLoop, LanguageManager.T(on ? "Tb_LoopOn" : "Tb_LoopOff"));
    }

    /// <summary>PR 风格时间码：HH:MM:SS:FF / HH:MM:SS。</summary>
    public void SetTime(TimeSpan pos, TimeSpan dur, int frameInSecond)
    {
        TextTime.Text =
            $"{pos:hh\\:mm\\:ss}:{frameInSecond:D2} / {dur:hh\\:mm\\:ss}";
    }

    public void SetInfo(string? info) => TextInfo.Text = info ?? string.Empty;

    // ---------- 按钮事件 ----------

    private void OnPlayPause(object? sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);
    private void OnStop(object? sender, RoutedEventArgs e) => StopClicked?.Invoke(this, EventArgs.Empty);
    private void OnFramePrev(object? sender, RoutedEventArgs e) => FrameStepClicked?.Invoke(this, -1);
    private void OnFrameNext(object? sender, RoutedEventArgs e) => FrameStepClicked?.Invoke(this, 1);
    private void OnSecPrev(object? sender, RoutedEventArgs e) => SecondsStepClicked?.Invoke(this, -CurrentStepSeconds());
    private void OnSecNext(object? sender, RoutedEventArgs e) => SecondsStepClicked?.Invoke(this, CurrentStepSeconds());

    private double CurrentStepSeconds() => StepProfileSecondsProvider?.Invoke() ?? 1.0;

    /// <summary>秒步进长度提供者（MainWindow 注入 SyncController.StepProfile.SecondsStep）。</summary>
    public Func<double>? StepProfileSecondsProvider { get; set; }

    private void OnLoop(object? sender, RoutedEventArgs e) =>
        LoopToggled?.Invoke(this, BtnLoop.Background is not null);

    private void OnAdd(object? sender, RoutedEventArgs e) => AddClicked?.Invoke(this, EventArgs.Empty);
    private void OnRemove(object? sender, RoutedEventArgs e) => RemoveClicked?.Invoke(this, EventArgs.Empty);

    private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || ComboSpeed.SelectedIndex < 0) return;
        var speed = Speeds[ComboSpeed.SelectedIndex];
        BtnPlayPause.Tag = speed;
        SpeedChanged?.Invoke(this, speed);
    }

    private void OnColorModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || ComboColorMode.SelectedIndex < 0) return;
        ColorModeChanged?.Invoke(this, ComboColorMode.SelectedIndex);
    }

    /// <summary>设置色彩模式下拉（0=SDR,1=HDR），不触发事件。</summary>
    public void SetColorMode(int index)
    {
        _suppressComboEvents = true;
        ComboColorMode.SelectedIndex = index;
        _suppressComboEvents = false;
    }
}
