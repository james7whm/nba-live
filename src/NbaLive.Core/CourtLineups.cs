using System.Text;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public enum CourtPresence { Unknown, OnCourt, OffCourt, Starter }
public sealed record PlayerCourtState(CourtPresence Presence, string Detail);

/// <summary>Live confirmation uses explicit lists; starting lineups are separately labeled references.</summary>
public static class CourtLineups
{
    public static IReadOnlyDictionary<string, PlayerCourtState> Resolve(TeamStatistics team, GameInfo game,
        SessionState? live, DateTimeOffset now)
    {
        Dictionary<string, PlayerCourtState> All(CourtPresence presence, string detail) =>
            team.Players.ToDictionary(p => p.Id, _ => new PlayerCourtState(presence, detail));
        var current = live?.Snapshot?.Game.Id == game.Id ? live.Snapshot.Game : game;
        IReadOnlyDictionary<string, PlayerCourtState> Starters(string phase) => team.Players.Count(p => p.IsStarter) == 5 ?
            team.Players.ToDictionary(p => p.Id, p => new PlayerCourtState(p.IsStarter ? CourtPresence.Starter : CourtPresence.OffCourt,
                $"{(p.IsStarter ? "首发参考" : "替补（首发参考视图）")} · {phase}；依据本场统计首发标记，不代表实时在场")) :
            All(CourtPresence.Unknown, $"{phase} · 首发名单不完整，无法展示首发五人");
        if (game.Status == GameStatus.Cancelled || current.Status == GameStatus.Cancelled)
            return All(CourtPresence.OffCourt, "比赛已结束或取消");
        if (game.Status == GameStatus.Finished || current.Status == GameStatus.Finished) return Starters("比赛已结束");
        if (current.Status == GameStatus.Scheduled) return Starters("比赛尚未开始");
        var sameGame = live?.Snapshot?.Game.Id == game.Id;
        var history = sameGame ? live!.History : [];
        var period = Math.Max(GamePageParser.Period(current.Progress), history.Select(e => e.Period).DefaultIfEmpty(0).Max());
        var healthy = live?.HasError != true && (!sameGame || now - live!.Snapshot!.FetchedAt <= TimeSpan.FromSeconds(30));
        if (period == 1 && healthy && !history.Any(e => BelongsTo(e.Text, team.Team))) return Starters("开赛初尚无该队换人名单");
        if (live?.Snapshot?.Game.Id != game.Id) return All(CourtPresence.Unknown, "等待本场换人名单");
        if (live.HasError || now - live.Snapshot.FetchedAt > TimeSpan.FromSeconds(30))
            return All(CourtPresence.Unknown, "连接中断或更新较旧，无法确认实时在场状态");
        var entry = live.History.Where(e => e.Period == period && BelongsTo(e.Text, team.Team)).MaxBy(e => e.Order);
        if (entry is null) return All(CourtPresence.Unknown, "本节尚未收到该队的完整换人名单");
        var list = Regex.Match(entry.Text, @"换人\s*[（(]([^）)]+)[）)]");
        var names = list.Success ? Regex.Split(list.Groups[1].Value, "[、，,]").Select(n => n.Trim()).ToArray() : [];
        if (names.Length != 5 || names.Select(Normalize).Distinct().Count() != 5)
            return All(CourtPresence.Unknown, "最新换人名单不完整，无法确认在场状态");
        var matched = new Dictionary<string, bool>();
        foreach (var name in names)
        {
            var key = Normalize(name);
            var candidates = team.Players.Where(p => Normalize(p.Name) == key).ToArray();
            var translated = false;
            if (candidates.Length == 0)
            {
                // Only a unique, single-character transliteration difference in a full name.
                candidates = team.Players.Where(p => key.Length >= 4 && Normalize(p.Name).Length == key.Length &&
                    Normalize(p.Name).Zip(key).Count(pair => pair.First != pair.Second) == 1).ToArray();
                translated = candidates.Length > 0;
            }
            if (candidates.Length == 0)
            {
                var family = Family(name);
                candidates = team.Players.Where(p => family.Length >= 2 && Family(p.Name) == family).ToArray();
                translated = candidates.Length > 0;
            }
            if (candidates.Length == 1 && !matched.TryAdd(candidates[0].Id, translated))
                return All(CourtPresence.Unknown, "换人名单姓名匹配有歧义");
        }
        var complete = matched.Count == 5;
        var origin = $"依据{entry.PeriodLabel} {entry.Clock}的五人换人名单；最近连通 {MatchTime.Format(live.Snapshot.FetchedAt)}";
        return team.Players.ToDictionary(p => p.Id, p => matched.TryGetValue(p.Id, out var translated) ?
            new PlayerCourtState(CourtPresence.OnCourt, "在场 · " + origin + (translated ? "；已校对名单译名" : "")) :
            complete ? new PlayerCourtState(CourtPresence.OffCourt, "不在场 · " + origin) :
            new PlayerCourtState(CourtPresence.Unknown, "名单存在未能可靠匹配的姓名，无法确认该球员状态"));
    }
    private static bool BelongsTo(string text, string team)
    {
        var at = text.IndexOf("换人", StringComparison.Ordinal);
        if (at < 0) return false;
        var prefix = text[..at].Trim();
        return prefix == team || prefix == team + "队";
    }
    private static string Normalize(string name) => string.Concat(name.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static string Family(string name) => Normalize(Regex.Split(name.Trim(), @"[·•\-－\s]+").Last());
}
