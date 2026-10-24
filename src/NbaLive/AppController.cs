using System.Windows.Interop;

namespace NbaLive;

internal sealed class AppController : IDisposable
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private readonly WebGameSource? _webSource;
    private readonly IGameSource _source;
    private readonly TextFeedCoordinator _session;
    private readonly StatisticsCoordinator _statisticsCoordinator;
    private readonly Dictionary<string, RecapTracker> _recapTrackers = [];
    private RecapTracker Recaps(string key)
    {
        if (!_recapTrackers.TryGetValue(key, out var tracker)) _recapTrackers[key] = tracker = new();
        return tracker;
    }
    private RecapTracker _recaps => Recaps(_session.State.FeedKey);
    private string _shownFeed = "structured";
    private TextOrigin _shownOrigin;
    private readonly EventBookmarks _bookmarks;
    private readonly bool _demo;
    private NativeTray? _tray;
    private readonly SemaphoreSlim _selection = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HotkeyManager? _hotkeys;
    private GamePickerWindow? _picker;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _history;
    private StatisticsWindow? _statistics;
    private RecapWindow? _recapWindow;
    private BookmarksWindow? _bookmarksWindow;
    private readonly HashSet<Window> _hiddenWindows = [];
    private bool _allHidden;
    private bool _clickThrough;
    private bool _disposed;
    private string _error = "";
    internal OverlayWindow Overlay { get; }

    internal AppController(bool demo, bool demoOpening = false, bool demoFinished = false, bool preview = false)
    {
        _demo = demo;
        _store = new SettingsStore(demo || preview ? Path.Combine(Path.GetTempPath(), demo ? "NbaLive-Demo" : "NbaLive-Preview") : null);
        _settings = _store.Load();
        ThemeManager.Apply(_settings.Theme);
        _bookmarks = new(_store.DirectoryPath);
        _error = _store.Error ?? "";
        _source = demo ? new DemoSource(demoOpening, demoFinished) : _webSource = new WebGameSource(loadRosters: true);
        _session = new(_source, _webSource is null ? null : new AnchorSource(_webSource), _settings.TextSource);
        _statisticsCoordinator = new((IStatisticsSource)_source);
        Overlay = new(_settings);
        Overlay.ChooseRequested += OpenPicker;
        Overlay.HistoryRequested += OpenHistory;
        Overlay.BookmarksRequested += OpenBookmarks;
        Overlay.StatisticsRequested += OpenStatistics;
        Overlay.SettingsRequested += OpenSettings;
        Overlay.HideRequested += ToggleVisibility;
        Overlay.RecapRequested += OpenRecap;
        Overlay.CapsuleRequested += ToggleCapsule;
        Overlay.FollowedFilterRequested += ToggleFollowedFilter;
        Overlay.SourceRequested += source => { ApplyPreferences(_settings with { TextSource = source }); Save(); };
        Overlay.DeliveryRequested += delivery => { ApplyPreferences(_settings with { TextDelivery = delivery }); Save(); };
        Overlay.ContentRequested += SetContent;
        Overlay.ContextMenuRequested += () => _tray?.ShowMenu();
        Overlay.HotkeyPressed += id => { if (id == 1) ToggleVisibility(); else if (id == 2) ToggleClickThrough(); };
        Overlay.PositionSaved += (x, y) => { _settings = _settings with { X = x, Y = y }; Save(); };
        _session.Changed += OnState;
        _session.StructuredChanged += OnStructuredState;
        _session.FeedObserved += OnFeedObserved;
        _statisticsCoordinator.Changed += OnStatistics;
    }

    internal async Task Start(bool demoPicker = false, string? previewGame = null)
    {
        Overlay.Show();
        _tray = new(Overlay);
        UpdateTray();
        _tray.Command += id =>
        {
            switch (id)
            {
                case 1: ToggleVisibility(); break;
                case 2: OpenPicker(); break;
                case 3: OpenHistory(); break;
                case 4: ToggleClickThrough(); break;
                case 5: OpenSettings(); break;
                case 7: Application.Current.Shutdown(); break;
                case 8: Overlay.Reposition(null, null); RestoreWindows(); break;
                case 9: OpenStatistics(); break;
                case 10: OpenRecap(); break;
                case 11: ToggleCapsule(); break;
                case 12: SetContent(OverlayContent.Text); break;
                case 13: SetContent(OverlayContent.Players); break;
                case 14: SetContent(OverlayContent.Lineup); break;
                case 15: ToggleFollowedFilter(); break;
                case 16: OpenBookmarks(); break;
                case 17: ApplyPreferences(_settings with { TextSource = TextSourcePreference.AnchorPreferred }); Save(); break;
                case 18: ApplyPreferences(_settings with { TextSource = TextSourcePreference.Structured }); Save(); break;
                case 19: ApplyPreferences(_settings with { TextDelivery = TextDelivery.Latest }); Save(); break;
                case 20: ApplyPreferences(_settings with { TextDelivery = TextDelivery.Paged }); Save(); break;
            }
        };
        _hotkeys = new(new WindowInteropHelper(Overlay).Handle);
        if (!_hotkeys.Apply(_settings.HideHotkey, _settings.LockHotkey)) _error = _hotkeys.Error;
        await UpdateStatisticsDemand();
        if (_demo)
        {
            if (demoPicker) { EnsurePicker(reveal: false); return; }
            var games = await _source.GetGames(MatchTime.Today, _lifetime.Token);
            await Select(games[0]);
            return;
        }
        if (previewGame is not null)
        {
            var games = await _source.GetGames(MatchTime.Today, _lifetime.Token);
            var requested = games.FirstOrDefault(game => game.Id == previewGame);
            if (requested is not null) { await Select(requested); return; }
        }
        if (_settings.LastGameId is not null)
        {
            try
            {
                var snapshot = await _source.GetSnapshot(_settings.LastGameId, _lifetime.Token);
                if (snapshot.Game.Status == GameStatus.Live) { await Select(snapshot.Game); return; }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or SourceFormatException or ArgumentException) { }
        }
        if (!_disposed) EnsurePicker(reveal: false);
    }
    private void OnState(SessionState state)
    {
        if (_disposed) return;
        if (!Overlay.Dispatcher.CheckAccess()) { Overlay.Dispatcher.BeginInvoke(() => OnState(state)); return; }
        var generation = _recaps.Generation;
        _recaps.Observe(state);
        _bookmarks.Observe(state);
        if (generation != _recaps.Generation || _shownFeed != state.FeedKey) _recapWindow?.ResetGame(_shownFeed != state.FeedKey);
        _shownFeed = state.FeedKey;
        if (_shownOrigin != state.Origin) { _shownOrigin = state.Origin; _ = UpdateStatisticsDemand(); }
        Overlay.Render(state, _demo);
        UpdateUnread();
        _history?.Update(state, _demo);
        if (_tray is not null) _tray.Text = _demo ? "NBA 直播条 · 演示数据" : state.HasError ? $"NBA 直播条 · {state.Message}" : "NBA 直播条";
    }
    private void OnStructuredState(SessionState state)
    {
        if (_disposed) return;
        if (!Overlay.Dispatcher.CheckAccess()) { Overlay.Dispatcher.BeginInvoke(() => OnStructuredState(state)); return; }
        if (!ReferenceEquals(state, _session.StructuredState)) return;
        Overlay.RenderStructured(state); _statistics?.SetLiveState(state);
    }
    private void OnFeedObserved(SessionState state)
    {
        if (_disposed) return;
        if (!Overlay.Dispatcher.CheckAccess()) { Overlay.Dispatcher.BeginInvoke(() => OnFeedObserved(state)); return; }
        if ((state.SelectedGame?.Id ?? state.Snapshot?.Game.Id) != (_session.StructuredState.SelectedGame?.Id ?? _session.StructuredState.Snapshot?.Game.Id)) return;
        Recaps(state.FeedKey).Observe(state); _bookmarks.Observe(state);
    }
    private async Task Select(GameInfo game)
    {
        await _selection.WaitAsync();
        try
        {
            if (_disposed) return;
            _settings = _settings with { LastGameId = game.Id };
            Save();
            _picker?.Close();
            if (!_allHidden && !Overlay.IsVisible) Overlay.Show();
            _recapTrackers.Clear(); _recapWindow?.ResetGame();
            await Task.WhenAll(_session.Select(game), _statisticsCoordinator.SetGame(game));
        }
        finally { _selection.Release(); }
    }
    private void OpenPicker()
        => EnsurePicker(reveal: true);
    private void EnsurePicker(bool reveal)
    {
        if (reveal) RestoreWindows();
        if (_picker is not null) { if (!_allHidden) Ui.Present(_picker); return; }
        _picker = new(_source, _settings.FavoriteTeam);
        _picker.Selected += async game => await Select(game);
        _picker.Closed += (_, _) => _picker = null;
        if (_allHidden) _hiddenWindows.Add(_picker); else Ui.Present(_picker);
    }
    private void OpenHistory()
    {
        RestoreWindows();
        if (_history is not null) { Ui.Present(_history); return; }
        _history = new(_bookmarks, OpenBookmarks);
        _history.Update(_session.State, _demo);
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }
    private void OpenSettings()
    {
        RestoreWindows();
        if (_settingsWindow is not null) { Ui.Present(_settingsWindow); return; }
        _settingsWindow = new(_settings, string.Join("\n", new[] { _error, _hotkeys?.Error, _store.Error }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
            _webSource?.Transport ?? "演示（无网络）", candidate =>
            {
                if (_hotkeys is null || !_hotkeys.Apply(candidate.HideHotkey, candidate.LockHotkey)) return _hotkeys?.Error ?? "快捷键服务未就绪";
                // Preserve the current match and position if they changed while settings were open.
                ApplyPreferences(candidate with { X = _settings.X, Y = _settings.Y, LastGameId = _settings.LastGameId,
                    FollowedPlayers = _settings.FollowedPlayers, StatisticsPreset = _settings.StatisticsPreset, StatisticsLayouts = _settings.StatisticsLayouts });
                _picker?.SetFavorite(_settings.FavoriteTeam);
                _error = "";
                return _store.Save(_settings) ? null : _store.Error;
            });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
    internal void ShowSettings() => OpenSettings();
    internal void ShowHistory() => OpenHistory();
    internal void ShowBookmarks() => OpenBookmarks();
    private void OpenBookmarks()
    {
        RestoreWindows();
        if (_bookmarksWindow is not null) { Ui.Present(_bookmarksWindow); return; }
        _bookmarksWindow = new(_bookmarks, _demo);
        _bookmarksWindow.Closed += (_, _) => _bookmarksWindow = null;
        _bookmarksWindow.Show();
    }
    internal void ShowStatistics() => OpenStatistics();
    private async void OpenStatistics()
    {
        RestoreWindows();
        if (_statistics is not null) { Ui.Present(_statistics); return; }
        var window = new StatisticsWindow(_statisticsCoordinator, _demo, _settings, ToggleFavorite, (preset, layouts) =>
        {
            _settings = (_settings with { StatisticsPreset = preset, StatisticsLayouts = layouts }).Validate();
            return _store.Save(_settings) ? null : "统计布局已在本次生效，但配置保存失败。";
        });
        _statistics = window;
        window.SetLiveState(_session.StructuredState);
        window.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_statistics, window)) _statistics = null;
            if (!_disposed) await _statisticsCoordinator.SetDemand(StatisticsConsumer.Window, false);
        };
        window.Show();
        await _statisticsCoordinator.SetDemand(StatisticsConsumer.Window, true);
    }
    private void OnStatistics(StatisticsState state)
    {
        if (_disposed) return;
        if (!Overlay.Dispatcher.CheckAccess()) { Overlay.Dispatcher.BeginInvoke(() => OnStatistics(state)); return; }
        if (!ReferenceEquals(state, _statisticsCoordinator.State)) return;
        Overlay.RenderStatistics(state);
    }
    private Task UpdateStatisticsDemand() => _statisticsCoordinator.SetDemand(StatisticsConsumer.PlayerOverlay,
        OverlayFeatures.NeedsStatistics(_settings) && !(_settings.ContentMode == OverlayContent.Text && _session.State.Origin == TextOrigin.Anchor));
    private void ApplyPreferences(AppSettings settings)
    {
        var oldSource = _settings.TextSource;
        _settings = settings.Validate();
        ThemeManager.Apply(_settings.Theme);
        if (oldSource != _settings.TextSource) _ = _session.SetPreference(_settings.TextSource);
        Overlay.ApplyStyle(_settings);
        _statistics?.SetFavorites(_settings.FollowedPlayers);
        UpdateTray();
        _ = UpdateStatisticsDemand();
    }
    private void SetContent(OverlayContent content) { ApplyPreferences(_settings with { ContentMode = content }); Save(); }
    private void ToggleCapsule() { ApplyPreferences(_settings with { CapsuleEnabled = !_settings.CapsuleEnabled }); Save(); }
    private void ToggleFollowedFilter() { ApplyPreferences(_settings with { FollowedPlayersOnly = !_settings.FollowedPlayersOnly }); Save(); }
    private string? ToggleFavorite(FavoritePlayer player)
    {
        var error = PlayerFavorites.Toggle(_settings.FollowedPlayers, player, out var updated);
        if (error is not null) return error;
        ApplyPreferences(_settings with { FollowedPlayers = updated });
        return _store.Save(_settings) ? null : "本次关注已生效，但配置保存失败；退出后可能丢失。";
    }
    private void UpdateTray()
    {
        if (_tray is null) return;
        _tray.CapsuleEnabled = _settings.CapsuleEnabled;
        _tray.ContentMode = _settings.ContentMode;
        _tray.FollowedPlayersOnly = _settings.FollowedPlayersOnly;
        _tray.TextSource = _settings.TextSource;
        _tray.TextDelivery = _settings.TextDelivery;
    }
    private void UpdateUnread() { Overlay.SetUnread(_recaps.UnreadCount); _recapWindow?.SetUnread(_recaps.UnreadCount); }
    internal void ShowRecap() => OpenRecap();
    private void OpenRecap()
    {
        RestoreWindows();
        if (_recapWindow is not null) { Ui.Present(_recapWindow); return; }
        _recapWindow = new(recent => _recaps.Capture(DateTimeOffset.UtcNow, recent), capture =>
        { Recaps(capture.FeedKey).MarkViewed(capture); UpdateUnread(); }, _demo, _bookmarks, OpenBookmarks);
        _recapWindow.Closed += (_, _) => _recapWindow = null;
        _recapWindow.Show();
        UpdateUnread();
    }
    internal void Reveal(bool settings = false)
    {
        if (_disposed) return;
        RestoreWindows();
        if (settings) OpenSettings();
        else if (_picker is not null) Ui.Present(_picker);
    }
    private void ToggleVisibility()
    {
        if (_allHidden) { RestoreWindows(); return; }
        _allHidden = true;
        _hiddenWindows.Clear();
        foreach (var window in Windows().Where(window => window.IsVisible)) _hiddenWindows.Add(window);
        foreach (var window in _hiddenWindows)
        {
            Ui.DismissPopups(window);
            window.Hide();
        }
    }
    private IEnumerable<Window> Windows() => new Window?[] { Overlay, _picker, _statistics, _settingsWindow, _history, _recapWindow, _bookmarksWindow }.OfType<Window>();
    private void RestoreWindows()
    {
        if (_disposed) return;
        _allHidden = false;
        var restore = Windows().Where(window => _hiddenWindows.Contains(window)).ToArray();
        _hiddenWindows.Clear();
        foreach (var window in restore)
        {
            var activate = window.ShowActivated;
            window.ShowActivated = false;
            try { window.Show(); }
            finally { window.ShowActivated = activate; }
        }
        if (!Overlay.IsVisible) Overlay.Show();
        Overlay.Topmost = true;
    }
    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        Overlay.SetClickThrough(_clickThrough);
        if (_tray is not null) _tray.ClickThrough = _clickThrough;
    }
    private void Save() { if (!_store.Save(_settings)) _error = _store.Error ?? ""; }
    internal void ReportError(string text) { _error = text; if (_settingsWindow is null) OpenSettings(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _session.Dispose();
        _statistics?.Close();
        _recapWindow?.Close();
        _bookmarksWindow?.Close();
        _statisticsCoordinator.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _webSource?.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed class DemoSource(bool opening = false, bool finished = false) : IGameSource, IStatisticsSource
{
    private int _tick;
    private readonly List<LiveEntry> _entries = [];
    private GameInfo Game(DateOnly date) => new("0", "湖人", "勇士", 86, 82, finished ? GameStatus.Finished : GameStatus.Live,
        finished ? "已结束" : opening ? "第一节 12:00" : "第三节 04:32", date);
    public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GameInfo>>([Game(date)]);
    public Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tick++;
        if (_entries.Count == 0 && !opening) SeedScores();
        if (finished) return Task.FromResult(new LiveSnapshot(Game(MatchTime.Today), _entries.ToArray(), DateTimeOffset.UtcNow));
        var clock = $"04:{32 - _tick % 30:00}";
        var period = opening ? 1 : 3;
        if ((!opening || _tick >= 4) && (_tick == 1 || _tick % 3 == 1))
        {
            var bench = _tick / 3 % 2 == 1;
            _entries.Add(new($"demo-lineup-away-{_tick}", period, clock, "湖人",
                $"湖人队换人（演示：詹姆斯、演示：戴维斯、演示：里夫斯、演示：范德比尔特、{(bench ? "演示：克内克特" : "演示：八村垒")}）", "86-82", _tick * 10));
            _entries.Add(new($"demo-lineup-home-{_tick}", period, clock, "勇士",
                "勇士队换人（演示：库里、演示：巴特勒、演示：格林、演示：穆迪、演示：波杰姆斯基）", "86-82", _tick * 10 + 1));
        }
        var (eventTeam, text) = (_tick % 4) switch
        {
            0 => ("勇士", "演示：穆迪中投得分"),
            1 => ("湖人", "演示：詹姆斯突破上篮得分"),
            2 => ("勇士", "演示：库里三分不中，戴维斯拿到篮板"),
            _ => ("湖人", "演示：湖人请求暂停")
        };
        _entries.Add(new($"demo-{_tick}", period, clock, eventTeam, text, "86-82", _tick * 10 + 2));
        var game = Game(MatchTime.Today);
        return Task.FromResult(new LiveSnapshot(game, _entries.ToArray(), DateTimeOffset.UtcNow));
    }
    private void SeedScores()
    {
        // Fictional scoring history for --demo only; real mode never synthesizes score records.
        var targets = finished ? new[] { (28, 26), (55, 57), (75, 72), (86, 82) } : new[] { (28, 26), (55, 57), (86, 82) };
        var away = 0; var home = 0; var sequence = -1000;
        for (var index = 0; index < targets.Length; index++)
        {
            var period = index + 1; var target = targets[index];
            void Add(string clock, string text, string team = "")
            {
                _entries.Add(new($"demo-scoring-{sequence}", period, clock, team, "演示：" + text, $"{away}-{home}", sequence++));
            }
            Add("12:00", $"第{period}节开始");
            var startAway = away; var startHome = home;
            var actions = new List<(bool Away, int Points)>();
            while (away < target.Item1 || home < target.Item2)
            {
                if (away < target.Item1) { var points = Math.Min(2 + actions.Count % 2, target.Item1 - away); away += points; actions.Add((true, points)); }
                if (home < target.Item2) { var points = Math.Min(2 + actions.Count % 2, target.Item2 - home); home += points; actions.Add((false, points)); }
            }
            away = startAway; home = startHome;
            var endClock = !finished && period == 3 ? 272 : 0;
            for (var action = 0; action < actions.Count; action++)
            {
                var shot = actions[action]; if (shot.Away) away += shot.Points; else home += shot.Points;
                var remaining = 720 - (int)Math.Round((720 - endClock) * (action + 1d) / actions.Count);
                Add($"{remaining / 60:00}:{remaining % 60:00}", $"{(shot.Away ? "詹姆斯" : "库里")}得{shot.Points}分", shot.Away ? "湖人" : "勇士");
            }
            if (endClock == 0) Add("00:00", $"第{period}节结束");
        }
    }
    public Task<BoxScoreSnapshot> GetBoxScore(string gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = Game(MatchTime.Today);
        var tick = Volatile.Read(ref _tick);
        static PlayerStatistics Player(string id, string name, int points, int rebounds, int assists, bool starter = true) =>
            new(id, name, starter, "F", "28:36", points == 0 ? "0-0" : $"{(points - 4) / 2}-{(points - 4) / 2 + 6}", "0-0", points == 0 ? "0-0" : "4-4", Math.Min(2, rebounds), Math.Max(0, rebounds - 2), rebounds, assists, 2, 1, 2, 1, points, 4)
            { JerseyNumber = id switch { "a1" => "23", "a2" => "3", "a3" => "15", "a4" => "28", "a5" => "2", "a6" => "4", "h1" => "30", "h2" => "10", "h3" => "23", "h4" => "4", "h5" => "2", _ => "0" } };
        var away = new[] { Player("a1", "演示：詹姆斯", 28, 8, 7 + tick / 3), Player("a2", "演示：戴维斯", 26, 12, 3),
            Player("a3", "演示：里夫斯", 18, 4, 5), Player("a4", "演示：八村垒", 14, 5, 1),
            Player("a5", "演示：范德比尔特", 0, 0, 0), Player("a6", "演示：克内克特", 0, 0, 0, false) };
        var home = new[] { Player("h1", "演示：库里", 30, 4, 6), Player("h2", "演示：巴特勒", 22, 6, 5),
            Player("h3", "演示：格林", 12, 9, 7), Player("h4", "演示：穆迪", 18, 3, 2),
            Player("h5", "演示：波杰姆斯基", 0, 0, 0), Player("h6", "演示：佩顿", 0, 0, 0, false) };
        static string Shots(IEnumerable<string> shots) => $"{shots.Sum(v => int.Parse(v.Split('-')[0]))}-{shots.Sum(v => int.Parse(v.Split('-')[1]))}";
        static PlayerStatistics Total(PlayerStatistics[] players) => new("total", "球队合计", false, "", "—", Shots(players.Select(p => p.FieldGoals)), Shots(players.Select(p => p.ThreePointers)), Shots(players.Select(p => p.FreeThrows)),
            players.Sum(p => p.OffensiveRebounds), players.Sum(p => p.DefensiveRebounds), players.Sum(p => p.Rebounds), players.Sum(p => p.Assists),
            players.Sum(p => p.Fouls), players.Sum(p => p.Steals), players.Sum(p => p.Turnovers), players.Sum(p => p.Blocks), players.Sum(p => p.Points), null);
        return Task.FromResult(new BoxScoreSnapshot(game, new(game.Away, away, Total(away)), new(game.Home, home, Total(home)), DateTimeOffset.UtcNow));
    }
}
