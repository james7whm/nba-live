using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NbaLive.Core;

internal static class ReadingRegression
{
    static object Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static IEnumerable<T> Find<T>(DependencyObject o) where T:DependencyObject {if(o is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(o,i)))yield return child;}
    static async Task Pump(){await Task.Delay(140);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    internal static async Task<int> Run(Window overlay,SessionState initial,AppSettings settings)
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception("Reading regression: "+message);checks++;}
        var game=initial.Snapshot!.Game;
        SessionState Burst(int count)
        {
            var rows=Enumerable.Range(1,count).Select(i=>initial.History[0] with { SourceId="regression-"+i,Text="阅读回归消息 "+i }).ToArray();
            return initial with {History=rows,Snapshot=initial.Snapshot with {Entries=rows},HistoryRevision=count,AddedEntryKeys=rows.TakeLast(40).Select(e=>e.Key).ToArray()};
        }
        foreach(var capsule in new[]{false,true})
        {
            Call(overlay,"ApplyStyle",settings with {CapsuleEnabled=capsule});
            Call(overlay,"Render",Burst(1000),true);Call(overlay,"EnterReading");if(capsule)Call(overlay,"SetExpanded",true);
            Call(overlay,"ToggleInlineHistory");await Pump();
            var history=(DependencyObject)Field(overlay,"_inlineHistory");var list=Find<ListBox>(history).Single();var scroll=Find<ScrollViewer>(list).First();
            Check(list.Items.Count==1000,"initial history count");scroll.ScrollToVerticalOffset(900);await Pump();
            var visible=Find<ListBoxItem>(list).Where(item=>item.TranslatePoint(new Point(),list).Y+item.ActualHeight>0).OrderBy(item=>item.TranslatePoint(new Point(),list).Y).First();
            var anchor=((LiveEntry)visible.DataContext).Key;var top=visible.TranslatePoint(new Point(),list).Y;
            Call(overlay,"Render",Burst(1040),true);await Pump();
            Check(list.Items.Count==1040 && ((LiveEntry)list.Items[0]).Text=="阅读回归消息 1040","expanded hovered view receives fresh entries");
            var retained=list.ItemContainerGenerator.ContainerFromItem(list.Items.Cast<LiveEntry>().Single(e=>e.Key==anchor)) as ListBoxItem;
            Check(retained is not null && Math.Abs(retained.TranslatePoint(new Point(),list).Y-top)<4,"arrival retains reading anchor");
            Call(overlay,"ToggleInlineHistory");await Pump();var lines=(TextBlock[])Field(overlay,"_lines");
            Check(list.Items.Count==0,"closing history releases its presentation rows without deleting session history");
            Check(lines[0].Text=="阅读回归消息 1040","closing while hovered immediately restores latest compact text");
            Call(overlay,"Render",Burst(1080),true);await Pump();Check(lines[0].Text=="阅读回归消息 1080","closing history resumes ongoing updates even while cursor stays inside");
            Call(overlay,"EnterReading");Call(overlay,"Render",Burst(1120),true);await Pump();Check(lines[0].Text=="阅读回归消息 1080","a new ordinary compact hover still freezes");
            Call(overlay,"LeaveReading");if(capsule){Call(overlay,"EnterReading");Call(overlay,"SetExpanded",true);}await Pump();
            Check(lines[0].Text=="阅读回归消息 1120","leave or capsule re-entry recovers newest text");
            Call(overlay,"EnterReading");Call(overlay,"ToggleInlineHistory");await Pump();scroll.ScrollToVerticalOffset(900);await Pump();
            Call(overlay,"Render",Burst(1160),true);await Pump();
            Find<Button>(history).Single(b=>Equals(b.Content,"返回最新")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
            Check(scroll.VerticalOffset<1 && ((LiveEntry)list.Items[0]).Text=="阅读回归消息 1160","return latest button scrolls to current head");
            Call(overlay,"ToggleInlineHistory");Call(overlay,"LeaveReading");
        }
        Call(overlay,"ApplyStyle",settings);Call(overlay,"Render",initial,true);Call(overlay,"EnterReading");return checks;
    }
}
