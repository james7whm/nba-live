using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public static class TeamInsightsParser
{
    private static string Text(IElement e) => Regex.Replace(e.TextContent, @"\s+", " ").Trim();
    private static double? Number(string s) => double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.IsNaN(n) && !double.IsInfinity(n) ? n : null;
    private static int Integer(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new SourceFormatException("球队战绩格式异常");
    public static string Season(string text)
    {
        var m = Regex.Match(text, @"(?<!\d)(20\d{2})\s*(?:至|[-–—])\s*(20\d{2}|\d{2})(?!\d)");
        if (!m.Success) throw new SourceFormatException("赛季信息未提供");
        var start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var end = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); if (end < 100) end += start / 100 * 100;
        if (end != start + 1) throw new SourceFormatException("赛季信息不一致");
        return $"{start}-{end}";
    }
    public static TeamStandingsSnapshot Standings(string html, DateTimeOffset fetched, bool division = false)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var season = Season(doc.Title ?? "");
        var rows = new List<TeamStanding>(); var group = "";
        foreach (var tr in doc.QuerySelectorAll("table.players_table tr"))
        {
            var cells = tr.Children.Where(e => e.LocalName is "td" or "th").ToArray();
            if (cells.Length == 1)
            {
                var title = Text(cells[0]);
                group = division ? TeamCatalog.Divisions.FirstOrDefault(title.StartsWith) ?? "" : title is "东部" or "西部" ? title : "";
                continue;
            }
            if (cells.Length != 14 || cells[1].QuerySelector("a[href*='/teams/']") is not { } link) continue;
            var slug = Regex.Match(link.GetAttribute("href") ?? "", @"/teams/([a-z0-9-]+)").Groups[1].Value;
            var team = TeamCatalog.Resolve(slug) ?? throw new SourceFormatException("排名包含未识别球队");
            if (group != (division ? team.Division : team.Conference)) throw new SourceFormatException("球队排名分组异常");
            var rank = Integer(Text(cells[0])); var wins = Integer(Text(cells[2])); var losses = Integer(Text(cells[3]));
            var played = wins + losses > 0;
            rows.Add(new(team, rank, wins, losses, played ? Number(Text(cells[4])) : null, Number(Text(cells[5])), Text(cells[6]), Text(cells[7]),
                played ? Number(Text(cells[10])) : null, played ? Number(Text(cells[11])) : null, played ? Number(Text(cells[12])) : null, played ? Text(cells[13]) : "尚未开赛"));
        }
        if (rows.Count != 30 || rows.Select(r => r.Team.Key).Distinct().Count() != 30 ||
            rows.GroupBy(r => division ? r.Team.Division : r.Team.Conference).Any(g => !g.Select(r => r.Rank).SequenceEqual(Enumerable.Range(1, division ? 5 : 15))))
            throw new SourceFormatException("排名数据不完整");
        return new(season, rows, fetched);
    }
    public static IReadOnlyDictionary<string, int> Divisions(string html, TeamStandingsSnapshot standings, DateTimeOffset now)
    {
        var divided = Standings(html, now, true);
        if (divided.Season != standings.Season) throw new SourceFormatException("赛区排名赛季不一致");
        // Different page caches may update at different times. Never attach an old division rank to a new record.
        return divided.Rows.Where(d => standings.Rows.Any(r => r.Team.Key == d.Team.Key && r.Wins == d.Wins && r.Losses == d.Losses))
            .ToDictionary(d => d.Team.Key, d => d.Rank);
    }
    public static IReadOnlyDictionary<string, TeamAverages> Averages(string html, TeamStandingsSnapshot standings)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var rows = new Dictionary<string, TeamAverages>();
        // This table has no season label. Require all 30 scoring averages to agree with the explicitly dated standings.
        // A table from a previous season, or partially refreshed table, is rejected as a whole.
        var headers = doc.QuerySelector("#data_js_sort") ?? throw new SourceFormatException("场均统计格式异常");
        var expected = new[]{("投篮","2"),("三分","5"),("罚球","8"),("篮板","11"),("助攻","14"),("失误","15"),("抢断","16"),("盖帽","17"),("犯规","18"),("得分","19")};
        if(expected.Any(h=>headers.QuerySelector("s[atr='"+h.Item2+"']")?.ParentElement is not {} header||Text(header)!=h.Item1))
            throw new SourceFormatException("场均统计列信息已变化，等待适配");
        foreach (var tr in headers.QuerySelectorAll("tr"))
        {
            var cells = tr.Children.ToArray(); if (cells.Length != 20 || cells[1].QuerySelector("a") is not { } link) continue;
            var slug = Regex.Match(link.GetAttribute("href") ?? "", @"/teams/([a-z0-9-]+)").Groups[1].Value;
            var team = TeamCatalog.Resolve(slug) ?? throw new SourceFormatException("场均统计包含未识别球队");
            var standing = standings.Rows.Single(r => r.Team.Key == team.Key);
            var points = Number(Text(cells[19]));
            if (standing.Games > 0 && (standing.Points is null || points is null || Math.Abs(points.Value - standing.Points.Value) > .11))
                throw new SourceFormatException("场均统计与本赛季排名尚未同步");
            double? V(int col) => standing.Games > 0 ? Number(Text(cells[col])) : null;
            if (rows.ContainsKey(team.Key)) throw new SourceFormatException("场均球队重复");
            rows[team.Key] = new(team.Key, new TeamMetric[] {
                new("points","得分",standing.Points),new("opponent","失分",standing.OpponentPoints),new("differential","净胜分",standing.Differential),
                new("rebounds","篮板",V(11)),new("assists","助攻",V(14)),new("fieldGoal","投篮",V(2),true),
                new("threePoint","三分",V(5),true),new("freeThrow","罚球",V(8),true),new("turnovers","失误",V(15)),
                new("offensiveRebounds","前场篮板",V(12)),new("defensiveRebounds","后场篮板",V(13)),new("steals","抢断",V(16)),new("blocks","盖帽",V(17)),new("fouls","犯规",V(18)) });
        }
        if (rows.Count != 30 || standings.Rows.Count(r => r.Games > 0) < 2) throw new SourceFormatException("本赛季场均统计暂未核验");
        return rows;
    }
    private static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    private static string Str(JsonElement e, string key) => Get(e, key) is var v && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    private static JsonDocument Document(string json)
    {
        var d = JsonDocument.Parse(json);
        if (Get(d.RootElement,"success").ValueKind == JsonValueKind.True && Get(Get(d.RootElement,"result"),"gameList").ValueKind == JsonValueKind.Array) return d;
        d.Dispose(); throw new SourceFormatException("球队赛程数据异常");
    }
    private static JsonElement.ArrayEnumerator Matches(JsonElement group)
    {
        var matches=Get(group,"matchList");if(matches.ValueKind!=JsonValueKind.Array)throw new SourceFormatException("赛程分组格式异常");return matches.EnumerateArray();
    }
    public static IReadOnlyDictionary<string, string> TeamIds(string json)
    {
        using var d = Document(json); var result = new Dictionary<string, string>();
        foreach (var group in Get(Get(d.RootElement,"result"),"gameList").EnumerateArray())
        foreach (var match in Matches(group))
        foreach (var side in new[]{"home","away"})
        {
            var team = TeamCatalog.Resolve(Str(match,side+"TeamName")); var id = Str(match,side+"TeamId");
            if (team is null || id.Length == 0 || !id.All(RuntimeCompat.IsAsciiDigit)) continue;
            if (result.TryGetValue(team.Key,out var previous) && previous != id) throw new SourceFormatException("球队编号不唯一");
            result[team.Key] = id;
        }
        return result;
    }
    public static TeamOverviewSnapshot Schedule(string json, TeamIdentity team, string teamId, string season, DateTimeOffset fetched)
    {
        using var d = Document(json); var games = new Dictionary<string, TeamGame>();
        foreach (var group in Get(Get(d.RootElement,"result"),"gameList").EnumerateArray())
        foreach (var item in Matches(group))
        {
            if (Str(item,"competitionStageType") != "REGULAR" || Str(item,"season") != season || Str(item,"leagueType") != "NBA") continue;
            var home = Str(item,"homeTeamId") == teamId; var away = Str(item,"awayTeamId") == teamId;
            if (home == away) continue;
            if (TeamCatalog.Resolve(Str(item, home ? "homeTeamName" : "awayTeamName"))?.Key != team.Key) throw new SourceFormatException("赛程球队编号不匹配");
            var opponent = TeamCatalog.Resolve(Str(item, home ? "awayTeamName" : "homeTeamName"));
            if (opponent is null) continue;
            var id = Str(item,"matchId");
            if (id.Length == 0 || !id.All(RuntimeCompat.IsAsciiDigit)) throw new SourceFormatException("赛程编号异常");
            if (!DateTime.TryParseExact(Str(item,"matchTime"),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)) continue;
            var status = Str(item,"matchStatus") switch { "COMPLETED" or "FINISHED" => GameStatus.Finished,"NOTSTARTED" or "NOT_STARTED" => GameStatus.Scheduled,"INPROGRESS" or "IN_PROGRESS" or "ONGOING" or "PLAYING" => GameStatus.Live,_ => GameStatus.Unknown };
            if (status == GameStatus.Unknown) continue;
            int? Score(string side) => int.TryParse(Str(item,side+"Score"),NumberStyles.None,CultureInfo.InvariantCulture,out var score) ? score : null;
            games[id] = new(id,season,team,opponent,home,new(DateTime.SpecifyKind(date,DateTimeKind.Unspecified),TimeSpan.FromHours(8)),status,
                status == GameStatus.Scheduled ? null : Score(home?"home":"away"),status == GameStatus.Scheduled ? null : Score(home?"away":"home"));
        }
        return new(team,season,games.Values.OrderBy(g=>g.Tipoff).ThenBy(g=>g.Id,StringComparer.Ordinal).ToArray(),fetched);
    }
}
