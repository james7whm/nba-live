namespace NbaLive.Core;

public sealed record PlayerDetailState(PlayerDetailQuery? Query, PlayerGameDetailSnapshot? Snapshot, string Message, bool HasError, bool IsLoading)
{
    public byte[]? Portrait { get; init; }
}

/// <summary>Only the visible, selected player requests detail. Superseded responses never enter the UI.</summary>
public sealed class PlayerDetailCoordinator(IPlayerGameDetailSource source, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private long _generation;
    private bool _visible, _disposed;
    public PlayerDetailState State { get; private set; } = new(null, null, "点击球员查看本场详情", false, false);
    public event Action<PlayerDetailState>? Changed;
    public void Select(PlayerDetailQuery query)
    {
        if (_disposed) return;
        var same = State.Query?.Key == query.Key;
        Cancel(); _visible = true;
        Publish(new(query, same ? State.Snapshot : null, "正在读取本场球员详情…", false, true) { Portrait = same ? State.Portrait : null });
        Start();
    }
    public void UpdateGame(GameInfo game)
    {
        if (State.Query is not { } query) return;
        if (query.Game.Id != game.Id) { Close(); return; }
        var changed = query.Game.Status != game.Status;
        State = State with { Query = query with { Game = game } };
        if (changed && _visible) Refresh();
    }
    public void UpdatePlayer(PlayerDetailQuery query)
    {
        if (State.Query?.Key == query.Key) State = State with { Query = query };
    }
    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (visible) { if (State.Query is not null) Refresh(); }
        else { Cancel(); Publish(State with { IsLoading = false }); }
    }
    public void Refresh()
    {
        if (_disposed || !_visible || State.Query is null) return;
        Cancel(); Publish(State with { IsLoading = true, HasError = false, Message = "正在刷新本场球员详情…" }); Start();
    }
    public void Close()
    {
        Cancel(); _visible = false; Publish(new(null, null, "点击球员查看本场详情", false, false));
    }
    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        var cts = _cts; _cts = null;
        if (cts is null) return;
        cts.Cancel(); _ = _running.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }
    private void Start()
    {
        if (_disposed || !_visible || State.Query is null) return;
        _cts = new(); var generation = Volatile.Read(ref _generation); _running = Run(generation, _cts.Token);
    }
    private bool Current(long generation, CancellationToken ct) => !_disposed && !ct.IsCancellationRequested && generation == Volatile.Read(ref _generation);
    private async Task Run(long generation, CancellationToken ct)
    {
        var failures = 0; var portraitAttempted = State.Portrait is not null;
        while (Current(generation, ct) && State.Query is { } query)
        {
            TimeSpan wait;
            try
            {
                var snapshot = await source.GetDetail(query, ct);
                if (!Current(generation, ct)) return;
                if (snapshot.Query.Key != query.Key) throw new SourceFormatException("球员详情与选择不符");
                Publish(new(State.Query, snapshot, query.Game.Status is GameStatus.Finished or GameStatus.Cancelled ? "比赛已结束 · 自动刷新已停止" : "每 15 秒刷新详情", false, false) { Portrait = State.Portrait });
                if (!portraitAttempted && snapshot.Portrait is { } uri && source is IPlayerPortraitSource images)
                {
                    portraitAttempted = true;
                    try
                    {
                        var bytes = await images.GetPortrait(uri, ct);
                        if (!Current(generation, ct)) return;
                        Publish(State with { Portrait = bytes });
                    }
                    catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException) { }
                }
                if (!Current(generation, ct)) return;
                failures = 0;
                if (State.Query?.Game.Status is GameStatus.Finished or GameStatus.Cancelled) return;
                wait = TimeSpan.FromSeconds(State.Query?.Game.Status == GameStatus.Scheduled ? 30 : 15);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or SourceFormatException or OperationCanceledException)
            {
                if (!Current(generation, ct)) return;
                Publish(State with { Message = ex is SourceFormatException ? "详情数据源异常 · " + ex.Message : "详情连接中断，稍后重试", HasError = true, IsLoading = false });
                if (ex is SourceFormatException && State.Query?.Game.Status is GameStatus.Finished or GameStatus.Cancelled) return;
                wait = TimeSpan.FromSeconds(new[] { 5, 10, 20, 30 }[Math.Min(failures++, 3)]);
            }
            try { await _delay(wait, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private void Publish(PlayerDetailState state) { if (_disposed) return; State = state; Changed?.Invoke(state); }
    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); }
}
