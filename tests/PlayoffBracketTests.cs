using NbaLive.Core;
using System.Text.Json;

internal static class PlayoffBracketTests
{
    static readonly TeamScope Scope=new("2023-2024",TeamPhase.Playoffs);
    static readonly DateTimeOffset Now=new(2026,10,26,0,0,0,TimeSpan.Zero);
    static string Fixture(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","history-"+name+".json"));
    static TeamGame[] Games()=>HistoricalTeamParser.Games(Fixture("post-games"),Scope).ToArray();
    static TeamStanding[] Rows()=>HistoricalTeamParser.Standings(Fixture("standings-2024"),new(Scope.Season),Now).Rows.Select(r=>r.Team.Key=="celtics"?r with {Progress="冠军"}:r).ToArray();
    static PlayoffBracketSnapshot Build(IEnumerable<TeamGame>? games=null,bool archived=true)=>PlayoffBracketBuilder.Build(Scope,Rows(),games??Games(),archived);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static Task Sync(Action action){action();return Task.CompletedTask;}
    static void Reject(Action action){try{action();}catch(SourceFormatException){return;}throw new Exception("Expected invalid ledger rejection");}
    internal static readonly (string Name,Func<Task> Run)[] Cases=
    [
        ("Bracket merges real dual-perspective ledger into 15 series and 82 games",()=>Sync(()=>
        {
            var graph=Build();Check(Games().Length==164&&graph.Series.Count==15&&graph.Series.Sum(n=>n.Games.Count)==82,"unique game and series totals");
            Check(graph.Edges.Count==14&&graph.ChampionKey=="celtics"&&graph.Notice=="","verified entire bracket");
            Check(graph.Series.Count(n=>n.Stage==0)==8&&graph.Series.Count(n=>n.Stage==1)==4&&graph.Series.Count(n=>n.Stage==2)==2,"round totals");
        })),
        ("Bracket uses postseason seeds and a consistent score perspective",()=>Sync(()=>
        {
            var la=Build().Series.Single(n=>n.HasTeam("lakers"));
            Check(la.A.Key=="nuggets"&&la.SeedA==2&&la.SeedB==7&&la.Score=="4 : 1"&&la.WinnerKey=="nuggets","seed differs from regular rank");
            Check(la.Games.Select(g=>g.Score).SequenceEqual(new[]{"114 : 103","101 : 99","112 : 105","108 : 119","108 : 106"}),"all games follow same team order");
            Check(la.Games.Select(g=>g.GameNumber).SequenceEqual(new int?[]{1,2,3,4,5}),"source game numbers preserved");
        })),
        ("Bracket follows actual upset paths through both conferences",()=>Sync(()=>
        {
            var graph=Build();Check(graph.Series.Count(n=>n.HasTeam("mavericks"))==4&&graph.Edges.Count(e=>e.TeamKey=="mavericks")==3,"Dallas path");
            foreach(var edge in graph.Edges){var a=graph.Series.Single(n=>n.Id==edge.From);var b=graph.Series.Single(n=>n.Id==edge.To);Check(a.HasTeam(edge.TeamKey)&&b.HasTeam(edge.TeamKey)&&b.Stage==a.Stage+1,"real participant continuity");}
        })),
        ("Bracket duplicate IDs do not double count and paired corrections replace old scores",()=>Sync(()=>
        {
            var games=Games();var id=games[0].Id;var corrected=games.Where(g=>g.Id==id).Select(g=>g with {TeamPoints=g.TeamPoints+1,OpponentPoints=g.OpponentPoints+1});
            var graph=Build(games.Concat(games).Concat(corrected));var node=graph.Series.Single(n=>n.Games.Any(g=>g.Id==id));
            Check(graph.Series.Sum(n=>n.Games.Count)==82&&node.Games.Single(g=>g.Id==id).TeamPoints==Build().Series.Single(n=>n.Id==node.Id).Games.Single(g=>g.Id==id).TeamPoints+1,"correction retains identity and count");
        })),
        ("Bracket refuses contradictory mirrors, seasons and regular-season scopes",()=>Sync(()=>
        {
            var games=Games();Reject(()=>Build(games.Concat([games[0] with {TeamPoints=999}])));
            Reject(()=>Build(games.Concat([games[0] with {Season="2022-2023"}])));
            Reject(()=>PlayoffBracketBuilder.Build(new(Scope.Season),Rows(),games,true));
        })),
        ("Bracket missing perspectives and missing middle games stay unverified",()=>Sync(()=>
        {
            var games=Games();var la=Build().Series.Single(n=>n.HasTeam("lakers"));var id=la.Games[2].Id;
            Check(!Build(games.Where(g=>g.Id!=id)).Series.Single(n=>n.HasTeam("lakers")).ScoreReliable,"gap in source game numbers");
            var missing=Build(games.Where(g=>g.Team.Key!="lakers"));Check(!missing.Series.Single(n=>n.HasTeam("lakers")).ScoreReliable&&missing.Notice.Length>0,"missing mirror doesn't imply verified score");
        })),
        ("Bracket unknown rounds and scheduled games never create false advancement",()=>Sync(()=>
        {
            var games=Games();var la=Build().Series.Single(n=>n.HasTeam("lakers"));
            var unknown=Build(games.Select(g=>la.Games.Any(v=>v.Id==g.Id)?g with {RoundKey="unknown",RoundName="待确认"}:g));
            Check(unknown.Series.Any(n=>n.Stage==-1&&!n.ScoreReliable)&&unknown.Edges.Count==13&&unknown.Notice.Length>0,"unknown round not guessed");
            var scheduled=Build(games.Select(g=>la.Games.Any(v=>v.Id==g.Id)?g with {Status=GameStatus.Scheduled,TeamPoints=null,OpponentPoints=null}:g),false);
            Check(scheduled.Series.Single(n=>n.HasTeam("lakers")).WinnerKey is null&&!scheduled.Edges.Any(e=>e.From==la.Id),"no future advancement");
        })),
        ("Bracket ambiguous parents and overlapping rounds are not linked",()=>Sync(()=>
        {
            var graph=Build();var games=Games();var la=graph.Series.Single(n=>n.HasTeam("lakers"));var next=graph.Series.Single(n=>n.Stage==1&&n.HasTeam("nuggets"));
            var extra=games.Where(g=>next.Games.Any(v=>v.Id==g.Id)).Select(g=>g with {Id="duplicate-series-"+g.Id,Team=g.Team.Key=="timberwolves"?TeamCatalog.Resolve("suns")!:g.Team,Opponent=g.Opponent.Key=="timberwolves"?TeamCatalog.Resolve("suns")!:g.Opponent});
            Check(!Build(games.Concat(extra)).Edges.Any(e=>e.From==la.Id),"ambiguous next opponent");
            var overlap=Build(games.Select(g=>next.Games.Any(v=>v.Id==g.Id)?g with {Tipoff=la.Games[0].Tipoff.AddDays(-1)}:g));
            Check(!overlap.Edges.Any(e=>e.From==la.Id),"next round cannot precede previous games");
        })),
        ("Bracket historical best-of-five requires no hardcoded four-win rule",()=>Sync(()=>
        {
            var games=Games();var node=Build().Series.Single(n=>n.HasTeam("lakers"));var ids=node.Games.Where(g=>g.Won==true).Take(3).Select(g=>g.Id).ToArray();
            var subset=games.Where(g=>ids.Contains(g.Id)).Select(g=>g with {GameNumber=Array.IndexOf(ids,g.Id)+1});var result=Build(subset).Series.Single();
            Check(result.Completed&&result.WinnerKey=="nuggets"&&result.WinsA==3,"three wins accepted for completed historical series");
        })),
        ("Bracket championship requires confirmed source champion and completed final",()=>Sync(()=>
        {
            Check(Build(archived:false).ChampionKey is null,"ongoing final cannot invent champion");
            var rows=Rows().Select(r=>r with {Progress="—"}).ToArray();Check(PlayoffBracketBuilder.Build(Scope,rows,Games(),true).ChampionKey is null,"wins alone not champion claim");
            Check(Build([]).Series.Count==0&&Build([]).Notice.Length>0,"empty source remains explicitly unavailable");
        })),
        ("Bracket preference defaults to legacy list and survives config roundtrip",()=>Sync(()=>
        {
            Check(!JsonSerializer.Deserialize<AppSettings>("{}")!.Validate().TeamBracketMode,"upgrade default list");
            Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings{TeamBracketMode=true}))!.Validate().TeamBracketMode,"view preference persisted");
        })),
        ("Bracket demand suspends details, rejects late results and resumes cached demand",async()=>
        {
            var source=new Source();using var coordinator=new TeamInsightsCoordinator(source,()=>Now);await coordinator.SelectTeam("lakers");
            var first=coordinator.SetActive(true);Check(source.Details==1&&coordinator.State.LoadingOverview,"detail in flight");
            await coordinator.SetDetailsActive(false);source.Pending.SetResult(new(TeamCatalog.Resolve("lakers")!,Scope.Season,[],Now));await first;
            Check(coordinator.State.Overview is null&&!coordinator.State.LoadingOverview,"late cancelled detail ignored");
            await coordinator.SelectTeam("celtics");await coordinator.Refresh(false);Check(source.Details==1&&source.Ranks==1,"graph uses shared standings without team fetches");
            await coordinator.SetDetailsActive(true);await coordinator.SetDetailsActive(true);Check(source.Details==2&&coordinator.State.Overview?.Team.Key=="celtics","single resumed request");
            await coordinator.SetDetailsActive(false);await coordinator.SetActive(false);await coordinator.SetActive(true);Check(source.Details==2,"global restore preserves graph demand");
            await coordinator.SetDetailsActive(true);Check(source.Details==2,"cached detail reused on list return");
        })
    ];
    sealed class Source:ITeamInsightsSource
    {
        public int Ranks,Details;public TaskCompletionSource<TeamOverviewSnapshot> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct){Ranks++;return Task.FromResult(new TeamStandingsSnapshot(Scope.Season,Rows(),Now));}
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct){Details++;return Details==1?Pending.Task:Task.FromResult(new TeamOverviewSnapshot(team,season,[],Now));}
    }
}
