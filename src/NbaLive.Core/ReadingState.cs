using System.Globalization;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public static class EventSelection
{
    public static bool IsImportant(LiveEntry entry)
    {
        var text = entry.Text;
        return text.Contains("犯规") || text.Contains("暂停") ||
            (text.Contains("命中") && !text.Contains("未命中")) || text.Contains("得分") ||
            Regex.IsMatch(text, @"计[123]分|(?:第.{1,5}节|比赛|加时).*(?:开始|结束)");
    }

    public static IReadOnlyList<LiveEntry> Recent(IReadOnlyList<LiveEntry> history, int count, bool importantOnly)
    {
        if (count <= 0) return [];
        var result = new List<LiveEntry>(count);
        for (var i = history.Count - 1; i >= 0 && result.Count < count; i--)
            if (!importantOnly || IsImportant(history[i])) result.Add(history[i]);
        return result;
    }
}

/// <summary>Freezes the visible snapshot while the network session keeps receiving data.</summary>
public sealed class ReadingState
{
    public SessionState? Visible { get; private set; }
    public SessionState? Latest { get; private set; }
    public bool IsPaused { get; private set; }
    public int PendingCount { get; private set; }
    private readonly HashSet<string> _pending = [];
    private string? _gameId;

    public SessionState Accept(SessionState state)
    {
        var id = state.SelectedGame?.Id ?? state.Snapshot?.Game.Id;
        var firstSnapshot = Visible?.Snapshot is null && state.Snapshot is not null;
        var changedGame = id != _gameId;
        _gameId = id;
        if (IsPaused && !changedGame && !firstSnapshot && !(state.IsLoading && state.Snapshot is null) && Visible is not null)
        {
            if (Latest is not null && state.HistoryRevision != Latest.HistoryRevision)
                foreach (var key in state.AddedEntryKeys) _pending.Add(key);
            PendingCount = _pending.Count;
        }
        else
        {
            Visible = state;
            _pending.Clear();
            PendingCount = 0;
        }
        Latest = state;
        return Visible ?? state;
    }
    public void Pause() { IsPaused = true; _pending.Clear(); PendingCount = 0; }
    public SessionState? Resume()
    {
        IsPaused = false;
        _pending.Clear();
        PendingCount = 0;
        return Visible = Latest;
    }
}

public static class PollingPolicy
{
    public static TimeSpan Next(GameInfo game, DateTimeOffset now)
    {
        if (game.Status == GameStatus.Scheduled)
        {
            var match = Regex.Match(game.Progress, @"(?<!\d)(\d{1,2}):(\d{2})(?!\d)");
            if (match.Success && TimeOnly.TryParseExact(match.Value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            {
                var tipOff = new DateTimeOffset(game.Date.ToDateTime(time), TimeSpan.FromHours(8));
                if (tipOff - now <= TimeSpan.FromMinutes(5)) return TimeSpan.FromSeconds(10);
            }
            return TimeSpan.FromSeconds(30);
        }
        return TimeSpan.FromSeconds(5);
    }
}

public static class GameOrdering
{
    public static bool IsFavorite(GameInfo game, string favorite) => !string.IsNullOrWhiteSpace(favorite) &&
        (game.Away.Equals(favorite, StringComparison.OrdinalIgnoreCase) || game.Home.Equals(favorite, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<GameInfo> Arrange(IEnumerable<GameInfo> games, string favorite) => games
        .OrderByDescending(g => IsFavorite(g, favorite))
        .ThenBy(g => g.Status switch { GameStatus.Live => 0, GameStatus.Scheduled => 1, GameStatus.Finished => 2, _ => 3 }).ToArray();
}

public static class SessionStatus
{
    public static string Describe(SessionState state, DateTimeOffset now)
    {
        if (state.HasError)
            return state.Snapshot is null ? state.Message : $"{state.Message} · 上次连通 {MatchTime.Format(state.Snapshot.FetchedAt)}";
        if (state.Snapshot is null) return state.Message;
        var connection = $"连通 {MatchTime.Format(state.Snapshot.FetchedAt)}";
        if (state.Snapshot.Game.Status == GameStatus.Finished) return $"已结束 · {connection}";
        if (state.Snapshot.Game.Status == GameStatus.Cancelled) return $"已取消／延期 · {connection}";
        var age = now - state.Snapshot.FetchedAt;
        if (state.Snapshot.Game.Status == GameStatus.Live && age > TimeSpan.FromSeconds(30))
            return $"同步较慢 · 上次连通 {MatchTime.Format(state.Snapshot.FetchedAt)}";
        if (state.LastEventObservedAt is { } eventTime)
            return $"{connection} · 新事件 {MatchTime.Format(eventTime)}";
        return $"{connection} · 等待文字直播";
    }
}
