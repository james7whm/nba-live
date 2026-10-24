using System.Globalization;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public sealed record ScorePoint(int Period, string Clock, double ElapsedSeconds, int Away, int Home, bool BreakBefore = false)
{
    public string TimeLabel => $"{GameScoring.PeriodLabel(Period)} {Clock}";
}
public sealed record PeriodScore(int Period, int? Away, int? Home, bool IsComplete)
{
    public string Label => GameScoring.PeriodLabel(Period);
}
public sealed record GameScoringSnapshot(GameInfo Game, IReadOnlyList<PeriodScore> Periods,
    IReadOnlyList<ScorePoint> Points, bool HasGaps, bool ScoreMismatch)
{
    public double DurationSeconds => GameScoring.PeriodStart(Periods.Count) + GameScoring.PeriodLength(Periods.Count);
    // Upper bound makes several free throws at the same clock resolve to the final recorded score.
    public ScorePoint? AtOrBefore(double elapsed)
    {
        if (Points.Count == 0 || elapsed < Points[0].ElapsedSeconds) return null;
        var low = 0; var high = Points.Count;
        while (low < high) { var middle = (low + high) / 2; if (Points[middle].ElapsedSeconds <= elapsed) low = middle + 1; else high = middle; }
        if (low < Points.Count && Points[low].BreakBefore && elapsed > Points[low - 1].ElapsedSeconds) return null;
        return Points[low - 1];
    }
}

/// <summary>Reconstructs recorded cumulative scores, never interpolating a missing quarter boundary.</summary>
public static class GameScoring
{
    public static string PeriodLabel(int period) => period <= 4 ? $"第{period}节" : $"加时{period - 4}";
    public static int PeriodLength(int period) => period <= 4 ? 720 : 300;
    public static int PeriodStart(int period) => period <= 4 ? (period - 1) * 720 : 2880 + (period - 5) * 300;
    private static bool Score(string text, out (int Away, int Home) score)
    {
        var match = Regex.Match(text, @"^\s*(\d{1,3})\s*[-:：－–—]\s*(\d{1,3})\s*$");
        score = match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) : default;
        return match.Success;
    }
    private static bool Clock(string clock, int period, out double remaining)
    {
        remaining = 0;
        var seconds = Regex.Match(clock, @"^(\d{1,2}(?:\.\d{1,3})?)[""″秒]$");
        if (seconds.Success) return double.TryParse(seconds.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out remaining) && remaining < 60;
        var match = Regex.Match(clock, @"^(\d{1,2}):([0-5]\d)(?:\.(\d{1,3}))?$");
        if (!match.Success) return false;
        remaining = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (match.Groups[3].Success) remaining += double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture);
        return remaining <= PeriodLength(period);
    }
    public static GameScoringSnapshot Build(GameInfo game, IReadOnlyList<LiveEntry> entries)
    {
        var points = new List<ScorePoint>();
        var starts = new Dictionary<int, (int Away, int Home)> { [1] = (0, 0) };
        var ends = new Dictionary<int, (int Away, int Home)>();
        var latest = new Dictionary<int, (int Away, int Home)>();
        var complete = new HashSet<int>();
        var uncertain = new HashSet<int>();
        var gaps = false; var breakNext = false; var pendingPeriod = 1; var seenPeriod = 0;
        void Add(int period, string clock, double elapsed, (int Away, int Home) score, bool boundary = false)
        {
            if (points.Count > 0 && elapsed < points[^1].ElapsedSeconds)
            {
                // Before a quarter-start marker, the source sometimes labels the new lineup as the old quarter.
                if (points[^1].Away != score.Away || points[^1].Home != score.Home) { gaps = breakNext = true; uncertain.Add(period); }
                return;
            }
            var disconnected = breakNext || points.Count > 0 && period > points[^1].Period &&
                (!starts.ContainsKey(period) || period > points[^1].Period + 1 || points[^1].ElapsedSeconds < PeriodStart(period) && (points[^1].Away != score.Away || points[^1].Home != score.Home));
            if (points.Count == 0 && (elapsed > 0 || score != (0, 0))) gaps = true;
            if (disconnected) gaps = true;
            points.Add(new(period, clock, elapsed, score.Away, score.Home, disconnected));
            latest[period] = score;
            breakNext = false;
            if (boundary) ends[period] = score;
        }
        foreach (var entry in entries.GroupBy(e => e.Key).Select(g => g.Last()).OrderBy(e => e.Order))
        {
            if (entry.Period is < 1 or > 20) { gaps = breakNext = true; continue; }
            var endMarker = (entry.Text.Contains("结束", StringComparison.Ordinal) || entry.Text.Contains("完毕", StringComparison.Ordinal)) &&
                (GamePageParser.Period(entry.Text) > 0 || entry.Text.Contains("比赛结束", StringComparison.Ordinal) || entry.Text.Contains("全场结束", StringComparison.Ordinal));
            var period = endMarker ? entry.Period : Math.Max(entry.Period, pendingPeriod);
            if (period > 20) continue;
            if (endMarker)
            {
                seenPeriod = Math.Max(seenPeriod, period);
                if (Score(entry.Score, out var closing) || latest.TryGetValue(period, out closing) && !uncertain.Contains(period))
                {
                    Add(period, "00:00", PeriodStart(period) + PeriodLength(period), closing, boundary: true);
                    complete.Add(period);
                }
                pendingPeriod = Math.Max(pendingPeriod, period + 1);
                continue;
            }
            if (!Score(entry.Score, out var score))
            {
                if (entry.Clock.Length > 0) { gaps = breakNext = true; uncertain.Add(period); }
                continue;
            }
            if (!Clock(entry.Clock, period, out var remaining))
            {
                if (entry.Clock.Length > 0) { gaps = breakNext = true; uncertain.Add(period); }
                continue;
            }
            seenPeriod = Math.Max(seenPeriod, period);
            if (remaining == PeriodLength(period))
            {
                // Ignore old-quarter labels on duplicate preamble rows; the explicit next start supplies the boundary.
                if (points.Count > 0 && PeriodStart(period) < points[^1].ElapsedSeconds) continue;
                if (period > 1) starts[period] = score;
                if (period > 1) { ends[period - 1] = score; complete.Add(period - 1); }
            }
            else if (complete.Contains(period - 1) && ends.TryGetValue(period - 1, out var previousEnd)) starts.TryAdd(period, previousEnd);
            Add(period, entry.Clock, PeriodStart(period) + PeriodLength(period) - remaining, score);
        }
        var currentPeriod = RuntimeCompat.Clamp(Math.Max(Math.Max(seenPeriod, GamePageParser.Period(game.Progress)), game.Status == GameStatus.Finished ? 4 : 1), 1, 20);
        if (game.Status == GameStatus.Finished && points.Count > 0 && points[^1].Period == currentPeriod &&
            points[^1].Away == game.AwayScore && points[^1].Home == game.HomeScore && !uncertain.Contains(currentPeriod))
        {
            Add(currentPeriod, "00:00", PeriodStart(currentPeriod) + PeriodLength(currentPeriod), (points[^1].Away, points[^1].Home), boundary: true);
            complete.Add(currentPeriod);
        }
        var periods = Enumerable.Range(1, Math.Max(4, currentPeriod)).Select(period =>
        {
            var knownStart = starts.TryGetValue(period, out var start);
            var knownEnd = ends.TryGetValue(period, out var end);
            if (!knownEnd && period == currentPeriod) knownEnd = latest.TryGetValue(period, out end) && !uncertain.Contains(period);
            int? away = knownStart && knownEnd && end.Away >= start.Away ? end.Away - start.Away : null;
            int? home = knownStart && knownEnd && end.Home >= start.Home ? end.Home - start.Home : null;
            return new PeriodScore(period, away, home, complete.Contains(period));
        }).ToArray();
        var mismatch = points.Count > 0 && (game.AwayScore is { } awayScore && awayScore != points[^1].Away || game.HomeScore is { } homeScore && homeScore != points[^1].Home);
        return new(game, periods, points, gaps || points.Count > 0 && periods.Where(p => p.Period <= currentPeriod).Any(p => p.Away is null || p.Home is null), mismatch);
    }
}
