namespace NbaLive.Core;

public sealed class PlayerGameDetailSource(WebGameSource transport) : IPlayerGameDetailSource, IPlayerPortraitSource
{
    private const string Api = "http://games.mobileapi.hupu.com/1/7.5.59/basketballapi/";
    private readonly Dictionary<string, MobileMatch> _matches = [];
    private readonly Dictionary<string, string> _matchPlayers = [], _teamPlayers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<PlayerGameDetailSnapshot> GetDetail(PlayerDetailQuery query, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await Read(query, ct); }
        finally { _gate.Release(); }
    }
    private async Task<PlayerGameDetailSnapshot> Read(PlayerDetailQuery query, CancellationToken ct)
    {
        if (!_matches.TryGetValue(query.Game.Id, out var match))
        {
            match = await new AnchorSource(transport).Resolve(query.Game, ct) ?? throw new SourceFormatException("未能唯一对应本场比赛");
            _matches.Clear(); _matchPlayers.Clear(); _teamPlayers.Clear(); _matches[query.Game.Id] = match;
        }
        if (!_matchPlayers.TryGetValue(match.Id, out var roster))
            _matchPlayers[match.Id] = roster = await transport.ReadMobile(new(Api + "playerMatchStats?matchId=" + Uri.EscapeDataString(match.Id)), false, ct);
        var (teamId, ids) = PlayerGameDetailParser.MatchPlayers(roster, query, match.Id);
        var mobileId = query.Player.Id;
        if (!ids.Contains(mobileId))
        {
            if (!_teamPlayers.TryGetValue(teamId, out var team))
                _teamPlayers[teamId] = team = await transport.ReadMobile(new(Api + "teamPlayerList?competitionType=nba&competitionLeagueType=nba&leagueType=nba&teamId=" + Uri.EscapeDataString(teamId)), false, ct);
            mobileId = PlayerGameDetailParser.ResolvePlayer(team, query.Player.Id, teamId, ids);
        }
        var json = await transport.ReadMobile(new(Api + "playerMatchShootPointsStats?matchId=" + Uri.EscapeDataString(match.Id) + "&playerId=" + Uri.EscapeDataString(mobileId)), false, ct);
        ct.ThrowIfCancellationRequested();
        var snapshot = PlayerGameDetailParser.Parse(json, query, match.Id, mobileId, teamId, DateTimeOffset.UtcNow);
        var full = snapshot.Find(0)!.Statistics;
        var played = snapshot.Periods.Any(p => p.Statistics.Minutes.Any(c => c >= '1' && c <= '9')) ||
            full.FieldGoals is not ("0-0" or "—") || full.FreeThrows is not ("0-0" or "—") || full.Points > 0 || full.Rebounds > 0 || full.Assists > 0;
        // The roster is cached only for ID mapping. Its pregame DNP flag must not survive a player's actual appearance.
        var status = played ? "" : query.Player.Status;
        if (!played && query.Game.Status == GameStatus.Finished && PlayerGameDetailParser.DidNotPlay(roster, mobileId)) status = "未出场（本场名单）";
        return snapshot with { Periods = snapshot.Periods.Select(p => p with { Statistics = p.Statistics with { Status = status } }).ToArray() };
    }
    public Task<byte[]> GetPortrait(Uri uri, CancellationToken ct) => transport.ReadPlayerPortrait(uri, ct);
}
