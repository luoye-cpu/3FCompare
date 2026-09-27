using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using _3FCompare.App;
// 本工程开了 ImplicitUsings，System.IO.Path 在作用域内 ⇒ 必须起别名，否则 Path 二义
using Path = Avalonia.Controls.Shapes.Path;

namespace _3FCompare.Controls;

/// <summary>传输栏（WinForms TransportBar 对应）：播放/停止/双步进/循环/加减路/倍速/色彩模式/时间码。
/// <para>倍速与色彩是两个不产生独立 Popup 的交互件：倍速为点击循环 chip，色彩为"点击后在
/// 同一栏位内展开候选行"的选择器。不用下拉的原因（Avalonia 12.1.1 实测 + 两宿主几何约束）
/// 见 TransportBar.axaml 中 ChipSpeed 处的注释。</para></summary>
public partial class TransportBar : UserControl
{
    // 慢速档（<1.0）已移除：内核原生变速未接线前，伪变速实现只对 >1.0 生效，
    // 更慢的档位会被静默忽略（见 PlaybackCoordinator 的每秒 Seek 逻辑）
    private static readonly double[] Speeds = { 1.0, 2.0, 4.0 };

    private int _speedIndex;      // Speeds 下标；0 = 1.0×
    private int _colorIndex;      // 0=Auto,1=SDR,2=HDR

    /// <summary>播放/暂停图标（docs/31 阶段 4）：<see cref="SetPlaying"/> 只换几何，不换控件。</summary>
    private Path? _playPauseIcon;

    public event EventHandler? PlayPauseClicked;
    public event EventHandler<int>? FrameStepClicked;      // ±1
    public event EventHandler<double>? SecondsStepClicked; // ±seconds
    public event EventHandler? AddClicked;
    public event EventHandler? RemoveClicked;
    public event EventHandler<double>? SpeedChanged;
    public event EventHandler<int>? ColorModeChanged;      // 0=Auto,1=SDR,2=HDR

    public double CurrentSpeed => BtnPlayPause.Tag is double d ? d : 1.0;
    public int CurrentColorMode => _colorIndex;

    /// <summary>以编程方式设置倍速：走与用户点击 chip <b>完全相同</b>的
    /// <see cref="StepSpeed"/> 终点 <see cref="ApplySpeedIndex"/> → <see cref="SpeedChanged"/>
    /// 链路（自测 / 快捷键复用）。
    /// 返回是否命中档位；当前已是该档位时不会触发事件（返回 false）。</summary>
    public bool SetSpeed(double speed)
    {
        var i = Array.IndexOf(Speeds, speed);
        if (i < 0 || i == _speedIndex) return false;
        ApplySpeedIndex(i);
        return true;
    }

    public TransportBar()
    {
        InitializeComponent();
        InitIcons();
        // 倍速 chip 的步进入口。左键/Enter/空格都汇聚到 Button 原生 Click → OnSpeedChipClick，
        // 这里只补 Click 通道覆盖不到的三种输入（真机输入验证过该接线在获焦 Button 上的语义）：
        // 右键与 Shift+左键在 tunnel 吞掉按压后步进 —— Button 的 Click 状态机不会启动；
        // 滚轮在 bubble 截获；方向键在 tunnel 截获（不吃掉会同时触发主窗口的 ←/→ 帧步进）。
        ChipSpeed.AddHandler(InputElement.PointerPressedEvent, OnSpeedChipPressed, RoutingStrategies.Tunnel);
        ChipSpeed.AddHandler(InputElement.PointerWheelChangedEvent, OnSpeedChipWheel, RoutingStrategies.Bubble);
        ChipSpeed.AddHandler(InputElement.KeyDownEvent, OnSpeedChipKeyDown, RoutingStrategies.Tunnel);
        UpdateSpeedChip();  // 常显 1.0×；初值不发事件（与旧 ComboBox 建好即选 0 档时无人订阅等价）
        UpdateColorChip();
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, b => global::Avalonia.Threading.Dispatcher.UIThread.Post(b.ApplyLanguage));
        ApplyLanguage();
    }

    // ---- 响应式收缩 ----
    // 三块区域撑满时约需 1000px，窗口最小 960 会把中间那排播放按钮挤变形。
    // 按「信息量优先级」由低到高丢弃：说明文字 → 色彩模式宽度 → 倍速宽度。
    // 两档宽度与改造前的 ComboBox 完全一致：只换控件形态，不动底栏布局。
    private const double WideThreshold = 1180;
    private const double MediumThreshold = 1020;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var w = e.NewSize.Width;
        if (w <= 0) return;
        TextInfo.IsVisible = w >= WideThreshold;
        ColorSlot.Width = w >= WideThreshold ? 172 : w >= MediumThreshold ? 132 : 104;
        ChipSpeed.Width = w >= MediumThreshold ? 82 : 68;
    }

    /// <summary>装配矢量图标（docs/31 阶段 4）。此前 <c>Content</c> 是 <c>+ − ◀ ▶ ■ 🔁</c> 这类字形，
    /// 字体缺字时会渲染成豆腐块；几何图标与字体无关，颜色一律由 <see cref="AppIcons.Create"/>
    /// 绑到主题令牌（无硬编码颜色）。</summary>
    private void InitIcons()
    {
        BtnAdd.Content = AppIcons.Create("Add");
        BtnRemove.Content = AppIcons.Create("Remove");
        BtnSecPrev.Content = AppIcons.Create("PrevSecond");
        BtnFramePrev.Content = AppIcons.Create("PrevFrame");
        BtnFrameNext.Content = AppIcons.Create("NextFrame");
        BtnSecNext.Content = AppIcons.Create("NextSecond");
        _playPauseIcon = AppIcons.Create("Play");
        BtnPlayPause.Content = _playPauseIcon;
    }

    private void ApplyLanguage()
    {
        RefreshTips();   // 文案只写身份，步进量与键位实时拼（见 RefreshTips）
        ToolTip.SetTip(BtnAdd, LanguageManager.T("Tb_Add"));
        ToolTip.SetTip(BtnRemove, LanguageManager.T("Tb_Remove"));
        ToolTip.SetTip(ChipSpeed, LanguageManager.T("Tb_Speed"));
        ToolTip.SetTip(ChipColor, LanguageManager.T("Tb_ColorMode"));
        BtnColorAuto.Content = ColorLabel(0);
        BtnColorSdr.Content = ColorLabel(1);
        BtnColorHdr.Content = ColorLabel(2);
        UpdateColorChip(); // chip 常显文案也要随语言切换刷新
    }

    // ---------- 倍速 chip ----------

    private void OnSpeedChipClick(object? sender, RoutedEventArgs e) => StepSpeed(+1);

    private void OnSpeedChipPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(ChipSpeed).Properties;
        if (!p.IsRightButtonPressed &&
            !(p.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            return; // 普通左键：留给 Button 自己的 Click 通道

        StepSpeed(-1);
        // 实测（Avalonia 12.1.1，真机注入）：tunnel 里 Handled 后 Button 的按压状态机不启动，
        // 不会再补发一次 Click —— 后退不会被前进抵消。
        e.Handled = true;
    }

    private void OnSpeedChipWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        StepSpeed(e.Delta.Y > 0 ? +1 : -1);
        e.Handled = true; // 悬停步进到此为止，不再携带别处的滚动/缩放语义
    }

    private void OnSpeedChipKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter/空格刻意不在这里步进：获焦 Button 会把它们转成恰好一次原生 Click（= +1，已实测），
        // 这里再算就成了双击。只接管没有 Click 通道的方向键。
        int d = e.Key switch
        {
            Key.Right or Key.Up => +1,
            Key.Left or Key.Down => -1,
            _ => 0,
        };
        if (d == 0) return;
        StepSpeed(d);
        e.Handled = true; // 不吃掉的话主窗口的 ←/→ 全局帧步进会跟着一起触发
    }

    /// <summary>步进并回绕（档位表 = <see cref="Speeds"/>，不另写常量）。
    /// 末档 4× 再前进一档即回 1× —— 这就是「回到 1×」的路径；
    /// 右击 / Shift+左击 / 滚轮下拨则逐级后退，同样能回到 1×。</summary>
    private void StepSpeed(int delta)
    {
        var n = Speeds.Length;
        ApplySpeedIndex(((_speedIndex + delta) % n + n) % n);
    }

    private void ApplySpeedIndex(int i)
    {
        if (i == _speedIndex) return;
        _speedIndex = i;
        var speed = Speeds[i];
        UpdateSpeedChip();
        BtnPlayPause.Tag = speed;
        SpeedChanged?.Invoke(this, speed);
    }

    private void UpdateSpeedChip()
    {
        ChipSpeed.Content = FormatSpeed(Speeds[_speedIndex]);
        // 非 1× 用与循环按钮同款的"激活"令牌底（ThemePalette 动态绑定，无硬编码颜色）。
        if (_speedIndex == 0) ChipSpeed.ClearValue(Button.BackgroundProperty);
        else ThemePalette.SetBrush(ChipSpeed, Button.BackgroundProperty, "ButtonActiveBrush");
    }

    /// <summary>当前值常显：1.0× / 2.0× / 4.0×（U+00D7 乘号，非字形依赖）。</summary>
    private static string FormatSpeed(double speed) => $"{speed:0.0}\u00d7";

    // ---------- 色彩/SDR 选择器（栏内展开候选行） ----------

    private static string ColorLabel(int i) => i switch
    {
        0 => LanguageManager.T("Color_Auto"),
        1 => "SDR",
        _ => "HDR",
    };

    private void OnColorChipClick(object? sender, RoutedEventArgs e) => SetColorChoicesOpen(true);
    private void OnColorAutoClick(object? sender, RoutedEventArgs e) => PickColor(0);
    private void OnColorSdrClick(object? sender, RoutedEventArgs e) => PickColor(1);
    private void OnColorHdrClick(object? sender, RoutedEventArgs e) => PickColor(2);

    private void SetColorChoicesOpen(bool open)
    {
        ChipColor.IsVisible = !open;
        ColorChoices.IsVisible = open;
    }

    /// <summary>选一档并收起候选行；重复选当前档只收起、不发事件（与旧 ComboBox 同语义）。</summary>
    private void PickColor(int i)
    {
        var changed = i != _colorIndex;
        _colorIndex = i;
        UpdateColorChip();
        SetColorChoicesOpen(false);
        if (changed) ColorModeChanged?.Invoke(this, i);
    }

    private void UpdateColorChip()
    {
        ChipColor.Content = ColorLabel(_colorIndex);
        PaintChoice(BtnColorAuto, _colorIndex == 0);
        PaintChoice(BtnColorSdr, _colorIndex == 1);
        PaintChoice(BtnColorHdr, _colorIndex == 2);
    }

    private static void PaintChoice(Button b, bool selected)
    {
        if (selected) ThemePalette.SetBrush(b, Button.BackgroundProperty, "ButtonActiveBrush");
        else b.ClearValue(Button.BackgroundProperty);
    }

    /// <summary>设置色彩模式（0=Auto,1=SDR,2=HDR），不触发事件。</summary>
    public void SetColorMode(int index)
    {
        if (index is < 0 or > 2 || index == _colorIndex) return;
        _colorIndex = index;
        UpdateColorChip();
    }

    // ---------- 状态回显 ----------

    private bool _playingHint;

    public void SetPlaying(bool playing)
    {
        _playingHint = playing;
        if (_playPauseIcon is not null)
            _playPauseIcon.Data = AppIcons.Get(playing ? "Pause" : "Play");
        RefreshTips();
    }

    /// <summary>帧步进长度提供者（MainWindow 注入 <c>SyncController.StepProfile.FrameStep</c>）。</summary>
    public Func<int>? FrameStepProvider { get; set; }

    /// <summary>某个动作当前绑定的键位（MainWindow 按用户设置渲染）。
    /// 键位现在是可配置的 ⇒ 把键名写死在 tooltip 文案里必然变成假话。</summary>
    public Func<Services.TransportKeys.Slot, string>? KeyHintProvider { get; set; }

    /// <summary>重算全部传输按钮的 tooltip。语言切换、设置采纳、播放态变化都要走这里。</summary>
    public void RefreshTips()
    {
        var frames = (FrameStepProvider?.Invoke() ?? 1).ToString();
        var seconds = (StepProfileSecondsProvider?.Invoke() ?? 1.0).ToString("0.##");
        SetStepTip(BtnSecPrev, "Tb_SecPrevFmt", seconds, Services.TransportKeys.Slot.StepSecondBackward);
        SetStepTip(BtnFramePrev, "Tb_FramePrevFmt", frames, Services.TransportKeys.Slot.StepFrameBackward);
        SetStepTip(BtnSecNext, "Tb_SecNextFmt", seconds, Services.TransportKeys.Slot.StepSecondForward);
        SetStepTip(BtnFrameNext, "Tb_FrameNextFmt", frames, Services.TransportKeys.Slot.StepFrameForward);
        ToolTip.SetTip(BtnPlayPause, BuildTip(LanguageManager.T(_playingHint ? "Tb_Pause" : "Tb_Play"),
            Services.TransportKeys.Slot.PlayPause));
    }

    /// <summary>自测取 tooltip 用的三个按钮（保持字段私有）。</summary>
    public (global::Avalonia.Controls.Control secPrev,
            global::Avalonia.Controls.Control framePrev,
            global::Avalonia.Controls.Control playPause) TipProbeTargets() => (BtnSecPrev, BtnFramePrev, BtnPlayPause);

    /// <summary>读某个按钮当前的 tooltip 文本（自测用它核对"文案有没有跟着设置走"）。</summary>
    public string TipTextOf(global::Avalonia.Controls.Control c) => ToolTip.GetTip(c) as string ?? string.Empty;

    private void SetStepTip(global::Avalonia.Controls.Control c, string labelKey, string amount,
        Services.TransportKeys.Slot slot) =>
        ToolTip.SetTip(c, BuildTip(string.Format(LanguageManager.T(labelKey), amount), slot));

    private string BuildTip(string body, Services.TransportKeys.Slot slot) =>
        string.Format(LanguageManager.T("Tb_TipFmt"), body,
            KeyHintProvider?.Invoke(slot) ?? LanguageManager.T("Keys_Unbound"));

    // 2026-09-26：底栏的【循环】按钮按用户要求撤掉，循环区间仍由时间轴标记与剪辑功能设置
    // （`SyncController.LoopEnabled` 的读写方不变），所以这里不再有 SetLoop / 高亮态。

    private string _lastTimecode = string.Empty;

    /// <summary>PR 风格时间码：HH:MM:SS:FF / HH:MM:SS。同值跳过 UI 更新。</summary>
    public void SetTime(TimeSpan pos, TimeSpan dur, int frameInSecond)
    {
        var tc = $"{pos:hh\\:mm\\:ss}:{frameInSecond:D2}";
        if (tc == _lastTimecode) return;
        _lastTimecode = tc;
        TextTime.Text = $"{tc} / {dur:hh\\:mm\\:ss}";
    }

    public void SetInfo(string? info) => TextInfo.Text = info ?? string.Empty;

    // ---------- 按钮事件 ----------

    private void OnPlayPause(object? sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);
    private void OnFramePrev(object? sender, RoutedEventArgs e) => FrameStepClicked?.Invoke(this, -1);
    private void OnFrameNext(object? sender, RoutedEventArgs e) => FrameStepClicked?.Invoke(this, 1);
    private void OnSecPrev(object? sender, RoutedEventArgs e) => SecondsStepClicked?.Invoke(this, -CurrentStepSeconds());
    private void OnSecNext(object? sender, RoutedEventArgs e) => SecondsStepClicked?.Invoke(this, CurrentStepSeconds());

    private double CurrentStepSeconds() => StepProfileSecondsProvider?.Invoke() ?? 1.0;

    /// <summary>秒步进长度提供者（MainWindow 注入 SyncController.StepProfile.SecondsStep）。</summary>
    public Func<double>? StepProfileSecondsProvider { get; set; }

    private void OnAdd(object? sender, RoutedEventArgs e) => AddClicked?.Invoke(this, EventArgs.Empty);
    private void OnRemove(object? sender, RoutedEventArgs e) => RemoveClicked?.Invoke(this, EventArgs.Empty);
}
