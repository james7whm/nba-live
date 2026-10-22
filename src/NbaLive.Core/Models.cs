using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public enum GameStatus { Scheduled, Live, Finished, Cancelled, Unknown }
public enum TextOrigin { Structured, Anchor }
public enum TextSourcePreference { AnchorPreferred, Structured }
public enum TextDelivery { Latest, Paged }

public sealed record GameInfo(string Id, string Away, string Home, int? AwayScore,
    int? HomeScore, GameStatus Status, string Progress, DateOnly Date)
{
    public string Score => $"{Away} {AwayScore?.ToString() ?? "—"} : {HomeScore?.ToString() ?? "—"} {Home}";
    public string Display => $"{Score}    {Progress}";
}

public sealed record LiveEntry(string? SourceId, int Period, string Clock, string Team,
    string Text, string Score, long Order)
{
    public TextOrigin Origin { get; init; }
    public string Author { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public string Key => !string.IsNullOrWhiteSpace(SourceId) ? SourceId :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Period}|{Clock}|{Normalize(Text)}")));
    public string PeriodLabel => Period switch { 0 => "", <= 4 => $"第{Period}节", _ => $"加时{Period - 4}" };
    public string Display => string.IsNullOrEmpty(Clock) ? Text : $"{PeriodLabel} {Clock}  {Text}".Trim();
    public static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
}

public sealed record LiveSnapshot(GameInfo Game, IReadOnlyList<LiveEntry> Entries, DateTimeOffset FetchedAt);

public interface IGameSource
{
    Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken cancellationToken);
    Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken cancellationToken);
}

public sealed class SourceFormatException(string message) : Exception(message);

public static class MatchTime
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, China).DateTime);
    public static string Format(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, China).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
