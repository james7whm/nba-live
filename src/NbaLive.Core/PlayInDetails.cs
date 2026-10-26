using System.Globalization;
using System.Text.Json;

namespace NbaLive.Core;

public sealed record PlayInTeamBox(TeamIdentity Team,IReadOnlyList<int> Periods,IReadOnlyList<TeamMetric> Metrics,TeamStatistics Players)
{
    public TeamMetric? Metric(string key)=>Metrics.FirstOrDefault(m=>m.Key==key);
}
public sealed record PlayInBoxScore(TeamScope Scope,TeamGame Game,PlayInTeamBox A,PlayInTeamBox B,DateTimeOffset FetchedAt,string Notice);
public interface IPlayInDetailSource {Task<PlayInBoxScore> GetPlayInDetail(TeamScope scope,TeamGame game,CancellationToken ct);}

public static class PlayInDetailParser
{
    static JsonElement Get(JsonElement e,string key)=>HistoricalTeamParser.Get(e,key);
    static string S(JsonElement e,string key)=>HistoricalTeamParser.Str(e,key);
    static JsonElement[] List(JsonElement e)=>e.ValueKind==JsonValueKind.Array?e.EnumerateArray().ToArray():[];
    static string Value(JsonElement e)=>e.ValueKind is JsonValueKind.String or JsonValueKind.Number?e.ToString():"";
    static int? Int(string s,bool signed=false)=>int.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)&&(signed||n>=0)?n:null;
    static string Id(JsonElement e){var s=S(e,"id");return s.Length>0&&s.All(RuntimeCompat.IsAsciiDigit)?s:throw new SourceFormatException("归档编号无效");}
    static JsonElement One(IEnumerable<JsonElement> values,Func<JsonElement,bool> predicate)
    {
        var matches=values.Where(predicate).ToArray();return matches.Length<=1?matches.FirstOrDefault():throw new SourceFormatException("单场身份记录重复");
    }
    public static PlayInBoxScore Parse(string json,TeamScope scope,TeamGame game,DateTimeOffset now)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;var header=Get(root,"header");var season=Get(header,"season");
        if(scope.Phase!=TeamPhase.PlayIn||scope.Season!=game.Season||Id(header)!=game.Id||Int(S(season,"year"))!=scope.EndYear||Int(S(season,"type"))!=5||S(Get(header,"league"),"id")!="46")throw new SourceFormatException("单场统计不属于所选附加赛");
        var competition=One(List(Get(header,"competitions")),c=>S(c,"id")==game.Id);
        var sides=List(Get(competition,"competitors"));
        if(sides.Length!=2||sides.Select(p=>Id(Get(p,"team"))).Distinct().Count()!=2||!DateTimeOffset.TryParse(S(competition,"date"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)||date!=game.Tipoff)throw new SourceFormatException("单场比赛身份或时间不一致");
        var finished=Get(Get(Get(competition,"status"),"type"),"completed").ValueKind==JsonValueKind.True;
        if(game.Status==GameStatus.Finished&&!finished)throw new SourceFormatException("单场状态尚未同步");
        var box=Get(root,"boxscore");var teamBoxes=List(Get(box,"teams"));var playerBoxes=List(Get(box,"players"));var notes=new List<string>();
        PlayInTeamBox Team(TeamIdentity identity,int? expected,bool home)
        {
            var id=identity.ArchiveId.Length>0?identity.ArchiveId:HistoricalTeamParser.ArchiveId(identity.Key);
            var side=One(sides,p=>Id(Get(p,"team"))==id);
            if(side.ValueKind==JsonValueKind.Undefined||S(side,"homeAway")!=(home?"home":"away"))throw new SourceFormatException("统计球队或主客场不一致");
            var points=Int(S(side,"score"));if(finished&&(!points.HasValue||game.Status==GameStatus.Finished&&points!=expected))throw new SourceFormatException("统计比分与赛果不一致");
            var periods=List(Get(side,"linescores")).Select(p=>Int(S(p,"value").Length>0?S(p,"value"):S(p,"displayValue"))).ToArray();
            if(periods.Length>20||periods.Any(v=>!v.HasValue)||finished&&periods.Length>0&&(periods.Length<4||periods.Sum(p=>p!.Value)!=points))throw new SourceFormatException("单场分节得分与总分不一致");
            if(periods.Length==0)notes.Add("分节数据未提供");
            var source=One(teamBoxes,t=>Id(Get(t,"team"))==id);var raw=List(Get(source,"statistics"));
            if(raw.GroupBy(v=>S(v,"name")).Any(g=>g.Count()>1))throw new SourceFormatException("球队统计列重复");
            var fields=raw.ToDictionary(v=>S(v,"name"),v=>S(v,"displayValue"));
            string F(string key)=>fields.TryGetValue(key,out var value)?value:"";
            int? N(string key)=>Int(F(key));
            (int? Made,int? Attempts) Shot(string key)
            {
                var s=F(key).Split('-');if(s.Length!=2)return(null,null);var made=Int(s[0]);var attempts=Int(s[1]);
                if(made is not null&&attempts is not null&&made>attempts)throw new SourceFormatException("投篮命中多于出手");return(made,attempts);
            }
            var fg=Shot("fieldGoalsMade-fieldGoalsAttempted");var three=Shot("threePointFieldGoalsMade-threePointFieldGoalsAttempted");var ft=Shot("freeThrowsMade-freeThrowsAttempted");
            if(finished&&fg.Made is {} fgm&&three.Made is {} tpm&&ft.Made is {} ftm&&(fgm<tpm||fgm*2+tpm+ftm!=points))throw new SourceFormatException("投篮统计与单场得分不一致");
            double? Rate((int? Made,int? Attempts) shot)=>shot.Made is {} made&&shot.Attempts is >0?made*100d/shot.Attempts.Value:null;
            var metrics=new TeamMetric[]{new("points","得分",points),new("rebounds","篮板",N("totalRebounds")),new("assists","助攻",N("assists")),new("steals","抢断",N("steals")),new("blocks","盖帽",N("blocks")),new("turnovers","失误",N("totalTurnovers")??N("turnovers")),new("fouls","犯规",N("fouls")),new("offensiveRebounds","前场篮板",N("offensiveRebounds")),new("defensiveRebounds","后场篮板",N("defensiveRebounds")),new("fieldGoal","投篮命中率",Rate(fg),true),new("threePoint","三分命中率",Rate(three),true),new("freeThrow","罚球命中率",Rate(ft),true),new("fgm","投篮命中",fg.Made),new("fga","投篮出手",fg.Attempts),new("tpm","三分命中",three.Made),new("tpa","三分出手",three.Attempts),new("ftm","罚球命中",ft.Made),new("fta","罚球出手",ft.Attempts)};
            var players=new Dictionary<string,PlayerStatistics>();var roster=One(playerBoxes,t=>Id(Get(t,"team"))==id);
            foreach(var table in List(Get(roster,"statistics")))
            {
                var keys=List(Get(table,"keys")).Select(Value).ToArray();if(keys.Distinct().Count()!=keys.Length)throw new SourceFormatException("球员统计列重复");
                foreach(var entry in List(Get(table,"athletes")))
                {
                    var athlete=Get(entry,"athlete");var athleteId=Id(athlete);var cells=List(Get(entry,"stats")).Select(Value).ToArray();var dnp=Get(entry,"didNotPlay").ValueKind==JsonValueKind.True;
                    if(!dnp&&keys.Length!=cells.Length)throw new SourceFormatException("球员统计列数量变化");
                    string P(string key){var i=Array.IndexOf(keys,key);return i>=0&&i<cells.Length&&cells[i].Length>0?cells[i]:"—";}
                    int? PN(string key)=>dnp?null:Int(P(key),key=="plusMinus");
                    players[athleteId]=new("archive:"+athleteId,S(athlete,"displayName"),Get(entry,"starter").ValueKind==JsonValueKind.True,S(Get(athlete,"position"),"abbreviation"),dnp?"—":P("minutes"),dnp?"—":P("fieldGoalsMade-fieldGoalsAttempted"),dnp?"—":P("threePointFieldGoalsMade-threePointFieldGoalsAttempted"),dnp?"—":P("freeThrowsMade-freeThrowsAttempted"),PN("offensiveRebounds"),PN("defensiveRebounds"),PN("rebounds"),PN("assists"),PN("fouls"),PN("steals"),PN("turnovers"),PN("blocks"),PN("points"),PN("plusMinus"),dnp?"未出场 · "+S(entry,"reason"):""){JerseyNumber=S(athlete,"jersey")};
                }
            }
            if(raw.Length==0||players.Count==0)notes.Add("本场部分详细数据暂未提供");
            string Shooting((int? Made,int? Attempts) shot)=>shot.Made is {} made&&shot.Attempts is {} attempted?$"{made}-{attempted}":"—";
            var total=raw.Length==0?null:new PlayerStatistics("archive-total","球队合计",false,"—","—",Shooting(fg),Shooting(three),Shooting(ft),N("offensiveRebounds"),N("defensiveRebounds"),N("totalRebounds"),N("assists"),N("fouls"),N("steals"),N("totalTurnovers")??N("turnovers"),N("blocks"),points,null);
            return new(identity,periods.Select(p=>p!.Value).ToArray(),metrics,new(identity.Name,players.Values.ToArray(),total));
        }
        var a=Team(game.Team,game.TeamPoints,game.Home);var b=Team(game.Opponent,game.OpponentPoints,!game.Home);
        var current=game with {TeamPoints=(int?)a.Metric("points")?.Value,OpponentPoints=(int?)b.Metric("points")?.Value,Status=finished?GameStatus.Finished:game.Status};
        return new(scope,current,a,b,now,string.Join(" · ",notes.Distinct()));
    }
}

public sealed record PlayInDetailState(TeamScope? Scope,TeamGame? Game,PlayInBoxScore? Snapshot,bool Loading,string Message);
public sealed class PlayInDetailCoordinator(IPlayInDetailSource? source,Func<DateTimeOffset>? clock=null):IDisposable
{
    readonly Func<DateTimeOffset> _clock=clock??(()=>DateTimeOffset.UtcNow);
    readonly Dictionary<string,PlayInBoxScore> _cache=[];
    CancellationTokenSource? _cancel;Task? _task;long _epoch;bool _visible,_disposed;
    public PlayInDetailState State {get;private set;}=new(null,null,null,false,"");
    public event Action<PlayInDetailState>? Changed;
    void Publish()=>Changed?.Invoke(State);
    string Key=>(State.Scope?.Key??"")+"/"+(State.Game?.Id??"");
    void Cancel(){_epoch++;_cancel?.Cancel();_task=null;}
    public Task Select(TeamScope? scope,TeamGame? game)
    {
        if(_disposed)return Task.CompletedTask;
        if(State.Scope==scope&&State.Game?.Id==game?.Id)
        {
            if(State.Game==game)return Refresh(false);
            Cancel();State=State with {Game=game,Snapshot=null,Loading=false,Message=""};Publish();return Refresh(true);
        }
        Cancel();State=new(scope,game,null,false,"");if(_cache.TryGetValue(Key,out var cached))State=State with {Snapshot=cached};Publish();return Refresh(false);
    }
    public Task SetVisible(bool visible)
    {
        if(_disposed||_visible==visible)return Task.CompletedTask;_visible=visible;
        if(visible)return Refresh(false);Cancel();State=State with {Loading=false};Publish();return Task.CompletedTask;
    }
    public Task Refresh(bool force=true)
    {
        if(_disposed||!_visible||State.Game is null||State.Scope is null)return Task.CompletedTask;
        if(source is null){State=State with {Message="本场详细数据暂未提供"};Publish();return Task.CompletedTask;}
        if(_task is {IsCompleted:false})return _task;
        if(!force&&State.Snapshot is {} s&&State.Message.Length==0&&_clock()-s.FetchedAt<(s.Game.Status==GameStatus.Finished?TimeSpan.FromDays(1):TeamInsightsCoordinator.RefreshInterval))return Task.CompletedTask;
        _cancel?.Dispose();_cancel=new();return _task=Read(State.Scope,State.Game,++_epoch,_cancel.Token);
    }
    async Task Read(TeamScope scope,TeamGame game,long epoch,CancellationToken ct)
    {
        State=State with {Loading=true,Message="正在读取本场统计…"};Publish();
        try
        {
            var result=await source!.GetPlayInDetail(scope,game,ct);
            if(_disposed||!_visible||epoch!=_epoch||ct.IsCancellationRequested)return;
            if(result.Scope!=scope||result.Game.Id!=game.Id||result.A.Team.Key!=game.Team.Key||result.B.Team.Key!=game.Opponent.Key)throw new SourceFormatException("返回的统计比赛身份不一致");
            if(_cache.Count>=12)_cache.Remove(_cache.OrderBy(v=>v.Value.FetchedAt).First().Key);_cache[Key]=result;
            State=State with {Game=result.Game,Snapshot=result,Loading=false,Message=result.Notice};Publish();
        }
        catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or SourceFormatException or JsonException)
        {
            if(_disposed||epoch!=_epoch||ct.IsCancellationRequested)return;
            State=State with {Loading=false,Message=ex is SourceFormatException?"本场统计未通过核验，保留最近成功数据":"本场统计连接中断，保留最近成功数据"};Publish();
        }
    }
    public void Dispose(){if(_disposed)return;_disposed=true;Cancel();_cancel?.Dispose();_cache.Clear();}
}
