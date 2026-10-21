namespace NbaLive.Core;

public sealed record RecapSnapshot(long Generation, long Through, GameInfo? Game, string Heading,
    string Connection, IReadOnlyList<LiveEntry> Highlights, IReadOnlyList<LiveEntry> Entries, DateTimeOffset CreatedAt, bool HasSnapshot);

/// <summary>Arrival order is independent of the source's event chronology, so late events remain unread.</summary>
public sealed class RecapTracker
{
    private readonly Dictionary<string, (long Sequence, LiveEntry Entry)> _entries = [];
    private long _sequence;
    private long _readThrough;
    private long _initialThrough;
    private long _generation;
    private long _lastViewedThrough;
    private readonly HashSet<long> _readSequences = [];
    private bool _initialized;
    private bool _viewed;
    private string? _gameId;
    private GameInfo? _readGame;
    private SessionState? _latest;
    private IReadOnlyList<LiveEntry>? _observedHistory;
    public int UnreadCount => _entries.Values.Count(e => e.Sequence > (_viewed ? _readThrough : _initialThrough) && !_readSequences.Contains(e.Sequence));
    public long Generation => _generation;
    public void Reset(string? gameId)
    {
        _entries.Clear(); _sequence = _readThrough = _initialThrough = 0; _generation++;
        _readSequences.Clear(); _lastViewedThrough = 0;
        _initialized = _viewed = false; _readGame = null; _latest = null; _observedHistory = null; _gameId = gameId;
    }
    public void Observe(SessionState state)
    {
        var id = state.SelectedGame?.Id ?? state.Snapshot?.Game.Id;
        if (id != _gameId || (state.IsLoading && state.Snapshot is null)) Reset(id);
        _latest = state;
        if (state.Snapshot is null) return;
        if (ReferenceEquals(_observedHistory, state.History)) return;
        _observedHistory = state.History;
        foreach (var entry in state.History)
            _entries[entry.Key] = (_entries.TryGetValue(entry.Key, out var existing) ? existing.Sequence : ++_sequence, entry);
        if (!_initialized) { _initialThrough = _sequence; _initialized = true; }
    }
    public RecapSnapshot Capture(DateTimeOffset now, bool recent = false)
    {
        var all = _entries.Values.OrderBy(e => e.Entry.Order).ThenBy(e => e.Sequence).ToArray();
        var entries = (recent || !_viewed ? all.TakeLast(10) : all.Where(e => e.Sequence > _readThrough && !_readSequences.Contains(e.Sequence)))
            .Select(e => e.Entry).ToArray();
        var game = _latest?.Snapshot?.Game ?? _latest?.SelectedGame;
        var heading = recent || !_viewed ? $"最近 {entries.Length} 条已获取事件" : $"上次回顾后收到 {entries.Length} 条新事件";
        if (_viewed && !recent && _readGame is { AwayScore: not null, HomeScore: not null } before &&
            game is { AwayScore: not null, HomeScore: not null } after)
            heading += $"\n比分前后对照：{after.Away} {before.AwayScore} → {after.AwayScore}；{after.Home} {before.HomeScore} → {after.HomeScore}（可能包含来源修正）";
        var connection = _latest is null ? "请选择比赛" : SessionStatus.Describe(_latest, now);
        return new(_generation, _sequence, game, heading, connection,
            entries.Where(EventSelection.IsImportant).TakeLast(5).ToArray(), entries, now, _latest?.Snapshot is not null);
    }
    public void MarkViewed(RecapSnapshot snapshot)
    {
        if (snapshot.Generation != _generation) return;
        // Do not consume an empty pre-fetch preview or let an older open window rewind the boundary.
        if (!snapshot.HasSnapshot || !_initialized || (_viewed && snapshot.Through < _readThrough)) return;
        // The first recent-ten preview establishes the session's review boundary.
        // Later explicit recent-ten previews only consume the entries actually shown.
        if (!_viewed) _readThrough = snapshot.Through;
        _viewed = true;
        foreach (var entry in snapshot.Entries)
            if (_entries.TryGetValue(entry.Key, out var item) && item.Sequence <= snapshot.Through && item.Sequence > _readThrough)
                _readSequences.Add(item.Sequence);
        while (_readSequences.Remove(_readThrough + 1)) _readThrough++;
        if (snapshot.Through >= _lastViewedThrough) { _lastViewedThrough = snapshot.Through; _readGame = snapshot.Game; }
    }
}
