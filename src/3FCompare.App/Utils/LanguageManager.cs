using System.Text;

namespace _3FCompare.App;

/// <summary>双语语言管理器。
/// 提供 <see cref="T"/> 资源查找与 <see cref="LanguageChanged"/> 语言切换通知；
/// 主窗体及各控件订阅该事件以在运行时刷新文本。</summary>
public static class LanguageManager
{
    private static int _currentLanguage = 0; // 0=中文, 1=英文

    /// <summary>语言变更通知（切换语言后触发，监听者据此刷新自身文本）。</summary>
    public static event EventHandler? LanguageChanged;

    public static int CurrentLanguage
    {
        get => _currentLanguage;
        set => SetLanguage(value);
    }

    public static bool IsEnglish => _currentLanguage == 1;

    public static void SetLanguage(int lang)
    {
        if ((lang is 0 or 1) && _currentLanguage != lang)
        {
            _currentLanguage = lang;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    // 中文文本
    private static readonly Dictionary<string, string> Chinese = new()
    {
        // 窗口标题
        ["Settings_DialogTitle"] = "设置",
        // 硬件加速
        ["Hardware_EnableHardwareDecode"] = "启用硬件解码 (GPU)",
        ["Hardware_DecodeGPU"] = "解码 GPU：",
        ["Hardware_GPUAuto"] = "系统默认",

        // 步进
        ["Stepping_StepByFrame"] = "按帧步进步长：",
        ["Stepping_StepBySecond"] = "按秒步进步长：",

        // 窗口
        ["Window_StartFullscreen"] = "启动时进入全屏模式",
        ["Window_HideChrome"] = "全屏时隐藏时间轴/工具栏",

        // 色彩
        ["Color_ColorMode"] = "色彩模式：",
        ["Color_SDR"] = "SDR 输出",
        ["Color_HDRAuto"] = "HDR 输出 (自动检测)",

        // 布局
        ["Layout_DefaultCols"] = "默认网格列数：",
        ["Layout_DefaultRows"] = "默认网格行数：",

        // FFmpeg
        ["FFmpeg_Path"] = "FFmpeg DLL 目录：",
        ["FFmpeg_Browse"] = "浏览…",
        ["FFmpeg_Hint"] = "手动设置（优先）> 自动检测（应用目录 / PATH）",
        ["FFmpeg_Test"] = "测试探测",

        // 设置
        ["Settings_Ok"] = "确定",
        ["Settings_Cancel"] = "取消",

        // 系统提示
        ["Msg_FolderTitle"] = "选择包含 FFmpeg DLL 的目录",
        ["Msg_AutoDetect"] = "留空 = 自动检测（FFMPEG_DIR / PATH / 应用目录）",
        ["Msg_ValidateSuccess"] = "✓ 目录有效",
        ["Msg_ValidateFailed"] = "✗ 目录无效",
        ["Msg_AutoDetectSuccess"] = "✓ 自动检测到：",
        ["Msg_AutoDetectFailed"] = "当前未找到 FFmpeg",
        ["Msg_NativeLoadSuccess"] = "✓ FFF.Native 加载成功，FFmpeg 可用",
        ["Msg_NativeLoadFailed"] = "✗ FFF.Native 加载失败",

        // ---- 窗口 / 状态栏 / 菜单 / 侧边栏（0.1.4 双语完善新增）----
        ["Win_Title"] = "3FCompare – ICAT 类视频盯帧对比",
        ["Menu_File"] = "文件(&F)",
        ["Menu_Open"] = "打开视频…",
        ["Menu_SaveSession"] = "保存会话…",
        ["Menu_LoadSession"] = "加载会话…",
        ["Menu_ExportFrame"] = "导出当前帧 PNG… (Ctrl+S)",
        ["Menu_Exit"] = "退出",
        ["Menu_View"] = "视图(&V)",
        ["Menu_ToggleSingle"] = "单屏/多屏切换",
        ["Menu_Fullscreen"] = "全屏切换 (F11)",
        ["Menu_AbSlider"] = "A-B 滑块视图",
        ["Menu_Probe"] = "像素探针",
        ["Menu_Bookmarks"] = "书签",
        ["Menu_Offset"] = "偏移校准 (F6)",
        ["Menu_MediaInfo"] = "媒体信息",
        ["Menu_Diff"] = "差异叠加",
        ["Menu_Audio"] = "音频",
        ["Menu_ShowGrid"] = "显示 对比网格",
        ["Menu_GridLayout"] = "网格布局",
        ["Menu_Grid_2x1"] = "2×1（默认）",
        ["Menu_Grid_2x2"] = "2×2",
        ["Menu_Grid_3x3"] = "3×3",
        ["Menu_Grid_Auto"] = "自动",
        ["Menu_Settings"] = "设置(&S)",
        ["Menu_SettingsDialog"] = "设置…",
        ["Menu_Settings_Lang"] = "语言",
        ["Status_EngineReal"] = "引擎: FFF.Native (3FP)",
        ["Status_EngineDemo"] = "引擎: 演示模式 (Simulated)",
        ["Status_ReadyOpen"] = "就绪 — 点击「打开视频」或拖拽文件",
        ["Status_Ready"] = "就绪 — 打开视频开始对比",
        ["Status_DemoHint"] = "演示模式 — 打开任意视频文件体验（画面为合成）",
        ["Status_GridMode"] = "网格",
        ["Status_SingleMode"] = "单屏",
        ["Status_Color"] = "色彩",
        ["Status_ExportDone"] = "已导出截图",
        ["Status_Steps"] = "步进",
        ["Tab_Probe"] = "探针",
        ["Tab_Bookmarks"] = "书签",
        ["Tab_Offset"] = "偏移",
        ["Tab_Media"] = "媒体",
        ["Tab_Audio"] = "音频",
        ["Mag_Magnifier"] = "放大镜",
        ["Tb_Play"] = "播放 (Space)",
        ["Tb_Pause"] = "暂停 (Space)",
        ["Tb_Stop"] = "停止",
        ["Tb_FramePrev"] = "后退一帧 (←)",
        ["Tb_FrameNext"] = "前进一帧 (→)",
        ["Tb_SecPrev"] = "后退一秒 (Shift+←)",
        ["Tb_SecNext"] = "前进一秒 (Shift+→)",
        ["Tb_LoopOn"] = "循环: 开",
        ["Tb_LoopOff"] = "循环: 关",
        ["Tb_Speed"] = "播放速度",
        ["Tb_Add"] = "加路",
        ["Tb_Remove"] = "减路",
        ["Tb_ColorMode"] = "SDR: 标准动态范围输出 | HDR: 高动态范围输出（自动检测显示器能力）",
        ["Probe_Title"] = "像素探针",
        ["Probe_Coord"] = "坐标: --",
        ["Probe_Mode"] = "色彩模式: --",
        ["Probe_Hint"] = "鼠标悬停视频面以读取像素\n（显示颜色管理前码值）",
        ["Probe_ReadFail"] = "读取失败",
        ["Probe_Bits"] = "码值(8位)",
        ["Probe_MapSdr"] = "映射SDR",
        ["Probe_RawHdr"] = "原始HDR",
        ["Probe_PeakHdr"] = "峰值HDR",
        ["Bookmark_Title"] = "书签",
        ["Bookmark_NotePlaceholder"] = "备注内容…",
        ["Bookmark_Add"] = "＋ 添加当前帧",
        ["Bookmark_Export"] = "⇩ 导出…",
        ["Bookmark_Col_Time"] = "时间",
        ["Bookmark_Col_Frame"] = "帧号",
        ["Bookmark_Col_Note"] = "备注",
        ["Offset_Title"] = "偏移校准（相对第 1 路）",
        ["Offset_Value"] = "偏移: 0ms (0帧@24fps)",
        ["Offset_Align"] = "◎ 对齐于此帧",
        ["Offset_MsMinus"] = "◀ 100ms",
        ["Offset_MsPlus"] = "100ms ▶",
        ["Offset_FrameMinus"] = "◀ 1帧",
        ["Offset_FramePlus"] = "1帧 ▶",
        ["Offset_Reset"] = "↺ 归零",
        ["Offset_NotSelected"] = "未选中路",
        ["MediaInfo_Title"] = "媒体信息",
        ["MediaInfo_Empty"] = "选中一个已打开的媒体以查看信息",
        ["MediaInfo_File"] = "文件",
        ["MediaInfo_Video"] = "— 视频 —",
        ["MediaInfo_Resolution"] = "分辨率",
        ["MediaInfo_Codec"] = "编码",
        ["MediaInfo_Framerate"] = "帧率",
        ["MediaInfo_PixelFormat"] = "像素格式",
        ["MediaInfo_Color"] = "色彩",
        ["MediaInfo_Hdr"] = "HDR",
        ["MediaInfo_Frames"] = "帧数",
        ["MediaInfo_Lossless"] = "无损",
        ["MediaInfo_Interlaced"] = "交错",
        ["MediaInfo_Yes"] = "是",
        ["MediaInfo_No"] = "否",
        ["MediaInfo_Audio"] = "— 音频 —",
        ["MediaInfo_AudioCodec"] = "编码",
        ["MediaInfo_Channels"] = "声道",
        ["MediaInfo_Container"] = "— 容器 —",
        ["MediaInfo_Format"] = "格式",
        ["MediaInfo_Duration"] = "时长",
        ["MediaInfo_Bitrate"] = "码率",
        ["MediaInfo_FileSize"] = "文件大小",
        ["Audio_Title"] = "音频",
        ["Audio_Track"] = "音轨:",
        ["Audio_Volume"] = "音量:",
        ["Audio_Mute"] = "静音",
        ["Audio_Hint"] = "音轨选择对真实 3FP 会话生效；演示模式为占位。",
        ["Timeline_SetA"] = "设为 A 点",
        ["Timeline_SetB"] = "设为 B 点",
        ["Diff_Need2"] = "差异叠加：需要至少 2 路已打开的媒体",
        ["Diff_HeaderFmt"] = "差异热力图  [{0}] vs [{1}]  （点击刷新）",
        ["Diff_SampleFail"] = "采样失败",
        ["Diff_LegendWeak"] = "弱差异",
        ["Diff_LegendStrong"] = "强差异",
        ["Thumbnail_Hint"] = "拖动时间轴预览帧",
        ["AbSlider_LaneFmt"] = "路 {0}",
        ["Grid_Empty"] = "点击「打开视频」或拖拽文件到此处\n支持 1~9 路对比",
        ["Msg_DiffNeed2"] = "差异叠加需要至少 2 路视频。请先用「打开视频」加载两路。",
        ["Msg_SelectMedia"] = "请先选中一个已打开的媒体",
        ["Msg_CaptureUnavailable"] = "截图失败：窗口帧捕获与像素采样均不可用",
        ["Msg_CaptureFail"] = "截图失败",
        ["Msg_SessionInvalid"] = "会话文件无效或为空",
        ["Msg_AppName"] = "3FCompare",
        ["Filter_Media"] = "媒体文件|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv|所有文件|*.*",
        ["Filter_All"] = "所有文件|*.*",
        ["Filter_Png"] = "PNG 图像|*.png",
        ["Filter_Session"] = "3FCompare 会话|*.3fcs;*.json",
        ["Filter_Bookmark"] = "JSON|*.json|CSV|*.csv",
        ["Audio_TrackFmt"] = "轨 {0}: {1} ({2}ch)",
        ["Audio_NoTrack"] = "无音轨",
    };

    // 英文文本
    private static readonly Dictionary<string, string> English = new()
    {
        ["Settings_DialogTitle"] = "Settings",

        ["Hardware_EnableHardwareDecode"] = "Enable Hardware Decode (GPU)",
        ["Hardware_DecodeGPU"] = "Decode GPU:",
        ["Hardware_GPUAuto"] = "System Default",

        ["Stepping_StepByFrame"] = "Frame Step Size:",
        ["Stepping_StepBySecond"] = "Second Step Size:",

        ["Window_StartFullscreen"] = "Start in Fullscreen Mode",
        ["Window_HideChrome"] = "Hide Timeline/Toolbar in Fullscreen",

        ["Color_ColorMode"] = "Color Mode:",
        ["Color_SDR"] = "SDR Output",
        ["Color_HDRAuto"] = "HDR Output (Auto Detect)",

        ["Layout_DefaultCols"] = "Default Grid Columns:",
        ["Layout_DefaultRows"] = "Default Grid Rows:",

        ["FFmpeg_Path"] = "FFmpeg DLL Directory:",
        ["FFmpeg_Browse"] = "Browse…",
        ["FFmpeg_Hint"] = "Manual (Preferred) > Auto Detect (App Dir / PATH)",
        ["FFmpeg_Test"] = "Test Detection",

        ["Settings_Ok"] = "OK",
        ["Settings_Cancel"] = "Cancel",

        ["Msg_FolderTitle"] = "Select Directory Containing FFmpeg DLL",
        ["Msg_AutoDetect"] = "Leave Empty = Auto Detect (FFMPEG_DIR / PATH / App Dir)",
        ["Msg_ValidateSuccess"] = "✓ Directory Valid",
        ["Msg_ValidateFailed"] = "✗ Invalid Directory",
        ["Msg_AutoDetectSuccess"] = "✓ Auto Detect: ",
        ["Msg_AutoDetectFailed"] = "FFmpeg Not Found",
        ["Msg_NativeLoadSuccess"] = "✓ FFF.Native Loaded Successfully, FFmpeg Available",
        ["Msg_NativeLoadFailed"] = "✗ FFF.Native Load Failed",

        // ---- 窗口 / 状态栏 / 菜单 / 侧边栏（0.1.4 双语完善新增）----
        ["Win_Title"] = "3FCompare – ICAT Video Frame Comparison",
        ["Menu_File"] = "File(&F)",
        ["Menu_Open"] = "Open Videos…",
        ["Menu_SaveSession"] = "Save Session…",
        ["Menu_LoadSession"] = "Load Session…",
        ["Menu_ExportFrame"] = "Export Current Frame PNG… (Ctrl+S)",
        ["Menu_Exit"] = "Exit",
        ["Menu_View"] = "View(&V)",
        ["Menu_ToggleSingle"] = "Toggle Single/Multi View",
        ["Menu_Fullscreen"] = "Toggle Fullscreen (F11)",
        ["Menu_AbSlider"] = "A-B Slider View",
        ["Menu_Probe"] = "Pixel Probe",
        ["Menu_Bookmarks"] = "Bookmarks",
        ["Menu_Offset"] = "Offset Calibration (F6)",
        ["Menu_MediaInfo"] = "Media Info",
        ["Menu_Diff"] = "Diff Overlay",
        ["Menu_Audio"] = "Audio",
        ["Menu_ShowGrid"] = "Show Comparison Grid",
        ["Menu_GridLayout"] = "Grid Layout",
        ["Menu_Grid_2x1"] = "2×1 (Default)",
        ["Menu_Grid_2x2"] = "2×2",
        ["Menu_Grid_3x3"] = "3×3",
        ["Menu_Grid_Auto"] = "Auto",
        ["Menu_Settings"] = "Settings(&S)",
        ["Menu_SettingsDialog"] = "Settings…",
        ["Menu_Settings_Lang"] = "Language",
        ["Status_EngineReal"] = "Engine: FFF.Native (3FP)",
        ["Status_EngineDemo"] = "Engine: Demo Mode (Simulated)",
        ["Status_ReadyOpen"] = "Ready — click Open Videos or drop files",
        ["Status_Ready"] = "Ready — open videos to compare",
        ["Status_DemoHint"] = "Demo mode — open any video to try (synthetic frames)",
        ["Status_GridMode"] = "Grid",
        ["Status_SingleMode"] = "Single",
        ["Status_Color"] = "Color",
        ["Status_ExportDone"] = "Exported",
        ["Status_Steps"] = "Step",
        ["Tab_Probe"] = "Probe",
        ["Tab_Bookmarks"] = "Bookmarks",
        ["Tab_Offset"] = "Offset",
        ["Tab_Media"] = "Media",
        ["Tab_Audio"] = "Audio",
        ["Mag_Magnifier"] = "Magnifier",
        ["Tb_Play"] = "Play (Space)",
        ["Tb_Pause"] = "Pause (Space)",
        ["Tb_Stop"] = "Stop",
        ["Tb_FramePrev"] = "Back one frame (←)",
        ["Tb_FrameNext"] = "Forward one frame (→)",
        ["Tb_SecPrev"] = "Back one second (Shift+←)",
        ["Tb_SecNext"] = "Forward one second (Shift+→)",
        ["Tb_LoopOn"] = "Loop: On",
        ["Tb_LoopOff"] = "Loop: Off",
        ["Tb_Speed"] = "Playback speed",
        ["Tb_Add"] = "Add lane",
        ["Tb_Remove"] = "Remove lane",
        ["Tb_ColorMode"] = "SDR: Standard Dynamic Range | HDR: High Dynamic Range (auto-detect display)",
        ["Probe_Title"] = "Pixel Probe",
        ["Probe_Coord"] = "Coord: --",
        ["Probe_Mode"] = "Color Mode: --",
        ["Probe_Hint"] = "Hover over a video surface to read pixels\n(pre-colormanaged values)",
        ["Probe_ReadFail"] = "Read failed",
        ["Probe_Bits"] = "8-bit values",
        ["Probe_MapSdr"] = "Map SDR",
        ["Probe_RawHdr"] = "Raw HDR",
        ["Probe_PeakHdr"] = "Peak HDR",
        ["Bookmark_Title"] = "Bookmarks",
        ["Bookmark_NotePlaceholder"] = "Note…",
        ["Bookmark_Add"] = "＋ Add Current Frame",
        ["Bookmark_Export"] = "⇩ Export…",
        ["Bookmark_Col_Time"] = "Time",
        ["Bookmark_Col_Frame"] = "Frame",
        ["Bookmark_Col_Note"] = "Note",
        ["Offset_Title"] = "Offset Calibration (relative to Lane 1)",
        ["Offset_Value"] = "Offset: 0ms (0 frames @24fps)",
        ["Offset_Align"] = "◎ Align Here",
        ["Offset_MsMinus"] = "◀ 100ms",
        ["Offset_MsPlus"] = "100ms ▶",
        ["Offset_FrameMinus"] = "◀ 1 frame",
        ["Offset_FramePlus"] = "1 frame ▶",
        ["Offset_Reset"] = "↺ Reset",
        ["Offset_NotSelected"] = "No lane selected",
        ["MediaInfo_Title"] = "Media Info",
        ["MediaInfo_Empty"] = "Select an opened media to view info",
        ["MediaInfo_File"] = "File",
        ["MediaInfo_Video"] = "— Video —",
        ["MediaInfo_Resolution"] = "Resolution",
        ["MediaInfo_Codec"] = "Codec",
        ["MediaInfo_Framerate"] = "Frame Rate",
        ["MediaInfo_PixelFormat"] = "Pixel Format",
        ["MediaInfo_Color"] = "Color",
        ["MediaInfo_Hdr"] = "HDR",
        ["MediaInfo_Frames"] = "Frames",
        ["MediaInfo_Lossless"] = "Lossless",
        ["MediaInfo_Interlaced"] = "Interlaced",
        ["MediaInfo_Yes"] = "Yes",
        ["MediaInfo_No"] = "No",
        ["MediaInfo_Audio"] = "— Audio —",
        ["MediaInfo_AudioCodec"] = "Codec",
        ["MediaInfo_Channels"] = "Channels",
        ["MediaInfo_Container"] = "— Container —",
        ["MediaInfo_Format"] = "Format",
        ["MediaInfo_Duration"] = "Duration",
        ["MediaInfo_Bitrate"] = "Bitrate",
        ["MediaInfo_FileSize"] = "File Size",
        ["Audio_Title"] = "Audio",
        ["Audio_Track"] = "Track:",
        ["Audio_Volume"] = "Volume:",
        ["Audio_Mute"] = "Mute",
        ["Audio_Hint"] = "Track selection applies to real 3FP sessions; no-op in demo mode.",
        ["Timeline_SetA"] = "Set A Point",
        ["Timeline_SetB"] = "Set B Point",
        ["Diff_Need2"] = "Diff overlay requires at least 2 opened media",
        ["Diff_HeaderFmt"] = "Diff Heatmap  [{0}] vs [{1}]  (click to refresh)",
        ["Diff_SampleFail"] = "Sampling failed",
        ["Diff_LegendWeak"] = "Weak",
        ["Diff_LegendStrong"] = "Strong",
        ["Thumbnail_Hint"] = "Drag on the timeline to preview frames",
        ["AbSlider_LaneFmt"] = "Lane {0}",
        ["Grid_Empty"] = "Click Open Videos or drop files here\nSupports 1~9 way comparison",
        ["Msg_DiffNeed2"] = "Diff overlay needs at least 2 videos. Open two first.",
        ["Msg_SelectMedia"] = "Select an opened media first",
        ["Msg_CaptureUnavailable"] = "Capture failed: window capture and pixel sampling unavailable",
        ["Msg_CaptureFail"] = "Capture failed",
        ["Msg_SessionInvalid"] = "Invalid or empty session file",
        ["Msg_AppName"] = "3FCompare",
        ["Filter_Media"] = "Media files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv|All files|*.*",
        ["Filter_All"] = "All files|*.*",
        ["Filter_Png"] = "PNG image|*.png",
        ["Filter_Session"] = "3FCompare Session|*.3fcs;*.json",
        ["Filter_Bookmark"] = "JSON|*.json|CSV|*.csv",
        ["Audio_TrackFmt"] = "Track {0}: {1} ({2}ch)",
        ["Audio_NoTrack"] = "No Audio Track",
    };

    public static string T(string key)
    {
        var dict = IsEnglish ? English : Chinese;
        return dict.TryGetValue(key, out var text) ? text : key;
    }

    /// <summary>格式化资源字符串（支持 {0}/{1}… 占位符）。key 缺失时返回 key。</summary>
    public static string Tf(string key, params object[] args)
    {
        var s = T(key);
        try { return string.Format(s, args); }
        catch { return s; }
    }
}
