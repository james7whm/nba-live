using NbaLive.Core;
using System.Text.Json.Nodes;

internal static class HistoricalTeamTests
{
    static readonly DateTimeOffset Now=new(2026,10,26,12,0,0,TimeSpan.FromHours(8));
    static readonly TeamScope Regular=new("2023-2024"),Post=new("2023-2024",TeamPhase.Playoffs);
    static string Fixture(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","history-"+name+".json"));
    static TeamStandingsSnapshot Rank()=>HistoricalTeamParser.Standings(Fixture("standings-2024"),Regular,Now);
    static TeamIdentity Team(string key)=>Rank().Rows.Single(r=>r.Team.Key==key).Team;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static Task Sync(Action action){action();return Task.CompletedTask;}
    static void Reject(Action action){try{action();}catch(SourceFormatException){return;}throw new Exception("Expected scoped data rejection");}
    internal static readonly (string Name,Func<Task> Run)[] Cases=
    [
        ("Historical scope handles century boundaries and settings migration",()=>Sync(()=>
        {
            Check(TeamScope.FromShort("99-00")=="1999-2000"&&TeamScope.FromShort("00-01")=="2000-2001","centuries");
            Check(TeamScope.FromShort("23-25") is null&&!TeamScope.ValidSeason("2023-2025"),"invalid spans");
            Check(new AppSettings().Validate().LastTeamSeason==""&&new AppSettings().LastTeamPhase==TeamPhase.Regular,"old config defaults");
            Check(new AppSettings{LastTeamSeason="bad",LastTeamPhase=(TeamPhase)9}.Validate().LastTeamPhase==TeamPhase.Regular,"invalid phase");
            Check(Regular.Key!=Post.Key,"phase is part of identity");
        })),
        ("Season catalog comes from explicit historical rows",()=>Sync(()=>
        {
            var seasons=HistoricalTeamParser.Seasons(Fixture("lakers"),"2026-2027");
            Check(seasons.First().Id=="2026-2027"&&seasons.First().Current&&seasons.Any(s=>s.Id=="1996-1997")&&seasons.Any(s=>s.Id==Regular.Season),"verified available labels");
            Check(seasons.Select(s=>s.Id).Distinct().Count()==seasons.Count,"no duplicate seasons");
        })),
        ("Historical team statistics separate regular and postseason totals",()=>Sync(()=>
        {
            foreach(var key in new[]{"lakers","celtics"})
            {
                var team=Team(key);var id=key=="lakers"?"1901000000501323":"1901000000501263";
                var regular=HistoricalTeamParser.History(Fixture(key),team,id,Regular);var post=HistoricalTeamParser.History(Fixture(key),team,id,Post);
                Check(regular.Summary?.Games==82&&regular.Summary.Wins==(key=="lakers"?47:64),"regular record");
                Check(post.Summary?.Games==(key=="lakers"?5:19)&&post.Summary.Wins==(key=="lakers"?1:16),"postseason record");
                Check(post.Averages!.Metrics.Single(m=>m.Key=="points").Value==(key=="lakers"?106.4:108.8),"actual dated phase values");
                Check(post.Averages.Metrics.Single(m=>m.Key=="opponent").Value is null,"missing field isn't defensive rating");
            }
        })),
        ("Missing historical rows and fields never become current season or zero",()=>Sync(()=>
        {
            var absent=HistoricalTeamParser.History(Fixture("lakers"),Team("lakers"),"1901000000501323",new("1990-1991"));
            Check(absent.Summary is null&&absent.Averages is null,"missing season stays missing");
            var json=Fixture("lakers").Replace("\"抢断\"","\"未提供\"");
            Check(HistoricalTeamParser.History(json,Team("lakers"),"1901000000501323",Regular).Averages!.Metrics.Single(m=>m.Key=="steals").Value is null,"missing metric");
            var node=JsonNode.Parse(Fixture("lakers"))!;var stage=node["result"]!["teamHistoryStatsMap"]!["avg"]!["leagueStatsMap"]!["NBA"]!["teamCompetitionStatsMap"]!["NBA"]!["teamCompetitionStageStatsMap"]!["REGULAR"]!;
            var row=stage["statsRowList"]!.AsArray().Single(r=>r!["quotaList"]![0]!.GetValue<string>()=="23-24")!["quotaList"]!;
            row[1]="0";row[2]="0胜0负";var zero=HistoricalTeamParser.History(node.ToJsonString(),Team("lakers"),"1901000000501323",Regular);
            Check(zero.Summary?.Games==0&&zero.Summary.WinPercent is null&&zero.Averages!.Metrics.All(m=>m.Value is null),"zero appearances have no fabricated rates");
        })),
        ("Historical statistics validate explicit identity and header meaning",()=>Sync(()=>
        {
            Reject(()=>HistoricalTeamParser.History(Fixture("lakers"),Team("lakers"),"999",Regular));
            Reject(()=>HistoricalTeamParser.History(Fixture("lakers").Replace("\"场数\"","\"未知\""),Team("lakers"),"1901000000501323",Regular));
            var json=Fixture("lakers").Replace("47胜35负","48胜35负");
            Reject(()=>HistoricalTeamParser.History(json,Team("lakers"),"1901000000501323",Regular));
        })),
        ("Historical standings retain source tie order and distinguish postseason seed",()=>Sync(()=>
        {
            var rank=Rank();var la=rank.Rows.Single(r=>r.Team.Key=="lakers");var bos=rank.Rows.Single(r=>r.Team.Key=="celtics");
            Check(la.Rank==8&&la.PlayoffSeed==7&&la.Record=="47–35","play-in seed is distinct from regular finish");
            Check(bos.Rank==1&&bos.Record=="64–18","east values");
            var west=rank.Rows.Where(r=>r.Team.Conference=="西部").ToArray();Check(west[0].Team.Key=="thunder"&&west[1].Team.Key=="nuggets","source tie order");
            Check(rank.DivisionRanks.Count==0&&la.Team.Division=="","unverified historical division not guessed");
        })),
        ("Historical rankings reject wrong season and unsorted responses",()=>Sync(()=>
        {
            Reject(()=>HistoricalTeamParser.Standings(Fixture("standings-2024"),new("2022-2023"),Now));
            var node=JsonNode.Parse(Fixture("standings-2024"))!;var entries=node["children"]![1]!["standings"]!["entries"]!.AsArray();
            var first=entries[0]!.DeepClone();entries.RemoveAt(0);entries.Add(first);
            Reject(()=>HistoricalTeamParser.Standings(node.ToJsonString(),Regular,Now));
            node=JsonNode.Parse(Fixture("standings-2024"))!;node["children"]!.AsArray().RemoveAt(0);
            Reject(()=>HistoricalTeamParser.Standings(node.ToJsonString(),Regular,Now));
        })),
        ("Historical division ranks use dated membership and reject mismatched records",()=>Sync(()=>
        {
            var ranked=HistoricalTeamParser.Division(Fixture("division-pacific"),Rank(),Now);
            Check(ranked.DivisionRanks["lakers"]==3&&ranked.Rows.Single(r=>r.Team.Key=="lakers").Team.Division=="太平洋赛区","verified historical membership and rank");
            Check(ranked.DivisionRanks.Count==5,"only provided group populated");
            Reject(()=>HistoricalTeamParser.Division(Fixture("division-pacific"),Rank() with {Season="2022-2023"},Now));
            var changed=Rank() with {Rows=Rank().Rows.Select(r=>r.Team.Key=="lakers"?r with {Wins=48}:r).ToArray()};
            Check(!HistoricalTeamParser.Division(Fixture("division-pacific"),changed,Now).DivisionRanks.ContainsKey("lakers"),"stale group result not attached");
        })),
        ("Historical franchise identity preserves old names without current logos",()=>Sync(()=>
        {
            var rank=HistoricalTeamParser.Standings(Fixture("standings-2005"),new("2004-2005"),Now);
            var sea=rank.Rows.Single(r=>r.Team.Key=="thunder").Team;var nets=rank.Rows.Single(r=>r.Team.Key=="nets").Team;
            Check(sea.Name=="超音速"&&sea.LogoTeam!="thunder"&&sea.EnglishName=="Seattle SuperSonics","Seattle identity");
            Check(nets.Name=="新泽西篮网"&&nets.LogoTeam!="nets","New Jersey identity");
        })),
        ("Regular schedule validates requested season and excludes cup final",()=>Sync(()=>
        {
            var games=HistoricalTeamParser.Games(Fixture("lakers-games"),Regular,"lakers");
            Check(games.Count==82&&games.Count(g=>g.Won==true)==47&&games.Count(g=>g.Won==false)==35,"cup final doesn't count");
            Check(games.All(g=>g.Tipoff.Offset==TimeSpan.FromHours(8))&&Math.Abs(games.Average(g=>g.TeamPoints!.Value)-118)<.1,"Beijing time and score crosscheck");
            Reject(()=>HistoricalTeamParser.Games(Fixture("lakers-games"),new("2022-2023"),"lakers"));
        })),
        ("Postseason ledger excludes play-in and matches east and west records",()=>Sync(()=>
        {
            var all=HistoricalTeamParser.Games(Fixture("post-games"),Post);var la=all.Where(g=>g.Team.Key=="lakers").ToArray();var bos=all.Where(g=>g.Team.Key=="celtics").ToArray();
            Check(all.Count==164&&la.Length==5&&la.Count(g=>g.Won==true)==1,"82 playoff games, no play-in");
            Check(bos.Length==19&&bos.Count(g=>g.Won==true)==16,"east champion record");
            Check(all.All(g=>g.RoundKey.Length>0),"scoreboard abbreviations translated");
        })),
        ("Series derive from explicit round metadata and preserve individual game numbers",()=>Sync(()=>
        {
            var all=HistoricalTeamParser.Games(Fixture("post-games"),Post);
            var la=HistoricalTeamParser.Series(all.Where(g=>g.Team.Key=="lakers"),true);var bos=HistoricalTeamParser.Series(all.Where(g=>g.Team.Key=="celtics"),true);
            Check(la.Count==1&&la[0].Round=="首轮"&&la[0].Score=="1 : 4"&&la[0].Games.Select(g=>g.GameNumber).SequenceEqual(new int?[]{1,2,3,4,5}),"first round and game numbers");
            Check(bos.Count==4&&bos.Last().Round=="总决赛"&&bos.Last().Score=="4 : 1","actual finals");
            Check(HistoricalTeamParser.PostseasonDates(Fixture("post-dates")).Start.Month==4,"explicit source playoff calendar");
        })),
        ("Archive corrections replace identities while large IDs stay strings",()=>Sync(()=>
        {
            var root=JsonNode.Parse(Fixture("lakers-games"))!;var events=root["events"]!.AsArray();var first=events[0]!.DeepClone();
            first["id"]="9007199254740993123";events.Add(first);var corrected=first.DeepClone();corrected["competitions"]![0]!["competitors"]![0]!["score"]=new JsonObject{["value"]=150};events.Add(corrected);
            var games=HistoricalTeamParser.Games(root.ToJsonString(),Regular,"lakers");Check(games.Count==83&&games.Count(g=>g.Id=="9007199254740993123")==1,"duplicate correction not extra game");
        })),
        ("Range switch cancels old responses and clears mismatched content immediately",async()=>
        {
            var pending=new TaskCompletionSource<TeamStandingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);var source=new Source();
            using var c=new TeamInsightsCoordinator(source,()=>Now);await c.SelectTeam("lakers");await c.SetActive(true);
            source.Rank=s=>s==Regular?pending.Task:Task.FromResult(Rank() with {Season=s.Season,Phase=s.Phase});
            var old=c.SelectScope(Regular);Check(c.State.Standings is null&&c.State.Overview is null,"old scope cleared");
            await c.SelectScope(Post);pending.SetResult(Rank());await old;
            Check(c.State.Scope==Post&&c.State.Standings?.Phase==TeamPhase.Playoffs&&c.State.Overview?.Phase==TeamPhase.Playoffs,"late regular result cannot replace playoffs");
        }),
        ("Scope caches isolate phases and historical refresh pauses when hidden",async()=>
        {
            var clock=Now;var source=new Source();using var c=new TeamInsightsCoordinator(source,()=>clock);await c.SelectTeam("lakers");await c.SelectScope(Regular);await c.SetActive(true);
            var initial=source.Ranks;clock=clock.AddMinutes(10);await c.Refresh(false);Check(source.Ranks==initial,"historical cache survives live five minute interval");
            await c.SelectScope(Post);await c.SelectScope(Regular);Check(source.Ranks==initial+1&&!c.State.LoadingStandings,"regular and playoffs independently cached without spurious loading");
            await c.SetActive(false);clock=clock.AddDays(2);await c.Refresh();Check(source.Ranks==initial+1,"hidden requests paused");await c.SetActive(true);Check(source.Ranks==initial+2,"expired archive refreshes on restore");
            await c.Refresh();Check(source.Invalidations>0,"manual refresh bypasses source cache");
        }),
        ("Partial module failure preserves only same-scope successful statistics and games",async()=>
        {
            var clock=Now;var source=new Source();using var c=new TeamInsightsCoordinator(source,()=>clock);await c.SelectTeam("lakers");await c.SelectScope(Regular);await c.SetActive(true);
            var old=c.State.Overview!;
            source.Detail=(t,s)=>Task.FromResult(new TeamOverviewSnapshot(t,s.Season,[],Now.AddMinutes(1)){Phase=s.Phase,GamesAvailable=false,Notice="赛程读取未完成"});
            await c.Refresh();Check(ReferenceEquals(c.State.Overview!.Averages,old.Averages)&&ReferenceEquals(c.State.Overview.Games,old.Games),"preserve independent modules");
            Check(c.State.Overview.AveragesFetchedAt==old.AveragesFetchedAt&&c.State.OverviewMessage=="赛程读取未完成","old success time retained");
            var requests=source.Details;clock=clock.AddMinutes(7);await c.Refresh(false);Check(source.Details==requests+1,"transient archive error retries in five minutes instead of waiting a day");
            await c.SelectScope(Post);Check(c.State.Overview?.Averages is null&&c.State.Overview?.Games.Count==0,"never borrow regular modules for postseason");
        }),
        ("Cached scope return restores dated identity instead of previous franchise name",async()=>
        {
            var source=new Source();source.Rank=s=>Task.FromResult(Rank() with {Season=s.Season,Phase=s.Phase,Rows=Rank().Rows.Select(r=>r.Team.Key=="thunder"&&s==Regular?r with {Team=r.Team with {Name="超音速",LogoTeam="历史"}}:r).ToArray()});
            using var c=new TeamInsightsCoordinator(source,()=>Now);await c.SelectTeam("thunder");await c.SetActive(true);await c.SelectScope(Regular);
            Check(c.State.SelectedTeam?.Name=="超音速","historical identity loaded");await c.SelectScope(new("2026-2027"));
            Check(c.State.SelectedTeam?.Name=="雷霆"&&c.State.SelectedTeam.LogoTeam=="thunder","cached current metadata restored");c.Dispose();c.Dispose();
        }),
        ("Season switch publishes loading immediately and completes after failure or recovery",async()=>
        {
            var source=new Source();using var c=new TeamInsightsCoordinator(source,()=>Now);await c.SetActive(true);
            var pending=new TaskCompletionSource<TeamStandingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Rank=_=>pending.Task;var states=new List<TeamInsightsState>();c.Changed+=states.Add;
            var load=c.SelectScope(Post);
            Check(states.Count>0&&states[0].LoadingStandings&&states[0].Standings is null,"first new-scope notification already loading");
            Check(states.Where(s=>s.Scope==Post&&s.Standings is null).All(s=>s.LoadingStandings),"no false unavailable state during request");
            pending.SetException(new IOException("offline"));await load;
            Check(!c.State.LoadingStandings&&c.State.Standings is null&&c.State.StandingsMessage.Contains("连接中断"),"failure ends loading without stale scope");
            pending=new(TaskCreationOptions.RunContinuationsAsynchronously);states.Clear();var retry=c.Refresh(false);
            Check(c.State.LoadingStandings&&states[0].LoadingStandings,"retry publishes loading even when all data still null");
            pending.SetResult(Rank() with {Phase=TeamPhase.Playoffs});await retry;
            Check(!c.State.LoadingStandings&&c.State.Standings?.Phase==TeamPhase.Playoffs,"success replaces loading");
        }),
        ("Manual catalog refresh retains previously verified historical scope on partial outage",async()=>
        {
            var source=new Source();using var c=new TeamInsightsCoordinator(source,()=>Now);await c.SelectScope(Regular);await c.SetActive(true);
            source.Catalog=()=>new[]{new TeamSeason("2026-2027",true)};await c.Refresh();
            Check(c.State.Scope==Regular&&c.State.Seasons.Any(s=>s.Id==Regular.Season)&&c.State.Standings?.Season==Regular.Season,"temporary catalog failure doesn't replace selected range");
        })
    ];
    sealed class Source : IHistoricalTeamInsightsSource
    {
        public int Ranks,Invalidations,Details;
        public Func<IReadOnlyList<TeamSeason>> Catalog=()=>new[]{new TeamSeason("2026-2027",true),new("2023-2024",false),new("2022-2023",false)};
        public Func<TeamScope,Task<TeamStandingsSnapshot>> Rank=s=>Task.FromResult(HistoricalTeamTests.Rank() with {Season=s.Season,Phase=s.Phase});
        public Func<TeamIdentity,TeamScope,Task<TeamOverviewSnapshot>> Detail=(t,s)=>Task.FromResult(new TeamOverviewSnapshot(t,s.Season,HistoricalTeamParser.Games(Fixture("post-games"),Post,t.Key),Now){Phase=s.Phase,Averages=new(t.Key,new[]{new TeamMetric("points","得分",100)}),AveragesFetchedAt=Now,GamesFetchedAt=Now});
        public void Invalidate(TeamScope scope)=>Invalidations++;
        public Task<IReadOnlyList<TeamSeason>> GetSeasons(CancellationToken ct)=>Task.FromResult(Catalog());
        public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct)=>GetStandings(new("2026-2027"),ct);
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity t,string season,CancellationToken ct)=>GetTeamOverview(t,new TeamScope(season),ct);
        public Task<TeamStandingsSnapshot> GetStandings(TeamScope s,CancellationToken ct){Ranks++;return Rank(s);}
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity t,TeamScope s,CancellationToken ct){Details++;return Detail(t,s);}
    }
}
