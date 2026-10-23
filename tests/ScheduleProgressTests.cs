using NbaLive.Core;
using System.Net;

internal static class ScheduleProgressTests
{
    private static readonly DateOnly Date = new(2026, 10, 22);
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Real mixed schedule reads live team_vs_c status and remaining clocks", () => Sync(() =>
        {
            var games = new GamePageParser().ParseGames(Fixture(), Date);
            Check(games.Count == 11, "all real games retained");
            var live = games.Where(g => g.Status == GameStatus.Live).ToArray();
            Check(live.Length == 3, "three actual live cards no longer become Unknown");
            Check(live.Single(g => g.Id == "168972") is { AwayScore: 29, HomeScore: 62, Progress: "第二节剩3:09" }, "first live score and clock");
            Check(live.Single(g => g.Id == "168866").Progress == "第二节剩2:28", "Warriors Lakers clock");
            Check(live.Single(g => g.Id == "168973").Progress == "第一节剩2:40", "Kings Clippers clock");
            Check(games.Count(g => g.Status == GameStatus.Finished) == 8 && games.All(g => g.Progress.Length > 0), "finished cards still use their own status");
        })),
        ("Schedule separates live overtime, pregame, final and unknown cards without guessing from scores", () => Sync(() =>
        {
            var html = Page(
                Card("1", "c", "进行中<p>第三节剩08:50</p>"),
                Card("2", "c", "进行中<p>加1剩4:30</p>"),
                Card("3", "c", "进行中<p>第二个加时 02:15</p>"),
                Card("4", "b", "未开始<p>19:30</p>"),
                Card("5", "b", "已结束"), Card("6", "b", "延期"), Card("7", "b", ""));
            var games = new GamePageParser().ParseGames(html, Date);
            Check(games[0] is { Status: GameStatus.Live, Progress: "第三节剩8:50" }, "regular clock normalized");
            Check(games[1] is { Status: GameStatus.Live, Progress: "加1剩4:30" } && games[2].Progress == "加2剩2:15", "multiple overtime labels");
            Check(games[3] is { Status: GameStatus.Scheduled, Progress: "未开始 19:30" }, "pregame retains tip-off time");
            Check(games[4].Status == GameStatus.Finished && games[5].Status == GameStatus.Cancelled && games[6].Status == GameStatus.Unknown, "non-live cards aren't inferred live from nonzero scores");
        })),
        ("Live clock formatting preserves breaks and missing clocks instead of inventing countdowns", () => Sync(() =>
        {
            var examples = new (string Input, string Expected)[]
            {
                ("第三节 08:50", "第三节剩8:50"), ("第4节剩余 00:09", "第四节剩0:09"),
                ("第一个加时剩4:30", "加1剩4:30"), ("加时赛 04:30", "加1剩4:30"),
                ("加2剩0:00", "加2剩0:00"), ("第四节剩0:00.7", "第四节剩0:00.7"), ("中场休息", "中场休息"),
                ("第三节结束", "第三节结束"), ("进行中", "进行中"),
                ("第三节剩15:00", "第三节剩15:00"), ("加1剩7:00", "加1剩7:00"),
                ("第三节剩8:99", "第三节剩8:99"), ("", "")
            };
            foreach (var (input, expected) in examples) Check(GamePageParser.FormatLiveProgress(input) == expected, input);
            Check(GamePageParser.Status("加1剩4:30") == GameStatus.Live, "short overtime identifies live snapshots too");
        })),
        ("Overlay header formats regular quarters and numbered overtime with remaining time", () => Sync(() =>
        {
            GameInfo Live(string progress) => new("1", "客队", "主队", 99, 98, GameStatus.Live, progress, Date);
            Check(OverlayProgress.Format(Live("第4节 11:50")) == "第四节剩11:50", "fourth quarter countdown");
            Check(OverlayProgress.Format(Live("加1剩4:30")) == "加时1剩4:30", "first overtime");
            Check(OverlayProgress.Format(Live("第二个加时 02:15")) == "加时2剩2:15", "numbered overtime");
            Check(OverlayProgress.Format(Live("加1 · 时间未提供")) == "加时1 · 时间未提供", "overtime retains its number when the clock is unavailable");
            Check(OverlayProgress.Format(Live("中场休息")) == "中场休息", "break isn't a countdown");
            Check(OverlayProgress.Format(Live("")) == "进行中 · 时间未提供", "missing clocks aren't invented");
        })),
        ("Overlay finished and pregame statuses do not reuse old game clocks", () => Sync(() =>
        {
            var game = new GameInfo("1", "老鹰", "魔术", 132, 125, GameStatus.Finished, "第四节 11:50", Date);
            Check(OverlayProgress.Format(game) == "已结束", "don't turn an archive into a live clock");
            Check(OverlayProgress.Format(game with { Status = GameStatus.Scheduled, Progress = "未开始 19:30" }) == "未开始 19:30", "scheduled time kept as scheduled");
            Check(OverlayProgress.Format(null) == "", "empty overlay has no phantom game clock");
        })),
        ("Inline history expands upward at floating and corner positions across monitor scales", () => Sync(() =>
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
            foreach (var area in new[] { new WorkArea(0, 0, 1920, 1040), new WorkArea(-1920, -200, 0, 880) })
            {
                var width = (int)(280 * scale); var oldHeight = (int)(110 * scale); var expandedHeight = (int)(320 * scale);
                foreach (var y in new[] { area.Top + 16, area.Top + 400, area.Bottom - oldHeight - 16 })
                {
                    var x = area.Right - width - 16;
                    var expanded = WindowPlacement.ExpandUpward(area, x, y, width, oldHeight, expandedHeight, scale);
                    Check(expanded.X == x && expanded.Y == Math.Max(area.Top, y + oldHeight - expandedHeight), "bottom anchor is stable unless the top work area clamps it");
                    Check(expanded.Y >= area.Top && expanded.Y + expandedHeight <= area.Bottom, "expanded reader remains in visible work area");
                    Check(WindowPlacement.Calculate(area, width, oldHeight, scale, x, y) == new WindowPosition(x, y), "stored compact coordinates can be restored");
                }
            }
        })),
        ("Native HTTP schedule uses the real live-card parser without per-game requests", async () =>
        {
            var handler = new ScheduleHandler();
            using var source = new WebGameSource(handler);
            var games = await source.GetGames(Date, CancellationToken.None);
            Check(handler.Calls == 1 && games.Single(g => g.Id == "168866") is { Status: GameStatus.Live, Progress: "第二节剩2:28" }, "actual source path retains live identity and clock in one read");
        })
    ];
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "games-live-2026-10-22.html"));
    private static string Page(params string[] cards) => "<div class='gamecenter_livestart'><div class='gamecenter_content_l'>" + string.Concat(cards) + "</div></div>";
    private static string Card(string id, string kind, string status) => $"<div class='list_box'><div class='team_vs_a_1'><span class='txt'><a>客队</a></span><span class='num'>92</span></div><div class='team_vs_a_2'><span class='txt'><a>主队</a></span><span class='num'>95</span></div><div class='team_vs_{kind}'><span class='a'></span><span class='b'>{status}</span></div><a href='/games/playbyplay/{id}'>文字直播</a></div>";
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class ScheduleHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Check(request.RequestUri!.AbsolutePath == "/games/2026-10-22", "one schedule endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture()) });
        }
    }
}
