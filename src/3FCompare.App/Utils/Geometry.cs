namespace _3FCompare.App.Utils;

/// <summary>通用几何与索引边界检查辅助方法。</summary>
public static class Geometry
{
    /// <summary>将索引钳制到 [0, count-1]；count &lt;= 0 时返回 -1。</summary>
    public static int ClampIndex(int index, int count)
        => count <= 0 ? -1 : Math.Clamp(index, 0, count - 1);

    /// <summary>判断索引是否有效（[0, count-1] 且 count &gt; 0）。</summary>
    public static bool IsValidIndex(int index, int count)
        => count > 0 && index >= 0 && index < count;

    /// <summary>将坐标钳制到 [minX..maxX] x [minY..maxY] 范围内。</summary>
    public static Point ClampPoint(Point p, int minX, int minY, int maxX, int maxY)
        => new(Math.Clamp(p.X, minX, maxX), Math.Clamp(p.Y, minY, maxY));

    /// <summary>将坐标钳制到指定矩形范围内（clamp 到内部）。</summary>
    public static Point ClampToRect(Point p, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return rect.Location;
        return new Point(
            Math.Clamp(p.X, rect.Left, rect.Right - 1),
            Math.Clamp(p.Y, rect.Top, rect.Bottom - 1));
    }

    /// <summary>判断点是否落在矩形内部。</summary>
    public static bool IsInsideRect(Point p, Rectangle rect)
        => rect.Contains(p);

    /// <summary>按网格计算某一路的格子区域（cols×rows）；非法时返回空矩形。</summary>
    public static Rectangle CellRect(int index, int cols, int rows, Rectangle bounds)
    {
        if (cols <= 0 || rows <= 0 || index < 0) return Rectangle.Empty;
        var total = cols * rows;
        var clampedIndex = Math.Clamp(index, 0, total - 1);
        var cellW = bounds.Width / cols;
        var cellH = bounds.Height / rows;
        var r = clampedIndex / cols;
        var c = clampedIndex % cols;
        return new Rectangle(bounds.X + c * cellW, bounds.Y + r * cellH, cellW, cellH);
    }
}
