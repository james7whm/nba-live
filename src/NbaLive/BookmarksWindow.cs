using System.Windows.Data;

namespace NbaLive;

internal sealed class BookmarksWindow : Window
{
    private readonly EventBookmarks _bookmarks;
    private readonly bool _demo;
    private readonly ComboBox _games = new() { Width = 360, Padding = new(6), Margin = new(0, 0, 8, 0), DisplayMemberPath = nameof(GameChoice.Label) };
    private readonly ListBox _entries = new() { Background = Brushes.Transparent, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = Ui.Text("", 11);
    private bool _refreshing;
    private string? _gameId;
    private ScrollViewer? _scroll;
    private sealed record GameChoice(string? Id, string Label);
    internal BookmarksWindow(EventBookmarks bookmarks, bool demo)
    {
        _bookmarks = bookmarks; _demo = demo;
        Ui.Initialize(this, "精彩收藏 · NBA 直播条", 780, 570);
        var root = new DockPanel { Margin = new(24) };
        var header = new StackPanel(); header.Children.Add(Ui.Text("精彩收藏", 20));
        header.Children.Add(Ui.Text("仅保存手动收藏的原文 · 保留在本机，重启和切场后仍可查看", 11));
        header.Children.Add(_games);
        var actions = new WrapPanel { Margin = new(0, 10, 0, 12) };
        actions.Children.Add(Ui.Button("复制所选", () => Copy(_entries.SelectedItem is BookmarkedEvent selected ? [selected] : [])));
        actions.Children.Add(Ui.Button("复制当前列表", () => Copy(_entries.Items.Cast<BookmarkedEvent>())));
        actions.Children.Add(Ui.Button("取消所选收藏", () =>
        {
            if (_entries.SelectedItem is BookmarkedEvent selected) _bookmarks.Remove(selected);
            else _status.Text = "请先选择一条收藏。";
        }));
        header.Children.Add(actions); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom); _status.Margin = new(0, 12, 0, 0); root.Children.Add(_status);
        VirtualizingPanel.SetIsVirtualizing(_entries, true); VirtualizingPanel.SetVirtualizationMode(_entries, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(BookmarkedEvent.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(MarginProperty, new Thickness(0, 10, 8, 10));
        _entries.ItemTemplate = new DataTemplate { VisualTree = text };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(BookmarkedEvent.Display))));
        _entries.ItemContainerStyle = itemStyle;
        root.Children.Add(_entries); Content = root;
        _games.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _gameId = (_games.SelectedItem as GameChoice)?.Id; Refresh(); _scroll?.ScrollToTop();
        };
        _bookmarks.Changed += Refresh;
        Loaded += (_, _) => _scroll = FindScroll(_entries);
        Closed += (_, _) => _bookmarks.Changed -= Refresh;
        Refresh();
    }
    private void Refresh()
    {
        var selected = (_entries.SelectedItem as BookmarkedEvent)?.Identity;
        var offset = _scroll?.VerticalOffset ?? 0;
        _refreshing = true;
        var games = _bookmarks.Items.DistinctBy(e => e.Game.Id).Select(e => new GameChoice(e.Game.Id, $"{e.Game.Date:yyyy-MM-dd} · {e.Game.Away} vs {e.Game.Home}")).ToArray();
        if (_gameId is not null && games.All(g => g.Id != _gameId)) _gameId = null;
        _games.ItemsSource = new[] { new GameChoice(null, "全部比赛") }.Concat(games).ToArray();
        _games.SelectedItem = _games.Items.Cast<GameChoice>().First(g => g.Id == _gameId);
        var entries = _bookmarks.Items.Where(e => _gameId is null || e.Game.Id == _gameId).ToArray();
        _entries.ItemsSource = entries;
        _entries.SelectedItem = entries.FirstOrDefault(e => e.Identity == selected);
        _refreshing = false;
        _status.Text = _bookmarks.Error ?? ((_demo ? "演示数据 · " : "") + (entries.Length == 0 ? "暂无收藏，可从历史或回顾中添加。" : $"当前 {entries.Length} 条 · 全部 {_bookmarks.Items.Count} 条 · 由收藏时间近到远"));
        Dispatcher.BeginInvoke(() => _scroll?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }
    private void Copy(IEnumerable<BookmarkedEvent> entries)
    {
        var text = EventBookmarks.Format(entries);
        if (text.Length == 0) { _status.Text = "请先选择收藏，或在历史／回顾中添加。"; return; }
        try { Clipboard.SetText(text); _status.Text = "已复制为纯文本。"; }
        catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "剪贴板正忙，请稍后重试。"; }
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
