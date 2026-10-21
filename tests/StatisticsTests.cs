using NbaLive.Core;
using System.Net;

internal static class StatisticsTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Real box score keeps teams, starters, bench, totals and numeric plus-minus", () => Sync(() =>
        {
            var result = Parse(Fixture());
            Check(result.Game.Id == "168863" && result.Game.Status == GameStatus.Finished && result.Game.AwayScore == 106, "correct game header");
            Check(result.Away.Players.Count == 17 && result.Home.Players.Count == 17, "full rosters, excluding headings and totals");
            var maxey = result.Away.Players[0];
            Check(maxey.Name == "泰雷斯-马克西" && maxey.Points == 28 && maxey.Rebounds == 4 && maxey.Assists == 5 && maxey.PlusMinus == 11, "player values and signed differential");
            Check(maxey.IsStarter && !result.Away.Players[5].IsStarter && result.Home.Players[0].PlusMinus == -11, "starter/bench and negative values");
            Check(result.Away.Total?.Points == 106 && result.Home.Total?.Points == 100 && result.Away.Total?.FieldGoals == "37-89", "team totals not mixed with players");
        })),
        ("Statistics parse reordered columns, overtime, DNP and unknown values without invented zeros", () => Sync(() =>
        {
            var html = Synthetic();
            var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
            try
            {
                foreach (var row in doc.QuerySelectorAll("#J_away_content tr"))
                {
                    var children = row.Children.ToArray();
                    if (children.Length == 16) { row.InsertBefore(children[14], children[2]); }
                }
                var away = doc.QuerySelector("#J_away_content tbody") ?? doc.QuerySelector("#J_away_content")!;
                var dnp = doc.CreateElement("tr");
                dnp.InnerHtml = "<td><a href='/players/dnp-2.html'>未出场球员</a></td><td colspan='15'>DNP 未出场</td>";
                away.AppendChild(dnp);
                var result = Parse(doc.DocumentElement.OuterHtml.Replace("进行中 第三节", "进行中 第二个加时"), "1");
                Check(result.Game.Progress.Contains("加时") && result.Away.Players[0].Points == 20, "headers map correctly after reordering");
                Check(result.Away.Players[0].Steals is null && result.Away.Players[0].Minutes == "28:36", "blank value is unknown and minutes preserved");
                Check(result.Away.Players[1].Status.Contains("DNP") && result.Away.Players[1].Points is null, "DNP retains reason without fake score");
            }
            finally { doc.Dispose(); }
        })),
        ("Statistics reject wrong games, missing tables, incomplete rows and invalid counts", () => Sync(() =>
        {
            Throws(() => Parse(Fixture(), "999"));
            Throws(() => Parse(Synthetic().Replace("J_home_content", "missing"), "1"));
            Throws(() => Parse(Synthetic().Replace("<td>20</td>", "<td>bad</td>"), "1"));
            Throws(() => Parse(Synthetic().Replace("<td>+4</td>", ""), "1"));
            var beforeTipOff = Synthetic().Replace("进行中 第三节", "未开始").Replace("J_home_content", "missing");
            Check(!Parse(beforeTipOff, "1").HasPlayerData, "pregame without a table is an explicit waiting state");
        })),
        ("Statistics source caches unchanged HTML and reparses corrected values", async () =>
        {
            var html = Synthetic();
            using var source = new WebGameSource(new Responses([
                Response(html), Response(html), Response(html.Replace("<td>20</td>", "<td>21</td>"))]));
            var first = await source.GetBoxScore("1", CancellationToken.None);
            var same = await source.GetBoxScore("1", CancellationToken.None);
            var corrected = await source.GetBoxScore("1", CancellationToken.None);
            Check(ReferenceEquals(first.Away, same.Away) && same.FetchedAt >= first.FetchedAt, "reuse only parsed data, advance successful fetch time");
            Check(corrected.Away.Players[0].Points == 21 && !ReferenceEquals(corrected.Away, same.Away), "correction invalidates cache");
        }),
        ("Statistics session retains data during failure, recovers, and stops after final", async () =>
        {
            var live = Parse(Synthetic(), "1");
            var final = live with { Game = live.Game with { Status = GameStatus.Finished } };
            var source = new StatisticsQueue([live, new HttpRequestException(), new SourceFormatException("bad"), final]);
            var waits = new List<double>();
            using var session = new StatisticsSession(source, (time, _) => { waits.Add(time.TotalSeconds); return Task.CompletedTask; });
            var states = new List<StatisticsState>();
            session.Changed += states.Add;
            await session.Select(live.Game);
            Check(waits.SequenceEqual(new double[] { 15, 5, 10 }) && source.Calls == 4, "cadence, backoff, and stop");
            Check(states.Where(s => s.HasError).All(s => ReferenceEquals(s.Snapshot, live)), "last good stats survive network and parse failures");
            Check(session.State.Snapshot == final && !session.State.HasError, "final corrected values are published");
        }),
        ("Manual statistics refresh keeps previous data until success or failure", async () =>
        {
            var first = Parse(Fixture());
            var source = new StatisticsQueue([first, new HttpRequestException(), first]);
            using var session = new StatisticsSession(source, (_, _) => Task.CompletedTask);
            var states = new List<StatisticsState>();
            session.Changed += states.Add;
            await session.Select(first.Game);
            await session.Select(first.Game, keepPrevious: true);
            Check(states.Any(s => s.IsLoading && s.Snapshot == first) && states.Any(s => s.HasError && s.Snapshot == first), "manual refresh preserves visible snapshot");
            Check(source.Calls == 3 && !session.State.HasError, "manual refresh can recover an ended game's data");
        }),
        ("Closing or switching statistics cancels old fetches, ignores late results and clears previous game", async () =>
        {
            var source = new DelayedStatistics();
            using var session = new StatisticsSession(source);
            var first = Parse(Synthetic(), "1").Game;
            var states = new List<StatisticsState>();
            session.Changed += states.Add;
            await session.Select(first);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var switched = session.Select(first with { Id = "2" });
            source.Release.TrySetResult(); // Simulate a source that returns an old result after cancellation.
            await switched;
            await session.StopAsync();
            Check(!states.Any(s => s.Snapshot?.Game.Id == "1"), "late cancelled result never publishes");
            Check(states.Any(s => s.SelectedGame?.Id == "2" && s.Snapshot is null), "switch clears old snapshot");
            Check(source.MaxActive == 1 && source.Active == 0, "never overlaps requests; close cancels polling");
        }),
        ("Live text and statistics share one HTTP request gate", async () =>
        {
            var handler = new GateHandler();
            using var source = new WebGameSource(handler);
            await Task.WhenAll(source.GetSnapshot("168863", CancellationToken.None), source.GetBoxScore("168863", CancellationToken.None));
            Check(handler.MaxActive == 1 && handler.Calls == 2, "statistics do not overlap text HTTP requests");
        }),
        ("Scheduled statistics wait through unpublished 404 and resume when tables appear", async () =>
        {
            var games = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "games-2026-10-21.html")).Replace("已结束", "未开始");
            using var source = new WebGameSource(new Responses([Response(games), new(HttpStatusCode.NotFound), Response(Fixture())]));
            await source.GetGames(new(2026, 10, 21), CancellationToken.None);
            var waiting = await source.GetBoxScore("168863", CancellationToken.None);
            var ready = await source.GetBoxScore("168863", CancellationToken.None);
            Check(waiting.Game.Status == GameStatus.Scheduled && !waiting.HasPlayerData && ready.HasPlayerData, "waiting is not a fabricated zero table or a permanent failure");
        })
    ];

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (SourceFormatException) { return; } throw new Exception("Expected SourceFormatException"); }
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "boxscore-168863.html"));
    private static BoxScoreSnapshot Parse(string html, string id = "168863") => new BoxScoreParser().Parse(html, id, DateTimeOffset.UtcNow);
    private static HttpResponseMessage Response(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
    private static string Synthetic()
    {
        var columns = new[] { "首发", "", "时间", "投篮", "3分", "罚球", "前场", "后场", "篮板", "助攻", "犯规", "抢断", "失误", "封盖", "得分", "+/-" };
        var values = new[] { "<a href='/players/one-1.html'>球员甲</a>", "F", "28:36", "8-14", "2-5", "2-2", "2", "6", "8", "7", "3", "", "2", "1", "20", "+4" };
        var rows = "<tr>" + string.Join("", columns.Select(v => $"<td>{v}</td>")) + "</tr><tr>" + string.Join("", values.Select(v => $"<td>{v}</td>")) + "</tr>";
        return "<div class='team_a'><div class='message'><h2>86</h2><p><a>客队</a></p></div></div>" +
            "<div class='team_b'><div class='message'><h2>82</h2><p><a>主队</a></p></div></div>" +
            "<div class='table_list_c'><li><a href='/games/boxscore/1'><p>进行中 第三节 04:32</p></a></li></div>" +
            $"<table id='J_away_content'>{rows}</table><table id='J_home_content'>{rows}</table><script>window.GM={{match_id:'1'}};</script>";
    }
    private sealed class Responses(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _items = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_items.Dequeue());
    }
    private sealed class StatisticsQueue(IEnumerable<object> values) : IStatisticsSource
    {
        private readonly Queue<object> _items = new(values);
        internal int Calls;
        public Task<BoxScoreSnapshot> GetBoxScore(string id, CancellationToken ct)
        {
            Calls++;
            var value = _items.Dequeue();
            return value is Exception error ? Task.FromException<BoxScoreSnapshot>(error) : Task.FromResult((BoxScoreSnapshot)value);
        }
    }
    private sealed class DelayedStatistics : IStatisticsSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active;
        internal int MaxActive;
        public async Task<BoxScoreSnapshot> GetBoxScore(string id, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            Started.TrySetResult();
            try
            {
                if (id == "1") { await Release.Task; return Parse(Synthetic(), "1"); }
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class GateHandler : HttpMessageHandler
    {
        internal int Calls;
        internal int Active;
        internal int MaxActive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref Active));
            try
            {
                await Task.Delay(30, ct);
                return Response(request.RequestUri!.AbsolutePath.Contains("boxscore") ? Fixture() :
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "finished-168863.html")));
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
