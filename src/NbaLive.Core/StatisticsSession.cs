namespace NbaLive.Core;

public sealed record StatisticsState(GameInfo? SelectedGame, BoxScoreSnapshot? Snapshot, string Message, bool HasError, bool IsLoading);

/// <summary>Exists only while the statistics window is open; shares the source's request gate.</summary>
public sealed class StatisticsSession(IStatisticsSource source,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private readonly SemaphoreSlim _selection = new(1, 1);
    private bool _disposed;
    public StatisticsState State { get; private set; } = new(null, null, "请选择比赛后查看球员统计", false, false);
    public event Action<StatisticsState>? Changed;

    public async Task Select(GameInfo game, bool keepPrevious = false)
    {
        await _selection.WaitAsync();
        try
        {
            if (_disposed) return;
            _cts?.Cancel();
            await _running;
            if (_disposed) return;
            _cts?.Dispose();
            _cts = new();
            var previous = keepPrevious && State.SelectedGame?.Id == game.Id ? State.Snapshot : null;
            Publish(new(game, previous, previous is null ? "正在读取球员统计…" : "正在刷新球员统计…", false, true));
            _running = Run(game.Id, _cts.Token);
        }
        finally { _selection.Release(); }
    }
    private async Task Run(string gameId, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var snapshot = await source.GetBoxScore(gameId, ct);
                ct.ThrowIfCancellationRequested();
                if (snapshot.Game.Id != gameId) throw new SourceFormatException("统计场次与选择不符。");
                var ended = snapshot.Game.Status is GameStatus.Finished or GameStatus.Cancelled;
                var message = snapshot.HasPlayerData ? ended ? "比赛已结束 · 自动刷新已停止" : "每 15 秒自动刷新" :
                    ended ? "本场暂无球员统计 · 自动刷新已停止" : "等待球员统计发布";
                Publish(new(snapshot.Game, snapshot, message, false, false));
                failures = 0;
                if (ended) return;
                wait = snapshot.Game.Status == GameStatus.Scheduled ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SourceFormatException)
            {
                Publish(State with { Message = ex is SourceFormatException ? "统计数据源异常" : "统计连接中断", HasError = true, IsLoading = false });
                wait = TimeSpan.FromSeconds(new[] { 5, 10, 20, 30 }[Math.Min(failures++, 3)]);
            }
            try { await _delay(wait, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private void Publish(StatisticsState state) { State = state; Changed?.Invoke(state); }
    public async Task StopAsync() { _cts?.Cancel(); await _running; }
    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        // Dispose after the outstanding operation has observed cancellation.
        var cts = _cts;
        _ = _running.ContinueWith(_ => cts?.Dispose(), TaskScheduler.Default);
    }
}
