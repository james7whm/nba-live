namespace NbaLive.Core;

/// <summary>First load is a baseline; later arrivals are shown in readable pages instead of being skipped.</summary>
public sealed class TextPlayback
{
    private TextDelivery _delivery = TextDelivery.Paged;
    public TextDelivery Delivery
    {
        get => _delivery;
        set { if (_delivery != value) { _delivery = value; JumpToLatest(); } }
    }
    public static readonly TimeSpan PageDuration = TimeSpan.FromSeconds(4);
    private readonly Dictionary<string, LiveEntry> _entries = [];
    private readonly List<string> _pending = [];
    private IReadOnlyList<LiveEntry> _history = [];
    private IReadOnlyList<string> _baseline = [];
    private IReadOnlyList<LiveEntry> _latestPage = [];
    private string? _gameId;
    private bool _initialized, _viewInitialized;
    private DateTimeOffset _nextPageAt;
    private DateTimeOffset? _suspendedAt;
    public IReadOnlyList<LiveEntry> Visible { get; private set; } = [];
    public int PendingCount => _pending.Count;
    public bool IsReplaying => PendingCount > 0 || _viewInitialized && !Visible.Select(e => e.Key).SequenceEqual(_latestPage.Select(e => e.Key));
    public void Observe(SessionState state)
    {
        var gameId = (state.SelectedGame?.Id ?? state.Snapshot?.Game.Id) + "|" + state.FeedKey;
        if (gameId != _gameId || state.IsLoading && state.Snapshot is null)
        {
            _gameId = gameId; _entries.Clear(); _pending.Clear(); _baseline = []; _history = []; _latestPage = [];
            Visible = []; _initialized = _viewInitialized = false; _nextPageAt = default; _suspendedAt = null;
        }
        if (ReferenceEquals(_history, state.History) && (_initialized || state.Snapshot is null)) return;
        _history = state.History;
        foreach (var entry in state.History)
        {
            if (_initialized && !_entries.ContainsKey(entry.Key)) _pending.Add(entry.Key);
            _entries[entry.Key] = entry;
        }
        if (!_initialized && state.Snapshot is not null)
        {
            _initialized = true;
            _baseline = state.History.Select(e => e.Key).ToArray();
        }
    }
    public void JumpToLatest()
    {
        _pending.Clear(); _baseline = _history.Select(e => e.Key).ToArray(); _viewInitialized = false;
    }
    public void Suspend(DateTimeOffset now) => _suspendedAt ??= now;
    public void Refresh(int count, Func<LiveEntry, bool> matches, bool filterReady, bool canAdvance, DateTimeOffset now)
    {
        if (!filterReady || count <= 0) { Suspend(now); return; }
        _pending.RemoveAll(key => !_entries.TryGetValue(key, out var entry) || !matches(entry));
        _latestPage = _history.Where(matches).TakeLast(count).Reverse().ToArray();
        if (!canAdvance) { Suspend(now); return; }
        if (Delivery == TextDelivery.Latest)
        {
            Visible = _latestPage; _pending.Clear(); _baseline = _history.Select(e => e.Key).ToArray();
            _viewInitialized = _initialized; _suspendedAt = null; return;
        }
        if (_suspendedAt is { } suspended && _viewInitialized && now > suspended) _nextPageAt += now - suspended;
        _suspendedAt = null;
        if (!_viewInitialized)
        {
            Visible = _baseline.Where(_entries.ContainsKey).Select(key => _entries[key]).Where(matches).TakeLast(count).Reverse().ToArray();
            _viewInitialized = _initialized;
            _nextPageAt = now + PageDuration;
        }
        else Visible = Visible.Where(e => _entries.ContainsKey(e.Key)).Select(e => _entries[e.Key]).Take(count).ToArray();
        if (Visible.Count > 0 && now < _nextPageAt) return;
        if (_pending.Count > 0)
        {
            var keys = _pending.Take(count).ToArray(); _pending.RemoveRange(0, keys.Length);
            Visible = keys.Select(key => _entries[key]).Reverse().ToArray();
            _nextPageAt = now + PageDuration;
        }
        else if (IsReplaying)
        {
            Visible = _latestPage; _nextPageAt = now + PageDuration;
        }
    }
}
