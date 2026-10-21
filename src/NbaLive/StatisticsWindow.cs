using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace NbaLive;

internal sealed class StatisticsWindow : Window
{
    private readonly StatisticsCoordinator _coordinator;
    private readonly Func<FavoritePlayer, string?> _toggleFavorite;
    private readonly bool _demo;
    private readonly TextBlock _heading = Ui.Text("球员技术统计", 20);
    private readonly TextBlock _progress = Ui.Text("", 12);
    private readonly TextBlock _status = Ui.Text("请选择比赛后查看球员统计", 11);
    private readonly Button _refresh;
    private readonly TeamSheet _away = new();
    private readonly TeamSheet _home = new();
    private readonly TabItem _awayTab;
    private readonly TabItem _homeTab;
    private bool _closed;
    private string _notice = "";

    internal StatisticsWindow(StatisticsCoordinator coordinator, bool demo, IReadOnlyList<FavoritePlayer> favorites,
        Func<FavoritePlayer, string?> toggleFavorite)
    {
        _demo = demo;
        _coordinator = coordinator;
        _toggleFavorite = toggleFavorite;
        Ui.Initialize(this, "球员技术统计 · NBA 直播条", 1100, 650);
        var root = new DockPanel { Margin = new(22) };
        var top = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _refresh = Ui.Button("刷新", () => _ = _coordinator.Refresh());
        actions.Children.Add(_refresh);
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        var title = new StackPanel();
        title.Children.Add(_heading);
        title.Children.Add(_progress);
        top.Children.Add(title);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _status.Margin = new(0, 12, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var tabs = new TabControl { Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 230)) };
        _awayTab = new() { Header = "客队", Content = _away, Padding = new(18, 8, 18, 8) };
        _homeTab = new() { Header = "主队", Content = _home, Padding = new(18, 8, 18, 8) };
        tabs.Items.Add(_awayTab);
        tabs.Items.Add(_homeTab);
        root.Children.Add(tabs);
        Content = root;
        _away.FollowRequested += ToggleFavorite;
        _home.FollowRequested += ToggleFavorite;
        SetFavorites(favorites);
        _coordinator.Changed += OnState;
        Closed += (_, _) => { _closed = true; _coordinator.Changed -= OnState; };
        OnState(_coordinator.State);
    }
    internal void SetFavorites(IReadOnlyList<FavoritePlayer> favorites)
    {
        _away.SetFavorites(favorites); _home.SetFavorites(favorites);
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
        _heading.Text = game?.Score ?? "球员技术统计";
        _progress.Text = game is null ? "选择比赛后，此窗口随当前场次更新" :
            $"{game.Progress} · 统计与文字直播分别采样，时间可能略有差异";
        _refresh.IsEnabled = game is not null && !state.IsLoading;
        _awayTab.Header = $"{game?.Away ?? "客队"}（客队）";
        _homeTab.Header = $"{game?.Home ?? "主队"}（主队）";
        _away.Update(state.Snapshot?.Away);
        _home.Update(state.Snapshot?.Home);
        var updated = state.Snapshot is null ? "" : $" · 最近更新 {MatchTime.Format(state.Snapshot.FetchedAt)}";
        _status.Text = (_demo ? "演示数据 · " : "") + state.Message +
            (state.HasError && state.Snapshot is not null ? " · 保留上次数据" : "") + updated + (_notice.Length > 0 ? "\n" + _notice : "");
        _status.ToolTip = _status.Text + "\n统计窗口与球员卡共用订阅；没有消费者时停止。缺失数值显示 —，不当作 0。";
    }

    private sealed class TeamSheet : DockPanel
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
            BorderThickness = new(0), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(242, 245, 247))
        };
        private readonly TextBlock _total = Ui.Text("", 12);
        private readonly TextBlock _empty = Ui.Text("等待球员统计", 13);
        private TeamStatistics? _shown;
        private ScrollViewer? _scroll;
        private HashSet<string> _favorites = [];
        internal event Action<FavoritePlayer>? FollowRequested;
        internal TeamSheet()
        {
            Margin = new(12);
            _total.Margin = new(0, 12, 0, 0);
            SetDock(_total, Dock.Bottom);
            Children.Add(_total);
            _grid.ItemsSource = _rows;
            var star = new FrameworkElementFactory(typeof(Button));
            star.SetBinding(ContentControl.ContentProperty, new Binding(nameof(PlayerRow.Star)));
            star.SetBinding(ToolTipProperty, new Binding(nameof(PlayerRow.StarTip)));
            star.SetValue(BackgroundProperty, Brushes.Transparent);
            star.SetValue(BorderThicknessProperty, new Thickness(0));
            star.SetValue(PaddingProperty, new Thickness(2));
            star.SetValue(MarginProperty, new Thickness(0));
            star.SetValue(FontSizeProperty, 16.0);
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "关注", Width = 42,
                CellTemplate = new DataTemplate { VisualTree = star }, CanUserSort = false });
            _grid.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is Button { DataContext: PlayerRow row })
                { FollowRequested?.Invoke(new(row.Id, row.Name)); e.Handled = true; }
            }));
            AddColumn("球员", nameof(PlayerRow.Name), 175);
            AddColumn("出场", nameof(PlayerRow.Role), 66);
            AddColumn("时间", nameof(PlayerRow.Minutes), 58);
            AddColumn("得分", nameof(PlayerRow.Points), 50);
            AddColumn("篮板", nameof(PlayerRow.Rebounds), 50);
            AddColumn("助攻", nameof(PlayerRow.Assists), 50);
            AddColumn("投篮", nameof(PlayerRow.FieldGoals), 68);
            AddColumn("三分", nameof(PlayerRow.ThreePointers), 68);
            AddColumn("罚球", nameof(PlayerRow.FreeThrows), 68);
            AddColumn("抢断", nameof(PlayerRow.Steals), 50);
            AddColumn("盖帽", nameof(PlayerRow.Blocks), 50);
            AddColumn("失误", nameof(PlayerRow.Turnovers), 50);
            AddColumn("犯规", nameof(PlayerRow.Fouls), 50);
            AddColumn("前场板", nameof(PlayerRow.OffensiveRebounds), 62);
            AddColumn("后场板", nameof(PlayerRow.DefensiveRebounds), 62);
            AddColumn("+/-", nameof(PlayerRow.PlusMinus), 54, "{0:+0;-0;0}");
            AddColumn("位置", nameof(PlayerRow.Position), 58);
            var body = new Grid();
            body.Children.Add(_grid);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.IsHitTestVisible = false;
            body.Children.Add(_empty);
            Children.Add(body);
            Loaded += (_, _) => _scroll = FindScrollViewer(_grid);
        }
        internal void SetFavorites(IReadOnlyList<FavoritePlayer> favorites)
        {
            _favorites = favorites.Select(p => p.Id).ToHashSet();
            foreach (var row in _rows) row.SetFollowed(_favorites.Contains(row.Id));
        }
        private void AddColumn(string heading, string path, double width, string? format = null)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(path) { TargetNullValue = "—" }));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = heading, Width = width, SortMemberPath = path, ElementStyle = style,
                Binding = new Binding(path) { TargetNullValue = "—", StringFormat = format }
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
            if (changed && _grid.Items.SortDescriptions.Count > 0) CollectionViewSource.GetDefaultView(_rows).Refresh();
            if (selected is PlayerRow selectedRow && _byId.ContainsKey(selectedRow.Id)) _grid.SelectedItem = selected;
            _empty.Visibility = incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = team is null ? "等待球员统计" : "该队暂无球员统计";
            var total = team?.Total;
            static string Value(int? value) => value?.ToString() ?? "—";
            _total.Text = total is null ? "球队合计尚未发布 · 点击列标题排序，横向滚动查看更多" :
                $"球队合计：得分 {Value(total.Points)} · 篮板 {Value(total.Rebounds)} · 助攻 {Value(total.Assists)} · 投篮 {total.FieldGoals} · 三分 {total.ThreePointers} · 罚球 {total.FreeThrows}\n点击列标题排序 · 投篮／三分／罚球：命中数-出手数 · +/-：在场净胜分";
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
    private sealed class PlayerRow(PlayerStatistics value) : INotifyPropertyChanged
    {
        private PlayerStatistics _value = value;
        private bool _followed;
        public string Star => _followed ? "★" : "☆";
        public string StarTip => (_followed ? "取消关注 " : "关注 ") + Name;
        internal void SetFollowed(bool followed)
        {
            if (_followed == followed) return;
            _followed = followed;
            PropertyChanged?.Invoke(this, new(nameof(Star))); PropertyChanged?.Invoke(this, new(nameof(StarTip)));
        }
        public string Id => _value.Id;
        public string Name => _value.Name;
        public string Role => _value.Status.Length == 0 ? _value.Role : _value.Status;
        public string Position => _value.Position;
        public string Minutes => _value.Minutes;
        public string FieldGoals => _value.FieldGoals;
        public string ThreePointers => _value.ThreePointers;
        public string FreeThrows => _value.FreeThrows;
        public int? Points => _value.Points;
        public int? Rebounds => _value.Rebounds;
        public int? Assists => _value.Assists;
        public int? OffensiveRebounds => _value.OffensiveRebounds;
        public int? DefensiveRebounds => _value.DefensiveRebounds;
        public int? Fouls => _value.Fouls;
        public int? Steals => _value.Steals;
        public int? Turnovers => _value.Turnovers;
        public int? Blocks => _value.Blocks;
        public int? PlusMinus => _value.PlusMinus;
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
