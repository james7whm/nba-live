using System.Globalization;
using System.Windows.Automation.Peers;

namespace NbaLive;

internal sealed class JerseyBadge : FrameworkElement
{
    internal static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(string), typeof(JerseyBadge),
        new FrameworkPropertyMetadata("-", FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly DependencyProperty PresenceProperty = DependencyProperty.Register(nameof(Presence), typeof(CourtPresence), typeof(JerseyBadge),
        new FrameworkPropertyMetadata(CourtPresence.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly DependencyProperty TeamColorProperty = DependencyProperty.Register(nameof(TeamColor), typeof(TeamJerseyColor), typeof(JerseyBadge),
        new FrameworkPropertyMetadata(TeamPalette.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Number { get => (string)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public CourtPresence Presence { get => (CourtPresence)GetValue(PresenceProperty); set => SetValue(PresenceProperty, value); }
    public TeamJerseyColor TeamColor { get => (TeamJerseyColor)GetValue(TeamColorProperty); set => SetValue(TeamColorProperty, value); }
    private static readonly Geometry Shirt = CreateShirt();
    private static readonly IReadOnlyDictionary<uint, Brush> TeamBrushes = TeamPalette.All.Append(TeamPalette.Unknown)
        .ToDictionary(c => c.Rgb, c => FrozenBrush((byte)(c.Rgb >> 16), (byte)(c.Rgb >> 8), (byte)c.Rgb));
    private static readonly Brush Grey = FrozenBrush(145, 153, 164);
    private static readonly Brush Outline = FrozenBrush(125, 134, 147);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    public JerseyBadge() { Width = 26; Height = 28; VerticalAlignment = VerticalAlignment.Center; }
    private static Geometry CreateShirt()
    {
        var geometry = Geometry.Parse("M6,1 L10,1 C10,5 16,5 16,1 L20,1 L20,7 Q20,10 23,11 L21,27 L5,27 L3,11 Q6,10 6,7 Z");
        geometry.Freeze(); return geometry;
    }
    private static Brush FrozenBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var teamBrush = TeamBrushes.GetValueOrDefault(TeamColor.Rgb) ?? TeamBrushes[TeamPalette.Unknown.Rgb];
        var colored = Presence is CourtPresence.OnCourt or CourtPresence.Starter;
        var fill = colored ? teamBrush : Presence == CourtPresence.OffCourt ? Grey : Brushes.Transparent;
        dc.DrawGeometry(fill, new Pen(colored ? teamBrush : Outline, 1), Shirt);
        var number = new FormattedText(Number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11,
            Presence == CourtPresence.Unknown ? Outline : colored && TeamColor.UseDarkNumber ? Brushes.Black : Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(number, new Point((26 - number.Width) / 2, 9));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
