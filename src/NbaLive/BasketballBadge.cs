namespace NbaLive;

/// <summary>A small native vector mark; no emoji, font glyph or external image dependency.</summary>
internal sealed class BasketballBadge : FrameworkElement
{
    internal static Brush RedInk => Ui.Brush("RedInk");
    private static readonly Pen Seam = CreateSeam();
    private static readonly Geometry Curves = CreateCurves();
    private static readonly Geometry Circle = CreateCircle();
    public BasketballBadge() { Width = Height = 14; IsHitTestVisible = false; }
    private static Pen CreateSeam() { var pen = new Pen(Brushes.White, 1.15); pen.Freeze(); return pen; }
    private static Geometry CreateCurves()
    {
        var geometry = Geometry.Parse("M1,0 C6,3 6,11 1,14 M13,0 C8,3 8,11 13,14");
        geometry.Freeze(); return geometry;
    }
    private static Geometry CreateCircle() { var geometry = new EllipseGeometry(new Point(7, 7), 6.5, 6.5); geometry.Freeze(); return geometry; }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawGeometry(RedInk, null, Circle);
        context.PushClip(Circle);
        context.DrawLine(Seam, new(0, 7), new(14, 7));
        context.DrawLine(Seam, new(7, 0), new(7, 14));
        context.DrawGeometry(null, Seam, Curves);
        context.Pop();
    }
}

/// <summary>Keeps the ball immediately after short names and reserves its space when long names trim.</summary>
internal sealed class PlayerNamePanel : Panel
{
    public PlayerNamePanel() { ClipToBounds = true; }
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) return new();
        var text = InternalChildren[0]; var ball = InternalChildren[1];
        ball.Measure(availableSize);
        var reserve = ball.Visibility == Visibility.Collapsed ? 0 : ball.DesiredSize.Width + 4;
        text.Measure(new(Math.Max(0, availableSize.Width - reserve), availableSize.Height));
        return new(text.DesiredSize.Width + reserve, Math.Max(text.DesiredSize.Height, ball.DesiredSize.Height));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var text = InternalChildren[0]; var ball = InternalChildren[1];
        var reserve = ball.Visibility == Visibility.Collapsed ? 0 : ball.DesiredSize.Width + 4;
        var width = Math.Min(text.DesiredSize.Width, Math.Max(0, finalSize.Width - reserve));
        text.Arrange(new Rect(0, 0, width, finalSize.Height));
        ball.Arrange(new Rect(width + (reserve > 0 ? 4 : 0), Math.Max(0, (finalSize.Height - ball.DesiredSize.Height) / 2), ball.DesiredSize.Width, ball.DesiredSize.Height));
        return finalSize;
    }
}
