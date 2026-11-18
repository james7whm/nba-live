using NbaLive.Core;
using System.Net;

internal static class ScheduleProgressTests
{
    private static readonly DateOnly Date = new(2026, 10, 22);
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Empty web schedule falls back to mobile identifiers and keeps match identity", async () =>
        {
            var handler = new MobileScheduleHandler();
            using var source = new WebGameSource(handler);
            var games = await source.GetGames(Date, CancellationToken.None);
            var game = games.Single();
            Check(game.Id == MobileScheduleHandler.Match && game is { Away: "勇士", Home: "掘金", Status: GameStatus.Live, AwayScore: 80, HomeScore: 77 }, "mobile game is returned with lossless id");
            var live = await source.GetSnapshot(game.Id, CancellationToken.None);
            Check(live.StructuredUnavailable && live.Game.Id == game.Id, "mobile score never fabricates structured events");
            var stats = await source.GetBoxScore(game.Id, CancellationToken.None);
            Check(stats.Away.Total?.Points == 134 && stats.Home.Total?.Points == 114 && stats.Away.Players.Any(p => p.Name.Contains("库里") && p.JerseyNumber == "30"), "mobile box score uses game-scoped players and total");
            Check(await new AnchorSource(source).Resolve(game, CancellationToken.None) is { Id: MobileScheduleHandler.Match }, "anchor session reuses mobile id");
        }),
        ("Mobile schedule and score take priority while a verified web id is preserved", async () =>
        {
            var handler = new MobileLegacyHandler(); using var source = new WebGameSource(handler);
            var game = (await source.GetGames(Date, CancellationToken.None)).Single();
            Check(game.Id == "168866" && game.AwayScore == 80 && game.HomeScore == 77, "mobile values retain uniquely matched legacy id");
            Check(handler.Paths[0].Contains("scheduleList") && handler.Paths[1] == "/games/2026-10-22", "mobile schedule is requested first");
            var snapshot = await source.GetSnapshot(game.Id, CancellationToken.None);
            Check(snapshot.Game.Id == game.Id && snapshot.Game.AwayScore == 80 && snapshot.StructuredUnavailable, "web failure keeps verified mobile score and flags missing text");
            Check(handler.Paths[2].Contains("singleMatch") && handler.Paths[3].Contains("playbyplay"), "mobile score precedes structured fallback");
        }),
        ("Mobile schedule distinguishes missing dates, duplicates and mismatched identities", () => Sync(() =>
        {
            Check(MobileSchedule.Games(MobileScheduleHandler.Schedule, Date.AddDays(1)) is null, "out of range date is not treated as empty");
            Check(MobileSchedule.Games(MobileScheduleHandler.Schedule, Date)!.Count == 1, "matching date returns games");
            var mobile = MobileSchedule.Games(MobileScheduleHandler.Schedule, Date)!.Single();
            var web = mobile with { Id = "168866", AwayScore = 70, HomeScore = 60 };
            var combined = MobileSchedule.Combine([web], [mobile]).Single();
            Check(combined.Id == web.Id && combined.AwayScore == 80 && combined.HomeScore == 77, "mobile score keeps previously saved web identity");
            var final = web with { Status = GameStatus.Finished };
            Check(MobileSchedule.Combine([final], [mobile]).Single() == final, "final result is not downgraded");
            try { MobileSchedule.Combine([web, web with { Id = "168867" }], [mobile]); throw new Exception("expected ambiguous mapping rejection"); }
            catch (SourceFormatException) { }
            try { MobileSchedule.Match(MobileScheduleHandler.MatchJson, "1537808027304853505"); throw new Exception("expected mismatch"); }
            catch (SourceFormatException) { }
            var duplicate = MobileScheduleHandler.Schedule.Replace(MobileScheduleHandler.Item, MobileScheduleHandler.Item + "," + MobileScheduleHandler.Item);
            try { MobileSchedule.Games(duplicate, Date); throw new Exception("expected duplicate rejection"); }
            catch (SourceFormatException) { }
        })),
        ("Mobile lineup requires five complete and fresh on-court marks", () => Sync(() =>
        {
            var game = new GameInfo(MobileScheduleHandler.Match, "勇士", "掘金", 80, 77, GameStatus.Live, "第三节", Date);
            var roster = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-lendeborg-roster.json"));
            var now = DateTimeOffset.UtcNow;
            var stats = MobileSchedule.BoxScore(roster, game, now);
            var team = stats.Away with { Players = stats.Away.Players.Select((p, i) => p with { OnCourt = i < 5 }).ToArray() };
            Check(CourtLineups.Resolve(team, game, null, now).Values.Count(x => x.Presence == CourtPresence.OnCourt) == 5, "five fresh marks are shown");
            Check(CourtLineups.Resolve(team, game, null, now.AddMinutes(1)).Values.All(x => x.Presence == CourtPresence.Unknown), "stale marks are not shown");
            var incomplete = team with { Players = team.Players.Select((p, i) => i == 0 ? p with { OnCourt = null } : p).ToArray() };
            Check(CourtLineups.Resolve(incomplete, game, null, now).Values.All(x => x.Presence == CourtPresence.Unknown), "incomplete lineup is not guessed");
        })),
        ("Mobile statistics preserve verified legacy player identities", () => Sync(() =>
        {
            var game = new GameInfo(MobileScheduleHandler.Match, "勇士", "掘金", 80, 77, GameStatus.Live, "第三节", Date);
            var roster = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-lendeborg-roster.json"));
            var stats = MobileSchedule.BoxScore(roster, game, DateTimeOffset.UtcNow);
            var sample = stats.Away.Players[0];
            var mobile = stats.Away with { MobileTeamId = "1901000000501273", Players = [sample with { Id = "1901000000473521" }] };
            var web = stats.Away with { Players = [sample with { Id = "jalenbrunson-150988.html" }] };
            var teamRoster = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-team.json"));
            var map = MobileSchedule.LegacyPlayerIds(mobile, web, teamRoster);
            var merged = MobileSchedule.ApplyLegacyPlayerIds(mobile, map, "knicks");
            Check(merged.Players.Single().Id == "jalenbrunson-150988.html" && merged.Players.Single().Points == sample.Points && merged.RosterSlug == "knicks", "preserve old identity while using mobile values");
            try { MobileSchedule.LegacyPlayerIds(mobile with { MobileTeamId = "wrong" }, web, teamRoster); throw new Exception("expected team mismatch"); }
            catch (SourceFormatException) { }
        })),
        ("Legacy games use mobile statistics when both team identity maps are verified", async () =>
        {
            var handler = new MobileLegacyStatsHandler(); using var source = new WebGameSource(handler);
            var game = (await source.GetGames(Date, CancellationToken.None)).Single();
            var first = await source.GetBoxScore(game.Id, CancellationToken.None);
            Check(first.Source == "mobile" && first.Game.Id == "168866" && first.Away.Players.Any(p => p.Id == "stephencurry-150001.html" && p.Points == 24), "mobile values preserve old followed-player id");
            var second = await source.GetBoxScore(game.Id, CancellationToken.None);
            Check(second.Source == "mobile" && handler.Paths.Count(p => p.Contains("teamPlayerList")) == 2 && handler.Paths.Count(p => p.Contains("playerMatchStats")) == 2, "identity map is reused while statistics refresh");
        }),
        ("Unverified mobile player mappings fall back and back off", async () =>
        {
            var handler = new MobileLegacyStatsHandler { BadMapping = true }; using var source = new WebGameSource(handler);
            var game = (await source.GetGames(Date, CancellationToken.None)).Single();
            var first = await source.GetBoxScore(game.Id, CancellationToken.None);
            var second = await source.GetBoxScore(game.Id, CancellationToken.None);
            Check(first.Source == "web" && second.Source == "web" && first.Away.Players.Single().Id == "stephencurry-150001.html", "retain web stats and original identity when mapping is unavailable");
            Check(handler.Paths.Count(p => p.Contains("playerMatchStats")) == 1 && handler.Paths.Count(p => p.Contains("boxscore")) == 2, "back off and reuse already fetched web page");
        }),
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
        ("Mobile schedule corrects stale live score while preserving identity and refusing ambiguous matches", () => Sync(() =>
        {
            var old = new GameInfo("168866", "尼克斯", "76人", 90, 94, GameStatus.Live, "第四节剩30.0秒", Date);
            const string item = "{\"matchId\":\"1537808028357623808\",\"awayTeamName\":\"尼克斯\",\"homeTeamName\":\"76人\",\"awayScore\":93,\"homeScore\":98,\"matchStatus\":\"COMPLETED\"}";
            string Schedule(string entries) => "{\"success\":true,\"result\":{\"gameList\":[{\"day\":\"20261022\",\"matchList\":[" + entries + "]}]}}";
            var updated = AnchorParser.MergeSchedule(Schedule(item), [old]).Single();
            Check(updated is { Id: "168866", Status: GameStatus.Finished, AwayScore: 93, HomeScore: 98, Progress: "已结束" }, "fresh final replaces stale score");
            Check(AnchorParser.MergeSchedule(Schedule(item + "," + item), [old]).Single() == old, "ambiguous matches do not override");
            Check(AnchorParser.MergeSchedule(Schedule(item.Replace("\"homeScore\":98", "\"homeScore\":null")), [old]).Single() == old, "incomplete score does not override");
            var final = old with { Status = GameStatus.Finished, Progress = "已结束" };
            Check(AnchorParser.MergeSchedule(Schedule(item.Replace("COMPLETED", "INPROGRESS")), [final]).Single() == final, "final is not downgraded");
        })),
        ("Schedule falls back to webpage when optional mobile request fails", async () =>
        {
            var handler = new ScheduleHandler();
            using var source = new WebGameSource(handler);
            var games = await source.GetGames(Date, CancellationToken.None);
            Check(handler.Calls == 2 && games.Single(g => g.Id == "168866") is { Status: GameStatus.Live, Progress: "第二节剩2:28" }, "web schedule remains available after mobile failure");
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
            if (request.RequestUri!.Host == "games.mobileapi.hupu.com") throw new HttpRequestException("offline");
            Check(request.RequestUri!.AbsolutePath == "/games/2026-10-22", "one schedule endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture()) });
        }
    }
    private sealed class MobileScheduleHandler : HttpMessageHandler
    {
        internal const string Match = "1537808027304853504";
        internal const string Item = "{\"matchId\":\"1537808027304853504\",\"awayTeamName\":\"勇士\",\"homeTeamName\":\"掘金\",\"awayScore\":80,\"homeScore\":77,\"matchStatus\":\"INPROGRESS\",\"matchTime\":\"2026-10-22 08:00:00\"}";
        internal const string Schedule = "{\"success\":true,\"result\":{\"gameList\":[{\"day\":\"20261022\",\"matchList\":[" + Item + "]}]}}";
        internal const string MatchJson = "{\"success\":true,\"result\":" + Item + "}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("scheduleList") ? Schedule : path.Contains("singleMatch") ? MatchJson :
                path.Contains("playerMatchStats") ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-lendeborg-roster.json")) :
                "<div class='gamecenter_livestart'>0场比赛</div>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class MobileLegacyHandler : HttpMessageHandler
    {
        internal readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (path.Contains("playbyplay")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var card = Card("168866", "c", "进行中 <p>第三节剩2:28</p>").Replace("客队", "勇士").Replace("主队", "掘金");
            var body = path.Contains("scheduleList") ? MobileScheduleHandler.Schedule : path.Contains("singleMatch") ? MobileScheduleHandler.MatchJson : Page(card);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class MobileLegacyStatsHandler : HttpMessageHandler
    {
        internal readonly List<string> Paths = [];
        internal bool BadMapping;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; var path = uri.AbsolutePath; Paths.Add(path);
            var card = Card("168866", "c", "进行中 <p>第三节剩2:28</p>").Replace("客队", "勇士").Replace("主队", "掘金");
            var headers = new[] { "首发", "", "时间", "投篮", "3分", "罚球", "前场", "后场", "篮板", "助攻", "犯规", "抢断", "失误", "封盖", "得分", "+/-" };
            string Table(string id, string player, string legacy) => "<table id='" + id + "'><tr>" + string.Concat(headers.Select(x => "<td>" + x + "</td>")) + "</tr><tr>" +
                string.Concat(new[] { "<a href='/players/" + legacy + "'>" + player + "</a>", "G", "30", "2-4", "1-2", "1-1", "0", "2", "2", "3", "1", "1", "0", "0", "6", "+1" }.Select(x => "<td>" + x + "</td>")) + "</tr></table>";
            var box = "<div class='team_a'><div class='message'><h2>80</h2><p><a>勇士</a></p></div></div>" +
                "<div class='team_b'><div class='message'><h2>77</h2><p><a>掘金</a></p></div></div>" +
                "<div class='table_list_c'><li><a href='/games/boxscore/168866'><p>进行中 第三节 04:32</p></a></li></div>" +
                Table("J_away_content", "库里", "stephencurry-150001.html") + Table("J_home_content", "约基奇", "nikolajokic-150002.html") + "<script>window.GM={match_id:'168866'};</script>";
            string Team(string teamId, string player, int legacy) => "{\"result\":{\"info\":{\"teamId\":\"" + teamId + "\"},\"list\":[{\"playerId\":\"" + player + "\",\"player_id\":" + legacy + "}]}}";
            var body = path.Contains("scheduleList") ? MobileScheduleHandler.Schedule : path.Contains("playerMatchStats") ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-detail-lendeborg-roster.json")) :
                path.Contains("teamPlayerList") ? uri.Query.Contains("1901000000501331") ? Team("1901000000501331", "1901000000442301", BadMapping ? 999999 : 150001) : Team("1901000000501301", "1901000000448416", 150002) :
                path.Contains("boxscore") ? box : Page(card);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
