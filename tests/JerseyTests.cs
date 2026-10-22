using NbaLive.Core;
using System.Net;

internal static class JerseyTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("All thirty franchises have distinct stable jersey colors and resolve team aliases", () => Sync(() =>
        {
            var names = new[] { "老鹰", "凯尔特人", "篮网", "黄蜂", "公牛", "骑士", "独行侠", "掘金", "活塞", "勇士", "火箭", "步行者", "快船", "湖人", "灰熊", "热火", "雄鹿", "森林狼", "鹈鹕", "尼克斯", "雷霆", "魔术", "76人", "太阳", "开拓者", "国王", "马刺", "猛龙", "爵士", "奇才" };
            var colors = names.Select(n => TeamPalette.Resolve(n)).ToArray();
            Check(TeamPalette.All.Count == 30 && colors.All(c => c != TeamPalette.Unknown), "cover every NBA franchise");
            Check(colors.Select(c => c.Rgb).Distinct().Count() == 30 && colors.All(c => c.Rgb <= 0xFFFFFF && c.Rgb != TeamPalette.Unknown.Rgb), "no repeated hex colors or fallback collision");
            Check(colors.All(c => TeamPalette.Resolve(null, c.Key) == c), "stable roster slug mapping for all thirty teams");
            Check(TeamPalette.Resolve(" LA Lakers ", "LAKERS") == TeamPalette.Resolve("洛杉矶湖人"), "slug takes priority over unfamiliar display names");
            Check(TeamPalette.Resolve("费城76人") == TeamPalette.Resolve("Philadelphia 76ers") && TeamPalette.Resolve("小牛") == TeamPalette.Resolve("DAL"), "Chinese, English and former-name aliases");
            Check(TeamPalette.Resolve("开拓者", "trail-blazers") == TeamPalette.Resolve("Portland Trail Blazers"), "source slug variants");
            Check(TeamPalette.Resolve("未知队") == TeamPalette.Unknown && TeamPalette.Resolve("湖人", "unknown-slug") == TeamPalette.Resolve("湖人"), "unknown identifiers fall back safely without partial name matching");
        })),
        ("Jersey numbers choose readable dark or white text across the complete team palette", () => Sync(() =>
        {
            static double Linear(uint c) => c / 255d <= .04045 ? c / 255d / 12.92 : Math.Pow((c / 255d + .055) / 1.055, 2.4);
            foreach (var color in TeamPalette.All.Append(TeamPalette.Unknown))
            {
                var luminance = .2126 * Linear((color.Rgb >> 16) & 255) + .7152 * Linear((color.Rgb >> 8) & 255) + .0722 * Linear(color.Rgb & 255);
                var contrast = color.UseDarkNumber ? (luminance + .05) / .05 : 1.05 / (luminance + .05);
                Check(contrast >= 4.5, $"readable jersey number: {color.Name}");
            }
            Check(TeamPalette.Resolve("太阳").UseDarkNumber && !TeamPalette.Resolve("篮网").UseDarkNumber, "light fill gets dark numbers and black fill gets white numbers");
        })),
        ("Roster numbers preserve 0 and 00, source identities and conflicting unknowns", () => Sync(() =>
        {
            var numbers = TeamRosterParser.Parse(Roster(("one.html", "0"), ("two.html", "00"), ("bad.html", "999"),
                ("conflict.html", "3"), ("conflict.html", "4")));
            Check(numbers.Count == 2 && numbers["one.html"] == "0" && numbers["two.html"] == "00", "never merge 0 and 00 or invent conflicting numbers");
        })),
        ("Jersey metadata is cached and shares the live text HTTP gate", async () =>
        {
            var handler = new RosterHandler();
            using var source = new WebGameSource(handler, loadRosters: true);
            var first = await source.GetBoxScore("168863", CancellationToken.None);
            await Task.WhenAll(source.GetBoxScore("168863", CancellationToken.None), source.GetSnapshot("168863", CancellationToken.None));
            Check(first.Away.Players[0].JerseyNumber == "0" && first.Away.Players[3].JerseyNumber == "23", "numbers join by source id, not name");
            Check(first.Home.Players[0].JerseyNumber == "11" && first.Away.Players[1].JerseyNumber is null, "never substitute missing numbers");
            Check(handler.RosterCalls == 2 && handler.MaxActive == 1, "one roster per team and no concurrent HTTP requests");
        }),
        ("Unavailable optional roster metadata keeps statistics and backs off", async () =>
        {
            var handler = new RosterHandler { FailRosters = true };
            using var source = new WebGameSource(handler, loadRosters: true);
            var first = await source.GetBoxScore("168863", CancellationToken.None);
            var next = await source.GetBoxScore("168863", CancellationToken.None);
            Check(first.Away.Total?.Points == 106 && next.Away.Players.Count == 17 && next.Away.Players.All(p => p.JerseyNumber is null), "valid stats survive a metadata failure");
            Check(handler.RosterCalls == 2, "failed metadata is not retried on each 15-second snapshot");
        }),
        ("Cancelling a game during roster retrieval cancels the optional request", async () =>
        {
            var handler = new RosterHandler { HoldRoster = true };
            using var source = new WebGameSource(handler, loadRosters: true);
            using var cts = new CancellationTokenSource();
            var pending = source.GetBoxScore("168863", cts.Token);
            await handler.RosterStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); cts.Cancel();
            try { await pending; throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            Check(handler.Active == 0 && handler.RosterCalls == 1, "do not continue loading the other team after cancellation");
        }),
        ("Explicit current five-player lists identify bench players in and starters out", () => Sync(() =>
        {
            var team = Team(); var game = Game();
            var state = Live([Entry(1, "球员乙、球员丙、球员丁、球员戊、球员己")]);
            var result = CourtLineups.Resolve(team, game, state, DateTimeOffset.UtcNow);
            Check(result["0"].Presence == CourtPresence.OffCourt && result["5"].Presence == CourtPresence.OnCourt, "starter and playing time never determine current presence");
            Check(result.Values.Count(v => v.Presence == CourtPresence.OnCourt) == 5, "five explicit players, not five assumed starters");
        })),
        ("Latest source-ordered lineup handles substitutions, corrections and late old entries", () => Sync(() =>
        {
            var team = Team(); var game = Game();
            var first = Entry(1, "球员甲、球员乙、球员丙、球员丁、球员戊");
            var swap = Entry(3, "球员乙、球员丙、球员丁、球员戊、球员己");
            var result = CourtLineups.Resolve(team, game, Live([swap, first]), DateTimeOffset.UtcNow);
            Check(result["0"].Presence == CourtPresence.OffCourt && result["5"].Presence == CourtPresence.OnCourt, "late older entry cannot rewind lineup");
            var corrected = swap with { Text = first.Text };
            result = CourtLineups.Resolve(team, game, Live([first, corrected]), DateTimeOffset.UtcNow);
            Check(result["0"].Presence == CourtPresence.OnCourt && result["5"].Presence == CourtPresence.OffCourt, "corrected latest event changes current state");
        })),
        ("Missing, incomplete and ambiguous lineups remain unknown instead of grey off-court", () => Sync(() =>
        {
            var team = Team(); var game = Game();
            Check(CourtLineups.Resolve(team, game, Live([]), DateTimeOffset.UtcNow).Values.All(v => v.Presence == CourtPresence.Unknown), "missing is not off-court");
            Check(CourtLineups.Resolve(team, game, Live([Entry(1, "球员甲、球员乙")]), DateTimeOffset.UtcNow).Values.All(v => v.Presence == CourtPresence.Unknown), "partial list does not invent remaining players");
            var ambiguous = team with { Players = team.Players.Select((p, i) => p with { Name = i switch { 0 => "杰伦-威廉姆斯", 1 => "杰林-威廉姆斯", _ => p.Name } }).ToArray() };
            var result = CourtLineups.Resolve(ambiguous, game, Live([Entry(1, "杰隆-威廉姆斯、球员丙、球员丁、球员戊、球员己")]), DateTimeOffset.UtcNow);
            Check(result["0"].Presence == CourtPresence.Unknown && result["1"].Presence == CourtPresence.Unknown && result.Values.All(v => v.Presence != CourtPresence.OffCourt), "ambiguous transliterations never choose an id or mark unmatched players out");
        })),
        ("New periods, changed games, network failures and stale data invalidate court confirmation", () => Sync(() =>
        {
            var team = Team(); var game = Game(); var state = Live([Entry(1, "球员甲、球员乙、球员丙、球员丁、球员戊")]);
            var cases = new[] { state with { HasError = true }, state with { Snapshot = state.Snapshot! with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } },
                state with { Snapshot = state.Snapshot! with { Game = game with { Progress = "第四节 12:00" } } },
                state with { Snapshot = state.Snapshot! with { Game = game with { Id = "2" } } } };
            foreach (var item in cases) Check(CourtLineups.Resolve(team, game, item, DateTimeOffset.UtcNow).Values.All(v => v.Presence == CourtPresence.Unknown), "never carry stale confirmation across boundaries");
            var starters = team with { Players = team.Players.Select((p, i) => p with { IsStarter = i < 5 }).ToArray() };
            var final = CourtLineups.Resolve(starters, game with { Status = GameStatus.Finished }, state, DateTimeOffset.UtcNow);
            Check(final.Values.Count(v => v.Presence == CourtPresence.Starter) == 5 && final.Values.All(v => v.Presence != CourtPresence.OnCourt), "ended game uses labeled starters, never a live presence claim");
        })),
        ("Real substitution text matches team-scoped identities despite punctuation and unique translated names", () => Sync(() =>
        {
            var box = new BoxScoreParser().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "boxscore-168863.html")), "168863", DateTimeOffset.UtcNow);
            var live = new GamePageParser().ParseSnapshot(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "finished-168863.html")), "168863", DateTimeOffset.UtcNow);
            var game = box.Game with { Status = GameStatus.Live, Progress = "第四节 00:10" };
            var state = new SessionState(live with { Game = game }, live.Entries, "", false, false) { SelectedGame = game };
            foreach (var team in new[] { box.Away, box.Home })
            {
                var result = CourtLineups.Resolve(team, game, state, DateTimeOffset.UtcNow);
                Check(result.Values.Count(p => p.Presence == CourtPresence.OnCourt) == 5, "latest actual five-player list matches five unique source identities");
            }
        }))
    ];
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static GameInfo Game() => new("1", "客队", "主队", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
    private static TeamStatistics Team() => new("客队", new[] { "球员甲", "球员乙", "球员丙", "球员丁", "球员戊", "球员己" }
        .Select((name, i) => new PlayerStatistics(i.ToString(), name, i == 0, "G", "28:36", "1-2", "0-0", "0-0", 0, 0, 0, 0, 0, 0, 0, 0, 2, 0)).ToArray(), null);
    private static LiveEntry Entry(int order, string names) => new(order.ToString(), 3, "04:32", "客队", $"客队换人（{names}）", "86-82", order);
    private static SessionState Live(LiveEntry[] entries) => new(new(Game(), entries, DateTimeOffset.UtcNow), entries, "", false, false) { SelectedGame = Game() };
    private static string Roster(params (string Id, string Number)[] players) => string.Join("", players.Select(p =>
        $"<div class='x_list'><span class='c1'>{p.Number}</span><span class='c2'><a href='/players/{p.Id}'>球员</a></span></div>"));
    private sealed class RosterHandler : HttpMessageHandler
    {
        internal bool FailRosters, HoldRoster;
        internal int RosterCalls, Active, MaxActive;
        internal readonly TaskCompletionSource RosterStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            try
            {
                await Task.Delay(10, ct);
                var path = request.RequestUri!.AbsolutePath;
                if (path.StartsWith("/teams/", StringComparison.Ordinal))
                {
                    RosterCalls++; RosterStarted.TrySetResult();
                    if (HoldRoster) await Task.Delay(Timeout.Infinite, ct);
                    if (FailRosters) return new(HttpStatusCode.ServiceUnavailable);
                    return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("76ers") ?
                        Roster(("tyresemaxey-152368.html", "0"), ("lebronjames-650.html", "23")) : Roster(("jalenbrunson-150988.html", "11"))) };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                    path.Contains("boxscore") ? "boxscore-168863.html" : "finished-168863.html"))) };
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
