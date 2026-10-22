namespace NbaLive.Core;

public static class OverlayFeatures
{
    public static bool NeedsStatistics(AppSettings settings) => settings.ContentMode == OverlayContent.Lineup ||
        settings.FollowedPlayers.Length > 0 && (settings.ContentMode == OverlayContent.Players ||
            settings.ContentMode == OverlayContent.Text && settings.FollowedPlayersOnly);
}

public sealed record LineupCard(string Team, TeamJerseyColor Color, IReadOnlyList<PlayerStatistics> Players, string Message, string Details)
{
    public bool IsStarterReference { get; init; }
}

public static class LineupCards
{
    public static LineupCard Create(TeamStatistics? team, GameInfo? game, SessionState? live, DateTimeOffset now)
    {
        var name = team?.Team ?? "球队";
        var color = TeamPalette.Resolve(name, team?.RosterSlug);
        LineupCard Empty(string message, string? details = null) => new(name, color, [], message, details ?? message);
        if (game is null || team is null || team.Players.Count == 0) return Empty("等待本场球员统计");
        var states = CourtLineups.Resolve(team, game, live, now);
        var players = team.Players.Where(p => states[p.Id].Presence is CourtPresence.OnCourt or CourtPresence.Starter).ToArray();
        if (players.Length == 5) return new(name, color, players, "", states[players[0].Id].Detail)
        { IsStarterReference = states[players[0].Id].Presence == CourtPresence.Starter };
        var detail = states.Values.FirstOrDefault()?.Detail ?? "等待本场换人名单";
        return Empty(detail.Contains("首发名单不完整", StringComparison.Ordinal) ? detail : detail == "比赛尚未开始" ? "比赛尚未开始" : detail == "比赛已结束或取消" ? "比赛已结束／取消" :
            players.Length > 0 ? $"阵容未确认 · 已确认 {players.Length}/5 人" : "阵容未确认", detail);
    }
}

public sealed record PlayerStatDelta(int? Points, int? Rebounds, int? Assists)
{
    public bool HasChanges => Points is not (null or 0) || Rebounds is not (null or 0) || Assists is not (null or 0);
    public bool HasReduction => Points < 0 || Rebounds < 0 || Assists < 0;
    public string Description => !HasChanges ? "本次无得分／篮板／助攻变化" : string.Join(" · ",
        new[] { Describe("得分", Points), Describe("篮板", Rebounds), Describe("助攻", Assists) }.Where(s => s.Length > 0)) +
        (HasReduction ? "（含数据下调／修正）" : "");
    private static string Describe(string name, int? delta) => delta is null or 0 ? "" : $"{name} {delta:+0;-0;0}";
    public static string Format(int? value, int? delta, string missing = "—") => value is null ? missing :
        value.Value.ToString() + (delta is null or 0 ? "" : delta < 0 ? $"（修正 {delta}）" : $"（+{delta}）");
}

public sealed record StatisticsDeltaSnapshot(string GameId, DateTimeOffset From, DateTimeOffset Through,
    IReadOnlyDictionary<(string Team, string PlayerId), PlayerStatDelta> Players)
{
    public PlayerStatDelta? Find(string team, string playerId) => Players.GetValueOrDefault((team, playerId));
    public string Description => $"最近两次成功统计：{MatchTime.Format(From)} → {MatchTime.Format(Through)}；变化可能包含多条事件或来源修正，不表示单次进攻。";
}

/// <summary>Compares successful snapshots only; initial data, new players and missing values are not increments.</summary>
public sealed class StatisticsChangeTracker
{
    private BoxScoreSnapshot? _previous;
    private StatisticsDeltaSnapshot? _changes;
    public void Reset() { _previous = null; _changes = null; }
    public StatisticsState Observe(StatisticsState state)
    {
        var gameId = state.SelectedGame?.Id ?? state.Snapshot?.Game.Id;
        if (_previous is not null && _previous.Game.Id != gameId) Reset();
        if (state.Snapshot is not { } current || current.Game.Id != gameId || state.IsLoading || state.HasError)
            return state with { Changes = _changes };
        if (ReferenceEquals(current, _previous) || _previous is not null && current.FetchedAt <= _previous.FetchedAt)
            return state with { Changes = _changes };
        if (_previous is { } prior)
        {
            var changes = new Dictionary<(string, string), PlayerStatDelta>();
            var previous = Players(prior).ToDictionary(p => (p.Team, p.Player.Id), p => p.Player);
            foreach (var (team, player) in Players(current))
                if (previous.TryGetValue((team, player.Id), out var before))
                    changes[(team, player.Id)] = new(Diff(player.Points, before.Points), Diff(player.Rebounds, before.Rebounds), Diff(player.Assists, before.Assists));
            _changes = new(current.Game.Id, prior.FetchedAt, current.FetchedAt, changes);
        }
        _previous = current;
        return state with { Changes = _changes };
    }
    private static int? Diff(int? value, int? previous) => value is not null && previous is not null ? value - previous : null;
    private static IEnumerable<(string Team, PlayerStatistics Player)> Players(BoxScoreSnapshot snapshot) =>
        snapshot.Away.Players.Select(p => (snapshot.Away.Team, p)).Concat(snapshot.Home.Players.Select(p => (snapshot.Home.Team, p)));
}
