namespace NbaLive.Core;

public sealed class TeamInsightsSource(WebGameSource transport) : ITeamInsightsSource
{
    private const string Mobile = "http://games.mobileapi.hupu.com/1/7.5.59/basketballapi/";
    private IReadOnlyDictionary<string,string> _ids = new Dictionary<string,string>();
    private TeamStandingsSnapshot? _previous;
    public async Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct)
    {
        var snapshot = TeamInsightsParser.Standings(await transport.ReadTeamInsightsPage("/standings",ct),DateTimeOffset.UtcNow);
        var previous = _previous?.Season == snapshot.Season ? _previous : null;
        var notices = new List<string>();
        try
        {
            var divisions = TeamInsightsParser.Divisions(await transport.ReadTeamInsightsPage("/standings/division",ct),snapshot,DateTimeOffset.UtcNow);
            snapshot = snapshot with { DivisionRanks=divisions, DivisionFetchedAt=DateTimeOffset.UtcNow };
            if(divisions.Count!=30) notices.Add("部分赛区排名与战绩尚未同步");
        }
        catch(Exception ex) when(IsDataFailure(ex,ct))
        {
            var valid=previous?.DivisionRanks.Where(d=>previous.Rows.Any(p=>p.Team.Key==d.Key && snapshot.Rows.Any(r=>r.Team.Key==p.Team.Key && r.Record==p.Record))).ToDictionary(d=>d.Key,d=>d.Value);
            snapshot=snapshot with {DivisionRanks=valid??new Dictionary<string,int>(),DivisionFetchedAt=previous?.DivisionFetchedAt};
            notices.Add("赛区排名刷新未完成");
        }
        try
        {
            snapshot=snapshot with {Averages=TeamInsightsParser.Averages(await transport.ReadTeamInsightsPage("/stats/teams",ct),snapshot),AveragesFetchedAt=DateTimeOffset.UtcNow};
        }
        catch(Exception ex) when(IsDataFailure(ex,ct))
        {
            snapshot=snapshot with {Averages=previous?.Averages??new Dictionary<string,TeamAverages>(),AveragesFetchedAt=previous?.AveragesFetchedAt};
            notices.Add("场均统计尚未完成本赛季核验或刷新，缺失项显示 —");
        }
        try { _ids=TeamInsightsParser.TeamIds(await transport.ReadMobile(new(Mobile+"scheduleList?competitionTag=nba&time_zone=Asia%2FShanghai"),false,ct)); }
        catch(Exception ex) when(IsDataFailure(ex,ct)) { notices.Add("球队赛程映射刷新未完成"); }
        ct.ThrowIfCancellationRequested();
        _previous=snapshot with {Notice=string.Join(" · ",notices)};
        return _previous;
    }
    public async Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct)
    {
        if(!_ids.TryGetValue(team.Key,out var id)) throw new SourceFormatException("该球队的赛程编号暂未提供，请刷新后重试");
        var json=await transport.ReadMobile(new(Mobile+"teamScheduleList?competitionType=NBA&competitionLeagueType=NBA&leagueType=NBA&teamId="+Uri.EscapeDataString(id)),false,ct);
        // The endpoint returns multiple seasons and phases. Filter explicit metadata, never infer from calendar dates.
        ct.ThrowIfCancellationRequested();
        return TeamInsightsParser.Schedule(json,team,id,season,DateTimeOffset.UtcNow);
    }
    private static bool IsDataFailure(Exception ex,CancellationToken ct) => !ct.IsCancellationRequested &&
        ex is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or SourceFormatException or System.Text.Json.JsonException;
}
