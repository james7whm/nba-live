using System.Diagnostics;

namespace NbaLive.Core;

/// <summary>Arrival order is authoritative. Comment IDs and publication clocks are never sorting keys.</summary>
public sealed class AnchorTimeline
{
    private readonly Dictionary<string, LiveEntry> _entries = [];
    private long _order;
    public IReadOnlyList<LiveEntry> History { get; private set; } = [];
    public int Revision { get; private set; }
    public IReadOnlyList<string> Merge(AnchorRoom room, IReadOnlyList<AnchorMessage> messages)
    {
        var added = new List<string>(); var changed = false;
        foreach (var message in messages)
        {
            var key = $"anchor:{room.Key}:{message.Id}";
            var exists = _entries.TryGetValue(key, out var previous);
            var entry = new LiveEntry(key, 0, "", "", message.Text, "", exists ? previous!.Order : ++_order)
            { Origin = TextOrigin.Anchor, Author = message.Author, PublishedAt = message.PublishedAt };
            if (entry == previous) continue;
            _entries[key] = entry; changed = true;
            if (!exists) added.Add(key);
        }
        if (changed) { History = _entries.Values.OrderBy(e => e.Order).ToArray(); Revision++; }
        return added;
    }
}

public sealed class AnchorSession(IAnchorSource source, Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private readonly SemaphoreSlim _selection = new(1, 1);
    private int _epoch;
    private bool _enabled = true;
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SessionState State { get; private set; } = new(null, [], "正在查找主播…", false, true) { Origin = TextOrigin.Anchor, FeedKey = "anchor" };
    public bool HasRoom { get; private set; }
    public GameInfo? MobileGame { get; private set; }
    public DateTimeOffset? ScoreFetchedAt { get; private set; }
    public string ScoreMessage { get; private set; } = "";
    public string Availability { get; private set; } = "正在查找主播";
    public event Action? Changed;
    public Task Completion => _running;
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled) _resume.TrySetResult();
        else _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public async Task Select(GameInfo? game)
    {
        var epoch = Interlocked.Increment(ref _epoch);
        _cts?.Cancel();
        await _selection.WaitAsync();
        try
        {
            if (epoch != _epoch) return;
            await _running;
            _cts?.Dispose(); _cts = new();
            HasRoom = false; MobileGame = null; ScoreFetchedAt = null; ScoreMessage = ""; Availability = "正在查找主播";
            State = new(null, [], "正在查找主播…", false, true) { Origin = TextOrigin.Anchor, FeedKey = "anchor", SelectedGame = game };
            Changed?.Invoke();
            _running = game is null ? Task.CompletedTask : Run(game, _cts.Token);
        }
        finally { _selection.Release(); }
    }
    private static bool Recoverable(Exception ex) => ex is HttpRequestException or OperationCanceledException or SourceFormatException;
    private Task WaitEnabled(CancellationToken ct) => _enabled ? Task.CompletedTask : _resume.Task.WaitAsync(ct);
    private async Task Run(GameInfo selected, CancellationToken ct)
    {
        MobileMatch? match = null; AnchorRoom? room = null; var timeline = new AnchorTimeline();
        string? cursor = null; var initialized = false; var failures = 0; var metadataFailures = 0;
        var nextResolve = DateTimeOffset.MinValue; var nextScore = DateTimeOffset.MinValue; var nextRoom = DateTimeOffset.MinValue;
        DateTimeOffset? finalEmpty = null; DateTimeOffset? lastEvent = null;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp(); var wait = TimeSpan.FromSeconds(1);
            try
            {
                if (!_enabled) await _resume.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (match is null && _now() >= nextResolve)
                {
                    nextResolve = _now().AddSeconds(60);
                    match = await source.Resolve(selected, ct); ct.ThrowIfCancellationRequested();
                    if (match is not null) { MobileGame = match.Game; ScoreFetchedAt = _now(); }
                    Availability = match is null ? "未找到唯一主播比赛映射" : "正在查找主播";
                    Changed?.Invoke();
                }
                if (match is not null && _now() >= nextScore)
                {
                    await WaitEnabled(ct);
                    try
                    {
                        match = await source.GetMatch(match, ct); ct.ThrowIfCancellationRequested();
                        MobileGame = match.Game; ScoreFetchedAt = _now(); ScoreMessage = ""; metadataFailures = 0;
                        nextScore = _now().AddSeconds(5); Changed?.Invoke();
                    }
                    catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
                    {
                        ScoreMessage = ex is SourceFormatException ? "比分数据源异常" : "比分连接中断";
                        nextScore = _now().AddSeconds(new[] { 5, 10, 20, 30 }[Math.Min(metadataFailures++, 3)]); Changed?.Invoke();
                    }
                }
                if (match is not null && _now() >= nextRoom)
                {
                    await WaitEnabled(ct);
                    var resolved = await source.GetRoom(match, ct); ct.ThrowIfCancellationRequested();
                    nextRoom = _now().AddSeconds(resolved is not null && match.Game.Status == GameStatus.Finished ? 5 : 60);
                    // A temporarily unavailable room never replaces an established feed with structured text.
                    if (resolved is not null)
                    {
                        if (room?.Key != resolved.Key)
                        {
                            timeline = new(); cursor = null; initialized = false; finalEmpty = null; lastEvent = null;
                            State = new(null, [], "正在同步主播历史…", false, true)
                            { Origin = TextOrigin.Anchor, FeedKey = "anchor:" + resolved.Key, SourceLabel = "主播 · " + resolved.Name, SelectedGame = match.Game };
                        }
                        room = resolved; HasRoom = true; Availability = "";
                    }
                    else if (room is null) Availability = "本场暂无主播";
                    Changed?.Invoke();
                }
                if (room is not null && match is not null)
                {
                    var added = new List<string>(); var empty = false; var received = false;
                    var budget = Stopwatch.GetTimestamp();
                    do
                    {
                        await WaitEnabled(ct);
                        var page = await source.GetMessages(room, cursor, ct); ct.ThrowIfCancellationRequested();
                        empty = page.Count == 0;
                        if (empty) break;
                        received = true;
                        added.AddRange(timeline.Merge(room, page));
                        var next = page[^1].Id;
                        if (next == cursor) throw new SourceFormatException("主播分页游标未前进");
                        cursor = next;
                        if (_now() >= nextScore)
                        {
                            try
                            {
                                match = await source.GetMatch(match, ct); ct.ThrowIfCancellationRequested();
                                MobileGame = match.Game; ScoreFetchedAt = _now(); ScoreMessage = ""; metadataFailures = 0; nextScore = _now().AddSeconds(5);
                            }
                            catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
                            {
                                ScoreMessage = ex is SourceFormatException ? "比分数据源异常" : "比分连接中断";
                                nextScore = _now().AddSeconds(new[] { 5, 10, 20, 30 }[Math.Min(metadataFailures++, 3)]);
                            }
                        }
                        // Every request releases the shared gate, letting live requests and secondary readers proceed.
                        await Task.Yield();
                    }
                    while (Stopwatch.GetElapsedTime(budget) < TimeSpan.FromSeconds(60));
                    if (empty && !initialized) { initialized = true; added.Clear(); }
                    if (added.Count > 0) lastEvent = _now();
                    var game = MobileGame ?? match.Game;
                    State = new(initialized ? new(game, timeline.History, _now()) : null, initialized ? timeline.History : [],
                        initialized ? (game.Status == GameStatus.Finished ? "比赛已结束" : timeline.History.Count == 0 ? "等待主播消息" : "") : $"正在同步主播历史（已获取 {timeline.History.Count} 条）…", false, !initialized)
                    {
                        Origin = TextOrigin.Anchor, FeedKey = "anchor:" + room.Key, SourceLabel = string.IsNullOrWhiteSpace(room.Name) ? "主播解说" : "主播 · " + room.Name,
                        SelectedGame = game, ScoreFetchedAt = ScoreFetchedAt, ScoreMessage = ScoreMessage, HistoryRevision = timeline.Revision,
                        AddedEntryKeys = added, LastBatchSize = added.Count, LastEventObservedAt = lastEvent, FetchDuration = Stopwatch.GetElapsedTime(started)
                    };
                    Changed?.Invoke();
                    if (initialized && room.Finished && empty)
                    {
                        if (received) finalEmpty = null;
                        if (finalEmpty is { } previous && _now() - previous >= TimeSpan.FromSeconds(5)) return;
                        finalEmpty ??= _now();
                    }
                    else finalEmpty = null;
                    wait = !initialized ? TimeSpan.FromSeconds(1) : room.Finished ? TimeSpan.FromSeconds(5) :
                        game.Status == GameStatus.Live ? room.LiveInterval : room.IdleInterval;
                }
                else wait = TimeSpan.FromSeconds(5);
                failures = 0;
                if (room?.Finished != true)
                    wait = TimeSpan.FromTicks(Math.Max(0, wait.Ticks - Stopwatch.GetElapsedTime(started).Ticks));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (Recoverable(ex))
            {
                var message = ex is SourceFormatException ? "主播数据源异常" : "主播连接中断";
                Availability = message;
                State = State with { Message = message, HasError = true, IsLoading = false, AddedEntryKeys = [], LastBatchSize = 0 };
                Changed?.Invoke();
                wait = TimeSpan.FromSeconds(new[] { 5, 10, 20, 30 }[Math.Min(failures++, 3)]);
                if (match is null) nextResolve = _now() + wait;
                else if (room is null) nextRoom = _now() + wait;
            }
            try { await _delay(wait, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    public async Task StopAsync() { Interlocked.Increment(ref _epoch); _cts?.Cancel(); await _running; }
    public void Dispose() { Interlocked.Increment(ref _epoch); _cts?.Cancel(); }
}

/// <summary>Text selection is separate from structured analytics. Both consumers share one transport.</summary>
public sealed class TextFeedCoordinator : IDisposable
{
    private readonly LiveSession _structured;
    private readonly AnchorSession? _anchor;
    private GameInfo? _game;
    private TextSourcePreference _preference;
    public SessionState State { get; private set; } = new(null, [], "请选择比赛", false, false);
    public SessionState StructuredState => _structured.State;
    public event Action<SessionState>? Changed;
    public event Action<SessionState>? StructuredChanged;
    public event Action<SessionState>? FeedObserved;
    public TextFeedCoordinator(IGameSource source, IAnchorSource? anchor, TextSourcePreference preference)
    {
        _preference = preference; _structured = new(source);
        _structured.Changed += state => { StructuredChanged?.Invoke(state); FeedObserved?.Invoke(state); Publish(); };
        if (anchor is not null)
        {
            _anchor = new(anchor);
            _anchor.Changed += () => { if (_anchor.HasRoom) FeedObserved?.Invoke(_anchor.State); Publish(); };
        }
    }
    public async Task Select(GameInfo game)
    {
        _game = game;
        // Cancel the old mobile session before exposing any newly selected structured snapshot.
        if (_anchor is not null) { _anchor.SetEnabled(_preference == TextSourcePreference.AnchorPreferred); await _anchor.Select(null); }
        await _structured.Select(game);
        if (_anchor is not null && _preference == TextSourcePreference.AnchorPreferred) await _anchor.Select(game);
    }
    public async Task SetPreference(TextSourcePreference preference)
    {
        if (_preference == preference) return;
        _preference = preference;
        if (_anchor is not null)
        {
            _anchor.SetEnabled(preference == TextSourcePreference.AnchorPreferred);
            if (preference == TextSourcePreference.AnchorPreferred && _game is not null && _anchor.State.SelectedGame?.Id != _game.Id)
                await _anchor.Select(_game);
        }
        Publish();
    }
    private void Publish()
    {
        var useAnchor = _preference == TextSourcePreference.AnchorPreferred && _anchor?.HasRoom == true;
        var state = useAnchor ? _anchor!.State : _structured.State;
        if (_preference == TextSourcePreference.AnchorPreferred && _anchor is not null)
        {
            if (_anchor.MobileGame is { } game && game.Id == (state.SelectedGame?.Id ?? state.Snapshot?.Game.Id))
                state = state with { SelectedGame = game, Snapshot = state.Snapshot is { } snapshot ? snapshot with { Game = game } : null, ScoreFetchedAt = _anchor.ScoreFetchedAt, ScoreMessage = _anchor.ScoreMessage };
            if (!useAnchor) state = state with { SourceLabel = "赛况实录 · " + _anchor.Availability };
        }
        State = state;
        Changed?.Invoke(state);
    }
    public async Task StopAsync() { if (_anchor is not null) await _anchor.StopAsync(); await _structured.StopAsync(); }
    public void Dispose() { _anchor?.Dispose(); _structured.Dispose(); }
}
