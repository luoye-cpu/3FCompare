using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using _3FCompare.Core.Backend;
using _3FCompare.Localization;

namespace _3FCompare;

/// <summary>HDR 动态元数据注入的 UI 接线（2026-10-10 夜，接续通用注入通道内核 + 托管封装）。
/// <para>为什么从 MainWindow.axaml.cs 拆出来：该类已 3744 行，且这里的逻辑自成一块
/// （选文件 → 解析 → 扇出 → 提示），与"打开视频""保存会话"并非同一职责；
/// 拆出的样板见 MainWindow.Capture.cs（Menu_ExportFrame 的处理器就住在那里）。</para>
///
/// <para><b>为什么扇出到所有路而不是只作用于当前选中路</b>：本软件的核心场景是
/// "同一素材的两个编码版本逐帧对比"。动态元数据决定色调映射曲线，若只给一路注入，
/// 两路就会跑不同的曲线，亮度不再可比——与 <c>OnColorModeChanged</c> 里
/// "取并集统一色调映射"（P1-8）是同一个道理。所以注入/清除一律对所有活动路生效。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>已注入的外部元数据文件路径（null = 当前无注入）。仅用于提示与状态显示。</summary>
    private string? _dynamicMetadataPath;

    /// <summary>已注入的条目数（0 = 当前无注入）。</summary>
    private int _dynamicMetadataCount;

    /// <summary>「文件 → 载入动态元数据…」：选一个 JSON → 解析并校验 → 扇出注入所有路。</summary>
    private async void OnLoadDynamicMetadata(object? sender, RoutedEventArgs e)
    {
        // D1：async void 必须有异常边界（与 OnOpenVideos 同规），
        // 否则 StorageProvider 抛异常会直接冒泡到 Dispatcher 并闪退。
        try
        {
            if (_sync.Slots.Count == 0)
            {
                StatusInfo.Text = LanguageManager.T("Status_DmNeedMedia");
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageManager.T("Menu_LoadDynamicMetadata"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Dynamic Metadata") { Patterns = new[] { "*.json" } },
                    FilePickerFileTypes.All,
                },
            });
            var path = files?.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return; // 用户取消

            if (!DynamicMetadataFile.TryLoad(path, out var packets, out var error))
            {
                // 解析失败是"用户给的文件不对"，属可预期的业务失败：说清哪一行哪个字段不对，
                // 而不是抛异常进 ReportErrorAsync（那条路子会记成未处理异常）。
                await Views.MessageBox.Show(this, LanguageManager.T("Msg_OperationFailed"),
                    $"{LanguageManager.T("Msg_DmLoadFailed")}\n\n{error}",
                    LanguageManager.T("Settings_Ok"));
                return;
            }

            var table = packets!;
            var laneTotal = _sync.Slots.Count;
            var applied = ApplyToAllSessions(s => s.SetDynamicMetadata(table));

            _dynamicMetadataPath = applied > 0 ? path : null;
            _dynamicMetadataCount = applied > 0 ? table.Count : 0;

            StatusInfo.Text = $"{DescribeInjectionResult(applied, laneTotal, table.Count)}"
                + (applied > 0 ? $" — {Path.GetFileName(path)}" : string.Empty);
            RefreshDynamicMetadataPanel();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("DynamicMetadata", LanguageManager.T("Msg_DmLoadFailed"), ex);
        }
    }

    /// <summary>「文件 → 清除注入的动态元数据」：扇出清除所有路（kind=None 清全部 kind）。</summary>
    private void OnClearDynamicMetadata(object? sender, RoutedEventArgs e)
    {
        if (_dynamicMetadataCount == 0 && _dynamicMetadataPath is null)
        {
            StatusInfo.Text = LanguageManager.T("Status_DmClearNone");
            return;
        }

        var laneTotal = _sync.Slots.Count;
        var applied = ApplyToAllSessions(s => s.ClearDynamicMetadata(DynamicMetadataKind.None));
        _dynamicMetadataPath = null;
        _dynamicMetadataCount = 0;

        StatusInfo.Text = applied > 0
            ? LanguageManager.Tf("Status_DmCleared", applied, laneTotal)
            : LanguageManager.T("Status_DmClearNone");
        RefreshDynamicMetadataPanel();
    }

    /// <summary>对每一路施加同一个会话级动作，返回<b>成功</b>的路数。
    /// <para>逐路 try/catch 是刻意的：演示模式（SimulatedEngine）与旧内核都可能在某一路上
    /// 抛/返回 false，一路失败不该拖垮其余路——与 <c>OnColorModeChanged</c> 的扇出同规。</para></summary>
    private int ApplyToAllSessions(Func<IPlayerSession, bool> apply)
    {
        var slots = _sync.Slots; // 副本：遍历期间并发增删不会抛
        var applied = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Failed) continue;
            try
            {
                // false = 内核不支持该导出或整批被拒（旧表续任），同样算"这一路没生效"。
                if (apply(slot.Session)) applied++;
            }
            catch (Exception ex)
            {
                _3FCompare.Core.Diagnostics.AppLog.Warn("DynamicMetadata",
                    $"第 {i} 路动态元数据操作抛出异常并已跳过（{ex.GetType().Name}: {ex.Message}）");
            }
        }
        return applied;
    }

    /// <summary>把注入结果拼成状态栏文案（纯函数：只读三个整数 + 查语言表，不碰 Avalonia、不碰内核）。
    /// <para>internal static 是给 <c>3FCompare.Platform.Tests</c> 留的直连入口
    /// （UI 工程已对它开 InternalsVisibleTo），这样"全成功 / 部分成功 / 全失败"三种措辞
    /// 能被单测钉住，而不必构造一个真实窗口。</para></summary>
    internal static string DescribeInjectionResult(int appliedLanes, int totalLanes, int entryCount)
        => appliedLanes <= 0
            ? LanguageManager.Tf("Status_DmLoadedNone", entryCount, appliedLanes, totalLanes)
            : appliedLanes < totalLanes
                ? LanguageManager.Tf("Status_DmLoadedPartial", entryCount, appliedLanes, totalLanes)
                : LanguageManager.Tf("Status_DmLoadedAll", entryCount, appliedLanes, totalLanes);

    /// <summary>读取某一路的动态元数据来源。null = 内核无此导出 / 该路不可用。
    /// <para>注意这里<b>不</b>用 UI 自己记的注入状态去回答：内核 <c>HdrMetadataSource()</c>
    /// 是 kind 无关的（VideoRenderer.cpp:4741 只看 <c>InjectedMetadataActive()</c>），
    /// 所以 HDR10+ 与 Vivid 注入都会如实回 Injected，清除后自动回 Bitstream——
    /// "注入状态下默认切换过来"由内核保证，UI 不需要另记一份会漂移的影子状态。</para></summary>
    private static DynamicMetadataSource? ReadDynamicMetadataSource(IPlayerSession? session)
    {
        if (session is null) return null;
        try { return session.GetHdrMetadataSource(); }
        catch { return null; } // 演示模式/旧内核：这一行就不显示来源
    }

    /// <summary>注入或清除后刷新媒体信息面板，让「动态元数据来源」行立刻反映新状态。</summary>
    private void RefreshDynamicMetadataPanel()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        var session = slot?.Session;
        _mediaPanel.ShowMediaInfo(session?.ReadMediaInfo(), ReadDynamicMetadataSource(session));
    }
}
