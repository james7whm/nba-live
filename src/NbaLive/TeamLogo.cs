using System.Windows.Media.Imaging;

namespace NbaLive;

/// <summary>Bundled NBA marks: drawing never starts an HTTP request.</summary>
public sealed class TeamLogo : FrameworkElement
{
    public static readonly DependencyProperty TeamProperty = DependencyProperty.Register(nameof(Team),typeof(string),typeof(TeamLogo),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.AffectsRender));
    public string Team { get => (string)GetValue(TeamProperty); set => SetValue(TeamProperty,value); }
    private static readonly Dictionary<string,BitmapSource> Images=[];
    public TeamLogo()
    {
        Width=Height=24; IsHitTestVisible=false;
        Loaded+=(_,_)=> { ThemeManager.Changed+=Redraw; InvalidateVisual(); };
        Unloaded+=(_,_)=>ThemeManager.Changed-=Redraw;
    }
    private void Redraw()=>InvalidateVisual();
    internal static BitmapSource? Get(string team)
    {
        var color=TeamPalette.Resolve(team); if(color.Key==TeamPalette.Unknown.Key) return null;
        var key=color.Key+(ThemeManager.IsDark?"-dark":"-light");
        if(Images.TryGetValue(key,out var cached)) return cached;
        try
        {
            // Current marks are at most 30 DIP; 64 pixels also covers 200% scaling.
            var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.DecodePixelWidth=64;
            image.UriSource=new Uri("pack://application:,,,/NbaLive;component/Assets/Teams/"+key+".png"); image.EndInit(); image.Freeze();
            Images[key]=image; return image;
        }
        catch(Exception ex) when(ex is IOException or NotSupportedException or System.IO.FileFormatException) {return null;}
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if(Get(Team) is {} image) dc.DrawImage(image,new Rect(0,0,ActualWidth,ActualHeight));
        else
        {
            var radius=Math.Min(ActualWidth,ActualHeight)/2;
            dc.DrawEllipse(Ui.Brush("SoftBrush"),new Pen(Ui.Brush("LineBrush"),1),new(ActualWidth/2,ActualHeight/2),radius-1,radius-1);
            var text=new FormattedText(string.IsNullOrEmpty(Team)?"?":Team[..1],System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),Math.Max(9,radius),Ui.Brush("MutedInk"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text,new((ActualWidth-text.Width)/2,(ActualHeight-text.Height)/2));
        }
    }
}

internal sealed class MatchScoreLine : Viewbox
{
    private readonly TeamLogo _away=new(),_home=new();
    private readonly TextBlock _awayName=new(),_homeName=new(),_score=new();
    internal MatchScoreLine(double fontSize=16,double logoSize=22)
    {
        Stretch=Stretch.Uniform; StretchDirection=StretchDirection.DownOnly; HorizontalAlignment=HorizontalAlignment.Left; VerticalAlignment=VerticalAlignment.Center;
        var row=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        _away.Width=_away.Height=_home.Width=_home.Height=logoSize;
        foreach(var text in new[]{_awayName,_homeName,_score}) {text.FontSize=fontSize; text.VerticalAlignment=VerticalAlignment.Center; text.Margin=new(4,0,4,0);}
        _score.FontWeight=FontWeights.SemiBold; _score.Margin=new(5,0,5,0);
        row.Children.Add(_away); row.Children.Add(_awayName); row.Children.Add(_score); row.Children.Add(_homeName); row.Children.Add(_home); Child=row;
    }
    internal void Update(GameInfo? game)
    {
        Visibility=game is null?Visibility.Collapsed:Visibility.Visible;
        _away.Team=_awayName.Text=game?.Away??""; _home.Team=_homeName.Text=game?.Home??"";
        _score.Text=$"{game?.AwayScore?.ToString()??"—"} : {game?.HomeScore?.ToString()??"—"}";
        ToolTip=game?.Score; System.Windows.Automation.AutomationProperties.SetName(this,game?.Score??"");
    }
}
