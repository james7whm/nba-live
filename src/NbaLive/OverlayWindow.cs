using System.Windows.Interop;
using System.Text.RegularExpressions;

namespace NbaLive;

internal sealed class OverlayWindow : Window
{
    private readonly Border _surface;
    private readonly Grid _full;
    private readonly Grid _capsule;
    private readonly TextBlock _score = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _progress = new() { FontSize = 10, MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _capsuleText = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _warning = new() { Text = "离线", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 0, 0) };
    private readonly TextBlock[] _lines = [new(), new()];
    private readonly TextBlock _status = new() { FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pending = new() { FontSize = 9, MaxWidth = 62, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _toolbar;
    private readonly StackPanel _navigation;
    private readonly Button _choosePlayers;
    private readonly Button _modeButton;
    private readonly Button _recapButton;
    private readonly ReadingState _reading = new();
    private readonly PlayerRotation _rotation = new();
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _rotationTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppSettings _settings;
    private StatisticsState? _latestStatistics;
    private StatisticsState? _visibleStatistics;
    private Native.Geometry? _rest;
    private bool _expanded;
    private bool _placing;
    private bool _ready;
    private bool _demo;
    private bool _menuHeld;
    private bool _tipHeld;
    private bool _dragging;
    private int _layoutVersion;
    internal event Action? ChooseRequested;
    internal event Action? HistoryRequested;
    internal event Action? StatisticsRequested;
    internal event Action? SettingsRequested;
    internal event Action? HideRequested;
    internal event Action? RecapRequested;
    internal event Action? CapsuleRequested;
    internal event Action<OverlayContent>? ContentRequested;
    internal event Action? ContextMenuRequested;
    internal event Action<int>? HotkeyPressed;
    internal event Action<int, int>? PositionSaved;

    internal OverlayWindow(AppSettings settings)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        _settings = settings; Title = "NBA 直播条";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        _full = new Grid { Margin = new(12, 8, 12, 5) };
        _full.RowDefinitions.Add(new() { Height = new(22) });
        _full.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _full.RowDefinitions.Add(new() { Height = new(22) });
        var header = new DockPanel();
        DockPanel.SetDock(_progress, Dock.Right);
        header.Children.Add(_progress); header.Children.Add(_score); _full.Children.Add(header);
        var content = new Grid(); Grid.SetRow(content, 1); _full.Children.Add(content);
        var lines = new StackPanel(); content.Children.Add(lines);
        foreach (var line in _lines)
        {
            line.Height = 22; line.VerticalAlignment = VerticalAlignment.Center;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetInitialShowDelay(line, 500); lines.Children.Add(line);
        }
        _navigation = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        _navigation.Children.Add(Tool("‹", () => MovePlayer(-1), "上一位关注球员"));
        _navigation.Children.Add(Tool("›", () => MovePlayer(1), "下一位关注球员"));
        content.Children.Add(_navigation);
        _choosePlayers = Tool("去选择球员", () => StatisticsRequested?.Invoke());
        _choosePlayers.HorizontalAlignment = HorizontalAlignment.Left; _choosePlayers.VerticalAlignment = VerticalAlignment.Bottom;
        content.Children.Add(_choosePlayers);
        var footer = new Grid(); Grid.SetRow(footer, 2); _full.Children.Add(footer);
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumnSpan(_status, 2); footer.Children.Add(_status); footer.Children.Add(_pending);
        _toolbar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Hidden };
        var drag = new TextBlock { Text = "⠿", Cursor = Cursors.SizeAll, Padding = new(4, 0, 5, 0), ToolTip = "拖动到屏幕角落" };
        drag.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragOverlay(); };
        _toolbar.Children.Add(drag);
        _modeButton = Tool("文字 ▾", OpenContentMenu); _toolbar.Children.Add(_modeButton);
        _recapButton = Tool("回顾", () => RecapRequested?.Invoke()); _toolbar.Children.Add(_recapButton);
        _toolbar.Children.Add(Tool("更多", OpenMoreMenu));
        _toolbar.Children.Add(Tool("隐藏", () => HideRequested?.Invoke()));
        Grid.SetColumn(_toolbar, 1); footer.Children.Add(_toolbar);
        _capsule = new Grid { Margin = new(10, 0, 10, 0) };
        _capsule.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _capsule.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _capsule.Children.Add(_capsuleText); Grid.SetColumn(_warning, 1); _capsule.Children.Add(_warning);
        var layers = new Grid(); layers.Children.Add(_full); layers.Children.Add(_capsule);
        _surface = new Border { Child = layers }; Content = _surface;
        MouseEnter += (_, _) => EnterReading(); MouseLeave += (_, _) => ScheduleLeave();
        ToolTipOpening += (_, _) => { _tipHeld = true; _leaveTimer.Stop(); };
        ToolTipClosing += (_, _) => { _tipHeld = false; ScheduleLeave(); };
        MouseRightButtonUp += (_, _) => WithInteractionHold(() => ContextMenuRequested?.Invoke());
        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); if (IsMouseOver && IsVisible) SetExpanded(true); };
        _leaveTimer.Tick += (_, _) =>
        {
            if (IsMouseOver || _menuHeld || _tipHeld || _dragging) return;
            _leaveTimer.Stop(); LeaveReading();
        };
        _rotationTimer.Tick += (_, _) =>
        {
            _rotation.Move(_settings.FollowedPlayers, 1, automatic: true, paused: _reading.IsPaused, visible: IsVisible && !Collapsed);
            RenderContent();
        };
        _statusTimer.Tick += (_, _) => { if (IsVisible) UpdateStatus(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Clamp(true); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _expandTimer.Stop(); _leaveTimer.Stop(); LeaveReading(); }
            else { RenderContent(); UpdateStatus(); UpdateRotationTimer(); }
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle; Native.Configure(hwnd, false);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        };
        Loaded += (_, _) => { _ready = true; Reposition(_settings.X, _settings.Y); _statusTimer.Start(); };
        LocationChanged += (_, _) => { if (_ready && !_placing && !_dragging) { _saveTimer.Stop(); _saveTimer.Start(); } };
        Closed += (_, _) => { _saveTimer.Stop(); _expandTimer.Stop(); _leaveTimer.Stop(); _statusTimer.Stop(); _rotationTimer.Stop(); };
        ApplyStyle(settings); Render(new(null, [], "请选择一场 NBA 比赛", false, false), false);
    }
    private bool Collapsed => _settings.CapsuleEnabled && !_expanded;
    private Button Tool(string label, Action action, string? tip = null)
    {
        var button = new Button { Content = label, FontSize = 10, Padding = new(5, 0, 5, 0), Margin = new(1, 0, 0, 0),
            BorderThickness = new(0), Background = Brushes.Transparent, Focusable = false, ToolTip = tip };
        button.Click += (_, _) => action(); return button;
    }
    private void OpenContentMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "文字直播", () => ContentRequested?.Invoke(OverlayContent.Text), _settings.ContentMode == OverlayContent.Text);
        AddMenu(menu, "关注球员", () => ContentRequested?.Invoke(OverlayContent.Players), _settings.ContentMode == OverlayContent.Players);
        ShowMenu(menu, _modeButton);
    }
    private void OpenMoreMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "选择比赛", () => ChooseRequested?.Invoke());
        AddMenu(menu, "本次会话历史", () => HistoryRequested?.Invoke());
        AddMenu(menu, "球员技术统计／关注球员", () => StatisticsRequested?.Invoke());
        AddMenu(menu, "设置", () => SettingsRequested?.Invoke());
        AddMenu(menu, "胶囊模式", () => CapsuleRequested?.Invoke(), _settings.CapsuleEnabled);
        ShowMenu(menu, _toolbar);
    }
    private static void AddMenu(ContextMenu menu, string label, Action action, bool? check = null)
    {
        var item = new MenuItem { Header = label, IsCheckable = check.HasValue, IsChecked = check == true };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    private void ShowMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Opened += (_, _) => { _menuHeld = true; _leaveTimer.Stop(); };
        menu.Closed += (_, _) => { _menuHeld = false; ScheduleLeave(); };
        menu.IsOpen = true;
    }
    internal void WithInteractionHold(Action action)
    {
        _menuHeld = true; _leaveTimer.Stop();
        try { action(); } finally { _menuHeld = false; ScheduleLeave(); }
    }
    private void EnterReading()
    {
        _leaveTimer.Stop();
        if (!_reading.IsPaused) { _reading.Pause(); _visibleStatistics = _latestStatistics; }
        _toolbar.Visibility = Visibility.Visible; _status.Visibility = Visibility.Hidden;
        if (Collapsed) _expandTimer.Start();
        UpdateStatus(); UpdateRotationTimer();
    }
    private void ScheduleLeave()
    {
        _expandTimer.Stop();
        if (!IsMouseOver && !_menuHeld && !_tipHeld) _leaveTimer.Start();
    }
    private void LeaveReading()
    {
        _reading.Resume(); _visibleStatistics = _latestStatistics;
        _toolbar.Visibility = Visibility.Hidden; _status.Visibility = Visibility.Visible;
        SetExpanded(false); RenderContent(); UpdateStatus(); UpdateRotationTimer();
    }
    private void MovePlayer(int direction) { _rotation.Move(_settings.FollowedPlayers, direction); RenderContent(); }
    private void UpdateRotationTimer()
    {
        if (IsVisible && !Collapsed && !_reading.IsPaused && _settings.ContentMode == OverlayContent.Players && _settings.FollowedPlayers.Length > 1)
        { if (!_rotationTimer.IsEnabled) _rotationTimer.Start(); }
        else _rotationTimer.Stop();
    }
    internal void ApplyStyle(AppSettings settings)
    {
        var wasCapsule = _settings.CapsuleEnabled;
        if (wasCapsule != settings.CapsuleEnabled) _rest = null;
        _settings = settings; _rotation.Sync(settings.FollowedPlayers);
        if (!settings.CapsuleEnabled) _expanded = false;
        _score.FontSize = _capsuleText.FontSize = settings.FontSize;
        foreach (var line in _lines) line.FontSize = settings.FontSize;
        _surface.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * settings.BackgroundOpacity), 239, 241, 243));
        LayoutWindow(save: _ready && (wasCapsule != settings.CapsuleEnabled || !_expanded));
        RenderContent(); UpdateStatus(); UpdateRotationTimer();
    }
    private void SetExpanded(bool expanded)
    {
        if (!_settings.CapsuleEnabled || _expanded == expanded) return;
        if (expanded && _ready) _rest = Native.GetGeometry(this);
        _expanded = expanded; LayoutWindow(save: false); RenderContent(); UpdateRotationTimer();
    }
    private void LayoutWindow(bool save)
    {
        var height = Collapsed ? 30 : _settings.CapsuleEnabled || _settings.ContentMode == OverlayContent.Players ? 110 :
            _settings.DisplayMode switch { OverlayMode.ScoreOnly => 58, OverlayMode.OneEvent => 84, _ => 110 };
        var width = Collapsed ? 280 : _settings.Width;
        _full.Visibility = Collapsed ? Visibility.Collapsed : Visibility.Visible;
        _capsule.Visibility = Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _surface.CornerRadius = new(Collapsed ? 15 : 4);
        if (!_ready) { Width = width; Height = height; return; }
        _saveTimer.Stop(); _placing = true;
        var token = ++_layoutVersion; var before = Native.GetGeometry(this);
        var newWidth = (int)Math.Round(width * before.Scale); var newHeight = (int)Math.Round(height * before.Scale);
        WindowPosition position;
        if (_settings.CapsuleEnabled && _rest is { } rest)
            position = Collapsed ? WindowPlacement.Calculate(before.Area, newWidth, newHeight, before.Scale, rest.X, rest.Y) :
                WindowPlacement.ExpandContaining(before.Area, rest.X, rest.Y, (int)Math.Round(280 * before.Scale),
                    (int)Math.Round(30 * before.Scale), newWidth, newHeight, before.Scale);
        else position = WindowPlacement.PreserveCorner(before.Area, before.Width, before.Height, newWidth, newHeight, before.Scale, before.X, before.Y);
        Native.ResizeAt(this, width, height, position);
        if (Collapsed) _rest = Native.GetGeometry(this);
        Dispatcher.BeginInvoke(() =>
        {
            if (token != _layoutVersion) return;
            _placing = false; if (save) CommitPosition();
        }, DispatcherPriority.Loaded);
    }
    internal void Render(SessionState state, bool demo)
    {
        _demo = demo;
        var priorId = _reading.Latest?.SelectedGame?.Id ?? _reading.Latest?.Snapshot?.Game.Id;
        var id = state.SelectedGame?.Id ?? state.Snapshot?.Game.Id;
        if (id != priorId || state.IsLoading) _visibleStatistics = _latestStatistics;
        _reading.Accept(state); RenderContent(); UpdateStatus();
    }
    internal void RenderStatistics(StatisticsState state)
    {
        _latestStatistics = state;
        if (!_reading.IsPaused || _visibleStatistics?.Snapshot is null) _visibleStatistics = state;
        RenderContent(); UpdateStatus();
    }
    internal void SetUnread(int count)
    {
        _recapButton.Content = count == 0 ? "回顾" : $"回顾({(count > 99 ? "99+" : count)})";
        _recapButton.ToolTip = $"上次回顾后新收到 {count} 条事件；首次打开显示最近 10 条。";
    }
    private void RenderContent()
    {
        if (_reading.Visible is not { } state) return;
        var game = state.Snapshot?.Game ?? state.SelectedGame;
        _score.Text = game?.Score ?? "NBA 直播条"; _score.ToolTip = _score.Text;
        _progress.Text = game?.Progress ?? ""; _progress.ToolTip = _progress.Text;
        var progress = Regex.Replace(game?.Progress ?? "", "第([一二三四1234])节", m =>
            "Q" + ("一二三四".IndexOf(m.Groups[1].Value, StringComparison.Ordinal) is var n && n >= 0 ? (n + 1).ToString() : m.Groups[1].Value));
        _capsuleText.Text = (_demo ? "演示 · " : "") + (game is null ? "NBA 直播条 · 请选择比赛" :
            $"{game.Away} {game.AwayScore?.ToString() ?? "—"}:{game.HomeScore?.ToString() ?? "—"} {game.Home} · {progress}");
        _modeButton.Content = _settings.ContentMode == OverlayContent.Players ? "球员 ▾" : "文字 ▾";
        var players = _settings.ContentMode == OverlayContent.Players;
        _navigation.Visibility = players && _settings.FollowedPlayers.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        _choosePlayers.Visibility = players && _settings.FollowedPlayers.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _lines[1].Margin = new(0, 0, players && _settings.FollowedPlayers.Length > 1 ? 50 : 0, 0);
        var count = players || _settings.CapsuleEnabled ? 2 : (int)_settings.DisplayMode;
        for (var index = 0; index < 2; index++) _lines[index].Visibility = index < count ? Visibility.Visible : Visibility.Collapsed;
        if (players)
        {
            var favorite = _settings.FollowedPlayers.FirstOrDefault(p => p.Id == _rotation.CurrentId);
            var card = favorite is null ? new PlayerCard("还没有关注球员", "", "在统计表中点击 ☆，最多关注 3 人。") :
                PlayerCards.Create(favorite, _visibleStatistics, game?.Id);
            _lines[0].Text = card.FirstLine; _lines[1].Text = card.SecondLine;
            foreach (var line in _lines) line.ToolTip = card.Details;
        }
        else
        {
            var recent = EventSelection.Recent(state.History, count, _settings.ImportantEventsOnly);
            for (var index = 0; index < 2; index++)
            {
                _lines[index].Text = index < recent.Count ? recent[index].Display : index == 0 ?
                    state.History.Count > 0 && _settings.ImportantEventsOnly ? "暂无匹配事件 · 历史可查看全部" : state.Message : "";
                _lines[index].ToolTip = _lines[index].Text;
            }
        }
    }
    private void UpdateStatus()
    {
        if (_reading.Latest is not { } state) return;
        var status = SessionStatus.Describe(state, DateTimeOffset.UtcNow); var stats = _latestStatistics;
        var relevant = _settings.ContentMode == OverlayContent.Players && _settings.FollowedPlayers.Length > 0 &&
            stats?.SelectedGame?.Id == (state.SelectedGame?.Id ?? state.Snapshot?.Game.Id);
        if (relevant && stats is not null)
            status += stats.HasError ? $" · {stats.Message}" : stats.Snapshot is { } snapshot ?
                $" · 球员 {MatchTime.Format(snapshot.FetchedAt)}{(DateTimeOffset.UtcNow - snapshot.FetchedAt > TimeSpan.FromSeconds(35) ? "（较旧）" : "")}" : " · 等待球员统计";
        _status.Text = (_demo ? "演示数据 · " : "") + status;
        _status.ToolTip = _status.Text + "\n连通是成功检查比分页面的本机时间；新事件是本机发现新增条目的时间。\n球员统计与比分分别采样；没有新事件不等于断网。";
        _capsuleText.ToolTip = _capsuleText.Text + "\n" + _status.ToolTip;
        var error = state.HasError || relevant && stats?.HasError == true;
        _warning.Text = error ? "离线" : "较旧";
        _warning.Visibility = error || state.Snapshot is { } live && live.Game.Status == GameStatus.Live &&
            DateTimeOffset.UtcNow - live.FetchedAt > TimeSpan.FromSeconds(30) ? Visibility.Visible : Visibility.Collapsed;
        _warning.ToolTip = _status.ToolTip;
        _pending.Text = !_reading.IsPaused ? "" : error ? "连接异常" : _reading.PendingCount > 0 ? $"+{_reading.PendingCount} 条" : "已暂停";
        if (_demo && _reading.IsPaused) _pending.Text = "演示 " + _pending.Text;
        _pending.ToolTip = $"悬停固定内容，后台继续更新；新增 {_reading.PendingCount} 条。\n{status}";
    }
    private void DragOverlay()
    {
        _dragging = true; _leaveTimer.Stop(); _saveTimer.Stop();
        try { DragMove(); Clamp(false, snap: true); CommitPosition(); }
        finally { _dragging = false; ScheduleLeave(); }
    }
    private void CommitPosition()
    {
        var geometry = Native.GetGeometry(this);
        var position = _settings.CapsuleEnabled && _expanded ? WindowPlacement.PreserveCorner(geometry.Area,
            geometry.Width, geometry.Height, (int)Math.Round(280 * geometry.Scale), (int)Math.Round(30 * geometry.Scale),
            geometry.Scale, geometry.X, geometry.Y) : new WindowPosition(geometry.X, geometry.Y);
        if (_settings.CapsuleEnabled) _rest = geometry with { X = position.X, Y = position.Y, Width = (int)Math.Round(280 * geometry.Scale), Height = (int)Math.Round(30 * geometry.Scale) };
        PositionSaved?.Invoke(position.X, position.Y);
    }
    internal void Reposition(int? x, int? y)
    {
        _placing = true;
        if (_settings.CapsuleEnabled && _expanded) { _expanded = false; _rest = null; LayoutWindow(false); }
        Native.Place(this, x, y); _placing = false; CommitPosition();
    }
    private void Clamp(bool save, bool snap = false)
    {
        if (!_ready) return;
        var geometry = Native.GetGeometry(this); _placing = true;
        Native.Place(this, geometry.X, geometry.Y, snap); _placing = false;
        if (save) CommitPosition();
    }
    internal void SetClickThrough(bool value)
    {
        if (value) LeaveReading();
        Native.Configure(new WindowInteropHelper(this).Handle, value);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmMouseActivate) { handled = true; return new IntPtr(3); }
        if (message == Native.WmHotkey) { HotkeyPressed?.Invoke(wParam.ToInt32()); handled = true; }
        if (message is Native.WmDisplayChange or Native.WmDpiChanged)
            Dispatcher.BeginInvoke(() => { Clamp(false); CommitPosition(); LayoutWindow(false); }, DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }
}
