using System.Windows.Data;

namespace NbaLive;

internal sealed class RecapWindow : Window
{
    private readonly Func<bool, RecapSnapshot> _capture;
    private readonly Action<RecapSnapshot> _markViewed;
    private readonly bool _demo;
    private readonly TextBlock _heading = Ui.Text("刚才发生了啥", 20);
    private readonly TextBlock _summary = Ui.Text("", 12);
    private readonly TextBlock _highlights = Ui.Text("", 13);
    private readonly TextBlock _status = Ui.Text("", 11);
    private readonly Button _new;
    private readonly ListBox _entries = new() { BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private RecapSnapshot? _shown;
    internal RecapWindow(Func<bool, RecapSnapshot> capture, Action<RecapSnapshot> markViewed, bool demo)
    {
        _capture = capture; _markViewed = markViewed; _demo = demo;
        Ui.Initialize(this, "刚才发生了啥 · NBA 直播条", 780, 620);
        var root = new DockPanel { Margin = new(24) };
        var top = new StackPanel(); top.Children.Add(_heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
        _new = Ui.Button("查看新增", () => ShowCapture(false)); actions.Children.Add(_new);
        actions.Children.Add(Ui.Button("重看最近 10 条", () => ShowCapture(true))); top.Children.Add(actions);
        top.Children.Add(_summary);
        top.Children.Add(new ScrollViewer { Content = _highlights, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        top.Children.Add(Ui.Text("本次范围内的全部原文 · 由早到晚", 11));
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _status.Margin = new(0, 12, 0, 0); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        VirtualizingPanel.SetIsVirtualizing(_entries, true);
        VirtualizingPanel.SetVirtualizationMode(_entries, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(LiveEntry.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MarginProperty, new Thickness(0, 8, 0, 8));
        _entries.ItemTemplate = new DataTemplate { VisualTree = text };
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(LiveEntry.Display))));
        _entries.ItemContainerStyle = rowStyle;
        root.Children.Add(_entries); Content = root;
        Loaded += (_, _) => ShowCapture(false);
    }
    private void ShowCapture(bool recent)
    {
        _shown = _capture(recent);
        _heading.Text = _shown.Game?.Score ?? "刚才发生了啥";
        _summary.Text = _shown.Heading;
        _highlights.Text = _shown.Entries.Count == 0 ? "暂无新增事件。可重看最近 10 条，或稍后查看新增。" :
            _shown.Highlights.Count == 0 ? "这段记录没有匹配到关键事件，请查看下方原文。" :
                "关键事件（按来源文字摘录）：\n" + string.Join("\n", _shown.Highlights.Select(e => "· " + e.Display));
        _entries.ItemsSource = _shown.Entries;
        if (_shown.Entries.Count > 0) _entries.ScrollIntoView(_shown.Entries[0]);
        _status.Text = (_demo ? "演示数据 · " : "") + $"回顾生成 {MatchTime.Format(_shown.CreatedAt)} · {_shown.Connection}\n内容固定在生成时；不推断连续得分或事件因果。";
        var displayed = _shown;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && ReferenceEquals(_shown, displayed)) _markViewed(displayed);
        }, DispatcherPriority.Loaded);
    }
    internal void SetUnread(int count) => _new.Content = $"查看新增 ({count})";
    internal void ResetGame()
    {
        _shown = null; _entries.ItemsSource = null; _heading.Text = "比赛已切换";
        _summary.Text = "旧场次回顾已清空，请点击查看新增。"; _highlights.Text = ""; _status.Text = "";
    }
}
