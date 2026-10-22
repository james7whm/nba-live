using NbaLive.Core;
using System.Text.Json;

internal static class LiveCompanionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Old settings keep text defaults; lineup and followed-only preferences roundtrip", () => Sync(() =>
        {
            var old = JsonSerializer.Deserialize<AppSettings>("{\"ContentMode\":1,\"Width\":400}")!.Validate();
            Check(old.ContentMode == OverlayContent.Players && !old.FollowedPlayersOnly && old.Width == 400, "existing enum values and appearance stay compatible");
            var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old with { ContentMode = OverlayContent.Lineup, FollowedPlayersOnly = true }))!.Validate();
            Check(next.ContentMode == OverlayContent.Lineup && next.FollowedPlayersOnly, "new preferences persist");
            Check(new AppSettings().ContentMode == OverlayContent.Text && !new AppSettings().FollowedPlayersOnly, "upgrades do not switch modes or enable a filter");
        })),
        ("Overlay statistics demand covers lineup and filtered text without unnecessary subscriptions", () => Sync(() =>
        {
            var settings = new AppSettings();
            Check(!OverlayFeatures.NeedsStatistics(settings), "ordinary text has no statistics subscription");
            Check(OverlayFeatures.NeedsStatistics(settings with { ContentMode = OverlayContent.Lineup }), "lineup needs statistics without any favorites");
            Check(!OverlayFeatures.NeedsStatistics(settings with { ContentMode = OverlayContent.Players }), "empty player card has no subscription");
            settings = settings with { FollowedPlayers = [new("p1", "勒布朗-詹姆斯")] };
            Check(!OverlayFeatures.NeedsStatistics(settings) && OverlayFeatures.NeedsStatistics(settings with { FollowedPlayersOnly = true }), "text filtering requests roster only when needed");
            Check(OverlayFeatures.NeedsStatistics(settings with { ContentMode = OverlayContent.Players }), "player cards retain their subscription");
        })),
        ("Mini lineups show five confirmed identities for each team and update after substitutions", () => Sync(() =>
        {
            var box = Box(); var live = Live(box);
            foreach (var team in new[] { box.Away, box.Home })
            {
                var card = LineupCards.Create(team, box.Game, live, Now);
                Check(card.Players.Count == 5 && card.Players.Select(p => p.Id).Distinct().Count() == 5 && card.Color != TeamPalette.Unknown, "five identities with the proper team color");
            }
            var changed = live.History.Append(E(3, "湖人队换人（勒布朗-詹姆斯、安东尼-戴维斯、奥斯汀-里夫斯、八村垒、多尔顿-克内克特）")).ToArray();
            var result = LineupCards.Create(box.Away, box.Game, live with { History = changed }, Now);
            Check(result.Players.Any(p => p.Id == "p6") && result.Players.All(p => p.Id != "p5"), "latest lineup replaces the actual player, not just a shirt number");
        })),
        ("Mini lineups never invent starters for incomplete, stale, ended or unavailable data", () => Sync(() =>
        {
            var box = Box(); var live = Live(box);
            Check(LineupCards.Create(null, box.Game, live, Now).Message.Contains("等待"), "statistics may be absent");
            Check(LineupCards.Create(box.Away, box.Game, live with { HasError = true }, Now).Players.Count == 0, "live connection failure removes confirmation");
            Check(LineupCards.Create(box.Away, box.Game, live, Now.AddSeconds(31)).Players.Count == 0, "stale cached list isn't a confirmed lineup");
            Check(LineupCards.Create(box.Away, box.Game with { Status = GameStatus.Finished }, live, Now).Message.Contains("结束"), "ended game has no current five");
            var incomplete = new[] { E(3, "湖人队换人（勒布朗-詹姆斯、安东尼-戴维斯）") };
            Check(LineupCards.Create(box.Away, box.Game, live with { History = incomplete }, Now).Players.Count == 0, "two names never get filled with starters");
            var mismatched = box.Away with { Players = box.Away.Players.Select(p => p.Id == "p5" ? p with { Name = "其他球员" } : p).ToArray() };
            var partial = LineupCards.Create(mismatched, box.Game, live, Now);
            Check(partial.Players.Count == 0 && partial.Message.Contains("4/5"), "partial resolution is disclosed instead of presenting five");
        })),
        ("Taller lineup capsules keep the old hit area inside expansion at every corner and scale", () => Sync(() =>
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
            foreach (var width in new[] { 280, 480 })
            {
                var area = new WorkArea(-2560, -200, 0, 1240);
                var capsuleWidth = (int)(280 * scale); var capsuleHeight = (int)(30 * scale);
                var expandedWidth = (int)(width * scale); var expandedHeight = (int)(154 * scale);
                foreach (var point in new[] { new WindowPosition(area.Left + 16, area.Top + 16), new(area.Right - capsuleWidth - 16, area.Top + 16),
                    new(area.Left + 16, area.Bottom - capsuleHeight - 16), new(area.Right - capsuleWidth - 16, area.Bottom - capsuleHeight - 16) })
                {
                    var result = WindowPlacement.ExpandContaining(area, point.X, point.Y, capsuleWidth, capsuleHeight, expandedWidth, expandedHeight, scale);
                    Check(result.X <= point.X && result.Y <= point.Y && result.X + expandedWidth >= point.X + capsuleWidth && result.Y + expandedHeight >= point.Y + capsuleHeight, "no hover oscillation from losing the original hit area");
                    Check(result.X >= area.Left && result.Y >= area.Top && result.X + expandedWidth <= area.Right && result.Y + expandedHeight <= area.Bottom, "lineup remains in the work area");
                }
            }
        })),
        ("Followed event filter matches full and unique short names across both teams", () => Sync(() =>
        {
            var box = Box(); var filter = new PlayerEventFilter(box, [new("p1", "旧名字"), new("h1", "库里")], "1");
            Check(filter.Matches(E(1, "詹姆斯突破上篮得分")) && filter.Matches(E(2, "勒布朗·詹姆斯助攻，戴维斯扣篮得分")), "source id chooses the roster identity and punctuation is normalized");
            Check(filter.Matches(E(3, "斯蒂芬-库里三分不中，戴维斯拿到篮板")), "a line can mention players on both teams");
            Check(filter.Matches(E(4, "库里被换下")) && !filter.Matches(E(5, "里夫斯三分命中")), "substitutions and misses count, unrelated scoring doesn't");
        })),
        ("Ambiguous short names and longer non-favorite names do not create false followed events", () => Sync(() =>
        {
            var box = Box() with { Away = new("湖人", [P("one", "杰伦-威廉姆斯"), P("two", "杰林-威廉姆斯"), P("three", "詹姆斯"), P("four", "詹姆斯-琼斯"), P("five", "James Smith")], null) };
            var filter = new PlayerEventFilter(box, [new("one", "杰伦-威廉姆斯"), new("three", "詹姆斯"), new("five", "James Smith")], "1");
            Check(!filter.Matches(E(1, "威廉姆斯上篮得分")) && filter.Matches(E(2, "杰伦·威廉姆斯上篮得分")), "shared surname requires a full name");
            Check(!filter.Matches(E(3, "詹姆斯-琼斯三分命中")) && filter.Matches(E(4, "詹姆斯上篮")), "a shorter favorite name cannot steal a longer player's mention");
            Check(filter.Matches(E(5, "James Smith scores")) && !filter.Matches(E(6, "James Smithsonian scores")), "Latin name boundaries are preserved");
        })),
        ("Followed filters handle changed text, empty favorites, absent players and game switches", () => Sync(() =>
        {
            var box = Box(); var favorites = new[] { new FavoritePlayer("p1", "勒布朗-詹姆斯") };
            var filter = new PlayerEventFilter(box, favorites, "1");
            var first = E(1, "詹姆斯命中三分");
            Check(filter.Matches(first) && !filter.Matches(first with { Text = "戴维斯命中三分" }), "source correction invalidates the cached event result");
            Check(new PlayerEventFilter(box, [], "1").AvailableFavorites == 0 && !new PlayerEventFilter(box, favorites, "2").Matches(first), "no favorites and wrong-game rosters never match");
            Check(!new PlayerEventFilter(null, favorites, null).Matches(first) && !new PlayerEventFilter(box, [new("absent", "詹姆斯")], "1").Matches(first), "waiting and absent ids never substitute a same-name player");
        })),
        ("Followed and important-event filters intersect without changing session history", () => Sync(() =>
        {
            var history = new[] { E(1, "詹姆斯投篮不中"), E(2, "戴维斯扣篮得分"), E(3, "詹姆斯突破上篮得分"), E(4, "詹姆斯被换下") };
            var filter = new PlayerEventFilter(Box(), [new("p1", "勒布朗-詹姆斯")], "1");
            Check(EventSelection.Recent(history, 2, false, filter).Select(e => e.SourceId).SequenceEqual(new[] { "4", "3" }), "latest matching entries first");
            Check(EventSelection.Recent(history, 2, true, filter).Single().SourceId == "3" && history.Length == 4, "intersection does not prune the original history");
        })),
        ("Statistics deltas establish a baseline then compare player identities with no invented missing values", () => Sync(() =>
        {
            var tracker = new StatisticsChangeTracker(); var first = Box();
            Check(tracker.Observe(State(first)).Changes is null, "first fetch isn't a full-game increment");
            var second = Change(first, 1, p => p.Id == "p1" ? p with { Points = 30, Rebounds = 9, Assists = null, Name = "来源更名" } : p);
            var result = tracker.Observe(State(second)); var delta = result.Changes!.Find("湖人", "p1")!;
            Check(delta.Points == 2 && delta.Rebounds == 1 && delta.Assists is null && !delta.HasReduction, "compare by identity and keep unknowns unknown");
            Check(PlayerStatDelta.Format(30, delta.Points) == "30（+2）" && PlayerStatDelta.Format(null, delta.Assists) == "—", "compact, honest display");
            var same = tracker.Observe(State(second with { FetchedAt = Now.AddSeconds(2) }));
            Check(same.Changes!.Find("湖人", "p1") is { HasChanges: false }, "a successful unchanged poll clears the previous increment");
        })),
        ("Statistics reductions are disclosed as corrections and new roster entries have no increment", () => Sync(() =>
        {
            var tracker = new StatisticsChangeTracker(); var first = Box(); tracker.Observe(State(first));
            var second = Change(first, 1, p => p.Id == "p1" ? p with { Points = 27, Rebounds = 7 } : p);
            second = second with { Away = second.Away with { Players = second.Away.Players.Append(P("new", "新球员", 12)).ToArray() } };
            var delta = tracker.Observe(State(second)).Changes!;
            Check(delta.Find("湖人", "p1") is { Points: -1, Rebounds: -1, HasReduction: true }, "negative corrections are not positive live achievements");
            Check(PlayerStatDelta.Format(27, -1).Contains("修正 -1") && delta.Find("湖人", "new") is null, "correction label and no baseline for new players");
        })),
        ("Statistics failures and loading preserve comparison; new games and reselects reset it", () => Sync(() =>
        {
            var tracker = new StatisticsChangeTracker(); var first = Box(); tracker.Observe(State(first));
            var second = Change(first, 2, p => p with { Assists = p.Assists + 1 });
            var successful = tracker.Observe(State(second));
            var error = tracker.Observe(State(second) with { HasError = true, Message = "连接中断" });
            var loading = tracker.Observe(State(second) with { IsLoading = true });
            Check(error.Changes == successful.Changes && loading.Changes == successful.Changes, "intermediate states do not consume or create a comparison");
            var recovered = tracker.Observe(State(Change(second, 4, p => p with { Assists = p.Assists + 2 })));
            Check(recovered.Changes!.Find("湖人", "p1")!.Assists == 2 && recovered.Changes.From == second.FetchedAt, "recovery compares the last successful sample, not a failed poll");
            var newGame = Box() with { Game = Game() with { Id = "2" } };
            Check(tracker.Observe(State(newGame)).Changes is null, "new match resets before comparison");
            tracker.Reset(); Check(tracker.Observe(State(newGame with { FetchedAt = Now.AddSeconds(10) })).Changes is null, "reselecting the same match resets the baseline too");
        })),
        ("Frozen player cards keep their captured delta while later statistics arrive", () => Sync(() =>
        {
            var tracker = new StatisticsChangeTracker(); var first = Box(); tracker.Observe(State(first));
            var visible = tracker.Observe(State(Change(first, 1, p => p with { Points = p.Points + 2 })));
            var favorite = new FavoritePlayer("p1", "詹姆斯"); var card = PlayerCards.Create(favorite, visible, "1");
            var latest = tracker.Observe(State(Change(first, 2, p => p with { Points = p.Points + 5 })));
            Check(PlayerCards.Create(favorite, visible, "1") == card && card.FirstLine.Contains("30（+2）"), "captured state is immutable");
            Check(PlayerCards.Create(favorite, latest, "1").FirstLine.Contains("33（+3）") && !PlayerCards.Create(favorite, latest, "2").FirstLine.Contains("+3"), "resume advances the comparison without leaking across games");
        })),
        ("Shared coordinator publishes one consistent delta for all consumers and resets on reselect", async () =>
        {
            var source = new Counter();
            using var coordinator = new StatisticsCoordinator(source);
            await coordinator.SetGame(Game());
            await coordinator.SetDemand(StatisticsConsumer.PlayerOverlay, true);
            await Until(() => coordinator.State.Snapshot is not null);
            await coordinator.SetDemand(StatisticsConsumer.Window, true);
            Check(source.Calls == 1 && coordinator.State.Changes is null, "opening another consumer does not fetch or invent increments");
            await coordinator.Refresh(); await Until(() => source.Calls == 2 && !coordinator.State.IsLoading);
            Check(coordinator.State.Changes!.Find("湖人", "p1")!.Points == 2, "controller state carries the shared comparison");
            await coordinator.SetDemand(StatisticsConsumer.Window, false);
            Check(source.Calls == 2, "lineup or filtered text keeps the only subscription");
            await coordinator.SetGame(Game()); await Until(() => source.Calls == 3 && !coordinator.State.IsLoading);
            Check(coordinator.State.Changes is null && source.MaxActive == 1, "same-match reselect starts a new baseline and never overlaps");
            await coordinator.SetDemand(StatisticsConsumer.PlayerOverlay, false);
        }),
        ("Archived source lineup and player short-name filtering work together", () => Sync(() =>
        {
            var box = new BoxScoreParser().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "boxscore-168863.html")), "168863", Now);
            var snapshot = new GamePageParser().ParseSnapshot(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "finished-168863.html")), "168863", Now);
            var game = box.Game with { Status = GameStatus.Live, Progress = "第四节 00:10" };
            var live = new SessionState(snapshot with { Game = game }, snapshot.Entries, "", false, false) { SelectedGame = game };
            Check(LineupCards.Create(box.Away, game, live, Now).Players.Count == 5 && LineupCards.Create(box.Home, game, live, Now).Players.Count == 5, "actual archived lists resolve both fives");
            var maxey = box.Away.Players[0]; var filter = new PlayerEventFilter(box, [new(maxey.Id, maxey.Name)], box.Game.Id);
            Check(snapshot.Entries.Any(filter.Matches) && filter.Matches(E(999, "马克西突破上篮得分")), "real roster identity supports unique short names");
        }))
    ];
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static GameInfo Game() => new("1", "湖人", "勇士", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
    private static PlayerStatistics P(string id, string name, int points = 28) => new(id, name, false, "F", "28:36", "10-18", "2-4", "6-6", 2, 6, 8, 7, 2, 1, 1, 1, points, 4) { JerseyNumber = "00" };
    private static BoxScoreSnapshot Box() => new(Game(), new("湖人", [P("p1", "勒布朗-詹姆斯"), P("p2", "安东尼-戴维斯"), P("p3", "奥斯汀-里夫斯"), P("p4", "八村垒"), P("p5", "贾里德-范德比尔特"), P("p6", "多尔顿-克内克特")], null),
        new("勇士", [P("h1", "斯蒂芬-库里"), P("h2", "吉米-巴特勒"), P("h3", "德雷蒙德-格林"), P("h4", "摩西-穆迪"), P("h5", "布兰丁-波杰姆斯基")], null), Now);
    private static StatisticsState State(BoxScoreSnapshot box) => new(box.Game, box, "", false, false);
    private static BoxScoreSnapshot Change(BoxScoreSnapshot box, int seconds, Func<PlayerStatistics, PlayerStatistics> change) => box with
    { Away = box.Away with { Players = box.Away.Players.Select(change).ToArray() }, FetchedAt = Now.AddSeconds(seconds) };
    private static LiveEntry E(int order, string text) => new(order.ToString(), 3, "04:32", "湖人", text, "86-82", order);
    private static SessionState Live(BoxScoreSnapshot box)
    {
        var entries = new[] { E(1, "湖人队换人（勒布朗-詹姆斯、安东尼-戴维斯、奥斯汀-里夫斯、八村垒、贾里德-范德比尔特）"),
            E(2, "勇士队换人（斯蒂芬-库里、吉米-巴特勒、德雷蒙德-格林、摩西-穆迪、布兰丁-波杰姆斯基）") };
        return new(new(box.Game, entries, Now), entries, "", false, false) { SelectedGame = box.Game };
    }
    private sealed class Counter : IStatisticsSource
    {
        internal int Calls, Active, MaxActive;
        public async Task<BoxScoreSnapshot> GetBoxScore(string gameId, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls); MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            try { await Task.Delay(10, ct); return Change(Box(), call, p => p with { Points = p.Points + call * 2 }); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
