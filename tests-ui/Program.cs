using System.Reflection;
using System.Windows;
using NbaLive.Core;

internal static class Program
{
    [STAThread] static int Main()
    {
        if(Environment.GetCommandLineArgs().Contains("--smoke"))
        {
            var smoke=new NbaLive.App();smoke.InitializeComponent();
            var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};var smokeExit=0;
            timer.Tick+=(_,_)=>{timer.Stop();if(smoke.Windows.Count<2){Console.Error.WriteLine("Startup did not create expected windows");smokeExit=1;}else Console.WriteLine("PASS App startup: "+string.Join(",",smoke.Windows.Cast<Window>().Select(w=>w.GetType().Name)));smoke.Shutdown();};
            smoke.Startup+=(_,_)=>timer.Start();smoke.Run();return smokeExit;
        }
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
                    total+=await LayoutRegression.Run(overlay,new(live,live.Entries,"",false,false){SelectedGame=live.Game},settings);
                    overlay.Close();
                }
                Console.WriteLine($"PASS {total} WPF assertions: reading, triple digit scores and collapse arrow alignment, both themes.");app.Shutdown();
            }
            catch(Exception ex){Console.Error.WriteLine(ex);exit=1;app.Shutdown(1);}
        };
        app.Run();return exit;
    }
}
