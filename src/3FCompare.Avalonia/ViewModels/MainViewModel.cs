using CommunityToolkit.Mvvm.ComponentModel;
using _3FCompare.App;

namespace _3FCompare.Avalonia.ViewModels;

/// <summary>主窗体状态：承接 WinForms MainForm 的状态字段（M1 骨架集；
/// M2 接入播放后扩充播放/时间轴相关属性）。</summary>
public partial class MainViewModel : ObservableObject
{
    // ---- 引擎 / 状态栏 ----

    [ObservableProperty]
    private string _engineLabel = LanguageManager.T("Status_EngineDemo");

    [ObservableProperty]
    private string _statusInfo = LanguageManager.T("Status_ReadyOpen");

    [ObservableProperty]
    private bool _isRealMode;

    // ---- 播放状态（M2 接线）----

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _loopEnabled;

    [ObservableProperty]
    private string _timecode = "00:00:00:00 / 00:00:00";

    // ---- 布局状态 ----

    [ObservableProperty]
    private bool _singleView;

    [ObservableProperty]
    private int _laneCount;

    [ObservableProperty]
    private double _speed = 1.0;

    /// <summary>状态栏信息：网格/单屏 + 路数（WinForms UpdateStatus 等价）。</summary>
    public void UpdateLayoutStatus(bool singleView, int lanes, int failed, string? runtimeError)
    {
        var parts = new List<string>
        {
            singleView ? LanguageManager.T("Status_SingleMode") : LanguageManager.T("Status_GridMode"),
            $"{lanes}/9",
        };
        if (failed > 0)
            parts.Add($"✗{failed}");
        if (!string.IsNullOrEmpty(runtimeError))
            parts.Add(runtimeError);
        StatusInfo = string.Join("  |  ", parts);
    }
}
