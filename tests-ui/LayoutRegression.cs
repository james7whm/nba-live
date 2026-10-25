using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NbaLive.Core;

internal static class LayoutRegression
{
    static readonly Assembly Assembly=typeof(NbaLive.App).Assembly;
    static object Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    static object? Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject{if(root is T t)yield return t;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    static async Task Pump(){await Task.Delay(120);await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);}
    static void Shot(Window window,string name)
    {
        var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth+root.Margin.Left+root.Margin.Right),(int)Math.Ceiling(root.ActualHeight+root.Margin.Top+root.Margin.Bottom),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/layout-regression");using var stream=File.Create("artifacts/layout-regression/"+name+".png");encoder.Save(stream);
    }
    internal static async Task<int> Run(Window overlay,SessionState state,AppSettings settings)
    {
        var count=0;void Check(bool ok,string text){if(!ok)throw new Exception("Layout: "+text);count++;}
        var lineup=(Panel)Field(overlay,"_lineup");
        Check(lineup.Children.Count==0,"text mode defers unused lineup controls");
        Call(overlay,"ApplyStyle",settings with {ContentMode=OverlayContent.Lineup}); Call(overlay,"Render",state,true); await Pump();
        Check(lineup.Children.Count==2,"lineup initializes both teams on first use");
        Call(overlay,"ApplyStyle",settings); Call(overlay,"Render",state,true);
        var logo=(BitmapSource)typeof(NbaLive.TeamLogo).GetMethod("Get",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,["湖人"])!;
        Check(logo.PixelWidth==64&&logo.PixelHeight==64&&logo.IsFrozen,"logo decode remains sufficient for 30 DIP at 200 percent scaling");
        var game=state.Snapshot!.Game with {Away="火箭",Home="马刺",AwayScore=114,HomeScore=110,Status=GameStatus.Finished,Progress="已结束"};
        foreach(var points in new[]{(114,110),(199,188),(999,999)})
        {
            var source=new Schedule(game with {AwayScore=points.Item1,HomeScore=points.Item2});
            var picker=(Window)Activator.CreateInstance(Assembly.GetType("NbaLive.GamePickerWindow")!,BindingFlags.NonPublic|BindingFlags.Instance,null,[source,""],null)!;picker.Show();await Pump();
            var score=Find<TextBlock>(picker).Single(t=>t.Inlines.OfType<Run>().Count()==3);
            var content=string.Concat(score.Inlines.OfType<Run>().Select(r=>r.Text));Check(content.Contains(points.Item1.ToString())&&content.Contains(points.Item2.ToString()),"both triple digit scores retained");
            var box=Find<Viewbox>(picker).Single();var bounds=score.TransformToAncestor(box).TransformBounds(new Rect(score.RenderSize));
            Check(bounds.Left>=-1&&bounds.Right<=box.ActualWidth+1,"triple digit score fits visible viewbox");
            if(points.Item1==114)
            {
                var date=Find<NbaLive.PremiumDatePicker>(picker).Single();
                var arrow=(System.Windows.Shapes.Path)Field(date,"_chevron");var label=(TextBlock)Field(date,"_label");
                foreach(var font in new[]{11,16})foreach(var open in new[]{false,true})
                {
                    label.FontSize=font;date.IsDropDownOpen=open;await Pump();
                    var arrowCenter=arrow.TranslatePoint(new Point(arrow.ActualWidth/2,arrow.ActualHeight/2),date);
                    var labelCenter=label.TranslatePoint(new Point(label.ActualWidth/2,label.ActualHeight/2),date);
                    Check(Math.Abs(arrowCenter.Y-labelCenter.Y)<1,"calendar vector arrow stays centered with date label");
                    Check(arrow.Data.ToString()==Geometry.Parse(open?"M0,4 L4,0 L8,4":"M0,0 L4,4 L8,0").ToString(),"calendar arrow indicates open or closed state");
                }
                date.IsDropDownOpen=false;label.FontSize=14;await Pump();
                Shot(picker,"score-"+settings.Theme+"-"+typeof(object).Assembly.GetName().Name);
            }
            picker.Close();
        }
        foreach(var width in new[]{280,360,480})foreach(var font in new[]{11,16})
        {
            Call(overlay,"ApplyStyle",settings with {Width=width,FontSize=font});Call(overlay,"Render",state with {Snapshot=state.Snapshot with {Game=game},SelectedGame=game},true);Call(overlay,"EnterReading");Call(overlay,"ToggleInlineHistory");await Pump();
            var toggle=(Button)Field(overlay,"_historyToggle");var progress=(TextBlock)Field(overlay,"_progress");
            var a=toggle.TranslatePoint(new Point(toggle.ActualWidth/2,toggle.ActualHeight/2),overlay);var b=progress.TranslatePoint(new Point(progress.ActualWidth/2,progress.ActualHeight/2),overlay);
            Check(Math.Abs(a.Y-b.Y)<1,"collapse arrow and finished label vertically centered");Check(toggle.Content is System.Windows.Shapes.Path,"arrow is vector, independent of font baseline");
            if(width==360&&font==16)Shot(overlay,"finished-"+settings.Theme+"-"+typeof(object).Assembly.GetName().Name);
            Call(overlay,"ToggleInlineHistory");Call(overlay,"LeaveReading");
        }
        return count;
    }
    sealed class Schedule(GameInfo game):IGameSource
    {
        public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date,CancellationToken ct)=>Task.FromResult<IReadOnlyList<GameInfo>>([game]);
        public Task<LiveSnapshot> GetSnapshot(string id,CancellationToken ct)=>throw new NotSupportedException();
    }
}
