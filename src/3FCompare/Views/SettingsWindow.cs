using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using global::Avalonia.Platform.Storage;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Diagnostics;
using _3FCompare.Services;

namespace _3FCompare.Views;

/// <summary>设置窗口（WinForms SettingsDialog 对应，8 节）：
/// 语言/硬件加速+GPU/步进/快捷键/窗口全屏/解码色彩/布局/FFmpeg 路径+检测。
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
    private readonly ComboBox _align = new();

    // ---- 「快捷键」节 ----

    /// <summary>本节的 6 行（顺序 = <see cref="TransportKeys.Slots"/> = 冲突优先级 = 底栏从左到右）。</summary>
    private readonly List<KeyRow> _keyRows = new();

    /// <summary>本节的状态行：改键结果与拒绝原因都写在这里（沿用 _ffmpegStatus 那一套
    /// "文字 + ErrorBrush/SuccessBrush"的可见提示模式，不做静默失败）。</summary>
    private readonly TextBlock _keyStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>当前处于"等待按键"状态的那一行。同一时刻只允许一行在捕获，
    /// 否则两个按钮都会吃掉自己的按键、用户分不清在改哪一个。</summary>
    private KeyRow? _capturing;

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
            AutoEnterCompare = current.AutoEnterCompare,
            CompareAlign = current.CompareAlign,
            Language = current.Language,
            // ⚠ 深拷贝而不是引用直传：见 CopyBindings 的注释
            KeyBindings = CopyBindings(current.KeyBindings),
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
        _colorMode.Items.Add(LanguageManager.T("Color_Auto_Long"));
        _colorMode.Items.Add(LanguageManager.T("Color_SDR"));
        _colorMode.Items.Add(LanguageManager.T("Color_HDRAuto"));
        _colorMode.SelectedIndex = current.ColorMode == ColorModeSetting.Auto ? 0
            : current.ColorMode == ColorModeSetting.MapToHdr ? 2 : 1;
        _cols.Value = current.DefaultGridCols;
        _rows.Value = current.DefaultGridRows;
        // 分辨率对齐模式：两项与 CompareAlign 的取值一一对应（0=相对 / 1=像素级）。
        // 顺序即枚举序，故 SelectedIndex 可直接当枚举值用（读取处已钳位）。
        _align.Items.Add(LanguageManager.T("Align_Relative"));
        _align.Items.Add(LanguageManager.T("Align_Pixel"));
        _align.SelectedIndex = Math.Clamp(current.CompareAlign, 0, 1);
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
        // 紧跟「步进」节：步进量和触发它的键位是同一件事的两半，拆开放会让人找不到。
        stack.Children.Add(BuildKeysSection());
        stack.Children.Add(Section(LanguageManager.T("Window_StartFullscreen"),
            Row(_startFullscreen, _hideChrome)));
        stack.Children.Add(Section(LanguageManager.T("Vrr_SectionTitle"),
            _vrrTearing,
            Hint(LanguageManager.T("Vrr_TearingHint"))));
        stack.Children.Add(Section(LanguageManager.T("Scrub_SectionTitle"),
            _scrubPreview, _minimap, Hint(LanguageManager.T("Scrub_PreviewHint"))));
        stack.Children.Add(Section(LanguageManager.T("Status_Color"),
            Row(_colorMode)));
        stack.Children.Add(Section(LanguageManager.T("Align_SectionTitle"),
            Row(Label(LanguageManager.T("Align_Mode")), _align),
            Hint(LanguageManager.T("Align_Hint"))));
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

    // ══════════ 「快捷键」节 ══════════

    /// <summary>整节一次性构造成一个 Section 面板（标题 + 6 行 + 提示 + 恢复默认 + 状态行）。
    /// <para>刻意做成"一个方法交出一整节"：与本文件其它节（外观 / 分辨率对齐 / FFmpeg）同形态，
    /// 不往别的面板上零散插行。</para></summary>
    private Control BuildKeysSection()
    {
        var children = new List<Control>();
        foreach (var slot in TransportKeys.Slots)
        {
            // 初值走一次 Canonical：行里的字符串与"按一次键写出来的字符串"必须同形，
            // 否则光打开设置又点确定就会被判成"改动过"（白写一次盘，还让人怀疑自己改坏了）
            var row = new KeyRow(this, slot,
                TransportKeys.Canonical(TransportKeys.Get(slot, _orig.KeyBindings)));
            _keyRows.Add(row);
            children.Add(Row(row.Title, row.Capture, row.Clear));
        }
        children.Add(Hint(LanguageManager.T("Keys_Hint")));

        var reset = new Button { Content = LanguageManager.T("Keys_Reset"), Height = 26 };
        reset.Click += (_, _) => ResetKeyRows();
        children.Add(reset);
        children.Add(_keyStatus);
        return Section(LanguageManager.T("Keys_SectionTitle"), children.ToArray());
    }

    /// <summary>「恢复默认」：只改界面上的 6 行，点「确定」才落盘 ——
    /// 本窗口没有"立刻生效"的第二套语义（主题那处例外是刻意设计，见 _origTheme）。</summary>
    private void ResetKeyRows()
    {
        foreach (var row in _keyRows)
        {
            EndCapture(row);
            row.Value = TransportKeys.Canonical(TransportKeys.DefaultOf(row.Slot));
            row.Refresh();
        }
        ShowKeyStatus(LanguageManager.T("Keys_ResetDone"), error: false);
    }

    /// <summary>把结果/拒绝原因写到本节的状态行（可见提示；沿用 FFmpeg 状态行的配色约定）。</summary>
    private void ShowKeyStatus(string text, bool error)
    {
        _keyStatus.Text = text;
        ThemePalette.SetBrush(_keyStatus, TextBlock.ForegroundProperty,
            error ? "ErrorBrush" : "SuccessBrush");
    }

    /// <summary>某一行进入"等待按键"。同一时刻只允许一行在捕获，否则两个按钮都会吃键，
    /// 用户分不清现在按下去改的是哪一个。</summary>
    private void BeginCapture(KeyRow row)
    {
        if (_capturing is not null && !ReferenceEquals(_capturing, row)) EndCapture(_capturing);
        _capturing = row;
        row.BeginCaptureVisual();
        row.Capture.Focus();
        ShowKeyStatus(LanguageManager.T("Keys_CapturingHint"), error: false);
    }

    private void EndCapture(KeyRow? row)
    {
        if (row is null) return;
        if (ReferenceEquals(_capturing, row)) _capturing = null;
        row.EndCaptureVisual();
    }

    /// <summary>清除绑定：写入空串 = 未绑定，分派侧的规则是"无绑定的动作不参与分派"
    /// （见 <c>TransportKeys.BuildMap</c> 规则 ①）。</summary>
    private void ClearBinding(KeyRow row)
    {
        EndCapture(row);
        row.Value = string.Empty;
        row.Refresh();
        ShowKeyStatus(LanguageManager.Tf("Keys_ClearedFmt",
            LanguageManager.T(TransportKeys.LabelKey(row.Slot))), error: false);
    }

    /// <summary>捕获态下按下一个键：要么接受（写进该行的 Value），要么**拒绝并说明理由**。
    /// 两条出口都会退出捕获态并把状态行填满 —— 静默失败是本仓库不接受的行为
    /// （用户下一次只会看到"还是旧键"，不知道刚才那次为什么不算）。</summary>
    private void OnCaptureKeyDown(KeyRow row, Key key, KeyModifiers mods)
    {
        if (!ReferenceEquals(_capturing, row)) return;   // 已不在捕获态：不受理
        var display = TransportKeys.Display(key, mods);

        // ① 固定键位：MainWindow.OnKeyDown 里那些不来自设置的分支（O/B/C/S/V/G/T/P/R/H/
        //    F11/Escape/上下键/F6/Delete/D1..D9）。硬编码优先是结构性的（只在 default 臂查表），
        //    所以这里绑了也不会生效 ⇒ 当场拒绝，而不是让用户去猜。
        if (TransportKeys.IsReserved(key, mods))
        {
            EndCapture(row);
            ShowKeyStatus(LanguageManager.Tf("Keys_ErrReserved", display), error: true);
            return;
        }

        // ② 与本节另一行重复：Core 的 Normalize 也会消解冲突（按槽序先声明者赢），
        //    但那是"加载时兜底"，不该拿来当界面规则 —— 到这里就拒绝，冲突进不了配置文件。
        var spec = TransportKeys.ToSpec(key, mods);
        var clash = _keyRows.FirstOrDefault(r => !ReferenceEquals(r, row) &&
            string.Equals(r.Value, spec, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            EndCapture(row);
            ShowKeyStatus(LanguageManager.Tf("Keys_ErrDuplicate", display,
                LanguageManager.T(TransportKeys.LabelKey(clash.Slot))), error: true);
            return;
        }

        row.Value = spec;
        row.MarkCommitted();
        EndCapture(row);
        row.Refresh();
        ShowKeyStatus(LanguageManager.Tf("Keys_BoundFmt", display), error: false);
    }

    /// <summary>把界面上 6 行的值收集成一份新的键位表（供 Result 使用）。
    /// 末尾走一遍 <see cref="KeyBindingsSettings.Normalize"/>：与配置文件同一道校验，
    /// 不另造第二条路径（界面侧已经拒绝过冲突与固定键位，这里正常是空操作）。</summary>
    private KeyBindingsSettings ReadKeyBindings()
    {
        var bindings = new KeyBindingsSettings();
        foreach (var row in _keyRows) TransportKeys.Set(row.Slot, bindings, row.Value);
        bindings.Normalize();
        return bindings;
    }

    /// <summary>键位表深拷贝。<see cref="AppSettings.KeyBindings"/> 是引用类型，
    /// 把 current 的实例直接存进 _orig / Result 会让三方共享同一个对象 ——
    /// 用户在对话框里改一半又点「取消」，主窗口的 _settings 却已经被改掉并当场生效。</summary>
    private static KeyBindingsSettings CopyBindings(KeyBindingsSettings? from)
    {
        var to = new KeyBindingsSettings();
        if (from is not null) TransportKeys.CopyInto(from, to);
        return to;
    }

    /// <summary>「快捷键」节的一行：动作标签 + 显示当前键的按钮（点击进入等待按键）+ 清除绑定。
    ///
    /// <para><b>为什么用 tunnel 处理器读键，而不是覆写 OnKeyDown</b>：获焦的 Button 会把
    /// 空格/回车转成恰好一次原生 Click（本仓库为此踩过"点过按钮后空格不再切播放/暂停"），
    /// 捕获态必须在那之前把键截走 —— tunnel 里置 Handled 后按钮的按压状态机不会启动，
    /// 同一手法见 <c>Controls/TransportBar.axaml.cs</c> 的倍速 chip。</para></summary>
    private sealed class KeyRow
    {
        public readonly TransportKeys.Slot Slot;
        public readonly TextBlock Title;
        public readonly Button Capture;
        public readonly Button Clear;

        /// <summary>当前绑定值（键名字符串，"" = 未绑定）。落盘时由 ReadKeyBindings 统一收集。</summary>
        public string Value { get; set; }

        private readonly SettingsWindow _owner;
        private bool _capturing;

        /// <summary>上一次"按键提交成功"的时刻。用于吞掉框架可能在 KeyUp 补发的那一次 Click
        /// （否则表现为"刚设好键，按钮又回到等待按键状态"）。300ms 远短于人有意识的再点一次。</summary>
        private long _committedAtTicks;

        public KeyRow(SettingsWindow owner, TransportKeys.Slot slot, string value)
        {
            _owner = owner;
            Slot = slot;
            Value = value;

            Title = new TextBlock
            {
                Text = LanguageManager.T(TransportKeys.LabelKey(slot)),
                FontSize = 12,
                Width = 168,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ThemePalette.SetBrush(Title, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            Capture = new Button
            {
                Width = 112,   // 固定宽度：文案在「←」与「按下新按键…」之间切换时整行不能跳
                Height = 26,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Capture.Click += (_, _) =>
            {
                if (_capturing) { _owner.EndCapture(this); return; }  // 再点一次 = 取消
                if (Environment.TickCount64 - _committedAtTicks < 300) return;
                _owner.BeginCapture(this);
            };
            // 捕获态的按键在 Button 自己处理之前截走（见类型注释）
            Capture.AddHandler(InputElement.KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
            // 焦点跑掉就等于不想改了：继续挂着"等待按键"只会让人以为界面卡住
            Capture.LostFocus += (_, _) => _owner.EndCapture(this);

            Clear = new Button { Content = LanguageManager.T("Keys_Clear"), Height = 26 };
            Clear.Click += (_, _) => _owner.ClearBinding(this);

            Refresh();
        }

        private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            // 捕获期间这颗键整个归这里：既不给 Button 转 Click，也不给别的焦点控件（方向键会动滑块）
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                _owner.EndCapture(this);      // Esc = 取消捕获（它同时是"退出全屏"的固定键，不可绑）
                _owner.ShowKeyStatus(LanguageManager.T("Keys_Cancelled"), error: false);
                return;
            }
            if (IsModifierOnly(e.Key)) return;   // 只按下了修饰键：继续等真正那颗键（组合键按 Ctrl+X 一次成）
            _owner.OnCaptureKeyDown(this, e.Key, e.KeyModifiers);
        }

        /// <summary>这颗键本身就是修饰键 ⇒ 不能单独作为绑定（它总是和别的键一起出现）。</summary>
        private static bool IsModifierOnly(Key key) => key is
            Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

        /// <summary>按当前状态刷新：按钮文字（等待按键 / 当前键位）、清除按钮的可见性。</summary>
        public void Refresh()
        {
            Capture.Content = _capturing
                ? LanguageManager.T("Keys_Capturing")
                : TransportKeys.Display(Value);
            // 未绑定时没有"清除"可点（少一个按了没反应的按钮）
            Clear.IsVisible = Value.Length > 0;
        }

        public void BeginCaptureVisual()
        {
            if (_capturing) return;
            _capturing = true;
            Refresh();
        }

        public void EndCaptureVisual()
        {
            if (!_capturing) return;
            _capturing = false;
            Refresh();
        }

        /// <summary>提交成功（区别于 Esc/失焦退出）：只有这条路会给"补发的 Click"上闸门。</summary>
        public void MarkCommitted() => _committedAtTicks = Environment.TickCount64;
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
        // 对齐模式：越界下标（列表项数变化 / 脏初值）会被静默当成某一档，故显式钳一次，
        // 与 AppSettings.Normalize 的 [0,1] 保持一致。
        var align = Math.Clamp(_align.SelectedIndex, 0, 1);
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

        // 快捷键：先收集再比较，只走一次（ReadKeyBindings 里的 Normalize 可能写日志，
        // 调用两次等于同一件事报两遍，且两次的值理论上还可能被第二遍的清洗改掉）
        var bindings = ReadKeyBindings();
        var changed = lang != _orig.Language || hw != _orig.HardwareDecode || adapter != _orig.PreferredAdapterIndex
            || frame != _orig.FrameStep || Math.Abs(sec - _orig.SecondsStep) > 0.001
            || startFs != _orig.StartFullscreen || hideChrome != _orig.HideChromeInFullscreen
            || color != _orig.ColorMode || cols != _orig.DefaultGridCols || rows != _orig.DefaultGridRows
            || vrrTearing != _orig.VrrTearingPresent
            || scrubPreview != _orig.ScrubPreviewEnabled || miniMap != _orig.MinimapEnabled
            // ⚠ 新设置项必须加进这条链：漏了它 Changed 恒为 false ⇒ 改了设置"确定"后
            // MainWindow 直接 return（Result 不落地），表现为"设置存不住"且无任何报错。
            || align != _orig.CompareAlign
            // 快捷键：键位表是引用类型，必须逐槽比而不是比引用（Same 见 TransportKeys）
            || !TransportKeys.Same(_orig.KeyBindings, bindings)
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
                AutoEnterCompare = _orig.AutoEnterCompare,
                CompareAlign = align,
                Language = lang,
                // 局部实例，与 _orig / 调用方的 _settings 都不共享（见 CopyBindings）
                KeyBindings = bindings,
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
