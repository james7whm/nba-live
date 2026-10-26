using NbaLive.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PlayInTests
{
    static readonly DateTimeOffset Now=new(2026,10,26,0,0,0,TimeSpan.Zero);
    static readonly TeamScope Scope=new("2023-2024",TeamPhase.PlayIn);
    static string F(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",name+".json"));
    static TeamStandingsSnapshot Rank()=>HistoricalTeamParser.Standings(F("history-standings-2024"),new(Scope.Season),Now);
    static TeamGame[] Ledger()=>HistoricalTeamParser.Games(F("history-post-games"),Scope).ToArray();
    static PlayInSnapshot Graph(IEnumerable<TeamGame>? games=null)
    {
        var dates=HistoricalTeamParser.PostseasonDates(F("play-in-type-2024"),5);return PlayInBuilder.Build(Scope,Rank(),games??Ledger(),dates.Start,dates.End,Now,HistoricalTeamParser.Games(F("history-post-games"),Scope with {Phase=TeamPhase.Playoffs}));
    }
    static TeamGame Game()=>Graph().Games.Single(g=>g.Id=="401654661");
    static PlayInBoxScore Box(string? json=null)=>PlayInDetailParser.Parse(json??F("play-in-summary-2024"),Scope,Game(),Now);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static Task Sync(Action action){action();return Task.CompletedTask;}
    static void Reject(Action action){try{action();}catch(SourceFormatException){return;}throw new Exception("Expected explicit data rejection");}
    internal static readonly (string Name,Func<Task> Run)[] Cases=
    [
        ("Play-in configuration preserves old phase values and independent view preferences",()=>Sync(()=>
        {
            var old=JsonSerializer.Deserialize<AppSettings>("{\"LastTeamPhase\":1,\"TeamBracketMode\":false}")!.Validate();
            Check((int)TeamPhase.Regular==0&&(int)TeamPhase.Playoffs==1&&(int)TeamPhase.PlayIn==2&&old.LastTeamPhase==TeamPhase.Playoffs&&old.PlayInBracketMode&&!old.TeamBracketMode,"migration");
            Check(Scope.ArchiveType==5&&Scope.Label=="2023–24 · 附加赛"&&Scope.Key!=new TeamScope(Scope.Season,TeamPhase.Playoffs).Key,"separate identity");
        })),
        ("Play-in ledger filters six games without leaking into regular season or playoffs",()=>Sync(()=>
        {
            Check(Ledger().Length==12&&Ledger().All(g=>g.RoundKey is "seven-eight" or "nine-ten" or "eighth"),"three game roles");
            Check(HistoricalTeamParser.Games(F("history-post-games"),Scope with {Phase=TeamPhase.Playoffs}).Count==164,"82 playoff games unchanged");
            var summary=HistoricalTeamParser.History(F("history-lakers"),TeamCatalog.Resolve("lakers")!,"1901000000501323",Scope);Check(summary.Summary is null&&summary.Averages is null,"never use playoff season averages as play-in");
        })),
        ("Play-in merges mirrors into six games and confirms four berths and actual first-round opponents",()=>Sync(()=>
        {
            var graph=Graph();Check(graph.Games.Count==6&&graph.Participants.Count==8&&graph.Berths.Count==4&&graph.Notice=="","entire ledger");
            Check(graph.Berths.Single(b=>b.Team.Key=="lakers").Seed==7&&graph.Participants.Single(r=>r.Team.Key=="lakers").Rank==8,"original rank versus final seed");
            Check(graph.Berths.Single(b=>b.Team.Key=="pelicans").Opponent?.Key=="thunder"&&graph.Berths.Single(b=>b.Team.Key=="76ers").Opponent?.Key=="knicks","source first-round participants");
            Check(graph.Participants.Single(r=>r.Team.Key=="warriors").Progress=="已淘汰"&&graph.Participants.Single(r=>r.Team.Key=="pelicans").Record=="1–1","independent records");
        })),
        ("Play-in duplicate corrections retain identities and inconsistent mirrors are rejected",()=>Sync(()=>
        {
            var data=Ledger();var id=data[0].Id;var correction=data.Where(g=>g.Id==id).Select(g=>g with {TeamPoints=g.TeamPoints+1,OpponentPoints=g.OpponentPoints+1});
            Check(Graph(data.Concat(data).Concat(correction)).Games.Count==6,"correction does not double count");
            Reject(()=>Graph(data.Concat([data[0] with {TeamPoints=999}])));Reject(()=>Graph(data.Skip(1)));Reject(()=>Graph(data.Select(g=>g with {Season="2022-2023"})));
        })),
        ("Play-in refuses false advancement from wrong roles, dates or unplayed games",()=>Sync(()=>
        {
            var data=Ledger();Reject(()=>Graph(data.Select(g=>g.RoundKey=="seven-eight"?g with {RoundKey="nine-ten"}:g)));
            Reject(()=>Graph(data.Select(g=>g with {Tipoff=g.Tipoff.AddMonths(1)})));
            var scheduled=Graph(data.Select(g=>g with {Status=GameStatus.Scheduled,TeamPoints=null,OpponentPoints=null}));Check(scheduled.Berths.Count==0,"no predicted winners");
            var broken=Graph(data.Select(g=>g.RoundKey=="seven-eight"?g with {RoundKey="unverified"}:g));Check(broken.Berths.Count==0&&broken.Notice.Contains("待确认"),"unknown role cannot fabricate path");
        })),
        ("Play-in 2020 needs only one win by eighth and no fabricated second game",()=>Sync(()=>
        {
            var scope=new TeamScope("2019-2020",TeamPhase.PlayIn);var rank=HistoricalTeamParser.Standings(F("play-in-standings-2020"),scope,Now);var games=HistoricalTeamParser.Games(F("play-in-games-2020"),scope);var dates=HistoricalTeamParser.PostseasonDates(F("play-in-type-2020"),5);
            var graph=PlayInBuilder.Build(scope,rank,games,dates.Start,dates.End,Now);Check(graph.Games.Count==1&&graph.Legacy&&graph.Berths.Single().Team.Key=="blazers"&&graph.Berths.Single().Seed==8,"one win clinches");
            Check(graph.ConferenceNotice("东部")=="该赛季东部未举行附加赛"&&graph.Games[0].Tipoff.Day==16&&graph.Games[0].GameNumber==1,"eastern absence independently confirmed and Beijing date correct");
            var ninthWins=games.Select(g=>g with {TeamPoints=g.OpponentPoints,OpponentPoints=g.TeamPoints}).ToArray();Check(PlayInBuilder.Build(scope,rank,ninthWins,dates.Start,dates.End,Now).Berths.Count==0,"ninth needs a second victory");
            var second=ninthWins.Select(g=>g with {Id="9007199254740993123",Tipoff=g.Tipoff.AddDays(1),GameNumber=2});Check(PlayInBuilder.Build(scope,rank,ninthWins.Concat(second),dates.Start,dates.End,Now).Berths.Single().Team.Key=="grizzlies","second ninth-place win clinches");
            Reject(()=>PlayInBuilder.Build(scope,rank,games.Concat(second),dates.Start,dates.End,Now));
        })),
        ("Play-in explicit phase records handle the 2023 one-day calendar discrepancy",()=>Sync(()=>
        {
            var scope=new TeamScope("2022-2023",TeamPhase.PlayIn);var games=HistoricalTeamParser.Games(F("play-in-games-2023"),scope);var dates=HistoricalTeamParser.PostseasonDates(F("play-in-type-2023"),5);
            // Team ranks are only used here to test the calendar bound; the actual season is verified by the live source probe.
            var regular=Rank() with {Season=scope.Season};
            var first=games.Min(g=>g.Tipoff);Check(first<dates.Start,"real source discrepancy retained in fixture");
            var rankings=new Dictionary<string,int>{{"heat",7},{"hawks",8},{"raptors",9},{"bulls",10},{"lakers",7},{"timberwolves",8},{"pelicans",9},{"thunder",10}};
            regular=regular with {Rows=regular.Rows.Select(r=>rankings.TryGetValue(r.Team.Key,out var rank)?r with {Rank=rank}:r).ToArray()};
            var graph=PlayInBuilder.Build(scope,regular,games,dates.Start,dates.End,first.AddHours(1));Check(graph.Games.Count==6&&graph.Berths.Count==4&&!graph.Notice.Contains("尚未开始")&&graph.Start==first,"explicit games extend calendar within bounded tolerance");
        })),
        ("Play-in first survival win cannot be described as a later qualification",()=>Sync(()=>
        {
            var data=Ledger();var final=Graph().Games.Single(g=>g.RoundKey=="eighth"&&g.Team.Conference=="西部");
            var changed=data.Select(g=>g.Id==final.Id?g with {TeamPoints=g.OpponentPoints,OpponentPoints=g.TeamPoints}:g).ToArray();var graph=Graph(changed);var first=graph.Games.Single(g=>g.RoundKey=="nine-ten"&&g.Team.Key=="kings");
            Check(graph.Berths.Any(b=>b.Seed==8&&b.Team.Key=="kings")&&PlayInBuilder.Outcome(graph,first)=="国王进入第8种子争夺","no retroactive clinching in earlier game");
            Check(PlayInBuilder.Outcome(graph,graph.Games.Single(g=>g.Id==final.Id))=="国王晋级第8种子","only deciding game confirms qualification");
        })),
        ("Play-in absence, not-started and missing archive have distinct states",()=>Sync(()=>
        {
            var old=new TeamScope("2004-2005",TeamPhase.PlayIn);var unsupported=PlayInBuilder.Build(old,Rank() with {Season=old.Season},[],Now,Now,Now);Check(unsupported.Notice=="该赛季未设置附加赛","known unsupported season");
            var dates=HistoricalTeamParser.PostseasonDates(F("play-in-type-2024"),5);Check(PlayInBuilder.Build(Scope,Rank(),[],dates.Start,dates.End,dates.Start.AddDays(-1)).Notice.Contains("尚未开始"),"future");
            Check(Graph([]).Notice=="附加赛资料暂未提供","empty isn't not-held");
        })),
        ("Play-in single-game identity, periods and official percentages are verified",()=>Sync(()=>
        {
            var box=Box();Check(box.A.Team.Key=="pelicans"&&box.B.Team.Key=="kings"&&box.A.Periods.SequenceEqual(new[]{22,32,29,22})&&box.B.Periods.SequenceEqual(new[]{24,21,29,24}),"period totals");
            Check(box.A.Metric("fieldGoal")?.Display=="51.8%"&&box.B.Metric("fieldGoal")?.Display=="40.9%"&&box.A.Metric("rebounds")?.Value==45&&box.A.Metric("assists")?.Value==29,"derive exact percentage from makes and attempts");
            var ingram=box.A.Players.Players.Single(p=>p.Name=="Brandon Ingram");Check(ingram.Points==24&&ingram.Rebounds==6&&ingram.Assists==6&&ingram.JerseyNumber=="14"&&ingram.Id.StartsWith("archive:"),"verified archived player namespace and jersey");
            Check(box.A.Players.Total?.Points==105&&box.A.Players.Total.Turnovers==16&&box.A.Players.Players.Any(p=>p.Status.StartsWith("未出场")),"team turnovers and DNP remain separate");
        })),
        ("Play-in single-game parser rejects stale season, other phase, wrong IDs and mismatched scores",()=>Sync(()=>
        {
            var json=F("play-in-summary-2024");Reject(()=>Box(json.Replace("\"year\":2024","\"year\":2023")));Reject(()=>Box(json.Replace("\"type\":5","\"type\":3")));
            Reject(()=>PlayInDetailParser.Parse(json,Scope,Game() with {Id="other"},Now));Reject(()=>PlayInDetailParser.Parse(json,Scope,Game() with {TeamPoints=104},Now));Reject(()=>PlayInDetailParser.Parse(json,Scope,Game() with {Home=false},Now));
            var node=JsonNode.Parse(json)!;node["header"]!["competitions"]![0]!["competitors"]![0]!["linescores"]![0]!["displayValue"]="999";Reject(()=>Box(node.ToJsonString()));
        })),
        ("Play-in missing statistics remain missing and duplicate identities cannot crash UI",()=>Sync(()=>
        {
            var node=JsonNode.Parse(F("play-in-summary-2024"))!;node["boxscore"]!["teams"]=new JsonArray();node["boxscore"]!["players"]=new JsonArray();foreach(var c in node["header"]!["competitions"]![0]!["competitors"]!.AsArray())c!.AsObject().Remove("linescores");
            var box=Box(node.ToJsonString());Check(box.A.Metric("rebounds")?.Value is null&&box.A.Periods.Count==0&&box.A.Players.Players.Count==0&&box.Notice.Length>0,"missing is not zero");
            node=JsonNode.Parse(F("play-in-summary-2024"))!;var teams=node["boxscore"]!["teams"]!.AsArray();teams.Add(teams[0]!.DeepClone());Reject(()=>Box(node.ToJsonString()));
        })),
        ("Play-in overtime, zero attempts and large corrected player IDs are preserved",()=>Sync(()=>
        {
            var node=JsonNode.Parse(F("play-in-summary-2024"))!;var competitors=node["header"]!["competitions"]![0]!["competitors"]!.AsArray();
            foreach(var competitor in competitors){var lines=competitor!["linescores"]!.AsArray();lines.Add(new JsonObject{["displayValue"]="0"});}
            var player=node["boxscore"]!["players"]![0]!["statistics"]![0]!["athletes"]![0]!;player["athlete"]!["id"]="9007199254740993123";var athletes=node["boxscore"]!["players"]![0]!["statistics"]![0]!["athletes"]!.AsArray();var correction=player.DeepClone();correction["athlete"]!["jersey"]="99";athletes.Add(correction);
            var box=Box(node.ToJsonString());Check(box.A.Periods.Count==5&&box.B.Players.Players.Single(p=>p.Id=="archive:9007199254740993123").JerseyNumber=="99","overtime and correction");
            foreach(var team in node["boxscore"]!["teams"]!.AsArray())foreach(var stat in team!["statistics"]!.AsArray())if(stat!["name"]!.GetValue<string>()=="freeThrowsMade-freeThrowsAttempted")stat["displayValue"]="0-0";
            // Remove points validation only by keeping this as an in-progress snapshot; zero attempts still have no percentage.
            node["header"]!["competitions"]![0]!["status"]!["type"]!["completed"]=false;
            var live=PlayInDetailParser.Parse(node.ToJsonString(),Scope,Game() with {Status=GameStatus.Live},Now);Check(live.A.Metric("freeThrow")?.Value is null,"zero attempts isn't 0% shooting");
        })),
        ("Play-in detail cancels late selection and hidden responses and shares in-flight request",async()=>
        {
            var source=new DetailSource();using var c=new PlayInDetailCoordinator(source,()=>Now);await c.Select(Scope,Game());var load=c.SetVisible(true);var shared=c.Refresh();Check(source.Calls==1,"single flight");
            await c.Select(null,null);source.Pending.SetResult(Box());await Task.WhenAll(load,shared);Check(c.State.Snapshot is null&&c.State.Game is null,"late old selection rejected");
            source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var selection=c.Select(Scope,Game());var second=c.Refresh();await c.SetVisible(false);source.Pending.SetResult(Box());await Task.WhenAll(selection,second);Check(c.State.Snapshot is null&&!c.State.Loading,"hidden response rejected");
        }),
        ("Play-in detail keeps last success on failure and caches a finished game across restore",async()=>
        {
            var source=new DetailSource();source.Pending.SetResult(Box());using var c=new PlayInDetailCoordinator(source,()=>Now);await c.Select(Scope,Game());await c.SetVisible(true);var snapshot=c.State.Snapshot;
            await c.SetVisible(false);await c.SetVisible(true);Check(source.Calls==1&&ReferenceEquals(snapshot,c.State.Snapshot),"fresh ended game cache");
            source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var refresh=c.Refresh();source.Pending.SetException(new IOException("offline"));await refresh;Check(ReferenceEquals(snapshot,c.State.Snapshot)&&c.State.Message.Contains("连接中断"),"preserve last good");
            source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var retry=c.Refresh();source.Pending.SetResult(Box());await retry;Check(c.State.Message==""&&!c.State.Loading,"recovery");
        })
    ];
    sealed class DetailSource:IPlayInDetailSource
    {
        public int Calls;public TaskCompletionSource<PlayInBoxScore> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PlayInBoxScore> GetPlayInDetail(TeamScope scope,TeamGame game,CancellationToken ct){Calls++;return Pending.Task;}
    }
}
