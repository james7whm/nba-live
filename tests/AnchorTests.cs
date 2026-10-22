using NbaLive.Core;
using System.Net;
using System.Text.Json;

internal static class AnchorTests
{
    private static readonly GameInfo Game = new("168866", "勇士", "湖人", 105, 127, GameStatus.Live, "第四节剩1:00", new(2026, 10, 22));
    private static readonly MobileMatch Match = new("1537445624650137600", Game);
    private static readonly AnchorRoom Room = new(Match.Id, "20564829:10449:1037103", "犀牛", false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    private static string Json(object result) => JsonSerializer.Serialize(new { success = true, result });
    private static object ScheduleItem(string id = "1537445624650137600", string away = "金州勇士", string home = "洛杉矶湖人") =>
        new { matchId = id, awayTeamName = away, homeTeamName = home, awayScore = 105, homeScore = 127, matchStatus = "COMPLETED" };
    private static string Schedule(params object[] items) => Json(new { gameList = new[] { new { day = "20261022", matchList = items } } });
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Anchor mapping keeps nineteen-digit IDs and requires unique date and home-away identities", () => Sync(() =>
        {
            var match = AnchorParser.Resolve(Schedule(ScheduleItem()), Game);
            Check(match?.Id == Match.Id && match.Game.Id == Game.Id, "separate source IDs, same application game");
            Check(AnchorParser.Resolve(Schedule(ScheduleItem(), ScheduleItem("1537445624650137601")), Game) is null, "ambiguous matches rejected");
            Check(AnchorParser.Resolve(Schedule(ScheduleItem()), Game with { Date = Game.Date.AddDays(-1) }) is null, "outside returned schedule rejected");
            Check(AnchorParser.Resolve(Schedule(ScheduleItem(home: "勇士", away: "湖人")), Game) is null, "home-away reversal not guessed");
        })),
        ("Anchor room rejects zero keys, validates JSON and honors polling configuration", () => Sync(() =>
        {
            Check(AnchorParser.Room(Json(new { liveActivityKey = "0:0:0" }), Match.Id) is null, "success flag is not a valid room");
            var room = AnchorParser.Room(Json(new { liveActivityKey = Room.Key, anchorName = "犀牛", liveStatus = "FINISH", liveRoomMsgConfig = new { polling = new { open = true, inProgressIntervalInMillSeconds = 50, notInProgressIntervalInMillSeconds = 5000 } } }), Match.Id);
            Check(room?.Finished == true && room.LiveInterval.TotalSeconds == 1 && room.IdleInterval.TotalSeconds == 5, "one-second floor");
            Throws(() => AnchorParser.Messages("{\"success\":false,\"result\":[]}"));
            Throws(() => AnchorParser.Messages("<html>error</html>"));
            Throws(() => AnchorParser.Room(Json(new { liveActivityKey = "wrong" }), Match.Id));
        })),
        ("Mobile match updates score and overtime without inventing a missing game clock", () => Sync(() =>
        {
            var payload = Json(new { matchId = Match.Id, awayTeamName = "勇士", homeTeamName = "湖人", awayScore = 110, homeScore = 109, matchStatus = "INPROGRESS", currentQuarter = 5, frontEndMatchStatus = new { desc = "加时1 04:30" } });
            var match = AnchorParser.Match(payload, Match);
            Check(match.Game.Status == GameStatus.Live && match.Game.AwayScore == 110 && match.Game.Progress.Contains("4:30"), "mobile score and overtime");
            var noClock = AnchorParser.Match(Json(new { matchId = Match.Id, awayTeamName = "勇士", homeTeamName = "湖人", matchStatus = "INPROGRESS", currentQuarter = 5 }), Match);
            Check(noClock.Game.Progress == "加1 · 时间未提供" && noClock.Game.AwayScore is null, "missing stays missing");
            Throws(() => AnchorParser.Match(payload, Match with { Id = "wrong" }));
        })),
        ("Anchor messages preserve same-time server order, literal fragments and correction identity", () => Sync(() =>
        {
            var messages = AnchorParser.Messages(Json(new[] {
                new { commentId = "3011690597", content = "比赛开始！！", nickName = "犀牛", time = "2026-10-22 10:20:37" },
                new { commentId = "3011612293", content = "5-6", nickName = "犀牛", time = "2026-10-22 10:20:37" } }));
            var timeline = new AnchorTimeline(); timeline.Merge(Room, messages);
            Check(timeline.History.Select(e => e.Text).SequenceEqual(new[] { "比赛开始！！", "5-6" }), "don't sort numeric IDs");
            Check(timeline.History[0].PublishedAt?.Offset == TimeSpan.FromHours(8) && timeline.History[0].Origin == TextOrigin.Anchor, "source and timezone");
            Check(timeline.Merge(Room, [messages[0] with { Text = "修正原文" }]).Count == 0 && timeline.History[0].Text == "修正原文", "correction isn't unread");
            Check(timeline.History.All(e => e.Score == "" && e.Period == 0), "never infer event score or period from prose");
        })),
        ("Anchor initialization drains thirty-message pages before publishing a non-unread baseline", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock(); var requested = new List<string?>();
            source.Messages = cursor =>
            {
                requested.Add(cursor);
                return cursor switch { null => Enumerable.Range(1, 30).Select(M).ToArray(), "30" => [M(31)], _ => [] };
            };
            using var session = new AnchorSession(source, step.Delay, () => step.Now); var tracker = new RecapTracker();
            session.Changed += () => tracker.Observe(session.State);
            await session.Select(Game); await Until(() => session.State.History.Count == 31);
            Check(requested.SequenceEqual(new string?[] { null, "30", "31" }) && tracker.UnreadCount == 0 && session.State.AddedEntryKeys.Count == 0, "oldest first, empty marks sync boundary");
            await session.StopAsync();
        }),
        ("Anchor startup failure retains its cursor and resumes baseline without counting old messages", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock(); var calls = 0;
            source.Messages = cursor => { calls++; return calls switch { 1 => [M(1)], 2 => throw new HttpRequestException(), 3 => cursor == "1" ? new[] { M(2) } : throw new Exception("cursor lost"), _ => [] }; };
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => session.State.HasError && step.Waiting);
            Check(session.State.Snapshot is null, "partial history isn't mistaken for live");
            step.Step(); await Until(() => session.State.History.Count == 2 && !session.State.HasError);
            Check(session.State.AddedEntryKeys.Count == 0, "resumed initial pages remain baseline"); await session.StopAsync();
        }),
        ("Anchor failures retain displayed text, recover with new entries and independent score timestamps", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock(); var calls = 0;
            source.Messages = _ => ++calls switch { 1 => [M(1)], 2 => [], 3 => throw new HttpRequestException(), 4 => [M(2)], _ => [] };
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => session.State.History.Count == 1 && step.Waiting);
            step.Step(); await Until(() => session.State.HasError && step.Waiting);
            Check(session.HasRoom && session.State.History.Count == 1, "don't fall back on temporary failure");
            Check(step.LastDelay.TotalSeconds == 5, "retry floor");
            step.Step(); await Until(() => session.State.History.Count == 2 && !session.State.HasError);
            Check(session.State.LastBatchSize == 1 && session.ScoreFetchedAt is not null, "new event and metadata kept separate"); await session.StopAsync();
        }),
        ("Ended anchor rooms collect late tail messages then require two quiet checks five seconds apart", async () =>
        {
            var source = new FakeAnchor { RoomValue = Room with { Finished = true } }; var step = new StepClock(); var calls = 0;
            source.Messages = _ => ++calls switch { 1 => [M(1)], 2 => [], 3 => [M(2)], _ => [] };
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => session.State.History.Count == 1 && step.Waiting);
            step.Step(); await Until(() => session.State.History.Count == 2 && step.Waiting);
            Check(!session.Completion.IsCompleted, "new tail resets quiet period");
            step.Step(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(step.Now >= StepClock.Start.AddSeconds(10), "two empty checks after last nonempty page"); await session.StopAsync();
        }),
        ("Cancelling an anchor match ignores a late response from the old selection", async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<AnchorMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new FakeAnchor { Pending = pending }; var step = new StepClock();
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => source.MessageCalls > 0);
            var stop = session.Select(null); pending.SetResult([M(1)]); await stop;
            Check(!session.HasRoom && session.State.History.Count == 0 && session.State.SelectedGame is null, "old response never displayed"); await session.StopAsync();
        }),
        ("Latest delivery replaces bursts immediately, freezes on hover and switches to paged at the latest boundary", () => Sync(() =>
        {
            var feed = new TextPlayback { Delivery = TextDelivery.Latest };
            var first = State([E(1)]); feed.Observe(first); feed.Refresh(2, _ => true, true, true, StepClock.Start);
            feed.Observe(State(Enumerable.Range(1, 40).Select(E).ToArray())); feed.Refresh(2, _ => true, true, false, StepClock.Start.AddSeconds(1));
            Check(feed.Visible[0].Text == "消息1", "hover fixes visible page");
            feed.Refresh(2, _ => true, true, true, StepClock.Start.AddSeconds(2));
            Check(feed.Visible[0].Text == "消息40" && feed.PendingCount == 0, "no four-second wait");
            feed.Delivery = TextDelivery.Paged; feed.Refresh(2, _ => true, true, true, StepClock.Start.AddSeconds(2));
            Check(feed.PendingCount == 0 && feed.Visible[0].Text == "消息40", "mode changes don't replay historical backlog");
        })),
        ("Source switches reset visible playback but keep independent recap boundaries and literal bookmark origins", () => Sync(() =>
        {
            var structured = State([E(1)]) with { FeedKey = "structured", Origin = TextOrigin.Structured };
            var anchor = State([E(1), E(2)]) with { FeedKey = "anchor:" + Room.Key, Origin = TextOrigin.Anchor };
            var feed = new TextPlayback(); feed.Observe(structured); feed.Refresh(2, _ => true, true, true, StepClock.Start);
            feed.Observe(anchor); feed.Refresh(2, _ => true, true, true, StepClock.Start);
            Check(feed.PendingCount == 0 && feed.Visible.Count == 2, "switch is a new baseline");
            var left = new RecapTracker(); var right = new RecapTracker(); left.Observe(structured); right.Observe(anchor);
            var captured = right.Capture(StepClock.Start); right.MarkViewed(captured);
            right.Observe(anchor with { History = [E(1), E(2), E(3)], HistoryRevision = 2 });
            Check(left.UnreadCount == 0 && right.UnreadCount == 1 && captured.Entries.Count == 2, "reading capture stays fixed");
            Check(new BookmarkedEvent(Game, E(1), StepClock.Start).Display.Contains("主播解说"), "saved origin label");
        })),
        ("Mobile score failure is visible without discarding healthy anchor messages", async () =>
        {
            var source = new FakeAnchor { ScoreFailure = new SourceFormatException("invalid score") }; var step = new StepClock();
            source.Messages = cursor => cursor is null ? [M(1)] : [];
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => session.State.History.Count == 1);
            Check(!session.State.HasError && session.State.ScoreMessage == "比分数据源异常" && session.State.Snapshot?.Game.AwayScore == 105, "independent metadata failure preserves last score and text");
            Check(SessionStatus.Describe(session.State, step.Now).Contains("比分数据源异常"), "status discloses score failure"); await session.StopAsync();
        }),
        ("An empty initial snapshot remains a baseline so a later first burst is fully paged", () => Sync(() =>
        {
            var feed = new TextPlayback(); feed.Observe(State([])); feed.Refresh(2, _ => true, true, true, StepClock.Start);
            feed.Observe(State(Enumerable.Range(1, 8).Select(E).ToArray())); feed.Refresh(2, _ => true, true, true, StepClock.Start.AddSeconds(1));
            Check(feed.Visible.Reverse().Select(e => e.Text).SequenceEqual(new[] { "消息1", "消息2" }) && feed.PendingCount == 6, "first real batch isn't mistaken for old history");
        })),
        ("Legacy settings and bookmark entries keep defaults while new source and delivery preferences roundtrip", () => Sync(() =>
        {
            var old = JsonSerializer.Deserialize<AppSettings>("{\"Width\":280,\"FontSize\":16}")!.Validate();
            Check(old.TextSource == TextSourcePreference.AnchorPreferred && old.TextDelivery == TextDelivery.Latest && old.Width == 280, "migration preserves appearance");
            var updated = old with { TextSource = TextSourcePreference.Structured, TextDelivery = TextDelivery.Paged };
            Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(updated))!.Validate() == updated, "roundtrip");
            Check((updated with { TextSource = (TextSourcePreference)20, TextDelivery = (TextDelivery)20 }).Validate().TextDelivery == TextDelivery.Latest, "invalid enums default safely");
            Check(JsonSerializer.Deserialize<LiveEntry>("{\"SourceId\":\"1\",\"Text\":\"旧原文\",\"Clock\":\"\",\"Team\":\"\",\"Score\":\"\",\"Order\":1,\"Period\":0}")!.Origin == TextOrigin.Structured, "legacy bookmark origin");
        })),
        ("Structured and anchor consumers share one HTTP gate and send no authentication or cookies", async () =>
        {
            var handler = new GateHandler(); using var transport = new WebGameSource(handler); var source = new AnchorSource(transport);
            await Task.WhenAll(source.GetMessages(Room, null, default), source.GetMessages(Room, "30", default),
                transport.GetGames(Game.Date, default));
            Check(handler.Maximum == 1 && handler.Cursors.Contains("commentId=30") && !handler.HasCredentials && handler.JsonAcceptCorrect, "one in-flight request across HTML and JSON, with correct content negotiation");
        }),
        ("Coordinator labels absent anchors and keeps structured analytics separate from mobile header scores", async () =>
        {
            var source = new StaticGames(); var anchor = new FakeAnchor { RoomValue = null };
            using var coordinator = new TextFeedCoordinator(source, anchor, TextSourcePreference.AnchorPreferred);
            SessionState? analytics = null; coordinator.StructuredChanged += state => analytics = state;
            await coordinator.Select(Game); await Until(() => coordinator.State.SourceLabel.Contains("暂无主播"));
            Check(coordinator.State.Origin == TextOrigin.Structured && analytics?.History.Single().Origin == TextOrigin.Structured, "explicit fallback and structured-only consumers");
            await coordinator.SetPreference(TextSourcePreference.Structured);
            Check(coordinator.State.SourceLabel == "赛况实录", "explicit source doesn't query anchors"); await coordinator.StopAsync();
        }),
        ("Anchor retries follow five-ten-twenty-thirty backoff and reject stalled cursors", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock();
            source.Messages = _ => throw new HttpRequestException();
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game);
            foreach (var expected in new[] { 5, 10, 20, 30 })
            {
                await Until(() => step.Waiting && step.LastDelay.TotalSeconds == expected);
                if (expected != 30) step.Step();
            }
            await session.StopAsync();
            var stalled = new FakeAnchor { Messages = _ => [M(1)] }; var otherStep = new StepClock();
            using var other = new AnchorSession(stalled, otherStep.Delay, () => otherStep.Now);
            await other.Select(Game); await Until(() => other.State.HasError);
            Check(other.State.Message.Contains("数据源异常") && stalled.MessageCalls == 2, "a repeated cursor cannot spin forever"); await other.StopAsync();
        }),
        ("An absent anchor is checked again after sixty seconds and can become available", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock(); var rooms = 0;
            source.RoomResolver = () => ++rooms == 1 ? null : Room;
            source.Messages = cursor => cursor is null ? [M(1)] : [];
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => step.Waiting);
            Check(!session.HasRoom && session.Availability.Contains("暂无主播"), "no anchor is explicit");
            while (step.Now < StepClock.Start.AddSeconds(60))
            {
                var before = step.Now; step.Step(); await Until(() => step.Now > before && step.Waiting);
            }
            await Until(() => session.State.History.Count == 1);
            Check(rooms == 2 && session.HasRoom, "recheck starts anchor subscription"); await session.StopAsync();
        }),
        ("Switching away pauses unused anchor requests and switching back preserves the cursor and history", async () =>
        {
            var source = new FakeAnchor(); var step = new StepClock(); var cursors = new List<string?>();
            source.Messages = cursor => { cursors.Add(cursor); return cursor is null ? new[] { M(1) } : []; };
            using var session = new AnchorSession(source, step.Delay, () => step.Now);
            await session.Select(Game); await Until(() => session.State.History.Count == 1 && step.Waiting);
            var history = session.State.History; session.SetEnabled(false); step.Step(); await Until(() => !step.Waiting);
            Check(source.MessageCalls == 2 && ReferenceEquals(history, session.State.History), "paused source keeps history without polling");
            session.SetEnabled(true); await Until(() => source.MessageCalls == 3);
            Check(cursors[^1] == "1" && cursors.Count(c => c is null) == 1 && ReferenceEquals(history, session.State.History), "resumes the previous cursor"); await session.StopAsync();
        }),
        ("Coordinator uses mobile header snapshots while statistics and scoring retain original structured events", async () =>
        {
            var source = new FakeAnchor { UpdatedGame = Game with { AwayScore = 999 } };
            source.Messages = cursor => cursor is null ? [M(1)] : [];
            using var coordinator = new TextFeedCoordinator(new StaticGames(), source, TextSourcePreference.AnchorPreferred);
            await coordinator.Select(Game); await Until(() => coordinator.State.Snapshot is not null && coordinator.State.Origin == TextOrigin.Anchor);
            Check(coordinator.State.Snapshot!.Game.AwayScore == 999 && coordinator.StructuredState.Snapshot!.Game.AwayScore == 105, "header cannot overwrite analytics metadata");
            Check(coordinator.StructuredState.History.All(e => e.Origin == TextOrigin.Structured), "human prose never reaches analytics");
            var previous = coordinator.State.History;
            await coordinator.SetPreference(TextSourcePreference.Structured);
            await coordinator.SetPreference(TextSourcePreference.AnchorPreferred);
            Check(ReferenceEquals(coordinator.State.History, previous), "source toggles keep anchor history"); await coordinator.StopAsync();
        })
    ];
    private static AnchorMessage M(int id) => new(id.ToString(), "消息" + id, "犀牛", StepClock.Start, false);
    private static LiveEntry E(int id) => new("anchor:" + Room.Key + ":" + id, 0, "", "", "消息" + id, "", id) { Origin = TextOrigin.Anchor };
    private static SessionState State(LiveEntry[] history) => new(new(Game, history, StepClock.Start), history, "", false, false) { SelectedGame = Game, HistoryRevision = 1, Origin = TextOrigin.Anchor, FeedKey = "anchor:" + Room.Key };
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (SourceFormatException) { return; } throw new Exception("expected source format error"); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, timeout.Token); }
    private sealed class StepClock
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 22, 2, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now { get; private set; } = Start;
        public TimeSpan LastDelay { get; private set; }
        public bool Waiting { get; private set; }
        private readonly SemaphoreSlim _steps = new(0);
        public async Task Delay(TimeSpan duration, CancellationToken ct)
        { LastDelay = duration; Waiting = true; await _steps.WaitAsync(ct); Waiting = false; Now += duration; }
        public void Step() => _steps.Release();
    }
    private sealed class FakeAnchor : IAnchorSource
    {
        public AnchorRoom? RoomValue { get; init; } = Room;
        public Func<string?, IReadOnlyList<AnchorMessage>> Messages { get; set; } = _ => [];
        public TaskCompletionSource<IReadOnlyList<AnchorMessage>>? Pending { get; init; }
        public int MessageCalls;
        public Func<AnchorRoom?>? RoomResolver;
        public GameInfo? UpdatedGame;
        public Exception? ScoreFailure;
        public Task<MobileMatch?> Resolve(GameInfo game, CancellationToken ct) => Task.FromResult<MobileMatch?>(Match with { Game = game });
        public Task<MobileMatch> GetMatch(MobileMatch match, CancellationToken ct) => ScoreFailure is null ? Task.FromResult(UpdatedGame is null ? match : match with { Game = UpdatedGame }) : Task.FromException<MobileMatch>(ScoreFailure);
        public Task<AnchorRoom?> GetRoom(MobileMatch match, CancellationToken ct) => Task.FromResult(RoomResolver is null ? RoomValue : RoomResolver());
        public Task<IReadOnlyList<AnchorMessage>> GetMessages(AnchorRoom room, string? cursor, CancellationToken ct)
        { Interlocked.Increment(ref MessageCalls); return Pending?.Task ?? Task.FromResult(Messages(cursor)); }
    }
    private sealed class StaticGames : IGameSource
    {
        public Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken ct) => Task.FromResult<IReadOnlyList<GameInfo>>([Game]);
        public Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken ct) => Task.FromResult(new LiveSnapshot(Game, [new("s", 1, "10:00", "", "赛况", "2-0", 1)], StepClock.Start));
    }
    private sealed class GateHandler : HttpMessageHandler
    {
        private int _active;
        public int Maximum;
        public bool HasCredentials;
        public bool JsonAcceptCorrect = true;
        public readonly List<string> Cursors = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Maximum = Math.Max(Maximum, Interlocked.Increment(ref _active));
            try
            {
                HasCredentials |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
                if (request.RequestUri!.Host == "live-api.liangle.com") JsonAcceptCorrect &= request.Headers.Accept.Any(a => a.MediaType == "application/json");
                if (request.RequestUri!.Query.Contains("commentId=")) Cursors.Add(request.RequestUri.Query.TrimStart('?').Split('&').Last());
                await Task.Delay(10, ct);
                var text = request.RequestUri.Host == "live-api.liangle.com" ? Json(Array.Empty<object>()) :
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "games-2026-10-21.html"));
                return new(HttpStatusCode.OK) { Content = new StringContent(text) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
