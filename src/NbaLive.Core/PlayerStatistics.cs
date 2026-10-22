namespace NbaLive.Core;

public sealed record PlayerStatistics(string Id, string Name, bool IsStarter, string Position,
    string Minutes, string FieldGoals, string ThreePointers, string FreeThrows,
    int? OffensiveRebounds, int? DefensiveRebounds, int? Rebounds, int? Assists,
    int? Fouls, int? Steals, int? Turnovers, int? Blocks, int? Points, int? PlusMinus,
    string Status = "")
{
    public string Role => IsStarter ? "首发" : "替补";
    public string? JerseyNumber { get; init; }
}

public sealed record TeamStatistics(string Team, IReadOnlyList<PlayerStatistics> Players, PlayerStatistics? Total)
{
    public string? RosterSlug { get; init; }
}
public sealed record BoxScoreSnapshot(GameInfo Game, TeamStatistics Away, TeamStatistics Home, DateTimeOffset FetchedAt)
{
    public bool HasPlayerData => Away.Players.Count > 0 || Home.Players.Count > 0;
    public static BoxScoreSnapshot Waiting(GameInfo game, DateTimeOffset now) =>
        new(game, new(game.Away, [], null), new(game.Home, [], null), now);
}

public interface IStatisticsSource
{
    Task<BoxScoreSnapshot> GetBoxScore(string gameId, CancellationToken cancellationToken);
}
