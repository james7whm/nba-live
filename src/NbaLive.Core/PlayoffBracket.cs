namespace NbaLive.Core;

public sealed record PlayoffBracketEdge(string From,string To,string TeamKey);
public sealed record PlayoffBracketSeries(string Id,string RoundKey,string Round,int Stage,string Conference,
    TeamIdentity A,TeamIdentity B,int? SeedA,int? SeedB,int WinsA,int WinsB,IReadOnlyList<TeamGame> Games,
    bool ScoreReliable,bool Completed,string? WinnerKey)
{
    public bool HasTeam(string key)=>A.Key==key||B.Key==key;
    public string Score=>ScoreReliable?$"{WinsA} : {WinsB}":"— : —";
    public string Status=>!ScoreReliable?"资料不完整":Completed?"已结束":Games.All(g=>g.Status==GameStatus.Scheduled)?"未开始":"进行中";
}
public sealed record PlayoffBracketSnapshot(TeamScope Scope,IReadOnlyList<PlayoffBracketSeries> Series,
    IReadOnlyList<PlayoffBracketEdge> Edges,string? ChampionKey,string Notice);

/// <summary>One node per real series; links are evidenced by an actual shared participant in the next round.</summary>
public static class PlayoffBracketBuilder
{
    public static int Stage(string key)=>key switch {"round-of-16" or "first-round"=>0,"conference-semifinal" or "conference-semi-final"=>1,"conference-final"=>2,"final" or "nba-final"=>3,_=>-1};
    public static PlayoffBracketSnapshot Build(TeamScope scope,IReadOnlyList<TeamStanding> standings,IEnumerable<TeamGame> ledger,bool archived)
    {
        if(scope.Phase!=TeamPhase.Playoffs)throw new SourceFormatException("对阵图仅接受季后赛范围");
        var metadata=standings.GroupBy(r=>r.Team.Key).ToDictionary(g=>g.Key,g=>g.First());
        var records=ledger.ToArray();if(records.Any(g=>g.Season!=scope.Season))throw new SourceFormatException("对阵图包含其他赛季");
        var normalized=new List<(TeamGame Game,bool Mirrored)>();
        foreach(var group in records.GroupBy(g=>g.Id,StringComparer.Ordinal))
        {
            var sides=group.GroupBy(g=>g.Team.Key).Select(g=>g.Last()).ToArray();
            if(sides.Length>2||sides.Length==0)throw new SourceFormatException("对阵比赛身份不唯一");
            var first=sides[0];if(first.Team.Key==first.Opponent.Key)throw new SourceFormatException("对阵双方身份相同");
            if(sides.Length==2)
            {
                var other=sides[1];
                if(other.Team.Key!=first.Opponent.Key||other.Opponent.Key!=first.Team.Key||other.TeamPoints!=first.OpponentPoints||other.OpponentPoints!=first.TeamPoints||other.Status!=first.Status||other.RoundKey!=first.RoundKey||other.Tipoff!=first.Tipoff||other.Home==first.Home||other.GameNumber!=first.GameNumber)
                    throw new SourceFormatException("对阵比赛双方记录不一致");
            }
            normalized.Add((first,sides.Length==2));
        }
        TeamIdentity Identity(TeamIdentity team)=>metadata.TryGetValue(team.Key,out var row)?row.Team:team;
        int? Seed(string key)=>metadata.TryGetValue(key,out var row)?row.PlayoffSeed:null;
        var nodes=new List<PlayoffBracketSeries>();
        foreach(var group in normalized.GroupBy(v=>v.Game.RoundKey+"/"+string.Join("/",new[]{v.Game.Team.Key,v.Game.Opponent.Key}.OrderBy(k=>k,StringComparer.Ordinal))))
        {
            var first=group.First().Game;var a=Identity(first.Team);var b=Identity(first.Opponent);
            if((Seed(a.Key)??int.MaxValue)>(Seed(b.Key)??int.MaxValue)||(Seed(a.Key)==Seed(b.Key)&&string.CompareOrdinal(a.Key,b.Key)>0))(a,b)=(b,a);
            var games=group.Select(v=>v.Game.Team.Key==a.Key?v.Game with {Team=a,Opponent=b}:v.Game with {Team=a,Opponent=b,Home=!v.Game.Home,TeamPoints=v.Game.OpponentPoints,OpponentPoints=v.Game.TeamPoints})
                .OrderBy(g=>g.Tipoff).ThenBy(g=>g.Id,StringComparer.Ordinal).ToArray();
            var stage=Stage(first.RoundKey);var numbers=games.Where(g=>g.GameNumber.HasValue).Select(g=>g.GameNumber!.Value).OrderBy(n=>n).ToArray();
            var reliable=stage>=0&&group.All(v=>v.Mirrored)&&games.All(g=>g.Status!=GameStatus.Finished||g.Won.HasValue)&&
                (numbers.Length==0||numbers.Length==games.Length&&numbers.SequenceEqual(Enumerable.Range(1,numbers.Length)));
            var winsA=games.Count(g=>g.Won==true);var winsB=games.Count(g=>g.Won==false);
            var completed=archived&&reliable&&games.All(g=>g.Status==GameStatus.Finished)&&winsA!=winsB;
            var conference=stage==3?"总决赛":a.Conference==b.Conference&&a.Conference.Length>0?a.Conference:"未分类";
            nodes.Add(new(scope.Season+"/"+group.Key,first.RoundKey,first.RoundName.Length>0?first.RoundName:"轮次未提供",stage,conference,a,b,Seed(a.Key),Seed(b.Key),winsA,winsB,games,reliable,completed,completed?(winsA>winsB?a.Key:b.Key):null));
        }
        var edges=new List<PlayoffBracketEdge>();var uncertain=false;
        for(var i=0;i<nodes.Count;i++)
        {
            var node=nodes[i];if(node.Stage<0||node.Stage>=3)continue;
            var candidates=nodes.Where(n=>n.Stage==node.Stage+1&&(n.Conference==node.Conference||n.Stage==3))
                .Select(n=>(Node:n,Shared:new[]{node.A.Key,node.B.Key}.Where(n.HasTeam).ToArray())).Where(v=>v.Shared.Length>0).ToArray();
            if(candidates.Length==0)continue;
            if(candidates.Length!=1||candidates[0].Shared.Length!=1){uncertain=true;continue;}
            var next=candidates[0];var winner=next.Shared[0];
            if(node.Games.Any(g=>g.Status!=GameStatus.Finished)||next.Node.Games.Min(g=>g.Tipoff)<node.Games.Max(g=>g.Tipoff)){uncertain=true;continue;}
            var scoreMatches=winner==node.A.Key?node.WinsA>node.WinsB:node.WinsB>node.WinsA;
            nodes[i]=node with {WinnerKey=winner,Completed=true,ScoreReliable=node.ScoreReliable&&scoreMatches};
            edges.Add(new(node.Id,next.Node.Id,winner));
        }
        var finals=nodes.Where(n=>n.Stage==3).ToArray();
        var claimed=standings.Where(s=>s.Progress=="冠军").Select(s=>s.Team.Key).Distinct().ToArray();
        var champion=finals.Length==1&&claimed.Length==1&&finals[0].Completed&&finals[0].ScoreReliable&&finals[0].WinnerKey==claimed[0]?claimed[0]:null;
        var partial=uncertain||nodes.Any(n=>!n.ScoreReliable||n.Stage<0||n.Conference=="未分类"||n.Stage>0&&edges.Count(e=>e.To==n.Id)<2);
        var notice=nodes.Count==0?"暂无已确认的季后赛对阵":partial?"部分晋级关系或赛果待确认，仅连接已核验的实际对阵":"";
        return new(scope,nodes.OrderBy(n=>n.Stage).ThenBy(n=>n.Games[0].Tipoff).ToArray(),edges,champion,notice);
    }
}
