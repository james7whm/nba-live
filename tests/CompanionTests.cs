using NbaLive.Core;
using System.Text.Json;

internal static class CompanionTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Partial settings keep appearance; favorites normalize and roundtrip", () => Sync(() =>
        {
            var old = JsonSerializer.Deserialize<AppSettings>("{\"Width\":400,\"DisplayMode\":1}")!.Validate();
            Check(!old.CapsuleEnabled && old.ContentMode == OverlayContent.Text && old.DisplayMode == OverlayMode.OneEvent && old.FollowedPlayers.Length == 0, "omitted fields use defaults without changing the selected density");
            var configured = old with { CapsuleEnabled = true, ContentMode = OverlayContent.Players,
                FollowedPlayers = [new(" a ", " 甲 "), new("a", "重复"), null!, new("", "无效"), new("b", "乙"), new("c", "丙"), new("d", "丁")] };
            var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(configured))!.Validate();
            Check(loaded.CapsuleEnabled && loaded.ContentMode == OverlayContent.Players && loaded.FollowedPlayers.Select(p => p.Id).SequenceEqual(new[] { "a", "b", "c" }), "trim, deduplicate and enforce three favorites");
            Check((old with { ContentMode = (OverlayContent)99, FollowedPlayers = null! }).Validate().ContentMode == OverlayContent.Text, "damaged settings recover");
        })),
        ("Favorite selection enforces the limit without swapping players", () => Sync(() =>
        {
            FavoritePlayer[] favorites = [new("a", "甲"), new("b", "乙"), new("c", "丙")];
            Check(PlayerFavorites.Toggle(favorites, new("d", "丁"), out var rejected) is not null && rejected.SequenceEqual(favorites), "limit leaves the list intact");
            Check(PlayerFavorites.Toggle(favorites, new("b", "乙"), out var removed) is null && removed.Select(p => p.Id).SequenceEqual(new[] { "a", "c" }), "remove keeps order");
            Check(PlayerFavorites.Toggle(removed, new("d", "甲"), out var added) is null && added[^1].Id == "d", "identity comes from source id, not display name");
        })),
        ("Player rotation pauses on hover and hide, permits manual browsing, and retains identity", () => Sync(() =>
        {
            FavoritePlayer[] players = [new("a", "甲"), new("b", "乙"), new("c", "丙")];
            var rotation = new PlayerRotation(); rotation.Sync(players);
            rotation.Move(players, 1, automatic: true, paused: true);
            rotation.Move(players, 1, automatic: true, visible: false);
            Check(rotation.CurrentId == "a", "automatic rotation pauses");
            rotation.Move(players, -1, paused: true);
            Check(rotation.CurrentId == "c", "manual previous wraps while paused");
            rotation.Sync([players[0], players[2]]);
            Check(rotation.CurrentId == "c", "removing another favorite does not jump");
            rotation.Move([players[0], players[2]], 1, automatic: true);
            Check(rotation.CurrentId == "a", "visible automatic rotation wraps");
            rotation.Sync([]); rotation.Move([], 1);
            Check(rotation.CurrentId is null, "empty selection is safe");
        })),
        ("Player cards preserve missing values, DNP, source identity and game boundaries", () => Sync(() =>
        {
            var source = Snapshot("1");
            var favorite = new FavoritePlayer("p1", "球员甲");
            var state = new StatisticsState(source.Game, source, "", false, false);
            var card = PlayerCards.Create(favorite, state, "1");
            Check(card.FirstLine.Contains("28 分") && card.FirstLine.Contains("— 板") && card.SecondLine.Contains("28:36"), "read source data without fake zero");
            Check(PlayerCards.Create(favorite, state, "2").SecondLine.Contains("等待"), "never show another game's statistics");
            Check(PlayerCards.Create(new("other", "球员甲"), state, "1").SecondLine.Contains("暂无"), "same name never substitutes another id");
            var dnp = source.Away.Players[0] with { Status = "DNP 未出场", Points = null };
            var dnpState = state with { Snapshot = source with { Away = source.Away with { Players = [dnp] } } };
            Check(PlayerCards.Create(favorite, dnpState, "1").SecondLine.Contains("未出场"), "keep DNP reason");
        })),
        ("Statistics consumers share one subscription, release only the last demand, and reuse cached data", async () =>
        {
            var source = new CountingStatistics();
            using var coordinator = new StatisticsCoordinator(source);
            await coordinator.SetGame(Game("1"));
            Check(source.Calls == 0, "no consumer means no request");
            await coordinator.SetDemand(StatisticsConsumer.PlayerOverlay, true);
            await Until(() => coordinator.State.Snapshot is not null);
            var cached = coordinator.State.Snapshot;
            await coordinator.SetDemand(StatisticsConsumer.Window, true);
            await coordinator.SetDemand(StatisticsConsumer.Window, false);
            Check(source.Calls == 1, "opening and closing statistics does not duplicate the player subscription");
            await coordinator.SetDemand(StatisticsConsumer.PlayerOverlay, false);
            Check(coordinator.State.Snapshot == cached, "no consumer retains a cache, not a running poll");
            var states = new List<StatisticsState>(); coordinator.Changed += states.Add;
            await coordinator.SetDemand(StatisticsConsumer.Window, true);
            await Until(() => source.Calls == 2 && !coordinator.State.IsLoading);
            Check(states.Any(s => s.IsLoading && s.Snapshot == cached) && source.MaxActive == 1, "resume retains values and starts just once");
            await coordinator.SetDemand(StatisticsConsumer.Window, false);
        }),
        ("Shared statistics clear on switching and ignore a cancelled old game's late response", async () =>
        {
            var source = new HeldStatistics();
            using var coordinator = new StatisticsCoordinator(source);
            await coordinator.SetDemand(StatisticsConsumer.PlayerOverlay, true);
            await coordinator.SetGame(Game("1"));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var seen = new List<StatisticsState>(); coordinator.Changed += seen.Add;
            var changing = coordinator.SetGame(Game("2"));
            Check(coordinator.State.SelectedGame?.Id == "2" && coordinator.State.Snapshot is null, "clear before awaiting cancellation");
            source.Release.TrySetResult(); await changing;
            await Until(() => coordinator.State.Snapshot?.Game.Id == "2");
            Check(!seen.Any(s => s.Snapshot?.Game.Id == "1") && source.MaxActive == 1, "late response cannot leak or overlap");
        }),
        ("First recap previews the last ten without counting initial history as unread", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State(Enumerable.Range(1, 40).Select(i => Entry(i)).ToArray()));
            var recap = tracker.Capture(DateTimeOffset.UtcNow);
            Check(tracker.UnreadCount == 0 && recap.Entries.Count == 10 && recap.Entries[0].SourceId == "31", "initial snapshot is a preview, not forty unread items");
            Check(recap.Highlights.Count == 5 && recap.Highlights[0].Order < recap.Highlights[^1].Order, "at most five chronological highlights");
            tracker.MarkViewed(recap);
            Check(tracker.Capture(DateTimeOffset.UtcNow).Entries.Count == 0, "subsequent recap only gets new entries");
        })),
        ("A delayed first recap establishes its captured boundary and keeps racing arrivals unread", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State([Entry(1)]));
            tracker.Observe(State(Enumerable.Range(1, 25).Select(Entry).ToArray()));
            var first = tracker.Capture(DateTimeOffset.UtcNow);
            tracker.Observe(State(Enumerable.Range(1, 26).Select(Entry).ToArray()));
            tracker.MarkViewed(first);
            Check(first.Entries.Count == 10 && tracker.UnreadCount == 1, "first preview establishes its boundary, not the later arrival");
            Check(tracker.Capture(DateTimeOffset.UtcNow).Entries.Single().SourceId == "26", "subsequent recap starts after first review");
        })),
        ("Recap read boundary only consumes the captured range while new arrivals stay unread", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State([Entry(1)]));
            tracker.MarkViewed(tracker.Capture(DateTimeOffset.UtcNow));
            tracker.Observe(State([Entry(1), Entry(2)]));
            var openWindow = tracker.Capture(DateTimeOffset.UtcNow);
            tracker.Observe(State([Entry(1), Entry(2), Entry(3)]));
            tracker.MarkViewed(openWindow);
            Check(tracker.UnreadCount == 1 && openWindow.Entries.Single().SourceId == "2", "new arrival does not change an open snapshot");
            var next = tracker.Capture(DateTimeOffset.UtcNow);
            Check(next.Entries.Single().SourceId == "3", "remaining event is available next time");
            tracker.MarkViewed(next); tracker.MarkViewed(openWindow);
            Check(tracker.UnreadCount == 0, "old window cannot rewind the read boundary");
        })),
        ("Re-reading ten recent events does not consume older unread events", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State([Entry(1)]));
            tracker.MarkViewed(tracker.Capture(DateTimeOffset.UtcNow));
            tracker.Observe(State(Enumerable.Range(1, 25).Select(Entry).ToArray()));
            var recent = tracker.Capture(DateTimeOffset.UtcNow, true); tracker.MarkViewed(recent);
            Check(tracker.UnreadCount == 14, "only the ten actually displayed events become read");
            var remaining = tracker.Capture(DateTimeOffset.UtcNow);
            Check(remaining.Entries.First().SourceId == "2" && remaining.Entries.Last().SourceId == "15", "older unread range is retained");
            tracker.MarkViewed(remaining); Check(tracker.UnreadCount == 0, "reading the remainder closes the gap");
        })),
        ("Recap treats corrections as updates and late same-clock events as new arrivals", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State([Entry(10), Entry(20)]));
            var original = tracker.Capture(DateTimeOffset.UtcNow); tracker.MarkViewed(original);
            var corrected = Entry(10) with { Text = "来源修正：三分未命中" };
            tracker.Observe(State([corrected, Entry(20)]));
            Check(tracker.UnreadCount == 0 && tracker.Capture(DateTimeOffset.UtcNow, true).Entries[0].Text == corrected.Text, "correction isn't a new arrival");
            tracker.Observe(State([Entry(5), corrected, Entry(20), Entry(21)]));
            var recap = tracker.Capture(DateTimeOffset.UtcNow);
            Check(recap.Entries.Select(e => e.SourceId).SequenceEqual(new[] { "5", "21" }) && tracker.UnreadCount == 2, "backfill and same-clock events both survive");
            Check(original.Entries[0].Text != corrected.Text, "old captured content remains frozen");
        })),
        ("Recap resets on reselect and cannot consume initial data from a pre-fetch window", () => Sync(() =>
        {
            var tracker = new RecapTracker();
            tracker.Observe(new(null, [], "连接中", false, true) { SelectedGame = Game("1") });
            var preFetch = tracker.Capture(DateTimeOffset.UtcNow);
            tracker.Observe(State([Entry(1), Entry(2)])); tracker.MarkViewed(preFetch);
            Check(tracker.Capture(DateTimeOffset.UtcNow).Entries.Count == 2, "a pre-fetch preview cannot mark newly loaded data read");
            var old = tracker.Capture(DateTimeOffset.UtcNow); tracker.MarkViewed(old);
            tracker.Observe(new(null, [], "连接中", false, true) { SelectedGame = Game("1") });
            tracker.Observe(State([Entry(1), Entry(2), Entry(3)])); tracker.MarkViewed(old);
            Check(tracker.UnreadCount == 0 && tracker.Capture(DateTimeOffset.UtcNow).Entries.Count == 3, "same-game reselect starts a fresh preview");
        })),
        ("Recap shows literal score comparison and connection state instead of inferred scoring runs", () => Sync(() =>
        {
            var tracker = new RecapTracker(); tracker.Observe(State([Entry(1)]));
            tracker.MarkViewed(tracker.Capture(DateTimeOffset.UtcNow));
            var corrected = State([Entry(1)]) with { Snapshot = new(Game("1") with { AwayScore = 84 }, [Entry(1)], DateTimeOffset.UtcNow), HasError = true, Message = "连接中断" };
            tracker.Observe(corrected); var recap = tracker.Capture(DateTimeOffset.UtcNow);
            Check(recap.Heading.Contains("86 → 84") && recap.Heading.Contains("修正") && !recap.Heading.Contains("连得"), "score correction stays a literal comparison");
            Check(recap.Connection.Contains("连接中断") && recap.Entries.Count == 0, "keep content and disclose stale connection");
        })),
        ("Capsule expansion contains its old hit area at every corner and 100-200 percent DPI", () => Sync(() =>
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var area = new WorkArea(-2560, -200, 0, 1240);
                var width = (int)Math.Round(280 * scale); var height = (int)Math.Round(30 * scale);
                var expandedWidth = (int)Math.Round(480 * scale); var expandedHeight = (int)Math.Round(110 * scale);
                foreach (var point in new[] { new WindowPosition(area.Left + 16, area.Top + 16),
                    new(area.Right - width - 16, area.Top + 16), new(area.Left + 16, area.Bottom - height - 16),
                    new(area.Right - width - 16, area.Bottom - height - 16), new(-1000, 600), new(area.Right - width, area.Bottom - height) })
                {
                    var result = WindowPlacement.ExpandContaining(area, point.X, point.Y, width, height, expandedWidth, expandedHeight, scale);
                    Check(result.X <= point.X && result.Y <= point.Y && result.X + expandedWidth >= point.X + width && result.Y + expandedHeight >= point.Y + height, "expanded hit area includes the capsule");
                    Check(result.X >= area.Left && result.Y >= area.Top && result.X + expandedWidth <= area.Right && result.Y + expandedHeight <= area.Bottom, "expanded rectangle stays visible");
                }
            }
        }))
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static GameInfo Game(string id) => new(id, "客队", "主队", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
    private static LiveEntry Entry(int id) => new(id.ToString(), 3, "04:32", "客队", $"球员{id}上篮得分", "86-82", id);
    private static SessionState State(LiveEntry[] entries) => new(new(Game("1"), entries, DateTimeOffset.UtcNow), entries, "", false, false) { SelectedGame = Game("1"), HistoryRevision = entries.Length };
    private static BoxScoreSnapshot Snapshot(string id)
    {
        var game = Game(id);
        var player = new PlayerStatistics("p1", "球员甲", true, "G", "28:36", "10-18", "2-4", "6-6", 2, null, null, 7, 2, 1, 1, 1, 28, 4);
        return new(game, new(game.Away, [player], null), new(game.Home, [], null), DateTimeOffset.UtcNow);
    }
    private sealed class CountingStatistics : IStatisticsSource
    {
        internal int Calls, Active, MaxActive;
        public async Task<BoxScoreSnapshot> GetBoxScore(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            try { await Task.Delay(20, ct); return Snapshot(id); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class HeldStatistics : IStatisticsSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active, MaxActive;
        public async Task<BoxScoreSnapshot> GetBoxScore(string id, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            try { if (id == "1") { Started.TrySetResult(); await Release.Task; } return Snapshot(id); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
