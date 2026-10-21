namespace NbaLive.Core;

public static class PlayerFavorites
{
    public static FavoritePlayer[] Normalize(IEnumerable<FavoritePlayer?>? players) => (players ?? [])
        .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
        .Select(p => new FavoritePlayer(p!.Id.Trim(), p.Name.Trim()))
        .Where(p => p.Id.Length <= 180 && p.Name.Length <= 100)
        .DistinctBy(p => p.Id, StringComparer.Ordinal).Take(3).ToArray();

    public static string? Toggle(IReadOnlyList<FavoritePlayer> current, FavoritePlayer player, out FavoritePlayer[] updated)
    {
        if (current.Any(p => p.Id == player.Id)) { updated = current.Where(p => p.Id != player.Id).ToArray(); return null; }
        if (current.Count >= 3) { updated = current.ToArray(); return "最多关注 3 人，请先取消一位关注。"; }
        updated = Normalize(current.Append(player));
        return null;
    }
}

public sealed class PlayerRotation
{
    public string? CurrentId { get; private set; }
    public void Sync(IReadOnlyList<FavoritePlayer> players)
    {
        if (!players.Any(p => p.Id == CurrentId)) CurrentId = players.FirstOrDefault()?.Id;
    }
    public void Move(IReadOnlyList<FavoritePlayer> players, int direction, bool automatic = false, bool paused = false, bool visible = true)
    {
        Sync(players);
        if (players.Count == 0 || (automatic && (paused || !visible))) return;
        var index = players.ToList().FindIndex(p => p.Id == CurrentId);
        CurrentId = players[(index + direction % players.Count + players.Count) % players.Count].Id;
    }
}

public sealed record PlayerCard(string FirstLine, string SecondLine, string Details);
public static class PlayerCards
{
    public static PlayerCard Create(FavoritePlayer favorite, StatisticsState? state, string? gameId)
    {
        if (state?.Snapshot is not { } snapshot || snapshot.Game.Id != gameId)
            return new(favorite.Name, state?.HasError == true ? state.Message : "等待本场球员统计", "统计与文字直播分别采样；缺失值显示 —。");
        var player = snapshot.Away.Players.Concat(snapshot.Home.Players).FirstOrDefault(p => p.Id == favorite.Id);
        if (player is null) return new(favorite.Name, "本场暂无该球员数据", $"最近统计更新 {MatchTime.Format(snapshot.FetchedAt)}\n未自动替换关注对象。");
        static string N(int? value) => value?.ToString() ?? "—";
        var details = $"{player.Name} · {player.Role} · {player.Position}\n时间 {player.Minutes} · 投篮 {player.FieldGoals} · 三分 {player.ThreePointers} · 罚球 {player.FreeThrows}\n抢断 {N(player.Steals)} · 盖帽 {N(player.Blocks)} · 失误 {N(player.Turnovers)} · 犯规 {N(player.Fouls)} · +/- {N(player.PlusMinus)}\n最近统计更新 {MatchTime.Format(snapshot.FetchedAt)}；统计与比分分别采样。";
        return new($"{player.Name}  {N(player.Points)} 分 · {N(player.Rebounds)} 板 · {N(player.Assists)} 助",
            player.Status.Length > 0 ? player.Status : $"时间 {player.Minutes} · 投篮 {player.FieldGoals}", details);
    }
}
