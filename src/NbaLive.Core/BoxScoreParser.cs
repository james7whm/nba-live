using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public sealed class BoxScoreParser
{
    private static string Text(IElement? element) => LiveEntry.Normalize(element?.TextContent ?? "");
    private static int? Number(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "—" or "--" or "-" or "N/A") return null;
        if (int.TryParse(value.Replace('−', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        throw new SourceFormatException("球员统计数值无法识别。");
    }

    public BoxScoreSnapshot Parse(string html, string gameId, DateTimeOffset fetchedAt)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var sourceId = Regex.Match(html, "match_id:\\s*[\"'](\\d+)").Groups[1].Value;
        if (sourceId.Length > 0 && sourceId != gameId) throw new SourceFormatException("统计场次与选择不符。");
        var away = doc.QuerySelector(".team_a .message");
        var home = doc.QuerySelector(".team_b .message");
        var awayName = Text(away?.QuerySelector("p a"));
        var homeName = Text(home?.QuerySelector("p a"));
        if (awayName.Length == 0 || homeName.Length == 0) throw new SourceFormatException("无法读取统计场次的球队。");
        var nav = doc.QuerySelectorAll(".table_list_c li").FirstOrDefault(li => li.QuerySelector($"a[href$='/{gameId}']") is not null);
        var progress = Text(nav?.QuerySelector("p"));
        if (sourceId.Length == 0 && nav is null) throw new SourceFormatException("无法确认统计场次编号。");
        if (progress.Length == 0) progress = Text(doc.QuerySelector(".team_num"));
        var status = GamePageParser.Status(progress);
        var date = MatchTime.Today;
        var dateMatch = Regex.Match(Text(doc.QuerySelector(".time_f")), @"\d{4}年\d{1,2}月\d{1,2}日");
        if (dateMatch.Success && !DateOnly.TryParseExact(dateMatch.Value, "yyyy年M月d日", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            throw new SourceFormatException("统计场次日期无法识别。");
        var game = new GameInfo(gameId, awayName, homeName, Number(Text(away?.QuerySelector("h2"))),
            Number(Text(home?.QuerySelector("h2"))), status, progress, date);
        if (status == GameStatus.Live && (game.AwayScore is null || game.HomeScore is null))
            throw new SourceFormatException("进行中的统计场次缺少比分。");
        var awayTable = doc.QuerySelector("#J_away_content");
        var homeTable = doc.QuerySelector("#J_home_content");
        if (awayTable is null || homeTable is null)
        {
            if (status is GameStatus.Scheduled or GameStatus.Cancelled) return BoxScoreSnapshot.Waiting(game, fetchedAt);
            throw new SourceFormatException("球员统计表格缺失。");
        }
        var result = new BoxScoreSnapshot(game, ParseTeam(awayTable, awayName), ParseTeam(homeTable, homeName), fetchedAt);
        // An intact header with no published rows is a waiting state, never invented zero statistics.
        return result;
    }

    private static TeamStatistics ParseTeam(IElement table, string team)
    {
        var players = new List<PlayerStatistics>();
        var ids = new HashSet<string>();
        Dictionary<string, int>? columns = null;
        PlayerStatistics? total = null;
        var starter = false;
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.Children.Where(c => c.LocalName is "td" or "th").ToArray();
            if (cells.Length == 0) continue;
            var first = Text(cells[0]);
            if (first is "首发" or "替补")
            {
                starter = first == "首发";
                columns = cells.Select((cell, index) => (Label: Text(cell), Index: index))
                    .Where(x => x.Label.Length > 0).GroupBy(x => x.Label).ToDictionary(g => g.Key, g => g.First().Index);
                foreach (var required in new[] { "时间", "投篮", "3分", "罚球", "前场", "后场", "篮板", "助攻", "犯规", "抢断", "失误", "封盖", "得分", "+/-" })
                    if (!columns.ContainsKey(required)) throw new SourceFormatException("球员统计列结构无法识别。");
                continue;
            }
            if (first == "命中率") continue;
            var link = cells[0].QuerySelector("a[href*='/players/']");
            if (link is null && first is not ("统计" or "合计")) continue;
            if (columns is null) throw new SourceFormatException("球员统计缺少列标题。");
            var id = first is "统计" or "合计" ? "total" : link!.GetAttribute("href")!.Split('/').Last();
            var name = link is null ? "球队合计" : Text(link);
            if (name.Length == 0) throw new SourceFormatException("球员姓名缺失。");
            var dnp = string.Join(" ", cells.Skip(1).Select(Text));
            if (link is not null && (dnp.Contains("未出场") || dnp.Contains("未上场") || dnp.Contains("没有上场") || Regex.IsMatch(dnp, @"\bDNP\b", RegexOptions.IgnoreCase)))
            {
                if (ids.Add(id)) players.Add(new(id, name, starter, "", "—", "—", "—", "—", null, null, null, null, null, null, null, null, null, null, dnp));
                continue;
            }
            string Value(string label)
            {
                var index = columns[label];
                if (index >= cells.Length || cells.Any(c => c.HasAttribute("colspan"))) throw new SourceFormatException("球员统计行不完整。");
                var value = Text(cells[index]);
                return value.Length == 0 ? "—" : value;
            }
            var player = new PlayerStatistics(id, name, starter, cells.Length > 1 ? Text(cells[1]) : "", Value("时间"),
                Value("投篮"), Value("3分"), Value("罚球"), Number(Value("前场")), Number(Value("后场")),
                Number(Value("篮板")), Number(Value("助攻")), Number(Value("犯规")), Number(Value("抢断")),
                Number(Value("失误")), Number(Value("封盖")), Number(Value("得分")), Number(Value("+/-")));
            if (id == "total") total = player;
            else if (ids.Add(id)) players.Add(player);
        }
        if (columns is null) throw new SourceFormatException("球员统计缺少可识别的标题。");
        return new(team, players, total);
    }
}
