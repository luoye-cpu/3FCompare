namespace _3FCompare.App.Utils;

/// <summary>
/// DPI 缩放工具：把以 96 DPI（100%）为基准设计的逻辑像素，
/// 换算到当前窗体/控件的实际 DPI（如 4K 250% → DeviceDpi = 240）。
/// 配合窗体 AutoScaleMode.Dpi 使用，确保高缩放比例下控件不挤压、不重叠、不溢出。
/// </summary>
public static class Dpi
{
    /// <summary>逻辑基准 DPI（100%）。WinForms 的 AutoScaleDimensions 以此为基准。</summary>
    public const float BaseDpi = 96f;

    /// <summary>是否可初始化（Handle 已创建时才可读 DeviceDpi）。</summary>
    public static bool IsReady(Control c) => c.IsHandleCreated;

    /// <summary>当前控件的 DPI 缩放因子（如 250% → 2.5）。</summary>
    public static float Factor(Control c)
    {
        var dpi = c.IsHandleCreated ? c.DeviceDpi : BaseDpi;
        return dpi / BaseDpi;
    }

    /// <summary>按 DPI 缩放整数值（四舍五入）。</summary>
    public static int X(Control c, int value)
        => (int)Math.Round(value * Factor(c));

    /// <summary>按 DPI 缩放 Point。</summary>
    public static Point P(Control c, Point p)
        => new(X(c, p.X), X(c, p.Y));

    /// <summary>按 DPI 缩放 Size。</summary>
    public static Size S(Control c, Size s)
        => new(X(c, s.Width), X(c, s.Height));

    /// <summary>按 DPI 缩放宽度。</summary>
    public static int W(Control c, int width) => X(c, width);

    /// <summary>按 DPI 缩放高度。</summary>
    public static int H(Control c, int height) => X(c, height);
}