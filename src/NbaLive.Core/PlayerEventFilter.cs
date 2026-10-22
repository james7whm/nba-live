using System.Text;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

/// <summary>Conservative text matching against this game's roster, including unique short names.</summary>
public sealed class PlayerEventFilter
{
    private sealed record Alias(string Text, string PlayerId);
    private readonly Alias[] _aliases;
    private readonly HashSet<string> _favorites;
    private readonly Dictionary<string, (string Text, bool Match)> _cache = [];
    public int AvailableFavorites => _favorites.Count;
    public PlayerEventFilter(BoxScoreSnapshot? snapshot, IReadOnlyList<FavoritePlayer> favorites, string? gameId)
    {
        var players = snapshot is not null && snapshot.Game.Id == gameId ? snapshot.Away.Players.Concat(snapshot.Home.Players).DistinctBy(p => p.Id).ToArray() : [];
        var ids = players.Select(p => p.Id).ToHashSet();
        _favorites = favorites.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        _aliases = players.SelectMany(p => Names(p.Name).Select(name => new Alias(name, p.Id)))
            .Distinct().GroupBy(a => a.Text).Where(g => g.Select(a => a.PlayerId).Distinct().Count() == 1)
            .Select(g => g.First()).OrderByDescending(a => a.Text.Length).ToArray();
    }
    private static IEnumerable<string> Names(string name)
    {
        name = Regex.Replace(name, @"^演示[：:]\s*", "").Trim();
        var full = Normalize(name);
        if (full.Length >= 2) yield return full;
        var shortName = Normalize(Regex.Split(name, @"[·•\-－\s]+").Last());
        if (shortName.Length >= 2) yield return shortName;
    }
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    public bool Matches(LiveEntry entry)
    {
        if (_cache.TryGetValue(entry.Key, out var prior) && prior.Text == entry.Text) return prior.Match;
        var match = MatchText(entry.Text);
        _cache[entry.Key] = (entry.Text, match);
        return match;
    }
    private bool MatchText(string value)
    {
        if (_favorites.Count == 0) return false;
        var original = value.Normalize(NormalizationForm.FormKC);
        var positions = Enumerable.Range(0, original.Length).Where(i => char.IsLetterOrDigit(original[i])).ToArray();
        var text = new string(positions.Select(i => char.ToLowerInvariant(original[i])).ToArray());
        var mentions = new List<(int Start, int End, string Id)>();
        foreach (var alias in _aliases)
        {
            for (var from = 0; from <= text.Length - alias.Text.Length;)
            {
                var at = text.IndexOf(alias.Text, from, StringComparison.Ordinal);
                if (at < 0) break;
                var end = at + alias.Text.Length;
                static bool Latin(char c) => char.IsAsciiLetter(c);
                var before = positions[at] - 1;
                var after = positions[end - 1] + 1;
                if ((!Latin(alias.Text[0]) || before < 0 || !Latin(original[before])) &&
                    (!Latin(alias.Text[^1]) || after == original.Length || !Latin(original[after])))
                    mentions.Add((at, end, alias.PlayerId));
                from = at + 1;
            }
        }
        return mentions.Any(m => _favorites.Contains(m.Id) && !mentions.Any(other => other.Id != m.Id &&
            other.Start <= m.Start && other.End >= m.End && other.End - other.Start > m.End - m.Start));
    }
}
