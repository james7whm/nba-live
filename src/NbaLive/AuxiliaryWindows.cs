using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Documents;

namespace NbaLive;

internal static class Ui
{
    internal static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }
    internal static void Initialize(Window window, string title, double width, double height)
    {
        window.Style = (Style)Application.Current.FindResource(typeof(Window));
        window.Title = title;
        var work = SystemParameters.WorkArea;
        window.Width = window.MinWidth = Math.Min(width, Math.Max(280, work.Width - 32));
        window.Height = Math.Min(height, Math.Max(240, work.Height - 32));
        window.MinHeight = Math.Min(window.Height, 300);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowInTaskbar = true;
    }
    internal static void Present(Window window)
    {
        window.ShowActivated = true;
        if (!window.IsVisible) window.Show();
        Native.ActivateDialog(window);
        // A menu restores focus when it closes. Activate once more after that input finishes.
        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible) Native.ActivateDialog(window);
        }, DispatcherPriority.ContextIdle);
    }
}

internal sealed class GamePickerWindow : Window
{
    private readonly IGameSource _source;
    private readonly ListBox _games = new() { BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, FontSize = 14 };
    private readonly TextBlock _message = Ui.Text("正在读取今日比赛…", 12);
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DatePicker _date = new() { SelectedDate = MatchTime.Today.ToDateTime(TimeOnly.MinValue), Width = 140, Margin = new(0, 0, 12, 0) };
    private bool _busy;
    private bool _closed;
    private DateOnly? _loadedDate;
    private string _favoriteTeam;
    private IReadOnlyList<GameInfo> _availableGames = [];
    private sealed record GameChoice(GameInfo Game, bool Favorite)
    {
        public string Display => (Favorite ? "☆ " : "") + Game.Display;
        public string FavoriteMark => Favorite ? "☆ " : "";
        public string AwayPoints => Game.AwayScore?.ToString() ?? "—";
        public string HomePoints => Game.HomeScore?.ToString() ?? "—";
        public string ProgressText => Game.Status == GameStatus.Live ? GamePageParser.FormatLiveProgress(Game.Progress) : Game.Progress;
        public Brush ScoreInk => Game.Status == GameStatus.Live ? BasketballBadge.RedInk : (Brush)Application.Current.FindResource("Ink");
        public FontWeight AwayWeight => Game.Status == GameStatus.Finished && Game.AwayScore is { } away && Game.HomeScore is { } home && away > home ? FontWeights.Bold : FontWeights.Normal;
        public FontWeight HomeWeight => Game.Status == GameStatus.Finished && Game.AwayScore is { } away && Game.HomeScore is { } home && home > away ? FontWeights.Bold : FontWeights.Normal;
    }
    internal event Action<GameInfo>? Selected;

    internal GamePickerWindow(IGameSource source, string favoriteTeam)
    {
        _source = source;
        _favoriteTeam = favoriteTeam;
        Ui.Initialize(this, "选择 NBA 比赛 · NBA 直播条", 650, 430);
        var root = new DockPanel { Margin = new(24) };
        var heading = new StackPanel();
        heading.Children.Add(Ui.Text("选择一场比赛", 21));
        heading.Children.Add(Ui.Text("北京时间 · 关注球队优先 · 每 60 秒更新比赛列表", 12));
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 16) };
        filters.Children.Add(_date);
        filters.Children.Add(Ui.Button("刷新", () => _ = Refresh()));
        heading.Children.Add(filters);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var footer = new StackPanel();
        footer.Children.Add(_message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Ui.Button("查看文字直播", Choose));
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 14, 12, 14)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 4)));
        _games.ItemContainerStyle = itemStyle;
        _games.ItemTemplate = CreateGameTemplate();
        _games.MouseDoubleClick += (_, _) => Choose();
        root.Children.Add(_games);
        Content = root;
        _date.SelectedDateChanged += async (_, _) => { if (IsLoaded) await Refresh(); };
        Loaded += async (_, _) => { await Refresh(); if (!_closed) _refresh.Start(); };
        _refresh.Tick += async (_, _) => await Refresh();
        Closed += (_, _) => { _closed = true; _refresh.Stop(); _cts.Cancel(); };
    }
    private static DataTemplate CreateGameTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetBinding(ToolTipProperty, new Binding(nameof(GameChoice.Display)));
        void Add(string? path = null, string? value = null, string? weight = null, bool score = false)
        {
            var run = new FrameworkElementFactory(typeof(Run));
            if (path is not null) run.SetBinding(Run.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
            else run.SetValue(Run.TextProperty, value ?? "");
            if (weight is not null) run.SetBinding(Run.FontWeightProperty, new Binding(weight));
            if (score) run.SetBinding(Run.ForegroundProperty, new Binding(nameof(GameChoice.ScoreInk)));
            text.AppendChild(run);
        }
        Add(nameof(GameChoice.FavoriteMark)); Add("Game.Away"); Add(value: " ");
        Add(nameof(GameChoice.AwayPoints), weight: nameof(GameChoice.AwayWeight), score: true);
        Add(value: " : ", score: true);
        Add(nameof(GameChoice.HomePoints), weight: nameof(GameChoice.HomeWeight), score: true);
        Add(value: " "); Add("Game.Home"); Add(value: "    "); Add(nameof(GameChoice.ProgressText), score: true);
        return new DataTemplate { VisualTree = text };
    }
    private void Choose() { if (!_busy && !_closed && _games.SelectedItem is GameChoice choice) Selected?.Invoke(choice.Game); }
    internal void SetFavorite(string team)
    {
        _favoriteTeam = team;
        ArrangeGames((_games.SelectedItem as GameChoice)?.Game.Id);
    }
    private void ArrangeGames(string? selectedId)
    {
        var choices = GameOrdering.Arrange(_availableGames, _favoriteTeam)
            .Select(g => new GameChoice(g, GameOrdering.IsFavorite(g, _favoriteTeam))).ToArray();
        _games.ItemsSource = choices;
        _games.SelectedItem = choices.FirstOrDefault(c => c.Game.Id == selectedId) ??
            choices.FirstOrDefault(c => c.Favorite && c.Game.Status == GameStatus.Live) ??
            choices.FirstOrDefault(c => c.Game.Status == GameStatus.Live) ?? choices.FirstOrDefault();
    }
    private async Task Refresh()
    {
        if (_busy || _closed) return;
        _busy = true;
        var date = DateOnly.FromDateTime(_date.SelectedDate ?? MatchTime.Today.ToDateTime(TimeOnly.MinValue));
        if (_loadedDate != date) _games.ItemsSource = null;
        _games.IsEnabled = false;
        _message.Text = "正在读取比赛…";
        try
        {
            var games = await _source.GetGames(date, _cts.Token);
            if (_closed) return;
            var current = DateOnly.FromDateTime(_date.SelectedDate ?? MatchTime.Today.ToDateTime(TimeOnly.MinValue));
            if (date != current) return;
            var selected = (_games.SelectedItem as GameChoice)?.Game.Id;
            _availableGames = games;
            ArrangeGames(selected);
            _loadedDate = date;
            _message.Text = games.Count == 0 ? "当天没有 NBA 比赛，可选择其他日期。" : $"{games.Count} 场比赛 · 选择后不会自动切换到其他场次";
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SourceFormatException)
        {
            _message.Text = ex is SourceFormatException ? "数据源异常，请稍后刷新。" : "连接中断，请检查网络后刷新。";
        }
        finally
        {
            _busy = false;
            _games.IsEnabled = true;
            if (!_closed && date != DateOnly.FromDateTime(_date.SelectedDate ?? MatchTime.Today.ToDateTime(TimeOnly.MinValue))) _ = Refresh();
        }
    }
}

internal sealed class HistoryWindow : Window
{
    private readonly EventBookmarks _bookmarks;
    private GameInfo? _game;
    private readonly TextBlock _bookmarkStatus = Ui.Text("", 11);
    private readonly ObservableCollection<HistoryRow> _entries = [];
    private readonly ListBox _list = new() { BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _heading = Ui.Text("本次会话历史", 20);
    private readonly TextBlock _detail = Ui.Text("", 12);
    private ScrollViewer? _scroll;
    private readonly Dictionary<string, HistoryRow> _rows = [];
    private IReadOnlyList<LiveEntry>? _shownHistory;
    private string? _gameId;
    internal HistoryWindow(EventBookmarks bookmarks, Action openBookmarks)
    {
        _bookmarks = bookmarks;
        Ui.Initialize(this, "直播历史 · NBA 直播条", 650, 520);
        var root = new DockPanel { Margin = new(24) };
        var header = new StackPanel();
        header.Children.Add(_heading);
        header.Children.Add(_detail);
        header.Children.Add(Ui.Button("精彩收藏", openBookmarks));
        header.Children.Add(_bookmarkStatus);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        _list.ItemsSource = _entries;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(HistoryRow.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.FontSizeProperty, 13.0);
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var bookmark = new FrameworkElementFactory(typeof(Button));
        bookmark.SetBinding(ContentControl.ContentProperty, new Binding(nameof(HistoryRow.BookmarkMark)));
        bookmark.SetBinding(ToolTipProperty, new Binding(nameof(HistoryRow.BookmarkTip)));
        bookmark.SetValue(DockPanel.DockProperty, Dock.Left); bookmark.SetValue(WidthProperty, 32.0);
        bookmark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top); bookmark.SetValue(FontSizeProperty, 16.0);
        bookmark.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); bookmark.SetValue(PaddingProperty, new Thickness(2));
        bookmark.SetValue(BackgroundProperty, Brushes.Transparent); bookmark.SetValue(BorderThicknessProperty, new Thickness(0));
        row.AppendChild(bookmark); row.AppendChild(text);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new Thickness(0, 12, 8, 12));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(225, 228, 232)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.AppendChild(row);
        _list.ItemTemplate = new DataTemplate { VisualTree = border };
        root.Children.Add(_list);
        Content = root;
        _list.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { DataContext: HistoryRow item } && _game is not null)
            {
                var error = _bookmarks.Toggle(_game, item.Entry);
                _bookmarkStatus.Text = error ?? (_bookmarks.Contains(_game.Id, item.Entry.Key) ? "已收藏此事件。" : "已取消此事件的收藏。");
                e.Handled = true;
            }
        }));
        _bookmarks.Changed += UpdateBookmarks;
        Closed += (_, _) => _bookmarks.Changed -= UpdateBookmarks;
        Loaded += (_, _) => _scroll = FindScrollViewer(_list);
    }
    private void UpdateBookmarks()
    {
        foreach (var row in _entries) row.SetBookmarked(_game is not null && _bookmarks.Contains(_game.Id, row.Entry.Key));
        _bookmarkStatus.Text = _bookmarks.Error ?? $"◇ 收藏事件 · 已保存 {_bookmarks.Items.Count} 条；仅手动收藏会跨重启保留。";
    }
    internal void Update(SessionState state, bool demo)
    {
        var gameId = (state.SelectedGame?.Id ?? state.Snapshot?.Game.Id) + "|" + state.FeedKey;
        _game = state.Snapshot?.Game ?? state.SelectedGame;
        if (gameId != _gameId || (state.IsLoading && state.Snapshot is null))
        { _entries.Clear(); _rows.Clear(); _shownHistory = null; _gameId = gameId; _scroll?.ScrollToTop(); }
        _heading.Text = state.Snapshot?.Game.Score ?? "本次会话历史";
        _detail.Text = $"{(demo ? "演示数据 · " : "")}{state.SourceLabel} · 由早到晚 · {state.History.Count} 条\n{SessionStatus.Describe(state, DateTimeOffset.UtcNow)} · 阅读位置保持不变";
        if (ReferenceEquals(_shownHistory, state.History)) return;
        _shownHistory = state.History;
        var offset = _scroll?.VerticalOffset ?? 0;
        for (var index = 0; index < state.History.Count; index++)
        {
            var entry = state.History[index];
            if (_rows.TryGetValue(entry.Key, out var row))
            {
                row.Update(entry);
                if (!ReferenceEquals(_entries[index], row)) _entries.Move(_entries.IndexOf(row), index);
                continue;
            }
            row = new HistoryRow(entry);
            _entries.Insert(index, row);
            _rows[entry.Key] = row;
        }
        UpdateBookmarks();
        Dispatcher.BeginInvoke(() => _scroll?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private sealed class HistoryRow(LiveEntry entry) : System.ComponentModel.INotifyPropertyChanged
    {
        private LiveEntry _entry = entry;
        private bool _bookmarked;
        public LiveEntry Entry => _entry;
        public string BookmarkMark => _bookmarked ? "◆" : "◇";
        public string BookmarkTip => _bookmarked ? "取消收藏此事件" : "收藏此事件";
        internal void SetBookmarked(bool value)
        {
            if (_bookmarked == value) return;
            _bookmarked = value;
            PropertyChanged?.Invoke(this, new(nameof(BookmarkMark))); PropertyChanged?.Invoke(this, new(nameof(BookmarkTip)));
        }
        public string Display => _entry.Display;
        public override string ToString() => Display;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        internal void Update(LiveEntry value)
        {
            if (_entry == value) return;
            _entry = value;
            PropertyChanged?.Invoke(this, new(nameof(Display)));
        }
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly Slider _width;
    private readonly Slider _font;
    private readonly Slider _opacity;
    private readonly TextBox _hide;
    private readonly TextBox _clickThrough;
    private readonly TextBlock _error;
    private readonly ComboBox _mode;
    private readonly CheckBox _importantOnly;
    private readonly CheckBox _followedOnly;
    private readonly TextBox _favorite;
    private readonly CheckBox _capsule;
    private readonly ComboBox _content;
    private readonly ComboBox _source, _delivery;
    internal SettingsWindow(AppSettings settings, string error, string transport, Func<AppSettings, string?> apply)
    {
        Ui.Initialize(this, "设置 · NBA 直播条", 520, 730);
        var root = new StackPanel { Margin = new(28, 22, 28, 22) };
        root.Children.Add(Ui.Text("让直播安静地待在角落", 20));
        root.Children.Add(Ui.Text("浅灰便签 · 文字始终清晰 · 不播放声音", 12));
        root.Children.Add(Ui.Text("文字来源", 12));
        _source = new ComboBox { ItemsSource = new[] { "优先主播解说（无主播时自动回退）", "赛况实录" }, SelectedIndex = (int)settings.TextSource, Margin = new(0, 0, 0, 12), Padding = new(8, 6, 8, 6) };
        root.Children.Add(_source);
        root.Children.Add(Ui.Text("文字展示", 12));
        _delivery = new ComboBox { ItemsSource = new[] { "实时最新 · 直接显示最近动态", "完整补播 · 每页停留 4 秒" }, SelectedIndex = (int)settings.TextDelivery, Margin = new(0, 0, 0, 12), Padding = new(8, 6, 8, 6) };
        root.Children.Add(_delivery);
        root.Children.Add(Ui.Text("主播保留全部原文，重要事件与球员筛选仅作用于赛况实录；统计与走势图使用独立赛况快照。主播实况对照验证待完成。", 11));
        _capsule = new CheckBox { Content = "胶囊模式：280×30，悬停展开", IsChecked = settings.CapsuleEnabled, Margin = new(0, 0, 0, 12) };
        root.Children.Add(_capsule);
        root.Children.Add(Ui.Text("展开内容", 12));
        _content = new ComboBox { ItemsSource = new[] { "文字直播", "关注球员卡片", "场上五人 · 双方迷你阵容" }, SelectedIndex = (int)settings.ContentMode, Padding = new(8, 6, 8, 6), Margin = new(0, 0, 0, 12) };
        root.Children.Add(_content);
        root.Children.Add(Ui.Text("在球员统计表点击 ☆，最多关注 3 人；卡片每 8 秒切换，悬停暂停。", 11));
        root.Children.Add(Ui.Text("场上五人显示双方两排球衣，展开高度 154 DIP；名单未确认时明确提示。", 11));
        root.Children.Add(Ui.Text("显示内容", 12));
        _mode = new ComboBox { ItemsSource = new[] { "仅比分 · 58 DIP 高", "比分＋一条动态 · 84 DIP 高", "比分＋两条动态 · 110 DIP 高" },
            SelectedIndex = (int)settings.DisplayMode, Padding = new(8, 6, 8, 6), Margin = new(0, 0, 0, 12) };
        root.Children.Add(_mode);
        _importantOnly = new CheckBox { Content = "只看得分、犯规、暂停和节次变化", IsChecked = settings.ImportantEventsOnly, Margin = new(0, 0, 0, 12),
            ToolTip = "按关键词筛选，可能存在遗漏；历史窗口始终保留全部文字。" };
        root.Children.Add(_importantOnly);
        _followedOnly = new CheckBox { Content = "文字直播只看关注球员", IsChecked = settings.FollowedPlayersOnly, Margin = new(0, 0, 0, 12),
            ToolTip = "按本场名单可靠匹配姓名；简称有歧义时不猜测。可与重要事件筛选叠加，历史和回顾仍保留全部。" };
        root.Children.Add(_followedOnly);
        _favorite = AddInput(root, "关注球队", settings.FavoriteTeam);
        _favorite.ToolTip = "填写与赛程一致的球队名，例如：湖人。仅优先排列，不自动切场；留空表示不设置。";
        _width = AddSlider(root, "窗口宽度", 280, 480, settings.Width, "0 DIP");
        _font = AddSlider(root, "文字大小", 11, 16, settings.FontSize, "0 DIP");
        _opacity = AddSlider(root, "背景不透明度", 20, 100, settings.BackgroundOpacity * 100, "0' %'");
        _hide = AddInput(root, "隐藏／恢复", settings.HideHotkey);
        _clickThrough = AddInput(root, "鼠标穿透开关", settings.LockHotkey);
        root.Children.Add(Ui.Text("快捷键支持 Ctrl、Alt、Shift，加字母、数字或 F1～F24。\n鼠标穿透默认关闭，也可从托盘菜单恢复。", 11));
        _error = Ui.Text(error, 11);
        _error.MinHeight = 32;
        root.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Ui.Button("保存设置", () =>
        {
            var candidate = settings with { Width = _width.Value, FontSize = _font.Value,
                BackgroundOpacity = _opacity.Value / 100, HideHotkey = _hide.Text.Trim(), LockHotkey = _clickThrough.Text.Trim(),
                DisplayMode = (OverlayMode)_mode.SelectedIndex, ImportantEventsOnly = _importantOnly.IsChecked == true,
                FollowedPlayersOnly = _followedOnly.IsChecked == true, FavoriteTeam = _favorite.Text.Trim() };
            candidate = candidate with { CapsuleEnabled = _capsule.IsChecked == true, ContentMode = (OverlayContent)_content.SelectedIndex,
                TextSource = (TextSourcePreference)_source.SelectedIndex, TextDelivery = (TextDelivery)_delivery.SelectedIndex };
            var result = apply(candidate);
            if (result is null) Close(); else _error.Text = result;
        }));
        buttons.Children.Add(Ui.Button("取消", Close));
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = $"数据连接：{transport}", FontSize = 10, Foreground = Brushes.Gray, Margin = new(0, 18, 0, 0) });
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static Slider AddSlider(Panel parent, string label, double min, double max, double value, string format)
    {
        var row = new DockPanel { Margin = new(0, 8, 0, 2) };
        var readout = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
        DockPanel.SetDock(readout, Dock.Right);
        row.Children.Add(readout);
        row.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        parent.Children.Add(row);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(0, 5, 0, 12) };
        void Update() => readout.Text = slider.Value.ToString(format);
        slider.ValueChanged += (_, _) => Update();
        Update();
        parent.Children.Add(slider);
        return slider;
    }
    private static TextBox AddInput(Panel parent, string label, string value)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 12) };
        row.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        var input = new TextBox { Text = value, FontSize = 13, MaxLength = 40 };
        row.Children.Add(input);
        parent.Children.Add(row);
        return input;
    }
}
