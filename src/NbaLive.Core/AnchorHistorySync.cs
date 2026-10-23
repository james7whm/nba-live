namespace NbaLive.Core;

/// <summary>Live rooms page backwards; archived rooms may page forwards. IDs are opaque identities.</summary>
public sealed class AnchorHistorySync(AnchorRoom room)
{
    private readonly AnchorTimeline _timeline = new();
    private readonly HashSet<string> _known = [];
    private readonly HashSet<string> _cursors = [];
    private readonly List<AnchorMessage> _pending = [];
    private readonly Dictionary<string, int> _pendingIndex = [];
    private bool? _forward;
    public string? Cursor { get; private set; }
    public bool IsInitialized { get; private set; }
    public int BufferedCount => _pending.Count;
    public IReadOnlyList<LiveEntry> History => _timeline.History;
    public int Revision => _timeline.Revision;
    public IReadOnlyList<string> Added { get; private set; } = [];
    public bool ReceivedNew { get; private set; }

    // Keep the unfinished page chain through timeouts. Only a complete chain is published,
    // so an interruption cannot reverse batches or turn startup history into unread events.
    public bool Accept(IReadOnlyList<AnchorMessage> page)
    {
        var direction = Forward(page) ?? _forward ?? false;
        if (_forward is { } previous && previous != direction)
        {
            // A room can switch protocol at the final buzzer. Restart the unfinished chain,
            // retaining committed history, to avoid combining pages from opposite directions.
            _forward = direction; Cursor = null; _cursors.Clear(); _pending.Clear(); _pendingIndex.Clear();
            return false;
        }
        _forward = direction;
        var boundary = !direction && IsInitialized && page.Any(message => _known.Contains(message.Id));
        if (page.Count > 0 && !boundary && (_cursors.Contains(page[^1].Id) || page[^1].Id == Cursor))
            throw new SourceFormatException("主播分页游标未前进");
        foreach (var message in page)
        {
            if (_pendingIndex.TryGetValue(message.Id, out var index)) _pending[index] = message;
            else { _pendingIndex[message.Id] = _pending.Count; _pending.Add(message); }
        }
        if (page.Count > 0 && !boundary)
        {
            Cursor = page[^1].Id; _cursors.Add(Cursor);
            return false;
        }

        var added = _timeline.Merge(room, (direction ? _pending : _pending.AsEnumerable().Reverse()).ToArray());
        ReceivedNew = added.Count > 0;
        Added = IsInitialized ? added : [];
        foreach (var message in _pending) _known.Add(message.Id);
        IsInitialized = true;
        _pending.Clear(); _pendingIndex.Clear(); _cursors.Clear();
        // Backward pagination must poll the latest head, never the exhausted oldest cursor.
        // Forward archives retain their newest cursor so late closing remarks can be received.
        if (!direction) Cursor = null;
        return true;
    }

    private static bool? Forward(IReadOnlyList<AnchorMessage> page)
    {
        for (var i = 1; i < page.Count; i++)
        {
            if (page[i].PreviousId == page[i - 1].Id) return true;
            if (page[i - 1].PreviousId == page[i].Id) return false;
        }
        // Older archived responses may omit predecessor links. Different publication times
        // distinguish direction without reordering equal-time fragments or numeric IDs.
        for (var i = 1; i < page.Count; i++)
            if (page[i - 1].PublishedAt is { } before && page[i].PublishedAt is { } after && before != after)
                return before < after;
        return null;
    }
}
