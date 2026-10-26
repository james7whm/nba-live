using AngleSharp.Html.Parser;
using NbaLive.Core;
using System.Text.Json;

internal static class TeamInsightsTests
{
    static string Fixture(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","teams-"+name));
    static readonly DateTimeOffset Now=new(2026,10,25,20,0,0,TimeSpan.FromHours(8));
    static TeamStandingsSnapshot Rank()=>TeamInsightsParser.Standings(Fixture("standings.html"),Now);
    static TeamIdentity Lakers=>TeamCatalog.Resolve("lakers")!;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static Task Sync(Action action){action();return Task.CompletedTask;}
    static void Reject(Action action){try{action();}catch(SourceFormatException){return;}throw new Exception("Expected invalid data rejection");}
    internal static readonly (string Name,Func<Task> Run)[] Cases=
    [
        ("Team transport retries closed HTTPS handshakes without hiding HTTP status failures",async()=>
        {
            var requests=new List<Uri>();
            using var transport=new WebGameSource(new Handler(request=>
            {
                var uri=request.RequestUri!;requests.Add(uri);
                if(uri.Scheme=="https")throw new HttpRequestException("handshake closed",new System.Net.WebException("closed",new IOException("peer closed"),System.Net.WebExceptionStatus.SendFailure,null));
                var body=uri.AbsolutePath switch {"/standings"=>Fixture("standings.html"),"/standings/division"=>Fixture("division.html"),"/stats/teams"=>Fixture("averages.html"),_=>Fixture("schedule-lakers.json")};
                return new(System.Net.HttpStatusCode.OK){Content=new StringContent(body)};
            }));
            var result=await new TeamInsightsSource(transport).GetStandings(default);
            Check(result.Rows.Count==30&&result.Averages.Count==30&&requests.Count(u=>u.Scheme=="https")==1,"public data HTTP fallback works for Framework error chain");
            var count=0;using var failed=new WebGameSource(new Handler(_=>{count++;return new(System.Net.HttpStatusCode.InternalServerError);}));
            try{await new TeamInsightsSource(failed).GetStandings(default);throw new Exception("Expected server error");}catch(HttpRequestException){Check(count==1,"HTTP errors never trigger handshake fallback");}
        }),
        ("Team catalog maps thirty stable identities and all six divisions",()=>Sync(()=>
        {
            Check(TeamCatalog.All.Count==30&&TeamCatalog.All.Select(t=>t.Key).Distinct().Count()==30,"unique keys");
            Check(TeamCatalog.All.GroupBy(t=>t.Division).Count()==6&&TeamCatalog.All.GroupBy(t=>t.Division).All(g=>g.Count()==5),"six groups of five");
            Check(TeamCatalog.All.Count(t=>t.Conference=="东部")==15&&TeamCatalog.All.Count(t=>t.Conference=="西部")==15,"two conferences");
            Check(TeamCatalog.Resolve("洛杉矶湖人")==Lakers&&TeamCatalog.Resolve("SAS")?.Key=="spurs"&&TeamCatalog.Resolve("not-a-team") is null,"canonical aliases");
        })),
        ("Standings preserve source tie order and current season across both conferences",()=>Sync(()=>
        {
            var rank=Rank();Check(rank.Season=="2026-2027"&&rank.Rows.Count==30,"season and complete league");
            var west=rank.Rows.Where(r=>r.Team.Conference=="西部").ToArray();
            Check(west.Select(r=>r.Rank).SequenceEqual(Enumerable.Range(1,15)),"source order unchanged");
            var la=west.Single(r=>r.Team.Key=="lakers");Check(la.Record=="2–0"&&la.Points==126&&la.WinPercent==100,"actual fixture values");
        })),
        ("Division ranks reject cross-season data and stale team records",()=>Sync(()=>
        {
            var rank=Rank();var divided=TeamInsightsParser.Divisions(Fixture("division.html"),rank,Now);
            Check(divided.Count==30&&divided[Lakers.Key]==1,"actual division ranks");
            Reject(()=>TeamInsightsParser.Divisions(Fixture("division.html").Replace("2026-27","2025-26"),rank,Now));
            var changed=rank with {Rows=rank.Rows.Select(r=>r.Team.Key==Lakers.Key?r with {Wins=r.Wins+1}:r).ToArray()};
            Check(!TeamInsightsParser.Divisions(Fixture("division.html"),changed,Now).ContainsKey(Lakers.Key),"stale rank not attached to fresh record");
        })),
        ("Missing or duplicate standings never become partial official ranks",()=>Sync(()=>
        {
            Reject(()=>TeamInsightsParser.Standings(Fixture("standings.html").Replace("/teams/lakers","/teams/thunder"),Now));
            using var d=new HtmlParser().ParseDocument(Fixture("standings.html"));d.QuerySelector("a[href$='/lakers']")!.ParentElement!.ParentElement!.Remove();
            Reject(()=>TeamInsightsParser.Standings(d.DocumentElement!.OuterHtml,Now));
            Reject(()=>TeamInsightsParser.Season("2026-2028"));
        })),
        ("Zero-game teams expose no fabricated rates or averages",()=>Sync(()=>
        {
            using var d=new HtmlParser().ParseDocument(Fixture("standings.html"));var row=d.QuerySelector("a[href$='/lakers']")!.ParentElement!.ParentElement!;
            row.Children[2].TextContent="0";row.Children[3].TextContent="0";
            var team=TeamInsightsParser.Standings(d.DocumentElement!.OuterHtml,Now).Rows.Single(r=>r.Team.Key==Lakers.Key);
            Check(team.Record=="0–0"&&team.WinPercent is null&&team.Points is null&&team.Streak=="尚未开赛","no fake zero rates");
        })),
        ("Season averages require all thirty scoring rows to agree with dated standings",()=>Sync(()=>
        {
            var averages=TeamInsightsParser.Averages(Fixture("averages.html"),Rank());var la=averages[Lakers.Key];
            Check(averages.Count==30&&la.Metrics.Count==14,"complete current averages");
            Check(la.Metrics.Single(m=>m.Key=="fieldGoal").Display=="57.5%"&&la.Metrics.Single(m=>m.Key=="rebounds").Value==40,"column meaning verified");
            using var d=new HtmlParser().ParseDocument(Fixture("averages.html"));var row=d.QuerySelector("a[href$='/lakers']")!.ParentElement!.ParentElement!;row.Children[19].TextContent="999.0";
            Reject(()=>TeamInsightsParser.Averages(d.DocumentElement!.OuterHtml,Rank()));
            Reject(()=>TeamInsightsParser.Averages(Fixture("averages.html").Replace("atr=\"14\"","atr=\"99\""),Rank()));
        })),
        ("Team schedule filters explicit season and stage and retains Beijing time",()=>Sync(()=>
        {
            foreach(var key in new[]{"lakers","celtics"})
            {
                var json=Fixture("schedule-"+key+".json");var team=TeamCatalog.Resolve(key)!;var id=TeamInsightsParser.TeamIds(json)[key];
                Check(id.Length>15,"large source IDs remain strings");var schedule=TeamInsightsParser.Schedule(json,team,id,"2026-2027",Now);
                Check(schedule.Games.Count>=70&&schedule.Games.All(g=>g.Season=="2026-2027"),"old season excluded");
                Check(schedule.Games.All(g=>g.Tipoff.Offset==TimeSpan.FromHours(8)),"Beijing offset independent of host");
                Check(schedule.Recent(10).Count==2&&schedule.Upcoming(Now,5).Count==5,"only regular season, no preseason games");
                Check(schedule.Recent(10).Select(g=>g.Tipoff).SequenceEqual(schedule.Recent(10).Select(g=>g.Tipoff).OrderBy(t=>t)),"oldest to newest form");
            }
        })),
        ("Schedule corrections overwrite duplicate IDs without numeric sorting or invented scores",()=>Sync(()=>
        {
            var json=ScheduleJson(Match("9007199254740993","COMPLETED",101,99),Match("9","COMPLETED",102,104),Match("9007199254740993","COMPLETED",105,99),Match("10","NOTSTARTED",0,0));
            var result=TeamInsightsParser.Schedule(json,Lakers,"100","2026-2027",Now);
            Check(result.Games.Count==3&&result.Games.Single(g=>g.Id=="9007199254740993").TeamPoints==105,"corrected identity preserved");
            Check(result.Games.Single(g=>g.Id=="10").TeamPoints is null,"pregame zeros aren't results");
            Check(result.Recent(10).Count==2,"only completed results");
        })),
        ("Cancelled, live, missing-stage and unknown-result games do not count toward form",()=>Sync(()=>
        {
            var json=ScheduleJson(Match("1","CANCELLED",0,0),Match("2","INPROGRESS",8,6),Match("3","COMPLETED",null,null),Match("4","COMPLETED",100,99,"PRESEASON"),Match("5","COMPLETED",100,99,season:"2025-2026"));
            var result=TeamInsightsParser.Schedule(json,Lakers,"100","2026-2027",Now);
            Check(result.Games.Count==2&&result.Recent(10).Count==0&&result.Upcoming(Now,5).Count==0,"no unsupported form results");
            Reject(()=>TeamInsightsParser.Schedule(ScheduleJson(Match("1","COMPLETED",100,99)),TeamCatalog.Resolve("celtics")!,"100","2026-2027",Now));
            Reject(()=>TeamInsightsParser.Schedule("{\"success\":true,\"result\":{\"gameList\":[{}]}}",Lakers,"100","2026-2027",Now));
        })),
        ("Team navigation preference migrates independently of favorite team",()=>Sync(()=>
        {
            Check(JsonSerializer.Deserialize<AppSettings>("{}")!.Validate().LastBrowsedTeam=="","old settings work");
            var settings=new AppSettings{LastBrowsedTeam="洛杉矶湖人",FavoriteTeam="勇士"};
            Check(settings.Validate().LastBrowsedTeam=="lakers"&&settings.Validate().FavoriteTeam=="勇士","browsing doesn't change favorite");
            Check((settings with {LastBrowsedTeam="garbage"}).Validate().LastBrowsedTeam=="","invalid key normalized");
        })),
        ("Team coordinator shares refresh, caches selections and pauses hidden requests",async()=>
        {
            var clock=Now;var fake=new Source();using var coordinator=new TeamInsightsCoordinator(fake,()=>clock);
            await coordinator.SelectTeam(Lakers.Key);await coordinator.SetActive(true);await coordinator.Refresh(false);
            Check(fake.Ranks==1&&fake.Details==1,"fresh cache avoids requests");
            await coordinator.SelectTeam("celtics");await coordinator.SelectTeam(Lakers.Key);
            Check(fake.Details==2,"per-team cache reused");
            await coordinator.SetActive(false);clock=clock.AddMinutes(6);await coordinator.Refresh();await coordinator.SelectTeam("warriors");
            Check(fake.Ranks==1&&fake.Details==2,"hidden window makes no requests");
            await coordinator.SetActive(true);Check(fake.Ranks==2&&fake.Details==3,"restore refreshes expired data");
        }),
        ("Late team responses cannot overwrite a newly selected or hidden panel",async()=>
        {
            var pending=new TaskCompletionSource<TeamOverviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);var fake=new Source();
            fake.Detail=(team,season,ct)=>team.Key==Lakers.Key?pending.Task:Task.FromResult(new TeamOverviewSnapshot(team,season,[],Now));
            using var coordinator=new TeamInsightsCoordinator(fake,()=>Now);await coordinator.SelectTeam(Lakers.Key);var first=coordinator.SetActive(true);
            Check(coordinator.State.LoadingOverview,"initial detail in progress");await coordinator.SelectTeam("celtics");
            pending.SetResult(new(Lakers,"2026-2027",[],Now));await first;
            Check(coordinator.State.Overview?.Team.Key=="celtics","late response rejected");
            var slow=new TaskCompletionSource<TeamOverviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);fake.Detail=(_,_,_)=>slow.Task;
            var refresh=coordinator.Refresh();await coordinator.SetActive(false);slow.SetResult(new(TeamCatalog.Resolve("celtics")!,"2026-2027",[],Now));await refresh;
            Check(!coordinator.State.LoadingOverview&&!coordinator.Active,"hidden cancelled response ignored");
        }),
        ("Single-flight standings retain last success on failure and recover",async()=>
        {
            var pending=new TaskCompletionSource<TeamStandingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);var fake=new Source {Rank=()=>pending.Task};
            using var coordinator=new TeamInsightsCoordinator(fake,()=>Now);var active=coordinator.SetActive(true);var refresh=coordinator.Refresh();
            Check(fake.Ranks==1,"single rank request");pending.SetResult(Rank());await Task.WhenAll(active,refresh);
            var previous=coordinator.State.Standings;fake.Rank=()=>Task.FromException<TeamStandingsSnapshot>(new IOException("offline"));await coordinator.Refresh();
            Check(ReferenceEquals(previous,coordinator.State.Standings)&&coordinator.State.StandingsMessage.Contains("连接中断"),"preserve last good snapshot");
            fake.Rank=()=>Task.FromResult(Rank());await coordinator.Refresh();Check(coordinator.State.StandingsMessage=="","recover cleanly");
        }),
        ("Season rollover invalidates cached team details",async()=>
        {
            var fake=new Source();using var coordinator=new TeamInsightsCoordinator(fake,()=>Now);await coordinator.SelectTeam(Lakers.Key);await coordinator.SetActive(true);
            fake.Rank=()=>Task.FromResult(Rank() with {Season="2027-2028"});await coordinator.Refresh();
            Check(coordinator.State.Overview?.Season=="2027-2028"&&fake.Details==2,"new season cannot reuse old schedule");
        })
    ];
    static object Match(string id,string status,int? home,int? away,string stage="REGULAR",string season="2026-2027")=>new {matchId=id,matchStatus=status,homeScore=home,awayScore=away,homeTeamId="100",awayTeamId="200",homeTeamName="湖人",awayTeamName="勇士",leagueType="NBA",competitionStageType=stage,season,matchTime="2026-10-26 05:00:00"};
    static string ScheduleJson(params object[] matches)=>JsonSerializer.Serialize(new{success=true,result=new{gameList=new[]{new{day="20261026",matchList=matches}}}});
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));
    }
    sealed class Source : ITeamInsightsSource
    {
        public int Ranks,Details;
        public Func<Task<TeamStandingsSnapshot>> Rank=()=>Task.FromResult(TeamInsightsTests.Rank());
        public Func<TeamIdentity,string,CancellationToken,Task<TeamOverviewSnapshot>> Detail=(team,season,_)=>Task.FromResult(new TeamOverviewSnapshot(team,season,[],Now));
        public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct){Ranks++;return Rank();}
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct){Details++;return Detail(team,season,ct);}
    }
}
