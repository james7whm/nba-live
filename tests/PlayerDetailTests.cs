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
    const string LendeMatch = "1537808027304853504", LendeMobile = "1519326309480660992", LendeTeam = "1901000000501331";
    static readonly PlayerDetailQuery LendeQuery = new(new("169007", "勇士", "掘金", 134, 114, GameStatus.Finished, "已结束", new(2026, 10, 27)), "勇士",
        Player with { Id = "yaxellendeborg-153410.html", Name = "亚克塞尔-伦德博格", JerseyNumber = "1", Points = 31, Rebounds = 12, Assists = 6, FieldGoals = "9-19", ThreePointers = "1-8" });
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
        ("Player detail resolves verified Lendeborg alias and validates complete game detail", () => Sync(() =>
        {
            var box = new BoxScoreParser().Parse(Fixture("player-detail-lendeborg-box.html"), "169007", DateTimeOffset.UtcNow);
            var original = box.Away.Players.Single(p => PlayerGameDetailParser.LegacyNumber(p.Id) == "153410");
            Check(box.Game.Away == "勇士" && box.Game.Home == "掘金" && box.Game.AwayScore == 134 && box.Game.HomeScore == 114 && box.Game.Date == LendeQuery.Game.Date, "web game identity and date");
            Check(original.Points == 31 && original.Rebounds == 12 && original.Assists == 6 && original.FieldGoals == "9-19", "web and mobile totals agree");
            var mapping = PlayerGameDetailParser.MatchPlayers(Fixture("player-detail-lendeborg-roster.json"), LendeQuery, LendeMatch);
            var id = PlayerGameDetailParser.ResolvePlayer(Fixture("player-detail-lendeborg-team.json"), LendeQuery.Player.Id, mapping.TeamId, mapping.PlayerIds);
            var detail = PlayerGameDetailParser.Parse(Fixture("player-detail-lendeborg.json"), LendeQuery, LendeMatch, id, mapping.TeamId, DateTimeOffset.UtcNow);
            var full = detail.Find(0)!.Statistics; var quarters = detail.Periods.Where(p => p.Period > 0).ToArray();
            Check(id == LendeMobile && mapping.TeamId == LendeTeam && detail.Number == "1", "verified mobile ID and jersey");
            Check(full.Points == 31 && full.Rebounds == 12 && full.Assists == 6 && full.FieldGoals == "9-19", "actual game totals");
            Check(detail.Shots.Count == 19 && detail.Shots.Count(s => s.Made) == 9 && quarters.Sum(p => p.Statistics.Points) == 31 &&
                quarters.Sum(p => p.Statistics.Rebounds) == 12 && quarters.Sum(p => p.Statistics.Assists) == 6, "quarters and shots reconcile");
            Check(detail.Query.Player.Id == LendeQuery.Player.Id && full.Id == LendeQuery.Player.Id, "original identity retained for table and favorites");
        })),
        ("Player alias rejects ambiguous candidates, wrong mobile IDs and players outside current roster", () => Sync(() =>
        {
            var json = JsonNode.Parse(Fixture("player-detail-lendeborg-team.json"))!;
            var list = json["result"]!["list"]!.AsArray();
            var alias = list.Single(p => p!["playerId"]!.ToString() == LendeMobile)!;
            var original = alias.DeepClone(); original["playerId"] = "other-mobile"; original["player_id"] = 153410; list.Add(original);
            Reject(() => PlayerGameDetailParser.ResolvePlayer(json.ToJsonString(), LendeQuery.Player.Id, LendeTeam, [LendeMobile, "other-mobile"]));
            Check(PlayerGameDetailParser.ResolvePlayer(json.ToJsonString(), LendeQuery.Player.Id, LendeTeam, [LendeMobile]) == LendeMobile, "only this match's roster is eligible");
            Reject(() => PlayerGameDetailParser.ResolvePlayer(Fixture("player-detail-lendeborg-team.json"), LendeQuery.Player.Id, "wrong-team", [LendeMobile]));
            Reject(() => PlayerGameDetailParser.ResolvePlayer(Fixture("player-detail-lendeborg-team.json"), LendeQuery.Player.Id, LendeTeam, []));
            list.Remove(original); alias["playerId"] = "other-mobile";
            Reject(() => PlayerGameDetailParser.ResolvePlayer(json.ToJsonString(), LendeQuery.Player.Id, LendeTeam, ["other-mobile"]));
        })),
        ("Player alias never bypasses detail response player, legacy, date or team checks", () => Sync(() =>
        {
            void Invalid(Action<JsonNode> edit, string mobile = LendeMobile)
            {
                var json = JsonNode.Parse(Fixture("player-detail-lendeborg.json"))!; edit(json["result"]!);
                Reject(() => PlayerGameDetailParser.Parse(json.ToJsonString(), LendeQuery, LendeMatch, mobile, LendeTeam, DateTimeOffset.UtcNow));
            }
            Invalid(r => r["nbaPid"] = 62078); Invalid(r => r["playerId"] = "other-mobile");
            Invalid(r => r["playerId"] = "other-mobile", "other-mobile");
            Invalid(r => r["teamName"] = "掘金"); Invalid(r => r["matchTime"] = "已结束 2026-10-26");
        })),
        ("Player mapping refreshes both stale rosters once then caches successful mapping", async () =>
        {
            var paths = new List<string>(); var rosters = 0; var teams = 0;
            using var transport = new WebGameSource(new Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath.Split('/').Last(); paths.Add(path);
                var body = path switch
                {
                    "scheduleList" => Schedule(),
                    "playerMatchStats" => ++rosters == 1 ? Fixture("player-detail-lendeborg-roster.json").Replace(LendeMobile, "absent-player") : Fixture("player-detail-lendeborg-roster.json"),
                    "teamPlayerList" => ++teams == 1 ? "{\"result\":{\"info\":{\"teamId\":\"" + LendeTeam + "\"},\"list\":[]}}" : Fixture("player-detail-lendeborg-team.json"),
                    _ => Fixture("player-detail-lendeborg.json")
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            var source = new PlayerGameDetailSource(transport); var detail = await source.GetDetail(LendeQuery, default); await source.GetDetail(LendeQuery, default);
            Check(detail.Find(0)!.Statistics.Points == 31 && rosters == 2 && teams == 2, "both rosters refreshed and reused");
            Check(paths.Count(p => p == "playerMatchShootPointsStats") == 2 && paths.Count(p => p == "scheduleList") == 1, "no duplicate detail or match requests");
        }),
        ("Permanent mapping failure is bounded and manual retry does not reuse the failed rosters", async () =>
        {
            var rosters = 0; var teams = 0; var details = 0; var recovered = false;
            using var transport = new WebGameSource(new Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath.Split('/').Last();
                var body = path switch
                {
                    "scheduleList" => Schedule(),
                    "playerMatchStats" => ReadRoster(), "teamPlayerList" => ReadTeam(), _ => ReadDetail()
                };
                string ReadRoster() { rosters++; return Fixture("player-detail-lendeborg-roster.json"); }
                string ReadTeam() { teams++; return recovered ? Fixture("player-detail-lendeborg-team.json") : "{\"result\":{\"info\":{\"teamId\":\"" + LendeTeam + "\"},\"list\":[]}}"; }
                string ReadDetail() { details++; return Fixture("player-detail-lendeborg.json"); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            var source = new PlayerGameDetailSource(transport); var failed = false;
            try { await source.GetDetail(LendeQuery, default); } catch (SourceFormatException) { failed = true; }
            Check(failed && rosters == 2 && teams == 2 && details == 0, "failure retries only once and never fetches guessed detail");
            recovered = true; await source.GetDetail(LendeQuery, default);
            Check(rosters == 3 && teams == 3 && details == 1, "manual retry fetches clean rosters");
        }),
        ("Cancelling a mapping refresh cannot start detail requests or another retry", async () =>
        {
            using var cancel = new CancellationTokenSource(); var rosters = 0; var teams = 0; var details = 0;
            using var transport = new WebGameSource(new Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath.Split('/').Last(); string body;
                if (path == "scheduleList") body = Schedule();
                else if (path == "playerMatchStats") { if (++rosters == 2) cancel.Cancel(); body = Fixture("player-detail-lendeborg-roster.json"); }
                else if (path == "teamPlayerList") { teams++; body = "{}"; }
                else { details++; body = Fixture("player-detail-lendeborg.json"); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            var cancelled = false;
            try { await new PlayerGameDetailSource(transport).GetDetail(LendeQuery, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && rosters == 2 && teams == 1 && details == 0, "cancelled mapping never continues requests");
        }),
        ("Finished player format error stops polling and recovers through manual refresh", async () =>
        {
            var calls = 0; var waits = 0; var pending = new TaskCompletionSource<PlayerGameDetailSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = new PlayerDetailCoordinator(new Fake((q, _) => ++calls == 1 ? Task.FromException<PlayerGameDetailSnapshot>(new SourceFormatException("未能唯一对应本场球员编号")) : pending.Task),
                (wait, ct) => { waits++; return Park(wait, ct); });
            session.Select(Query); await Until(() => session.State.HasError);
            Check(calls == 1 && waits == 0 && !session.State.IsLoading && session.State.Snapshot is null, "finished identity error stops automatically");
            session.Refresh(); Check(session.State.IsLoading && !session.State.HasError && calls == 2, "manual retry clears old error");
            pending.SetResult(Parse()); await Until(() => session.State.Snapshot is not null);
            Check(!session.State.IsLoading && !session.State.HasError && waits == 0, "manual recovery stops after finished success");
        }),
        ("Live format errors and finished network errors retain automatic recovery", async () =>
        {
            foreach (var format in new[] { true, false })
            {
                var calls = 0; var waits = new List<int>(); var q = format ? Query with { Game = Game with { Status = GameStatus.Live } } : Query;
                using var session = new PlayerDetailCoordinator(new Fake((selected, _) => ++calls == 1
                    ? Task.FromException<PlayerGameDetailSnapshot>(format ? new SourceFormatException("unavailable") : new HttpRequestException())
                    : Task.FromResult(Parse() with { Query = selected })), (wait, ct) => { waits.Add((int)wait.TotalSeconds); return waits.Count == 1 ? Task.CompletedTask : Park(wait, ct); });
                session.Select(q); await Until(() => session.State.Snapshot is not null);
                Check(calls == 2 && waits[0] == 5 && !session.State.HasError, "transient recovery remains enabled");
            }
        }),
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
    static string Schedule() => JsonSerializer.Serialize(new { success = true, result = new { gameList = new[] { new { day = "20261027", matchList = new[] { new { matchId = LendeMatch, awayTeamName = "勇士", homeTeamName = "掘金" } } } } } });
    static string StaleRoster()
    {
        var json = JsonNode.Parse(Fixture("player-detail-roster.json"))!; json["result"]!["awayPlayers"]![0]!["dnp"] = 1; return json.ToJsonString();
    }
    sealed class Fake(Func<PlayerDetailQuery, CancellationToken, Task<PlayerGameDetailSnapshot>> read) : IPlayerGameDetailSource
    { public Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct) => read(query, ct); }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => read(request, ct); }
}
