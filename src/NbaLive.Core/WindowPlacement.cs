namespace NbaLive.Core;

public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom);
public readonly record struct WindowPosition(int X, int Y);

/// <summary>Physical pixel geometry; supports negative monitor coordinates and per-monitor DPI.</summary>
public static class WindowPlacement
{
    public static WindowPosition ExpandContaining(WorkArea area, int x, int y, int width, int height,
        int expandedWidth, int expandedHeight, double scale)
    {
        var desired = PreserveCorner(area, width, height, expandedWidth, expandedHeight, scale, x, y);
        return Calculate(area, expandedWidth, expandedHeight, scale,
            Math.Clamp(desired.X, x + width - expandedWidth, x),
            Math.Clamp(desired.Y, y + height - expandedHeight, y));
    }
    public static WindowPosition PreserveCorner(WorkArea area, int oldWidth, int oldHeight, int newWidth, int newHeight,
        double scale, int x, int y)
    {
        var margin = (int)Math.Round(16 * scale);
        var near = 32 * scale;
        var nextX = Math.Abs((double)x - (area.Right - oldWidth - margin)) < near ? area.Right - newWidth - margin : x;
        var nextY = Math.Abs((double)y - (area.Bottom - oldHeight - margin)) < near ? area.Bottom - newHeight - margin : y;
        return Calculate(area, newWidth, newHeight, scale, nextX, nextY);
    }
    public static WindowPosition Calculate(WorkArea area, int width, int height, double scale,
        int? savedX = null, int? savedY = null, bool snap = false)
    {
        var margin = (int)Math.Round(16 * scale);
        var x = savedX ?? area.Right - width - margin;
        var y = savedY ?? area.Bottom - height - margin;
        if (snap)
        {
            var threshold = 32 * scale;
            if (Math.Abs((double)x - (area.Left + margin)) < threshold) x = area.Left + margin;
            if (Math.Abs((double)x - (area.Right - width - margin)) < threshold) x = area.Right - width - margin;
            if (Math.Abs((double)y - (area.Top + margin)) < threshold) y = area.Top + margin;
            if (Math.Abs((double)y - (area.Bottom - height - margin)) < threshold) y = area.Bottom - height - margin;
        }
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new(x, y);
    }
}
