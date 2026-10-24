using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Documents;

namespace NbaLive;

internal static class Ui
{
    internal static Brush Brush(string key) { ThemeManager.Ensure(); return (Brush)Application.Current.FindResource(key); }
    internal static TextBlock Text(string value, double size = 13) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12),
        FontWeight = size >= 18 ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush(size <= 12 ? "MutedInk" : "Ink")
    };
    internal static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Child = child, Background = Brush("CardSurface"), BorderBrush = Brush("LineBrush"), BorderThickness = new(1),
        CornerRadius = new(10), Padding = padding ?? new Thickness(18)
    };
    internal static StackPanel Section(Panel parent, string title)
    {
        var content = new StackPanel();
        var heading = Text(title, 15); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 18);
        content.Children.Add(heading);
        var card = Card(content); card.Margin = new(0, 0, 0, 14); parent.Children.Add(card);
        return content;
    }
    internal static DockPanel TeamLabel(string team,string suffix = "")
    {
        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var logo = new TeamLogo { Team = team, Width = 20, Height = 20, Margin = new(0,0,7,0) };
        DockPanel.SetDock(logo,Dock.Left); panel.Children.Add(logo);
        panel.Children.Add(new TextBlock {Text = team+suffix, VerticalAlignment = VerticalAlignment.Center}); return panel;
    }
    internal static Button Primary(string label, Action action)
    {
        var button = Button(label, action); button.Style = (Style)Application.Current.FindResource("PrimaryButton"); return button;
    }
    internal static void Field(Panel parent, string label, Control input)
    {
        var text = Text(label, 12); text.Margin = new(0, 0, 0, 7); parent.Children.Add(text);
        input.Margin = new(0, 0, 0, 14); parent.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
    }
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
        window.SourceInitialized += (_, _) => Native.SetThemeCaption(window);
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
    internal static void DismissPopups(DependencyObject root)
    {
        if (root is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = false;
        if (root is ComboBox combo) combo.IsDropDownOpen = false;
        if (root is FrameworkElement element)
        {
            if (element.ContextMenu is { } menu) menu.IsOpen = false;
            if (element.ToolTip is ToolTip tip) tip.IsOpen = false;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) DismissPopups(VisualTreeHelper.GetChild(root, i));
    }
}

internal sealed class GamePickerWindow : Window
{
    private readonly IGameSource _source;
    private readonly ListBox _games = new() { BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, FontSize = 14 };
    private readonly TextBlock _message = Ui.Text("正在读取今日比赛…", 12);
    private readonly Button _choose;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly PremiumDatePicker _date = new() { SelectedDate = MatchTime.Today.ToDateTime(TimeOnly.MinValue), Width = 140, Margin = new(0, 0, 12, 0) };
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
        public FontWeight AwayWeight => Game.Status == GameStatus.Live ? FontWeights.SemiBold : Game.Status == GameStatus.Finished && Game.AwayScore is { } away && Game.HomeScore is { } home && away > home ? FontWeights.Bold : FontWeights.Normal;
        public FontWeight HomeWeight => Game.Status == GameStatus.Live ? FontWeights.SemiBold : Game.Status == GameStatus.Finished && Game.AwayScore is { } away && Game.HomeScore is { } home && home > away ? FontWeights.Bold : FontWeights.Normal;
    }
    internal event Action<GameInfo>? Selected;

    internal GamePickerWindow(IGameSource source, string favoriteTeam)
    {
        _source = source;
        _favoriteTeam = favoriteTeam;
        Ui.Initialize(this, "选择 NBA 比赛 · NBA 直播条", 600, 800);
        Background = Ui.Brush("WindowSurface");
        var root = new DockPanel { Margin = new(22, 24, 22, 22) };
        var heading = new DockPanel { Margin = new(0, 0, 0, 24), Height = 40 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _date.Width = 128; _date.Margin = new(0, 0, 8, 0);
        _date.ToolTip = "选择比赛日期 · 北京时间";
        filters.Children.Add(_date);
        var refresh = Ui.Button("", () => _ = Refresh()); refresh.Style = (Style)Application.Current.FindResource("IconButton");
        refresh.Width = 36; refresh.Height = 40; refresh.Margin = new(0); refresh.Padding = new(7); refresh.ToolTip = "刷新比赛列表（每 60 秒自动更新）";
        refresh.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 19,8 A 8,8 0 1 0 19,16 M 19,3 L 19,9 L 13,9"),
            Stroke = Ui.Brush("MutedInk"), StrokeThickness = 1.5, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        System.Windows.Automation.AutomationProperties.SetName(refresh, "刷新比赛列表"); filters.Children.Add(refresh);
        DockPanel.SetDock(filters, Dock.Right); heading.Children.Add(filters);
        var title = Ui.Text("选择一场比赛", 24); title.Margin = new(0); title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var footer = new DockPanel { LastChildFill = true };
        _choose = Ui.Primary("查看文字直播", Choose); _choose.Margin = new(14, 0, 0, 0); _choose.IsDefault = true; _choose.IsEnabled = false;
        _choose.MinWidth = 150; _choose.Height = 42; _choose.FontSize = 14;
        DockPanel.SetDock(_choose, Dock.Right); footer.Children.Add(_choose);
        _message.Margin = new(0); _message.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_message);
        var footerChrome = new Border { Child = footer, Margin = new(0, 18, 0, 0), Padding = new(0, 18, 0, 0), BorderThickness = new(0, 1, 0, 0), BorderBrush = Ui.Brush("LineBrush") };
        DockPanel.SetDock(footerChrome, Dock.Bottom);
        root.Children.Add(footerChrome);
        _games.ItemContainerStyle = (Style)Application.Current.FindResource("GameCard");
        ScrollViewer.SetHorizontalScrollBarVisibility(_games, ScrollBarVisibility.Disabled);
        _games.ItemTemplate = CreateGameTemplate();
        _games.MouseDoubleClick += (_, _) => Choose();
        _games.SelectionChanged += (_, _) => _choose.IsEnabled = !_busy && _games.SelectedItem is GameChoice;
        root.Children.Add(_games);
        Content = root;
        _date.SelectedDateChanged += async (_, _) => { if (IsLoaded) await Refresh(); };
        Loaded += async (_, _) => { await Refresh(); if (!_closed) _refresh.Start(); };
        _refresh.Tick += async (_, _) => await Refresh();
        Closed += (_, _) => { _closed = true; _refresh.Stop(); _cts.Cancel(); };
    }
    private static DataTemplate CreateGameTemplate()
    {
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:NbaLive;assembly=NbaLive">
              <Grid ToolTip="{Binding Display}" MinHeight="32">
                <Grid.ColumnDefinitions><ColumnDefinition Width="36"/><ColumnDefinition Width="*"/><ColumnDefinition Width="148"/><ColumnDefinition Width="*"/><ColumnDefinition Width="36"/><ColumnDefinition Width="108"/></Grid.ColumnDefinitions>
                <local:TeamLogo Team="{Binding Game.Away}" Width="30" Height="30"/>
                <TextBlock Grid.Column="1" Text="{Binding Game.Away}" FontSize="16" Margin="4,0,0,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <Viewbox Grid.Column="2" Margin="6,0" Stretch="Uniform" StretchDirection="DownOnly" VerticalAlignment="Center">
                <TextBlock TextAlignment="Center" VerticalAlignment="Center" FontSize="27" Foreground="{Binding ScoreInk}">
                  <Run Text="{Binding AwayPoints, Mode=OneWay}" FontWeight="{Binding AwayWeight}"/><Run Text=" : "/><Run Text="{Binding HomePoints, Mode=OneWay}" FontWeight="{Binding HomeWeight}"/>
                </TextBlock>
                </Viewbox>
                <TextBlock Grid.Column="3" Text="{Binding Game.Home}" FontSize="16" Margin="8,0,2,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <local:TeamLogo Grid.Column="4" Team="{Binding Game.Home}" Width="30" Height="30"/>
                <Border Grid.Column="5" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1,0,0,0" Margin="8,0,0,0" Padding="8,0,0,0">
                  <TextBlock Text="{Binding ProgressText}" Foreground="{Binding ScoreInk}" FontSize="13" TextAlignment="Right" VerticalAlignment="Center" TextWrapping="Wrap"/>
                </Border>
              </Grid>
            </DataTemplate>
            """);
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
        _choose.IsEnabled = false;
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
            _choose.IsEnabled = !_closed && _games.SelectedItem is GameChoice;
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
        bookmark.SetValue(StyleProperty, (Style)Application.Current.FindResource("IconButton"));
        bookmark.SetBinding(ContentControl.ContentProperty, new Binding(nameof(HistoryRow.BookmarkMark)));
        bookmark.SetBinding(ToolTipProperty, new Binding(nameof(HistoryRow.BookmarkTip)));
        bookmark.SetValue(DockPanel.DockProperty, Dock.Left); bookmark.SetValue(WidthProperty, 32.0);
        bookmark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top); bookmark.SetValue(FontSizeProperty, 16.0);
        bookmark.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); bookmark.SetValue(PaddingProperty, new Thickness(2));
        bookmark.SetValue(BackgroundProperty, Brushes.Transparent); bookmark.SetValue(BorderThicknessProperty, new Thickness(0));
        row.AppendChild(bookmark); row.AppendChild(text);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new Thickness(0, 12, 8, 12));
        border.SetValue(Border.BorderBrushProperty, Ui.Brush("LineBrush"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.AppendChild(row);
        _list.ItemTemplate = new DataTemplate { VisualTree = border };
        root.Children.Add(Ui.Card(_list, new Thickness(8)));
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
    private readonly ComboBox _favorite;
    private readonly CheckBox _capsule;
    private readonly ComboBox _content;
    private readonly ComboBox _source, _delivery;
    private readonly ComboBox _theme;
    internal SettingsWindow(AppSettings settings, string error, string transport, Func<AppSettings, string?> apply)
    {
        Ui.Initialize(this, "设置 · NBA 直播条", 560, 760);
        var root = new DockPanel { Margin = new(24, 20, 24, 20) };
        var heading = new StackPanel();
        heading.Children.Add(Ui.Text("设置", 24));
        heading.Children.Add(Ui.Text("按你的习惯，把直播安静地放在角落", 12));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        _error = Ui.Text(error, 11); _error.Foreground = BasketballBadge.RedInk;
        _error.Margin = new(0, 0, 0, 8); footer.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("取消", Close); cancel.IsCancel = true; cancel.MinWidth = 80; buttons.Children.Add(cancel);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var sections = new StackPanel { Margin = new(0, 4, 6, 0) };
        var appearance = Ui.Section(sections, "外观");
        _theme = new ComboBox { ItemsSource = new[] { "暖白 · 默认", "暗黑 · 炭灰" }, SelectedIndex = (int)settings.Theme };
        Ui.Field(appearance, "界面主题", _theme);
        _capsule = new CheckBox { Content = "胶囊模式 · 悬停展开", IsChecked = settings.CapsuleEnabled, Margin = new(0, 0, 0, 18) };
        appearance.Children.Add(_capsule);
        _content = new ComboBox { ItemsSource = new[] { "文字直播", "关注球员卡片", "场上五人 · 双方迷你阵容" }, SelectedIndex = (int)settings.ContentMode };

        _mode = new ComboBox { ItemsSource = new[] { "仅比分 · 58 DIP 高", "比分＋一条动态 · 84 DIP 高", "比分＋两条动态 · 110 DIP 高" }, SelectedIndex = (int)settings.DisplayMode };

        _width = AddSlider(appearance, "窗口宽度", 280, 480, settings.Width, "0 DIP");
        _font = AddSlider(appearance, "文字大小", 11, 16, settings.FontSize, "0 DIP");
        _opacity = AddSlider(appearance, "背景不透明度", 20, 100, settings.BackgroundOpacity * 100, "0' %'");
        appearance.Children.Add(Ui.Text("仅调整背景透明度，文字始终清晰；胶囊固定为 280×30 DIP。", 11));

        var reading = Ui.Section(sections, "直播与阅读");
        Ui.Field(reading, "展开内容", _content);
        Ui.Field(reading, "文字显示密度", _mode);
        _source = new ComboBox { ItemsSource = new[] { "优先主播解说（无主播时自动回退）", "赛况实录" }, SelectedIndex = (int)settings.TextSource };
        Ui.Field(reading, "文字来源", _source);
        _delivery = new ComboBox { ItemsSource = new[] { "实时最新 · 直接显示最近动态", "完整补播 · 每页停留 4 秒" }, SelectedIndex = (int)settings.TextDelivery };
        Ui.Field(reading, "文字展示", _delivery);
        _importantOnly = new CheckBox { Content = "只看重要事件", IsChecked = settings.ImportantEventsOnly, Margin = new(0, 0, 0, 16),
            ToolTip = "仅对赛况实录按得分、犯规、暂停和节次关键词筛选；历史保留全部文字。" };
        reading.Children.Add(_importantOnly);
        _followedOnly = new CheckBox { Content = "文字只看关注球员", IsChecked = settings.FollowedPlayersOnly, Margin = new(0, 0, 0, 16),
            ToolTip = "仅对赛况实录按本场名单匹配姓名；简称有歧义时不猜测。历史和回顾保留全部。" };
        reading.Children.Add(_followedOnly);
        _favorite = FavoriteTeams(settings.FavoriteTeam);
        Ui.Field(reading, "关注球队", _favorite);
        _favorite.ToolTip = "选择一支球队，在比赛列表中优先排列；选择“不设置”取消关注。";
        reading.Children.Add(Ui.Text("在比赛列表中优先显示该队比赛，不会自动切换正在看的比赛。", 11));
        reading.Children.Add(Ui.Text("统计表点击 ☆，最多关注 3 人；球员卡每 8 秒切换，悬停暂停。场上五人为双方迷你阵容，名单未确认时会提示。", 11));
        reading.Children.Add(Ui.Text("主播保留全部原文，筛选仅作用于赛况实录；统计与走势图独立采样。主播实况对照验证待完成。", 11));

        var shortcuts = Ui.Section(sections, "快捷键");
        _hide = AddInput(shortcuts, "隐藏／恢复", settings.HideHotkey);
        _clickThrough = AddInput(shortcuts, "鼠标穿透", settings.LockHotkey);
        shortcuts.Children.Add(Ui.Text("支持 Ctrl、Alt、Shift，加字母、数字或 F1～F24。开启穿透后，也可从系统托盘恢复操作。", 11));
        var save = Ui.Primary("保存设置", () =>
        {
            var candidate = settings with { Theme = (AppTheme)_theme.SelectedIndex, Width = _width.Value, FontSize = _font.Value,
                BackgroundOpacity = _opacity.Value / 100, HideHotkey = _hide.Text.Trim(), LockHotkey = _clickThrough.Text.Trim(),
                DisplayMode = (OverlayMode)_mode.SelectedIndex, ImportantEventsOnly = _importantOnly.IsChecked == true,
                FollowedPlayersOnly = _followedOnly.IsChecked == true, FavoriteTeam = (_favorite.SelectedItem as FavoriteTeamChoice)?.Value ?? "",
                CapsuleEnabled = _capsule.IsChecked == true, ContentMode = (OverlayContent)_content.SelectedIndex,
                TextSource = (TextSourcePreference)_source.SelectedIndex, TextDelivery = (TextDelivery)_delivery.SelectedIndex };
            var result = apply(candidate);
            if (result is null) Close(); else _error.Text = result;
        });
        save.IsDefault = true; save.MinWidth = 110; save.Margin = new(0); buttons.Children.Add(save);
        sections.Children.Add(Ui.Text($"数据连接：{transport}", 10));
        root.Children.Add(new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
    }
    private sealed record FavoriteTeamChoice(string Value, string Name);
    private static ComboBox FavoriteTeams(string favorite)
    {
        var choices = new List<FavoriteTeamChoice> { new("", "不设置") };
        choices.AddRange(TeamPalette.All.Select(team => new FavoriteTeamChoice(team.Name, team.Name)));
        var raw = (favorite ?? "").Trim();
        var resolved = TeamPalette.Resolve(raw);
        var value = resolved == TeamPalette.Unknown ? raw : resolved.Name;
        var selected = choices.FirstOrDefault(team => team.Value == value);
        if (selected is null) { selected = new(value, value + "（原有设置）"); choices.Add(selected); }
        var picker = new ComboBox { ItemsSource = choices, SelectedItem = selected, IsEditable = false, IsTextSearchEnabled = true, MaxDropDownHeight = 320 };
        TextSearch.SetTextPath(picker, nameof(FavoriteTeamChoice.Name));
        picker.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:NbaLive;assembly=NbaLive">
              <StackPanel Orientation="Horizontal">
                <local:TeamLogo x:Name="Logo" Team="{Binding Value}" Width="20" Height="20" Margin="0,0,8,0" VerticalAlignment="Center" />
                <TextBlock Text="{Binding Name}" VerticalAlignment="Center" />
              </StackPanel>
              <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding Value}" Value=""><Setter TargetName="Logo" Property="Visibility" Value="Collapsed" /></DataTrigger>
              </DataTemplate.Triggers>
            </DataTemplate>
            """);
        return picker;
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
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
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
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        row.Children.Add(input);
        parent.Children.Add(row);
        return input;
    }
}
