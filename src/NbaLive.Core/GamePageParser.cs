using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;
using System.Globalization;

namespace NbaLive.Core;

/// <summary>Parses the public NBA desktop pages; never executes site scripts.</summary>
public sealed class GamePageParser
{
    private static string Text(IElement? element) => LiveEntry.Normalize(element?.TextContent ?? "");
    private static int? Score(IElement? element) => int.TryParse(Text(element), out var value) ? value : null;

    public IReadOnlyList<GameInfo> ParseGames(string html, DateOnly date)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        if (doc.QuerySelector(".gamecenter_livestart") is null)
            throw new SourceFormatException("比赛列表结构无法识别，可能是网页变更或访问验证。");
        var games = new List<GameInfo>();
        foreach (var box in doc.QuerySelectorAll(".gamecenter_content_l .list_box"))
        {
            var link = box.QuerySelector("a[href*='/games/playbyplay/'], a[href*='/games/boxscore/'], a[href*='/games/preview/']");
            var id = Regex.Match(link?.GetAttribute("href") ?? "", @"/games/(?:playbyplay|boxscore|preview)/(\d+)").Groups[1].Value;
            var away = box.QuerySelector(".team_vs_a_1");
            var home = box.QuerySelector(".team_vs_a_2");
            var awayName = Text(away?.QuerySelector(".txt a"));
            var homeName = Text(home?.QuerySelector(".txt a"));
            if (id.Length == 0 || awayName.Length == 0 || homeName.Length == 0)
                throw new SourceFormatException("发现比赛卡片，但无法读取球队或比赛编号。");
            var progress = Text(box.QuerySelector(".team_vs_b"));
            progress = Regex.Replace(progress, @"未开始\s*(?=\d)", "未开始 ");
            games.Add(new(id, awayName, homeName, Score(away?.QuerySelector(".num")),
                Score(home?.QuerySelector(".num")), Status(progress), progress, date));
        }
        if (games.Count == 0 && !Text(doc.Body).Contains("0场比赛"))
            throw new SourceFormatException("未识别到比赛卡片，也没有找到无比赛提示。");
        return games.DistinctBy(g => g.Id).ToArray();
    }

    public LiveSnapshot ParseSnapshot(string html, string gameId, DateTimeOffset fetchedAt)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var away = doc.QuerySelector(".team_a .message");
        var home = doc.QuerySelector(".team_b .message");
        var awayName = Text(away?.QuerySelector("p a"));
        var homeName = Text(home?.QuerySelector("p a"));
        if (awayName.Length == 0 || homeName.Length == 0)
            throw new SourceFormatException("无法读取比赛球队，可能是网页变更或访问验证。");
        var scriptId = Regex.Match(html, "match_id:\\s*[\"'](\\d+)").Groups[1].Value;
        if (scriptId.Length != 0 && scriptId != gameId)
            throw new SourceFormatException("返回的比赛编号与选择不符。");
        var nav = doc.QuerySelectorAll(".table_list_c li").FirstOrDefault(li =>
            li.QuerySelector($"a[href$='/{gameId}']") is not null);
        var progress = Text(nav?.QuerySelector("p"));
        var state = Status(progress);
        var entries = new List<LiveEntry>();
        var rows = doc.QuerySelectorAll(".table_overflow tr");
        // On archived pages rows are chronological; some live pages put newest first.
        var withIds = rows.Select(r => long.TryParse(r.Id, out var id) ? id : 0).Where(id => id > 0).ToArray();
        var orderedRows = withIds.Length > 1 && withIds[0] > withIds[^1] ? rows.Reverse() : rows.AsEnumerable();
        var period = 1;
        long fallbackOrder = 0;
        foreach (var row in orderedRows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length == 0) continue;
            var text = Text(cells.Length >= 3 ? cells[2] : cells[0]);
            if (text.Length == 0) continue;
            var inferred = Period(text);
            if (inferred > 0) period = inferred;
            var clock = cells.Length >= 3 ? Text(cells[0]) : "";
            var team = cells.Length >= 3 ? Text(cells[1]) : "";
            var score = cells.Length >= 4 ? Text(cells[3]) : "";
            var order = long.TryParse(row.Id, out var sequence) ? sequence : ++fallbackOrder;
            entries.Add(new(string.IsNullOrWhiteSpace(row.Id) ? null : row.Id, period, clock, team, text, score, order));
            if (text.Contains("比赛结束") || text.Contains("全场结束")) state = GameStatus.Finished;
        }
        if (doc.QuerySelector(".table_overflow") is null && state != GameStatus.Scheduled && state != GameStatus.Cancelled)
            throw new SourceFormatException("文字记录区域缺失。");
        var awayScore = Score(away?.QuerySelector("h2"));
        var homeScore = Score(home?.QuerySelector("h2"));
        if (state == GameStatus.Live && (awayScore is null || homeScore is null))
            throw new SourceFormatException("进行中的比赛缺少比分。");
        if (progress.Length == 0) progress = Text(doc.QuerySelector(".team_num"));
        if (state == GameStatus.Unknown) state = Status(progress);
        // is_live=0 also means pregame, so do not treat it as a final score flag.
        var dateMatch = Regex.Match(Text(doc.QuerySelector(".time_f")), @"(\d{4})年(\d{1,2})月(\d{1,2})日");
        var date = MatchTime.Today;
        if (dateMatch.Success && !DateOnly.TryParseExact(dateMatch.Value, "yyyy年M月d日", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            throw new SourceFormatException("比赛日期无法识别。");
        if (state == GameStatus.Finished) progress = "已结束";
        else if (progress.Length == 0 && entries.Count > 0) progress = $"{entries[^1].PeriodLabel} {entries[^1].Clock}".Trim();
        var game = new GameInfo(gameId, awayName, homeName, awayScore, homeScore, state, progress, date);
        return new(game, entries.DistinctBy(e => e.Key).ToArray(), fetchedAt);
    }

    public static GameStatus Status(string text) => text switch
    {
        _ when text.Contains("已结束") || text.Contains("全场结束") => GameStatus.Finished,
        _ when text.Contains("未开始") || text.Contains("赛前") => GameStatus.Scheduled,
        _ when text.Contains("取消") || text.Contains("延期") => GameStatus.Cancelled,
        _ when text.Contains("进行中") || text.Contains("节") || text.Contains("加时") || text.Contains("中场") => GameStatus.Live,
        _ => GameStatus.Unknown
    };

    private static int Period(string text)
    {
        var regular = Regex.Match(text, @"第([一二三四1234])节");
        if (regular.Success) return "一二三四".IndexOf(regular.Groups[1].Value, StringComparison.Ordinal) is var n && n >= 0
            ? n + 1 : int.Parse(regular.Groups[1].Value);
        var overtime = Regex.Match(text, @"(?:第([一二三四五六七八九\d]+)个?加时|加时(?:赛)?\s*([一二三四五六七八九\d]*))");
        if (!overtime.Success) return 0;
        var value = overtime.Groups[1].Value + overtime.Groups[2].Value;
        if (value.Length == 0) return 5;
        return 4 + (int.TryParse(value, out var number) ? number : "一二三四五六七八九".IndexOf(value, StringComparison.Ordinal) + 1);
    }
}
