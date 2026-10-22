namespace NbaLive.Core;

/// <summary>Public source endpoints, kept separate from application branding.</summary>
public static class SourcePages
{
    public static Uri BaseUri { get; } = new("https://nba.hupu.com/");
    public static Uri BoxScore(string id) => new(BaseUri, $"games/boxscore/{id}");
    public static Uri TeamRoster(string slug) => new(BaseUri, $"teams/{slug}");
}
