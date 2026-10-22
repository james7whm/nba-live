using System.Globalization;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public enum StatisticsPreset { Full, Compact, Shooting }
public sealed record StatisticsColumnSetting(string Key, double Width);
public sealed record StatisticsTableLayout
{
    public StatisticsPreset Preset { get; init; }
    public bool Home { get; init; }
    public StatisticsColumnSetting[] Columns { get; init; } = [];
    public string? SortColumn { get; init; }
    public bool SortDescending { get; init; }
}

public static class StatisticsPresets
{
    private static readonly string[] Full = ["Star", "Name", "Minutes", "Points", "Rebounds", "Assists", "FieldGoals", "FieldGoalPercentage", "ThreePointers", "ThreePointPercentage", "FreeThrows", "FreeThrowPercentage", "Steals", "Blocks", "Turnovers", "Fouls", "OffensiveRebounds", "DefensiveRebounds", "PlusMinus", "Position"];
    private static readonly string[] Compact = ["Star", "Name", "Minutes", "Points", "Rebounds", "Assists"];
    private static readonly string[] Shooting = ["Star", "Name", "Minutes", "Points", "FieldGoals", "FieldGoalPercentage", "ThreePointers", "ThreePointPercentage", "FreeThrows", "FreeThrowPercentage"];
    public static IReadOnlyList<string> Columns(StatisticsPreset preset) => preset switch { StatisticsPreset.Compact => Compact, StatisticsPreset.Shooting => Shooting, _ => Full };
    public static StatisticsTableLayout[] Normalize(IEnumerable<StatisticsTableLayout?>? layouts) => (layouts ?? [])
        .Where(l => l is not null && Enum.IsDefined(l.Preset)).Select(l =>
        {
            var valid = Columns(l!.Preset);
            return l with
            {
                Columns = (l.Columns ?? []).Where(c => c is not null && valid.Contains(c.Key) && double.IsFinite(c.Width))
                    .DistinctBy(c => c.Key).Select(c => c with { Width = Math.Clamp(c.Width, 30, 480) }).ToArray(),
                SortColumn = l.SortColumn != "Star" && valid.Contains(l.SortColumn ?? "") ? l.SortColumn : null
            };
        }).DistinctBy(l => (l.Preset, l.Home)).Take(6).ToArray();
    public static double? Percentage(string value)
    {
        var match = Regex.Match(value, @"^\s*(\d+)\s*[-－—]\s*(\d+)\s*$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var made) || !int.TryParse(match.Groups[2].Value, out var attempted) || attempted <= 0 || made > attempted) return null;
        return made * 100d / attempted;
    }
    public static string DisplayPercentage(double? percentage) => percentage?.ToString("0.0'%'", CultureInfo.CurrentCulture) ?? "—";
}
