using NbaLive.Core;
using System.Text.Json;

internal static class CollectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Opening and pregame lineups color five starters as references, then prefer confirmed substitutions", () => Sync(() =>
        {
            var game = Game() with { Progress = "第一节 12:00" }; var team = Team();
            var live = Live(game, []);
            var opening = CourtLineups.Resolve(team, game, live, Now);
            Check(opening.Values.Count(v => v.Presence == CourtPresence.Starter) == 5 && opening.Values.All(v => v.Presence != CourtPresence.OnCourt), "starting references must not assert live presence");
            var card = LineupCards.Create(team, game, live, Now);
            Check(card.IsStarterReference && card.Players.Count == 5 && card.Details.Contains("首发参考"), "both presentations share the same explicit label");
            Check(LineupCards.Create(team, game with { Status = GameStatus.Scheduled }, null, Now).IsStarterReference, "published pregame starters can be shown");
            var entries = new[] { Entry(1, "湖人队换人（球员乙、球员丙、球员丁、球员戊、球员己）") };
            var active = LineupCards.Create(team, game, Live(game, entries), Now);
            Check(!active.IsStarterReference && active.Players.Any(p => p.Id == "5") && active.Players.All(p => p.Id != "0"), "confirmed bench player replaces starting reference immediately");
        })),
        ("Finished games use starting references instead of the last five, while cancellations don't", () => Sync(() =>
        {
            var game = Game(); var entries = new[] { Entry(1, "湖人队换人（球员乙、球员丙、球员丁、球员戊、球员己）") };
            var card = LineupCards.Create(Team(), game with { Status = GameStatus.Finished }, Live(game, entries), Now);
            Check(card.IsStarterReference && card.Players.Any(p => p.Id == "0") && card.Players.All(p => p.Id != "5") && card.Details.Contains("比赛已结束"), "show actual starters, not an archived final substitution list");
            Check(CourtLineups.Resolve(Team(), game with { Status = GameStatus.Cancelled }, null, Now).Values.All(v => v.Presence == CourtPresence.OffCourt), "cancelled games don't invent a played starting lineup");
            var incomplete = Team() with { Players = Team().Players.Take(4).ToArray() };
            Check(LineupCards.Create(incomplete, game with { Status = GameStatus.Finished }, null, Now).Players.Count == 0, "never pad an incomplete starting five");
        })),
        ("Midgame missing, failed, stale and malformed lineups never revert to opening starters", () => Sync(() =>
        {
            var game = Game(); var live = Live(game, []);
            var cases = new[] { live, live with { HasError = true }, live with { Snapshot = live.Snapshot! with { FetchedAt = Now.AddMinutes(-1) } } };
            foreach (var state in cases) Check(CourtLineups.Resolve(Team(), game, state, Now).Values.All(v => v.Presence == CourtPresence.Unknown), "midgame uncertainty stays unknown");
            var first = game with { Progress = "第一节 10:00" };
            Check(CourtLineups.Resolve(Team(), first, Live(first, []) with { HasError = true }, Now).Values.All(v => v.Presence == CourtPresence.Unknown), "opening failure doesn't become a false starting confirmation");
            Check(CourtLineups.Resolve(Team(), first, Live(first, [Entry(1, "湖人队换人（球员甲、球员乙）")]), Now).Values.All(v => v.Presence == CourtPresence.Unknown), "a malformed latest list isn't ignored in favor of starters");
        })),
        ("Column presets preserve old defaults, independent sides, sorting and layout configuration", () => Sync(() =>
        {
            var old = JsonSerializer.Deserialize<AppSettings>("{\"Width\":400}")!.Validate();
            Check(old.StatisticsPreset == StatisticsPreset.Full && old.StatisticsLayouts.Length == 0 && old.Width == 400, "old settings keep the complete table and appearance");
            var away = new StatisticsTableLayout { Preset = StatisticsPreset.Compact, Columns = [new("Name", 240), new("Assists", 110), new("Points", 120)], SortColumn = "Assists", SortDescending = true };
            var home = away with { Home = true, Columns = [new("Name", 180)], SortColumn = "Points", SortDescending = false };
            var configured = old with { StatisticsPreset = StatisticsPreset.Compact, StatisticsLayouts = [away, home, away with { Preset = StatisticsPreset.Full }] };
            var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(configured))!.Validate();
            Check(roundtrip.StatisticsLayouts.Length == 3 && roundtrip.StatisticsLayouts.Single(l => l.Home).SortColumn == "Points", "profiles and teams don't overwrite each other");
            Check(roundtrip.StatisticsLayouts.First().Columns[1].Key == "Assists" && roundtrip.StatisticsLayouts.First().SortDescending, "order, width and numeric sorting roundtrip");
            Check(StatisticsPresets.Columns(StatisticsPreset.Compact).SequenceEqual(new[] { "Star", "Name", "Minutes", "Points", "Rebounds", "Assists" }), "compact table includes player identity and core statistics");
            Check(StatisticsPresets.Columns(StatisticsPreset.Shooting).Contains("FieldGoalPercentage") && StatisticsPresets.Columns(StatisticsPreset.Full).Contains("Steals"), "shooting and complete sets are distinct");
        })),
        ("Damaged column configuration is bounded and unknown sort or column keys are rejected", () => Sync(() =>
        {
            var layout = new StatisticsTableLayout { Preset = StatisticsPreset.Compact,
                Columns = [new("Name", 9999), new("Points", -20), new("Points", 120), new("bad", 80), null!], SortColumn = "bad" };
            var result = (new AppSettings { StatisticsPreset = (StatisticsPreset)99, StatisticsLayouts = [layout, layout, layout with { Preset = (StatisticsPreset)99 }, null!] }).Validate();
            Check(result.StatisticsPreset == StatisticsPreset.Full && result.StatisticsLayouts.Length == 1, "invalid profiles and duplicates are normalized");
            Check(result.StatisticsLayouts[0].Columns.Select(c => c.Width).SequenceEqual(new double[] { 480, 30 }) && result.StatisticsLayouts[0].SortColumn is null, "invalid widths and sort paths cannot reach WPF bindings");
            var legacy = new StatisticsTableLayout { Preset = StatisticsPreset.Full, Columns = [new("Name", 240), new("Role", 66), new("Points", 120)], SortColumn = "Role" };
            var migrated = StatisticsPresets.Normalize([legacy, legacy with { Home = true, SortColumn = "Points", SortDescending = true }]);
            Check(migrated.All(l => l.Columns.Select(c => c.Key).SequenceEqual(new[] { "Name", "Points" })) && migrated[0].SortColumn is null && migrated[1].SortColumn == "Points" && migrated[1].SortDescending, "retired role columns and sorting are removed without losing other saved widths, order or sorting");
        })),
        ("Shooting percentages use source attempts, missing values stay unknown and totals are weighted", () => Sync(() =>
        {
            Check(StatisticsPresets.Percentage("5-10") == 50 && Math.Abs(StatisticsPresets.Percentage("1-3")!.Value - 100d / 3) < .001, "numeric percentages keep precision for sorting");
            Check(StatisticsPresets.Percentage("0-0") is null && StatisticsPresets.Percentage("—") is null && StatisticsPresets.Percentage("5-4") is null && StatisticsPresets.Percentage("bad") is null, "missing, unattempted and impossible values aren't zero percentages");
            Check(StatisticsPresets.Percentage("0-5") == 0 && StatisticsPresets.Percentage("6-13") != (StatisticsPresets.Percentage("5-10") + StatisticsPresets.Percentage("1-3")) / 2, "zero makes is valid and total ratio isn't a player-percentage mean");
        })),
        ("Bookmarks persist only selected events, preserve same-clock identities and isolate matches", () => WithTemp(dir =>
        {
            var store = new EventBookmarks(dir); var game = Game(); var one = Entry(1, "三分命中"); var two = Entry(2, "暂停");
            store.Observe(Live(game, [one, two])); Check(store.Items.Count == 0, "observing all history never archives it");
            Check(store.Toggle(game, one) is null && store.Toggle(game, two) is null && store.Toggle(game with { Id = "2" }, one) is null, "same-clock and same-source-id events in another match are distinct");
            var loaded = new EventBookmarks(dir);
            Check(loaded.Items.Count == 3 && loaded.Contains("1", one.Key) && loaded.Contains("2", one.Key), "selected records survive process restart");
            loaded.Toggle(game, one); Check(loaded.Items.Count == 2 && loaded.Contains("1", two.Key) && loaded.Contains("2", one.Key), "toggle removes just the selected identity");
            loaded.Remove(loaded.Items.First()); Check(new EventBookmarks(dir).Items.Count == 1, "explicit removal persists");
        })),
        ("Bookmarked source corrections update saved text without changing save time or adding duplicates", () => WithTemp(dir =>
        {
            var store = new EventBookmarks(dir); var entry = Entry(1, "三分命中"); var game = Game();
            store.Toggle(game, entry); var original = store.Items.Single();
            var corrected = entry with { Text = "来源修正：两分命中", Score = "2-0" };
            store.Observe(Live(game, [corrected]));
            Check(store.Items.Count == 1 && store.Items[0].Entry.Text == corrected.Text && store.Items[0].SavedAt == original.SavedAt && store.Items[0].CorrectedAt is not null, "corrections retain the bookmark and original save time");
            store.Observe(Live(game with { Id = "2" }, [entry]));
            Check(new EventBookmarks(dir).Items.Single().Entry.Text == corrected.Text, "other matches never overwrite a saved source id");
        })),
        ("Malformed bookmark files are retained and never overwritten by an empty recovery state", () => WithTemp(dir =>
        {
            var path = Path.Combine(dir, "bookmarks.json"); File.WriteAllText(path, "broken json");
            var store = new EventBookmarks(dir);
            Check(store.Error is not null && store.Toggle(Game(), Entry(1, "得分")) is not null && File.ReadAllText(path) == "broken json", "valuable local data stays intact on read failure");
        })),
        ("Bookmarking a frozen recap uses a known newer source correction without changing the recap", () => WithTemp(dir =>
        {
            var store = new EventBookmarks(dir); var captured = Entry(1, "原文：三分命中");
            var corrected = captured with { Text = "修正：两分命中", Score = "2-0" };
            store.Observe(Live(Game(), [corrected])); store.Toggle(Game(), captured);
            Check(store.Items.Single().Entry == corrected && captured.Text == "原文：三分命中", "save the latest known version while the captured reading snapshot stays fixed");
        })),
        ("Bookmark write failure preserves the previous file and in-memory selection", () => WithTemp(dir =>
        {
            var store = new EventBookmarks(dir); store.Toggle(Game(), Entry(1, "原文"));
            var path = Path.Combine(dir, "bookmarks.json"); var before = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".tmp");
            Check(store.Toggle(Game(), Entry(2, "新事件")) is not null && store.Items.Count == 1 && File.ReadAllText(path) == before, "a failed atomic write cannot claim success or lose existing bookmarks");
        })),
        ("Bookmark plain-text copies include match context, original text and event score", () => WithTemp(dir =>
        {
            var store = new EventBookmarks(dir); store.Toggle(Game(), Entry(1, "精彩扣篮\n原文补充"));
            var text = EventBookmarks.Format(store.Items);
            Check(text.Contains("湖人 vs 勇士") && text.Contains("精彩扣篮\n原文补充") && text.Contains("事件比分：86-82"), "copy is readable text with its own match and event context");
            Check(EventBookmarks.Format([]) == "" && !text.Contains("http"), "no links or hidden source metadata in copies");
        }))
    ];
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static Task WithTemp(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NbaLive-CollectionTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { action(dir); } finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static GameInfo Game() => new("1", "湖人", "勇士", 86, 82, GameStatus.Live, "第三节 04:32", MatchTime.Today);
    private static TeamStatistics Team() => new("湖人", new[] { "球员甲", "球员乙", "球员丙", "球员丁", "球员戊", "球员己" }.Select((name, i) =>
        new PlayerStatistics(i.ToString(), name, i < 5, "F", "28:36", "5-10", "1-3", "2-2", 1, 2, 3, 4, 1, 0, 0, 1, 13, 4)).ToArray(), null);
    private static LiveEntry Entry(int id, string text) => new(id.ToString(), 1, "11:20", "湖人", text, "86-82", id);
    private static SessionState Live(GameInfo game, LiveEntry[] entries) => new(new(game, entries, Now), entries, "", false, false) { SelectedGame = game };
}
