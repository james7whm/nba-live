using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class UpdateRegression
{
    static readonly Assembly Assembly = typeof(NbaLive.App).Assembly;
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(o, args);
    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject { if (root is T value) yield return value; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    sealed class Source : IUpdateSource
    {
        public Task<UpdateCheck> Check(UpdateVersion current, string variant, CancellationToken ct) => Task.FromResult(new UpdateCheck(new("1.3.0", "Gitee", UpdateSource.GiteePage, "修复直播阅读与布局。\n支持国内下载源和一键更新。", "2026-10-25", "", "", "", "", null), "发现新版本 v1.3.0"));
        public Task<UpdateRelease> Download(UpdateRelease release, string variant, string zip, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
    static async Task Pump() { await Task.Delay(150); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    static void Shot(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/update-review");
        using var image = File.Create($"artifacts/update-review/{name}-{typeof(object).Assembly.GetName().Name}.png"); png.Save(image);
    }
    internal static async Task<int> Run(AppSettings settings)
    {
        var count = 0; void Check(bool ok, string name) { if (!ok) throw new Exception("Updates UI: " + name); count++; }
        using var updates = new UpdateCoordinator(new Source(), new(1, 2, 1), "modern", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await updates.Check(); var install = 0; var skip = 0;
        new SettingsStore(Path.Combine(Path.GetTempPath(), "NbaLive-Demo")).Save(settings);
        var window = (Window)Activator.CreateInstance(Assembly.GetType("NbaLive.UpdateWindow")!, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [updates, new Action(() => install++), new Action(() => { skip++; updates.SkippedVersion = "1.3.0"; updates.NotifySkipped(); })], null)!;
        var controller = Activator.CreateInstance(Assembly.GetType("NbaLive.AppController")!, BindingFlags.Instance | BindingFlags.NonPublic, null, [true, false, false, false], null)!;
        try
        {
            window.Show(); await Pump();
            var texts = Find<TextBlock>(window).Select(t => t.Text).ToArray();
            var currentVersion = Assembly.GetName().Version!;
            Check(texts.Any(t => t.Contains("1.3.0")) && texts.Any(t => t.Contains($"当前 v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}")), "both versions shown");
            Check(texts.Any(t => t.Contains("Gitee") && t.Contains("修复直播")), "source and plain text release notes");
            Check(Find<ScrollViewer>(window).Any(), "release notes scroll");
            var button = Find<Button>(window).Single(b => Equals(b.Content, "下载并更新")); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(install == 1, "install only after explicit click");
            Find<Button>(window).Single(b => Equals(b.Content, "跳过此版本")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(skip == 1 && !updates.HasNotice && updates.State.Release is not null, "skip does not remove manual install");
            Call(updates, "Publish", updates.State with { Downloading = true, Progress = .45, Message = "下载测试" }); await Pump();
            Check(!button.IsEnabled && Math.Abs(Find<ProgressBar>(window).Single().Value - 45) < .1, "download cannot repeat and progress visible");
            Call(updates, "Publish", updates.State with { Downloading = false, Message = "下载失败，旧版可用" }); await Pump();
            Check(button.IsEnabled && Find<TextBlock>(window).Any(t => t.Text.Contains("旧版可用")), "failure permits retry");
            controller.GetType().GetField("_updateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, window);
            var overlay = (Window)controller.GetType().GetProperty("Overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!; overlay.Show();
            Call(controller, "ToggleVisibility"); await Pump(); Check(!window.IsVisible && !overlay.IsVisible, "global hide includes update window");
            Call(controller, "ToggleVisibility"); await Pump(); Check(window.IsVisible && overlay.IsVisible, "global restore preserves update window");
            var content = (FrameworkElement)window.Content; Shot(window, $"{settings.Theme}-available");
            Check(content.ActualWidth <= window.ActualWidth && window.ShowInTaskbar, "layout fits normal taskbar window");
            window.Width = 420; window.Height = 540; await Pump(); Shot(window, $"{settings.Theme}-narrow");
            Call(updates, "Publish", updates.State with { Release = null, Message = "当前已是最新版本" });
            window.Width = 580; window.Height = 680; await Pump(); Shot(window, $"{settings.Theme}-current");
            overlay.Close();
        }
        finally { window.Close(); ((IDisposable)controller).Dispose(); }
        return count;
    }
}
