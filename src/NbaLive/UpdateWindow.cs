using System.Diagnostics;

namespace NbaLive;

internal sealed class UpdateWindow : Window
{
    private readonly UpdateCoordinator _updates;
    private readonly TextBlock _status = Ui.Text("", 12), _version = Ui.Text("", 20), _notes = Ui.Text("", 12);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new(0, 4, 0, 16) };
    private readonly Button _download, _check, _skip, _later;
    internal UpdateWindow(UpdateCoordinator updates, Action install, Action skip)
    {
        _updates = updates; Ui.Initialize(this, "应用更新 · NBA 直播条", 580, 680);
        MinWidth = Math.Min(420, Width); MinHeight = Math.Min(540, Height);
        var root = new DockPanel { Margin = new(24) };
        var head = new StackPanel(); head.Children.Add(Ui.Text("应用更新", 24)); head.Children.Add(Ui.Text($"当前 v{UpdateMaintenance.CurrentVersion} · {(UpdateMaintenance.Variant == "net48" ? "兼容版" : UpdateMaintenance.Variant == "modern-lite" ? "轻量版" : "现代完整版")}", 12));
        head.Children.Add(_version); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var footer = new StackPanel { Margin = new(0, 18, 0, 0) };
        _status.Margin = new(0, 0, 0, 12); footer.Children.Add(_status); footer.Children.Add(_progress);
        var hint = Ui.Text("更新后自动重启，保留配置、关注与收藏。\n未收藏的本次会话历史会清空。", 11);
        hint.LineHeight = 18; hint.Margin = new(0, 0, 0, 20); footer.Children.Add(hint);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _check = Ui.Button("检查更新", () => _ = updates.Check()); _check.MinWidth = 100; _check.Margin = new(0); _check.MinHeight = 40;
        actions.Children.Add(_check);
        _later = Ui.Button("稍后", () => { updates.CancelDownload(); Close(); }); _later.MinWidth = 84; _later.MinHeight = 40; _later.Margin = new(0, 0, 12, 0);
        Grid.SetColumn(_later, 2); actions.Children.Add(_later);
        _download = Ui.Primary("下载并更新", install); _download.MinWidth = 120; _download.MinHeight = 40; _download.Margin = new(0);
        Grid.SetColumn(_download, 3); actions.Children.Add(_download); footer.Children.Add(actions);
        var links = new Grid(); links.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        links.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); links.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var channels = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text("手动下载", 11); label.Margin = new(0, 0, 10, 0); label.VerticalAlignment = VerticalAlignment.Center; channels.Children.Add(label);
        foreach (var channel in new[] { ("Gitee", UpdateSource.GiteePage), ("GitHub", UpdateSource.GithubPage) })
        {
            var link = Ui.Button(channel.Item1, () => Open(channel.Item2)); link.Style = (Style)Application.Current.FindResource("IconButton");
            link.Padding = new(8, 6, 8, 6); link.Margin = new(0, 0, 6, 0); link.SetResourceReference(Control.ForegroundProperty, "MutedInk"); channels.Children.Add(link);
        }
        links.Children.Add(channels);
        _skip = Ui.Button("跳过此版本", skip); _skip.Style = (Style)Application.Current.FindResource("IconButton");
        _skip.Padding = new(8, 6, 8, 6); _skip.Margin = new(0); _skip.SetResourceReference(Control.ForegroundProperty, "MutedInk");
        Grid.SetColumn(_skip, 2); links.Children.Add(_skip);
        footer.Children.Add(new Border { Child = links, BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new(0, 1, 0, 0), Margin = new(0, 20, 0, 0), Padding = new(0, 12, 0, 0) });
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = Ui.Card(_notes), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        updates.Changed += OnState; Closed += (_, _) => { updates.Changed -= OnState; updates.CancelDownload(); }; OnState(updates.State);
    }
    private static void Open(string page) { try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } }
    private void OnState(UpdateState state)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnState(state)); return; }
        _version.Text = state.Release is { } release ? "可更新至 v" + release.Version : "检查正式版更新";
        _status.Text = state.Message; _notes.Text = state.Release is { } r ? $"来源：{r.Origin} · {r.Published}\n\n{r.Notes}" : "国内优先检查 Gitee，无法获取适用版本时尝试 GitHub。\n只有你点击更新后才会下载安装包。";
        _check.IsEnabled = !state.Checking && !state.Downloading; _download.IsEnabled = state.Release is not null && _check.IsEnabled; _skip.IsEnabled = _download.IsEnabled;
        var available = state.Release is not null;
        _download.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _check.Style = (Style)Application.Current.FindResource(available ? typeof(Button) : "PrimaryButton");
        Grid.SetColumn(_check, available ? 0 : 3);
        _later.Content = state.Downloading ? "取消下载" : available ? "稍后" : "关闭";
        _download.Content = state.Downloading ? "正在下载…" : "下载并更新";
        _progress.Visibility = state.Downloading ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = state.Progress < 0; _progress.Value = Math.Max(0, state.Progress * 100);
    }
}
