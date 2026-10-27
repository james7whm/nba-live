using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace NbaLive;

internal sealed class PlayerDetailPanel : Border
{
    private readonly TextBlock _name = Ui.Text("球员本场", 20), _identity = Ui.Text("", 11), _status = Ui.Text("", 10);
    private readonly Border _portrait = new() { Width = 52, Height = 64, CornerRadius = new(5), Visibility = Visibility.Collapsed };
    private readonly JerseyBadge _jersey = new() { Width = 36, Height = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly TeamLogo _logo = new() { Width = 18, Height = 18, Margin = new(0, 0, 6, 0) };
    private readonly Button _follow, _close;
    private readonly WrapPanel _tabs = new() { Margin = new(0, 12, 0, 12) };
    private readonly StackPanel _figures = new();
    private readonly ShotCourt _court = new();
    private readonly ScrollViewer _scroll;
    private readonly HashSet<string> _favorites = [];
    private PlayerDetailState? _state;
    private PlayerGameDetailSnapshot? _rendered;
    private string? _key;
    private byte[]? _imageBytes;
    private int _period;
    internal event Action? CloseRequested, RefreshRequested;
    internal event Action<FavoritePlayer>? FollowRequested;
    internal int SelectedPeriod => _period;
    internal PlayerDetailPanel()
    {
        Background = Ui.Brush("CardSurface"); BorderBrush = Ui.Brush("LineBrush"); BorderThickness = new(1); CornerRadius = new(10); Padding = new(14);
        Margin = new(12, 0, 0, 0); Visibility = Visibility.Collapsed;
        var root = new DockPanel();
        var actions = new DockPanel { Margin = new(0, 0, 0, 12) };
        _close = Ui.Button("收起详情 ×", () => CloseRequested?.Invoke()); _close.Style = (Style)Application.Current.FindResource("IconButton");
        _close.Margin = new(0); DockPanel.SetDock(_close, Dock.Right); actions.Children.Add(_close);
        var title = Ui.Text("本场球员详情", 12); title.Margin = new(0); title.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(title);
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var footer = new DockPanel { Margin = new(0, 12, 0, 0) };
        var refresh = Ui.Button("刷新", () => RefreshRequested?.Invoke()); refresh.Style = (Style)Application.Current.FindResource("IconButton"); refresh.Margin = new(6, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Right); footer.Children.Add(refresh); _status.Margin = new(0); footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel(); var header = new DockPanel();
        var picture = new Grid { Width = 56, Height = 68, Margin = new(0, 0, 12, 0) }; picture.Children.Add(_jersey); picture.Children.Add(_portrait);
        DockPanel.SetDock(picture, Dock.Left); header.Children.Add(picture);
        _follow = Ui.Button("☆", Follow); _follow.Style = (Style)Application.Current.FindResource("IconButton"); _follow.FontSize = 22; _follow.Margin = new(6, 0, 0, 0);
        DockPanel.SetDock(_follow, Dock.Right); header.Children.Add(_follow);
        var bio = new StackPanel(); _name.Margin = new(0, 0, 0, 6); bio.Children.Add(_name);
        var team = new DockPanel(); team.Children.Add(_logo); _identity.Margin = new(0); _identity.VerticalAlignment = VerticalAlignment.Center; team.Children.Add(_identity); bio.Children.Add(team);
        header.Children.Add(bio); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(_tabs, Dock.Top); root.Children.Add(_tabs); content.Children.Add(_figures);
        _scroll = new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _scroll.SizeChanged += (_, _) =>
        {
            if (_scroll.ActualHeight <= 0 || _scroll.ActualWidth <= 0) return;
            var height = Math.Max(100, Math.Min(320, Math.Min(_scroll.ActualHeight - 6, (_scroll.ActualWidth - 20) * 646 / 690)));
            _court.Height = height; _court.Width = height * 690 / 646;
        };
        root.Children.Add(_scroll); Child = root;
    }
    internal void SetFavorites(IEnumerable<FavoritePlayer> favorites)
    {
        _favorites.Clear(); foreach (var player in favorites) _favorites.Add(player.Id); UpdateStar();
    }
    private void UpdateStar()
    {
        var followed = _state?.Query is { } query && _favorites.Contains(query.Player.Id);
        _follow.Content = followed ? "★" : "☆"; _follow.Foreground = Ui.Brush(followed ? "AccentInk" : "MutedInk");
        _follow.ToolTip = followed ? "取消关注" : "关注球员（最多 3 人）";
    }
    private void Follow() { if (_state?.Query is { } q) FollowRequested?.Invoke(new(q.Player.Id, q.Player.Name)); }
    internal void SetNarrow(bool value) { _close.Content = value ? "‹ 返回列表" : "收起详情 ×"; Margin = value ? new(0) : new(12, 0, 0, 0); }
    internal void Render(PlayerDetailState state, bool demo)
    {
        _state = state;
        if (state.Query is not { } query) { _key = null; _rendered = null; _figures.Children.Clear(); _tabs.Children.Clear(); return; }
        var changed = _key != query.Key;
        if (changed)
        {
            _key = query.Key; _period = 0; _rendered = null; _imageBytes = null; _portrait.Background = null; _portrait.Visibility = Visibility.Collapsed; _jersey.Visibility = Visibility.Visible; _scroll.ScrollToTop();
        }
        _name.Text = state.Snapshot?.Name ?? query.Player.Name;
        _logo.Team = query.Team; _jersey.Number = state.Snapshot?.Number ?? query.Player.JerseyNumber ?? "—";
        _jersey.TeamColor = TeamPalette.Resolve(query.Team); _jersey.Presence = query.Player.IsStarter ? CourtPresence.Starter : CourtPresence.OffCourt;
        _identity.Text = query.Team + " · " + (state.Snapshot?.Position ?? query.Player.Position) + " · " + _jersey.Number + "号";
        if (state.Portrait is { } bytes && !ReferenceEquals(bytes, _imageBytes))
        {
            _imageBytes = bytes;
            try
            {
                using var input = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96; image.StreamSource = input; image.EndInit(); image.Freeze();
                _portrait.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
                _portrait.Visibility = Visibility.Visible; _jersey.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or System.IO.FileFormatException) { }
        }
        UpdateStar(); _status.Foreground = Ui.Brush(state.HasError ? "RedInk" : "MutedInk");
        _status.Text = (demo ? "演示数据 · " : "") + state.Message + (state.Snapshot is { } fetched ? "\n更新 " + MatchTime.Format(fetched.FetchedAt) : "");
        if (state.Snapshot is null || !ReferenceEquals(_rendered, state.Snapshot) || changed)
        {
            _rendered = state.Snapshot; BuildFigures();
        }
    }
    private void BuildFigures()
    {
        var offset = _scroll.VerticalOffset; _tabs.Children.Clear(); _figures.Children.Clear();
        if (_rendered is not { } snapshot) { _figures.Children.Add(Ui.Text(_state?.HasError == true ? "本场详情暂不可用，可稍后刷新。" : "正在读取本场统计与投篮分布…", 12)); return; }
        var periods = new[] { 0, 1, 2, 3, 4 }.Concat(snapshot.Periods.Where(p => p.Period > 4).Select(p => p.Period)).Distinct().OrderBy(p => p).ToArray();
        foreach (var period in periods)
        {
            var button = Ui.Button(period == 0 ? "全场" : period <= 4 ? $"{period}" : $"加{period - 4}", () => { _period = period; BuildFigures(); });
            button.Style = (Style)Application.Current.FindResource("IconButton"); button.Margin = new(0, 0, 4, 4); button.Padding = new(10, 6, 10, 6);
            button.Background = Ui.Brush(period == _period ? "AccentTint" : "SoftBrush"); button.Foreground = Ui.Brush(period == _period ? "AccentInk" : "MutedInk");
            button.IsEnabled = snapshot.Find(period) is not null; button.ToolTip = period == 0 ? "全场" : period <= 4 ? $"第{period}节" : $"加时{period - 4}";
            _tabs.Children.Add(button);
        }
        var current = snapshot.Find(_period);
        if (current is null) { _figures.Children.Add(Ui.Text("该节统计尚未提供", 12)); return; }
        var p = current.Statistics; string N(int? value) => value?.ToString() ?? "—";
        var main = new UniformGrid { Columns = 3, Margin = new(0, 0, 0, 8) };
        foreach (var value in new[] { (N(p.Points), "得分"), (N(p.Rebounds), "篮板"), (N(p.Assists), "助攻") }) main.Children.Add(Metric(value.Item1, value.Item2, 24));
        _figures.Children.Add(main);
        var secondary = new UniformGrid { Columns = 4, Margin = new(0, 0, 0, 12) };
        foreach (var value in new[] { (p.Minutes, "时间"), (N(p.Steals), "抢断"), (N(p.Blocks), "盖帽"), (N(p.Fouls), "犯规"), (N(p.Turnovers), "失误"), (N(p.OffensiveRebounds), "前场板"), (N(p.DefensiveRebounds), "后场板"), (p.PlusMinus?.ToString("+0;-0;0") ?? "—", "+/-") }) secondary.Children.Add(Metric(value.Item1, value.Item2, 14));
        if (p.Status.Length > 0) _figures.Children.Add(Ui.Text(p.Status, 11));
        _court.Update(snapshot, _period); _figures.Children.Add(_court);
        var legend = Ui.Text("● 命中　○ 未中 · 悬停查看投篮", 10); legend.Margin = new(0, 6, 0, 12); _figures.Children.Add(legend);
        var shooting = new UniformGrid { Columns = 5, Margin = new(0, 0, 0, 10) };
        foreach (var value in new[] { (p.FieldGoals.Replace('-', '/'), "投篮"), (StatisticsPresets.DisplayPercentage(StatisticsPresets.Percentage(p.FieldGoals)), "命中率"), (PlayerGamePeriod.TwoPointers(p.FieldGoals, p.ThreePointers), "两分"), (p.ThreePointers.Replace('-', '/'), "三分"), (p.FreeThrows.Replace('-', '/'), "罚球") })
        {
            var metric = (FrameworkElement)Metric(value.Item1, value.Item2, 14);
            metric.ToolTip = value.Item2 + " " + value.Item1 + (value.Item2 == "命中率" ? "" : " · 命中率 " + StatisticsPresets.DisplayPercentage(StatisticsPresets.Percentage(value.Item1.Replace('/', '-'))));
            shooting.Children.Add(metric);
        }
        _figures.Children.Add(shooting);
        var types = new UniformGrid { Columns = 3, Margin = new(0, 0, 0, 12) }; foreach (var type in current.ShotTypes) types.Children.Add(Metric(type.Display, type.Name, 15)); _figures.Children.Add(types);
        _figures.Children.Add(secondary);
        var note = Ui.Text(snapshot.Notice, 10); note.Margin = new(0); _figures.Children.Add(note);
        Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }
    private static UIElement Metric(string value, string label, double size)
    {
        var panel = new StackPanel { Margin = new(2, 4, 2, 6) };
        var number = Ui.Text(value, size); number.Margin = new(0, 0, 0, 4); number.TextAlignment = TextAlignment.Center; panel.Children.Add(number);
        var text = Ui.Text(label, 10); text.Margin = new(0); text.TextAlignment = TextAlignment.Center; panel.Children.Add(text); return panel;
    }
}

internal sealed class ShotCourt : FrameworkElement
{
    private IReadOnlyList<PlayerShot> _shots = [];
    private bool _zero;
    private bool _whole;
    internal IReadOnlyList<PlayerShot> ShownShots => _shots;
    internal ShotCourt()
    {
        ClipToBounds = true; MinHeight = 100; HorizontalAlignment = HorizontalAlignment.Center;
        Loaded += (_, _) => ThemeManager.Changed += RefreshTheme; Unloaded += (_, _) => ThemeManager.Changed -= RefreshTheme;
        MouseMove += (_, e) =>
        {
            var position = e.GetPosition(this); var (scale, x, y) = Layout();
            var hits = _shots.Where(s => Math.Pow(s.X * scale + x - position.X, 2) + Math.Pow(s.Y * scale + y - position.Y, 2) <= 64).ToArray();
            ToolTip = hits.Length == 0 ? null : string.Join("\n", hits.Take(8).Select(s => (s.Period <= 4 ? $"第{s.Period}节" : $"加时{s.Period - 4}") + " · " + (s.Made ? "命中" : "未中") + (s.Clock.Length > 0 ? " · " + s.Clock : "") + (s.Kind.Length > 0 ? " · " + s.Kind : ""))) + (hits.Length > 8 ? $"\n另有 {hits.Length - 8} 次投篮" : "");
        };
    }
    private void RefreshTheme() => InvalidateVisual();
    internal void Update(PlayerGameDetailSnapshot snapshot, int period)
    {
        _shots = snapshot.Shots.Where(s => period == 0 || s.Period == period).ToArray(); _zero = snapshot.Find(period)?.Statistics.FieldGoals == "0-0"; _whole = period == 0;
        ToolTip = null; InvalidateMeasure(); InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 390 : availableSize.Width; return new(width, Math.Min(320, width * 646 / 690));
    }
    private (double Scale, double X, double Y) Layout()
    {
        var scale = Math.Max(.01, Math.Min(ActualWidth / 690, ActualHeight / 646)); return (scale, (ActualWidth - 690 * scale) / 2, (ActualHeight - 646 * scale) / 2);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRoundedRectangle(Ui.Brush("SoftBrush"), null, new Rect(RenderSize), 8, 8);
        var (scale, x, y) = Layout(); dc.PushTransform(new TranslateTransform(x, y)); dc.PushTransform(new ScaleTransform(scale, scale));
        var color = ((SolidColorBrush)Ui.Brush("MutedInk")).Color; var line = new Pen(new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)), 1.2 / scale);
        dc.DrawRectangle(null, line, new(12, 0, 666, 646)); dc.DrawRectangle(null, line, new(236, 0, 218, 260));
        dc.DrawEllipse(null, line, new(345, 260), 82, 82); dc.DrawEllipse(null, line, new(345, 646), 90, 90);
        dc.DrawLine(line, new(320, 48), new(370, 48)); dc.DrawLine(line, new(345, 48), new(345, 56)); dc.DrawEllipse(null, line, new(345, 64), 8, 8);
        dc.DrawLine(line, new(42, 0), new(42, 230)); dc.DrawLine(line, new(648, 0), new(648, 230));
        var arc = new StreamGeometry(); using (var path = arc.Open()) { path.BeginFigure(new(42, 230), false, false); path.BezierTo(new(93, 470), new(597, 470), new(648, 230), true, false); }
        dc.DrawGeometry(null, line, arc);
        var accent = Ui.Brush("AccentBrush"); var marker = new Pen(accent, 1.4 / scale);
        foreach (var shot in _shots) dc.DrawEllipse(shot.Made ? accent : Ui.Brush("SoftBrush"), marker, new(shot.X, shot.Y), 3.7 / scale, 3.7 / scale);
        dc.Pop(); dc.Pop();
        if (_shots.Count == 0)
        {
            var message = new FormattedText(_zero ? _whole ? "本场无出手" : "本节无出手" : "暂无投篮落点", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 12, Ui.Brush("MutedInk"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(message, new((ActualWidth - message.Width) / 2, ActualHeight * .55));
        }
    }
}

internal sealed class DemoPlayerDetailSource : IPlayerGameDetailSource
{
    public Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var player = query.Player;
        var pair = player.FieldGoals.Split('-'); var made = pair.Length == 2 && int.TryParse(pair[0], out var m) ? m : 4; var attempts = pair.Length == 2 && int.TryParse(pair[1], out var a) ? a : 8;
        var shots = Enumerable.Range(0, attempts).Select(i => new PlayerShot(i, i % 4 + 1, 120 + i * 67 % 450, 75 + i * 47 % 450, i < made)).ToArray();
        var periods = new List<PlayerGamePeriod> { new(0, player, [new("跳投", made, attempts)]) };
        for (var p = 1; p <= 4; p++)
        {
            var events = shots.Where(s => s.Period == p).ToArray();
            periods.Add(new(p, player with { Minutes = "8:00", FieldGoals = $"{events.Count(s => s.Made)}-{events.Length}", Points = events.Count(s => s.Made) * 2,
                Rebounds = null, Assists = null, Steals = null, Blocks = null, Fouls = null, Turnovers = null, OffensiveRebounds = null, DefensiveRebounds = null, PlusMinus = null,
                ThreePointers = "—", FreeThrows = "—" }, [new("跳投", events.Count(s => s.Made), events.Length)]));
        }
        return Task.FromResult(new PlayerGameDetailSnapshot(query, "demo-match", player.Id, player.Name, player.Position, player.JerseyNumber ?? "—", null, periods, shots, "演示数据，非真实比赛落点。", DateTimeOffset.UtcNow));
    }
}
