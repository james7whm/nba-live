using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace NbaLive;

internal sealed class StatisticsWindow : Window
{
    private readonly StatisticsCoordinator _coordinator;
    private readonly Func<FavoritePlayer, string?> _toggleFavorite;
    private readonly bool _demo;
    private readonly TextBlock _heading = Ui.Text("球员技术统计", 20);
    private readonly MatchScoreLine _matchHeading = new(22,30,true);
    internal event Action<string>? TeamRequested;
    private readonly TextBlock _progress = Ui.Text("", 12);
    private readonly TextBlock _status = Ui.Text("请选择比赛后查看球员统计", 11);
    private readonly Button _refresh;
    private readonly ComboBox _presetPicker;
    private readonly Func<StatisticsPreset, StatisticsTableLayout[], string?> _savePreferences;
    private readonly List<StatisticsTableLayout> _layouts;
    private StatisticsPreset _preset;
    private readonly TeamSheet _away = new();
    private readonly TeamSheet _home = new();
    private readonly GameScoreView _scores = new();
    private readonly TabItem _awayTab;
    private readonly TabItem _homeTab;
    private readonly Grid _body = new();
    private readonly PlayerDetailPanel _detail = new();
    private readonly TabControl _tabs;
    private PlayerDetailCoordinator? _detailCoordinator;
    private bool _closed;
    private SessionState? _liveState;
    private string _notice = "";

    internal StatisticsWindow(StatisticsCoordinator coordinator, bool demo, AppSettings settings,
        Func<FavoritePlayer, string?> toggleFavorite, Func<StatisticsPreset, StatisticsTableLayout[], string?> savePreferences)
    {
        _demo = demo;
        _matchHeading.TeamRequested+=team=>TeamRequested?.Invoke(team);
        _coordinator = coordinator;
        _toggleFavorite = toggleFavorite;
        _savePreferences = savePreferences; _preset = settings.StatisticsPreset;
        _layouts = StatisticsPresets.Normalize(settings.StatisticsLayouts).ToList();
        Ui.Initialize(this, "球员技术统计 · NBA 直播条", 1100, 650);
        var root = new DockPanel { Margin = new(24, 20, 24, 20) };
        var top = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _presetPicker = new() { ItemsSource = new[] { "完整 · 全部统计", "精简 · 时间／分板助", "投篮 · 命中率" },
            SelectedIndex = (int)_preset, Width = 170, FontSize = 12, Padding = new(6), Margin = new(0, 0, 8, 0),
            ToolTip = "分别记住每种预设、主客队的列宽、列顺序与排序；前两列固定。" };
        actions.Children.Add(_presetPicker);
        actions.Children.Add(Ui.Button("重置本预设", ResetPreset));
        _refresh = Ui.Button("刷新", () => _ = _coordinator.Refresh());
        actions.Children.Add(_refresh);
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        var title = new StackPanel();
        title.Children.Add(_matchHeading); _matchHeading.Margin = new(0,0,0,10);
        title.Children.Add(_progress);
        top.Children.Add(title);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _status.Margin = new(0, 12, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var filters = new DockPanel { Margin = new(0, 4, 0, 2) };
        var switches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var courtOnly = new CheckBox { Content = "场上五人", Margin = new(0, 0, 20, 0), ToolTip = "赛前和赛后使用首发参考；在场状态未知时不猜测。" };
        var followedOnly = new CheckBox { Content = "只看关注", Margin = new(0) };
        switches.Children.Add(courtOnly); switches.Children.Add(followedOnly);
        DockPanel.SetDock(switches, Dock.Right); filters.Children.Add(switches);
        var note = Ui.Text("球衣标首发 · 红球与红字标在场", 11); note.Margin = new(0); note.VerticalAlignment = VerticalAlignment.Center;
        filters.Children.Add(note); DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters);
        void Filter()
        {
            _away.SetFilters(courtOnly.IsChecked == true, followedOnly.IsChecked == true);
            _home.SetFilters(courtOnly.IsChecked == true, followedOnly.IsChecked == true);
        }
        courtOnly.Checked += (_, _) => Filter(); courtOnly.Unchecked += (_, _) => Filter();
        followedOnly.Checked += (_, _) => Filter(); followedOnly.Unchecked += (_, _) => Filter();
        var tabs = _tabs = new TabControl();
        tabs.SelectionChanged += (_, e) => { if (!ReferenceEquals(e.Source, tabs)) return; switches.IsEnabled = tabs.SelectedIndex != 2; CloseDetail(); };
        _awayTab = new() { Header = "客队", Content = _away, Padding = new(18, 8, 18, 8) };
        _homeTab = new() { Header = "主队", Content = _home, Padding = new(18, 8, 18, 8) };
        tabs.Items.Add(_awayTab);
        tabs.Items.Add(_homeTab);
        var scoreHost = new ScrollViewer { Content = _scores, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scoreHost.SizeChanged += (_, _) => _scores.Height = Math.Max(280, scoreHost.ActualHeight - _scores.Margin.Top - _scores.Margin.Bottom);
        tabs.Items.Add(new TabItem { Header = "球队得分", Content = scoreHost, Padding = new(18, 8, 18, 8) });
        _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _body.Children.Add(tabs); Grid.SetColumn(_detail, 1); _body.Children.Add(_detail); root.Children.Add(_body);
        _body.SizeChanged += (_, _) => ArrangeDetail();
        _away.PlayerRequested += player => OpenDetail(_coordinator.State.Snapshot?.Away.Team, player);
        _home.PlayerRequested += player => OpenDetail(_coordinator.State.Snapshot?.Home.Team, player);
        _detail.CloseRequested += CloseDetail; _detail.RefreshRequested += () => _detailCoordinator?.Refresh(); _detail.FollowRequested += ToggleFavorite;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _detail.Visibility == Visibility.Visible) { CloseDetail(); e.Handled = true; } };
        IsVisibleChanged += (_, _) => _detailCoordinator?.SetVisible(IsVisible && _detail.Visibility == Visibility.Visible);
        Content = root;
        _away.FollowRequested += ToggleFavorite;
        _home.FollowRequested += ToggleFavorite;
        SetFavorites(settings.FollowedPlayers);
        _away.LayoutChanged += layout => SaveLayout(layout with { Home = false });
        _home.LayoutChanged += layout => SaveLayout(layout with { Home = true });
        ApplyPreset();
        _presetPicker.SelectionChanged += (_, _) =>
        {
            _away.FlushLayout(); _home.FlushLayout();
            _preset = (StatisticsPreset)_presetPicker.SelectedIndex;
            ApplyPreset(); SavePreferences();
        };
        _coordinator.Changed += OnState;
        Closed += (_, _) => { _away.Stop(); _home.Stop(); _closed = true; _detailCoordinator?.Dispose(); _coordinator.Changed -= OnState; };
        OnState(_coordinator.State);
    }
    private void ApplyPreset()
    {
        _away.ApplyPreset(_preset, _layouts.FirstOrDefault(l => l.Preset == _preset && !l.Home));
        _home.ApplyPreset(_preset, _layouts.FirstOrDefault(l => l.Preset == _preset && l.Home));
    }
    private void SaveLayout(StatisticsTableLayout layout)
    {
        _layouts.RemoveAll(l => l.Preset == layout.Preset && l.Home == layout.Home);
        _layouts.Add(layout); SavePreferences();
    }
    private void SavePreferences()
    {
        var error = _savePreferences(_preset, _layouts.ToArray());
        if (error is not null) { _notice = error; if (!_closed) OnState(_coordinator.State); }
    }
    private void ResetPreset()
    {
        _away.CancelLayoutSave(); _home.CancelLayoutSave();
        _layouts.RemoveAll(l => l.Preset == _preset); ApplyPreset(); SavePreferences();
    }
    internal void SetFavorites(IReadOnlyList<FavoritePlayer> favorites)
    {
        _away.SetFavorites(favorites); _home.SetFavorites(favorites);
        _detail.SetFavorites(favorites);
    }
    internal void ConfigureDetails(IPlayerGameDetailSource source)
    {
        _detailCoordinator?.Dispose(); _detailCoordinator = new(source);
        _detailCoordinator.Changed += state =>
        {
            if (_closed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => RenderDetail(state)); return; }
            RenderDetail(state);
        };
    }
    private void RenderDetail(PlayerDetailState state)
    {
        if (!_closed && ReferenceEquals(state, _detailCoordinator?.State)) _detail.Render(state, _demo);
    }
    private void OpenDetail(string? team, PlayerStatistics player)
    {
        if (team is null || _coordinator.State.Snapshot is not { } snapshot || _detailCoordinator is null) return;
        var query = new PlayerDetailQuery(snapshot.Game, team, player) { StatisticsFetchedAt = snapshot.FetchedAt };
        if (_detail.Visibility == Visibility.Visible && _detailCoordinator.State.Query?.Key == query.Key) return;
        _detail.Visibility = Visibility.Visible; ArrangeDetail(); _detailCoordinator.Select(query);
    }
    private void CloseDetail()
    {
        _detail.Visibility = Visibility.Collapsed; _tabs.Visibility = Visibility.Visible; _detailCoordinator?.Close(); ArrangeDetail();
    }
    private void ArrangeDetail()
    {
        if (_body.ColumnDefinitions.Count < 2) return;
        var open = _detail.Visibility == Visibility.Visible; var narrow = _body.ActualWidth < 900;
        _detail.Width = narrow ? double.NaN : 440; _detail.SetNarrow(narrow);
        Grid.SetColumn(_detail, narrow ? 0 : 1); _tabs.Visibility = open && narrow ? Visibility.Collapsed : Visibility.Visible;
        _body.ColumnDefinitions[1].Width = open && !narrow ? GridLength.Auto : new(0);
    }
    internal void SetLiveState(SessionState state)
    {
        _liveState = state;
        UpdateCourtIndicators();
        _scores.Update(_liveState, _coordinator.State);
    }
    private void UpdateCourtIndicators()
    {
        if (_coordinator.State.Snapshot is not { } stats) return;
        var away = CourtLineups.Resolve(stats.Away, stats.Game, _liveState, DateTimeOffset.UtcNow);
        var home = CourtLineups.Resolve(stats.Home, stats.Game, _liveState, DateTimeOffset.UtcNow);
        _away.SetCourtStates(away); _home.SetCourtStates(home);
    }
    private void ToggleFavorite(FavoritePlayer favorite)
    {
        _notice = _toggleFavorite(favorite) ?? "关注列表已更新 · 最多 3 人，可在悬浮条切换到球员卡";
        OnState(_coordinator.State);
    }
    private void OnState(StatisticsState state)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnState(state)); return; }
        if (!ReferenceEquals(state, _coordinator.State)) return;
        var game = state.Snapshot?.Game ?? state.SelectedGame;
        if (_detailCoordinator?.State.Query is { } selected)
        {
            if (game is null || game.Id != selected.Game.Id) CloseDetail();
            else
            {
                _detailCoordinator.UpdateGame(game);
                var updatedPlayer = (state.Snapshot?.Away.Team == selected.Team ? state.Snapshot.Away : state.Snapshot?.Home)?.Players.FirstOrDefault(p => p.Id == selected.Player.Id);
                if (updatedPlayer is not null) _detailCoordinator.UpdatePlayer(selected with { Game = game, Player = updatedPlayer, StatisticsFetchedAt = state.Snapshot?.FetchedAt });
            }
        }
        _heading.Text = game?.Score ?? "球员技术统计";
        _matchHeading.Update(game);
        _progress.Text = game is null ? "选择比赛后，此窗口随当前场次更新" :
            $"{game.Progress} · 统计与文字直播分别采样，时间可能略有差异";
        _refresh.IsEnabled = game is not null && !state.IsLoading;
        _awayTab.Header = Ui.TeamLabel(game?.Away ?? "客队", "（客队）");
        _homeTab.Header = Ui.TeamLabel(game?.Home ?? "主队", "（主队）");
        _away.Update(state.Snapshot?.Away);
        _home.Update(state.Snapshot?.Home);
        _away.SetChanges(state.Changes);
        _home.SetChanges(state.Changes);
        UpdateCourtIndicators();
        _scores.Update(_liveState, state);
        var updated = state.Snapshot is null ? "" : $" · 最近更新 {MatchTime.Format(state.Snapshot.FetchedAt)}";
        _status.Text = (_demo ? "演示数据 · " : "") + state.Message +
            (state.HasError && state.Snapshot is not null ? " · 保留上次数据" : "") + updated + (_notice.Length > 0 ? "\n" + _notice : "");
        _status.ToolTip = _status.Text + "\n统计窗口与球员卡共用订阅；没有消费者时停止。缺失数值显示 -，不当作 0。";
    }

    internal sealed class TeamSheet : DockPanel
    {
        private readonly ObservableCollection<PlayerRow> _rows = [];
        private readonly Dictionary<string, PlayerRow> _byId = [];
        private readonly DataGrid _grid = new()
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserReorderColumns = true, CanUserSortColumns = true, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, FrozenColumnCount = 2, RowHeight = 34, FontSize = 12,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Background = Brushes.Transparent,
            BorderThickness = new(0), AlternatingRowBackground = Ui.Brush("CanvasBrush"),
            RowBackground = Ui.Brush("CardBrush"), HorizontalGridLinesBrush = Ui.Brush("LineBrush")
        };
        private readonly DataGrid _total = new()
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserReorderColumns = false, CanUserResizeColumns = false, CanUserResizeRows = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.None, FrozenColumnCount = 2, RowHeight = 34, Height = 35,
            FontSize = 12, FontWeight = FontWeights.SemiBold, Focusable = false, IsHitTestVisible = false,
            BorderThickness = new(0, 1, 0, 0), BorderBrush = Ui.Brush("LineBrush"),
            Background = Ui.Brush("SoftBrush"), RowBackground = Ui.Brush("SoftBrush"),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Ui.Brush("LineBrush"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        private static readonly PlayerStatistics EmptyTotal = new("total", "球队合计", false, "-", "-", "-", "-", "-",
            null, null, null, null, null, null, null, null, null, null);
        private readonly PlayerRow _totalRow = new(EmptyTotal, isTotal: true);
        private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, SmallChange = 30, Focusable = true, Visibility = Visibility.Collapsed };
        private readonly TextBlock _empty = Ui.Text("等待球员统计", 13);
        private TeamStatistics? _shown;
        private ScrollViewer? _scroll;
        private ScrollViewer? _totalScroll;
        private bool _syncingScroll;
        private HashSet<string> _favorites = [];
        private readonly DispatcherTimer _layoutSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Dictionary<string, double> _defaultWidths = [];
        private readonly List<(DataGridColumn Column, DependencyPropertyDescriptor Descriptor, EventHandler Handler)> _widthObservers = [];
        private StatisticsPreset _preset;
        private bool _restoringLayout;
        private bool _courtOnly, _followedOnly;
        private string _filteredKeys = "";
        internal event Action<FavoritePlayer>? FollowRequested;
        internal event Action<PlayerStatistics>? PlayerRequested;
        internal event Action<StatisticsTableLayout>? LayoutChanged;
        internal TeamSheet(bool archive=false)
        {
            Margin = new(12);
            var hint = Ui.Text("点击球员行查看本场详情 · 列标题排序 · 球衣标首发，红球／红字标在场", 10);
            if(archive)hint.Text="历史单场数据 · 球衣标首发 · 名称沿用归档原文 · 未关联实时关注或球员投篮详情";
            hint.ToolTip = "球衣按本场首发身份固定配色，与实时在场无关。红色篮球和红字表示依据本场最新五人换人名单确认在场，可能存在刷新延迟；没有有效名单、赛前或赛后不显示实时在场标记。号码来自当前球队名单，历史比赛号码可能不同。\n得分／篮板／助攻括号为最近两次成功统计的变化；首次不标增量，数据下调标为修正。悬停球衣可查看首发／替补身份、在场依据和比较时间。\n投篮／三分／罚球为命中数-出手数，命中率由对应命中数／出手数计算，未出手显示 —；+/- 为在场净胜分。";
            if(archive)hint.ToolTip="号码、首发与技术统计来自本场归档；不推测实时在场状态。球员编号独立核验，尚未关联实时关注和投篮详情。球队合计可能包含球队级失误，与球员逐项求和存在口径差异。";
            hint.Margin = new(0, 8, 0, 0);
            SetDock(hint, Dock.Bottom); Children.Add(hint);
            _grid.ItemsSource = _rows;
            CollectionViewSource.GetDefaultView(_rows).Filter = item => item is PlayerRow row &&
                (!_courtOnly || row.IsInFive) && (!_followedOnly || _favorites.Contains(row.Id));
            var cellStyle = new Style(typeof(DataGridCell), (Style)Application.Current.FindResource(typeof(DataGridCell)));
            var active = new DataTrigger { Binding = new Binding(nameof(PlayerRow.IsOnCourt)), Value = true };
            active.Setters.Add(new Setter(ForegroundProperty, BasketballBadge.RedInk));
            cellStyle.Triggers.Add(active);
            var selectedActive = new MultiDataTrigger();
            selectedActive.Conditions.Add(new Condition(new Binding(nameof(PlayerRow.IsOnCourt)), true));
            selectedActive.Conditions.Add(new Condition(new Binding(nameof(DataGridCell.IsSelected)) { RelativeSource = new(RelativeSourceMode.Self) }, true));
            selectedActive.Setters.Add(new Setter(BackgroundProperty, Ui.Brush("RedTint")));
            selectedActive.Setters.Add(new Setter(ForegroundProperty, BasketballBadge.RedInk));
            selectedActive.Setters.Add(new Setter(BorderBrushProperty, Ui.Brush("RedBorder")));
            cellStyle.Triggers.Add(selectedActive);
            _grid.CellStyle = cellStyle;
            PlayerRow? pressedRow = null; Point press = default;
            _grid.PreviewMouseLeftButtonDown += (_, e) =>
            {
                pressedRow = Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is null && Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is PlayerRow row ? row : null;
                press = e.GetPosition(_grid);
            };
            _grid.PreviewMouseLeftButtonUp += (_, e) =>
            {
                var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as PlayerRow;
                if (row is not null && ReferenceEquals(row, pressedRow) && (e.GetPosition(_grid) - press).Length < 4 && Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is null) PlayerRequested?.Invoke(row.Value);
                pressedRow = null;
            };
            _grid.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter && _grid.SelectedItem is PlayerRow row && Ancestor<ButtonBase>(Keyboard.FocusedElement as DependencyObject) is null)
                { PlayerRequested?.Invoke(row.Value); e.Handled = true; }
            };
            _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _total.ItemsSource = new[] { _totalRow };
            System.Windows.Automation.AutomationProperties.SetName(_total, "固定球队合计");
            var star = new FrameworkElementFactory(typeof(Button));
            star.SetValue(StyleProperty, (Style)Application.Current.FindResource("IconButton"));
            star.SetBinding(ContentControl.ContentProperty, new Binding(nameof(PlayerRow.Star)));
            star.SetBinding(ToolTipProperty, new Binding(nameof(PlayerRow.StarTip)));
            star.SetBinding(ForegroundProperty, new Binding(nameof(PlayerRow.StarInk)));
            star.SetValue(BackgroundProperty, Brushes.Transparent);
            star.SetValue(BorderThicknessProperty, new Thickness(0));
            star.SetValue(PaddingProperty, new Thickness(2));
            star.SetValue(MarginProperty, new Thickness(0));
            star.SetValue(FontSizeProperty, 16.0);
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "关注", Width = 42,
                CellTemplate = new DataTemplate { VisualTree = star }, CanUserSort = false, CanUserReorder = false });
            if(archive)_grid.Columns[0].Visibility=Visibility.Collapsed;
            _grid.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is Button { DataContext: PlayerRow row })
                { FollowRequested?.Invoke(new(row.Id, row.Name)); e.Handled = true; }
            }));
            var playerCell = new FrameworkElementFactory(typeof(DockPanel));
            playerCell.SetValue(MarginProperty, new Thickness(2, 0, 0, 0));
            var jersey = new FrameworkElementFactory(typeof(JerseyBadge));
            jersey.SetValue(DockPanel.DockProperty, Dock.Left);
            jersey.SetValue(MarginProperty, new Thickness(0, 0, 7, 0));
            jersey.SetBinding(JerseyBadge.NumberProperty, new Binding(nameof(PlayerRow.JerseyNumber)));
            jersey.SetBinding(JerseyBadge.PresenceProperty, new Binding(nameof(PlayerRow.JerseyAppearance)));
            jersey.SetBinding(JerseyBadge.TeamColorProperty, new Binding(nameof(PlayerRow.TeamColor)));
            jersey.SetBinding(ToolTipProperty, new Binding(nameof(PlayerRow.JerseyTip)));
            jersey.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(PlayerRow.JerseyTip)));
            playerCell.AppendChild(jersey);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlayerRow.Name)));
            name.SetBinding(ToolTipProperty, new Binding(nameof(PlayerRow.Name)));
            name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var namePanel = new FrameworkElementFactory(typeof(PlayerNamePanel));
            namePanel.AppendChild(name);
            var ball = new FrameworkElementFactory(typeof(BasketballBadge));
            ball.SetBinding(VisibilityProperty, new Binding(nameof(PlayerRow.IsOnCourt)) { Converter = new BooleanToVisibilityConverter() });
            namePanel.AppendChild(ball);
            playerCell.AppendChild(namePanel);
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "球员", Width = 205, SortMemberPath = nameof(PlayerRow.Name),
                CellTemplate = new DataTemplate { VisualTree = playerCell }, CanUserReorder = false });
            AddColumn("时间", nameof(PlayerRow.Minutes), 58);
            AddColumn("得分", nameof(PlayerRow.PointsDisplay), 105, sortPath: nameof(PlayerRow.Points));
            AddColumn("篮板", nameof(PlayerRow.ReboundsDisplay), 105, sortPath: nameof(PlayerRow.Rebounds));
            AddColumn("助攻", nameof(PlayerRow.AssistsDisplay), 105, sortPath: nameof(PlayerRow.Assists));
            AddColumn("投篮", nameof(PlayerRow.FieldGoals), 68);
            AddColumn("投篮%", nameof(PlayerRow.FieldGoalPercentageDisplay), 74, sortPath: nameof(PlayerRow.FieldGoalPercentage));
            AddColumn("三分", nameof(PlayerRow.ThreePointers), 68);
            AddColumn("三分%", nameof(PlayerRow.ThreePointPercentageDisplay), 74, sortPath: nameof(PlayerRow.ThreePointPercentage));
            AddColumn("罚球", nameof(PlayerRow.FreeThrows), 68);
            AddColumn("罚球%", nameof(PlayerRow.FreeThrowPercentageDisplay), 74, sortPath: nameof(PlayerRow.FreeThrowPercentage));
            AddColumn("抢断", nameof(PlayerRow.Steals), 50);
            AddColumn("盖帽", nameof(PlayerRow.Blocks), 50);
            AddColumn("失误", nameof(PlayerRow.Turnovers), 50);
            AddColumn("犯规", nameof(PlayerRow.Fouls), 50);
            AddColumn("前场板", nameof(PlayerRow.OffensiveRebounds), 62);
            AddColumn("后场板", nameof(PlayerRow.DefensiveRebounds), 62);
            AddColumn("+/-", nameof(PlayerRow.PlusMinus), 54, "{0:+0;-0;0}");
            AddColumn("位置", nameof(PlayerRow.Position), 58);
            var placeholderStyle = new Style(typeof(TextBlock));
            placeholderStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            placeholderStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            var totalNameStyle = new Style(typeof(TextBlock));
            totalNameStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            totalNameStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            foreach (var column in _grid.Columns)
            {
                // A separate one-row grid stays out of sorting and vertical scrolling.
                // Sharing each live column's width preserves alignment during resizing.
                var path = column is DataGridTextColumn textColumn ? ((Binding)textColumn.Binding).Path.Path :
                    string.IsNullOrEmpty(column.SortMemberPath) ? nameof(PlayerRow.Star) : column.SortMemberPath;
                var totalColumn = new DataGridTextColumn
                {
                    Header = column.Header, CanUserSort = false,
                    Binding = new Binding(path) { TargetNullValue = "-", StringFormat = (column as DataGridTextColumn)?.Binding.StringFormat },
                    ElementStyle = (column as DataGridTextColumn)?.ElementStyle ?? (path == nameof(PlayerRow.Name) ? totalNameStyle : placeholderStyle)
                };
                BindingOperations.SetBinding(totalColumn, DataGridColumn.WidthProperty,
                    new Binding(nameof(DataGridColumn.ActualWidth)) { Source = column, Mode = BindingMode.OneWay });
                BindingOperations.SetBinding(totalColumn, DataGridColumn.VisibilityProperty, new Binding(nameof(DataGridColumn.Visibility)) { Source = column });
                _total.Columns.Add(totalColumn);
                _defaultWidths[Key(column)] = column.Width.Value;
                var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
                EventHandler handler = (_, _) => QueueLayoutSave();
                descriptor.AddValueChanged(column, handler); _widthObservers.Add((column, descriptor, handler));
            }
            _grid.ColumnReordered += (_, _) =>
            {
                foreach (var column in _grid.Columns.OrderBy(c => c.DisplayIndex))
                    _total.Columns[_grid.Columns.IndexOf(column)].DisplayIndex = column.DisplayIndex;
                SyncHorizontal();
                QueueLayoutSave();
            };
            var body = new Grid();
            body.Children.Add(_grid);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.IsHitTestVisible = false;
            body.Children.Add(_empty);
            var table = new Grid();
            table.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            table.Children.Add(body);
            Grid.SetRow(_total, 1); table.Children.Add(_total);
            Grid.SetRow(_horizontal, 2); table.Children.Add(_horizontal);
            Children.Add(table);
            _horizontal.ValueChanged += (_, _) =>
            {
                if (_syncingScroll) return;
                _scroll?.ScrollToHorizontalOffset(_horizontal.Value);
                _totalScroll?.ScrollToHorizontalOffset(_horizontal.Value);
            };
            Loaded += (_, _) =>
            {
                if (_scroll is null && FindScrollViewer(_grid) is { } scroll)
                {
                    _scroll = scroll;
                    _scroll.ScrollChanged += (_, e) => { if (e.OriginalSource == _scroll) SyncHorizontal(); };
                }
                if (_totalScroll is null && FindScrollViewer(_total) is { } totalScroll)
                {
                    _totalScroll = totalScroll;
                    _totalScroll.ScrollChanged += (_, e) =>
                    { if (e.OriginalSource == _totalScroll && (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)) SyncHorizontal(); };
                }
                SyncHorizontal();
            };
            _grid.SizeChanged += (_, _) => SyncHorizontal();
            _grid.Sorting += (_, _) => QueueLayoutSave();
            _layoutSave.Tick += (_, _) => FlushLayout();
        }
        private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node is not null)
            {
                if (node is T found) return found;
                node = node is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(node);
            }
            return null;
        }
        private static string Key(DataGridColumn column) => string.IsNullOrEmpty(column.SortMemberPath) ? "Star" : column.SortMemberPath;
        private void QueueLayoutSave() { if (!_restoringLayout) { _layoutSave.Stop(); _layoutSave.Start(); } }
        internal void CancelLayoutSave() => _layoutSave.Stop();
        internal void FlushLayout()
        {
            if (!_layoutSave.IsEnabled) return;
            _layoutSave.Stop();
            var sort = _grid.Items.SortDescriptions.FirstOrDefault();
            LayoutChanged?.Invoke(new()
            {
                Preset = _preset,
                Columns = _grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
                    .Select(c => new StatisticsColumnSetting(Key(c), RuntimeCompat.IsFinite(c.ActualWidth) && c.ActualWidth >= 30 ? c.ActualWidth : c.Width.Value)).ToArray(),
                SortColumn = sort.PropertyName, SortDescending = sort.Direction == ListSortDirection.Descending
            });
        }
        internal void Stop()
        {
            FlushLayout(); _layoutSave.Stop();
            foreach (var observer in _widthObservers) observer.Descriptor.RemoveValueChanged(observer.Column, observer.Handler);
        }
        internal void ApplyPreset(StatisticsPreset preset, StatisticsTableLayout? layout)
        {
            _restoringLayout = true; _layoutSave.Stop(); _preset = preset;
            try
            {
                var visible = StatisticsPresets.Columns(preset);
                var saved = layout?.Columns ?? [];
                var order = new[] { "Star", "Name" }.Concat(saved.Select(c => c.Key).Where(k => k is not ("Star" or "Name") && visible.Contains(k)))
                    .Concat(visible).Concat(_grid.Columns.Select(Key)).Distinct().ToArray();
                foreach (var column in _grid.Columns)
                {
                    column.Visibility = visible.Contains(Key(column)) ? Visibility.Visible : Visibility.Collapsed;
                    column.Width = saved.FirstOrDefault(c => c.Key == Key(column))?.Width ?? _defaultWidths[Key(column)];
                    column.SortDirection = null;
                }
                if (preset == StatisticsPreset.Compact && saved.Length == 0)
                    foreach (var column in _grid.Columns.Where(c => Key(c) is "Points" or "Rebounds" or "Assists"))
                        column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                foreach (var key in order) _grid.Columns.First(c => Key(c) == key).DisplayIndex = Array.IndexOf(order, key);
                foreach (var column in _grid.Columns.OrderBy(c => c.DisplayIndex)) _total.Columns[_grid.Columns.IndexOf(column)].DisplayIndex = column.DisplayIndex;
                _grid.Items.SortDescriptions.Clear();
                if (layout?.SortColumn is { } sortKey && visible.Contains(sortKey) && sortKey != "Star")
                {
                    var direction = layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                    _grid.Columns.First(c => Key(c) == sortKey).SortDirection = direction;
                    _grid.Items.SortDescriptions.Add(new(sortKey, direction));
                }
                _scroll?.ScrollToHorizontalOffset(0); _totalScroll?.ScrollToHorizontalOffset(0);
                Dispatcher.BeginInvoke(SyncHorizontal, DispatcherPriority.Loaded);
            }
            finally { _restoringLayout = false; }
        }
        private void SyncHorizontal()
        {
            if (_scroll is null || _syncingScroll) return;
            _syncingScroll = true;
            try
            {
                var gutter = _scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ?
                    (_scroll.Template.FindName("PART_VerticalScrollBar", _scroll) as ScrollBar)?.ActualWidth ?? SystemParameters.VerticalScrollBarWidth : 0;
                if (Math.Abs(_total.Margin.Right - gutter) > .1)
                {
                    _total.Margin = new(0, 0, gutter, 0);
                    _horizontal.Margin = new(0, 0, gutter, 0);
                }
                _horizontal.Maximum = _scroll.ScrollableWidth;
                _horizontal.ViewportSize = _scroll.ViewportWidth;
                _horizontal.LargeChange = _scroll.ViewportWidth;
                _horizontal.Value = _scroll.HorizontalOffset;
                _horizontal.Visibility = _scroll.ScrollableWidth > .1 ? Visibility.Visible : Visibility.Collapsed;
                _totalScroll?.ScrollToHorizontalOffset(_scroll.HorizontalOffset);
            }
            finally { _syncingScroll = false; }
        }
        internal void SetFavorites(IReadOnlyList<FavoritePlayer> favorites)
        {
            _favorites = favorites.Select(p => p.Id).ToHashSet();
            foreach (var row in _rows) row.SetFollowed(_favorites.Contains(row.Id));
            RefreshFilter();
        }
        internal void SetCourtStates(IReadOnlyDictionary<string, PlayerCourtState> states)
        {
            foreach (var row in _rows) row.SetCourt(states.GetValueOrDefault(row.Id) ?? new(CourtPresence.Unknown, "状态尚未确认"));
            RefreshFilter();
        }
        internal void SetFilters(bool courtOnly, bool followedOnly)
        {
            _courtOnly = courtOnly; _followedOnly = followedOnly;
            _filteredKeys = ""; RefreshFilter(force: true);
        }
        private void RefreshFilter(bool force = false)
        {
            var keys = string.Join("|", _rows.Where(row => (!_courtOnly || row.IsInFive) && (!_followedOnly || _favorites.Contains(row.Id))).Select(row => row.Id));
            if (force || keys != _filteredKeys) { _filteredKeys = keys; CollectionViewSource.GetDefaultView(_rows).Refresh(); }
            _empty.Visibility = _grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = _rows.Count == 0 ? (_shown is null ? "等待球员统计" : "该队暂无球员统计") :
                _courtOnly ? "暂无匹配的五人名单，可关闭筛选查看全部球员" : "本队暂无关注球员，可关闭筛选后点击 ☆";
        }
        internal void SetChanges(StatisticsDeltaSnapshot? changes)
        {
            foreach (var row in _rows) row.SetChanges(changes?.Find(_shown?.Team ?? "", row.Id), changes?.Description);
        }
        private void AddColumn(string heading, string path, double width, string? format = null, string? sortPath = null)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(path) { TargetNullValue = "-" }));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = heading, Width = width, SortMemberPath = sortPath ?? path, ElementStyle = style,
                Binding = new Binding(path) { TargetNullValue = "-", StringFormat = format }
            });
        }
        internal void Update(TeamStatistics? team)
        {
            if (ReferenceEquals(team, _shown)) return;
            _shown = team;
            var offset = _scroll?.VerticalOffset ?? 0;
            var horizontal = _scroll?.HorizontalOffset ?? 0;
            var selected = _grid.SelectedItem;
            var changed = false;
            var incoming = team?.Players ?? [];
            var keys = incoming.Select(p => p.Id).ToHashSet();
            for (var index = _rows.Count - 1; index >= 0; index--)
                if (!keys.Contains(_rows[index].Id)) { _byId.Remove(_rows[index].Id); _rows.RemoveAt(index); changed = true; }
            foreach (var player in incoming)
            {
                if (_byId.TryGetValue(player.Id, out var existing)) changed |= existing.Update(player);
                else { var row = new PlayerRow(player); row.SetFollowed(_favorites.Contains(row.Id)); _byId.Add(player.Id, row); _rows.Add(row); changed = true; }
            }
            var color = TeamPalette.Resolve(team?.Team, team?.RosterSlug);
            foreach (var row in _rows) row.SetTeamColor(color);
            if (changed && _grid.Items.SortDescriptions.Count > 0) CollectionViewSource.GetDefaultView(_rows).Refresh();
            if (selected is PlayerRow selectedRow && _byId.ContainsKey(selectedRow.Id)) _grid.SelectedItem = selected;
            _empty.Visibility = incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = team is null ? "等待球员统计" : "该队暂无球员统计";
            _totalRow.Update(team?.Total ?? EmptyTotal);
            _total.ToolTip = team?.Total is null ? "球队合计尚未发布，缺失值显示 -。" : "球队合计固定在底部；时间、位置和 +/- 不作汇总。";
            RefreshFilter();
            if (changed) Dispatcher.BeginInvoke(() =>
            {
                _scroll?.ScrollToVerticalOffset(offset);
                _scroll?.ScrollToHorizontalOffset(horizontal);
            }, DispatcherPriority.Loaded);
        }
        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer scroll) return scroll;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
            return null;
        }
    }
    private sealed class PlayerRow(PlayerStatistics value, bool isTotal = false) : INotifyPropertyChanged
    {
        private PlayerStatistics _value = value;
        internal PlayerStatistics Value => _value;
        private bool _followed;
        private PlayerCourtState _court = new(CourtPresence.Unknown, "状态尚未确认");
        private PlayerStatDelta? _delta;
        private string? _deltaPeriod;
        public string JerseyNumber => _value.JerseyNumber ?? "-";
        public CourtPresence JerseyAppearance => _value.IsStarter ? CourtPresence.Starter : CourtPresence.OffCourt;
        public bool IsOnCourt => !isTotal && _court.Presence == CourtPresence.OnCourt;
        internal bool IsInFive => !isTotal && _court.Presence is CourtPresence.OnCourt or CourtPresence.Starter;
        public Brush StarInk => IsOnCourt ? BasketballBadge.RedInk : Ui.Brush(_followed ? "AccentInk" : "MutedInk");
        public TeamJerseyColor TeamColor { get; private set; } = TeamPalette.Unknown;
        public string JerseyTip => $"{Name} · {(_value.JerseyNumber is { } number ? number + " 号（当前球队名单）" : "号码尚未提供")}\n本场身份：{_value.Role}{(_value.Status.Length == 0 ? "" : " · " + _value.Status)}\n球衣配色：{(_value.IsStarter ? $"首发 · {TeamColor.Name} {TeamColor.Hex}" : "替补 · 灰色")}\n{_court.Detail}" +
            (_deltaPeriod is null ? "" : $"\n{_deltaPeriod}\n{_delta?.Description ?? "暂无可比较的上次统计"}");
        internal void SetChanges(PlayerStatDelta? delta, string? period)
        {
            if (_delta == delta && _deltaPeriod == period) return;
            _delta = delta; _deltaPeriod = period;
            PropertyChanged?.Invoke(this, new(nameof(PointsDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(ReboundsDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(AssistsDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(JerseyTip)));
        }
        internal void SetTeamColor(TeamJerseyColor color)
        {
            if (TeamColor == color) return;
            TeamColor = color;
            PropertyChanged?.Invoke(this, new(nameof(TeamColor)));
            PropertyChanged?.Invoke(this, new(nameof(JerseyTip)));
        }
        internal void SetCourt(PlayerCourtState state)
        {
            if (_court == state) return;
            _court = state;
            PropertyChanged?.Invoke(this, new(nameof(IsOnCourt)));
            PropertyChanged?.Invoke(this, new(nameof(StarInk)));
            PropertyChanged?.Invoke(this, new(nameof(JerseyTip)));
        }
        public string Star => isTotal ? "-" : _followed ? "★" : "☆";
        public string StarTip => (_followed ? "取消关注 " : "关注 ") + Name;
        internal void SetFollowed(bool followed)
        {
            if (_followed == followed) return;
            _followed = followed;
            PropertyChanged?.Invoke(this, new(nameof(Star))); PropertyChanged?.Invoke(this, new(nameof(StarTip)));
            PropertyChanged?.Invoke(this, new(nameof(StarInk)));
        }
        public string Id => _value.Id;
        public string Name => isTotal ? "球队合计" : _value.Name;
        private static string Display(string value) => string.IsNullOrWhiteSpace(value) || value is "—" or "--" or "N/A" ? "-" : value;
        public string Position => isTotal ? "-" : Display(_value.Position);
        public string Minutes => isTotal ? "-" : Display(_value.Minutes);
        public string FieldGoals => Display(_value.FieldGoals);
        public string ThreePointers => Display(_value.ThreePointers);
        public string FreeThrows => Display(_value.FreeThrows);
        public double? FieldGoalPercentage => StatisticsPresets.Percentage(_value.FieldGoals);
        public double? ThreePointPercentage => StatisticsPresets.Percentage(_value.ThreePointers);
        public double? FreeThrowPercentage => StatisticsPresets.Percentage(_value.FreeThrows);
        public string FieldGoalPercentageDisplay => StatisticsPresets.DisplayPercentage(FieldGoalPercentage);
        public string ThreePointPercentageDisplay => StatisticsPresets.DisplayPercentage(ThreePointPercentage);
        public string FreeThrowPercentageDisplay => StatisticsPresets.DisplayPercentage(FreeThrowPercentage);
        public int? Points => _value.Points;
        public string PointsDisplay => PlayerStatDelta.Format(Points, _delta?.Points, "-");
        public string ReboundsDisplay => PlayerStatDelta.Format(Rebounds, _delta?.Rebounds, "-");
        public string AssistsDisplay => PlayerStatDelta.Format(Assists, _delta?.Assists, "-");
        public int? Rebounds => _value.Rebounds;
        public int? Assists => _value.Assists;
        public int? OffensiveRebounds => _value.OffensiveRebounds;
        public int? DefensiveRebounds => _value.DefensiveRebounds;
        public int? Fouls => _value.Fouls;
        public int? Steals => _value.Steals;
        public int? Turnovers => _value.Turnovers;
        public int? Blocks => _value.Blocks;
        public int? PlusMinus => isTotal ? null : _value.PlusMinus;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal bool Update(PlayerStatistics next)
        {
            if (_value == next) return false;
            _value = next;
            PropertyChanged?.Invoke(this, new(null));
            return true;
        }
        public override string ToString() => Name;
    }
}
