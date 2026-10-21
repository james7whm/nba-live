using System.Windows.Interop;

namespace NbaLive;

internal sealed class AppController : IDisposable
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private readonly WebGameSource? _webSource;
    private readonly IGameSource _source;
    private readonly LiveSession _session;
    private readonly StatisticsCoordinator _statisticsCoordinator;
    private readonly RecapTracker _recaps = new();
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
    private bool _clickThrough;
    private bool _disposed;
    private string _error = "";
    internal OverlayWindow Overlay { get; }

    internal AppController(bool demo)
    {
        _demo = demo;
        _store = new SettingsStore(demo ? Path.Combine(Path.GetTempPath(), "NbaLive-Demo") : null);
        _settings = _store.Load();
        _error = _store.Error ?? "";
        _source = demo ? new DemoSource() : _webSource = new WebGameSource();
        _session = new LiveSession(_source);
        _statisticsCoordinator = new((IStatisticsSource)_source);
        Overlay = new(_settings);
        Overlay.ChooseRequested += OpenPicker;
        Overlay.HistoryRequested += OpenHistory;
        Overlay.StatisticsRequested += OpenStatistics;
        Overlay.SettingsRequested += OpenSettings;
        Overlay.HideRequested += ToggleVisibility;
        Overlay.RecapRequested += OpenRecap;
        Overlay.CapsuleRequested += ToggleCapsule;
        Overlay.ContentRequested += SetContent;
        Overlay.ContextMenuRequested += () => _tray?.ShowMenu();
        Overlay.HotkeyPressed += id => { if (id == 1) ToggleVisibility(); else if (id == 2) ToggleClickThrough(); };
        Overlay.PositionSaved += (x, y) => { _settings = _settings with { X = x, Y = y }; Save(); };
        _session.Changed += OnState;
        _statisticsCoordinator.Changed += OnStatistics;
    }

    internal async Task Start()
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
                case 8: Overlay.Reposition(null, null); if (!Overlay.IsVisible) Overlay.Show(); break;
                case 9: OpenStatistics(); break;
                case 10: OpenRecap(); break;
                case 11: ToggleCapsule(); break;
                case 12: SetContent(OverlayContent.Text); break;
                case 13: SetContent(OverlayContent.Players); break;
            }
        };
        _hotkeys = new(new WindowInteropHelper(Overlay).Handle);
        if (!_hotkeys.Apply(_settings.HideHotkey, _settings.LockHotkey)) _error = _hotkeys.Error;
        await UpdateStatisticsDemand();
        if (_demo)
        {
            var games = await _source.GetGames(MatchTime.Today, _lifetime.Token);
            await Select(games[0]);
            return;
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
        if (!_disposed) OpenPicker();
    }
    private void OnState(SessionState state)
    {
        if (_disposed) return;
        if (!Overlay.Dispatcher.CheckAccess()) { Overlay.Dispatcher.BeginInvoke(() => OnState(state)); return; }
        var generation = _recaps.Generation;
        _recaps.Observe(state);
        if (generation != _recaps.Generation) _recapWindow?.ResetGame();
        Overlay.Render(state, _demo);
        UpdateUnread();
        _history?.Update(state, _demo);
        if (_tray is not null) _tray.Text = _demo ? "NBA 直播条 · 演示数据" : state.HasError ? $"NBA 直播条 · {state.Message}" : "NBA 直播条";
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
            if (!Overlay.IsVisible) Overlay.Show();
            await Task.WhenAll(_session.Select(game), _statisticsCoordinator.SetGame(game));
        }
        finally { _selection.Release(); }
    }
    private void OpenPicker()
    {
        if (_picker is not null) { _picker.Activate(); return; }
        _picker = new(_source, _settings.FavoriteTeam);
        _picker.Selected += async game => await Select(game);
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show();
    }
    private void OpenHistory()
    {
        if (_history is not null) { _history.Activate(); return; }
        _history = new();
        _history.Update(_session.State, _demo);
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }
    private void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new(_settings, string.Join("\n", new[] { _error, _hotkeys?.Error, _store.Error }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
            _webSource?.Transport ?? "演示（无网络）", candidate =>
            {
                if (_hotkeys is null || !_hotkeys.Apply(candidate.HideHotkey, candidate.LockHotkey)) return _hotkeys?.Error ?? "快捷键服务未就绪";
                // Preserve the current match and position if they changed while settings were open.
                ApplyPreferences(candidate with { X = _settings.X, Y = _settings.Y, LastGameId = _settings.LastGameId,
                    FollowedPlayers = _settings.FollowedPlayers });
                _picker?.SetFavorite(_settings.FavoriteTeam);
                _error = "";
                return _store.Save(_settings) ? null : _store.Error;
            });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
    internal void ShowSettings() => OpenSettings();
    internal void ShowStatistics() => OpenStatistics();
    private async void OpenStatistics()
    {
        if (_statistics is not null) { _statistics.Activate(); return; }
        var window = new StatisticsWindow(_statisticsCoordinator, _demo, _settings.FollowedPlayers, ToggleFavorite);
        _statistics = window;
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
        _settings.ContentMode == OverlayContent.Players && _settings.FollowedPlayers.Length > 0);
    private void ApplyPreferences(AppSettings settings)
    {
        _settings = settings.Validate();
        Overlay.ApplyStyle(_settings);
        _statistics?.SetFavorites(_settings.FollowedPlayers);
        UpdateTray();
        _ = UpdateStatisticsDemand();
    }
    private void SetContent(OverlayContent content) { ApplyPreferences(_settings with { ContentMode = content }); Save(); }
    private void ToggleCapsule() { ApplyPreferences(_settings with { CapsuleEnabled = !_settings.CapsuleEnabled }); Save(); }
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
        _tray.PlayerContent = _settings.ContentMode == OverlayContent.Players;
    }
    private void UpdateUnread() { Overlay.SetUnread(_recaps.UnreadCount); _recapWindow?.SetUnread(_recaps.UnreadCount); }
    internal void ShowRecap() => OpenRecap();
    private void OpenRecap()
    {
        if (_recapWindow is not null) { _recapWindow.Activate(); return; }
        _recapWindow = new(recent => _recaps.Capture(DateTimeOffset.UtcNow, recent), capture =>
        { _recaps.MarkViewed(capture); UpdateUnread(); }, _demo);
        _recapWindow.Closed += (_, _) => _recapWindow = null;
        _recapWindow.Show();
        UpdateUnread();
    }
    internal void Reveal(bool settings = false)
    {
        if (_disposed) return;
        if (!Overlay.IsVisible) Overlay.Show();
        Overlay.Topmost = true;
        if (settings) OpenSettings();
        else if (_picker is not null) _picker.Activate();
    }
    private void ToggleVisibility()
    {
        if (Overlay.IsVisible) Overlay.Hide(); else { Overlay.Show(); Overlay.Topmost = true; }
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
        _statisticsCoordinator.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _webSource?.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed class DemoSource : IGameSource, IStatisticsSource
{
    private int _tick;
    private readonly List<LiveEntry> _entries = [];
    public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GameInfo>>([new("0", "湖人", "勇士", 86, 82, GameStatus.Live, "演示 · 第三节 04:32", date)]);
    public Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tick++;
        _entries.Add(new($"demo-{_tick}", 3, $"04:{32 - _tick % 30:00}", "湖人", _tick % 2 == 0 ? "演示：库里三分不中，戴维斯拿到篮板" : "演示：詹姆斯突破上篮得分", "86-82", _tick));
        var game = new GameInfo("0", "湖人", "勇士", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
        return Task.FromResult(new LiveSnapshot(game, _entries.ToArray(), DateTimeOffset.UtcNow));
    }
    public Task<BoxScoreSnapshot> GetBoxScore(string gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = new GameInfo("0", "湖人", "勇士", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
        var tick = Volatile.Read(ref _tick);
        static PlayerStatistics Player(string id, string name, int points, int rebounds, int assists, bool starter = true) =>
            new(id, name, starter, "F", "28:36", $"{(points - 4) / 2}-{(points - 4) / 2 + 6}", "0-2", "4-4", 2, rebounds - 2, rebounds, assists, 2, 1, 2, 1, points, 4);
        var away = new[] { Player("a1", "演示：詹姆斯", 28, 8, 7 + tick / 3), Player("a2", "演示：戴维斯", 26, 12, 3),
            Player("a3", "演示：里夫斯", 18, 4, 5), Player("a4", "演示：八村垒", 14, 5, 1, false) };
        var home = new[] { Player("h1", "演示：库里", 30, 4, 6), Player("h2", "演示：巴特勒", 22, 6, 5),
            Player("h3", "演示：格林", 12, 9, 7), Player("h4", "演示：穆迪", 18, 3, 2, false) };
        static PlayerStatistics Total(PlayerStatistics[] players) => new("total", "球队合计", false, "", "—", $"{players.Sum(p => (p.Points - 4) / 2)}-{players.Sum(p => (p.Points - 4) / 2 + 6)}", "0-8", "16-16",
            players.Sum(p => p.OffensiveRebounds), players.Sum(p => p.DefensiveRebounds), players.Sum(p => p.Rebounds), players.Sum(p => p.Assists),
            players.Sum(p => p.Fouls), players.Sum(p => p.Steals), players.Sum(p => p.Turnovers), players.Sum(p => p.Blocks), players.Sum(p => p.Points), null);
        return Task.FromResult(new BoxScoreSnapshot(game, new(game.Away, away, Total(away)), new(game.Home, home, Total(home)), DateTimeOffset.UtcNow));
    }
}
