using NbaLive.Core;

internal static class ScoringTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Independent score feed validates identity, period totals and score timeline", () => Sync(() =>
        {
            var game = new GameInfo("1537445630224367616", "勇士", "马刺", 10, 36, GameStatus.Live, "第一节剩56秒", new DateOnly(2026, 11, 14));
            const string board = """{"events":[{"id":"401909302","competitions":[{"date":"2026-11-14T02:30Z","competitors":[{"id":"24","homeAway":"home"},{"id":"9","homeAway":"away"}]}]}]}""";
            Check(GameScoreSource.FindGames(board,game).SequenceEqual(new[]{"401909302"}), "exact teams and Beijing date match");
            Check(GameScoreSource.FindGames(board,game with { Home="湖人" }).Count == 0, "different team rejected");
            Check(GameScoreSource.FindGames(board,game with { Date=game.Date.AddDays(1) }).Count == 0, "different date rejected");
            const string summary = """{"header":{"id":"401909302","competitions":[{"id":"401909302","date":"2026-11-14T02:30Z","competitors":[{"id":"24","homeAway":"home","score":"36","linescores":[{"displayValue":"36"}]},{"id":"9","homeAway":"away","score":"10","linescores":[{"displayValue":"10"}]}]}]},"plays":[{"id":"2","sequenceNumber":"2","period":{"number":1},"clock":{"displayValue":"56.9"},"awayScore":10,"homeScore":36},{"id":"1","sequenceNumber":"1","period":{"number":1},"clock":{"displayValue":"12:00"},"awayScore":0,"homeScore":0}]}""";
            var score = GameScoreSource.Parse(summary,game,"401909302");
            Check(score.Periods[0].Away == 10 && score.Periods[0].Home == 36 && score.Periods[1].Away is null, "quarter data with future period missing");
            Check(score.Points.Count==2 && score.Points[1].Clock=="56.9秒" && score.Points[1].Home==36, "ordered points and subminute clock");
            var rejected=false; try { GameScoreSource.Parse(summary,game with { Home="湖人" },"401909302"); } catch(SourceFormatException) { rejected=true; }
            Check(rejected,"summary identity mismatch rejected");
        })),
        ("Live 76ers Nets feed keeps all four periods and period end reviews in order", () => Sync(() =>
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "score-76ers-nets-20261117.json"));
            var game = new GameInfo("legacy-id", "76人", "篮网", 110, 103, GameStatus.Live, "第四节", new DateOnly(2026, 11, 17));
            var score = GameScoreSource.Parse(json, game, "401909997");
            Check(score.Periods.Select(p => p.Away).SequenceEqual(new int?[] { 33, 45, 27, 7 }), "away quarter scores");
            Check(score.Periods.Select(p => p.Home).SequenceEqual(new int?[] { 26, 33, 36, 11 }), "home quarter scores");
            Check(score.Points.Any(p => p.Period == 2 && p.Away > 33 && p.Away < 78), "second period has actual scoring points");
            Check(score.Points.Any(p => p.Period == 4 && p.ElapsedSeconds < 2880 && p.Away == 112), "fourth period stays in the fourth quarter after late third-quarter review");
            Check(!score.HasGaps && !score.ScoreMismatch, "full sample is contiguous and agrees with its own totals");
            Check(score.AtOrBefore(GameScoring.PeriodStart(3)) is { Away: 78, Home: 59 }, "third quarter starts from the second quarter final");
        })),
        ("Archived cumulative scores reconstruct all quarters and the final score", () => Sync(() =>
        {
            var snapshot = new GamePageParser().ParseSnapshot(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "finished-168863.html")), "168863", DateTimeOffset.UtcNow);
            var scoring = GameScoring.Build(snapshot.Game, snapshot.Entries);
            Check(scoring.Periods.Select(p => p.Away).SequenceEqual(new int?[] { 23, 31, 29, 23 }), "away quarters sum to 106: " + string.Join(", ", scoring.Periods.Select(p => $"{p.Period}={p.Away}/{p.Home}")));
            Check(scoring.Periods.Select(p => p.Home).SequenceEqual(new int?[] { 25, 33, 18, 24 }), "home quarters sum to 100");
            Check(scoring.Points[0].Away == 0 && scoring.Points[^1].Away == 106 && scoring.Points[^1].ElapsedSeconds == 2880 && !scoring.ScoreMismatch, "actual orientation and final timeline");
            Check(scoring.Points.Zip(scoring.Points.Skip(1)).All(p => p.First.ElapsedSeconds <= p.Second.ElapsedSeconds), "clock reset on preamble lineup never rewinds the chart");
            Check(scoring.Points.Any(p => p.Clock == "7.0\"" && p.Away == 106), "source sub-minute seconds notation retains final free throws");
        })),
        ("Quarter boundaries and overtime use twelve and five minute clocks", () => Sync(() =>
        {
            var entries = new[] { E(1, 1, "12:00", "0-0"), E(2, 1, "00:00", "20-22"), E(3, 2, "12:00", "20-22"), E(4, 2, "00:00", "45-40"), E(5, 3, "12:00", "45-40"), E(6, 3, "00:00", "70-70"), E(7, 4, "12:00", "70-70"), E(8, 4, "00:00", "90-90"), E(9, 5, "05:00", "90-90"), E(10, 5, "00:00", "100-100"), E(11, 6, "05:00", "100-100"), E(12, 6, "02:30.5", "105-102") };
            var score = GameScoring.Build(Game(105, 102, "第二个加时 02:30"), entries);
            Check(score.Periods.Count == 6 && score.Periods[4].Away == 10 && score.Periods[5].Away == 5, "each overtime has its own noncumulative score");
            Check(score.Points[^1].ElapsedSeconds == 3329.5 && score.DurationSeconds == 3480, "overtime uses five minutes and supports fractional clocks");
        })),
        ("Late scoring entries and corrected identities rebuild without double counting", () => Sync(() =>
        {
            var entries = new[] { E(1, 1, "12:00", "0-0"), E(3, 1, "11:00", "5-0"), E(2, 1, "11:30", "2-0"), E(3, 1, "11:00", "4-0"), E(4, 1, "11:00", "4-1"), E(5, 1, "11:00", "4-2") };
            var score = GameScoring.Build(Game(4, 2, "第一节 11:00"), entries);
            Check(score.Points.Count == 5 && score.AtOrBefore(60) is { Away: 4, Home: 2 }, "correction replaces identity and same-clock free throws resolve to last source order");
            Check(score.AtOrBefore(45) is { Away: 2, Home: 0 } && score.Periods[0].Away == 4, "late event takes its actual chronology, not arrival time");
        })),
        ("Partial histories don't invent missing quarters or join gaps", () => Sync(() =>
        {
            var score = GameScoring.Build(Game(70, 60, "第三节 06:00"), [E(1, 2, "08:00", "30-25"), E(2, 3, "06:00", "70-60")]);
            Check(score.HasGaps && score.Periods.All(p => p.Away is null && p.Home is null) && score.Points[1].BreakBefore, "missing boundary stays unknown");
            Check(score.AtOrBefore(100) is null && score.AtOrBefore(1500) is null, "hover cannot claim a score before known data or across a gap");
            Check(score.Points[0].ElapsedSeconds == 960, "no fabricated zero-to-thirty ramp");
            var skipped = GameScoring.Build(Game(50, 45, "第三节 12:00"), [E(1, 1, "12:00", "0-0"), E(2, 1, "00:00", "20-20"), E(3, 3, "12:00", "50-45")]);
            Check(skipped.Points[^1].BreakBefore && skipped.AtOrBefore(1000) is null, "a known later boundary cannot draw a fabricated entire missing quarter");
        })),
        ("Score corrections descend while malformed scores and clocks remain missing", () => Sync(() =>
        {
            var score = GameScoring.Build(Game(4, 2, "第一节 10:00"), [E(1, 1, "12:00", "0-0"), E(2, 1, "11:30", "5-2"), E(3, 1, "11:00", "4-2"), E(4, 1, "12:99", "6-2"), E(5, 1, "10:00", "4-2")]);
            Check(score.Points[2].Away == 4 && score.Points[2].Away < score.Points[1].Away, "official reductions aren't clamped into fake monotonically growing scores");
            Check(score.HasGaps && score.Points[^1].BreakBefore && score.Points.All(p => p.Away != 6), "invalid time doesn't place a scoring event");
            var empty = GameScoring.Build(Game(null, null, "未开始") with { Status = GameStatus.Scheduled }, []);
            Check(empty.Points.Count == 0 && empty.Periods.Count == 4 && empty.Periods.All(p => p.Away is null), "pregame shows dashes instead of invented zeros");
        })),
        ("Latest totals, period end markers and new games preserve data boundaries", () => Sync(() =>
        {
            var entries = new[] { E(1, 1, "12:00", "0-0"), E(2, 1, "00:05", "20-18"), E(3, 1, "", "", "第一节结束。"), E(4, 1, "12:00", "20-18", "换人"), E(5, 2, "12:00", "20-18"), E(6, 2, "10:00", "22-20") };
            var score = GameScoring.Build(Game(24, 20, "第二节 10:00"), entries);
            Check(score.Periods[0] is { Away: 20, Home: 18, IsComplete: true } && score.Periods[1].Away == 2 && score.ScoreMismatch, "period end carries its last published score, totals ahead of text are disclosed");
            var next = GameScoring.Build(Game(0, 0, "第一节 12:00") with { Id = "next" }, []);
            Check(next.Points.Count == 0 && next.Game.Id == "next", "builder has no state to leak scores across games");
        }))
    ];
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static GameInfo Game(int? away, int? home, string progress) => new("1", "湖人", "勇士", away, home, GameStatus.Live, progress, MatchTime.Today);
    private static LiveEntry E(int order, int period, string clock, string score, string text = "比分记录") => new(order.ToString(), period, clock, "", text, score, order);
}
