namespace NbaLive;

internal sealed class ChatAvatarPicker : Window
{
    private static readonly List<int> Recent = new();
    private readonly StackPanel _teams = new();
    private readonly WrapPanel _avatars = new();
    private readonly ContentControl _current = new();
    private readonly TextBox _search = new() { Height = 40, Padding = new(10, 4, 10, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _caption = Ui.Text("", 13);
    private string _conference = "东部", _team = "";
    private int _selected;
    internal int SelectedAvatar => _selected;

    internal ChatAvatarPicker(int selected)
    {
        _selected = ChatAvatar.Choices.Any(a => a.Id == selected) ? selected : 0;
        Ui.Initialize(this, "选择头像 · NBA 直播条", 600, 720); MinWidth = Math.Min(400, Width);
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(22), LastChildFill = true }; Content = root;
        var footer = new DockPanel { Margin = new(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var confirm = Ui.Primary("确认选择", () => { Recent.Remove(_selected); Recent.Insert(0, _selected); if (Recent.Count > 3) Recent.RemoveAt(3); DialogResult = true; }); confirm.MinWidth = 100; DockPanel.SetDock(confirm, Dock.Right); footer.Children.Add(confirm);
        var cancel = Ui.Button("取消", () => { DialogResult = false; }); cancel.Margin = new(0, 0, 8, 0); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        var content = new StackPanel(); scroll.Content = content;
        var title = Ui.Text("选择头像", 22); title.Margin = new(0, 0, 0, 14); content.Children.Add(title);
        var card = Ui.Card(_current, new Thickness(12)); card.Margin = new(0, 0, 0, 14); content.Children.Add(card);
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜索球员或球队");
        var hint = Ui.Text("搜索球员或球队", 12); hint.Margin = new(0, 0, 0, 5); content.Children.Add(hint);
        _search.ToolTip = "搜索球员或球队"; _search.TextChanged += (_, _) => RenderAvatars(); content.Children.Add(_search);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 8) };
        foreach (var item in new[] { "最近", "默认", "东部", "西部" })
        {
            var label = item; var button = Ui.Button(label, () => { _conference = label; _team = ""; RenderTeams(); RenderAvatars(); });
            button.Margin = new(0, 0, 8, 0); button.Padding = new(14, 5, 14, 5); tabs.Children.Add(button);
        }
        content.Children.Add(tabs); content.Children.Add(_teams);
        _caption.Margin = new(0, 10, 0, 8); content.Children.Add(_caption);
        _avatars.ItemWidth = 160; _avatars.ItemHeight = 145; content.Children.Add(_avatars);
        RenderCurrent(); RenderTeams(); RenderAvatars();
    }
    private void RenderCurrent()
    {
        var choice = ChatAvatar.Choices.First(c => c.Id == _selected);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var picture = ChatAvatar.Create(choice.Id, 64, true); picture.Margin = new(0, 0, 14, 0); row.Children.Add(picture);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var title = Ui.Text("当前选择", 12); title.Margin = new(0, 0, 0, 4); labels.Children.Add(title);
        var name = Ui.Text(choice.Name, 15); name.Margin = new(0); labels.Children.Add(name); row.Children.Add(labels); _current.Content = row;
    }
    private void RenderTeams()
    {
        _teams.Children.Clear();
        if (_conference == "默认" || _conference == "最近") return;
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5 };
        foreach (var team in TeamCatalog.All.Where(t => t.Conference == _conference))
        {
            var key = team.Key;
            var button = Ui.Button("", () => { _team = key; RenderTeams(); RenderAvatars(); });
            button.Content = new TeamLogo { Team = key, Width = 34, Height = 34 };
            button.Width = 60; button.Height = 48; button.Padding = new(2); button.Margin = new(0, 0, 4, 6);
            button.BorderBrush = Ui.Brush(_team == key ? "AccentBrush" : "LineBrush");
            button.Background = Ui.Brush(_team == key ? "AccentTint" : "CardBrush"); button.ToolTip = team.Name;
            System.Windows.Automation.AutomationProperties.SetName(button, "头像球队 " + team.Name); grid.Children.Add(button);
        }
        _teams.Children.Add(grid);
    }
    private void RenderAvatars()
    {
        _avatars.Children.Clear(); var query = _search.Text.Trim();
        var choices = ChatAvatar.Choices.Where(c =>
            query.Length > 0 ? c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (TeamCatalog.Resolve(c.Team)?.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
            : _conference == "最近" ? Recent.Contains(c.Id) || c.Id == _selected
            : _conference == "默认" ? c.Team.Length == 0
            : _team.Length == 0 ? c.Team.Length == 0
            : c.Team == _team).ToArray();
        _caption.Text = query.Length > 0 ? $"搜索结果 · {choices.Length} 个" : _conference == "最近" ? "最近使用 · 本次运行" : _conference == "默认" ? "默认头像" : _team.Length == 0 ? "选择球队后查看球员头像" : TeamCatalog.Resolve(_team)?.Name + " · 球员头像";
        foreach (var choice in choices)
        {
            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var picture = ChatAvatar.Create(choice.Id, 92); picture.HorizontalAlignment = HorizontalAlignment.Center; card.Children.Add(picture);
            var name = Ui.Text(choice.Name, 12); name.TextAlignment = TextAlignment.Center; name.Margin = new(0, 5, 0, 0); card.Children.Add(name);
            var button = Ui.Button("", () => { _selected = choice.Id; RenderCurrent(); RenderAvatars(); });
            button.Content = card; button.Margin = new(0, 0, 8, 8); button.Padding = new(4);
            button.BorderBrush = Ui.Brush(choice.Id == _selected ? "AccentBrush" : "LineBrush"); button.Background = Ui.Brush(choice.Id == _selected ? "AccentTint" : "CardBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, "选择头像 " + choice.Name); _avatars.Children.Add(button);
        }
    }
}
