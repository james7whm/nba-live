using System.Reflection;
using System.Windows;
using NbaLive.Core;

internal static class Program
{
    [STAThread] static int Main()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var exit=0;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("pack://application:,,,/NbaLive;component/UiTheme.xaml")});
        var assembly=typeof(NbaLive.App).Assembly;
        app.DispatcherUnhandledException+=(_,e)=>{Console.Error.WriteLine(e.Exception);e.Handled=true;exit=1;app.Shutdown(1);};
        app.Startup+=async (_,_)=>
        {
            try
            {
                var total=0;
                foreach(var theme in new[]{AppTheme.Warm,AppTheme.Dark})
                {
                    assembly.GetType("NbaLive.ThemeManager")!.GetMethod("Apply",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[theme]);
                    var source=(IGameSource)Activator.CreateInstance(assembly.GetType("NbaLive.DemoSource")!,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance,null,[false,false],null)!;
                    var live=await source.GetSnapshot("0",CancellationToken.None);
                    var settings=new AppSettings {Theme=theme};
                    var overlay=(Window)Activator.CreateInstance(assembly.GetType("NbaLive.OverlayWindow")!,BindingFlags.NonPublic|BindingFlags.Instance,null,[settings],null)!;
                    overlay.Show();
                    total+=await ReadingRegression.Run(overlay,new(live,live.Entries,"",false,false){SelectedGame=live.Game},settings);
                    overlay.Close();
                }
                Console.WriteLine($"PASS {total} WPF reading assertions: both themes, normal/capsule, scroll anchor, new arrivals, close, hover, resume and return latest.");app.Shutdown();
            }
            catch(Exception ex){Console.Error.WriteLine(ex);exit=1;app.Shutdown(1);}
        };
        app.Run();return exit;
    }
}
