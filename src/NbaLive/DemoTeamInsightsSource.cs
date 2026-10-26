namespace NbaLive;

internal sealed class DemoTeamInsightsSource : IHistoricalTeamInsightsSource,IPlayInDetailSource
{
    private static string Season=>$"{(MatchTime.Today.Month>=7?MatchTime.Today.Year:MatchTime.Today.Year-1)}-{(MatchTime.Today.Month>=7?MatchTime.Today.Year+1:MatchTime.Today.Year)}";
    private static readonly string[] West=["thunder","nuggets","lakers","rockets","timberwolves","warriors","spurs","suns","clippers","grizzlies","kings","mavericks","blazers","jazz","pelicans"];
    public void Invalidate(TeamScope scope) { }
    public Task<IReadOnlyList<TeamSeason>> GetSeasons(CancellationToken ct)=>Task.FromResult<IReadOnlyList<TeamSeason>>(new[]{new TeamSeason(Season,true),new("2023-2024",false),new("2022-2023",false),new("2019-2020",false),new("2004-2005",false)});
    public async Task<TeamStandingsSnapshot> GetStandings(TeamScope scope,CancellationToken ct)
    {
        var rank=await GetStandings(ct);
        if(scope.Phase==TeamPhase.PlayIn)
        {
            var regular=PlayInRegular(rank,scope);var piLedger=PlayInGames(scope);var start=new DateTimeOffset(scope.EndYear,scope.EndYear==2020?8:4,15,7,0,0,TimeSpan.Zero);var end=start.AddDays(scope.EndYear==2020?2:5);
            var next=scope.EndYear==2020?new[]{new TeamGame("demo-next-2020",scope.Season,TeamCatalog.Resolve("blazers")!,TeamCatalog.Resolve("lakers")!,false,end.AddDays(2),GameStatus.Finished,100,111){RoundKey="round-of-16"}}:PostGames(scope);
            var graph=PlayInBuilder.Build(scope,regular,piLedger,start,end,DateTimeOffset.UtcNow,next);
            return regular with {Phase=scope.Phase,Rows=graph.Participants,Teams=regular.Rows.Select(r=>r.Team).ToArray(),PlayIn=graph,Averages=new Dictionary<string,TeamAverages>()};
        }
        if(scope.Phase==TeamPhase.Regular)return rank with {Season=scope.Season,Rows=rank.Rows.Select(r=>scope.Season=="2004-2005"&&r.Team.Key=="thunder"?r with {Team=r.Team with {Name="超音速",FullName="西雅图超音速",EnglishName="Seattle SuperSonics",LogoTeam="超音速"}}:r).ToArray()};
        var ledger=PostGames(scope);var seeds=new[]{"celtics","knicks","bucks","cavaliers","magic","pacers","76ers","heat","thunder","nuggets","timberwolves","clippers","mavericks","suns","lakers","pelicans"}.Select((key,i)=>(key,seed:i%8+1)).ToDictionary(v=>v.key,v=>v.seed);
        var rows=rank.Rows.Where(r=>seeds.ContainsKey(r.Team.Key)).Select(r=>
        {
            var games=ledger.Where(g=>g.Team.Key==r.Team.Key).ToArray();var last=HistoricalTeamParser.Series(games,true).Last();
            return r with {Wins=games.Count(g=>g.Won==true),Losses=games.Count(g=>g.Won==false),PlayoffSeed=seeds[r.Team.Key],Progress=r.Team.Key=="celtics"?"冠军":last.Round};
        }).ToArray();
        return rank with {Season=scope.Season,Phase=scope.Phase,Rows=rows,Teams=rank.Rows.Select(r=>r.Team).ToArray(),Bracket=PlayoffBracketBuilder.Build(scope,rows,ledger,true)};
    }
    public async Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,TeamScope scope,CancellationToken ct)
    {
        if(scope.Phase==TeamPhase.PlayIn)
        {
            var piRank=await GetStandings(scope,ct);var piGames=PlayInGames(scope).Where(g=>g.Team.Key==team.Key).ToArray();
            return new(team,scope.Season,piGames,DateTimeOffset.UtcNow){Phase=scope.Phase,Summary=piRank.Rows.FirstOrDefault(r=>r.Team.Key==team.Key),Notice="演示数据 · 仅用于布局预览"};
        }
        if(scope.Phase==TeamPhase.Regular)
        {
            var overview=await GetTeamOverview(team,scope.Season,ct);
            return scope.Season==Season?overview:overview with {Games=overview.Games.Take(10).Select((g,i)=>g with {Tipoff=new DateTimeOffset(scope.EndYear,4,1+i,11,0,0,TimeSpan.FromHours(8))}).ToArray()};
        }
        var games=PostGames(scope).Where(g=>g.Team.Key==team.Key).ToArray();
        var rank=await GetStandings(scope,ct);var standing=rank.Rows.FirstOrDefault(r=>r.Team.Key==team.Key);
        var averages=games.Length>0?rank.Averages.GetValueOrDefault(team.Key):null;if(averages is not null)averages=averages with {Metrics=averages.Metrics.Select(m=>m.Key=="points"?m with {Value=games.Average(g=>g.TeamPoints!.Value)}:m.Key=="opponent"?m with {Value=games.Average(g=>g.OpponentPoints!.Value)}:m.Key=="differential"?m with {Value=games.Average(g=>g.TeamPoints!.Value-g.OpponentPoints!.Value)}:m).ToArray()};
        return new(team,scope.Season,games,DateTimeOffset.UtcNow){Phase=scope.Phase,Summary=standing,Averages=averages,Series=HistoricalTeamParser.Series(games,true)};
    }
    private static IReadOnlyList<TeamGame> PostGames(TeamScope scope)
    {
        (int Stage,string A,int W,string B,int L)[] pairs=[(0,"thunder",4,"pelicans",0),(0,"clippers",2,"mavericks",4),(0,"nuggets",4,"lakers",1),(0,"timberwolves",4,"suns",0),(1,"thunder",2,"mavericks",4),(1,"nuggets",3,"timberwolves",4),(2,"timberwolves",1,"mavericks",4),(0,"celtics",4,"heat",1),(0,"cavaliers",4,"magic",3),(0,"knicks",4,"76ers",2),(0,"bucks",2,"pacers",4),(1,"celtics",4,"cavaliers",1),(1,"knicks",3,"pacers",4),(2,"celtics",4,"pacers",0),(3,"celtics",4,"mavericks",1)];
        string[] keys=["round-of-16","conference-semifinal","conference-final","final"],names=["首轮","分区半决赛","分区决赛","总决赛"];
        var result=new List<TeamGame>();var series=0;
        foreach(var p in pairs)
        {
            var a=TeamCatalog.Resolve(p.A)!;var b=TeamCatalog.Resolve(p.B)!;var order=new List<bool>();var winner=p.W>p.L;
            for(var i=0;i<Math.Min(p.W,p.L);i++){order.Add(winner);order.Add(!winner);}while(order.Count<p.W+p.L)order.Add(winner);
            for(var i=0;i<order.Count;i++)
            {
                var first=order[i]?114:103;var second=order[i]?103:114;
                if(p.A=="nuggets"&&p.B=="lakers"){int[] pts=[114,101,112,108,108],against=[103,99,105,119,106];first=pts[i];second=against[i];}
                var game=new TeamGame($"demo-series-{series}-game-{i+1}",scope.Season,a,b,i%2==0,new DateTimeOffset(scope.EndYear,4,20,10,0,0,TimeSpan.FromHours(8)).AddDays(p.Stage*18+i*2),GameStatus.Finished,first,second){RoundKey=keys[p.Stage],RoundName=names[p.Stage],GameNumber=i+1};
                result.Add(game);result.Add(game with {Team=b,Opponent=a,Home=!game.Home,TeamPoints=second,OpponentPoints=first});
            }
            series++;
        }
        return result;
    }
    static TeamStandingsSnapshot PlayInRegular(TeamStandingsSnapshot rank,TeamScope scope)
    {
        string[] west=scope.EndYear==2020?["lakers","clippers","nuggets","rockets","thunder","jazz","mavericks","blazers","grizzlies"]:["thunder","nuggets","timberwolves","clippers","mavericks","suns","pelicans","lakers","kings","warriors"];
        string[] east=["celtics","knicks","bucks","cavaliers","magic","pacers","76ers","heat","bulls","hawks"];
        var rows=rank.Rows.Select(r=>{var list=r.Team.Conference=="西部"?west:east;var i=Array.IndexOf(list,r.Team.Key);return r with {Rank=i>=0?i+1:r.Rank,GamesBehind=scope.EndYear==2020&&r.Team.Conference=="东部"?(i==7?10:i==8?20:r.GamesBehind):r.GamesBehind};}).ToArray();return rank with {Season=scope.Season,Rows=rows};
    }
    static IReadOnlyList<TeamGame> PlayInGames(TeamScope scope)
    {
        if(scope.EndYear<2020)return [];
        var result=new List<TeamGame>();
        (string A,string B,int AScore,int BScore,string Role,int Day,int Hour)[] games=scope.EndYear==2020?[("blazers","grizzlies",126,122,"legacy",15,18)]:[("pelicans","lakers",106,110,"seven-eight",16,23),("kings","warriors",118,94,"nine-ten",17,2),("76ers","heat",105,104,"seven-eight",17,23),("bulls","hawks",131,116,"nine-ten",18,1),("heat","bulls",112,91,"eighth",19,23),("pelicans","kings",105,98,"eighth",20,1)];
        for(var i=0;i<games.Length;i++)
        {
            var g=games[i];var game=new TeamGame("demo-playin-"+i,scope.Season,TeamCatalog.Resolve(g.A)!,TeamCatalog.Resolve(g.B)!,true,new DateTimeOffset(scope.EndYear,scope.EndYear==2020?8:4,g.Day,g.Hour,30,0,TimeSpan.Zero),GameStatus.Finished,g.AScore,g.BScore){RoundKey=g.Role,RoundName=g.Role=="legacy"?"第8种子争夺（2020）":g.Role=="seven-eight"?"7–8位赛":g.Role=="nine-ten"?"9–10位赛":"第8种子争夺战",GameNumber=scope.EndYear==2020?1:null};
            result.Add(game);result.Add(game with {Team=game.Opponent,Opponent=game.Team,Home=false,TeamPoints=game.OpponentPoints,OpponentPoints=game.TeamPoints});
        }
        return result;
    }
    public Task<PlayInBoxScore> GetPlayInDetail(TeamScope scope,TeamGame game,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PlayInTeamBox Box(TeamIdentity team,int? points,int delta)
        {
            var value=points??0;var periods=new[]{value/4,value/4,value/4,value-3*(value/4)};var metrics=new TeamMetric[]{new("points","得分",points),new("rebounds","篮板",45-delta),new("assists","助攻",29-delta),new("fieldGoal","投篮命中率",51.8-delta,true)};
            var player=new PlayerStatistics("archive:demo-"+team.Key,"演示球员 · "+team.Name,true,"F","35","9-18","2-5","4-4",2,5,7,6,2,1,2,1,24,7){JerseyNumber="14"};
            return new(team,periods,metrics,new(team.Name,[player],null));
        }
        return Task.FromResult(new PlayInBoxScore(scope,game,Box(game.Team,game.TeamPoints,0),Box(game.Opponent,game.OpponentPoints,5),DateTimeOffset.UtcNow,"演示数据 · 仅用于布局预览"));
    }
    public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;var rows=new List<TeamStanding>();
        foreach(var conference in new[]{"东部","西部"})
        {
            var teams=conference=="西部"?West.Select(k=>TeamCatalog.Resolve(k)!):TeamCatalog.All.Where(t=>t.Conference==conference);
            var rank=0;foreach(var team in teams)
            {
                rank++;var wins=17-rank;var losses=20-wins;
                rows.Add(new(team,rank,wins,losses,wins*5,rank-1,$"{Math.Min(8,wins)}–{Math.Min(2,losses)}",$"{Math.Max(0,wins-8)}–{Math.Max(0,losses-2)}",117.8,111.6,6.2,rank%3==0?"3连胜":"1连败"));
            }
        }
        var averages=rows.ToDictionary(r=>r.Team.Key,r=>new TeamAverages(r.Team.Key,new TeamMetric[]{
            new("points","得分",r.Points),new("opponent","失分",r.OpponentPoints),new("differential","净胜分",r.Differential),
            new("rebounds","篮板",45.3),new("assists","助攻",27.2),new("fieldGoal","投篮",48.1,true),new("threePoint","三分",37.2,true),new("freeThrow","罚球",79.3,true),new("turnovers","失误",13.6),
            new("offensiveRebounds","前场篮板",10),new("defensiveRebounds","后场篮板",35.3),new("steals","抢断",8.1),new("blocks","盖帽",5.2),new("fouls","犯规",19.4)}));
        var divisions=rows.GroupBy(r=>r.Team.Division).SelectMany(g=>g.Select((r,i)=>(r.Team.Key,Rank:i+1))).ToDictionary(v=>v.Key,v=>v.Rank);
        return Task.FromResult(new TeamStandingsSnapshot(Season,rows,now){DivisionRanks=divisions,DivisionFetchedAt=now,Averages=averages,AveragesFetchedAt=now});
    }
    public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;var opponents=TeamCatalog.All.Where(t=>t.Key!=team.Key).ToArray();
        var games=Enumerable.Range(0,15).Select(i=>new TeamGame("demo-"+i,season,team,opponents[i],i%2==0,now.AddDays(i<10?i-10:i-9),
            i<10?GameStatus.Finished:GameStatus.Scheduled,i<10?(i%3==0?104:121):null,i<10?(i%3==0?111:114):null)).ToArray();
        return Task.FromResult(new TeamOverviewSnapshot(team,season,games,now));
    }
}
