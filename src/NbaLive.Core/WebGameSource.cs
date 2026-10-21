using System.Net;
using System.Security.Authentication;
using System.Collections.Concurrent;

namespace NbaLive.Core;

public sealed class WebGameSource : IGameSource, IStatisticsSource, IDisposable
{
    private readonly HttpClient _client;
    private readonly GamePageParser _parser = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly ConcurrentDictionary<string, GameInfo> _knownGames = new();
    private bool _useHttp;
    private sealed record SnapshotCache(string GameId, string Html, LiveSnapshot Snapshot);
    private SnapshotCache? _snapshotCache;
    private sealed record StatisticsCache(string GameId, string Html, BoxScoreSnapshot Snapshot);
    private StatisticsCache? _statisticsCache;
    public string Transport => _useHttp ? "HTTP（只读公开比赛数据）" : "HTTPS";

    public WebGameSource(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(12) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NbaLive/1.0.0 (+personal desktop reader)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        _client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    private async Task<string> Read(string path, CancellationToken ct)
    {
        await _requests.WaitAsync(ct);
        try
        {
            try { return await ReadPage($"{(_useHttp ? "http" : "https")}://{SourcePages.BaseUri.Host}{path}", ct); }
            catch (HttpRequestException ex) when (!_useHttp && IsTlsFailure(ex))
            {
                // Public legacy endpoint only; no cookies, credentials or user data are sent.
                // Certificate validation is never disabled. Transport is shown in settings.
                var html = await ReadPage($"http://{SourcePages.BaseUri.Host}{path}", ct);
                _useHttp = true;
                return html;
            }
        }
        finally { _requests.Release(); }
    }
    private async Task<string> ReadPage(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var requested = new Uri(url);
            var target = response.Headers.Location is { } location ? new Uri(requested, location) : null;
            var id = requested.AbsolutePath.Split('/').Last();
            if (requested.AbsolutePath.StartsWith("/games/playbyplay/", StringComparison.Ordinal) &&
                target?.Host == SourcePages.BaseUri.Host && target.AbsolutePath == $"/games/boxscore/{id}" &&
                _knownGames.TryGetValue(id, out var game) && game.Status == GameStatus.Scheduled)
                return ""; // Before tip-off the site redirects its unpublished live page to the preview.
            throw new SourceFormatException("数据响应异常，请稍后刷新。");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
    private static bool IsTlsFailure(Exception ex) => ex is AuthenticationException ||
        ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } ||
        (ex.InnerException is not null && IsTlsFailure(ex.InnerException));

    public async Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken cancellationToken)
    {
        var games = _parser.ParseGames(await Read($"/games/{date:yyyy-MM-dd}", cancellationToken), date);
        foreach (var game in games) _knownGames[game.Id] = game;
        return games;
    }

    public async Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken cancellationToken)
    {
        if (gameId.Length == 0 || !gameId.All(char.IsAsciiDigit)) throw new ArgumentException("比赛编号必须是数字。", nameof(gameId));
        string html;
        try { html = await Read($"/games/playbyplay/{gameId}", cancellationToken); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound &&
            _knownGames.TryGetValue(gameId, out var waiting) && waiting.Status == GameStatus.Scheduled)
        { return new(waiting, [], DateTimeOffset.UtcNow); }
        // The legacy site returns a genuinely empty 200 response before publishing the live page.
        if (string.IsNullOrWhiteSpace(html) && _knownGames.TryGetValue(gameId, out var scheduled) && scheduled.Status == GameStatus.Scheduled)
            return new(scheduled, [], DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var cached = Volatile.Read(ref _snapshotCache);
        if (cached?.GameId == gameId && cached.Html == html) return cached.Snapshot with { FetchedAt = now };
        var snapshot = _parser.ParseSnapshot(html, gameId, now);
        Volatile.Write(ref _snapshotCache, new(gameId, html, snapshot));
        return snapshot;
    }
    public void Dispose() => _client.Dispose();

    public async Task<BoxScoreSnapshot> GetBoxScore(string gameId, CancellationToken cancellationToken)
    {
        if (gameId.Length == 0 || !gameId.All(char.IsAsciiDigit)) throw new ArgumentException("比赛编号必须是数字。", nameof(gameId));
        string html;
        try { html = await Read(SourcePages.BoxScore(gameId).AbsolutePath, cancellationToken); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound &&
            _knownGames.TryGetValue(gameId, out var waiting) && waiting.Status == GameStatus.Scheduled)
        { return BoxScoreSnapshot.Waiting(waiting, DateTimeOffset.UtcNow); }
        if (string.IsNullOrWhiteSpace(html) && _knownGames.TryGetValue(gameId, out var scheduled) && scheduled.Status == GameStatus.Scheduled)
            return BoxScoreSnapshot.Waiting(scheduled, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var cached = Volatile.Read(ref _statisticsCache);
        if (cached?.GameId == gameId && cached.Html == html) return cached.Snapshot with { FetchedAt = now };
        var snapshot = new BoxScoreParser().Parse(html, gameId, now);
        Volatile.Write(ref _statisticsCache, new(gameId, html, snapshot));
        return snapshot;
    }
}
