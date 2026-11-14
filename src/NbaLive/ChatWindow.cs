namespace NbaLive;

// The profile view is usable independently; the chat never invents messages while
// the separately deployed service is unavailable.
internal sealed class ChatWindow : Window
{
    private readonly Func<string, int, string, string?> _save;
    private readonly Func<CancellationToken, Task<TeamStandingsSnapshot>> _getStandings;
    private readonly bool _demoStandings;
    private readonly DockPanel _root = new() { Margin = new(22) };
    private readonly StackPanel _body = new();
    private readonly ContentControl _view = new();
    private readonly TextBlock _score = Ui.Text("请选择一场比赛", 13);
    private readonly MatchScoreLine _scoreLine = new(17, 28) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _matchCard;
    private readonly TextBlock _title = Ui.Text("聊天室", 22);
    private readonly TextBlock _connection = Ui.Text("聊天室服务尚未配置，暂时无法收发消息。", 12);
    private readonly Button _profileButton;
    private readonly ChatClient? _client = ChatClient.FromEnvironment();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _standingsTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly List<ChatMessage> _messages = [];
    private readonly StackPanel _messageList = new();
    private ScrollViewer? _messageScroll;
    private ScrollViewer? _profileScroll;
    private Button? _jumpLatest;
    private System.Windows.Controls.Primitives.Popup? _emojiPopup;
    private long _cursor;
    private bool _loading, _sending, _editing;
    private bool _loadingStandings, _closed;
    private TeamStandingsSnapshot? _standings;
    private string _editTeam = "";
    private StackPanel? _teamGroups;
    private TextBlock? _teamStatus;
    private string? _pendingRoom, _pendingText, _pendingRequestId;
    private GameInfo? _game;
    private string _nickname;
    private string _team;
    private int _avatar;

    internal ChatWindow(AppSettings settings, Func<string, int, string, string?> save,
        Func<CancellationToken, Task<TeamStandingsSnapshot>> getStandings, bool demoStandings)
    {
        _save = save; _getStandings = getStandings; _demoStandings = demoStandings; _nickname = settings.ChatNickname; _team = settings.ChatTeam; _avatar = settings.ChatAvatar;
        Ui.Initialize(this, "聊天室 · NBA 直播条", 460, 680);
        MinWidth = Math.Min(360, Width);
        var header = new DockPanel { LastChildFill = true, Margin = new(0, 0, 0, 16) };
        _profileButton = Ui.Button("我的资料 ›", ShowProfile); _profileButton.Margin = new(8, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_profileButton, "我的资料"); UpdateProfileButton();
        DockPanel.SetDock(_profileButton, Dock.Right); header.Children.Add(_profileButton);
        _title.Margin = new(0); _title.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_title);
        DockPanel.SetDock(header, Dock.Top); _root.Children.Add(header);
        _score.Margin = new(0, 5, 0, 0); _score.TextAlignment = TextAlignment.Center;
        var scoreContent = new StackPanel(); scoreContent.Children.Add(_scoreLine); scoreContent.Children.Add(_score);
        _matchCard = Ui.Card(scoreContent, new Thickness(12)); _matchCard.Margin = new(0, 0, 0, 15);
        DockPanel.SetDock(_matchCard, Dock.Top); _root.Children.Add(_matchCard);
        _root.Children.Add(_view); Content = _root;
        _poll.Tick += async (_, _) => await RefreshMessages();
        _standingsTimer.Tick += async (_, _) => await RefreshStandings();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { _poll.Start(); if (_editing) { _standingsTimer.Start(); _ = RefreshStandings(); } await RefreshMessages(); }
            else { _poll.Stop(); _standingsTimer.Stop(); if (_emojiPopup is not null) _emojiPopup.IsOpen = false; }
        };
        Closed += (_, _) => { _closed = true; _poll.Stop(); _standingsTimer.Stop(); _lifetime.Cancel(); _client?.Dispose(); _lifetime.Dispose(); };
        if (_nickname.Length == 0) ShowProfile(); else ShowChat();
    }

    internal void SetGame(GameInfo? game)
    {
        var changed = _game?.Id != game?.Id;
        _game = game;
        _scoreLine.Update(game); _score.Text = game is null ? "请选择一场比赛" : OverlayProgress.Format(game);
        if (changed)
        {
            _cursor = 0; _messages.Clear();
            if (!_editing) ShowChat();
            if (IsVisible) _ = RefreshMessages();
        }
    }
    private void ShowChat()
    {
        _editing = false;
        _standingsTimer.Stop(); _teamGroups = null; _teamStatus = null;
        _title.Text = "聊天室"; _matchCard.Visibility = Visibility.Visible;
        DetachChat(); _view.Content = null; _body.Children.Clear(); _profileButton.IsEnabled = true;
        var chat = new DockPanel { LastChildFill = true };
        var label = Ui.Text($"{_nickname}  ·  {ChatProfile.TeamLabel(_team)}", 12); label.Margin = new(0, 0, 0, 8);
        DockPanel.SetDock(label, Dock.Top); chat.Children.Add(label);
        _connection.Text = _game is null ? "请先选择一场比赛。" : _client is null ? "聊天室服务尚未配置，暂时无法收发消息。" : _messages.Count == 0 ? "暂无消息，来聊聊比赛吧。" : "本场聊天室";
        DockPanel.SetDock(_connection, Dock.Top); chat.Children.Add(_connection);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); chat.Children.Add(footer);
        _jumpLatest = Ui.Button("有新消息 · 返回最新 ↓", () => { _messageScroll?.ScrollToEnd(); if (_jumpLatest is not null) _jumpLatest.Visibility = Visibility.Collapsed; });
        _jumpLatest.Visibility = Visibility.Collapsed; _jumpLatest.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(_jumpLatest);
        var composer = new DockPanel { Margin = new(0, 10, 0, 0) };
        var input = new ChatComposer { IsEnabled = _client is not null && _game is not null, ToolTip = "输入聊天消息" };
        var inputChrome = new Border { Child = input, Height = 44, Padding = new(12, 8, 12, 8), CornerRadius = new(7),
            Background = Ui.Brush("CardBrush"), BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new(1) };
        input.GotKeyboardFocus += (_, _) => inputChrome.BorderBrush = Ui.Brush("AccentBrush");
        input.LostKeyboardFocus += (_, _) => inputChrome.BorderBrush = Ui.Brush("LineBrush");
        var emoji = Ui.Button("☺", () => { if (_emojiPopup is not null) _emojiPopup.IsOpen = !_emojiPopup.IsOpen; });
        emoji.Width = 44; emoji.Height = 44; emoji.Padding = new(0); emoji.FontSize = 21; emoji.Margin = new(0, 0, 8, 0);
        emoji.ToolTip = "选择表情"; System.Windows.Automation.AutomationProperties.SetName(emoji, "选择表情");
        emoji.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = input });
        var emojiGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8, Width = 288 };
        _emojiPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = emoji, Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            VerticalOffset = -6, AllowsTransparency = true, StaysOpen = false
        };
        var picker = new StackPanel();
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(3, 2, 3, 8) };
        Func<string, bool, Task> sendMessage = null!;
        Button staticTab = null!, motionTab = null!;
        staticTab = Ui.Button("表情", () => PopulateEmoji(false));
        motionTab = Ui.Button("动态", () => PopulateEmoji(true));
        staticTab.Style = motionTab.Style = (Style)Application.Current.FindResource("OverlayButton");
        staticTab.Padding = motionTab.Padding = new Thickness(12, 5, 12, 5);
        staticTab.FontSize = motionTab.FontSize = 13; tabs.Children.Add(staticTab); tabs.Children.Add(motionTab);
        picker.Children.Add(tabs);
        picker.Children.Add(new ScrollViewer { Content = emojiGrid, Height = 184, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void PopulateEmoji(bool motion)
        {
            emojiGrid.Children.Clear();
            staticTab.Background = Ui.Brush(motion ? "CardBrush" : "AccentTint");
            motionTab.Background = Ui.Brush(motion ? "AccentTint" : "CardBrush");
            staticTab.Foreground = Ui.Brush(motion ? "MutedInk" : "AccentInk");
            motionTab.Foreground = Ui.Brush(motion ? "AccentInk" : "MutedInk");
            foreach (var (symbol, name) in motion ? ChatEmoji.Choices.Take(24) : ChatEmoji.Choices)
            {
                var value = motion ? "[动:" + symbol + "]" : symbol;
                var choice = Ui.Button("", async () =>
                {
                    if (_emojiPopup is not null) _emojiPopup.IsOpen = false;
                    if (motion) await sendMessage(value, false);
                    else if (!input.InsertEmoji(value)) { _connection.Text = "消息已达长度上限，请删减后再添加表情。"; input.Focus(); }
                });
                choice.Style = (Style)Application.Current.FindResource("OverlayButton");
                choice.Content = ChatEmoji.Picture(symbol, 30, motion);
                choice.Width = 36; choice.Height = 36; choice.MinHeight = 0; choice.Padding = new(2); choice.Margin = new(0);
                choice.ToolTip = motion ? name + " · 点击立即发送" : name;
                System.Windows.Automation.AutomationProperties.SetName(choice, (motion ? "动效 " : "表情 ") + name); emojiGrid.Children.Add(choice);
            }
        }
        PopulateEmoji(false);
        _emojiPopup.Child = new Border { Child = picker, Background = Ui.Brush("CardSurface"), BorderBrush = Ui.Brush("LineBrush"),
            BorderThickness = new(1), CornerRadius = new(10), Padding = new(6) };
        sendMessage = async (text, fromDraft) =>
        {
            var game = _game;
            if (_sending) { _connection.Text = "正在发送，请稍候。"; return; }
            if (_client is null || game is null || text.Length == 0) return;
            if (text.Length > ChatComposer.MaximumLength) { _connection.Text = "消息已达长度上限，请删减后再发送。"; return; }
            _sending = true;
            if (_pendingRoom != game.Id || _pendingText != text || _pendingRequestId is null)
            { _pendingRoom = game.Id; _pendingText = text; _pendingRequestId = Guid.NewGuid().ToString("N"); }
            var requestId = _pendingRequestId;
            try
            {
                await _client.SaveProfile(_nickname, _avatar, _team, _lifetime.Token);
                await _client.Send(game.Id, text, requestId, _lifetime.Token);
                if (_pendingRequestId == requestId) { _pendingRoom = null; _pendingText = null; _pendingRequestId = null; }
                if (_game?.Id == game.Id) { if (fromDraft && input.Value.Trim() == text) input.ClearValue(); await RefreshMessages(); }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
            { if (!_lifetime.IsCancellationRequested) _connection.Text = "发送失败，请检查连接或稍后重试。"; }
            finally { _sending = false; }
        };
        var send = Ui.Primary("发送", async () => await sendMessage(input.Value.Trim(), true));
        send.IsEnabled = input.IsEnabled; send.Height = 44; send.Margin = new(8, 0, 0, 0); send.MinWidth = 70;
        DockPanel.SetDock(emoji, Dock.Left); composer.Children.Add(emoji);
        DockPanel.SetDock(send, Dock.Right); composer.Children.Add(send); composer.Children.Add(inputChrome); footer.Children.Add(composer);
        RenderMessages(); _messageScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _messageList };
        chat.Children.Add(_messageScroll); _view.Content = chat;
        _messageScroll.Loaded += (_, _) => _messageScroll?.ScrollToEnd();
    }
    private void RenderMessages()
    {
        _messageList.Children.Clear();
        var motionBudget = 12;
        for (var messageIndex = 0; messageIndex < _messages.Count; messageIndex++)
        {
            var message = _messages[messageIndex];
            var row = new DockPanel { Margin = new(0, 0, 0, 14), LastChildFill = true };
            var avatar = ChatAvatar.Create(message.Avatar, 40, circular: true); avatar.Margin = new(0, 0, 10, 0); avatar.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(avatar, Dock.Left); row.Children.Add(avatar);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            var meta = new DockPanel { Margin = new(0, 0, 0, 5) };
            var time = Ui.Text(DateTimeOffset.FromUnixTimeSeconds(message.Created).ToLocalTime().ToString("HH:mm"), 11); time.Margin = new(8, 0, 0, 0);
            DockPanel.SetDock(time, Dock.Right); meta.Children.Add(time);
            var identity = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Ui.Text(message.Nickname, 13); name.Margin = new(0, 0, 7, 0); name.FontWeight = FontWeights.SemiBold; identity.Children.Add(name);
            var tagText = Ui.Text(ChatProfile.TeamLabel(message.Team), 11); tagText.Margin = new(0); tagText.Foreground = Ui.Brush(string.IsNullOrEmpty(message.Team) ? "MutedInk" : "AccentInk");
            identity.Children.Add(new Border { Child = tagText, Background = Ui.Brush(string.IsNullOrEmpty(message.Team) ? "SoftBrush" : "AccentTint"), CornerRadius = new(4), Padding = new(5, 1, 5, 1) });
            meta.Children.Add(identity); content.Children.Add(meta);
            var localBudget = messageIndex >= _messages.Count - 12 ? motionBudget : 0;
            var text = ChatEmoji.MessageWithBudget(message.Text, ref localBudget);
            if (messageIndex >= _messages.Count - 12) motionBudget = localBudget;
            var bubble = new Border { Child = text, Background = Ui.Brush("SoftBrush"), CornerRadius = new(8), Padding = new(10, 8, 10, 8), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 310 };
            content.Children.Add(bubble); row.Children.Add(content); _messageList.Children.Add(row);
        }
    }
    private void ShowProfile()
    {
        _editing = true;
        _editTeam = _team;
        _title.Text = "设置聊天资料"; _matchCard.Visibility = Visibility.Collapsed;
        DetachChat(); _view.Content = null; _body.Children.Clear(); _profileButton.IsEnabled = false;
        _messageScroll = null; _jumpLatest = null;
        var profileLayout = new DockPanel { LastChildFill = true };
        var profileFooter = new StackPanel { Margin = new(0, 10, 0, 0) };
        DockPanel.SetDock(profileFooter, Dock.Bottom); profileLayout.Children.Add(profileFooter);
        _profileScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        profileLayout.Children.Add(_profileScroll); _view.Content = profileLayout;
        var avatarLabel = Ui.Text("选择头像", 13); avatarLabel.Margin = new(0, 0, 0, 8); _body.Children.Add(avatarLabel);
        var selected = _avatar;
        var preview = new ContentControl();
        void UpdateAvatarPreview()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var picture = ChatAvatar.Create(selected, 64, circular: true); picture.Margin = new(0, 0, 12, 0); row.Children.Add(picture);
            var label = Ui.Text((ChatAvatar.Choices.FirstOrDefault(c => c.Id == selected)?.Name ?? "默认头像") + "  ·  点击更换 ›", 14);
            label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new(0); row.Children.Add(label); preview.Content = row;
        }
        var pickAvatar = Ui.Button("", () =>
        {
            var picker = new ChatAvatarPicker(selected) { Owner = this };
            if (picker.ShowDialog() == true) { selected = picker.SelectedAvatar; UpdateAvatarPreview(); }
        });
        pickAvatar.Content = preview; pickAvatar.HorizontalContentAlignment = HorizontalAlignment.Left; pickAvatar.Padding = new(10);
        pickAvatar.Margin = new(0, 0, 0, 14); System.Windows.Automation.AutomationProperties.SetName(pickAvatar, "选择头像");
        UpdateAvatarPreview(); _body.Children.Add(pickAvatar);
        var nickname = new TextBox { Text = _nickname, MaxLength = ChatProfile.MaximumNicknameLength, Height = 44, Padding = new(12, 2, 12, 2), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        Ui.Field(_body, "昵称（最多 12 个字）", nickname);
        var teamHeading = Ui.Text("支持的球队（可选）", 13); teamHeading.Margin = new(0, 0, 0, 8); _body.Children.Add(teamHeading);
        _teamStatus = Ui.Text(_standings is null ? "正在读取东西部排名…" : StandingsLabel(), 11); _teamStatus.Margin = new(0, 0, 0, 8); _body.Children.Add(_teamStatus);
        var clearTeam = Ui.Button("不选择球队 · 纯路人", () => { _editTeam = ""; RenderTeams(); });
        clearTeam.HorizontalAlignment = HorizontalAlignment.Left; clearTeam.Margin = new(0, 0, 0, 10); clearTeam.Padding = new(10, 5, 10, 5); _body.Children.Add(clearTeam);
        _teamGroups = new StackPanel(); _body.Children.Add(_teamGroups); RenderTeams();
        var status = Ui.Text("不选择球队时，昵称旁显示“纯路人”。", 12); status.Margin = new(0, 0, 0, 8); profileFooter.Children.Add(status);
        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (_nickname.Length > 0) buttonsRow.Children.Add(Ui.Button("取消", ShowChat));
        var save = Ui.Primary(_nickname.Length == 0 ? "保存并进入" : "保存", () =>
        {
            var normalized = ChatProfile.NormalizeNickname(nickname.Text);
            if (normalized.Length == 0) { status.Text = "请输入有效昵称，最多 12 个字。"; return; }
            var key = _editTeam;
            var error = _save(normalized, selected, key);
            if (error is not null) { status.Text = error; return; }
            _nickname = normalized; _avatar = selected; _team = key; UpdateProfileButton(); ShowChat();
        });
        save.Margin = new(8, 0, 0, 0); buttonsRow.Children.Add(save); profileFooter.Children.Add(buttonsRow);
        if (IsVisible) { _standingsTimer.Start(); _ = RefreshStandings(); }
    }
    private void DetachChat()
    {
        if (_emojiPopup is not null) { _emojiPopup.IsOpen = false; _emojiPopup.Child = null; _emojiPopup = null; }
        if (_connection.Parent is Panel parent) parent.Children.Remove(_connection);
        if (_messageScroll is not null) _messageScroll.Content = null;
        if (_profileScroll is not null) { _profileScroll.Content = null; _profileScroll = null; }
        if (_view.Content is ScrollViewer oldScroll && ReferenceEquals(oldScroll.Content, _body)) oldScroll.Content = null;
    }
    private void UpdateProfileButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var image = ChatAvatar.Create(_avatar, 26, circular: true); image.Margin = new(0, 0, 7, 0); content.Children.Add(image);
        content.Children.Add(new TextBlock { Text = "我的资料 ›", VerticalAlignment = VerticalAlignment.Center });
        _profileButton.Content = content; _profileButton.Padding = new(7, 4, 8, 4);
    }
    private async Task RefreshMessages()
    {
        var game = _game;
        if (_client is null || game is null || _loading || _lifetime.IsCancellationRequested || !IsVisible) return;
        _loading = true;
        try
        {
            var messages = await _client.Read(game.Id, _cursor, _lifetime.Token);
            if (_game?.Id != game.Id || _lifetime.IsCancellationRequested) return;
            foreach (var message in messages.Where(m => m.Id > _cursor).OrderBy(m => m.Id)) { _messages.Add(message); _cursor = message.Id; }
            if (messages.Count > 0 && !_editing)
            {
                var wasAtBottom = _messageScroll is null || _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset < 8;
                RenderMessages(); _connection.Text = "本场聊天室";
                if (wasAtBottom) _ = Dispatcher.BeginInvoke(() => _messageScroll?.ScrollToEnd(), DispatcherPriority.Loaded);
                else if (_jumpLatest is not null) _jumpLatest.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
        { if (!_lifetime.IsCancellationRequested) _connection.Text = "聊天室连接中断，正在重试。"; }
        finally { _loading = false; }
    }
    private string StandingsLabel() => _standings is null ? "排名暂不可用，球队按默认顺序显示" :
        (_demoStandings ? "演示排名 · " : "排名获取于 ") + MatchTime.Format(_standings.FetchedAt) + " · 每 60 秒检查";

    private void RenderTeams()
    {
        if (!_editing || _teamGroups is null) return;
        _teamGroups.Children.Clear();
        foreach (var conference in new[] { "东部", "西部" })
        {
            var title = Ui.Text(conference, 12); title.Margin = new(0, 4, 0, 6); _teamGroups.Children.Add(title);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new(0, 0, 0, 8) };
            foreach (var identity in ChatProfile.TeamsInConference(conference, _standings))
            {
                var key = identity.Key; var selected = _editTeam == key;
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TeamLogo { Team = key, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center });
                var label = Ui.Text(identity.Name, 10); label.Margin = new(0, 2, 0, 0); label.TextAlignment = TextAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
                content.Children.Add(label);
                var button = Ui.Button("", () => { _editTeam = key; RenderTeams(); }); button.Content = content;
                button.Height = 65; button.Padding = new(2); button.Margin = new(0, 0, 4, 4);
                button.BorderBrush = Ui.Brush(selected ? "AccentBrush" : "LineBrush"); button.Background = Ui.Brush(selected ? "AccentTint" : "CardBrush");
                button.ToolTip = identity.Name; System.Windows.Automation.AutomationProperties.SetName(button, "选择球队 " + identity.Name);
                grid.Children.Add(button);
            }
            _teamGroups.Children.Add(grid);
        }
    }

    private async Task RefreshStandings()
    {
        if (_closed || !_editing || !IsVisible || _loadingStandings) return;
        _loadingStandings = true;
        try
        {
            var standings = await _getStandings(_lifetime.Token);
            if (_closed) return;
            _standings = standings;
            if (_editing) { if (_teamStatus is not null) _teamStatus.Text = StandingsLabel(); RenderTeams(); }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or SourceFormatException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            if (!_closed && _editing && _teamStatus is not null) _teamStatus.Text = _standings is null ? "排名暂不可用，球队按默认顺序显示" : StandingsLabel() + " · 刷新失败";
        }
        finally { _loadingStandings = false; }
    }
}
