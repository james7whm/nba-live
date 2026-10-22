using System.Collections.ObjectModel;

namespace NbaLive.Core;

public sealed record TeamJerseyColor(string Key, string Name, uint Rgb)
{
    public string Hex => $"#{Rgb:X6}";
    // Choose the higher WCAG contrast against the fill, including light logo colors.
    public bool UseDarkNumber
    {
        get
        {
            static double Linear(uint channel) { var c = channel / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            var luminance = .2126 * Linear((Rgb >> 16) & 255) + .7152 * Linear((Rgb >> 8) & 255) + .0722 * Linear(Rgb & 255);
            return (luminance + .05) / .05 >= 1.05 / (luminance + .05);
        }
    }
}

public static class TeamPalette
{
    // Logo-inspired application colors, not an assertion of official brand hex values.
    // Shared shades are adjusted slightly so every franchise has a distinct RGB value.
    private sealed record Entry(TeamJerseyColor Color, string[] Aliases);
    private static Entry E(string key, string name, uint rgb, params string[] aliases) => new(new(key, name, rgb), aliases);
    private static readonly Entry[] Entries =
    [
        E("hawks", "老鹰", 0xE03A3E, "亚特兰大老鹰", "Atlanta Hawks", "ATL"),
        E("celtics", "凯尔特人", 0x008348, "波士顿凯尔特人", "Boston Celtics", "BOS"),
        E("nets", "篮网", 0x000000, "布鲁克林篮网", "Brooklyn Nets", "BKN", "BRK"),
        E("hornets", "黄蜂", 0x00788C, "夏洛特黄蜂", "Charlotte Hornets", "CHA"),
        E("bulls", "公牛", 0xCD1F43, "芝加哥公牛", "Chicago Bulls", "CHI"),
        E("cavaliers", "骑士", 0x860038, "克利夫兰骑士", "Cleveland Cavaliers", "CLE"),
        E("mavericks", "独行侠", 0x0064B1, "达拉斯独行侠", "小牛", "达拉斯小牛", "Dallas Mavericks", "DAL"),
        E("nuggets", "掘金", 0x0D2240, "丹佛掘金", "Denver Nuggets", "DEN"),
        E("pistons", "活塞", 0xC8102E, "底特律活塞", "Detroit Pistons", "DET"),
        E("warriors", "勇士", 0x1D428A, "金州勇士", "Golden State Warriors", "GSW"),
        E("rockets", "火箭", 0xCE0E2D, "休斯顿火箭", "Houston Rockets", "HOU"),
        E("pacers", "步行者", 0x0C2341, "印第安纳步行者", "Indiana Pacers", "IND"),
        E("clippers", "快船", 0x12173F, "洛杉矶快船", "LA Clippers", "Los Angeles Clippers", "LAC"),
        E("lakers", "湖人", 0x542C81, "洛杉矶湖人", "Los Angeles Lakers", "LAL"),
        E("grizzlies", "灰熊", 0x5D76A9, "孟菲斯灰熊", "Memphis Grizzlies", "MEM"),
        E("heat", "热火", 0x961A31, "迈阿密热火", "Miami Heat", "MIA"),
        E("bucks", "雄鹿", 0x00471A, "密尔沃基雄鹿", "Milwaukee Bucks", "MIL"),
        E("timberwolves", "森林狼", 0x0C2340, "明尼苏达森林狼", "Minnesota Timberwolves", "MIN"),
        E("pelicans", "鹈鹕", 0x0A2240, "新奥尔良鹈鹕", "New Orleans Pelicans", "NOP", "NO"),
        E("knicks", "尼克斯", 0xF58426, "纽约尼克斯", "New York Knicks", "NYK"),
        E("thunder", "雷霆", 0x057DC1, "俄克拉荷马城雷霆", "俄克拉荷马雷霆", "Oklahoma City Thunder", "OKC"),
        E("magic", "魔术", 0x0050B5, "奥兰多魔术", "Orlando Magic", "ORL"),
        E("76ers", "76人", 0x1D428B, "费城76人", "七六人", "费城七六人", "Philadelphia 76ers", "sixers", "PHI"),
        E("suns", "太阳", 0xF79E1E, "菲尼克斯太阳", "Phoenix Suns", "PHX", "PHO"),
        E("blazers", "开拓者", 0xCF0A2C, "波特兰开拓者", "Portland Trail Blazers", "trailblazers", "trail-blazers", "POR"),
        E("kings", "国王", 0x592D81, "萨克拉门托国王", "Sacramento Kings", "SAC"),
        E("spurs", "马刺", 0x111111, "圣安东尼奥马刺", "San Antonio Spurs", "SAS"),
        E("raptors", "猛龙", 0xBC2126, "多伦多猛龙", "Toronto Raptors", "TOR"),
        E("jazz", "爵士", 0x4E008E, "犹他爵士", "Utah Jazz", "UTA", "UTAH"),
        E("wizards", "奇才", 0x002B5C, "华盛顿奇才", "Washington Wizards", "WAS", "WSH")
    ];
    public static IReadOnlyList<TeamJerseyColor> All { get; } = new ReadOnlyCollection<TeamJerseyColor>(Entries.Select(e => e.Color).ToArray());
    public static TeamJerseyColor Unknown { get; } = new("unknown", "未识别球队", 0x334155);
    private static string Normalize(string? value) => string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static readonly IReadOnlyDictionary<string, TeamJerseyColor> Lookup = Entries
        .SelectMany(e => e.Aliases.Append(e.Color.Key).Append(e.Color.Name).Select(alias => (Alias: Normalize(alias), e.Color)))
        .GroupBy(e => e.Alias).ToDictionary(g => g.Key, g => g.First().Color);
    public static TeamJerseyColor Resolve(string? name, string? rosterSlug = null) =>
        Lookup.GetValueOrDefault(Normalize(rosterSlug)) ?? Lookup.GetValueOrDefault(Normalize(name)) ?? Unknown;
}
