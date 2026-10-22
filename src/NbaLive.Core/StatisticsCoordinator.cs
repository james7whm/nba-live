namespace NbaLive.Core;

public enum StatisticsConsumer { Window, PlayerOverlay }

/// <summary>One statistics subscription, shared by all views; desired state wins over queued changes.</summary>
public sealed class StatisticsCoordinator : IDisposable
{
    private readonly StatisticsSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<StatisticsConsumer> _consumers = [];
    private readonly StatisticsChangeTracker _changes = new();
    private GameInfo? _game;
    private long _epoch;
    private long? _activeEpoch;
    private long? _acceptingEpoch;
    private bool _disposed;
    public StatisticsState State { get; private set; } = new(null, null, "请选择比赛后查看球员统计", false, false);
    public event Action<StatisticsState>? Changed;
    public StatisticsCoordinator(IStatisticsSource source, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _session = new(source, delay);
        _session.Changed += Accept;
    }
    public Task SetGame(GameInfo game)
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _game = game; _epoch++; _acceptingEpoch = null;
            _changes.Reset();
            State = new(game, null, "等待球员统计", false, false);
        }
        Changed?.Invoke(State);
        return Reconcile();
    }
    public Task SetDemand(StatisticsConsumer consumer, bool enabled)
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (enabled) _consumers.Add(consumer); else _consumers.Remove(consumer);
        }
        return Reconcile();
    }
    public Task Refresh()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _epoch++; _acceptingEpoch = null;
        }
        return Reconcile();
    }
    private async Task Reconcile()
    {
        await _gate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_disposed || (_consumers.Count > 0 && _game is not null && _activeEpoch == _epoch)) return;
                _acceptingEpoch = null;
            }
            await _session.StopAsync();
            GameInfo? game;
            bool keep;
            lock (_sync)
            {
                _activeEpoch = null;
                if (_disposed || _consumers.Count == 0 || _game is null) return;
                game = _game;
                keep = State.Snapshot?.Game.Id == game.Id;
                _acceptingEpoch = _activeEpoch = _epoch;
            }
            await _session.Select(game, keep);
        }
        finally { _gate.Release(); }
    }
    private void Accept(StatisticsState state)
    {
        lock (_sync)
        {
            if (_disposed || _acceptingEpoch != _epoch || state.SelectedGame?.Id != _game?.Id) return;
            State = state = _changes.Observe(state);
        }
        Changed?.Invoke(state);
    }
    public void Dispose()
    {
        lock (_sync) { _disposed = true; _acceptingEpoch = null; }
        _session.Changed -= Accept;
        _session.Dispose();
    }
}
