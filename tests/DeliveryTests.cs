using NbaLive.Core;
using System.Net;

internal static class DeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 2, 0, 0, TimeSpan.Zero);
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Initial history is a baseline and a forty-event burst is displayed without skips", () => Sync(() =>
        {
            var feed = new TextPlayback(); var first = Entries(100);
            feed.Observe(State(first)); Refresh(feed, Now);
            Check(feed.Visible.Select(e => e.SourceId).SequenceEqual(new[] { "100", "99" }) && feed.PendingCount == 0, "don't replay initial history");
            feed.Observe(State(Entries(140))); Refresh(feed, Now.AddSeconds(1));
            Check(feed.Visible[0].SourceId == "100" && feed.PendingCount == 40, "new batch cannot overwrite a page before four seconds");
            var shown = new List<string>();
            for (var page = 1; page <= 20; page++) { Refresh(feed, Now.AddSeconds(page * 4)); shown.AddRange(feed.Visible.Reverse().Select(e => e.SourceId!)); }
            Check(shown.SequenceEqual(Enumerable.Range(101, 40).Select(i => i.ToString())) && !feed.IsReplaying, "every new identity is shown in chronological two-event pages");
        })),
        ("Repeated fetches and corrections do not enqueue duplicates or change a frozen page", () => Sync(() =>
        {
            var feed = new TextPlayback(); var first = State(Entries(2)); feed.Observe(first); Refresh(feed, Now);
            feed.Observe(first); feed.Observe(State([E(1), E(2) with { Text = "修正：两分命中" }]));
            Refresh(feed, Now.AddSeconds(20), canAdvance: false);
            Check(feed.Visible[0].Text != "修正：两分命中" && feed.PendingCount == 0, "hover freeze retains old text without fake new events");
            Refresh(feed, Now.AddSeconds(21));
            Check(feed.Visible[0].Text == "修正：两分命中" && feed.PendingCount == 0, "resume applies correction to existing identity");
            feed.Observe(State(Entries(6))); Refresh(feed, Now.AddSeconds(22));
            var pending = feed.PendingCount; feed.Observe(State(Entries(6))); Refresh(feed, Now.AddSeconds(22));
            Check(feed.PendingCount == pending, "identical polls do not duplicate pending entries");
        })),
        ("Hidden, hovered, collapsed and non-text consumers don't drain unread playback", () => Sync(() =>
        {
            var feed = new TextPlayback(); feed.Observe(State(Entries(2))); Refresh(feed, Now);
            feed.Observe(State(Entries(8)));
            for (var i = 1; i <= 4; i++) Refresh(feed, Now.AddHours(i), canAdvance: false);
            Check(feed.PendingCount == 6 && feed.Visible[0].SourceId == "2", "invisible pages are never counted as shown");
            Refresh(feed, Now.AddHours(5));
            Check(feed.Visible[0].SourceId == "4" && feed.PendingCount == 4, "restoring starts with the oldest unseen page");
            feed.Refresh(0, _ => true, true, true, Now.AddHours(6));
            Check(feed.PendingCount == 4, "score-only density doesn't silently consume events");
            var shortPage = new TextPlayback(); shortPage.Observe(State(Entries(2))); Refresh(shortPage, Now);
            shortPage.Observe(State(Entries(8))); Refresh(shortPage, Now.AddSeconds(4));
            shortPage.Suspend(Now.AddSeconds(4.2)); Refresh(shortPage, Now.AddSeconds(20), canAdvance: false);
            Refresh(shortPage, Now.AddSeconds(21));
            Check(shortPage.Visible[0].SourceId == "4", "time hidden doesn't consume the current page's remaining reading time");
            Refresh(shortPage, Now.AddSeconds(25));
            Check(shortPage.Visible[0].SourceId == "6", "resume gives the paused page its remaining dwell before advancing");
        })),
        ("Late events are read once and playback returns to the newest timeline", () => Sync(() =>
        {
            var feed = new TextPlayback(); feed.Observe(State([E(10), E(20)])); Refresh(feed, Now);
            feed.Observe(State([E(5), E(10), E(20)])); Refresh(feed, Now.AddSeconds(4));
            Check(feed.Visible.Count == 1 && feed.Visible[0].SourceId == "5" && feed.IsReplaying, "late arrival is not hidden behind already displayed newer events");
            Refresh(feed, Now.AddSeconds(8));
            Check(feed.Visible[0].SourceId == "20" && !feed.IsReplaying, "after the dwell restore recent events rather than remain on a late old message");
        })),
        ("Playback waits for player matching and applies filters without discarding queued matches", () => Sync(() =>
        {
            var feed = new TextPlayback(); feed.Observe(State([E(0) with { Text = "普通事件" }]));
            feed.Refresh(2, e => e.Text.Contains("命中"), false, true, Now);
            var entries = new[] { E(0) with { Text = "普通事件" }, E(1) with { Text = "换人" }, E(2), E(3) with { Text = "篮板" }, E(4) };
            feed.Observe(State(entries)); feed.Refresh(2, e => e.Text.Contains("命中"), false, true, Now.AddSeconds(5));
            Check(feed.PendingCount == 4 && feed.Visible.Count == 0, "missing metadata does not consume potentially matching arrivals");
            feed.Refresh(2, e => e.Text.Contains("命中"), true, true, Now.AddSeconds(6));
            Check(feed.Visible.Select(e => e.SourceId).SequenceEqual(new[] { "4", "2" }) && feed.PendingCount == 0, "both matched events survive until metadata is ready");
        })),
        ("Jump-to-latest and game reselection preserve history and independent recap unread counts", () => Sync(() =>
        {
            var feed = new TextPlayback(); var recap = new RecapTracker(); var first = State(Entries(2));
            feed.Observe(first); recap.Observe(first); Refresh(feed, Now);
            var next = State(Entries(8)); feed.Observe(next); recap.Observe(next);
            feed.JumpToLatest(); Refresh(feed, Now.AddSeconds(1));
            Check(feed.Visible[0].SourceId == "8" && feed.PendingCount == 0 && next.History.Count == 8 && recap.UnreadCount == 6, "manual skip affects only playback");
            feed.Observe(new(null, [], "重新连接", false, true) { SelectedGame = Game() });
            feed.Observe(State(Entries(3))); Refresh(feed, Now.AddSeconds(2));
            Check(feed.Visible[0].SourceId == "3" && feed.PendingCount == 0, "same-game reselection establishes a fresh baseline");
        })),
        ("Polling cadence subtracts fetch time but preserves retry policy and pregame intervals", () => Sync(() =>
        {
            Check(PollingPolicy.AfterSuccess(Game(), Now, TimeSpan.FromSeconds(3)) == TimeSpan.FromSeconds(2), "five seconds between starts, not fetch duration plus five");
            Check(PollingPolicy.AfterSuccess(Game(), Now, TimeSpan.FromSeconds(8)) == TimeSpan.Zero, "slow fetch doesn't add another full wait");
            Check(PollingPolicy.AfterSuccess(Game() with { Status = GameStatus.Scheduled, Progress = "未开始" }, Now, TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(22), "far pregame still targets thirty seconds");
        })),
        ("Queued live requests outrank secondary reads and cancelled waiters don't hold the gate", async () =>
        {
            var handler = new DeliveryHandler { HoldBox = true };
            using var source = new WebGameSource(handler);
            var first = source.GetBoxScore("168863", CancellationToken.None);
            await handler.BoxStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var cancel = new CancellationTokenSource();
            var cancelled = source.GetGames(MatchTime.Today, cancel.Token); cancel.Cancel();
            var secondary = source.GetGames(MatchTime.Today, CancellationToken.None);
            var live = source.GetSnapshot("168863", CancellationToken.None);
            handler.ReleaseBox.TrySetResult();
            await Task.WhenAll(first, secondary, live);
            try { await cancelled; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Check(handler.Paths.Count == 3 && handler.Paths[1].Contains("playbyplay") && handler.MaxActive == 1, "one HTTP request, live wins next slot, cancelled waiter never reaches network");
        }),
        ("Slow optional jersey metadata is bounded and releases the channel for live text", async () =>
        {
            var handler = new DeliveryHandler { HoldRosters = true };
            using var source = new WebGameSource(handler, loadRosters: true);
            var stats = source.GetBoxScore("168863", CancellationToken.None);
            await handler.RosterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var live = source.GetSnapshot("168863", CancellationToken.None);
            await live.WaitAsync(TimeSpan.FromSeconds(4));
            var snapshot = await stats.WaitAsync(TimeSpan.FromSeconds(5));
            Check(snapshot.Away.Total?.Points == 106 && snapshot.Home.Players.Count == 17 && handler.MaxActive == 1 && handler.Paths[2].Contains("playbyplay"), "timed-out optional data doesn't discard stats or block live behind the second roster");
        })
    ];
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static GameInfo Game() => new("1", "湖人", "勇士", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
    private static LiveEntry E(int i) => new(i.ToString(), 3, "04:32", "湖人", "球员命中 " + i, "86-82", i);
    private static LiveEntry[] Entries(int count) => Enumerable.Range(1, count).Select(E).ToArray();
    private static SessionState State(IReadOnlyList<LiveEntry> entries) => new(new(Game(), entries, Now), entries, "", false, false) { SelectedGame = Game(), HistoryRevision = entries.Count };
    private static void Refresh(TextPlayback feed, DateTimeOffset now, bool canAdvance = true) => feed.Refresh(2, _ => true, true, canAdvance, now);
    private sealed class DeliveryHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource BoxStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseBox = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RosterStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Paths = [];
        internal bool HoldBox, HoldRosters;
        private int _active;
        internal int MaxActive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active);
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            try
            {
                if (path.Contains("boxscore") && HoldBox) { BoxStarted.TrySetResult(); await ReleaseBox.Task.WaitAsync(ct); }
                if (path.StartsWith("/teams/", StringComparison.Ordinal) && HoldRosters) { RosterStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                var fixture = path.Contains("boxscore") ? "boxscore-168863.html" : path.Contains("playbyplay") ? "finished-168863.html" : "games-2026-10-21.html";
                return new(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
