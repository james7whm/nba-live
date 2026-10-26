using System.Globalization;

namespace NbaLive.Core;

public sealed record TeamIdentity(string Key, string Name, string FullName, string EnglishName, string Conference, string Division)
{
    public string LogoTeam { get; init; } = Key;
    public string ArchiveId { get; init; } = "";
}

public static class TeamCatalog
{
    private static TeamIdentity T(string key, string full, string english, string conference, string division) =>
        new(key, TeamPalette.Resolve(key).Name, full, english, conference, division);
    public static IReadOnlyList<TeamIdentity> All { get; } = Array.AsReadOnly(new[]
    {
        T("celtics","波士顿凯尔特人","BOSTON CELTICS","东部","大西洋赛区"), T("nets","布鲁克林篮网","BROOKLYN NETS","东部","大西洋赛区"),
        T("knicks","纽约尼克斯","NEW YORK KNICKS","东部","大西洋赛区"), T("76ers","费城76人","PHILADELPHIA 76ERS","东部","大西洋赛区"), T("raptors","多伦多猛龙","TORONTO RAPTORS","东部","大西洋赛区"),
        T("bulls","芝加哥公牛","CHICAGO BULLS","东部","中部赛区"), T("cavaliers","克利夫兰骑士","CLEVELAND CAVALIERS","东部","中部赛区"), T("pistons","底特律活塞","DETROIT PISTONS","东部","中部赛区"), T("pacers","印第安纳步行者","INDIANA PACERS","东部","中部赛区"), T("bucks","密尔沃基雄鹿","MILWAUKEE BUCKS","东部","中部赛区"),
        T("hawks","亚特兰大老鹰","ATLANTA HAWKS","东部","东南赛区"), T("hornets","夏洛特黄蜂","CHARLOTTE HORNETS","东部","东南赛区"), T("heat","迈阿密热火","MIAMI HEAT","东部","东南赛区"), T("magic","奥兰多魔术","ORLANDO MAGIC","东部","东南赛区"), T("wizards","华盛顿奇才","WASHINGTON WIZARDS","东部","东南赛区"),
        T("nuggets","丹佛掘金","DENVER NUGGETS","西部","西北赛区"), T("timberwolves","明尼苏达森林狼","MINNESOTA TIMBERWOLVES","西部","西北赛区"), T("thunder","俄克拉荷马城雷霆","OKLAHOMA CITY THUNDER","西部","西北赛区"), T("blazers","波特兰开拓者","PORTLAND TRAIL BLAZERS","西部","西北赛区"), T("jazz","犹他爵士","UTAH JAZZ","西部","西北赛区"),
        T("warriors","金州勇士","GOLDEN STATE WARRIORS","西部","太平洋赛区"), T("clippers","洛杉矶快船","LOS ANGELES CLIPPERS","西部","太平洋赛区"), T("lakers","洛杉矶湖人","LOS ANGELES LAKERS","西部","太平洋赛区"), T("suns","菲尼克斯太阳","PHOENIX SUNS","西部","太平洋赛区"), T("kings","萨克拉门托国王","SACRAMENTO KINGS","西部","太平洋赛区"),
        T("mavericks","达拉斯独行侠","DALLAS MAVERICKS","西部","西南赛区"), T("rockets","休斯顿火箭","HOUSTON ROCKETS","西部","西南赛区"), T("grizzlies","孟菲斯灰熊","MEMPHIS GRIZZLIES","西部","西南赛区"), T("pelicans","新奥尔良鹈鹕","NEW ORLEANS PELICANS","西部","西南赛区"), T("spurs","圣安东尼奥马刺","SAN ANTONIO SPURS","西部","西南赛区")
    });
    public static IReadOnlyList<string> Divisions { get; } = Array.AsReadOnly(new[] { "大西洋赛区", "中部赛区", "东南赛区", "西北赛区", "太平洋赛区", "西南赛区" });
    public static TeamIdentity? Resolve(string? key) => All.FirstOrDefault(t => t.Key == TeamPalette.Resolve(key).Key);
}

public sealed record TeamStanding(TeamIdentity Team, int Rank, int Wins, int Losses, double? WinPercent,
    double? GamesBehind, string HomeRecord, string AwayRecord, double? Points, double? OpponentPoints, double? Differential, string Streak)
{
    public int Games => Wins + Losses;
    public string Record => $"{Wins}–{Losses}";
    public int? PlayoffSeed { get; init; }
    public string Progress { get; init; } = "—";
}
public sealed record TeamMetric(string Key, string Label, double? Value, bool Percent = false)
{
    public string Display => Value is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) + (Percent ? "%" : "") : "—";
}
public sealed record TeamAverages(string TeamKey, IReadOnlyList<TeamMetric> Metrics);
public sealed record TeamStandingsSnapshot(string Season, IReadOnlyList<TeamStanding> Rows, DateTimeOffset FetchedAt)
{
    public IReadOnlyDictionary<string, int> DivisionRanks { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, TeamAverages> Averages { get; init; } = new Dictionary<string, TeamAverages>();
    public DateTimeOffset? DivisionFetchedAt { get; init; }
    public DateTimeOffset? AveragesFetchedAt { get; init; }
    public string Notice { get; init; } = "";
    public TeamPhase Phase { get; init; }
    public IReadOnlyList<TeamIdentity> Teams {get;init;} = Array.Empty<TeamIdentity>();
    public PlayoffBracketSnapshot? Bracket {get;init;}
    public PlayInSnapshot? PlayIn {get;init;}
}
public sealed record TeamGame(string Id, string Season, TeamIdentity Team, TeamIdentity Opponent, bool Home,
    DateTimeOffset Tipoff, GameStatus Status, int? TeamPoints, int? OpponentPoints)
{
    public bool? Won => Status == GameStatus.Finished && TeamPoints is { } a && OpponentPoints is { } b && a != b ? a > b : null;
    public string Score => TeamPoints is { } a && OpponentPoints is { } b ? $"{a} : {b}" : "—";
    public string Description => $"{Tipoff.ToOffset(TimeSpan.FromHours(8)):MM月dd日 HH:mm} · {(Home ? "主场" : "客场")} · {Team.Name} {Score} {Opponent.Name}";
    public string RoundKey { get; init; } = "";
    public string RoundName { get; init; } = "";
    public int? GameNumber { get; init; }
}
public sealed record TeamOverviewSnapshot(TeamIdentity Team, string Season, IReadOnlyList<TeamGame> Games, DateTimeOffset FetchedAt)
{
    public TeamPhase Phase { get; init; }
    public TeamStanding? Summary { get; init; }
    public TeamAverages? Averages { get; init; }
    public IReadOnlyList<TeamSeries> Series { get; init; } = Array.Empty<TeamSeries>();
    public string Notice { get; init; } = "";
    public bool GamesAvailable {get;init;} = true;
    public DateTimeOffset? AveragesFetchedAt {get;init;}
    public DateTimeOffset? GamesFetchedAt {get;init;}
    public IReadOnlyList<TeamGame> Recent(int count) => Games.Where(g => g.Status == GameStatus.Finished && g.Won.HasValue)
        .OrderByDescending(g => g.Tipoff).ThenBy(g => g.Id, StringComparer.Ordinal).Take(count).Reverse().ToArray();
    public IReadOnlyList<TeamGame> Upcoming(DateTimeOffset now, int count) => Games.Where(g => g.Status == GameStatus.Scheduled && g.Tipoff >= now)
        .OrderBy(g => g.Tipoff).ThenBy(g => g.Id, StringComparer.Ordinal).Take(count).ToArray();
}
public interface ITeamInsightsSource
{
    Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct);
    Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team, string season, CancellationToken ct);
}
