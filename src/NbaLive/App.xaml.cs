namespace NbaLive;

public partial class App : Application
{
    private AppController? _controller;
    private SingleInstance? _instance;
    private bool _showPending;
    private bool _settingsPending;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance(e.Args.Contains("--demo"));
        if (!_instance.IsFirst)
        {
            if (!await _instance.Signal(e.Args.Contains("--settings")))
                MessageBox.Show("已有实例正在运行。请先从托盘退出当前实例，再重新启动。", "NBA 直播条", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _instance.ShowRequested += settings => Dispatcher.BeginInvoke(() =>
        {
            if (_controller is null) { _showPending = true; _settingsPending |= settings; }
            else _controller.Reveal(settings);
        });
        _instance.Listen();
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NbaLive");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n");
            }
            catch (IOException) { }
            args.Handled = true;
            _controller?.ReportError("操作失败，请重试；详情已记录到本地 error.log。");
        };
        _controller = new AppController(e.Args.Contains("--demo"));
        MainWindow = _controller.Overlay;
        await _controller.Start();
        if (e.Args.Contains("--inspect-overlay")) Native.AllowInspection(_controller.Overlay);
        if (_showPending) _controller.Reveal(_settingsPending);
        if (e.Args.Contains("--settings")) _controller.ShowSettings();
        if (e.Args.Contains("--statistics")) _controller.ShowStatistics();
        if (e.Args.Contains("--recap")) _controller.ShowRecap();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
