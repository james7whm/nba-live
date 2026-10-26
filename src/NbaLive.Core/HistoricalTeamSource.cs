using System.Globalization;
using System.Text.Json;

namespace NbaLive.Core;

/// <summary>Dated archive for standings/schedules; selected-team season statistics share the existing request gate.</summary>
public sealed class HistoricalTeamSource(WebGameSource transport) : IHistoricalTeamInsightsSource,IPlayInDetailSource
{
    private const string Mobile="http://games.mobileapi.hupu.com/1/7.5.52/basketballapi/";
    private readonly TeamInsightsSource _currentSource=new(transport);
    private TeamStandingsSnapshot? _current;
    private IReadOnlyDictionary<string,string> _mobileIds=new Dictionary<string,string>();
    private readonly Dictionary<string,(string Json,DateTimeOffset Time)> _history=[];
    private readonly Dictionary<string,(IReadOnlyList<TeamGame> Games,DateTimeOffset Start,DateTimeOffset End,DateTimeOffset Fetched)> _post=[];
    private readonly Dictionary<string,TeamStandingsSnapshot> _regular=[];
    private readonly Dictionary<string,(PlayInSnapshot Snapshot,DateTimeOffset Time)> _playIns=[];
    private readonly Dictionary<string,PlayInBoxScore> _playInBoxes=[];
    private string _currentSeason="";
    private bool Current(TeamScope scope)=>scope.Season==_currentSeason;
    public void Invalidate(TeamScope scope){if(Current(scope))_current=null;_history.Clear();_post.Remove(scope.Season);_playIns.Remove(scope.Season);if(_regular.TryGetValue(scope.Season,out var previous))_regular[scope.Season]=previous with {FetchedAt=DateTimeOffset.MinValue};}
    private static bool Fresh(DateTimeOffset time,bool current)=>DateTimeOffset.UtcNow-time<(current?TimeSpan.FromMinutes(5):TimeSpan.FromDays(1));
    public async Task<IReadOnlyList<TeamSeason>> GetSeasons(CancellationToken ct)
    {
        _current=await _currentSource.GetStandings(ct);_currentSeason=_current.Season;
        var ids=TeamInsightsParser.TeamIds(await transport.ReadMobile(new(Mobile+"scheduleList?competitionTag=nba&time_zone=Asia%2FShanghai"),false,ct));
        ct.ThrowIfCancellationRequested();_mobileIds=ids;
        try {return HistoricalTeamParser.Seasons(await History("lakers",ct),_currentSeason);}
        catch(Exception ex) when(Failure(ex,ct)){return new[]{new TeamSeason(_currentSeason,true)};}
    }
    private async Task<string> History(string key,CancellationToken ct)
    {
        if(_history.TryGetValue(key,out var cached)&&Fresh(cached.Time,true))return cached.Json;
        if(!_mobileIds.TryGetValue(key,out var id))throw new SourceFormatException("该球队的历史统计编号暂未提供");
        var json=await transport.ReadMobile(new(Mobile+"team/history/season/stats?competitionType=NBA&competitionLeagueType=NBA&leagueType=NBA&teamId="+Uri.EscapeDataString(id)),false,ct);
        ct.ThrowIfCancellationRequested();
        if(_history.Count>=8)_history.Remove(_history.OrderBy(p=>p.Value.Time).First().Key);
        _history[key]=(json,DateTimeOffset.UtcNow);return json;
    }
    public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct)=>_currentSource.GetStandings(ct);
    public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct)=>GetTeamOverview(team,new TeamScope(season),ct);
    public async Task<TeamStandingsSnapshot> GetStandings(TeamScope scope,CancellationToken ct)
    {
        Validate(scope);
        if(Current(scope)&&scope.Phase==TeamPhase.Regular)
        {
            if(_current is not null&&Fresh(_current.FetchedAt,true))return _current;
            return _current=await _currentSource.GetStandings(ct);
        }
        var regular=await Regular(scope,ct);
        if(scope.Phase==TeamPhase.Regular)return regular;
        if(scope.Phase==TeamPhase.PlayIn)
        {
            var playIn=await PlayIn(scope,regular,ct);
            return regular with {Phase=TeamPhase.PlayIn,Rows=playIn.Participants,PlayIn=playIn,Bracket=null,FetchedAt=DateTimeOffset.UtcNow,Notice=playIn.Notice};
        }
        var post=await Postseason(scope,ct);
        var rows=regular.Rows.Select(r=>
        {
            var games=post.Games.Where(g=>g.Team.Key==r.Team.Key).ToArray();
            if(games.Length==0)return null;
            var series=HistoricalTeamParser.Series(games,post.End<DateTimeOffset.UtcNow);var last=series.LastOrDefault();
            var progress=last?.Round??"轮次未提供";
            if(last is {Round:"总决赛",Archived:true}&&last.Wins>last.Losses)progress="冠军";
            var wins=games.Count(g=>g.Won==true);var losses=games.Count(g=>g.Won==false);
            return r with {Rank=0,Wins=wins,Losses=losses,WinPercent=wins+losses>0?wins*100d/(wins+losses):null,GamesBehind=null,HomeRecord="—",AwayRecord="—",Streak="—",Points=null,OpponentPoints=null,Differential=null,Progress=progress};
        }).Where(r=>r is not null).Select(r=>r!).ToArray();
        PlayoffBracketSnapshot bracket;
        try{bracket=PlayoffBracketBuilder.Build(scope,regular.Rows.Select(r=>rows.FirstOrDefault(p=>p.Team.Key==r.Team.Key)??r).ToArray(),post.Games,post.End<DateTimeOffset.UtcNow);}
        catch(SourceFormatException){bracket=new(scope,[],[],null,"对阵记录未完整核验，请使用球队列表查看已获取资料");}
        return regular with {Rows=rows,Phase=TeamPhase.Playoffs,Bracket=bracket,Notice=rows.Length>0?"按当季季后赛种子显示 · 附加赛不计入":post.Start>DateTimeOffset.UtcNow?"季后赛尚未开始":post.End<DateTimeOffset.UtcNow?"该赛季季后赛资料未提供":"暂无已确认的季后赛场次"};
    }
    private async Task<TeamStandingsSnapshot> Regular(TeamScope scope,CancellationToken ct)
    {
        if(_regular.TryGetValue(scope.Season,out var cached)&&Fresh(cached.FetchedAt,Current(scope)||cached.Notice.Contains("刷新未完成")))return cached;
        var url=$"https://site.api.espn.com/apis/v2/sports/basketball/nba/standings?season={scope.EndYear}&seasontype=2&sort=winpercent%3Adesc%2Cplayoffseed%3Aasc";
        var json=await transport.ReadTeamArchive(new(url),ct);var rank=HistoricalTeamParser.Standings(json,scope,DateTimeOffset.UtcNow);
        try
        {
            using var document=JsonDocument.Parse(json);var conferences=HistoricalTeamParser.Get(document.RootElement,"children").EnumerateArray().Select(c=>HistoricalTeamParser.Str(c,"id")).ToArray();
            if(conferences.Length>4)throw new SourceFormatException("历史分组数量异常");
            foreach(var conference in conferences)
            {
                if(conference.Length==0||!conference.All(RuntimeCompat.IsAsciiDigit))throw new SourceFormatException("历史分组编号异常");
                var groups=await transport.ReadTeamArchive(new($"https://sports.core.api.espn.com/v2/sports/basketball/leagues/nba/seasons/{scope.EndYear}/types/2/groups/{conference}/children?limit=100"),ct);
                using var groupDocument=JsonDocument.Parse(groups);var items=HistoricalTeamParser.Get(groupDocument.RootElement,"items");
                if(items.ValueKind!=JsonValueKind.Array||items.GetArrayLength()>16)throw new SourceFormatException("赛区目录未提供");
                foreach(var item in items.EnumerateArray())
                {
                    if(!Uri.TryCreate(HistoricalTeamParser.Str(item,"$ref"),UriKind.Absolute,out var reference))throw new SourceFormatException("赛区目录地址异常");
                    var path=reference.AbsolutePath;
                    var match=System.Text.RegularExpressions.Regex.Match(path,$"/seasons/{scope.EndYear}/types/2/groups/(\\d+)$");if(!match.Success)throw new SourceFormatException("赛区目录赛季不一致");
                    var division=await transport.ReadTeamArchive(new($"https://site.api.espn.com/apis/v2/sports/basketball/nba/standings?season={scope.EndYear}&group={match.Groups[1].Value}&sort=winpercent%3Adesc%2Cplayoffseed%3Aasc"),ct);
                    rank=HistoricalTeamParser.Division(division,rank,DateTimeOffset.UtcNow);
                }
            }
        }
        catch(Exception ex) when(Failure(ex,ct))
        {
            if(cached is not null)
            {
                var old=cached.Rows.Where(r=>rank.Rows.Any(n=>n.Team.Key==r.Team.Key&&n.Record==r.Record)).ToDictionary(r=>r.Team.Key);
                var ranks=rank.DivisionRanks.ToDictionary(p=>p.Key,p=>p.Value);
                foreach(var pair in cached.DivisionRanks)if(old.ContainsKey(pair.Key)&&!ranks.ContainsKey(pair.Key))ranks[pair.Key]=pair.Value;
                var rows=rank.Rows.Select(r=>r.Team.Division.Length==0&&old.TryGetValue(r.Team.Key,out var previous)?r with {Team=r.Team with {Division=previous.Team.Division}}:r).ToArray();
                rank=rank with {Rows=rows,Teams=rows.Select(r=>r.Team).ToArray(),DivisionRanks=ranks,DivisionFetchedAt=cached.DivisionFetchedAt};
            }
            rank=rank with {Notice="赛区资料刷新未完成，保留已核验内容"};
        }
        ct.ThrowIfCancellationRequested();if(_regular.Count>=4)_regular.Remove(_regular.OrderBy(p=>p.Value.FetchedAt).First().Key);_regular[scope.Season]=rank;return rank;
    }
    private static void Validate(TeamScope scope)
    {
        if(!TeamScope.ValidSeason(scope.Season)||!Enum.IsDefined(typeof(TeamPhase),scope.Phase))throw new SourceFormatException("赛季或阶段无效");
    }
    private async Task<(IReadOnlyList<TeamGame> Games,DateTimeOffset Start,DateTimeOffset End)> Postseason(TeamScope scope,CancellationToken ct)
    {
        if(_post.TryGetValue(scope.Season,out var cached)&&Fresh(cached.Fetched,Current(scope)))return(cached.Games,cached.Start,cached.End);
        var json=await transport.ReadTeamArchive(new($"https://sports.core.api.espn.com/v2/sports/basketball/leagues/nba/seasons/{scope.EndYear}/types/3"),ct);
        var dates=HistoricalTeamParser.PostseasonDates(json);var games=new Dictionary<string,TeamGame>();
        if(dates.Start.Year!=scope.EndYear||dates.End.Year!=scope.EndYear)throw new SourceFormatException("季后赛日期不属于所选赛季");
        if(dates.Start<=DateTimeOffset.UtcNow)
        {
            var month=new DateTimeOffset(dates.Start.Year,dates.Start.Month,1,0,0,0,TimeSpan.Zero);var through=dates.End<DateTimeOffset.UtcNow?dates.End:DateTimeOffset.UtcNow;
            for(var count=0;month<=through&&count<10;month=month.AddMonths(1),count++)
            {
                var page=await transport.ReadTeamArchive(new("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/scoreboard?dates="+month.ToString("yyyyMM",CultureInfo.InvariantCulture)+"&limit=1000"),ct);
                using(var document=JsonDocument.Parse(page))
                {
                    var events=HistoricalTeamParser.Get(document.RootElement,"events");
                    if(events.ValueKind!=JsonValueKind.Array)throw new SourceFormatException("季后赛归档格式异常");
                    if(events.GetArrayLength()>=1000)throw new SourceFormatException("季后赛归档可能分页截断，暂不显示不完整汇总");
                }
                foreach(var game in HistoricalTeamParser.Games(page,scope))games[game.Id+"/"+game.Team.Key]=game;
            }
        }
        ct.ThrowIfCancellationRequested();var result=games.Values.OrderBy(g=>g.Tipoff).ToArray();
        if(_post.Count>=4)_post.Remove(_post.OrderBy(p=>p.Value.Fetched).First().Key);
        _post[scope.Season]=(result,dates.Start,dates.End,DateTimeOffset.UtcNow);return(result,dates.Start,dates.End);
    }
    public async Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,TeamScope scope,CancellationToken ct)
    {
        Validate(scope);
        if(scope.Phase==TeamPhase.PlayIn)return await PlayInOverview(team,scope,ct);
        if(Current(scope)&&scope.Phase==TeamPhase.Regular)return await _currentSource.GetTeamOverview(team,scope.Season,ct);
        var archiveId=HistoricalTeamParser.ArchiveId(team.Key);var notes=new List<string>();
        TeamStanding? summary=null;TeamAverages? averages=null;
        try
        {
            if(!_mobileIds.TryGetValue(team.Key,out var id))throw new SourceFormatException("历史统计编号未提供");
            (summary,averages)=HistoricalTeamParser.History(await History(team.Key,ct),team,id,scope);
            if(summary is null)notes.Add("该赛季该阶段的技术统计暂未提供");
            if(scope.Phase==TeamPhase.Regular&&summary is not null&&_regular.TryGetValue(scope.Season,out var dated)&&dated.Rows.FirstOrDefault(r=>r.Team.Key==team.Key) is {} row&&row.Record!=summary.Record)
            {
                summary=null;averages=null;notes.Add("历史统计与所选赛季战绩不一致，暂不展示未核验统计");
            }
        }
        catch(Exception ex) when(Failure(ex,ct)){notes.Add("技术统计读取未完成，缺失项显示 —");}
        IReadOnlyList<TeamGame> games=[];bool archived=!Current(scope),gamesAvailable=true;
        try
        {
            if(scope.Phase==TeamPhase.Playoffs)
            {
                var post=await Postseason(scope,ct);games=post.Games.Where(g=>g.Team.Key==team.Key).ToArray();archived=post.End<DateTimeOffset.UtcNow;
                if(games.Count==0)notes.Add(post.Start>DateTimeOffset.UtcNow?"季后赛尚未开始":archived&&post.Games.Count>0&&(summary is null||summary.Games==0)?"该队本赛季未参加季后赛":archived?"该赛季季后赛赛程资料暂未提供":"该队暂无已确认的季后赛场次");
            }
            else
            {
                var json=await transport.ReadTeamArchive(new($"https://site.api.espn.com/apis/site/v2/sports/basketball/nba/teams/{archiveId}/schedule?season={scope.EndYear}&seasontype=2"),ct);
                games=HistoricalTeamParser.Games(json,scope,team.Key);
            }
        }
        catch(Exception ex) when(Failure(ex,ct)){gamesAvailable=false;notes.Add("赛程读取未完成，请刷新重试");}
        ct.ThrowIfCancellationRequested();
        var complete=summary is not null&&games.Count(g=>g.Status==GameStatus.Finished&&g.Won.HasValue)==summary.Games;
        if(averages is not null&&complete&&games.Count>0)
        {
            var finished=games.Where(g=>g.Status==GameStatus.Finished&&g.Won.HasValue).ToArray();
            if(finished.Count(g=>g.Won==true)!=summary!.Wins||finished.Count(g=>g.Won==false)!=summary.Losses){averages=null;notes.Add("赛果与统计战绩尚未同步");}
            else if(summary.Points is {} points&&Math.Abs(points-finished.Average(g=>g.TeamPoints!.Value))>.15)
            {
                averages=null;notes.Add("场均得分与完整赛果不一致，暂不展示未核验统计");
            }
            else
            {
                var against=finished.Average(g=>g.OpponentPoints!.Value);var diff=finished.Average(g=>g.TeamPoints!.Value-g.OpponentPoints!.Value);
                averages=averages with {Metrics=averages.Metrics.Select(m=>m.Key=="opponent"?m with {Value=against}:m.Key=="differential"?m with {Value=diff}:m).ToArray()};
            }
        }
        return new(team,scope.Season,games,DateTimeOffset.UtcNow){Phase=scope.Phase,Summary=summary,Averages=averages,AveragesFetchedAt=averages is not null?DateTimeOffset.UtcNow:null,GamesFetchedAt=gamesAvailable?DateTimeOffset.UtcNow:null,GamesAvailable=gamesAvailable,Series=scope.Phase==TeamPhase.Playoffs?HistoricalTeamParser.Series(games,archived):Array.Empty<TeamSeries>(),Notice=string.Join(" · ",notes)};
    }
    private static bool Failure(Exception ex,CancellationToken ct)=>!ct.IsCancellationRequested&&ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or SourceFormatException or JsonException;

    private async Task<PlayInSnapshot> PlayIn(TeamScope scope,TeamStandingsSnapshot regular,CancellationToken ct)
    {
        if(_playIns.TryGetValue(scope.Season,out var cached)&&Fresh(cached.Time,Current(scope)))return cached.Snapshot;
        if(scope.EndYear<2020)return PlayInBuilder.Build(scope,regular,[],DateTimeOffset.MinValue,DateTimeOffset.MinValue,DateTimeOffset.UtcNow);
        var calendar=await transport.ReadTeamArchive(new($"https://sports.core.api.espn.com/v2/sports/basketball/leagues/nba/seasons/{scope.EndYear}/types/5"),ct);
        using(var doc=JsonDocument.Parse(calendar))if(HistoricalTeamParser.Str(doc.RootElement,"year")!=scope.EndYear.ToString(CultureInfo.InvariantCulture))throw new SourceFormatException("附加赛日历不属于所选赛季");
        var dates=HistoricalTeamParser.PostseasonDates(calendar,5);
        if(dates.Start.Year!=scope.EndYear||dates.End.Year!=scope.EndYear||dates.End-dates.Start>TimeSpan.FromDays(30))throw new SourceFormatException("附加赛阶段范围异常");
        var ledger=new Dictionary<string,TeamGame>();var next=new Dictionary<string,TeamGame>();
        if(dates.Start.AddDays(-2)<=DateTimeOffset.UtcNow)
        {
            var lower=dates.Start.AddDays(-2);var month=new DateTimeOffset(lower.Year,lower.Month,1,0,0,0,TimeSpan.Zero);
            for(var n=0;month<=dates.End.AddDays(2)&&month<=DateTimeOffset.UtcNow&&n<3;n++,month=month.AddMonths(1))
            {
                var json=await transport.ReadTeamArchive(new("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/scoreboard?dates="+month.ToString("yyyyMM",CultureInfo.InvariantCulture)+"&limit=1000"),ct);
                using(var doc=JsonDocument.Parse(json))
                {
                    var events=HistoricalTeamParser.Get(doc.RootElement,"events");if(events.ValueKind!=JsonValueKind.Array||events.GetArrayLength()>=1000)throw new SourceFormatException("附加赛归档可能截断");
                }
                foreach(var game in HistoricalTeamParser.Games(json,scope))ledger[game.Id+"/"+game.Team.Key]=game;
                foreach(var game in HistoricalTeamParser.Games(json,scope with {Phase=TeamPhase.Playoffs}))next[game.Id+"/"+game.Team.Key]=game;
            }
        }
        ct.ThrowIfCancellationRequested();var snapshot=PlayInBuilder.Build(scope,regular,ledger.Values,dates.Start,dates.End,DateTimeOffset.UtcNow,next.Values);
        if(_playIns.Count>=4)_playIns.Remove(_playIns.OrderBy(v=>v.Value.Time).First().Key);_playIns[scope.Season]=(snapshot,DateTimeOffset.UtcNow);return snapshot;
    }
    public async Task<PlayInBoxScore> GetPlayInDetail(TeamScope scope,TeamGame game,CancellationToken ct)
    {
        Validate(scope);if(scope.Phase!=TeamPhase.PlayIn||game.Season!=scope.Season||game.Id.Length==0||!game.Id.All(RuntimeCompat.IsAsciiDigit))throw new SourceFormatException("附加赛比赛编号无效");
        var json=await transport.ReadTeamArchive(new("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/summary?event="+game.Id),ct);
        var snapshot=PlayInDetailParser.Parse(json,scope,game,DateTimeOffset.UtcNow);ct.ThrowIfCancellationRequested();
        if(_playInBoxes.Count>=12)_playInBoxes.Remove(_playInBoxes.OrderBy(v=>v.Value.FetchedAt).First().Key);_playInBoxes[scope.Key+"/"+game.Id]=snapshot;return snapshot;
    }
    private async Task<TeamOverviewSnapshot> PlayInOverview(TeamIdentity team,TeamScope scope,CancellationToken ct)
    {
        var regular=await Regular(scope,ct);var snapshot=await PlayIn(scope,regular,ct);
        var games=snapshot.Games.Where(g=>g.Team.Key==team.Key||g.Opponent.Key==team.Key).Select(g=>g.Team.Key==team.Key?g:g with {Team=g.Opponent,Opponent=g.Team,Home=!g.Home,TeamPoints=g.OpponentPoints,OpponentPoints=g.TeamPoints}).ToArray();
        var complete=games.Length>0&&games.All(g=>g.Won.HasValue);var boxes=games.Select(g=>_playInBoxes.TryGetValue(scope.Key+"/"+g.Id,out var box)?box:null).ToArray();
        var teamBoxes=boxes.All(b=>b is not null)?boxes.Select(b=>b!.A.Team.Key==team.Key?b.A:b.B).ToArray():[];
        double? Avg(string key)=>complete&&teamBoxes.Length==games.Length&&teamBoxes.All(b=>b.Metric(key)?.Value is not null)?teamBoxes.Average(b=>b.Metric(key)!.Value!.Value):null;
        double? Rate(string made,string attempted)=>teamBoxes.Length>0&&teamBoxes.Length==games.Length&&teamBoxes.All(b=>b.Metric(made)?.Value is not null&&b.Metric(attempted)?.Value is not null)&&teamBoxes.Sum(b=>b.Metric(attempted)!.Value!.Value)>0?teamBoxes.Sum(b=>b.Metric(made)!.Value!.Value)*100/teamBoxes.Sum(b=>b.Metric(attempted)!.Value!.Value):null;
        var metrics=new TeamMetric[]{new("points","得分",complete?games.Average(g=>g.TeamPoints!.Value):null),new("opponent","失分",complete?games.Average(g=>g.OpponentPoints!.Value):null),new("differential","净胜分",complete?games.Average(g=>g.TeamPoints!.Value-g.OpponentPoints!.Value):null),new("rebounds","篮板",Avg("rebounds")),new("assists","助攻",Avg("assists")),new("fieldGoal","投篮",Rate("fgm","fga"),true),new("threePoint","三分",Rate("tpm","tpa"),true),new("freeThrow","罚球",Rate("ftm","fta"),true),new("turnovers","失误",Avg("turnovers")),new("offensiveRebounds","前场篮板",Avg("offensiveRebounds")),new("defensiveRebounds","后场篮板",Avg("defensiveRebounds")),new("steals","抢断",Avg("steals")),new("blocks","盖帽",Avg("blocks")),new("fouls","犯规",Avg("fouls"))};
        var summary=snapshot.Participants.FirstOrDefault(r=>r.Team.Key==team.Key);var notice=summary is null?(snapshot.Notice.Length>0?snapshot.Notice:"该队没有已确认的所选附加赛记录"):"仅统计所选赛季附加赛；其他场均需该队全部单场统计完整，缺失显示 —";
        return new(team,scope.Season,games,DateTimeOffset.UtcNow){Phase=scope.Phase,Summary=summary,Averages=new(team.Key,metrics),AveragesFetchedAt=DateTimeOffset.UtcNow,GamesFetchedAt=DateTimeOffset.UtcNow,Notice=notice};
    }
}
