using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace NbaLive;

internal sealed class GameScoreView : Grid
{
    private readonly Grid _table = new();
    private readonly ScrollViewer _tableScroll;
    private readonly ScoreChart _chart = new() { MinHeight = 120, Margin = new(0, 4, 0, 0) };
    private readonly TextBlock _message = Ui.Text("等待本场文字比分", 11);
    private IReadOnlyList<LiveEntry>? _history;
    private GameScoringSnapshot? _scoring;
    internal GameScoreView()
    {
        Margin = new(18, 12, 18, 12);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        _tableScroll = new() { Content = _table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _tableScroll.SizeChanged += (_, _) => SizeTable();
        Children.Add(_tableScroll);
        var help = Ui.Text("累计得分 · 实线为客队，虚线为主队 · 悬停查看记录时间与双方比分", 11);
        help.Margin = new(0, 10, 0, 0); help.TextWrapping = TextWrapping.Wrap;
        SetRow(help, 1); Children.Add(help);
        SetRow(_chart, 2); Children.Add(_chart);
        _message.TextWrapping = TextWrapping.Wrap; _message.Margin = new(0, 5, 0, 0);
        SetRow(_message, 3); Children.Add(_message);
    }
    internal void Update(SessionState? live, StatisticsState statistics)
    {
        var selected = statistics.SelectedGame ?? statistics.Snapshot?.Game ?? live?.SelectedGame ?? live?.Snapshot?.Game;
        var matches = selected is not null && live?.Snapshot?.Game.Id == selected.Id && (live.SelectedGame?.Id ?? selected.Id) == selected.Id;
        var game = matches ? live!.Snapshot!.Game : selected;
        var history = matches ? live!.History : Array.Empty<LiveEntry>();
        if (game is null)
        {
            _scoring = null; _history = null; _table.Children.Clear(); _chart.SetData(null, TeamPalette.Unknown, TeamPalette.Unknown);
            _message.Text = "选择比赛后查看分节得分和累计得分图。"; return;
        }
        if (!ReferenceEquals(_history, history) || _scoring?.Game != game)
        {
            _history = history; _scoring = GameScoring.Build(game, history);
            var sameStats = statistics.Snapshot?.Game.Id == game.Id ? statistics.Snapshot : null;
            var awayColor = TeamPalette.Resolve(game.Away, sameStats?.Away.RosterSlug);
            var homeColor = TeamPalette.Resolve(game.Home, sameStats?.Home.RosterSlug);
            BuildTable(_scoring, awayColor, homeColor);
            _chart.SetData(_scoring, awayColor, homeColor);
        }
        var notices = new List<string>();
        if (matches && live!.HasError) notices.Add($"{live.Message} · 保留最近文字比分");
        if (matches) notices.Add($"文字比分更新于 {MatchTime.Format(live!.Snapshot!.FetchedAt)}");
        if (_scoring!.Points.Count == 0) notices.Add(game.Status == GameStatus.Scheduled ? "比赛尚未开始，等待比分记录" : "暂无可绘制的文字比分记录");
        else
        {
            if (_scoring.HasGaps) notices.Add("记录不完整：缺少边界的分节显示 —，折线在缺口处断开");
            if (_scoring.ScoreMismatch) notices.Add("总分与文字记录暂不一致，折线以文字记录为准");
        }
        _message.Text = string.Join(" · ", notices);
    }
    private void SizeTable() => _table.Width = Math.Max(_tableScroll.ActualWidth, 140 + (_scoring?.Periods.Count + 1 ?? 5) * 68);
    private void BuildTable(GameScoringSnapshot data, TeamJerseyColor away, TeamJerseyColor home)
    {
        _table.Children.Clear(); _table.ColumnDefinitions.Clear(); _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Add(new() { Width = new(140) });
        for (var i = 0; i <= data.Periods.Count; i++) _table.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (var row = 0; row < 3; row++)
        {
            _table.RowDefinitions.Add(new() { Height = new(36) });
            var background = new Border { Background = Ui.Brush(row == 0 ? "SoftBrush" : row == 2 ? "CanvasBrush" : "CardBrush"),
                BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new(0, 0, 0, 1), CornerRadius = new(row == 0 ? 6 : 0) };
            SetRow(background, row); SetColumnSpan(background, data.Periods.Count + 2); _table.Children.Add(background);
        }
        Cell("球队", 0, 0);
        for (var i = 0; i < data.Periods.Count; i++)
        {
            var period = data.Periods[i];
            Cell(period.Label, 0, i + 1);
            Cell(period.Away?.ToString(CultureInfo.CurrentCulture) ?? "—", 1, i + 1, $"{period.Label} · {(period.IsComplete ? "已结束" : period.Away is null ? "尚无完整分节数据" : "本节进行中")}");
            Cell(period.Home?.ToString(CultureInfo.CurrentCulture) ?? "—", 2, i + 1, $"{period.Label} · {(period.IsComplete ? "已结束" : period.Home is null ? "尚无完整分节数据" : "本节进行中")}");
        }
        Cell("总分", 0, data.Periods.Count + 1);
        var latest = data.Points.LastOrDefault();
        Cell((data.Game.AwayScore ?? latest?.Away)?.ToString(CultureInfo.CurrentCulture) ?? "—", 1, data.Periods.Count + 1, bold: true);
        Cell((data.Game.HomeScore ?? latest?.Home)?.ToString(CultureInfo.CurrentCulture) ?? "—", 2, data.Periods.Count + 1, bold: true);
        Team(data.Game.Away, away, 1); Team(data.Game.Home, home, 2); SizeTable();
    }
    private void Cell(string text, int row, int column, string? tip = null, bool bold = false)
    {
        var label = Ui.Text(text, 12); label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new(0); label.TextWrapping = TextWrapping.NoWrap; label.TextAlignment = TextAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis; label.ToolTip = tip ?? text;
        if (row > 0) label.Foreground = Ui.Brush("Ink");
        if (bold) label.FontWeight = FontWeights.SemiBold;
        SetRow(label, row); SetColumn(label, column); _table.Children.Add(label);
    }
    private void Team(string name, TeamJerseyColor color, int row)
    {
        var panel = new DockPanel { Margin = new(6, 0, 6, 0), MaxWidth = 128, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = name };
        var dot = new TeamLogo { Team = name, Width = 22, Height = 22, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left); panel.Children.Add(dot);
        var label = Ui.Text(name, 12); label.Margin = new(0); label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(label);
        SetRow(panel, row); _table.Children.Add(panel);
    }
}

internal sealed class ScoreChart : FrameworkElement
{
    private GameScoringSnapshot? _data;
    private ScorePoint? _hover;
    private Rect _plot;
    private Geometry? _awayLine, _homeLine;
    private Brush _away = Brushes.Gray, _home = Brushes.Gray;
    private Pen _awayPen = new(Brushes.Gray, 2), _homePen = new(Brushes.Gray, 2);
    private double _ceiling = 20;
    private TeamJerseyColor _awayColor = TeamPalette.Unknown, _homeColor = TeamPalette.Unknown;
    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    internal ScoreChart()
    {
        Focusable = true; SizeChanged += (_, _) => { _awayLine = _homeLine = null; InvalidateVisual(); };
        Loaded += (_,_) => ThemeManager.Changed += RefreshTheme;
        Unloaded += (_,_) => ThemeManager.Changed -= RefreshTheme;
    }
    private void RefreshTheme() => SetData(_data,_awayColor,_homeColor);
    internal static Brush Brush(TeamJerseyColor color) { var brush = new SolidColorBrush(Color.FromRgb((byte)(color.Rgb >> 16), (byte)(color.Rgb >> 8), (byte)color.Rgb)); brush.Freeze(); return brush; }
    internal void SetData(GameScoringSnapshot? data, TeamJerseyColor away, TeamJerseyColor home)
    {
        _data = data; _hover = _hover is null ? null : data?.AtOrBefore(_hover.ElapsedSeconds);
        _awayColor = away; _homeColor = home;
        _away = new SolidColorBrush(ThemeManager.ChartColor(away)); _home = new SolidColorBrush(ThemeManager.ChartColor(home));
        _awayPen = new(_away, 2); _awayPen.Freeze();
        _homePen = new(_home, 2) { DashStyle = new DashStyle(new double[] { 4, 2 }, 0) }; _homePen.Freeze();
        _awayLine = _homeLine = null;
        AutomationProperties.SetName(this, data is null ? "累计得分图，暂无比分记录" : $"累计得分图 · {data.Game.Score} · {data.Points.Count} 个比分点；左右方向键查看记录，Home／End 查看首末记录");
        InvalidateVisual();
    }
    private void Text(DrawingContext dc, string value, Point at, Brush? ink = null, double width = 260, double size = 11)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, ink ?? Ui.Brush("MutedInk"), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, at);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 100 || ActualHeight < 85) return;
        _plot = new(42, 35, ActualWidth - 58, ActualHeight - 68);
        var data = _data;
        if (data is null || data.Points.Count == 0)
        {
            Text(dc, "暂无可绘制的文字比分记录", new(44, 65), width: ActualWidth - 70, size: 13); return;
        }
        var maximum = Math.Max(data.Game.AwayScore ?? 0, data.Game.HomeScore ?? 0);
        maximum = Math.Max(maximum, data.Points.Max(p => Math.Max(p.Away, p.Home)));
        _ceiling = Math.Max(20, Math.Ceiling(maximum / 20d) * 20);
        var grid = new Pen(Ui.Brush("LineBrush"), .6);
        for (var period = 1; period <= data.Periods.Count; period++)
        {
            var left = X(GameScoring.PeriodStart(period)); var right = X(GameScoring.PeriodStart(period) + GameScoring.PeriodLength(period));
            if (period % 2 == 0) dc.DrawRectangle(Ui.Brush("ChartBand"), null, new(left, _plot.Top, right - left, _plot.Height));
            dc.DrawLine(grid, new(left, _plot.Top), new(left, _plot.Bottom));
            var labelWidth = Math.Min(56, right - left);
            Text(dc, GameScoring.PeriodLabel(period), new((left + right - labelWidth) / 2, _plot.Bottom + 8), width: labelWidth);
        }
        for (var i = 0; i <= 4; i++)
        {
            var score = _ceiling * i / 4; var y = Y(score);
            dc.DrawLine(grid, new(_plot.Left, y), new(_plot.Right, y)); Text(dc, score.ToString("0", CultureInfo.CurrentCulture), new(2, y - 8), width: 35);
        }
        Logo(dc, data.Game.Away, new(44, 6, 18, 18));
        dc.DrawLine(_awayPen, new(69, 15), new(89, 15)); Text(dc, data.Game.Away + "（客）", new(96, 6), width: Math.Min(165, (ActualWidth - 150) / 2));
        var legend = Math.Min(290, ActualWidth / 2); Logo(dc, data.Game.Home, new(legend, 6, 18, 18));
        dc.DrawLine(_homePen, new(legend + 25, 15), new(legend + 45, 15));
        Text(dc, data.Game.Home + "（主）", new(legend + 52, 6), width: ActualWidth - legend - 57);
        _awayLine ??= Line(data, away: true); _homeLine ??= Line(data, away: false);
        dc.PushClip(new RectangleGeometry(_plot)); dc.DrawGeometry(null, _awayPen, _awayLine); dc.DrawGeometry(null, _homePen, _homeLine); dc.Pop();
        var last = data.Points[^1];
        dc.DrawEllipse(_away, new Pen(Brushes.White, 1), new(X(last.ElapsedSeconds), Y(last.Away)), 2.5, 2.5);
        dc.DrawEllipse(_home, new Pen(Brushes.White, 1), new(X(last.ElapsedSeconds), Y(last.Home)), 2.5, 2.5);
        if (_hover is { } hover)
        {
            var x = X(hover.ElapsedSeconds);
            dc.DrawLine(new Pen(Brushes.Gray, .8) { DashStyle = DashStyles.Dot }, new(x, _plot.Top), new(x, _plot.Bottom));
            dc.DrawEllipse(_away, new Pen(Brushes.White, 1), new(x, Y(hover.Away)), 3.5, 3.5);
            dc.DrawEllipse(_home, new Pen(Brushes.White, 1), new(x, Y(hover.Home)), 3.5, 3.5);
            var width = Math.Min(270, _plot.Width); var left = Math.Clamp(x + 12, _plot.Left, _plot.Right - width);
            dc.DrawRoundedRectangle(Ui.Brush("CardBrush"), new Pen(Ui.Brush("LineBrush"), 1), new(left, _plot.Top + 8, width, 79), 8, 8);
            Text(dc, "记录时间：" + hover.TimeLabel, new(left + 9, _plot.Top + 14), width: width - 18);
            Logo(dc, data.Game.Away, new(left + 9, _plot.Top + 36, 17, 17));
            Text(dc, $"{data.Game.Away}   {hover.Away}", new(left + 33, _plot.Top + 35), Ui.Brush("Ink"), width - 42, 12);
            Logo(dc, data.Game.Home, new(left + 9, _plot.Top + 59, 17, 17));
            Text(dc, $"{data.Game.Home}   {hover.Home}", new(left + 33, _plot.Top + 58), Ui.Brush("Ink"), width - 42, 12);
        }
    }
    private static void Logo(DrawingContext dc, string team, Rect at)
    {
        if(TeamLogo.Get(team) is { } image) dc.DrawImage(image, at);
    }
    private double X(double elapsed) => _plot.Left + elapsed / _data!.DurationSeconds * _plot.Width;
    private double Y(double score) => _plot.Bottom - score / _ceiling * _plot.Height;
    private Geometry Line(GameScoringSnapshot data, bool away)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            ScorePoint? previous = null;
            foreach (var point in data.Points)
            {
                var position = new Point(X(point.ElapsedSeconds), Y(away ? point.Away : point.Home));
                if (previous is null || point.BreakBefore) context.BeginFigure(position, false, false);
                else { context.LineTo(new(position.X, Y(away ? previous.Away : previous.Home)), true, false); context.LineTo(position, true, false); }
                previous = point;
            }
        }
        geometry.Freeze(); return geometry;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hover = _data is { Points.Count: > 0 } && _plot.Contains(point) ?
            _data.AtOrBefore(Math.Min(_data.Points[^1].ElapsedSeconds, (point.X - _plot.Left) / _plot.Width * _data.DurationSeconds)) : null;
        if (hover == _hover) return;
        _hover = hover; InvalidateVisual();
        AutomationProperties.SetHelpText(this, hover is null ? "该时间暂无已知比分" : $"{hover.TimeLabel} · {_data!.Game.Away} {hover.Away} : {hover.Home} {_data.Game.Home}");
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_data is not { Points.Count: > 0 } || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var index = _hover is null ? -1 : _data.Points.ToList().IndexOf(_hover);
        index = e.Key switch { Key.Home => 0, Key.End => _data.Points.Count - 1, Key.Left => Math.Max(0, index - 1), _ => Math.Min(_data.Points.Count - 1, index + 1) };
        _hover = _data.Points[index]; InvalidateVisual(); e.Handled = true;
        AutomationProperties.SetHelpText(this, $"{_hover.TimeLabel} · {_data.Game.Away} {_hover.Away} : {_hover.Home} {_data.Game.Home}");
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
