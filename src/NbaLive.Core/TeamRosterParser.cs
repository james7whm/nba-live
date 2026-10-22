using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public static class TeamRosterParser
{
    public static IReadOnlyDictionary<string, string> Parse(string html)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.QuerySelectorAll(".x_list"))
        {
            var number = LiveEntry.Normalize(row.QuerySelector(".c1")?.TextContent ?? "");
            var href = row.QuerySelector(".c2 a[href*='/players/']")?.GetAttribute("href");
            if (!Regex.IsMatch(number, @"^\d{1,2}$") || !Uri.TryCreate(SourcePages.BaseUri, href, out var uri) ||
                uri.Host != SourcePages.BaseUri.Host || !uri.AbsolutePath.StartsWith("/players/", StringComparison.Ordinal)) continue;
            var id = uri.AbsolutePath.Split('/').Last();
            if (id.Length == 0 || conflicts.Contains(id)) continue;
            if (numbers.TryGetValue(id, out var existing) && existing != number) { numbers.Remove(id); conflicts.Add(id); }
            else numbers[id] = number; // Keep strings: 0 and 00 are different jersey numbers.
        }
        if (numbers.Count == 0) throw new SourceFormatException("球队号码名单尚不可用。");
        return numbers;
    }
}
