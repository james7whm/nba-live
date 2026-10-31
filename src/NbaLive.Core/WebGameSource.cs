using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Collections.Concurrent;

namespace NbaLive.Core;

public sealed class WebGameSource : IGameSource, IStatisticsSource, IDisposable
{
    private readonly HttpClient _client;
    private readonly GamePageParser _parser = new();
    private readonly PriorityRequestGate _requests = new();
    private readonly ConcurrentDictionary<string, GameInfo> _knownGames = new();
    private bool _useHttp;
    private bool _mobileHttp;
    private sealed record SnapshotCache(string GameId, string Html, LiveSnapshot Snapshot);
    private SnapshotCache? _snapshotCache;
    private sealed record StatisticsCache(string GameId, string Html, BoxScoreSnapshot Snapshot);
    private StatisticsCache? _statisticsCache;
    private readonly bool _loadRosters;
    private sealed record RosterCache(DateTimeOffset RetryAfter, IReadOnlyDictionary<string, string> Numbers);
    private readonly ConcurrentDictionary<string, RosterCache> _rosters = new();
    public string Transport => (_useHttp ? "HTTP（只读公开比赛数据）" : "HTTPS") + (_mobileHttp ? " · 移动接口 HTTP（匿名只读）" : "");

    public WebGameSource(HttpMessageHandler? handler = null, bool loadRosters = false)
    {
        _loadRosters = loadRosters;
        _client = new HttpClient(handler ??
#if NETFRAMEWORK
        new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false, AllowAutoRedirect = false,
            SslProtocols = SslProtocols.Tls12
        }
#else
        new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }
#endif
        ) { Timeout = TimeSpan.FromSeconds(12) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NbaLive/1.0.0 (+personal desktop reader)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        _client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    internal Task<string> ReadTeamInsightsPage(string path, CancellationToken ct)
    {
        if (path is not ("/standings" or "/standings/division" or "/stats/teams")) throw new ArgumentException("Unsupported team page", nameof(path));
        return Read(path, ct);
    }
    internal async Task<string> ReadTeamArchive(Uri uri,CancellationToken ct)
    {
        if(uri.Scheme!="https"||uri.Host is not ("site.api.espn.com" or "sports.core.api.espn.com"))throw new ArgumentException("Unsupported archive endpoint",nameof(uri));
        using var lease=await _requests.Enter(false,ct);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(8));
        return await ReadPage(uri.AbsoluteUri,deadline.Token,json:true);
    }
    private async Task<string> Read(string path, CancellationToken ct)
    {
        var live = path.StartsWith("/games/playbyplay/", StringComparison.Ordinal);
        var optional = path.StartsWith("/teams/", StringComparison.Ordinal);
        using var lease = await _requests.Enter(live, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(optional ? 2 : live ? 8 : 5));
        try { return await ReadPage($"{(_useHttp ? "http" : "https")}://{SourcePages.BaseUri.Host}{path}", deadline.Token); }
        catch (HttpRequestException ex) when (!_useHttp && IsTlsFailure(ex))
        {
            // Both transport attempts share one deadline; certificate validation is never disabled.
            var html = await ReadPage($"http://{SourcePages.BaseUri.Host}{path}", deadline.Token);
            _useHttp = true;
            return html;
        }
    }
    internal async Task<string> ReadMobile(Uri uri, bool live, CancellationToken ct)
    {
        if (uri.Host is not ("games.mobileapi.hupu.com" or "live-api.liangle.com") ||
            uri.Scheme != "https" && !(uri.Host == "games.mobileapi.hupu.com" && uri.Scheme == "http"))
            throw new ArgumentException("Unsupported data endpoint.", nameof(uri));
        using var lease = await _requests.Enter(live, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var mobile = uri.Host == "games.mobileapi.hupu.com";
        if (mobile && uri.Scheme == "http") _mobileHttp = true;
        try { return await ReadPage(mobile && _mobileHttp ? new UriBuilder(uri) { Scheme = "http", Port = -1 }.Uri.AbsoluteUri : uri.AbsoluteUri, deadline.Token, json: true); }
        catch (HttpRequestException ex) when (mobile && !_mobileHttp && IsTlsFailure(ex))
        {
            var json = await ReadPage(new UriBuilder(uri) { Scheme = "http", Port = -1 }.Uri.AbsoluteUri, deadline.Token, json: true);
            _mobileHttp = true;
            return json;
        }
    }
    internal async Task<byte[]> ReadPlayerPortrait(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != "https" || !uri.Host.EndsWith(".hoopchina.com.cn", StringComparison.OrdinalIgnoreCase)) throw new IOException("头像地址不可用");
        using var lease = await _requests.Enter(false, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new SourceHttpException(response.StatusCode);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new IOException("头像文件过大");
        using var input = await response.Content.ReadAsStreamAsync(); using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token)) > 0)
        {
            if (output.Length + read > 2 * 1024 * 1024) throw new IOException("头像文件过大");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private async Task<string> ReadPage(string url, CancellationToken ct, bool json = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (json) request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request, ct);
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
        if(!response.IsSuccessStatusCode) throw new SourceHttpException(response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }
    private static bool IsTlsFailure(Exception ex) => ex is AuthenticationException ||
        ex is WebException { Status: WebExceptionStatus.TrustFailure or WebExceptionStatus.SecureChannelFailure } ||
        // .NET Framework also reports a peer closing the HTTPS handshake as SendFailure + IOException.
        ex is WebException { Status: WebExceptionStatus.SendFailure, InnerException: IOException } ||
#if !NETFRAMEWORK
        ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } ||
#endif
        (ex.InnerException is not null && IsTlsFailure(ex.InnerException));

    public async Task<IReadOnlyList<GameInfo>> GetGames(DateOnly date, CancellationToken cancellationToken)
    {
        var games = _parser.ParseGames(await Read($"/games/{date:yyyy-MM-dd}", cancellationToken), date);
        if (date == MatchTime.Today || games.Any(g => g.Status == GameStatus.Live))
        {
            try
            {
                var json = await ReadMobile(new Uri("http://games.mobileapi.hupu.com/1/7.5.59/basketballapi/scheduleList?competitionTag=nba&time_zone=Asia%2FShanghai"), false, cancellationToken);
                games = AnchorParser.MergeSchedule(json, games);
            }
            catch (Exception ex) when (ex is HttpRequestException or SourceFormatException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The optional mobile schedule must not make the original list unavailable.
            }
        }
        foreach (var game in games) _knownGames[game.Id] = game;
        return games;
    }

    public async Task<LiveSnapshot> GetSnapshot(string gameId, CancellationToken cancellationToken)
    {
        if (gameId.Length == 0 || !gameId.All(RuntimeCompat.IsAsciiDigit)) throw new ArgumentException("比赛编号必须是数字。", nameof(gameId));
        string html;
        try { html = await Read($"/games/playbyplay/{gameId}", cancellationToken); }
        catch (HttpRequestException ex) when (RuntimeCompat.StatusCode(ex) == HttpStatusCode.NotFound &&
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
        if (gameId.Length == 0 || !gameId.All(RuntimeCompat.IsAsciiDigit)) throw new ArgumentException("比赛编号必须是数字。", nameof(gameId));
        string html;
        try { html = await Read(SourcePages.BoxScore(gameId).AbsolutePath, cancellationToken); }
        catch (HttpRequestException ex) when (RuntimeCompat.StatusCode(ex) == HttpStatusCode.NotFound &&
            _knownGames.TryGetValue(gameId, out var waiting) && waiting.Status == GameStatus.Scheduled)
        { return BoxScoreSnapshot.Waiting(waiting, DateTimeOffset.UtcNow); }
        if (string.IsNullOrWhiteSpace(html) && _knownGames.TryGetValue(gameId, out var scheduled) && scheduled.Status == GameStatus.Scheduled)
            return BoxScoreSnapshot.Waiting(scheduled, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var cached = Volatile.Read(ref _statisticsCache);
        BoxScoreSnapshot snapshot;
        if (cached?.GameId == gameId && cached.Html == html) snapshot = cached.Snapshot with { FetchedAt = now };
        else
        {
            snapshot = new BoxScoreParser().Parse(html, gameId, now);
            Volatile.Write(ref _statisticsCache, new(gameId, html, snapshot));
        }
        if (!_loadRosters) return snapshot;
        return snapshot with { Away = await AddNumbers(snapshot.Away, cancellationToken), Home = await AddNumbers(snapshot.Home, cancellationToken) };
    }
    private async Task<TeamStatistics> AddNumbers(TeamStatistics team, CancellationToken ct)
    {
        if (team.RosterSlug is not { } slug || team.Players.Count == 0) return team;
        if (!_rosters.TryGetValue(slug, out var cache) || cache.RetryAfter <= DateTimeOffset.UtcNow)
        {
            try
            {
                var numbers = TeamRosterParser.Parse(await Read(SourcePages.TeamRoster(slug).AbsolutePath, ct));
                cache = new(DateTimeOffset.UtcNow.AddHours(6), numbers);
            }
            catch (Exception ex) when (ex is HttpRequestException or SourceFormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Optional metadata failure never discards valid scores or invents a number.
                cache = new(DateTimeOffset.UtcNow.AddMinutes(1), cache?.Numbers ?? new Dictionary<string, string>());
            }
            _rosters[slug] = cache;
        }
        ct.ThrowIfCancellationRequested();
        return team with { Players = team.Players.Select(p => cache.Numbers.TryGetValue(p.Id, out var number) ? p with { JerseyNumber = number } : p).ToArray() };
    }
}
