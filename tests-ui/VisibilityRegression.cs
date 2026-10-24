using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NbaLive.Core;

internal static class VisibilityRegression
{
    static object Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(o)!;
    static object? Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static Window Overlay(object controller)=>(Window)controller.GetType().GetProperty("Overlay",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
    static Window[] Windows(object controller)=>((IEnumerable<Window>)Call(controller,"Windows")!).ToArray();
    static async Task Pump(){await Task.Delay(150);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    internal static async Task<int> Run(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Visibility: "+message);count++;}
        new SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"NbaLive-Demo")).Save(settings);
        var type=typeof(NbaLive.App).Assembly.GetType("NbaLive.AppController")!;
        var controller=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[true,false,false,false],null)!;
        var overlay=Overlay(controller);
        try
        {
            var store=(SettingsStore)Field(controller,"_store");
            Check(store.DirectoryPath.StartsWith(System.IO.Path.GetTempPath(),StringComparison.OrdinalIgnoreCase),"controller uses isolated demo configuration");
            overlay.Show();
            foreach(var method in new[]{"OpenPicker","ShowStatistics","ShowSettings","ShowHistory","ShowRecap","ShowBookmarks"})Call(controller,method);
            await Pump();var windows=Windows(controller);
            Check(windows.Length==7&&windows.All(w=>w.IsVisible),"all seven application windows are open");
            var picker=(Window)Field(controller,"_picker");
            var date=(NbaLive.PremiumDatePicker)Field(picker,"_date");date.IsDropDownOpen=true;await Pump();
            var popup=(Popup)Field(date,"_popup");Check(popup.IsOpen,"calendar starts open");
            var stats=(Window)Field(controller,"_statistics");stats.WindowState=WindowState.Minimized;await Pump();
            Call(controller,"ToggleVisibility");await Pump();
            Check(windows.All(w=>!w.IsVisible),"hide removes overlay and every auxiliary window");
            Check(!popup.IsOpen&&!date.IsDropDownOpen,"hide dismisses detached calendar popup and resets its state");
            Check(Windows(controller).SequenceEqual(windows),"hide retains existing window instances");
            Call(controller,"ToggleVisibility");await Pump();
            Check(windows.All(w=>w.IsVisible),"toggle restores previously visible windows");
            Check(stats.WindowState==WindowState.Minimized,"restore retains minimized window state");
            Check(!date.IsDropDownOpen,"restoring does not reopen calendar");
            Check(windows.All(w=>w.ShowActivated==!ReferenceEquals(w,overlay)),"restoring preserves activation preferences");
            Call(controller,"ToggleVisibility");Call(controller,"ShowStatistics");await Pump();
            Check(windows.All(w=>w.IsVisible)&&ReferenceEquals(stats,Field(controller,"_statistics")),"explicit statistics entry restores group without duplicate window");
            Call(controller,"ToggleVisibility");Call(controller,"Reveal",false);await Pump();
            Check(windows.All(w=>w.IsVisible),"single-instance reveal restores whole group");
            Call(controller,"ToggleVisibility");picker.Close();await Pump();Call(controller,"Reveal",true);await Pump();
            Check(Windows(controller).Count()==6&&Windows(controller).All(w=>w.IsVisible),"closed hidden window is not resurrected");
            Call(controller,"ToggleVisibility");Call(controller,"EnsurePicker",false);await Pump();
            var deferredPicker=(Window)Field(controller,"_picker");
            Check(Windows(controller).All(w=>!w.IsVisible),"late automatic picker does not reveal hidden application");
            Call(controller,"ToggleVisibility");await Pump();
            Check(deferredPicker.IsVisible&&Windows(controller).All(w=>w.IsVisible),"deferred picker joins next group restore");
            Call(controller,"ToggleVisibility");
            var source=(IGameSource)Field(controller,"_source");var games=await source.GetGames(MatchTime.Today,CancellationToken.None);
            await (Task)Call(controller,"Select",games[0])!;await Pump();
            Check(Windows(controller).All(w=>!w.IsVisible),"asynchronous match selection cannot reveal hidden windows");
            var session=(TextFeedCoordinator)Field(controller,"_session");
            Check(session.State.Snapshot is not null,"hidden application still receives live data");
            Call(controller,"ToggleVisibility");await Pump();
            Check(Windows(controller).All(w=>w.IsVisible)&&Windows(controller).Count()==6,"restore after hidden selection skips closed picker");
            var hiddenHistory=(Window)Field(controller,"_history");hiddenHistory.Hide();Call(controller,"ToggleVisibility");Call(controller,"ToggleVisibility");await Pump();
            Check(!hiddenHistory.IsVisible,"window already hidden before group hide stays hidden");
            Call(controller,"ShowHistory");await Pump();Check(hiddenHistory.IsVisible,"explicit history entry can show separately hidden window");
        }
        finally
        {
            ((IDisposable)controller).Dispose();
            foreach(var window in Windows(controller).ToArray())window.Close();
        }
        return count;
    }
}
