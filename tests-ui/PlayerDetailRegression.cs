using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class PlayerDetailRegression
{
    static readonly Assembly Assembly = typeof(NbaLive.App).Assembly;
    static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(o, args);
    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject { if (root is T value) yield return value; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    static async Task Pump() { await Task.Delay(150); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    static void Shot(Window window, string name)
    {
        var root = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right), (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/player-detail-review");
        using var file = File.Create("artifacts/player-detail-review/" + name + "-" + typeof(object).Assembly.GetName().Name + ".png"); encoder.Save(file);
    }
    internal static async Task<int> Run(AppSettings settings)
    {
        var checks = 0; void Check(bool value, string label) { if (!value) throw new Exception("Player details: " + label); checks++; }
        var game = new GameInfo("168869", "尼克斯", "凯尔特人", 103, 107, GameStatus.Finished, "已结束", new(2026, 10, 24));
        var player = new PlayerStatistics("karlanthonytowns-150037.html", "卡尔-安东尼-唐斯", true, "C", "37", "9-13", "2-3", "2-3", 3, 7, 10, 3, 5, 1, 4, 0, 22, 2) { JerseyNumber = "32" };
        var query = new PlayerDetailQuery(game, game.Away, player);
        var snapshot = PlayerGameDetailParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-towns.json")), query, "1537445625157648384", "1901000000468026", "1901000000501273", DateTimeOffset.UtcNow);
        var box = new BoxScoreSnapshot(game, new(game.Away, [player], player), new(game.Home, [player with { Id = "other", Name = "演示：其他球员" }], null), DateTimeOffset.UtcNow);
        using var statistics = new StatisticsCoordinator(new Stats(box)); await statistics.SetGame(game); await statistics.SetDemand(StatisticsConsumer.Window, true);
        var followCalls = 0; var details = new Detail(snapshot);
        var window = (Window)Activator.CreateInstance(Assembly.GetType("NbaLive.StatisticsWindow")!, BindingFlags.NonPublic | BindingFlags.Instance, null,
            [statistics, false, settings, new Func<FavoritePlayer, string?>(_ => { followCalls++; return null; }), new Func<StatisticsPreset, StatisticsTableLayout[], string?>((_, _) => null)], null)!;
        Call(window, "ConfigureDetails", details); window.Show(); await Pump();
        try
        {
            var sheet = Field(window, "_away"); var grid = (DataGrid)Field(sheet, "_grid"); var panel = (FrameworkElement)Field(window, "_detail");
            Check(panel.Visibility == Visibility.Collapsed, "detail initially closed");
            var star = Find<Button>(grid).First(b => Equals(b.Content, "☆") || Equals(b.Content, "★")); star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(followCalls == 1 && details.Calls == 0 && panel.Visibility == Visibility.Collapsed, "star only follows without opening detail");
            grid.SelectedIndex = 0; grid.Focus(); var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            grid.RaiseEvent(enter); await Pump();
            Check(panel.IsVisible && Math.Abs(panel.ActualWidth - 440) < 1, "Enter opens 440 DIP side panel");
            Check(((TabControl)Field(window, "_tabs")).IsVisible, "wide layout retains player table");
            var court = Field(panel, "_court"); var shots = (IReadOnlyList<PlayerShot>)court.GetType().GetProperty("ShownShots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(court)!;
            Check(shots.Count == 13 && shots.Count(s => s.Made) == 9, "real shot coordinates rendered");
            Check(((FrameworkElement)court).ActualHeight <= ((ScrollViewer)Field(panel, "_scroll")).ViewportHeight + 1, "whole court fits scroll viewport");
            Check(Find<TextBlock>(panel).Any(t => t.Text == "7/10") && Find<TextBlock>(panel).Any(t => t.Text == "22"), "two pointers and points displayed");
            var detailCoordinator = (PlayerDetailCoordinator)Field(window, "_detailCoordinator");
            var initial = new TaskCompletionSource<PlayerGameDetailSnapshot>();
            detailCoordinator.Close(); details.Read = (_, _) => initial.Task;
            Call(window, "OpenDetail", game.Away, player); await Pump();
            Check(Find<TextBlock>(panel).Any(t => t.Text == "正在读取本场统计与投篮分布…"), "empty panel shows loading");
            initial.SetException(new SourceFormatException("未能唯一对应本场球员编号")); await Pump();
            Check(Find<TextBlock>(panel).Any(t => t.Text == "本场详情暂不可用，可稍后刷新。") && !Find<TextBlock>(panel).Any(t => t.Text.Contains("正在读取")), "failed load replaces body as well as footer");
            Shot(window, "detail-error-" + settings.Theme);
            var retry = new TaskCompletionSource<PlayerGameDetailSnapshot>(); details.Read = (_, _) => retry.Task;
            detailCoordinator.Refresh(); await Pump();
            Check(!Find<TextBlock>(panel).Any(t => t.Text.Contains("暂不可用")) && Find<TextBlock>(panel).Any(t => t.Text == "正在读取本场统计与投篮分布…"), "manual retry clears error body");
            retry.SetResult(snapshot); await Pump();
            var figure = ((Panel)Field(panel, "_figures")).Children[0];
            details.Read = (_, _) => Task.FromException<PlayerGameDetailSnapshot>(new System.Net.Http.HttpRequestException());
            detailCoordinator.Refresh(); await Pump();
            Check(ReferenceEquals(((Panel)Field(panel, "_figures")).Children[0], figure) && Find<TextBlock>(panel).Any(t => t.Text == "22"), "failure retains valid figures without rebuilding reading content");
            details.Read = null; detailCoordinator.Refresh(); await Pump();
            Shot(window, "towns-" + settings.Theme);
            var tabs = (Panel)Field(panel, "_tabs"); tabs.Children.OfType<Button>().Single(b => Equals(b.Content, "3")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check((int)panel.GetType().GetProperty("SelectedPeriod", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)! == 3, "quarter selected");
            shots = (IReadOnlyList<PlayerShot>)court.GetType().GetProperty("ShownShots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(court)!;
            Check(shots.Count == 2 && shots.All(s => s.Period == 3), "quarter filters points locally");
            var coordinator = (PlayerDetailCoordinator)Field(window, "_detailCoordinator"); var calls = details.Calls; coordinator.Refresh(); await Pump();
            Check(details.Calls == calls + 1 && (int)panel.GetType().GetProperty("SelectedPeriod", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)! == 3, "refresh retains period");
            window.Hide(); await Pump(); Check(coordinator.State.Snapshot is not null && !window.IsVisible, "hide keeps detail content");
            window.Show(); await Pump(); Check(details.Calls == calls + 2 && panel.IsVisible, "restore refreshes selected detail");
            window.MinWidth = 600; window.Width = 760; await Pump();
            Check(panel.ActualWidth > 650 && !((TabControl)Field(window, "_tabs")).IsVisible, "narrow layout uses full body");
            Shot(window, "towns-narrow-" + settings.Theme);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Pump();
            Check(panel.Visibility == Visibility.Collapsed && ((TabControl)Field(window, "_tabs")).IsVisible && coordinator.State.Query is null, "Escape returns to table and cancels detail");
            window.Width = 1100; Call(window, "OpenDetail", game.Away, player); await Pump();
            ((TabControl)Field(window, "_tabs")).SelectedIndex = 1; await Pump();
            Check(panel.Visibility == Visibility.Collapsed && coordinator.State.Query is null, "team tab closes previous player");
            Call(window, "OpenDetail", game.Home, player with { Id = "other", Name = "演示：其他球员" }); await Pump();
            Check(coordinator.State.Query?.Player.Id == "other", "opponent selection replaces player identity");
            await statistics.SetGame(game with { Id = "another" }); await Pump();
            Check(panel.Visibility == Visibility.Collapsed && coordinator.State.Query is null, "game change clears detail");
        }
        finally { window.Close(); }
        return checks;
    }
    internal static async Task<int> RunLive(AppSettings settings, bool alias = false)
    {
        using var source = new WebGameSource(); var args = Environment.GetCommandLineArgs(); var fileIndex = Array.IndexOf(args, "--box-score-file");
        var box = fileIndex >= 0 ? new BoxScoreParser().Parse(File.ReadAllText(args[fileIndex + 1]), alias ? "169007" : "168869", DateTimeOffset.UtcNow)
            : await source.GetBoxScore(alias ? "169007" : "168869", default);
        var player = box.Away.Players.Single(p => PlayerGameDetailParser.LegacyNumber(p.Id) == (alias ? "153410" : "150037"));
        using var statistics = new StatisticsCoordinator(new Stats(box)); await statistics.SetGame(box.Game); await statistics.SetDemand(StatisticsConsumer.Window, true);
        var window = (Window)Activator.CreateInstance(Assembly.GetType("NbaLive.StatisticsWindow")!, BindingFlags.NonPublic | BindingFlags.Instance, null,
            [statistics, false, settings, new Func<FavoritePlayer, string?>(_ => null), new Func<StatisticsPreset, StatisticsTableLayout[], string?>((_, _) => null)], null)!;
        Call(window, "ConfigureDetails", new PlayerGameDetailSource(source)); window.Show(); await Pump();
        try
        {
            Call(window, "OpenDetail", box.Away.Team, player); var coordinator = (PlayerDetailCoordinator)Field(window, "_detailCoordinator");
            for (var i = 0; i < 300 && coordinator.State.Snapshot is null; i++)
            {
                if (coordinator.State.HasError && coordinator.State.Message.StartsWith("详情数据源异常", StringComparison.Ordinal)) break;
                await Pump();
            }
            if (coordinator.State.Snapshot?.Shots.Count != (alias ? 19 : 13)) throw new Exception("Actual detail source failed: " + coordinator.State.Message);
            if (alias && (coordinator.State.Snapshot.Find(0)!.Statistics is not { Points: 31, Rebounds: 12, Assists: 6, FieldGoals: "9-19" } ||
                coordinator.State.Snapshot.Number != "1" || coordinator.State.Snapshot.Shots.Count(s => s.Made) != 9)) throw new Exception("Alias detail totals differ from verified game");
            for (var i = 0; i < 30 && coordinator.State.Portrait is null; i++) await Pump();
            var panel = (FrameworkElement)Field(window, "_detail"); var image = (Border)Field(panel, "_portrait");
            var portrait = (image.Background as ImageBrush)?.ImageSource as BitmapSource;
            if (portrait is null && (!alias || ((FrameworkElement)Field(panel, "_jersey")).Visibility != Visibility.Visible)) throw new Exception("Actual portrait or jersey fallback was not rendered");
            if (settings.Theme == AppTheme.Warm && portrait is not null)
            {
                Directory.CreateDirectory("artifacts/player-detail-review"); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(portrait));
                using var file = File.Create("artifacts/player-detail-review/decoded-avatar.png"); png.Save(file);
            }
            var scroll = (ScrollViewer)Field(panel, "_scroll"); scroll.ScrollToVerticalOffset(65); await Pump();
            var name = alias ? "actual-lendeborg" : "actual-towns";
            Shot(window, name + "-" + settings.Theme);
            scroll.ScrollToVerticalOffset(300); await Pump(); Shot(window, name + "-shooting-" + settings.Theme);
            scroll.ScrollToBottom(); await Pump(); Shot(window, name + "-bottom-" + settings.Theme);
            return 2;
        }
        finally { window.Close(); }
    }
    sealed class Stats(BoxScoreSnapshot snapshot) : IStatisticsSource
    { public Task<BoxScoreSnapshot> GetBoxScore(string id, CancellationToken ct) => Task.FromResult(snapshot with { Game = snapshot.Game with { Id = id } }); }
    sealed class Detail(PlayerGameDetailSnapshot snapshot) : IPlayerGameDetailSource
    {
        public int Calls;
        public Func<PlayerDetailQuery, CancellationToken, Task<PlayerGameDetailSnapshot>>? Read;
        public Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct) { Calls++; return Read is { } read ? read(query, ct) : Task.FromResult(snapshot with { Query = query, Name = query.Player.Name }); }
    }
}
