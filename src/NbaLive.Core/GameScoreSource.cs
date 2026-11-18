using System.Globalization;
using System.Text.Json;

namespace NbaLive.Core;

/// <summary>Independent scoreboard and play-by-play data, matched by date and exact team IDs.</summary>
public sealed class GameScoreSource(WebGameSource transport)
{
    private readonly Dictionary<string,string> _ids = new();
    private static JsonElement Get(JsonElement e,string key) => HistoricalTeamParser.Get(e,key);
    private static string S(JsonElement e,string key) => HistoricalTeamParser.Str(e,key);
    private static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static int? Int(JsonElement e,string key) => int.TryParse(S(e,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n) && n >= 0 ? n : null;
    private static double ClockSeconds(string clock)
    {
        var parts = clock.Split(':');
        if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds < 60) return seconds;
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0 &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) && seconds >= 0 && seconds < 60) return minutes * 60 + seconds;
        return -1;
    }
    private static (string Away,string Home) Teams(GameInfo game) =>
        (HistoricalTeamParser.ArchiveId(TeamCatalog.Resolve(game.Away)?.Key ?? ""), HistoricalTeamParser.ArchiveId(TeamCatalog.Resolve(game.Home)?.Key ?? ""));
    private static bool Matches(JsonElement comp,GameInfo game)
    {
        var teams=Teams(game); var competitors=Items(Get(comp,"competitors")).ToArray();
        if (competitors.Length != 2) return false;
        var away=competitors.Where(x=>S(x,"homeAway")=="away").ToArray(); var home=competitors.Where(x=>S(x,"homeAway")=="home").ToArray();
        return away.Length==1 && home.Length==1 && S(away[0],"id")==teams.Away && S(home[0],"id")==teams.Home &&
            DateTimeOffset.TryParse(S(comp,"date"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date) && DateOnly.FromDateTime(date.ToOffset(TimeSpan.FromHours(8)).DateTime)==game.Date;
    }
    public static IReadOnlyList<string> FindGames(string json,GameInfo game)
    {
        using var doc=JsonDocument.Parse(json); var result=new List<string>();
        foreach(var e in Items(Get(doc.RootElement,"events")))
        {
            var id=S(e,"id"); if(id.Length==0 || !id.All(RuntimeCompat.IsAsciiDigit)) continue;
            if(Items(Get(e,"competitions")).Any(c=>Matches(c,game))) result.Add(id);
        }
        return result;
    }
    public static GameScoringSnapshot Parse(string json,GameInfo game,string id)
    {
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement; var header=Get(root,"header");
        var comps=Items(Get(header,"competitions")).Where(c=>S(c,"id")==id && Matches(c,game)).ToArray();
        if(S(header,"id")!=id || comps.Length!=1) throw new SourceFormatException("比分记录与所选比赛不匹配");
        var competitors=Items(Get(comps[0],"competitors")).ToArray();
        var a=competitors.Single(c=>S(c,"homeAway")=="away"); var h=competitors.Single(c=>S(c,"homeAway")=="home");
        var away=Int(a,"score"); var home=Int(h,"score");
        var sampled=game with { AwayScore=away,HomeScore=home };
        // Late reviews and corrections can receive a larger sequence number despite an earlier game clock.
        // Sort by period and clock, using source order only to resolve simultaneous plays.
        var plays=Items(Get(root,"plays")).Select(p=>new { Row=p, Period=Int(Get(p,"period"),"number") ?? 0,
            Clock=ClockSeconds(S(Get(p,"clock"),"displayValue")), Order=long.TryParse(S(p,"sequenceNumber"),NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:-1 })
            .Where(p=>p.Order>=0).OrderBy(p=>p.Period).ThenByDescending(p=>p.Clock).ThenBy(p=>p.Order).ToArray();
        var entries=new List<LiveEntry>(); long order=0;
        foreach(var p in plays)
        {
            var period=Int(Get(p.Row,"period"),"number"); var ascore=Int(p.Row,"awayScore"); var hscore=Int(p.Row,"homeScore");
            var clock=S(Get(p.Row,"clock"),"displayValue"); var pid=S(p.Row,"id");
            if(period is null or < 1 or > 20 || ascore is null || hscore is null || clock.Length==0 || pid.Length==0) continue;
            if(!clock.Contains(":")) clock += "秒";
            // Some feeds append review events after the end marker with the same period and clock.
            // Keep them in their actual period; the next period's 12:00/05:00 record supplies the boundary.
            entries.Add(new(pid,period.Value,clock,"","",$"{ascore}-{hscore}",order++));
        }
        var scoring=GameScoring.Build(sampled,entries);
        var ap=Items(Get(a,"linescores")).Select(x=>Int(x,"displayValue")).ToArray();
        var hp=Items(Get(h,"linescores")).Select(x=>Int(x,"displayValue")).ToArray();
        var count=Math.Max(4,Math.Max(ap.Length,hp.Length));
        if(count>20) throw new SourceFormatException("分节数据异常");
        var periods=Enumerable.Range(1,count).Select(i=>new PeriodScore(i,i<=ap.Length?ap[i-1]:null,i<=hp.Length?hp[i-1]:null,i<Math.Max(ap.Length,hp.Length)||game.Status==GameStatus.Finished)).ToArray();
        return scoring with { Periods=periods };
    }
    public async Task<GameScoringSnapshot> Get(GameInfo game,CancellationToken ct)
    {
        if(!_ids.TryGetValue(game.Id,out var id))
        {
            var candidates=new List<string>();
            foreach(var date in new[]{game.Date.AddDays(-1),game.Date})
            {
                var json=await transport.ReadTeamArchive(new Uri("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/scoreboard?dates="+date.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"&limit=100"),ct);
                candidates.AddRange(FindGames(json,game));
            }
            var unique=candidates.Distinct(StringComparer.Ordinal).ToArray();
            if(unique.Length!=1) throw new SourceFormatException("未找到唯一对应的比分记录");
            _ids[game.Id]=id=unique[0];
        }
        var summary=await transport.ReadTeamArchive(new Uri("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/summary?event="+id),ct);
        return Parse(summary,game,id);
    }
}
