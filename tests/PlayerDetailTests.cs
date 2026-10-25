using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using NbaLive.Core;

internal static class PlayerDetailTests
{
    internal static readonly GameInfo Game = new("168869", "尼克斯", "凯尔特人", 103, 107, GameStatus.Finished, "已结束", new(2026, 10, 24));
    internal static readonly PlayerStatistics Player = new("karlanthonytowns-150037.html", "卡尔-安东尼-唐斯", true, "C", "37", "9-13", "2-3", "2-3", 3, 7, 10, 3, 5, 1, 4, 0, 22, 2);
    internal static readonly PlayerDetailQuery Query = new(Game, "尼克斯", Player);
    const string Match = "1537445625157648384", MobilePlayer = "1901000000468026", Team = "1901000000501273";
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    static string Detail => Fixture("player-detail-towns.json");
    static PlayerGameDetailSnapshot Parse(string? json = null, PlayerDetailQuery? query = null) => PlayerGameDetailParser.Parse(json ?? Detail, query ?? Query, Match, MobilePlayer, Team, DateTimeOffset.UtcNow);
    static string Edit(Action<JsonObject> change) { var root = JsonNode.Parse(Detail)!; change(root["result"]!.AsObject()); return root.ToJsonString(); }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    static void Reject(Action action) { try { action(); } catch (SourceFormatException) { return; } throw new Exception("Expected SourceFormatException"); }
    static async Task Until(Func<bool> ready) { for (var i = 0; i < 200; i++) { if (ready()) return; await Task.Delay(5); } throw new Exception("Timed out"); }
    static Task Park(TimeSpan _, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Player detail verifies Towns full game, quarters and 13 real shot coordinates", () => Sync(() =>
        {
            var s = Parse(); var full = s.Find(0)!.Statistics;
            Check(full.Points == 22 && full.Rebounds == 10 && full.Assists == 3 && full.FieldGoals == "9-13" && full.ThreePointers == "2-3", "screenshot totals");
            Check(s.Shots.Count == 13 && s.Shots.Count(p => p.Made) == 9 && s.Periods.Where(p => p.Period > 0).Sum(p => p.Statistics.Points) == 22, "quarters and made flag");
            Check(s.Find(0)!.ShotTypes.Single(t => t.Name == "扣篮").Display == "2/2" && s.Find(0)!.ShotTypes.Single(t => t.Name == "跳投").Display == "1/3", "shot methods match screenshot");
        })),
        ("Player detail derives two pointers and undefined percentage without copying source zeros", () => Sync(() =>
        {
            Check(PlayerGamePeriod.TwoPointers("9-13", "2-3") == "7/10" && PlayerGamePeriod.TwoPointers("3-3", "1-1") == "2/2", "two pointers");
            Check(PlayerGamePeriod.TwoPointers("1-2", "2-3") == "—" && PlayerGamePeriod.TwoPointers("—", "0-0") == "—", "bad totals not subtracted");
            Check(PlayerGamePeriod.TwoPointers("0-0", "0-0") == "0/0" && StatisticsPresets.DisplayPercentage(StatisticsPresets.Percentage("0-0")) == "—", "zero attempt percentage");
        })),
        ("Player detail preserves precise period seconds and marks box score fallback provenance", () => Sync(() =>
        {
            var s = Parse(query: Query with { StatisticsFetchedAt = DateTimeOffset.UtcNow });
            Check(s.Find(1)!.Statistics.Minutes == "9:14" && s.Find(0)!.Statistics.OffensiveRebounds == 3 && s.Find(0)!.Statistics.PlusMinus == 2, "missing detailed fields use named box score snapshot");
            Check(s.Notice.Contains("统计表") && s.Shots.All(p => p.Clock == "" && p.Kind == ""), "no invented shot time or method");
        })),
        ("Player detail uses explicit legacy player IDs and separate mobile match IDs", () => Sync(() =>
        {
            var mapping = PlayerGameDetailParser.MatchPlayers(Fixture("player-detail-roster.json"), Query, Match);
            Check(mapping.TeamId == Team && PlayerGameDetailParser.ResolvePlayer(Fixture("player-detail-team.json"), Player.Id, Team, mapping.PlayerIds) == MobilePlayer, "explicit ID mapping");
            Check(PlayerGameDetailParser.ResolvePlayer("{}", "1555066814055055360", Team, ["1555066814055055360"]) == "1555066814055055360", "large direct ID stays string");
            Check(PlayerGameDetailParser.DidNotPlay(Fixture("player-detail-roster.json"), "1251524510184636416"), "explicit game DNP flag");
            Reject(() => PlayerGameDetailParser.ResolvePlayer(Fixture("player-detail-team.json"), "999", Team, mapping.PlayerIds));
            Reject(() => PlayerGameDetailParser.MatchPlayers(Fixture("player-detail-roster.json"), Query with { Game = Game with { Home = "湖人" } }, Match));
        })),
        ("Player detail rejects mixed player, game, date and team responses", () => Sync(() =>
        {
            Reject(() => Parse(Edit(r => r["playerId"] = "wrong"))); Reject(() => Parse(Edit(r => r["nbaPid"] = "999")));
            Reject(() => Parse(Edit(r => r["matchId"] = "wrong"))); Reject(() => Parse(Edit(r => r["matchTime"] = "已结束 2026-10-23")));
            Reject(() => Parse(Edit(r => r["pointsList"]![0]!["teamId"] = "wrong")));
            Reject(() => Parse(Edit(r => r["quarterStatsList"] = new JsonArray()))); Reject(() => Parse("{}"));
            Reject(() => PlayerGameDetailParser.ResolvePlayer("bad json", Player.Id, Team, [MobilePlayer]));
        })),
        ("Player detail supports overtime, missing values and unplayed source status", () => Sync(() =>
        {
            var overtime = Parse(Edit(r => { r["quarterStatsList"]![0]!["quarter"] = "6"; r["pointsList"]![0]!["quarter"] = "6"; }));
            Check(overtime.Find(6)?.Label == "加时2" && overtime.Shots.Any(p => p.Period == 6), "dynamic overtime");
            var missing = Parse(Edit(r => { r["quarterStatsList"]![0]!["pts"] = null; r["quarterStatsList"]![0]!["fta"] = null; }));
            Check(missing.Find(1)!.Statistics.Points is null && missing.Find(1)!.Statistics.FreeThrows == "—", "missing is not zero");
            Check(Parse(query: Query with { Player = Player with { Status = "未出场" } }).Find(0)!.Statistics.Status == "未出场", "DNP retained");
        })),
        ("Player detail replaces complete shot snapshots while preserving coincident attempts", () => Sync(() =>
        {
            var same = Parse(Edit(r => { r["pointsList"]![1]!["x"] = r["pointsList"]![0]!["x"]!.DeepClone(); r["pointsList"]![1]!["y"] = r["pointsList"]![0]!["y"]!.DeepClone(); }));
            Check(same.Shots.Count == 13 && same.Shots[0].Index != same.Shots[1].Index, "same location is not an identity");
            Check(Parse().Shots.Count == Parse().Shots.Count, "repeat full response does not append");
            var bad = Parse(Edit(r => r["pointsList"]![0]!["x"] = -5));
            Check(bad.Shots.Count == 12 && bad.Notice.Contains("无效坐标") && bad.Notice.Contains("汇总暂不一致"), "invalid location omitted with notice");
        })),
        ("Player detail coordinator hides, resumes and closes without losing valid content", async () =>
        {
            var calls = 0; var live = Query with { Game = Game with { Status = GameStatus.Live } };
            using var session = new PlayerDetailCoordinator(new Fake((q, _) => { calls++; return Task.FromResult(Parse() with { Query = q }); }), Park);
            session.Select(live); await Until(() => session.State.Snapshot is not null); session.SetVisible(false);
            Check(calls == 1 && session.State.Snapshot is not null, "hidden keeps last snapshot");
            session.SetVisible(true); await Until(() => calls == 2 && !session.State.IsLoading); session.Close();
            Check(session.State.Query is null && session.State.Snapshot is null, "close cancels demand");
        }),
        ("Player detail drops noncooperative late responses after switching players", async () =>
        {
            var first = new TaskCompletionSource<PlayerGameDetailSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var cancelled = false;
            using var session = new PlayerDetailCoordinator(new Fake((q, ct) => { if (++calls == 1) { ct.Register(() => cancelled = true); return first.Task; } return Task.FromResult(Parse() with { Query = q }); }), Park);
            session.Select(Query); var other = Query with { Player = Player with { Id = "other", Name = "其他球员" } }; session.Select(other);
            await Until(() => session.State.Snapshot?.Query.Player.Id == "other"); first.SetResult(Parse()); await Task.Delay(30);
            Check(cancelled && session.State.Snapshot?.Query.Player.Id == "other", "late previous player cannot overwrite selected one");
        }),
        ("Player detail failure keeps content, backs off and stops when a game finishes", async () =>
        {
            var calls = 0; var waits = new List<int>(); var live = Query with { Game = Game with { Status = GameStatus.Live } };
            using var session = new PlayerDetailCoordinator(new Fake((q, _) => ++calls < 5 ? Task.FromException<PlayerGameDetailSnapshot>(new HttpRequestException()) : Task.FromResult(Parse() with { Query = q })),
                (wait, ct) => { waits.Add((int)wait.TotalSeconds); return waits.Count <= 4 ? Task.CompletedTask : Park(wait, ct); });
            session.Select(live); await Until(() => session.State.Snapshot is not null);
            Check(waits.Take(4).SequenceEqual(new[] { 5, 10, 20, 30 }), "failure schedule");
            session.UpdateGame(Game); await Until(() => calls == 6); await Task.Delay(25); Check(calls == 6 && !session.State.IsLoading, "finished stops after final refresh");
        }),
        ("Player detail replaces corrected snapshots without accumulating duplicate attempts", async () =>
        {
            var calls = 0; var original = Parse(); var changed = original.Shots.Select(s => s.Index == 0 ? s with { Made = false, X = 100 } : s).ToArray();
            var live = Query with { Game = Game with { Status = GameStatus.Live } };
            using var session = new PlayerDetailCoordinator(new Fake((q, _) => Task.FromResult(original with { Query = q, Shots = ++calls == 1 ? original.Shots : changed })), Park);
            session.Select(live); await Until(() => session.State.Snapshot is not null); session.Refresh(); await Until(() => calls == 2 && !session.State.IsLoading);
            Check(session.State.Snapshot!.Shots.Count == 13 && !session.State.Snapshot.Shots[0].Made && session.State.Snapshot.Shots[0].X == 100, "corrected attempts replace existing complete set");
        }),
        ("Player detail retains the last success through an outage and clears error on recovery", async () =>
        {
            var calls = 0; var live = Query with { Game = Game with { Status = GameStatus.Live } };
            using var session = new PlayerDetailCoordinator(new Fake((q, _) => ++calls == 2 ? Task.FromException<PlayerGameDetailSnapshot>(new HttpRequestException()) : Task.FromResult(Parse() with { Query = q })), Park);
            session.Select(live); await Until(() => session.State.Snapshot is not null); var previous = session.State.Snapshot;
            session.Refresh(); await Until(() => session.State.HasError); Check(ReferenceEquals(previous, session.State.Snapshot), "network failure retains same good snapshot");
            session.Refresh(); await Until(() => calls == 3 && !session.State.HasError && !session.State.IsLoading); Check(session.State.Snapshot is not null, "recovered source clears stale status");
        }),
        ("Player detail HTTP adapter maps once and reuses the shared anonymous channel", async () =>
        {
            var paths = new List<string>();
            using var transport = new WebGameSource(new Handler((request, _) =>
            {
                Check(request.Headers.Authorization is null && request.Headers.Accept.Any(h => h.MediaType == "application/json"), "anonymous JSON request");
                var path = request.RequestUri!.AbsolutePath.Split('/').Last(); paths.Add(path);
                var body = path switch
                {
                    "scheduleList" => JsonSerializer.Serialize(new { success = true, result = new { gameList = new[] { new { day = "20261024", matchList = new[] { new { matchId = Match, awayTeamName = "尼克斯", homeTeamName = "凯尔特人" } } } } } }),
                    "playerMatchStats" => StaleRoster(), "teamPlayerList" => Fixture("player-detail-team.json"), _ => Detail
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            var source = new PlayerGameDetailSource(transport); var latest = await source.GetDetail(Query, default); await source.GetDetail(Query, default);
            Check(latest.Find(0)!.Statistics.Status == "", "cached pregame DNP cannot override actual played statistics");
            Check(paths.SequenceEqual(new[] { "scheduleList", "playerMatchStats", "teamPlayerList", "playerMatchShootPointsStats", "playerMatchShootPointsStats" }), "cached ID mapping and full snapshot polling");
        }),
        ("Player portrait requests reject untrusted hosts and oversized responses", async () =>
        {
            using var transport = new WebGameSource(new Handler((_, _) => { var content = new ByteArrayContent([1]); content.Headers.ContentLength = 3000000; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); }));
            var images = (IPlayerPortraitSource)new PlayerGameDetailSource(transport); var rejected = 0;
            foreach (var uri in new[] { new Uri("https://example.com/avatar.png"), new Uri("https://i11.hoopchina.com.cn/avatar.png") })
                try { await images.GetPortrait(uri, default); } catch (IOException) { rejected++; }
            Check(rejected == 2, "portrait limits");
        })
    ];
    static string StaleRoster()
    {
        var json = JsonNode.Parse(Fixture("player-detail-roster.json"))!; json["result"]!["awayPlayers"]![0]!["dnp"] = 1; return json.ToJsonString();
    }
    sealed class Fake(Func<PlayerDetailQuery, CancellationToken, Task<PlayerGameDetailSnapshot>> read) : IPlayerGameDetailSource
    { public Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct) => read(query, ct); }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => read(request, ct); }
}
