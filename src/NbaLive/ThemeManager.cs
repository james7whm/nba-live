using System.ComponentModel;
using System.Windows.Data;

namespace NbaLive;

/// <summary>Shared bound brushes keep both templates and programmatically built views live when the palette changes.</summary>
internal static class ThemeManager
{
    internal sealed class Palette : INotifyPropertyChanged
    {
        private Dictionary<string, Color> _colors = [];
        public Color this[string name] => _colors[name];
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Set(AppTheme theme)
        {
            var dark = theme == AppTheme.Dark;
            string[] names = ["Ink","MutedInk","CanvasBrush","CardBrush","SoftBrush","LineBrush","AccentBrush","AccentInk","AccentTint","RedInk","RedTint","RedBorder","SurfaceTop","SurfaceBottom","HighlightBrush","SelectedInk","ChartBand","ShadowInk","KnobBrush"];
            string[] light = ["#303840","#737B83","#FAF9F6","#FBFAF7","#F0EEEA","#E5E0D8","#ED863D","#AB5015","#FFF2E7","#C8102E","#FFF0F2","#E7ABB7","#FFFFFF","#F7F5F1","#FFFFFF","#303840","#F4F2EF","#655D52","#FFFFFF"];
            string[] night = ["#F1F4F6","#A5B0BA","#18222A","#202C35","#2A3944","#3B4A55","#ED863D","#FFB276","#352C29","#FF7B86","#402D37","#87535D","#26343E","#172129","#50616E","#303840","#202D36","#000000","#E9EEF2"];
            _colors = names.Select((name,index)=>(name,Color:(Color)ColorConverter.ConvertFromString((dark?night:light)[index]))).ToDictionary(v=>v.name,v=>v.Color);
            PropertyChanged?.Invoke(this,new("Item[]"));
        }
    }
    private static readonly Palette Colors = new();
    private static Application? _application;
    internal static AppTheme Current { get; private set; } = AppTheme.Warm;
    internal static bool IsDark => Current == AppTheme.Dark;
    internal static event Action? Changed;
    internal static void Ensure()
    {
        var app = Application.Current;
        if (app is null || ReferenceEquals(app,_application)) return;
        _application = app; Colors.Set(Current);
        foreach (var key in new[] {"Ink","MutedInk","CanvasBrush","CardBrush","SoftBrush","LineBrush","AccentBrush","AccentInk","AccentTint","RedInk","RedTint","RedBorder","HighlightBrush","SelectedInk","ChartBand","ShadowInk","KnobBrush"})
        {
            var brush = new SolidColorBrush(); Bind(brush,SolidColorBrush.ColorProperty,key); app.Resources[key] = brush;
        }
        app.Resources["WindowSurface"] = Surface(); app.Resources["CardSurface"] = Surface();
    }
    private static void Bind(DependencyObject target,DependencyProperty property,string key) =>
        BindingOperations.SetBinding(target,property,new Binding("["+key+"]") { Source = Colors, Mode = BindingMode.OneWay });
    internal static LinearGradientBrush Surface(double opacity = 1)
    {
        var top = new GradientStop { Offset=0 }; var bottom = new GradientStop { Offset=1 };
        Bind(top,GradientStop.ColorProperty,"SurfaceTop"); Bind(bottom,GradientStop.ColorProperty,"SurfaceBottom");
        return new(new GradientStopCollection {top,bottom},new Point(0,0),new Point(0,1)) {Opacity=opacity};
    }
    internal static void Apply(AppTheme theme)
    {
        Ensure(); theme = RuntimeCompat.IsDefined(theme)?theme:AppTheme.Warm;
        if(Current==theme) return;
        Current=theme; Colors.Set(theme);
        foreach(Window window in Application.Current.Windows) Native.SetThemeCaption(window);
        Changed?.Invoke();
    }
    internal static Color ChartColor(TeamJerseyColor color)
    {
        var c=Color.FromRgb((byte)(color.Rgb>>16),(byte)(color.Rgb>>8),(byte)color.Rgb);
        if (!IsDark) return c;
        var lightness=(.2126*c.R+.7152*c.G+.0722*c.B)/255;
        var mix=lightness<.55?.48:.18;
        return Color.FromRgb((byte)(c.R+(255-c.R)*mix),(byte)(c.G+(255-c.G)*mix),(byte)(c.B+(255-c.B)*mix));
    }
}
