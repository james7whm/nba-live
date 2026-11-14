using System.Globalization;
using System.Text.Json;

namespace NbaLive.Core;

public static class MobileSchedule
{
    public static bool IsMobileId(string id) => id.Length >= 16 && id.All(RuntimeCompat.IsAsciiDigit);
    private static JsonElement Get(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;
    private static string S(JsonElement item, string key) => Get(item, key) is var value && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static int? N(JsonElement item, string key, bool signed = false) => int.TryParse(S(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && (signed || n >= 0) ? n : null;
    private static bool SameTeam(string a, string b)
    {
        var left = TeamPalette.Resolve(a); var right = TeamPalette.Resolve(b);
        return left != TeamPalette.Unknown && right != TeamPalette.Unknown ? left.Key == right.Key :
            !string.IsNullOrWhiteSpace(a) && LiveEntry.Normalize(a).Equals(LiveEntry.Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
    private static JsonDocument Open(string json)
    {
        try { var doc = JsonDocument.Parse(json); if (Get(doc.RootElement, "success").ValueKind == JsonValueKind.True) return doc; doc.Dispose(); }
        catch (JsonException) { }
        throw new SourceFormatException("移动端数据响应异常");
    }
    // Null means the response does not cover the requested date; an empty list means it explicitly does.
    public static IReadOnlyList<GameInfo>? Games(string json, DateOnly date)
    {
        using var doc = Open(json); var groups = Get(Get(doc.RootElement, "result"), "gameList");
        if (groups.ValueKind != JsonValueKind.Array) throw new SourceFormatException("移动端赛程结构异常");
        var matching = groups.EnumerateArray().Where(x => S(x, "day") == date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Length != 1) throw new SourceFormatException("移动端赛程日期重复");
        var rows = Get(matching[0], "matchList");
        if (rows.ValueKind != JsonValueKind.Array) throw new SourceFormatException("移动端赛程列表异常");
        var games = rows.EnumerateArray().Select(x => Game(x, date)).ToArray();
        if (games.Select(x => x.Id).Distinct().Count() != games.Length) throw new SourceFormatException("移动端比赛编号重复");
        return games;
    }
    public static IReadOnlyList<GameInfo> Combine(IReadOnlyList<GameInfo> web, IReadOnlyList<GameInfo> mobile)
    {
        // Preserve existing web identities only for an unambiguous date/team pairing.
        bool Same(GameInfo a, GameInfo b) => a.Date == b.Date && TeamPalette.Resolve(a.Away) != TeamPalette.Unknown &&
            TeamPalette.Resolve(a.Home) != TeamPalette.Unknown && TeamPalette.Resolve(a.Away).Key == TeamPalette.Resolve(b.Away).Key &&
            TeamPalette.Resolve(a.Home).Key == TeamPalette.Resolve(b.Home).Key;
        var merged = new List<GameInfo>();
        foreach (var item in mobile)
        {
            var matches = web.Where(w => Same(w, item)).ToArray();
            var reverseCount = matches.Length == 1 ? mobile.Count(m => Same(matches[0], m)) : 0;
            if (matches.Length > 1 || reverseCount > 1) throw new SourceFormatException("比赛映射存在歧义");
            if (matches.Length == 1 && (matches[0].Status == GameStatus.Finished && item.Status != GameStatus.Finished ||
                item.Status is GameStatus.Live or GameStatus.Finished && (item.AwayScore is null || item.HomeScore is null)))
                merged.Add(matches[0]);
            else merged.Add(matches.Length == 1 ? item with { Id = matches[0].Id } : item);
        }
        merged.AddRange(web.Where(w => !mobile.Any(m => Same(w, m))));
        return merged;
    }
    private static GameInfo Game(JsonElement item, DateOnly date)
    {
        var id = S(item, "matchId"); var away = S(item, "awayTeamName"); var home = S(item, "homeTeamName");
        if (!IsMobileId(id) || away.Length == 0 || home.Length == 0) throw new SourceFormatException("移动端比赛信息不完整");
        var scheduled = S(item, "matchTime");
        var game = AnchorParser.ReadGame(item, new(id, away, home, null, null, GameStatus.Unknown, "", date));
        if (game.Status == GameStatus.Scheduled && DateTime.TryParseExact(scheduled, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            game = game with { Progress = "未开始 " + time.ToString("HH:mm", CultureInfo.InvariantCulture) };
        return game;
    }
    public static GameInfo Match(string json, string matchId, GameInfo? expected = null)
    {
        using var doc = Open(json); var item = Get(doc.RootElement, "result");
        if (S(item, "matchId") != matchId) throw new SourceFormatException("移动端比赛编号不匹配");
        var stamp = S(item, "matchTime");
        if (stamp.Length < 10 || !DateOnly.TryParseExact(stamp.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new SourceFormatException("移动端比赛日期未提供");
        var game = Game(item, date);
        if (expected is not null && (game.Id != expected.Id || game.Date != expected.Date || !SameTeam(game.Away, expected.Away) || !SameTeam(game.Home, expected.Home)))
            throw new SourceFormatException("移动端比赛身份不匹配");
        return game;
    }
    public static BoxScoreSnapshot BoxScore(string json, GameInfo game, DateTimeOffset fetchedAt)
    {
        using var doc = Open(json); var root = Get(doc.RootElement, "result");
        if (S(root, "matchId") != game.Id || !SameTeam(S(root, "awayTeamName"), game.Away) || !SameTeam(S(root, "homeTeamName"), game.Home))
            throw new SourceFormatException("移动端统计比赛不匹配");
        TeamStatistics Team(string field, string team, string teamIdField)
        {
            var teamId = S(root, teamIdField); if (teamId.Length == 0) throw new SourceFormatException("移动端球队编号未提供");
            var rows = Get(root, field); if (rows.ValueKind != JsonValueKind.Array) throw new SourceFormatException("移动端球员统计未提供");
            var players = new List<PlayerStatistics>(); PlayerStatistics? total = null;
            foreach (var row in rows.EnumerateArray())
            {
                var id = S(row, "playerId"); if (id.EndsWith("-team-shooting", StringComparison.Ordinal)) continue;
                var isTotal = id.EndsWith("-team-stats", StringComparison.Ordinal);
                if (S(row, "teamId") != teamId || S(row, "matchId").Length > 0 && S(row, "matchId") != game.Id) throw new SourceFormatException("移动端球员不属于本场球队");
                if (!isTotal && (!IsMobileId(id) || S(row, "name").Length == 0)) throw new SourceFormatException("移动端球员编号异常");
                string Display(string key) => S(row, key) is var value && value.Length > 0 && value != "-" ? value : "—";
                var p = new PlayerStatistics(id, S(row,"name"), N(row,"starter") == 1, S(row,"position"), Display("mins"), Display("twoPoints"), Display("threePoints"), Display("ft"),
                    N(row,"oreb"), N(row,"dreb"), N(row,"reb"), N(row,"asts"), N(row,"pf"), N(row,"stl"), N(row,"to"), N(row,"blk"), N(row,"pts"), N(row,"plusMinus",true), N(row,"dnp") == 1 ? "未出场" : "") { JerseyNumber = S(row,"number"), OnCourt = N(row,"onCourt") switch { 1 => true, 0 => false, _ => null } };
                if (isTotal) total = p; else players.Add(p);
            }
            if (players.Select(p => p.Id).Distinct().Count() != players.Count) throw new SourceFormatException("移动端球员编号重复");
            return new(team, players, total) { CourtFetchedAt = fetchedAt };
        }
        return new(game, Team("awayPlayers",game.Away,"awayTeamId"), Team("homePlayers",game.Home,"homeTeamId"),fetchedAt);
    }
}
