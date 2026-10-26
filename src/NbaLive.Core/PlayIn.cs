namespace NbaLive.Core;

public sealed record PlayInBerth(string Conference,int Seed,TeamIdentity Team,TeamIdentity? Opponent,string GameId);
public sealed record PlayInSnapshot(TeamScope Scope,IReadOnlyList<TeamGame> Games,IReadOnlyList<TeamStanding> Participants,
    IReadOnlyList<PlayInBerth> Berths,DateTimeOffset Start,DateTimeOffset End,string Notice)
{
    public IReadOnlyList<string> NotHeldConferences {get;init;}=Array.Empty<string>();
    public bool Legacy=>Scope.EndYear==2020;
    public string ConferenceNotice(string conference)
    {
        if(Games.Any(g=>g.Team.Conference==conference))return "";
        if(Scope.EndYear<2020)return "该赛季未设置附加赛";
        if(NotHeldConferences.Contains(conference))return "该赛季"+conference+"未举行附加赛";
        return Notice.Length>0?Notice:"该分区附加赛资料暂未提供";
    }
}
public static class PlayInBuilder
{
    public static string Outcome(PlayInSnapshot snapshot,TeamGame game)
    {
        if(game.Won is not {} won)return game.Status==GameStatus.Live?"比赛进行中":"尚未开始 · 时间为北京时间";
        var winner=won?game.Team:game.Opponent;var berth=snapshot.Berths.FirstOrDefault(b=>b.GameId==game.Id&&b.Team.Key==winner.Key);
        if(berth is not null)return winner.Name+"晋级第"+berth.Seed+"种子";
        if(game.RoundKey=="nine-ten")return winner.Name+"进入第8种子争夺";
        if(snapshot.Legacy&&game.GameNumber==1)return winner.Name+"取得第1胜，仍需再胜1场";
        return "赛果已记录，晋级关系待确认";
    }
    public static bool Connects(TeamGame seven,TeamGame nine,TeamGame final)
    {
        if(seven.Won is not {} s||nine.Won is not {} n)return false;
        var expected=new[]{s?seven.Opponent.Key:seven.Team.Key,n?nine.Team.Key:nine.Opponent.Key};
        return expected.OrderBy(k=>k).SequenceEqual(new[]{final.Team.Key,final.Opponent.Key}.OrderBy(k=>k))&&seven.Tipoff<final.Tipoff&&nine.Tipoff<final.Tipoff;
    }
    public static PlayInSnapshot Build(TeamScope scope,TeamStandingsSnapshot regular,IEnumerable<TeamGame> ledger,DateTimeOffset start,DateTimeOffset end,DateTimeOffset now,IEnumerable<TeamGame>? nextRound=null)
    {
        if(scope.Phase!=TeamPhase.PlayIn||scope.Season!=regular.Season)throw new SourceFormatException("附加赛范围不一致");
        if(scope.EndYear<2020)return new(scope,[],[],[],start,end,"该赛季未设置附加赛");
        var data=ledger.ToArray();if(data.Any(g=>g.Season!=scope.Season))throw new SourceFormatException("附加赛混入其他赛季");
        var games=new List<TeamGame>();var metadata=regular.Rows.ToDictionary(r=>r.Team.Key);
        foreach(var group in data.GroupBy(g=>g.Id))
        {
            var sides=group.GroupBy(g=>g.Team.Key).Select(g=>g.Last()).ToArray();
            if(sides.Length!=2)throw new SourceFormatException("附加赛双方记录不完整");
            var a=sides[0];var b=sides[1];
            if(a.Team.Key!=b.Opponent.Key||a.Opponent.Key!=b.Team.Key||a.Home==b.Home||a.Tipoff!=b.Tipoff||a.Status!=b.Status||a.TeamPoints!=b.OpponentPoints||a.OpponentPoints!=b.TeamPoints||a.RoundKey!=b.RoundKey||a.GameNumber!=b.GameNumber)throw new SourceFormatException("附加赛双方记录不一致");
            if(!metadata.TryGetValue(a.Team.Key,out var ar)||!metadata.TryGetValue(a.Opponent.Key,out var br)||ar.Team.Conference!=br.Team.Conference)throw new SourceFormatException("附加赛球队分区不一致");
            if(a.Status==GameStatus.Finished&&!a.Won.HasValue)throw new SourceFormatException("附加赛已结束比分不完整");
            a=a with {Team=ar.Team,Opponent=br.Team};
            if(ar.Rank>br.Rank)a=a with {Team=br.Team,Opponent=ar.Team,Home=!a.Home,TeamPoints=a.OpponentPoints,OpponentPoints=a.TeamPoints};
            var ranks=new[]{ar.Rank,br.Rank}.OrderBy(n=>n).ToArray();
            if(a.RoundKey=="seven-eight"&&!ranks.SequenceEqual(new[]{7,8})||a.RoundKey=="nine-ten"&&!ranks.SequenceEqual(new[]{9,10})||scope.EndYear==2020&&!ranks.SequenceEqual(new[]{8,9}))throw new SourceFormatException("附加赛位次与比赛角色不符");
            // Some archive calendars (2023) start one local day after the explicitly tagged games.
            // Keep a bounded tolerance; season, type, opponents and roles remain independently checked.
            if(a.Tipoff<start.AddDays(-2)||a.Tipoff>end.AddDays(2))throw new SourceFormatException("附加赛日期不属于阶段范围");
            games.Add(a);
        }
        var berths=new List<PlayInBerth>();var uncertain=false;
        foreach(var conference in new[]{"西部","东部"})
        {
            var side=games.Where(g=>g.Team.Conference==conference).OrderBy(g=>g.Tipoff).ToArray();
            TeamIdentity? Winner(TeamGame? g)=>g?.Won is {} won?(won?g.Team:g.Opponent):null;
            void Add(int seed,TeamIdentity? team,TeamGame? decider)
            {
                if(team is null||decider is null)return;
                var next=(nextRound??[]).Where(g=>g.Season==scope.Season&&g.RoundKey is "round-of-16" or "first-round"&&g.Team.Key==team.Key).Select(g=>g.Opponent).GroupBy(t=>t.Key).ToArray();
                berths.Add(new(conference,seed,team,next.Length==1?next[0].First():null,decider.Id));
            }
            if(scope.EndYear==2020)
            {
                if(side.Length>2||side.Any(g=>g.RoundKey!="legacy")){uncertain=true;continue;}
                var first=side.FirstOrDefault();var eighth=first is null?null:metadata[first.Team.Key].Rank==8?first.Team:first.Opponent;
                if(first?.GameNumber!=1||eighth is null){if(side.Length>0)uncertain=true;continue;}
                if(Winner(first)?.Key==eighth.Key){if(side.Length>1)throw new SourceFormatException("2020 后续场次不应触发");Add(8,eighth,first);}
                else if(side.Length==2&&side[1].GameNumber==2&&first.Status==GameStatus.Finished)Add(8,Winner(side[1]),side[1]);
                continue;
            }
            if(side.GroupBy(g=>g.RoundKey).Any(g=>g.Count()>1)||side.Any(g=>g.RoundKey=="unverified")){uncertain=true;continue;}
            var seven=side.SingleOrDefault(g=>g.RoundKey=="seven-eight");var nine=side.SingleOrDefault(g=>g.RoundKey=="nine-ten");var final=side.SingleOrDefault(g=>g.RoundKey=="eighth");
            Add(7,Winner(seven),seven);
            if(final is not null)
            {
                if(seven is null||nine is null||!Connects(seven,nine,final)){uncertain=true;continue;}
                Add(8,Winner(final),final);
            }
        }
        var keys=games.SelectMany(g=>new[]{g.Team.Key,g.Opponent.Key}).ToHashSet();
        var rows=regular.Rows.Where(r=>keys.Contains(r.Team.Key)).Select(r=>
        {
            var played=games.Where(g=>g.Team.Key==r.Team.Key||g.Opponent.Key==r.Team.Key).ToArray();
            var wins=played.Count(g=>g.Won is {} won&&(g.Team.Key==r.Team.Key?won:!won));var losses=played.Count(g=>g.Won is {} won&&(g.Team.Key==r.Team.Key?!won:won));
            var seed=berths.FirstOrDefault(b=>b.Team.Key==r.Team.Key)?.Seed;
            var eliminated=played.Any(g=>g.Won is {} won&&(g.Team.Key==r.Team.Key?!won:won)&&(g.RoundKey is "nine-ten" or "eighth"||scope.EndYear==2020&&(g.GameNumber==2||regular.Rows.Single(v=>v.Team.Key==r.Team.Key).Rank==9)));
            return r with {Wins=wins,Losses=losses,WinPercent=wins+losses>0?wins*100d/(wins+losses):null,Progress=seed is {} s?$"第{s}种子":eliminated?"已淘汰":"待定",HomeRecord="—",AwayRecord="—",Streak="—",GamesBehind=null,Points=null,OpponentPoints=null,Differential=null};
        }).ToArray();
        if(games.Count>0){if(games.Min(g=>g.Tipoff)<start)start=games.Min(g=>g.Tipoff);if(games.Max(g=>g.Tipoff)>end)end=games.Max(g=>g.Tipoff);}
        var notice=start>now?"本赛季附加赛尚未开始":games.Count==0?"附加赛资料暂未提供":uncertain?"部分比赛角色或晋级关系待确认":scope.EndYear==2020?"2020 特殊赛制：第8位需1胜，第9位需2胜":"";
        var notHeld=scope.EndYear==2020&&end<now?new[]{"东部","西部"}.Where(c=>!games.Any(g=>g.Team.Conference==c)&&regular.Rows.FirstOrDefault(r=>r.Team.Conference==c&&r.Rank==8)?.GamesBehind is {} eight&&regular.Rows.FirstOrDefault(r=>r.Team.Conference==c&&r.Rank==9)?.GamesBehind is {} nine&&nine-eight>4).ToArray():[];
        return new(scope,games.OrderBy(g=>g.Tipoff).ThenBy(g=>g.Id,StringComparer.Ordinal).ToArray(),rows,berths,start,end,notice){NotHeldConferences=notHeld};
    }
}
