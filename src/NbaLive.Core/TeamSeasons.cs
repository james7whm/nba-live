using System.Globalization;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public enum TeamPhase { Regular=0, Playoffs=1, PlayIn=2 }
public sealed record TeamScope(string Season, TeamPhase Phase=TeamPhase.Regular)
{
    public string Key => Season+"/"+Phase;
    public string Label => ShortSeason(Season)+" · "+(Phase==TeamPhase.Regular?"常规赛":Phase==TeamPhase.PlayIn?"附加赛":"季后赛");
    public int ArchiveType => Phase==TeamPhase.Regular?2:Phase==TeamPhase.PlayIn?5:3;
    public int EndYear => int.Parse(Season.Substring(5),CultureInfo.InvariantCulture);
    public static string ShortSeason(string season)=>ValidSeason(season)?season.Substring(0,4)+"–"+season.Substring(7):season;
    public static bool ValidSeason(string? season)=>season is not null&&Regex.IsMatch(season,@"^(19|20)\d{2}-(19|20)\d{2}$")&&int.Parse(season.Substring(5),CultureInfo.InvariantCulture)==int.Parse(season.Substring(0,4),CultureInfo.InvariantCulture)+1;
    public static string? FromShort(string label)
    {
        var m=Regex.Match(label,@"^(\d{2})[-–](\d{2})$");if(!m.Success)return null;
        var start=int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);start+=start>=46?1900:2000;
        return (start+1)%100==int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)?$"{start}-{start+1}":null;
    }
}
public sealed record TeamSeason(string Id,bool Current)
{
    public string Label=>TeamScope.ShortSeason(Id)+(Current?" · 本赛季":"");
}
public sealed record TeamSeries(string Key,string Round,TeamIdentity Opponent,int Wins,int Losses,IReadOnlyList<TeamGame> Games,bool Archived)
{
    public string Score=>$"{Wins} : {Losses}";
    public string Status=>Archived&&Games.All(g=>g.Status==GameStatus.Finished)?"已结束":"已收录 "+Games.Count+" 场";
}
public interface IHistoricalTeamInsightsSource : ITeamInsightsSource
{
    Task<IReadOnlyList<TeamSeason>> GetSeasons(CancellationToken ct);
    Task<TeamStandingsSnapshot> GetStandings(TeamScope scope,CancellationToken ct);
    Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,TeamScope scope,CancellationToken ct);
    void Invalidate(TeamScope scope);
}
