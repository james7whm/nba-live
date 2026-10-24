using System.Text.Json;
using System.Text.Json.Serialization;

namespace NbaLive.Core;

public sealed record BookmarkedEvent(GameInfo Game, LiveEntry Entry, DateTimeOffset SavedAt)
{
    public DateTimeOffset? CorrectedAt { get; init; }
    [JsonIgnore] public (string GameId, string EventKey) Identity => (Game.Id, Entry.Key);
    [JsonIgnore] public string Display => $"{Game.Date:yyyy-MM-dd} · {Game.Away} vs {Game.Home} · {(Entry.Origin == TextOrigin.Anchor ? "主播解说" : "赛况实录")}\n{Entry.Display}" +
        (string.IsNullOrWhiteSpace(Entry.Score) ? "" : $"\n事件比分：{Entry.Score}") +
        (CorrectedAt is null ? "" : "\n已同步来源修正");
}

/// <summary>Persists only explicitly bookmarked events, separately from transient session history.</summary>
public sealed class EventBookmarks
{
    private readonly string _path;
    private bool _unreadable;
    private IReadOnlyList<LiveEntry>? _observed;
    private string? _observedGame;
    public IReadOnlyList<BookmarkedEvent> Items { get; private set; } = [];
    public string? Error { get; private set; }
    public event Action? Changed;
    public EventBookmarks(string directory)
    {
        _path = Path.Combine(directory, "bookmarks.json");
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<BookmarkedEvent[]>(File.ReadAllText(_path)) ?? throw new JsonException();
            if (saved.Any(e => e is null || e.Game is null || e.Entry is null || string.IsNullOrWhiteSpace(e.Game.Id) ||
                string.IsNullOrWhiteSpace(e.Game.Away) || string.IsNullOrWhiteSpace(e.Game.Home) || string.IsNullOrWhiteSpace(e.Entry.Text))) throw new JsonException();
            Items = Array.AsReadOnly(saved.OrderByDescending(e => e.SavedAt).DistinctBy(e => e.Identity).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _unreadable = true;
            Error = "收藏文件无法读取，已保留原文件；修复 bookmarks.json 后重新启动。当前不会覆盖它。";
        }
    }
    public bool Contains(string gameId, string eventKey) => Items.Any(e => e.Identity == (gameId, eventKey));
    public string? Toggle(GameInfo game, LiveEntry entry)
    {
        var identity = (game.Id, entry.Key);
        var latest = _observedGame == game.Id ? _observed?.FirstOrDefault(e => e.Key == entry.Key) ?? entry : entry;
        var next = Contains(game.Id, entry.Key) ? Items.Where(e => e.Identity != identity).ToArray() :
            Items.Append(new BookmarkedEvent(game, latest, DateTimeOffset.UtcNow)
            { CorrectedAt = latest != entry ? DateTimeOffset.UtcNow : null }).ToArray();
        return Persist(next);
    }
    public string? Remove(BookmarkedEvent item) => Persist(Items.Where(e => e.Identity != item.Identity).ToArray());
    public void Observe(SessionState state)
    {
        if (state.Snapshot is not { } snapshot || (_observedGame == snapshot.Game.Id && ReferenceEquals(_observed, state.History))) return;
        _observedGame = snapshot.Game.Id; _observed = state.History;
        if (!Items.Any(e => e.Game.Id == snapshot.Game.Id)) return;
        var entries = state.History.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Last());
        var changed = false;
        var next = Items.Select(saved =>
        {
            if (saved.Game.Id != snapshot.Game.Id || !entries.TryGetValue(saved.Entry.Key, out var current) || saved.Entry == current) return saved;
            changed = true;
            return saved with { Entry = current, CorrectedAt = DateTimeOffset.UtcNow };
        }).ToArray();
        if (changed) Persist(next);
    }
    private string? Persist(BookmarkedEvent[] next)
    {
        if (_unreadable) return Error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(next.OrderByDescending(e => e.SavedAt), new JsonSerializerOptions { WriteIndented = true, IgnoreReadOnlyProperties = true }));
            RuntimeCompat.ReplaceFile(_path + ".tmp", _path);
            Items = Array.AsReadOnly(next.OrderByDescending(e => e.SavedAt).ToArray()); Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "收藏保存失败，原有收藏已保留；请检查目录是否可写后重试。"; }
        Changed?.Invoke(); return Error;
    }
    public static string Format(IEnumerable<BookmarkedEvent> entries) => string.Join(Environment.NewLine + Environment.NewLine, entries.Select(e => e.Display));
}
