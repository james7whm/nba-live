using System.Collections.ObjectModel;
using System.Windows.Data;

namespace NbaLive;

internal sealed class InlineTextHistory : DockPanel
{
    private readonly TextBlock _heading = new() { FontSize = 10, Foreground = Ui.Brush("MutedInk"), Margin = new(0, 5, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _latest = new() { Content = "返回最新", Style = (Style)Application.Current.FindResource("OverlayButton"), FontSize = 10, Margin = new(4,0,0,3), ToolTip = "回到顶部并显示最新直播，后台取数不会因阅读历史暂停" };
    internal event Action? LatestRequested;
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Focusable = false };
    private readonly ObservableCollection<LiveEntry> _rows = [];
    private IReadOnlyList<LiveEntry>? _shown;
    private ScrollViewer? _scroll;
    private bool _showLatest;
    private int _updateVersion;
    internal InlineTextHistory()
    {
        var header = new DockPanel(); SetDock(_latest,Dock.Right); header.Children.Add(_latest); header.Children.Add(_heading);
        SetDock(header, Dock.Top); Children.Add(header); Children.Add(_list);
        _latest.Click += (_,_) => { Open(); LatestRequested?.Invoke(); };
        _list.ItemsSource = _rows;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(LiveEntry.Text)) { Mode = BindingMode.OneWay });
        text.SetBinding(ToolTipProperty, new Binding(nameof(LiveEntry.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var rowContent = new FrameworkElementFactory(typeof(DockPanel));
        var clock = new FrameworkElementFactory(typeof(TextBlock));
        clock.SetBinding(TextBlock.TextProperty, new Binding(nameof(LiveEntry.Clock)) { Mode = BindingMode.OneWay });
        clock.SetValue(DockProperty, Dock.Left); clock.SetValue(WidthProperty, 44.0);
        clock.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); clock.SetValue(TextBlock.ForegroundProperty, Ui.Brush("MutedInk"));
        var clockStyle = new Style(typeof(TextBlock));
        var noClock = new DataTrigger { Binding = new Binding(nameof(LiveEntry.Clock)), Value = "" };
        noClock.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); clockStyle.Triggers.Add(noClock);
        clock.SetValue(StyleProperty, clockStyle); rowContent.AppendChild(clock); rowContent.AppendChild(text);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new Thickness(0, 6, 5, 6));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.SetValue(Border.BorderBrushProperty, Ui.Brush("LineBrush"));
        border.AppendChild(rowContent); _list.ItemTemplate = new DataTemplate { VisualTree = border };
        var row = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource(typeof(ListBoxItem)));
        row.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        row.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        row.Setters.Add(new Setter(FocusableProperty, false));
        row.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(LiveEntry.Display))));
        _list.ItemContainerStyle = row;
        Loaded += (_, _) => { _scroll = FindScroll(_list); ScrollToLatest(); };
    }
    internal void Open() { _showLatest = true; ScrollToLatest(); }
    internal void Reset() { _updateVersion++; _shown = null; _rows.Clear(); _showLatest = true; }
    internal void Update(SessionState state, double fontSize)
    {
        _list.FontSize = fontSize;
        _heading.Text = $"{state.SourceLabel} · 本次会话 {state.History.Count} 条 · " + (state.History.Count == 0 ? "暂无已获取的消息" : "最新在上，向下查看更早消息");
        if (ReferenceEquals(_shown, state.History)) { ScrollToLatest(); return; }
        _shown = state.History;
        var offset = _scroll?.VerticalOffset ?? 0;
        var anchor = offset > 0 ? FindVisibleRow(_list) : null;
        var version = ++_updateVersion;
        // Reverse only this view. Session history, recap and playback retain chronological order.
        var history = state.History;
        var keys = _rows.Select(entry => entry.Key).ToHashSet();
        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[history.Count - 1 - index];
            if (index < _rows.Count && _rows[index].Key == entry.Key) { if (_rows[index] != entry) _rows[index] = entry; continue; }
            var existing = keys.Contains(entry.Key) ? _rows.FirstOrDefault(row => row.Key == entry.Key) : null;
            if (existing is null) _rows.Insert(index, entry);
            else { var old = _rows.IndexOf(existing); if (old != index) _rows.Move(old, index); if (_rows[index] != entry) _rows[index] = entry; }
        }
        while (_rows.Count > history.Count) _rows.RemoveAt(_rows.Count - 1);
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _updateVersion) return;
            _scroll ??= FindScroll(_list);
            if (_showLatest) ScrollToLatest();
            else if (anchor is { } saved && _rows.FirstOrDefault(entry => entry.Key == saved.Key) is { } entry)
            {
                _list.ScrollIntoView(entry); _list.UpdateLayout();
                if (_list.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem item && _scroll is not null)
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + item.TranslatePoint(new(), _list).Y - saved.Top);
            }
            else _scroll?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }
    private void ScrollToLatest()
    {
        if (!_showLatest || !IsVisible || _rows.Count == 0) return;
        _showLatest = false;
        Dispatcher.BeginInvoke(() => { if (IsVisible && _rows.Count > 0) { _list.ScrollIntoView(_rows[0]); _scroll?.ScrollToTop(); } }, DispatcherPriority.Loaded);
    }
    private (string Key, double Top)? FindVisibleRow(DependencyObject element)
    {
        if (element is ListBoxItem item && item.DataContext is LiveEntry entry)
        {
            var top = item.TranslatePoint(new(), _list).Y;
            return top + item.ActualHeight > 0 && top < _list.ActualHeight ? (entry.Key, top) : null;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindVisibleRow(VisualTreeHelper.GetChild(element, index)) is { } found) return found;
        return null;
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScroll(VisualTreeHelper.GetChild(element, index)) is { } found) return found;
        return null;
    }
}
