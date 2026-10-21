namespace NbaLive.Core;

public sealed record SessionState(LiveSnapshot? Snapshot, IReadOnlyList<LiveEntry> History,
    string Message, bool HasError, bool IsLoading)
{
    public GameInfo? SelectedGame { get; init; }
    public DateTimeOffset? LastEventObservedAt { get; init; }
    public int HistoryRevision { get; init; }
    public IReadOnlyList<string> AddedEntryKeys { get; init; } = [];
}

public sealed class LiveSession(IGameSource source,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Dictionary<string, LiveEntry> _history = [];
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private IReadOnlyList<LiveEntry> _orderedHistory = [];
    private int _revision;
    private DateTimeOffset? _lastEventObservedAt;
    public SessionState State { get; private set; } = new(null, [], "请从托盘或选赛按钮选择比赛", false, false);
    public event Action<SessionState>? Changed;

    public async Task Select(GameInfo game)
    {
        Stop();
        await _running;
        _cts?.Dispose();
        _cts = new();
        _history.Clear();
        _orderedHistory = [];
        _revision = 0;
        _lastEventObservedAt = null;
        Publish(new(null, [], "正在连接数据源…", false, true) { SelectedGame = game });
        _running = Run(game.Id, _cts.Token);
    }

    private async Task Run(string gameId, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var snapshot = await source.GetSnapshot(gameId, ct);
                var addedKeys = new List<string>();
                var contentChanged = false;
                foreach (var entry in snapshot.Entries)
                {
                    var key = entry.Key;
                    if (!_history.TryGetValue(key, out var previous)) addedKeys.Add(key);
                    else if (entry == previous) continue;
                    _history[key] = entry;
                    contentChanged = true;
                }
                if (contentChanged)
                {
                    _orderedHistory = _history.Values.OrderBy(e => e.Order).ToArray();
                    _revision++;
                }
                if (addedKeys.Count > 0) _lastEventObservedAt = snapshot.FetchedAt;
                Publish(new(snapshot, _orderedHistory, snapshot.Game.Status == GameStatus.Finished ? "比赛已结束" :
                    snapshot.Entries.Count == 0 ? "等待文字直播" : "", false, false)
                {
                    SelectedGame = snapshot.Game,
                    LastEventObservedAt = _lastEventObservedAt,
                    HistoryRevision = _revision,
                    AddedEntryKeys = addedKeys
                });
                failures = 0;
                if (snapshot.Game.Status is GameStatus.Finished or GameStatus.Cancelled) return;
                wait = PollingPolicy.Next(snapshot.Game, snapshot.FetchedAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SourceFormatException)
            {
                var text = ex is SourceFormatException ? "数据源异常" : "连接中断";
                Publish(State with { Message = text, HasError = true, IsLoading = false, AddedEntryKeys = [] });
                wait = TimeSpan.FromSeconds(new[] { 5, 10, 20, 30 }[Math.Min(failures++, 3)]);
            }
            try { await _delay(wait, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private void Publish(SessionState state) { State = state; Changed?.Invoke(state); }
    public void Stop() => _cts?.Cancel();
    public async Task StopAsync() { Stop(); await _running; }
    public void Dispose() { Stop(); _cts?.Dispose(); }
}
