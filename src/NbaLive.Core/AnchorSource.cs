using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public sealed record MobileMatch(string Id, GameInfo Game);
public sealed record AnchorRoom(string MatchId, string Key, string Name, bool Finished, TimeSpan LiveInterval, TimeSpan IdleInterval);
public sealed record AnchorMessage(string Id, string Text, string Author, DateTimeOffset? PublishedAt, bool IsClock)
{
    public string PreviousId { get; init; } = "";
}
public interface IAnchorSource
{
    Task<MobileMatch?> Resolve(GameInfo game, CancellationToken ct);
    Task<MobileMatch> GetMatch(MobileMatch match, CancellationToken ct);
    Task<AnchorRoom?> GetRoom(MobileMatch match, CancellationToken ct);
    // Live rooms return newest first and older cursor pages; archives may return oldest first and newer pages.
    // Preserve server order and predecessor IDs so the session can distinguish both protocols.
    Task<IReadOnlyList<AnchorMessage>> GetMessages(AnchorRoom room, string? cursor, CancellationToken ct);
}

/// <summary>Anonymous, read-only JSON adapter. Uses the structured source's HTTP client and request gate.</summary>
public sealed class AnchorSource(WebGameSource transport) : IAnchorSource
{
    // This legacy public metadata endpoint is verified over HTTP; its TLS endpoint is unavailable locally.
    private const string Mobile = "http://games.mobileapi.hupu.com/1/7.5.59/basketballapi/";
    private const string Live = "https://live-api.liangle.com/1/7.5.59/live/";
    public async Task<MobileMatch?> Resolve(GameInfo game, CancellationToken ct) => AnchorParser.Resolve(
        await transport.ReadMobile(new(Mobile + "scheduleList?competitionTag=nba&time_zone=Asia%2FShanghai"), false, ct), game);
    public async Task<MobileMatch> GetMatch(MobileMatch match, CancellationToken ct) => AnchorParser.Match(
        await transport.ReadMobile(new(Mobile + "singleMatch?matchId=" + Uri.EscapeDataString(match.Id)), false, ct), match);
    public async Task<AnchorRoom?> GetRoom(MobileMatch match, CancellationToken ct) => AnchorParser.Room(
        await transport.ReadMobile(new(Live + "queryLiveActivityKey?competitionType=basketball&matchId=" + Uri.EscapeDataString(match.Id)), true, ct), match.Id);
    public async Task<IReadOnlyList<AnchorMessage>> GetMessages(AnchorRoom room, string? cursor, CancellationToken ct) => AnchorParser.Messages(
        await transport.ReadMobile(new(Live + "queryLiveTextList?competitionType=basketball&matchId=" + Uri.EscapeDataString(room.MatchId) +
            "&liveActivityKeyStr=" + Uri.EscapeDataString(room.Key) + (cursor is null ? "" : "&commentId=" + Uri.EscapeDataString(cursor))), true, ct));
}

public static class AnchorParser
{
    private static JsonDocument Document(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("success", out var ok) &&
                ok.ValueKind == JsonValueKind.True && document.RootElement.TryGetProperty("result", out _)) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new SourceFormatException("主播数据响应异常");
    }
    private static JsonElement Get(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;
    private static string Str(JsonElement item, string key) => Get(item, key) is var v && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    private static int? Number(JsonElement item, string key) => int.TryParse(Str(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static bool SameTeam(string a, string b)
    {
        var left = TeamPalette.Resolve(a); var right = TeamPalette.Resolve(b);
        return left != TeamPalette.Unknown && right != TeamPalette.Unknown ? left.Key == right.Key :
            !string.IsNullOrWhiteSpace(a) && LiveEntry.Normalize(a).Equals(LiveEntry.Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
    public static MobileMatch? Resolve(string json, GameInfo game)
    {
        using var document = Document(json);
        var groups = Get(Get(document.RootElement, "result"), "gameList");
        if (groups.ValueKind != JsonValueKind.Array) throw new SourceFormatException("比赛映射数据异常");
        var candidates = new List<MobileMatch>();
        foreach (var group in groups.EnumerateArray())
        {
            if (Str(group, "day") != game.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) continue;
            var matches = Get(group, "matchList");
            if (matches.ValueKind != JsonValueKind.Array) throw new SourceFormatException("比赛列表数据异常");
            foreach (var item in matches.EnumerateArray())
                if (SameTeam(Str(item, "awayTeamName"), game.Away) && SameTeam(Str(item, "homeTeamName"), game.Home))
                {
                    var id = Str(item, "matchId");
                    if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new SourceFormatException("比赛编号数据异常");
                    candidates.Add(new(id, ReadGame(item, game)));
                }
        }
        var unique = candidates.DistinctBy(c => c.Id).ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }
    public static MobileMatch Match(string json, MobileMatch match)
    {
        using var document = Document(json); var item = Get(document.RootElement, "result");
        if (Str(item, "matchId") != match.Id || !SameTeam(Str(item, "awayTeamName"), match.Game.Away) || !SameTeam(Str(item, "homeTeamName"), match.Game.Home))
            throw new SourceFormatException("比赛数据不匹配");
        return match with { Game = ReadGame(item, match.Game) };
    }
    private static GameInfo ReadGame(JsonElement item, GameInfo original)
    {
        var status = Str(item, "matchStatus").ToUpperInvariant() switch
        {
            "COMPLETED" or "FINISHED" => GameStatus.Finished,
            "NOTSTARTED" or "NOT_STARTED" => GameStatus.Scheduled,
            "INPROGRESS" or "IN_PROGRESS" or "ONGOING" or "PLAYING" or "LIVE" => GameStatus.Live,
            "CANCELLED" or "POSTPONED" => GameStatus.Cancelled,
            _ => GameStatus.Unknown
        };
        var description = Str(Get(item, "frontEndMatchStatus"), "desc");
        if (description.Length == 0) description = Str(item, "matchStatusChinese");
        var progress = status == GameStatus.Finished ? "已结束" : GamePageParser.FormatLiveProgress(description);
        if (status == GameStatus.Unknown && Regex.IsMatch(description, @"第.{1,3}节|加时|加\d|中场|节间")) status = GameStatus.Live;
        if (status == GameStatus.Live && !Regex.IsMatch(description, @"第.{1,3}节|加时|加\d|中场|节间|休息|暂停"))
        {
            var quarter = Number(item, "currentQuarter");
            var label = quarter is > 4 ? $"加{quarter - 4}" : quarter is > 0 ? $"第{quarter}节" : "进行中";
            var remaining = Regex.Match(description, @"(?<!\d)\d{1,2}:\d{2}(?!\d)");
            progress = remaining.Success ? $"{label}剩{remaining.Value}" : $"{label} · {(description.Length == 0 ? "时间未提供" : description)}";
        }
        return original with { AwayScore = Number(item, "awayScore"), HomeScore = Number(item, "homeScore"), Status = status, Progress = progress };
    }
    public static AnchorRoom? Room(string json, string matchId)
    {
        using var document = Document(json); var item = Get(document.RootElement, "result");
        if (item.ValueKind != JsonValueKind.Object) throw new SourceFormatException("直播间数据异常");
        var key = Str(item, "liveActivityKey");
        if (string.IsNullOrWhiteSpace(key) || key == "0:0:0") return null;
        if (!Regex.IsMatch(key, @"^\d+:\d+:\d+$")) throw new SourceFormatException("直播间标识异常");
        var polling = Get(Get(item, "liveRoomMsgConfig"), "polling");
        if (Get(polling, "open").ValueKind == JsonValueKind.False) return null;
        TimeSpan Interval(string name, int fallback) => TimeSpan.FromMilliseconds(Math.Clamp(Number(polling, name) ?? fallback, 1000, 60000));
        return new(matchId, key, Str(item, "anchorName"), Str(item, "liveStatus") == "FINISH",
            Interval("inProgressIntervalInMillSeconds", 1000), Interval("notInProgressIntervalInMillSeconds", 5000));
    }
    public static IReadOnlyList<AnchorMessage> Messages(string json)
    {
        using var document = Document(json); var items = Get(document.RootElement, "result");
        if (items.ValueKind != JsonValueKind.Array) throw new SourceFormatException("主播消息数据异常");
        var result = new List<AnchorMessage>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Str(item, "commentId"); var text = Str(item, "content");
            if (id.Length == 0 || string.IsNullOrWhiteSpace(text)) throw new SourceFormatException("主播消息缺少标识或内容");
            DateTimeOffset? published = DateTime.TryParseExact(Str(item, "time"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time) ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), TimeSpan.FromHours(8)) : null;
            result.Add(new(id, text, Str(item, "nickName"), published, Str(item, "style") == "event") { PreviousId = Str(item, "previousCommentId") });
        }
        return result;
    }
}
