using System.Globalization;
using System.Text.Json;

namespace NbaLive.Core;

public sealed record PlayerDetailQuery(GameInfo Game, string Team, PlayerStatistics Player)
{
    public string Key => Game.Id + "|" + TeamPalette.Resolve(Team).Key + "|" + Player.Id;
    public DateTimeOffset? StatisticsFetchedAt { get; init; }
}
public sealed record ShotBreakdown(string Name, int? Made, int? Attempts)
{
    public string Display => Made is { } made && Attempts is { } attempts ? $"{made}/{attempts}" : "—";
}
// Coordinates are source canvas pixels on a 690 x 646 half court; Points is a made flag, not a score value.
public sealed record PlayerShot(int Index, int Period, double X, double Y, bool Made, string Clock = "", string Kind = "");
public sealed record PlayerGamePeriod(int Period, PlayerStatistics Statistics, IReadOnlyList<ShotBreakdown> ShotTypes)
{
    public string Label => Period == 0 ? "全场" : Period <= 4 ? $"第{Period}节" : $"加时{Period - 4}";
    public static string TwoPointers(string fieldGoals, string threePointers)
    {
        static int[]? Pair(string value)
        {
            var parts = value.Split('-');
            return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && a >= 0 && b >= a ? [a, b] : null;
        }
        var fg = Pair(fieldGoals); var three = Pair(threePointers);
        return fg is not null && three is not null && fg[0] >= three[0] && fg[1] >= three[1] && fg[0] - three[0] <= fg[1] - three[1]
            ? $"{fg[0] - three[0]}/{fg[1] - three[1]}" : "—";
    }
}
public sealed record PlayerGameDetailSnapshot(PlayerDetailQuery Query, string MobileMatchId, string MobilePlayerId,
    string Name, string Position, string Number, Uri? Portrait, IReadOnlyList<PlayerGamePeriod> Periods,
    IReadOnlyList<PlayerShot> Shots, string Notice, DateTimeOffset FetchedAt)
{
    public PlayerGamePeriod? Find(int period) => Periods.FirstOrDefault(p => p.Period == period);
}
public interface IPlayerGameDetailSource
{
    Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct);
}
public interface IPlayerPortraitSource
{
    Task<byte[]> GetPortrait(Uri uri, CancellationToken ct);
}

public static class PlayerGameDetailParser
{
    public static string LegacyNumber(string id)
    {
        var number = System.Text.RegularExpressions.Regex.Match(id, @"(?:^|[-/])(\d+)(?:\.html)?$");
        return number.Success ? number.Groups[1].Value : id;
    }
    private static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string S(JsonElement element, string name) => Get(element, name) is var value && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static int? N(JsonElement element, string name, bool signed = false) => int.TryParse(S(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && (signed || value >= 0) ? value : null;
    private static JsonDocument Document(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (Get(doc.RootElement, "success").ValueKind == JsonValueKind.True && Get(doc.RootElement, "result").ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        throw new SourceFormatException("球员详情响应异常");
    }
    private static bool SameTeam(string a, string b) => TeamPalette.Resolve(a) is var left && left != TeamPalette.Unknown
        ? left.Key == TeamPalette.Resolve(b).Key : a.Equals(b, StringComparison.OrdinalIgnoreCase);
    public static (string TeamId, string[] PlayerIds) MatchPlayers(string json, PlayerDetailQuery query, string matchId)
    {
        using var doc = Document(json); var root = Get(doc.RootElement, "result");
        if (S(root, "matchId") != matchId || !SameTeam(S(root, "awayTeamName"), query.Game.Away) || !SameTeam(S(root, "homeTeamName"), query.Game.Home))
            throw new SourceFormatException("球员详情比赛不匹配");
        var home = SameTeam(query.Team, query.Game.Home);
        if (!home && !SameTeam(query.Team, query.Game.Away)) throw new SourceFormatException("球员球队不属于本场比赛");
        var team = S(root, home ? "homeTeamId" : "awayTeamId"); var players = Get(root, home ? "homePlayers" : "awayPlayers");
        if (team.Length == 0 || players.ValueKind != JsonValueKind.Array) throw new SourceFormatException("本场球员名单未提供");
        return (team, players.EnumerateArray().Select(p => S(p, "playerId")).Where(id => id.Length > 0).Distinct().ToArray());
    }
    public static string ResolvePlayer(string json, string originalId, string teamId, IReadOnlyList<string> matchPlayers)
    {
        // Some newer players already use the mobile ID in the structured source.
        if (matchPlayers.Contains(originalId)) return originalId;
        using var doc = RosterDocument(json); var root = Get(doc.RootElement, "result");
        var list = Get(root, "list");
        if (S(Get(root, "info"), "teamId") != teamId || list.ValueKind != JsonValueKind.Array) throw new SourceFormatException("球员编号映射不可用");
        var ids = list.EnumerateArray().Where(p => S(p, "player_id") == LegacyNumber(originalId)).Select(p => S(p, "playerId")).Distinct().ToArray();
        if (ids.Length != 1 || !matchPlayers.Contains(ids[0])) throw new SourceFormatException("未能唯一对应本场球员编号");
        return ids[0];
    }
    public static bool DidNotPlay(string json, string playerId)
    {
        using var doc = Document(json); var root = Get(doc.RootElement, "result");
        return new[] { Get(root, "awayPlayers"), Get(root, "homePlayers") }.Where(a => a.ValueKind == JsonValueKind.Array)
            .SelectMany(a => a.EnumerateArray()).Any(p => S(p, "playerId") == playerId && N(p, "dnp") == 1);
    }
    private static JsonDocument RosterDocument(string json)
    {
        try { return JsonDocument.Parse(json); } catch (JsonException) { throw new SourceFormatException("球员编号名单响应异常"); }
    }
    public static PlayerGameDetailSnapshot Parse(string json, PlayerDetailQuery query, string matchId, string playerId, string teamId, DateTimeOffset now)
    {
        using var doc = Document(json); var root = Get(doc.RootElement, "result");
        if (S(root, "playerId") != playerId || (S(root, "nbaPid") != LegacyNumber(query.Player.Id) && playerId != query.Player.Id) ||
            !SameTeam(S(root, "teamName"), query.Team) || !SameTeam(S(root, "awayTeamName"), query.Game.Away) || !SameTeam(S(root, "homeTeamName"), query.Game.Home) ||
            (S(root, "matchId").Length > 0 && S(root, "matchId") != matchId)) throw new SourceFormatException("球员详情身份不匹配");
        var date = System.Text.RegularExpressions.Regex.Match(S(root, "matchTime"), @"\d{4}-\d{2}-\d{2}");
        if (date.Success && date.Value != query.Game.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) throw new SourceFormatException("球员详情日期不匹配");
        var name = S(root, "name"); var position = S(root, "position");
        var periods = new List<PlayerGamePeriod>(); var rows = Get(root, "quarterStatsList");
        if (rows.ValueKind != JsonValueKind.Array || name.Length == 0) throw new SourceFormatException("球员分节统计未提供");
        static int Quarter(string value) => int.TryParse(value, out var period) && period is >= 0 and <= 20 ? period : throw new SourceFormatException("球员节次无法识别");
        foreach (var row in rows.EnumerateArray())
        {
            var period = Quarter(S(row, "quarter"));
            string Shooting(string made, string attempted) => N(row, made) is { } m && N(row, attempted) is { } a && m <= a ? $"{m}-{a}" : "—";
            var seconds = N(row, "sec");
            var minutes = seconds is { } sec ? $"{sec / 60}:{sec % 60:00}" : N(row, "min")?.ToString(CultureInfo.InvariantCulture) ?? "—";
            var stats = new PlayerStatistics(query.Player.Id, name, query.Player.IsStarter, position, minutes,
                Shooting("fgm", "fga"), Shooting("tpm", "tpa"), Shooting("ftm", "fta"), N(row, "oreb"), N(row, "dreb"),
                N(row, "reb"), N(row, "asts"), N(row, "pf"), N(row, "stl"), N(row, "to"), N(row, "blk"), N(row, "pts"), N(row, "plusMinus", true), query.Player.Status)
                { JerseyNumber = query.Player.JerseyNumber };
            // The endpoint omits these full-game fields. Retain the independently fetched box-score values with its own timestamp in the UI.
            if (period == 0) stats = stats with { OffensiveRebounds = stats.OffensiveRebounds ?? query.Player.OffensiveRebounds,
                DefensiveRebounds = stats.DefensiveRebounds ?? query.Player.DefensiveRebounds, PlusMinus = stats.PlusMinus ?? query.Player.PlusMinus };
            string[] keys = ["jump", "dunk", "layup", "tip", "hook", "other"]; string[] labels = ["跳投", "扣篮", "上篮", "补篮", "勾手", "其他"];
            var types = keys.Select((key, i) => new ShotBreakdown(labels[i], N(row, key), N(row, key + "a"))).Select(t => t.Made > t.Attempts ? t with { Made = null, Attempts = null } : t).ToArray();
            periods.RemoveAll(p => p.Period == period); periods.Add(new(period, stats, types));
        }
        if (!periods.Any(p => p.Period == 0)) throw new SourceFormatException("球员全场统计未提供");
        var points = Get(root, "pointsList"); var shots = new List<PlayerShot>(); var invalid = 0;
        if (points.ValueKind != JsonValueKind.Array) throw new SourceFormatException("球员投篮记录未提供");
        var index = 0;
        foreach (var point in points.EnumerateArray())
        {
            if (S(point, "playerId") != playerId || S(point, "teamId") != teamId) throw new SourceFormatException("投篮记录不属于当前球员");
            var period = Quarter(S(point, "quarter")); var made = N(point, "points");
            if (period == 0 || made is not (0 or 1) || !double.TryParse(S(point, "x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(S(point, "y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !RuntimeCompat.IsFinite(x) || !RuntimeCompat.IsFinite(y) || x < 0 || x > 690 || y < 0 || y > 646)
            { invalid++; continue; }
            shots.Add(new(index++, period, x, y, made == 1, S(point, "clock"), S(point, "shootTypeName")));
        }
        var full = periods.Single(p => p.Period == 0).Statistics;
        var notice = "落点接口未提供逐球时间／出手方式时不推算；前后场篮板及缺失正负值沿用统计表" + (query.StatisticsFetchedAt is { } at ? $" {MatchTime.Format(at)}" : "") + " 快照。";
        if (invalid > 0) notice += $" {invalid} 条无效坐标未绘制。";
        if (full.FieldGoals != "—" && full.FieldGoals != $"{shots.Count(s => s.Made)}-{shots.Count}") notice += " 投篮落点与汇总暂不一致，可能尚未完整发布。";
        Uri? portrait = Uri.TryCreate(S(root, "logoUrl"), UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host.EndsWith(".hoopchina.com.cn", StringComparison.OrdinalIgnoreCase) ? url : null;
        return new(query, matchId, playerId, name, position, S(root, "shirtNumber").Replace("号", ""), portrait,
            periods.OrderBy(p => p.Period).ToArray(), shots, notice, now);
    }
}
