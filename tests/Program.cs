using NbaLive.Core;
using System.Text.Json;

if(args.Contains("--probe-play-in"))
{
    try
    {
        using var transport=new WebGameSource();var source=new HistoricalTeamSource(transport);await source.GetSeasons(default);
        foreach(var year in args.Contains("--all-years")?Enumerable.Range(2020,7).Select(y=>$"{y-1}-{y}").Append("2004-2005"):new[]{"2023-2024","2019-2020","2004-2005"})
        {
            var scope=new TeamScope(year,TeamPhase.PlayIn);var rank=await source.GetStandings(scope,default);var graph=rank.PlayIn!;
            PlayInBoxScore? box=null;if(graph.Games.Count>0)box=await source.GetPlayInDetail(scope,graph.Games.Last(),default);
            Console.WriteLine(JsonSerializer.Serialize(new{scope,Games=graph.Games.Select(g=>new{g.Id,g.RoundKey,A=g.Team.Name,B=g.Opponent.Name,g.Score,g.GameNumber,g.Tipoff}),graph.Berths,graph.Notice,East=graph.ConferenceNotice("东部"),Detail=box is null?null:new{A=box.A.Team.Name,B=box.B.Team.Name,box.A.Periods,Metrics=box.A.Metrics,Players=box.A.Players.Players.Count,OtherPlayers=box.B.Players.Players.Count}}));
        }
    }
    catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}
    return;
}

if(args.Contains("--probe-team-seasons"))
{
    try
    {
        using var transport=new WebGameSource();var source=new HistoricalTeamSource(transport);var seasons=await source.GetSeasons(default);
        var index=Array.IndexOf(args,"--season");var season=index>=0?args[index+1]:"2023-2024";
        foreach(var phase in new[]{TeamPhase.Regular,TeamPhase.Playoffs})
        {
            var scope=new TeamScope(season,phase);var rank=await source.GetStandings(scope,default);
            foreach(var key in new[]{"lakers","celtics"})
            {
                var team=rank.Teams.FirstOrDefault(t=>t.Key==key)??rank.Rows.FirstOrDefault(r=>r.Team.Key==key)?.Team??TeamCatalog.Resolve(key)!;
                var detail=await source.GetTeamOverview(team,scope,default);
                Console.WriteLine(JsonSerializer.Serialize(new {scope,Seasons=seasons.Select(s=>s.Id),Rows=rank.Rows.Count,Team=team.FullName,Standing=rank.Rows.FirstOrDefault(r=>r.Team.Key==key),detail.Summary,detail.Averages,Games=detail.Games.Count,Recent=detail.Recent(10).Count,Series=detail.Series.Select(s=>new{s.Round,s.Score,s.Status,Games=s.Games.Count}),detail.Notice}));
            }
        }
    }
    catch(Exception ex){for(Exception? error=ex;error is not null;error=error.InnerException)Console.Error.WriteLine(error.GetType().FullName+": "+error.Message);Environment.ExitCode=1;}
    return;
}

if (args.Contains("--probe-teams"))
{
    try
    {
    using var transport = new WebGameSource(); var source = new TeamInsightsSource(transport);
    var standings = await source.GetStandings(default);
    var index = Array.IndexOf(args, "--team");
    foreach (var key in index >= 0 ? new[] { args[index + 1] } : new[] { "lakers", "celtics" })
    {
        var team = TeamCatalog.Resolve(key)!; var overview = await source.GetTeamOverview(team, standings.Season, default);
        Console.WriteLine(JsonSerializer.Serialize(new { standings.Season, standing = standings.Rows.Single(r => r.Team.Key == team.Key),
            DivisionRank = standings.DivisionRanks.GetValueOrDefault(team.Key), standings.Notice,
            Averages = standings.Averages.GetValueOrDefault(team.Key), Games = overview.Games.Count, Recent = overview.Recent(10), Upcoming = overview.Upcoming(DateTimeOffset.UtcNow, 5) }));
    }
    }
    catch(Exception ex)
    {
        for(Exception? error=ex;error is not null;error=error.InnerException)Console.Error.WriteLine(error.GetType().FullName+": "+error.Message);
        Environment.ExitCode=1;
    }
    return;
}

if (args.Contains("--verify-installed-runtime"))
{
    var path = args[Array.IndexOf(args, "--verify-installed-runtime") + 1];
    foreach (var requirement in DesktopRuntimeRequirement.Read(path))
        if (!DesktopRuntimeRequirement.IsSatisfied(requirement, DesktopRuntimeRequirement.InstalledVersions(requirement.Name)))
            throw new InvalidOperationException("Installed runtime not detected: " + requirement.Name);
    Console.WriteLine("PASS Lite updater recognizes installed runtime from Windows registration.");
    return;
}

if (args.Contains("--verify-update-package"))
{
    var zip = args[Array.IndexOf(args, "--verify-update-package") + 1];
    var version = args[Array.IndexOf(args, "--version") + 1]; var variant = args[Array.IndexOf(args, "--variant") + 1];
    var destination = Path.GetFullPath(Path.Combine("artifacts", "update-package-probes", Guid.NewGuid().ToString("N")));
    var manifest = UpdatePackage.Extract(zip, destination, version, variant);
    Console.WriteLine(JsonSerializer.Serialize(new { Verified = true, manifest.Version, manifest.Variant, Files = manifest.Files.Count, Directory = destination }));
    return;
}

if (args.Contains("--probe-updates"))
{
    using var updateSource = new UpdateSource();
    foreach (var variant in new[] { "modern", "net48" })
    {
        var result = await updateSource.Check(new(0, 0, 0), variant, default);
        Console.WriteLine(JsonSerializer.Serialize(new { variant, result }));
        if (args.Contains("--download") && result.Release is { } release)
        {
            var folder = Path.Combine(Path.GetTempPath(), "NbaLiveUpdateProbes", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, release.AssetName); var downloaded = await updateSource.Download(release, variant, path, null, default);
            Console.WriteLine(JsonSerializer.Serialize(new { variant, downloaded.Origin, downloaded.Version, Bytes = new FileInfo(path).Length, Sha256 = UpdatePackage.Hash(path) }));
        }
    }
    return;
}

if (args.Contains("--probe-player-detail"))
{
    var gameIndex = Array.IndexOf(args, "--game"); var playerIndex = Array.IndexOf(args, "--player");
    if (gameIndex < 0 || playerIndex < 0) throw new ArgumentException("--probe-player-detail --game ID --player ID");
    using var source = new WebGameSource(); var fileIndex = Array.IndexOf(args, "--box-score-file");
    var stats = fileIndex >= 0 ? new BoxScoreParser().Parse(File.ReadAllText(args[fileIndex + 1]), args[gameIndex + 1], DateTimeOffset.UtcNow)
        : await source.GetBoxScore(args[gameIndex + 1], default);
    var team = stats.Away.Players.Any(p => PlayerGameDetailParser.LegacyNumber(p.Id) == args[playerIndex + 1]) ? stats.Away : stats.Home;
    var query = new PlayerDetailQuery(stats.Game, team.Team, team.Players.Single(p => PlayerGameDetailParser.LegacyNumber(p.Id) == args[playerIndex + 1])) { StatisticsFetchedAt = stats.FetchedAt };
    var adapter = new PlayerGameDetailSource(source); var snapshot = await adapter.GetDetail(query, default);
    Console.WriteLine(JsonSerializer.Serialize(snapshot));
    if (snapshot.Portrait is { } uri)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(new { PortraitBytes = (await adapter.GetPortrait(uri, default)).Length })); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { Console.WriteLine(JsonSerializer.Serialize(new { PortraitUnavailable = ex.GetType().Name })); }
    }
    return;
}
if (args.Contains("--probe-statistics"))
{
    var gameIndex = Array.IndexOf(args, "--game");
    if (gameIndex < 0 || gameIndex + 1 >= args.Length) throw new ArgumentException("--probe-statistics --game ID");
    using var source = new WebGameSource(loadRosters: true);
    var snapshot = await source.GetBoxScore(args[gameIndex + 1], CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { source.Transport, snapshot.Game, snapshot.FetchedAt,
        AwayPlayers = snapshot.Away.Players.Count, HomePlayers = snapshot.Home.Players.Count,
        AwayTotal = snapshot.Away.Total, HomeTotal = snapshot.Home.Total, FirstPlayer = snapshot.Away.Players.FirstOrDefault() }));
    return;
}
if (args.Contains("--probe"))
{
    using var source = new WebGameSource();
    var dateIndex = Array.IndexOf(args, "--date");
    var gameIndex = Array.IndexOf(args, "--game");
    var date = dateIndex >= 0 ? DateOnly.Parse(args[dateIndex + 1], System.Globalization.CultureInfo.InvariantCulture) : MatchTime.Today;
    var games = await source.GetGames(date, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { date, source.Transport, games }));
    foreach (var game in games.Where(g => gameIndex < 0 || g.Id == args[gameIndex + 1]))
    {
        var snapshot = await source.GetSnapshot(game.Id, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Game, snapshot.FetchedAt, Count = snapshot.Entries.Count, Last = snapshot.Entries.TakeLast(2) }));
    }
    return;
}
if (args.Contains("--observe"))
{
    var idIndex = Array.IndexOf(args, "--game");
    var secondsIndex = Array.IndexOf(args, "--seconds");
    if (idIndex < 0) throw new ArgumentException("--observe --game ID [--seconds 1800]");
    var seconds = secondsIndex >= 0 ? int.Parse(args[secondsIndex + 1]) : 1800;
    using var source = new WebGameSource();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    using var session = new LiveSession(source);
    var first = await source.GetSnapshot(args[idIndex + 1], cts.Token);
    if (first.Game.Status != GameStatus.Live)
    {
        Console.WriteLine("NOT_VERIFIED: requested game is not currently live.");
        Environment.ExitCode = 2;
        return;
    }
    var initialCount = first.Entries.Count;
    session.Changed += state => Console.WriteLine(JsonSerializer.Serialize(new { now = DateTimeOffset.UtcNow, state.Message,
        state.HasError, state.Snapshot?.Game, Count = state.History.Count, Last = state.History.LastOrDefault() }));
    await session.Select(first.Game);
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); } catch (OperationCanceledException) { }
    await session.StopAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { observationSeconds = seconds, initialCount, finalCount = session.State.History.Count,
        hasNewEvents = session.State.History.Count > initialCount, manualPageComparisonRequired = true }));
    return;
}

if (args.Contains("--observe-anchor"))
{
    var gameIndex = Array.IndexOf(args, "--game"); var secondsIndex = Array.IndexOf(args, "--seconds");
    if (gameIndex < 0) throw new ArgumentException("--observe-anchor --game ID [--seconds 1800]");
    var seconds = secondsIndex < 0 ? 1800 : int.Parse(args[secondsIndex + 1]);
    using var transport = new WebGameSource(); var adapter = new AnchorSource(transport);
    var game = (await transport.GetGames(MatchTime.Today, default)).Single(g => g.Id == args[gameIndex + 1]);
    var match = await adapter.Resolve(game, default);
    if (match is null || (await adapter.GetMatch(match, default)).Game.Status != GameStatus.Live)
    { Console.WriteLine("NOT_VERIFIED: a currently live match with an anchor is required."); Environment.ExitCode = 2; return; }
    using var session = new AnchorSession(adapter); var initial = -1;
    session.Changed += () =>
    {
        var state = session.State;
        if (initial < 0 && state.Snapshot is not null) initial = state.History.Count;
        Console.WriteLine(JsonSerializer.Serialize(new { now = DateTimeOffset.UtcNow, state.SourceLabel, state.Message, state.HasError,
            state.IsLoading, state.Snapshot?.Game, state.ScoreFetchedAt, Count = state.History.Count, state.LastBatchSize, Last = state.History.LastOrDefault() }));
    };
    await session.Select(game);
    try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
    finally { await session.StopAsync(); }
    Console.WriteLine(JsonSerializer.Serialize(new { observationSeconds = seconds, initialCount = initial, finalCount = session.State.History.Count,
        hasNewEvents = initial >= 0 && session.State.History.Count > initial, manualAppComparisonRequired = true }));
    if (!session.HasRoom || initial < 0) Environment.ExitCode = 2;
    return;
}

if (args.Contains("--probe-anchor"))
{
    var gameIndex = Array.IndexOf(args, "--game");
    var dateIndex = Array.IndexOf(args, "--date");
    if (gameIndex < 0) throw new ArgumentException("--probe-anchor --game ID [--date yyyy-MM-dd]");
    var date = dateIndex < 0 ? MatchTime.Today : DateOnly.Parse(args[dateIndex + 1], System.Globalization.CultureInfo.InvariantCulture);
    using var source = new WebGameSource();
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var game = (await source.GetGames(date, deadline.Token)).Single(g => g.Id == args[gameIndex + 1]);
    var adapter = new AnchorSource(source);
    var mapped = await adapter.Resolve(game, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Mapping = mapped, source.Transport }));
    using var session = new AnchorSession(adapter);
    session.Changed += () => Console.WriteLine(JsonSerializer.Serialize(new { session.HasRoom, session.Availability,
        session.State.SourceLabel, session.State.Message, session.State.HasError, session.State.IsLoading,
        session.State.Snapshot?.Game, Count = session.State.History.Count, session.ScoreFetchedAt }));
    await session.Select(game);
    try { await session.Completion.WaitAsync(deadline.Token); }
    finally { await session.StopAsync(); }
    var history = session.State.History;
    Console.WriteLine(JsonSerializer.Serialize(new { Count = history.Count, Unique = history.DistinctBy(e => e.Key).Count(),
        First = history.FirstOrDefault(), Last = history.LastOrDefault(),
        ScreenshotMatches = new[] { "比赛开始！！", "刚在隔壁结束！！", "东契奇击地给无球空切篮下的凯斯勒！", "失误了！！" }.All(text => history.Any(e => e.Text == text)),
        LiveAcceptance = "PENDING: archived messages only; 30-minute live comparison required" }));
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Real schedule: correct teams, ids, scores and finished status", () => Sync(() =>
    {
        var games = new GamePageParser().ParseGames(Fixture("games-2026-10-21.html"), new(2026, 10, 21));
        Check(games.Count == 3, "three real games");
        var game = games.Single(g => g.Id == "168863");
        Check(game.Away == "76人" && game.Home == "尼克斯", "correct away/home");
        Check(game.AwayScore == 106 && game.HomeScore == 100 && game.Status == GameStatus.Finished, "final score");
    })),
    ("Real archived play-by-play: complete chronological records", () => Sync(() =>
    {
        var snap = new GamePageParser().ParseSnapshot(Fixture("finished-168863.html"), "168863", DateTimeOffset.UtcNow);
        Check(snap.Entries.Count > 300, "full game parsed");
        Check(snap.Game.Status == GameStatus.Finished && snap.Game.AwayScore == 106, "correct snapshot status");
        Check(snap.Entries[0].Period == 1 && snap.Entries[^1].Period == 4, "quarter inference");
        Check(snap.Entries.Zip(snap.Entries.Skip(1)).All(p => p.First.Order <= p.Second.Order), "source ids chronological");
    })),
    ("Overtime, section markers and same-clock events retain identity", () => Sync(() =>
    {
        var snapshot = Parse(Rows(
            Row("1", "12:00", "第一节开始"), Row("2", "04:32", "球员甲得分"), Row("3", "04:32", "球员乙犯规"),
            "<tr id='4'><td colspan='4'>第一个加时开始</td></tr>", Row("5", "04:59", "跳球"),
            "<tr id='6'><td colspan='4'>第二个加时开始</td></tr>", Row("7", "04:58", "上篮")));
        Check(snapshot.Entries.Count == 7, "all events retained");
        Check(snapshot.Entries[1].Key != snapshot.Entries[2].Key, "same clock does not deduplicate different events");
        Check(snapshot.Entries[^1].Period == 6 && snapshot.Entries[^3].Period == 5, "overtime count");
    })),
    ("Reverse live DOM is normalized to chronological order", () => Sync(() =>
    {
        var snapshot = Parse(Rows(Row("3", "11:58", "投篮"), Row("2", "11:59", "篮板"), Row("1", "12:00", "第一节开始")));
        Check(snapshot.Entries.Select(e => e.SourceId).SequenceEqual(new[] { "1", "2", "3" }), "reverse source order");
    })),
    ("Missing structure and wrong game fail explicitly", () => Sync(() =>
    {
        Throws<SourceFormatException>(() => new GamePageParser().ParseGames("<html>captcha</html>", MatchTime.Today));
        Throws<SourceFormatException>(() => new GamePageParser().ParseSnapshot("<html>blocked</html>", "1", DateTimeOffset.UtcNow));
        Throws<SourceFormatException>(() => new GamePageParser().ParseSnapshot(Page(Rows()), "999", DateTimeOffset.UtcNow));
        Check(new GamePageParser().ParseGames("<div class='gamecenter_livestart'>0场比赛</div>", MatchTime.Today).Count == 0, "valid empty date");
    })),
    ("Scheduled is_live=0 is not misclassified as finished", () => Sync(() =>
    {
        var html = Page(Rows()).Replace("进行中", "未开始");
        Check(new GamePageParser().ParseSnapshot(html, "1", DateTimeOffset.UtcNow).Game.Status == GameStatus.Scheduled, "scheduled state");
    })),
    ("Known scheduled game waits through empty, missing and preview redirects, then becomes live", async () =>
    {
        const string list = "<div class='gamecenter_livestart'><div class='gamecenter_content_l'><div class='list_box'>" +
            "<div class='team_vs_a_1'><div class='txt'><a>客队</a></div></div><div class='team_vs_a_2'><div class='txt'><a>主队</a></div></div>" +
            "<div class='team_vs_b'>未开始 09:30</div><a href='/games/boxscore/1'>比赛前瞻</a></div></div></div>";
        var handler = new FakeHttpHandler([new(System.Net.HttpStatusCode.OK) { Content = new StringContent(list) },
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent("") }, new(System.Net.HttpStatusCode.NotFound),
            new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri(SourcePages.BaseUri, "games/boxscore/1") } },
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Page(Rows(Row("1", "12:00", "第一节开始")))) }]);
        using var source = new WebGameSource(handler);
        await source.GetGames(MatchTime.Today, CancellationToken.None);
        Check((await source.GetSnapshot("1", CancellationToken.None)).Game.Status == GameStatus.Scheduled, "empty page waits");
        Check((await source.GetSnapshot("1", CancellationToken.None)).Game.Status == GameStatus.Scheduled, "404 waits");
        Check((await source.GetSnapshot("1", CancellationToken.None)).Game.Status == GameStatus.Scheduled, "preview redirect waits without following it");
        Check((await source.GetSnapshot("1", CancellationToken.None)).Game.Status == GameStatus.Live, "live page starts normal updates");
    }),
    ("Missing source ID uses normalized text, clock and period", () => Sync(() =>
    {
        var a = Entry(null, "得分  两分") with { Period = 2 };
        Check(a.Key == (a with { Text = "得分 两分" }).Key, "whitespace normalized");
        Check(a.Key != (a with { Period = 3 }).Key, "quarter included");
        Check(a.Key != (a with { Text = "犯规" }).Key, "different events retained");
    })),
    ("Invalid game date is a recoverable source format error", () => Sync(() =>
    {
        Throws<SourceFormatException>(() => new GamePageParser().ParseSnapshot(Page(Rows()) + "<p class='time_f'>开赛：2026年13月99日</p>", "1", DateTimeOffset.UtcNow));
    })),
    ("Screen geometry handles 100-200 percent DPI, negative monitors and removed screens", () => Sync(() =>
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var width = (int)(360 * scale);
            var height = (int)(110 * scale);
            var area = new WorkArea(-2560, -200, 0, 1240);
            var pos = WindowPlacement.Calculate(area, width, height, scale);
            Check(pos.X == -width - (int)(16 * scale) && pos.Y == 1240 - height - (int)(16 * scale), "16 DIP corner margin");
            var moved = WindowPlacement.Calculate(area, width, height, scale, 9000, -9000);
            Check(moved.X == -width && moved.Y == -200, "offscreen clamped to nearest monitor");
            var snapped = WindowPlacement.Calculate(area, width, height, scale, -2550, -190, true);
            Check(snapped.X == -2560 + (int)(16 * scale) && snapped.Y == -200 + (int)(16 * scale), "top-left snap");
        }
    })),
    ("Retry backoff preserves last good snapshot; recovery corrects history and stops on final", async () =>
    {
        var waits = new List<double>();
        var first = Snap(GameStatus.Live, Entry("100", "原事件"));
        var final = Snap(GameStatus.Finished, Entry("100", "修正事件"), Entry("101", "比赛结束"));
        var source = new QueueSource([first, new HttpRequestException(), new HttpRequestException(), new SourceFormatException("bad"), new HttpRequestException(), new HttpRequestException(), final]);
        using var session = new LiveSession(source, (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; });
        var states = new List<SessionState>();
        session.Changed += states.Add;
        await session.Select(first.Game);
        await Until(() => session.State.Snapshot?.Game.Status == GameStatus.Finished);
        Check(waits.Count == 6 && waits[0] is >= 0 and <= 5 && waits.Skip(1).SequenceEqual(new double[] { 5, 10, 20, 30, 30 }), "normal start-to-start interval and retry backoff");
        Check(states.Where(s => s.HasError).All(s => s.Snapshot == first && s.History.Count == 1), "errors retain last snapshot");
        Check(states.Any(s => s.Message == "数据源异常"), "parse failure visible");
        Check(session.State.History.Count == 2 && session.State.History[0].Text == "修正事件", "same source id corrected without duplicate");
        Check(source.Calls == 7, "no polling after final");
    }),
    ("Slow fetches never overlap; changing game cancels old subscription", async () =>
    {
        var source = new SlowSource();
        using var session = new LiveSession(source);
        await session.Select(Snap(GameStatus.Live).Game);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.Select(Snap(GameStatus.Live).Game with { Id = "2" });
        await session.StopAsync();
        Check(source.MaxActive == 1 && source.Cancelled >= 1, "one active fetch and cancellation");
    }),
    ("Hover reading freezes score and text, counts unique additions, and resets on a new game", () => Sync(() =>
    {
        var first = new SessionState(Snap(GameStatus.Live, Entry("1", "命中")), [Entry("1", "命中")], "", false, false)
            { HistoryRevision = 1, AddedEntryKeys = ["1"] };
        var reading = new ReadingState();
        reading.Accept(first);
        reading.Pause();
        var second = first with { Snapshot = Snap(GameStatus.Live, Entry("2", "犯规")), History = [Entry("1", "命中"), Entry("2", "犯规")], HistoryRevision = 2, AddedEntryKeys = ["2"] };
        Check(ReferenceEquals(reading.Accept(second), first), "visible snapshot frozen");
        reading.Accept(second);
        reading.Accept(second with { HasError = true, Message = "连接中断", AddedEntryKeys = [] });
        Check(reading.PendingCount == 1, "duplicate refresh and errors do not inflate pending count");
        Check(reading.Resume()?.HasError == true && reading.PendingCount == 0, "resume reveals newest state including errors");
        reading.Pause();
        var loading = new SessionState(null, [], "正在连接", false, true) { SelectedGame = first.Snapshot!.Game with { Id = "2" } };
        Check(reading.Accept(loading) == loading && reading.PendingCount == 0, "switch game discards old frozen display");
    })),
    ("Important-event filtering keeps chronology and recognizes missed shots", () => Sync(() =>
    {
        var history = new[] { Entry("1", "换人"), Entry("2", "三分命中"), Entry("3", "三分未命中"), Entry("4", "个人犯规"), Entry("5", "暂停") };
        Check(EventSelection.Recent(history, 2, true).Select(e => e.SourceId).SequenceEqual(new[] { "5", "4" }), "latest matching events first");
        Check(!EventSelection.IsImportant(history[2]), "missed shot is not scored");
        Check(EventSelection.Recent(history, 0, false).Count == 0 && history.Length == 5, "score-only mode leaves full history intact");
        Check((Entry("6", "第四节结束。") with { Clock = "" }).Display == "第四节结束。", "section markers are not duplicated");
    })),
    ("Connection success and new-event timestamps remain independent", async () =>
    {
        var time = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var first = Snap(GameStatus.Live, Entry("1", "旧描述")) with { FetchedAt = time };
        var same = first with { FetchedAt = time.AddMinutes(1) };
        var corrected = Snap(GameStatus.Live, Entry("1", "更正描述")) with { FetchedAt = time.AddMinutes(2) };
        var final = corrected with { Game = corrected.Game with { Status = GameStatus.Finished } };
        using var session = new LiveSession(new QueueSource([first, same, corrected, final]), (_, _) => Task.CompletedTask);
        var snapshots = new List<SessionState>();
        session.Changed += s => { if (s.Snapshot is not null) snapshots.Add(s); };
        await session.Select(first.Game);
        await Until(() => session.State.Snapshot?.Game.Status == GameStatus.Finished);
        Check(ReferenceEquals(snapshots[0].History, snapshots[1].History), "unchanged data reuses ordered history");
        Check(snapshots[0].HistoryRevision == snapshots[1].HistoryRevision && snapshots[2].HistoryRevision == 2, "correction updates revision only when content changes");
        Check(snapshots.All(s => s.LastEventObservedAt == time), "corrections and polling do not fake new events");
        Check(!SessionStatus.Describe(snapshots[1], same.FetchedAt).Contains("连接中断"), "no new events is not a network error");
        Check(SessionStatus.Describe(snapshots[1], same.FetchedAt.AddSeconds(31)).Contains("同步较慢"), "stale successful response is identified separately");
    }),
    ("Polling slows before tip-off and accelerates in the last five minutes", () => Sync(() =>
    {
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero); // 08:00 China time
        var scheduled = Snap(GameStatus.Scheduled).Game with { Date = new(2026, 10, 21), Progress = "未开始 09:30" };
        Check(PollingPolicy.Next(scheduled, now).TotalSeconds == 30, "far from tip-off");
        Check(PollingPolicy.Next(scheduled with { Progress = "未开始 08:03" }, now).TotalSeconds == 10, "near tip-off");
        Check(PollingPolicy.Next(scheduled with { Progress = "未开始" }, now).TotalSeconds == 30, "unknown start time stays conservative");
        Check(PollingPolicy.Next(scheduled with { Status = GameStatus.Live }, now).TotalSeconds == 5, "live cadence retained");
    })),
    ("Identical HTML reuses parsed entries but a correction is reparsed", async () =>
    {
        var html = Page(Rows(Row("1", "12:00", "第一节开始")));
        using var source = new WebGameSource(new FakeHttpHandler([
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) },
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) },
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html.Replace("第一节开始", "第一节开始（已修正）")) }]));
        var first = await source.GetSnapshot("1", CancellationToken.None);
        var same = await source.GetSnapshot("1", CancellationToken.None);
        var corrected = await source.GetSnapshot("1", CancellationToken.None);
        Check(ReferenceEquals(first.Entries, same.Entries) && same.FetchedAt >= first.FetchedAt, "reuse with fresh connection timestamp");
        Check(!ReferenceEquals(same.Entries, corrected.Entries) && corrected.Entries[0].Text.Contains("已修正"), "changed body invalidates cache");
    }),
    ("Favorite ordering prioritizes matching teams without changing source data", () => Sync(() =>
    {
        var games = new[] { Snap(GameStatus.Live).Game with { Id = "1", Away = "勇士" },
            Snap(GameStatus.Scheduled).Game with { Id = "2", Home = "湖人" },
            Snap(GameStatus.Live).Game with { Id = "3", Home = "湖人" } };
        var ordered = GameOrdering.Arrange(games, "湖人");
        Check(ordered.Select(g => g.Id).SequenceEqual(new[] { "3", "2", "1" }), "favorite live first, then favorite scheduled");
        Check(games[0].Id == "1" && !GameOrdering.IsFavorite(games[0], ""), "original list and no-favorite mode intact");
    })),
    ("Resizing keeps the right-bottom corner across DPI values", () => Sync(() =>
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var area = new WorkArea(-2560, -200, 0, 1240);
            var oldW = (int)(360 * scale); var oldH = (int)(110 * scale);
            var newW = (int)(280 * scale); var newH = (int)(58 * scale);
            var old = WindowPlacement.Calculate(area, oldW, oldH, scale);
            var next = WindowPlacement.PreserveCorner(area, oldW, oldH, newW, newH, scale, old.X, old.Y);
            Check(next == WindowPlacement.Calculate(area, newW, newH, scale), "right-bottom anchor retained");
            Check(WindowPlacement.PreserveCorner(area, oldW, oldH, newW, newH, scale, -1500, 200) == new WindowPosition(-1500, 200), "free placement does not unexpectedly snap");
        }
    })),
    ("Settings use defaults for omitted fields and preferences roundtrip", () => Sync(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "NbaLive-Migration-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Width\":400,\"LastGameId\":\"123\"}");
            var store = new SettingsStore(dir);
            var old = store.Load();
            Check(old.Width == 400 && old.DisplayMode == OverlayMode.TwoEvents && !old.ImportantEventsOnly, "omitted preferences retain the default appearance");
            Check(store.Save(old with { DisplayMode = OverlayMode.ScoreOnly, ImportantEventsOnly = true, FavoriteTeam = "湖人" }), "save new settings");
            var updated = store.Load();
            Check(updated.DisplayMode == OverlayMode.ScoreOnly && updated.ImportantEventsOnly && updated.FavoriteTeam == "湖人" && updated.LastGameId == "123", "new settings and original game survive roundtrip");
            Check((old with { DisplayMode = (OverlayMode)999 }).Validate().DisplayMode == OverlayMode.TwoEvents, "invalid enum resets safely");
        }
        finally { Directory.Delete(dir, true); }
    })),
    ("Theme migrates old configurations to warm and persists dark without changing live preferences", () => Sync(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "NbaLive-Theme-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Width\":400,\"LastGameId\":\"123\",\"CapsuleEnabled\":true}");
            var store = new SettingsStore(dir);
            var old = store.Load();
            Check(old.Theme == AppTheme.Warm, "old configuration opens in warm theme");
            Check(store.Save(old with { Theme = AppTheme.Dark }), "dark theme saves");
            var updated = store.Load();
            Check(updated.Theme == AppTheme.Dark && updated.Width == 400 && updated.LastGameId == "123" && updated.CapsuleEnabled, "theme roundtrip preserves live preferences");
            Check((updated with { Theme = (AppTheme)999 }).Validate().Theme == AppTheme.Warm, "invalid theme safely falls back");
        }
        finally { Directory.Delete(dir, true); }
    })),
    ("Settings clamp invalid numbers, survive roundtrip and reject malformed hotkeys", () => Sync(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "NbaLive-Test-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(dir);
            Check(store.Save(new() { Width = 480, FontSize = 16, X = -300 }), "save succeeds");
            Check(store.Load().X == -300 && store.Load().Width == 480, "roundtrip includes negative monitor position");
            var invalid = new AppSettings { Width = double.NaN, FontSize = 99, BackgroundOpacity = 0, HideHotkey = "bad" };
            var safe = invalid.Validate();
            Check(safe.Width == 360 && safe.FontSize == 16 && safe.BackgroundOpacity == .2 && safe.HideHotkey == "Ctrl+Alt+H", "bounds");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "broken json");
            Check(store.Load().Width == 360 && store.Error is not null, "corrupt file recovers");
            Check(Hotkey.TryParse("Ctrl+Alt+F12", out _) && !Hotkey.TryParse("Ctrl+Ctrl+H", out _) && !Hotkey.TryParse("H", out _), "hotkeys validated");
        }
        finally { Directory.Delete(dir, true); }
    }))
}.Concat(StatisticsTests.Cases).Concat(CompanionTests.Cases).Concat(JerseyTests.Cases).Concat(LiveCompanionTests.Cases).Concat(CollectionTests.Cases).Concat(ScoringTests.Cases).Concat(DeliveryTests.Cases).Concat(ScheduleProgressTests.Cases).Concat(AnchorTests.Cases).Concat(PlayerDetailTests.Cases).Concat(UpdateTests.Cases).Concat(TeamInsightsTests.Cases).Concat(HistoricalTeamTests.Cases).Concat(PlayoffBracketTests.Cases).Concat(PlayInTests.Cases).ToArray();
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
Environment.ExitCode = failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Task Sync(Action action) { action(); return Task.CompletedTask; }
static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static string Row(string id, string clock, string text) => $"<tr id='{id}'><td>{clock}</td><td>客队</td><td>{text}</td><td>2-0</td></tr>";
static string Rows(params string[] rows) => $"<div class='table_overflow'><table>{string.Join("", rows)}</table></div>";
static string Page(string rows) => "<div class='table_list_c'><ul><li><a href='/games/boxscore/1'><p>进行中 第三节 04:32</p></a></li></ul></div>" +
    "<div class='team_a'><div class='message'><h2>2</h2><p><a>客队</a></p></div></div>" +
    "<div class='team_b'><div class='message'><h2>0</h2><p><a>主队</a></p></div></div>" + rows +
    "<script>window.GM={match_id:'1',is_live:parseInt(\"0\")};</script>";
static LiveSnapshot Parse(string rows) => new GamePageParser().ParseSnapshot(Page(rows), "1", DateTimeOffset.UtcNow);
static LiveEntry Entry(string? id, string text) => new(id, 1, "04:32", "客队", text, "2-0", long.TryParse(id, out var n) ? n : 0);
static LiveSnapshot Snap(GameStatus status, params LiveEntry[] entries) => new(new("1", "客队", "主队", 2, 0, status, "第一节", MatchTime.Today), entries, DateTimeOffset.UtcNow);
static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!predicate()) await Task.Delay(10, timeout.Token); }

sealed class QueueSource(Queue<object> queue) : IGameSource
{
    public QueueSource(IEnumerable<object> items) : this(new Queue<object>(items)) { }
    public int Calls { get; private set; }
    public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken ct) => throw new NotSupportedException();
    public Task<LiveSnapshot> GetSnapshot(string id, CancellationToken ct)
    {
        Calls++;
        var next = queue.Dequeue();
        return next is Exception exception ? Task.FromException<LiveSnapshot>(exception) : Task.FromResult((LiveSnapshot)next);
    }
}
sealed class SlowSource : IGameSource
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Active;
    public int MaxActive;
    public int Cancelled;
    public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken ct) => throw new NotSupportedException();
    public async Task<LiveSnapshot> GetSnapshot(string id, CancellationToken ct)
    {
        var active = Interlocked.Increment(ref Active);
        MaxActive = Math.Max(MaxActive, active);
        Started.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
        catch (OperationCanceledException) { Cancelled++; throw; }
        finally { Interlocked.Decrement(ref Active); }
    }
}
sealed class FakeHttpHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_responses.Dequeue());
}
