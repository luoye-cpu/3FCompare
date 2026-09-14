using System;

namespace _3FCompare.Core.Display;

/// <summary>时间码换算（纯计算，便于单测；UI 侧不要复制实现）。</summary>
public static class Timecode
{
    /// <summary>PR 时间码的「秒内帧号」，0 起。
    /// <para><b>必须与显示的"秒"同源</b>：时间码的 HH:MM:SS 来自 <c>Position100ns</c>（墙上时间），
    /// 所以帧号也只能由 <c>Position100ns</c> 推出。旧实现用 <c>FrameIndex % round(fps)</c>，
    /// 那是"标称帧网格"（29.97 按 30 计），与墙上时间每秒滑移 0.03 帧，
    /// 约 1000 s 后错开整整一个周期 —— 表现为「秒刚跳过去，帧号却是 29」这类不自洽。
    /// 23.976 同理（24 与 23.976 差 0.024，约 1000 s 错开一周）。</para>
    /// <para>代价：非整数帧率下某些秒会显示 29 帧而非 30 帧。这是"与墙上时间对齐"的必然结果，
    /// 也是正确的取舍 —— 时间码的秒已经来自墙上时间，两者必须一致。</para></summary>
    /// <param name="position100ns">媒体位置（100ns tick）。</param>
    /// <param name="fps">帧率，可为非整数（23.976 / 29.97 等）。</param>
    public static int FrameInSecond(long position100ns, double fps)
    {
        if (fps <= 0 || position100ns <= 0) return 0;
        var sec = TimeSpan.TicksPerSecond;
        var frac = (double)(position100ns % sec) / sec;
        var max = Math.Max(0, (int)Math.Ceiling(fps) - 1);
        return Math.Clamp((int)Math.Floor(frac * fps), 0, max);
    }
}
