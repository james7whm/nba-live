namespace NbaLive.Core;

public sealed record TeamInsightsState(TeamStandingsSnapshot? Standings,TeamIdentity? SelectedTeam,TeamOverviewSnapshot? Overview,
    bool LoadingStandings,bool LoadingOverview,string StandingsMessage,string OverviewMessage)
{
    public TeamScope? Scope {get;init;}
    public IReadOnlyList<TeamSeason> Seasons {get;init;}=Array.Empty<TeamSeason>();
}

/// <summary>Window-owned demand; bounded per-season cache and generation guards for cancelled responses.</summary>
public sealed class TeamInsightsCoordinator(ITeamInsightsSource source,Func<DateTimeOffset>? clock=null) : IDisposable
{
    public static readonly TimeSpan RefreshInterval=TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock=clock??(()=>DateTimeOffset.UtcNow);
    private readonly Dictionary<string,TeamOverviewSnapshot> _cache=[];
    private readonly Dictionary<string,TeamStandingsSnapshot> _rankCache=[];
    private TeamScope? _scope;
    private CancellationTokenSource? _rankCancellation,_detailCancellation;
    private Task? _rankTask,_detailTask;
    private long _rankEpoch,_detailEpoch;
    private bool _active,_disposed;
    private bool _detailsActive=true;
    public TeamInsightsState State {get;private set;}=new(null,null,null,false,false,"等待读取球队排名","");
    public event Action<TeamInsightsState>? Changed;
    public bool Active=>_active;
    public bool DetailsActive=>_detailsActive;
    public IPlayInDetailSource? PlayInSource=>source as IPlayInDetailSource;
    public Task SetDetailsActive(bool active)
    {
        if(_disposed||_detailsActive==active)return Task.CompletedTask;
        _detailsActive=active;
        if(active)return LoadDetail(false);
        _detailEpoch++;_detailCancellation?.Cancel();_detailTask=null;
        State=State with {LoadingOverview=false};Publish();return Task.CompletedTask;
    }
    private bool Fresh(DateTimeOffset? time,TeamScope? scope=null)=>time.HasValue&&_clock()-time.Value<((scope??State.Scope) is {} s&&State.Seasons.Any(v=>v.Id==s.Season&&!v.Current)?TimeSpan.FromDays(1):RefreshInterval);
    private bool Recent(DateTimeOffset time,string notice,TeamScope? scope=null)=>Transient(notice)?_clock()-time<RefreshInterval:Fresh(time,scope);
    private static bool Transient(string text)=>new[]{"未完成","不一致","尚未同步","中断","异常"}.Any(text.Contains);
    private void Publish()=>Changed?.Invoke(State);
    public Task SetActive(bool active)
    {
        if(_disposed)return Task.CompletedTask;
        _active=active;
        if(!active)
        {
            _rankEpoch++;_detailEpoch++;_rankCancellation?.Cancel();_detailCancellation?.Cancel();_rankTask=null;_detailTask=null;
            State=State with {LoadingStandings=false,LoadingOverview=false};Publish();return Task.CompletedTask;
        }
        return Refresh(false);
    }
    public Task SelectTeam(string? key)
    {
        if(_disposed)return Task.CompletedTask;
        var team=State.Standings?.Rows.FirstOrDefault(r=>r.Team.Key==key)?.Team??State.Standings?.Teams.FirstOrDefault(t=>t.Key==key)??TeamCatalog.Resolve(key);
        if(team is null)return Task.CompletedTask;
        if(State.SelectedTeam?.Key==team.Key)return LoadDetail(false);
        _detailEpoch++;_detailCancellation?.Cancel();_detailTask=null;
        _cache.TryGetValue(CacheKey(team.Key),out var cached);
        State=State with {SelectedTeam=team,Overview=cached?.Season==State.Standings?.Season?cached:null,LoadingOverview=false,OverviewMessage=""};
        Publish();return LoadDetail(false);
    }
    private string CacheKey(string team)=>(_scope?.Key??"current")+"/"+team;
    public Task SelectScope(TeamScope scope)
    {
        if(_disposed||!TeamScope.ValidSeason(scope.Season)||!Enum.IsDefined(typeof(TeamPhase),scope.Phase)||Equals(_scope,scope))return Task.CompletedTask;
        _rankEpoch++;_detailEpoch++;_rankCancellation?.Cancel();_detailCancellation?.Cancel();_rankTask=null;_detailTask=null;_scope=scope;
        _rankCache.TryGetValue(scope.Key,out var rank);
        var selected=State.SelectedTeam;
        if(rank is not null&&selected is not null)selected=rank.Rows.FirstOrDefault(r=>r.Team.Key==selected.Key)?.Team??rank.Teams.FirstOrDefault(t=>t.Key==selected.Key)??TeamCatalog.Resolve(selected.Key);
        TeamOverviewSnapshot? overview=null;if(State.SelectedTeam is {} team)_cache.TryGetValue(CacheKey(team.Key),out overview);
        var loading=_active&&(rank is null||!Recent(rank.FetchedAt,rank.Notice,scope));
        State=State with {Scope=scope,Standings=rank,Overview=overview,SelectedTeam=selected,LoadingStandings=loading,LoadingOverview=false,StandingsMessage=rank?.Notice??"",OverviewMessage=overview?.Notice??""};Publish();
        return Refresh(false);
    }
    public Task Refresh(bool force=true)
    {
        if(_disposed||!_active)return Task.CompletedTask;
        if(_rankTask is {IsCompleted:false})return _rankTask;
        if(!force&&State.Standings is {} cached&&Recent(cached.FetchedAt,State.StandingsMessage))return LoadDetail(false);
        if(force&&_scope is not null&&source is IHistoricalTeamInsightsSource refreshable)refreshable.Invalidate(_scope);
        _rankCancellation?.Dispose();_rankCancellation=new();var epoch=++_rankEpoch;
        return _rankTask=ReadRank(epoch,_rankCancellation.Token,force);
    }
    private async Task ReadRank(long epoch,CancellationToken ct,bool force)
    {
        State=State with {LoadingStandings=true,StandingsMessage="正在更新球队排名…"};Publish();
        try
        {
            if(source is IHistoricalTeamInsightsSource historical&&(State.Seasons.Count==0||force))
            {
                var seasons=await historical.GetSeasons(ct);
                if(_disposed||!_active||ct.IsCancellationRequested||epoch!=_rankEpoch)return;
                if(seasons.Count==0)throw new SourceFormatException("赛季目录未提供");
                if(seasons.Count==1&&State.Seasons.Count>1)
                {
                    var current=seasons[0].Id;
                    seasons=seasons.Concat(State.Seasons.Where(s=>s.Id!=current).Select(s=>s with {Current=false})).OrderByDescending(s=>s.Id,StringComparer.Ordinal).ToArray();
                }
                _scope??=new(seasons.FirstOrDefault(s=>s.Current)?.Id??seasons.FirstOrDefault()?.Id??throw new SourceFormatException("赛季目录未提供"));
                if(!seasons.Any(s=>s.Id==_scope.Season))_scope=new(seasons.FirstOrDefault(s=>s.Current)?.Id??seasons.First().Id);
                if(State.Scope!=_scope)State=State with {Standings=null,Overview=null};
                State=State with {Seasons=seasons,Scope=_scope};Publish();
            }
            var requested=_scope;
            var result=source is IHistoricalTeamInsightsSource history&&requested is not null?await history.GetStandings(requested,ct):await source.GetStandings(ct);
            if(_disposed||!_active||ct.IsCancellationRequested||epoch!=_rankEpoch)return;
            if(requested is not null&&(result.Season!=requested.Season||result.Phase!=requested.Phase))throw new SourceFormatException("返回的数据不属于当前选择范围");
            if(result.Season!=State.Standings?.Season)
            {
                if(source is not IHistoricalTeamInsightsSource)_cache.Clear();_detailEpoch++;_detailCancellation?.Cancel();_detailTask=null;
                State=State with {Overview=null};
            }
            if(requested is not null){if(_rankCache.Count>=8)_rankCache.Remove(_rankCache.OrderBy(v=>v.Value.FetchedAt).First().Key);_rankCache[requested.Key]=result;}
            var selected=result.Rows.FirstOrDefault(r=>r.Team.Key==State.SelectedTeam?.Key)?.Team??result.Teams.FirstOrDefault(t=>t.Key==State.SelectedTeam?.Key)??State.SelectedTeam??result.Rows.FirstOrDefault(r=>r.Team.Conference=="东部")?.Team;
            State=State with {Standings=result,SelectedTeam=selected,LoadingStandings=false,StandingsMessage=result.Notice,Scope=requested??new TeamScope(result.Season)};Publish();
            await LoadDetail(force);
        }
        catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or SourceFormatException or System.Text.Json.JsonException)
        {
            if(_disposed||epoch!=_rankEpoch||ct.IsCancellationRequested)return;
            State=State with {LoadingStandings=false,StandingsMessage=ex is SourceFormatException?"排名数据格式异常，保留最近成功内容":"排名连接中断，保留最近成功内容"};Publish();
            await LoadDetail(false);
        }
    }
    private Task LoadDetail(bool force)
    {
        if(_disposed||!_active||!_detailsActive||State.SelectedTeam is not {} team||State.Standings is not {} rank)return Task.CompletedTask;
        if(_detailTask is {IsCompleted:false})return _detailTask;
        if(!force&&State.Overview?.Team.Key==team.Key&&State.Overview.Season==rank.Season&&State.Overview.Phase==rank.Phase&&Recent(State.Overview.FetchedAt,State.OverviewMessage))return Task.CompletedTask;
        _detailCancellation?.Dispose();_detailCancellation=new();var epoch=++_detailEpoch;
        return _detailTask=ReadDetail(team,new(rank.Season,rank.Phase),epoch,_detailCancellation.Token);
    }
    private async Task ReadDetail(TeamIdentity team,TeamScope scope,long epoch,CancellationToken ct)
    {
        State=State with {LoadingOverview=true,OverviewMessage="正在读取所选赛季资料…"};Publish();
        try
        {
            var result=source is IHistoricalTeamInsightsSource historical?await historical.GetTeamOverview(team,scope,ct):await source.GetTeamOverview(team,scope.Season,ct);
            if(_disposed||!_active||epoch!=_detailEpoch||ct.IsCancellationRequested)return;
            if(result.Team.Key!=team.Key||result.Season!=scope.Season||result.Phase!=scope.Phase)throw new SourceFormatException("球队赛程赛季或阶段不匹配");
            var previous=State.Overview;
            if(previous is not null&&previous.Team.Key==team.Key&&previous.Season==scope.Season&&previous.Phase==scope.Phase)
            {
                if(result.Averages is null)result=result with {Averages=previous.Averages,Summary=previous.Summary,AveragesFetchedAt=previous.AveragesFetchedAt};
                if(!result.GamesAvailable)result=result with {Games=previous.Games,Series=previous.Series,GamesFetchedAt=previous.GamesFetchedAt};
            }
            if(_cache.Count>=32)_cache.Remove(_cache.OrderBy(v=>v.Value.FetchedAt).First().Key);
            _cache[CacheKey(team.Key)]=result;
            State=State with {Overview=result,LoadingOverview=false,OverviewMessage=result.Notice};Publish();
        }
        catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or SourceFormatException or System.Text.Json.JsonException)
        {
            if(_disposed||epoch!=_detailEpoch||ct.IsCancellationRequested)return;
            State=State with {LoadingOverview=false,OverviewMessage=ex is SourceFormatException?ex.Message:"赛程连接中断，保留最近成功内容"};Publish();
        }
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;_active=false;_rankEpoch++;_detailEpoch++;
        _rankCancellation?.Cancel();_detailCancellation?.Cancel();_rankCancellation?.Dispose();_detailCancellation?.Dispose();_cache.Clear();_rankCache.Clear();
    }
}
