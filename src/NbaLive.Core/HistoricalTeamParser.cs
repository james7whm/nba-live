using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public static class HistoricalTeamParser
{
    internal static JsonElement Get(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var value)?value:default;
    internal static string Str(JsonElement e,string key)=>Value(Get(e,key));
    private static string Value(JsonElement e)=>e.ValueKind is JsonValueKind.Number or JsonValueKind.String?e.ToString():"";
    private static IEnumerable<JsonElement> List(JsonElement e)=>e.ValueKind==JsonValueKind.Array?e.EnumerateArray():Enumerable.Empty<JsonElement>();
    private static double? Number(string s)=>double.TryParse(s.Trim().TrimEnd('%'),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&!double.IsInfinity(n)&&!double.IsNaN(n)?n:null;
    private static int? Integer(string s)=>Number(s) is {} n&&n>=0&&n<=int.MaxValue&&Math.Truncate(n)==n?(int)n:null;
    private static string Id(JsonElement e,string key){var id=Str(e,key);return id.Length>0&&id.All(RuntimeCompat.IsAsciiDigit)?id:throw new SourceFormatException("归档球队或比赛编号异常");}
    private static readonly string[] Keys=["","hawks","celtics","pelicans","bulls","cavaliers","mavericks","nuggets","pistons","warriors","rockets","pacers","clippers","lakers","heat","bucks","timberwolves","nets","knicks","magic","76ers","suns","blazers","kings","spurs","thunder","jazz","wizards","raptors","grizzlies","hornets"];
    public static TeamIdentity Identity(JsonElement data,string conference="")
    {
        var id=Id(data,"id");var index=Integer(id)??0;
        if(index<1||index>=Keys.Length)throw new SourceFormatException("归档包含暂不支持的历史球队身份");
        var current=TeamCatalog.Resolve(Keys[index])!;var english=Str(data,"displayName");
        var names=new Dictionary<string,(string Name,string Full)>(StringComparer.OrdinalIgnoreCase){
            ["Seattle SuperSonics"]=("超音速","西雅图超音速"),["New Jersey Nets"]=("新泽西篮网","新泽西篮网"),
            ["Charlotte Bobcats"]=("山猫","夏洛特山猫"),["New Orleans Hornets"]=("新奥尔良黄蜂","新奥尔良黄蜂"),
            ["New Orleans/Oklahoma City Hornets"]=("新奥尔良/俄城黄蜂","新奥尔良/俄城黄蜂"),["Vancouver Grizzlies"]=("温哥华灰熊","温哥华灰熊"),
            ["Washington Bullets"]=("子弹","华盛顿子弹")};
        if(names.TryGetValue(english,out var old))return new(current.Key,old.Name,old.Full,english,conference,""){ArchiveId=id,LogoTeam=old.Name+"（历史标识）"};
        // The dated archive supplies the historical identity. Never relabel an unknown identity with today's name or mark.
        var known=english.Equals(current.EnglishName,StringComparison.OrdinalIgnoreCase)||english=="LA Clippers";
        return new(current.Key,known?current.Name:english,known?current.FullName:english,english,conference,""){ArchiveId=id,LogoTeam=known?current.Key:english+"（历史标识）"};
    }
    public static string ArchiveId(string key)
    {
        var index=Array.IndexOf(Keys,key);return index>0?index.ToString(CultureInfo.InvariantCulture):throw new SourceFormatException("该球队的归档编号未提供");
    }
    public static TeamStandingsSnapshot Standings(string json,TeamScope scope,DateTimeOffset now)
    {
        using var d=JsonDocument.Parse(json);var root=d.RootElement;
        if(Integer(Str(Get(root,"season"),"year"))!=scope.EndYear)throw new SourceFormatException("归档排名返回了其他赛季");
        var rows=new List<TeamStanding>();
        foreach(var group in List(Get(root,"children")))
        {
            var name=Str(group,"name");var conference=name=="Eastern Conference"?"东部":name=="Western Conference"?"西部":name;
            var standings=Get(group,"standings");
            if(Integer(Str(standings,"season"))!=scope.EndYear||Integer(Str(standings,"seasonType"))!=2)throw new SourceFormatException("归档排名阶段不匹配");
            var rank=0;double previous=double.MaxValue;
            foreach(var entry in List(Get(standings,"entries")))
            {
                var fields=List(Get(entry,"stats")).ToArray();if(fields.Select(s=>Str(s,"name")).Distinct().Count()!=fields.Length)throw new SourceFormatException("历史排名指标重复");
                var stats=fields.ToDictionary(s=>Str(s,"name"));
                string S(string key,string field="displayValue")=>stats.TryGetValue(key,out var stat)?Str(stat,field):"";
                double? N(string key)=>Number(S(key,"value"));
                var team=Identity(Get(entry,"team"),conference);var wins=Integer(S("wins"))??throw new SourceFormatException("历史胜场未提供");
                var losses=Integer(S("losses"))??throw new SourceFormatException("历史负场未提供");var rate=N("winPercent");
                if(rate is {} r&&r>previous+.00001)throw new SourceFormatException("归档排名未按请求顺序返回");previous=rate??previous;
                var seed=Integer(S("playoffSeed"));
                rows.Add(new(team,++rank,wins,losses,wins+losses>0?rate*100:null,N("gamesBehind"),Empty(S("Home")),Empty(S("Road")),N("avgPointsFor"),N("avgPointsAgainst"),N("differential"),Streak(S("streak"))) {PlayoffSeed=seed>0?seed:null});
            }
        }
        if(rows.Count==0||rows.GroupBy(r=>r.Team.Conference).Count()<2||rows.Select(r=>r.Team.Key).Distinct().Count()!=rows.Count)throw new SourceFormatException("历史排名未完整提供或球队身份重复");
        return new(scope.Season,rows,now){Teams=rows.Select(r=>r.Team).ToArray(),Notice="该季赛区资料未提供；对应名次显示 —"};
    }
    private static string Empty(string text)=>string.IsNullOrWhiteSpace(text)||text is "-" or "--"?"—":text;
    public static TeamStandingsSnapshot Division(string json,TeamStandingsSnapshot rank,DateTimeOffset now)
    {
        using var d=JsonDocument.Parse(json);var root=d.RootElement;var standings=Get(root,"standings");var year=new TeamScope(rank.Season).EndYear;
        if(Integer(Str(Get(root,"season"),"year"))!=year||Integer(Str(standings,"season"))!=year||Integer(Str(standings,"seasonType"))!=2)throw new SourceFormatException("赛区归档范围不匹配");
        var name=Str(root,"name");var division=name switch {"Atlantic"=>"大西洋赛区","Central"=>"中部赛区","Southeast"=>"东南赛区","Northwest"=>"西北赛区","Pacific"=>"太平洋赛区","Southwest"=>"西南赛区","Midwest"=>"中西部赛区",_=>name};
        if(string.IsNullOrWhiteSpace(division)||Get(root,"children").ValueKind==JsonValueKind.Array&&Get(root,"children").GetArrayLength()>0)throw new SourceFormatException("该分组并非赛区");
        var ranks=rank.DivisionRanks.ToDictionary(p=>p.Key,p=>p.Value);var metadata=new Dictionary<string,TeamIdentity>();var position=0;
        foreach(var entry in List(Get(standings,"entries")))
        {
            position++;var identity=Identity(Get(entry,"team"));var original=rank.Rows.FirstOrDefault(r=>r.Team.Key==identity.Key);if(original is null)throw new SourceFormatException("赛区包含不属于本赛季的球队");
            var stats=List(Get(entry,"stats")).ToArray();var wins=Integer(Str(stats.FirstOrDefault(s=>Str(s,"name")=="wins"),"displayValue"));var losses=Integer(Str(stats.FirstOrDefault(s=>Str(s,"name")=="losses"),"displayValue"));
            if(wins!=original.Wins||losses!=original.Losses)continue;
            ranks[identity.Key]=position;metadata[identity.Key]=original.Team with {Division=division};
        }
        if(position==0)throw new SourceFormatException("赛区球队资料未提供");
        var rows=rank.Rows.Select(r=>metadata.TryGetValue(r.Team.Key,out var team)?r with {Team=team}:r).ToArray();
        return rank with {Rows=rows,Teams=rows.Select(r=>r.Team).ToArray(),DivisionRanks=ranks,DivisionFetchedAt=now,Notice=ranks.Count==rows.Length?"":"部分赛区归档未提供，缺失名次显示 —"};
    }
    private static string Streak(string s)=>s.StartsWith("W",StringComparison.Ordinal)?s.Substring(1)+"连胜":s.StartsWith("L",StringComparison.Ordinal)?s.Substring(1)+"连败":Empty(s);
    private static JsonElement Stage(JsonElement root,TeamPhase phase)
    {
        var map=Get(Get(Get(Get(Get(Get(Get(root,"result"),"teamHistoryStatsMap"),"avg"),"leagueStatsMap"),"NBA"),"teamCompetitionStatsMap"),"NBA");
        return phase==TeamPhase.PlayIn?default:Get(Get(map,"teamCompetitionStageStatsMap"),phase==TeamPhase.Regular?"REGULAR":"PLAYOFF");
    }
    public static IReadOnlyList<TeamSeason> Seasons(string json,string current)
    {
        using var d=JsonDocument.Parse(json);ValidateHistory(d.RootElement,null);
        var seasons=List(Get(Stage(d.RootElement,TeamPhase.Regular),"statsRowList")).Select(r=>List(Get(r,"quotaList")).FirstOrDefault()).Select(v=>TeamScope.FromShort(Value(v)))
            .Where(s=>s is not null).Select(s=>s!).Where(s=>StringComparer.Ordinal.Compare(s,current)<=0).Append(current).Distinct().OrderByDescending(s=>s,StringComparer.Ordinal).Select(s=>new TeamSeason(s,s==current)).ToArray();
        return seasons;
    }
    private static void ValidateHistory(JsonElement root,string? id)
    {
        if(Get(root,"success").ValueKind!=JsonValueKind.True||id is not null&&Str(Get(root,"result"),"teamId")!=id)throw new SourceFormatException("历史球队统计身份异常");
    }
    public static (TeamStanding? Summary,TeamAverages? Averages) History(string json,TeamIdentity team,string mobileId,TeamScope scope)
    {
        using var d=JsonDocument.Parse(json);ValidateHistory(d.RootElement,mobileId);var stage=Stage(d.RootElement,scope.Phase);
        if(stage.ValueKind==JsonValueKind.Undefined)return(null,null);
        var headers=List(Get(stage,"headerStrList")).Select(Value).ToArray();
        if(headers.Distinct().Count()!=headers.Length||!new[]{"赛季","场数","胜负","得分"}.All(headers.Contains))throw new SourceFormatException("历史统计列定义已变化");
        var row=List(Get(stage,"statsRowList")).LastOrDefault(r=>TeamScope.FromShort(Value(List(Get(r,"quotaList")).FirstOrDefault()))==scope.Season);
        if(row.ValueKind==JsonValueKind.Undefined)return(null,null);
        var cells=List(Get(row,"quotaList")).Select(Value).ToArray();if(cells.Length!=headers.Length)throw new SourceFormatException("历史统计列数量异常");
        string S(string key){var i=Array.IndexOf(headers,key);return i>=0?cells[i]:"";}
        double? N(string key)=>Number(S(key));var match=Regex.Match(S("胜负"),@"^(\d+)胜(\d+)负$");
        if(!match.Success)throw new SourceFormatException("历史战绩未提供");
        var wins=int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);var losses=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
        if(wins+losses!=Integer(S("场数")))throw new SourceFormatException("历史场数与战绩不一致");
        double? P(string key)=>wins+losses>0?N(key):null;
        var summary=new TeamStanding(team,0,wins,losses,P("胜率"),null,"—","—",P("得分"),null,null,"—");
        var metrics=new[]{new TeamMetric("points","得分",P("得分")),new("opponent","失分",null),new("differential","净胜分",null),
            new("rebounds","篮板",P("篮板")),new("assists","助攻",P("助攻")),new("fieldGoal","投篮",P("投篮%"),true),new("threePoint","三分",P("三分%"),true),new("freeThrow","罚球",P("罚球%"),true),new("turnovers","失误",P("失误")),
            new("offensiveRebounds","前场篮板",P("前板")),new("defensiveRebounds","后场篮板",P("后板")),new("steals","抢断",P("抢断")),new("blocks","盖帽",P("盖帽")),new("fouls","犯规",P("犯规"))};
        return(summary,new(team.Key,metrics));
    }
    public static (DateTimeOffset Start,DateTimeOffset End) PostseasonDates(string json,int phaseType=3)
    {
        using var d=JsonDocument.Parse(json);var r=d.RootElement;
        if(Str(r,"type")!=phaseType.ToString(CultureInfo.InvariantCulture)||!DateTimeOffset.TryParse(Str(r,"startDate"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var start)||!DateTimeOffset.TryParse(Str(r,"endDate"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var end)||end<start||end-start>TimeSpan.FromDays(250))throw new SourceFormatException("阶段日期范围未提供");
        return(start,end);
    }
    public static IReadOnlyList<TeamGame> Games(string json,TeamScope scope,string? selected=null)
    {
        using var d=JsonDocument.Parse(json);var root=d.RootElement;var requested=Get(root,"requestedSeason");
        if(requested.ValueKind!=JsonValueKind.Undefined&&(Integer(Str(requested,"year"))!=scope.EndYear||Integer(Str(requested,"type"))!=scope.ArchiveType))throw new SourceFormatException("赛程返回了其他赛季或阶段");
        if(Get(root,"events").ValueKind!=JsonValueKind.Array)throw new SourceFormatException("历史赛程未提供");
        var games=new Dictionary<string,TeamGame>();
        foreach(var e in List(Get(root,"events")))
        {
            var year=Integer(Str(Get(e,"season"),"year"));var phase=Integer(Str(Get(e,"seasonType"),"type"))??Integer(Str(Get(e,"season"),"type"));
            if(year!=scope.EndYear||phase!=scope.ArchiveType)continue;
            var c=List(Get(e,"competitions")).FirstOrDefault();var typeInfo=Get(c,"type");var type=Str(typeInfo,"slug");var abbreviation=Str(typeInfo,"abbreviation");
            if(type=="commissioners-cup"||type=="play-in"&&scope.Phase!=TeamPhase.PlayIn||abbreviation=="CC")continue;
            if(type.Length==0)type=abbreviation switch {"RD16"=>"round-of-16","QTR"=>"conference-semifinal","SEMI"=>"conference-final","FINAL"=>"final",_=>"source-round-"+Str(typeInfo,"id")};
            var people=List(Get(c,"competitors")).ToArray();if(people.Length!=2)throw new SourceFormatException("历史比赛双方不完整");
            if(!DateTimeOffset.TryParse(Str(e,"date"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var tipoff))continue;
            var id=Id(e,"id");var status=Get(Get(c,"status"),"type");var state=Str(status,"state");
            var gameStatus=Get(status,"completed").ValueKind==JsonValueKind.True?GameStatus.Finished:state=="in"?GameStatus.Live:state=="pre"?GameStatus.Scheduled:GameStatus.Unknown;
            if(gameStatus==GameStatus.Unknown)continue;
            var teams=people.Select(p=>Identity(Get(p,"team"))).ToArray();
            var note=string.Join(" ",List(Get(c,"notes")).Select(n=>Str(n,"headline")));var number=Regex.Match(note,@"Game\s+(\d+)");
            var round=Round(type,Str(Get(c,"type"),"text"));
            if(scope.Phase==TeamPhase.PlayIn)
            {
                type=scope.EndYear==2020?"legacy":note.IndexOf("7th Place vs 8th Place",StringComparison.OrdinalIgnoreCase)>=0?"seven-eight":note.IndexOf("9th Place vs 10th Place",StringComparison.OrdinalIgnoreCase)>=0?"nine-ten":note.IndexOf("8th Seed Game",StringComparison.OrdinalIgnoreCase)>=0?"eighth":"unverified";
                round=type switch{"legacy"=>"第8种子争夺（2020）","seven-eight"=>"7–8位赛","nine-ten"=>"9–10位赛","eighth"=>"第8种子争夺战",_=>"轮次待确认"};
                number=Regex.Match(note,@"Game\s+(\d+)",RegexOptions.IgnoreCase);
            }
            for(var i=0;i<2;i++)
            {
                var team=teams[i];if(selected is not null&&selected!=team.Key)continue;
                int? Score(int n){var score=Get(people[n],"score");return Integer(score.ValueKind==JsonValueKind.Object?Str(score,"value"):Value(score));}
                games[id+"/"+team.Key]=new(id,scope.Season,team,teams[1-i],Str(people[i],"homeAway")=="home",tipoff.ToOffset(TimeSpan.FromHours(8)),gameStatus,
                    gameStatus==GameStatus.Scheduled?null:Score(i),gameStatus==GameStatus.Scheduled?null:Score(1-i)){RoundKey=scope.Phase!=TeamPhase.Regular?type:"",RoundName=scope.Phase!=TeamPhase.Regular?round:"",GameNumber=number.Success?Integer(number.Groups[1].Value):null};
            }
        }
        return games.Values.OrderBy(g=>g.Tipoff).ThenBy(g=>g.Id,StringComparer.Ordinal).ToArray();
    }
    private static string Round(string key,string fallback)=>key switch {"round-of-16" or "first-round"=>"首轮","conference-semifinal" or "conference-semi-final"=>"分区半决赛","conference-final"=>"分区决赛","final" or "nba-final"=>"总决赛",_=>string.IsNullOrEmpty(fallback)?"轮次未提供":fallback};
    public static IReadOnlyList<TeamSeries> Series(IEnumerable<TeamGame> games,bool archived)=>games.Where(g=>!string.IsNullOrEmpty(g.RoundKey)).GroupBy(g=>g.RoundKey+"/"+g.Opponent.Key)
        .Select(g=>new TeamSeries(g.Key,g.First().RoundName,g.First().Opponent,g.Count(v=>v.Won==true),g.Count(v=>v.Won==false),g.OrderBy(v=>v.Tipoff).ToArray(),archived)).OrderBy(s=>s.Games[0].Tipoff).ToArray();
}
