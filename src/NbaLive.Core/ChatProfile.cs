namespace NbaLive.Core;

public static class ChatProfile
{
    public const int MaximumNicknameLength = 12;
    public static string NormalizeNickname(string? nickname)
    {
        var value = (nickname ?? "").Trim();
        return value.Length <= MaximumNicknameLength && !value.Any(char.IsControl) ? value : "";
    }
    public static string TeamLabel(string? team) => TeamCatalog.Resolve(team)?.Name ?? "纯路人";
    public static IReadOnlyList<TeamIdentity> TeamsInConference(string conference, TeamStandingsSnapshot? standings)
    {
        var catalog = TeamCatalog.All.Where(t => t.Conference == conference).ToArray();
        var ranked = standings?.Rows.Where(r => r.Team.Conference == conference && catalog.Any(t => t.Key == r.Team.Key))
            .OrderBy(r => r.Rank).Select(r => r.Team).DistinctBy(t => t.Key).ToArray() ?? [];
        return ranked.Concat(catalog.Where(t => ranked.All(r => r.Key != t.Key))).ToArray();
    }
}
