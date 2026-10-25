using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? value, out UpdateVersion version)
    {
        version = default;
        var match = Regex.Match(value ?? "", @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var a) || !int.TryParse(match.Groups[2].Value, out var b) || !int.TryParse(match.Groups[3].Value, out var c)) return false;
        version = new(a, b, c); return true;
    }
    public int CompareTo(UpdateVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record UpdateRelease(string Version, string Origin, string Page, string Notes, string Published,
    string AssetName, string Download, string Checksum, string Digest, long? Size);
public sealed record UpdateCheck(UpdateRelease? Release, string Message, bool Failed = false);
public interface IUpdateSource
{
    Task<UpdateCheck> Check(UpdateVersion current, string variant, CancellationToken ct);
    Task<UpdateRelease> Download(UpdateRelease release, string variant, string zip, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Anonymous, HTTPS-only release reads; no application or publisher credentials are used.</summary>
public sealed class UpdateSource : IUpdateSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string Etag, string Body)> _cache = [];
    public const string GiteePage = "https://gitee.com/james7whm/nba-live/releases";
    public const string GithubPage = "https://github.com/james7whm/nba-live/releases";
    private static string Api(string origin) => origin == "Gitee" ? "https://gitee.com/api/v5/repos/james7whm/nba-live/releases/" : "https://api.github.com/repos/james7whm/nba-live/releases/";
    public UpdateSource(HttpMessageHandler? handler = null)
    {
#if NETFRAMEWORK
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NbaLive-Desktop-Updates/1.0");
    }
    public static string AssetName(string version, string variant) => $"NbaLive-{version}" + (variant switch { "net48" => "-compatibility-preview-net48", "modern-lite" => "-lite", "modern" => "", _ => throw new ArgumentException("Unknown package variant", nameof(variant)) }) + "-win-x64.zip";
    public static bool TrustedDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host is "gitee.com" or "foruda.gitee.com" or "github.com" or "api.github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static string S(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Flag(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static UpdateRelease? Parse(string json, string origin, string variant)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Flag(root, "draft") || Flag(root, "prerelease") || !UpdateVersion.TryParse(S(root, "tag_name"), out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var name = AssetName(version.ToString(), variant);
        var zip = assets.EnumerateArray().Where(a => S(a, "name") == name).ToArray();
        var sha = assets.EnumerateArray().Where(a => S(a, "name") == name + ".sha256").ToArray();
        if (zip.Length != 1 || sha.Length != 1) return null;
        var download = S(zip[0], "browser_download_url"); var checksum = S(sha[0], "browser_download_url");
        var host = origin == "Gitee" ? "gitee.com" : "github.com";
        var prefix = $"https://{host}/james7whm/nba-live/releases/download/v{version}/";
        if (download != prefix + name || checksum != prefix + name + ".sha256") return null;
        long? size = null;
        if (zip[0].TryGetProperty("size", out var z) && z.ValueKind != JsonValueKind.Null)
        { if (z.ValueKind != JsonValueKind.Number || !z.TryGetInt64(out var n)) return null; size = n; }
        if (size is <= 0 or > 268435456) return null;
        return new(version.ToString(), origin, $"https://{host}/james7whm/nba-live/releases/tag/v{version}", S(root, "body"), S(root, "published_at") is { Length: > 0 } date ? date : S(root, "created_at"), name, download, checksum, S(zip[0], "digest"), size);
    }
    public async Task<UpdateCheck> Check(UpdateVersion current, string variant, CancellationToken ct)
    {
        var missing = false; var currentConfirmed = false;
        foreach (var origin in new[] { "Gitee", "GitHub" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await ReadText(Api(origin) + "latest", ct);
                var release = Parse(json, origin, variant);
                if (release is null) { missing = true; continue; }
                UpdateVersion.TryParse(release.Version, out var latest);
                if (latest.CompareTo(current) > 0) return new(release, $"发现新版本 v{latest} · {origin}");
                // A delayed domestic mirror must not hide a newer GitHub release.
                if (origin == "Gitee") { currentConfirmed = true; continue; }
                return new(null, "当前已是最新版本");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or SourceFormatException)
            { if (ct.IsCancellationRequested) throw; }
        }
        return currentConfirmed ? new(null, "当前已是最新版本（已检查国内源）") : new(null, missing ? "暂未提供适用的完整更新包，可查看发行版页面" : "暂时无法检查更新，请稍后重试", true);
    }
    public async Task<UpdateRelease> Download(UpdateRelease release, string variant, string zip, IProgress<double>? progress, CancellationToken ct)
    {
        if (!UpdateVersion.TryParse(release.Version, out _) || ParseCandidate(release, variant) is false) throw new SourceFormatException("更新附件信息无效");
        string? baselineHash = null;
        foreach (var origin in new[] { release.Origin, release.Origin == "Gitee" ? "GitHub" : "Gitee" })
        {
            try
            {
                var candidate = origin == release.Origin ? release : Parse(await ReadText(Api(origin) + "tags/v" + release.Version, ct), origin, variant);
                if (candidate is null || candidate.Version != release.Version) continue;
                var checksumText = await ReadText(candidate.Checksum, ct);
                var expected = ReadChecksum(checksumText, candidate.AssetName);
                if (baselineHash is not null && baselineHash != expected) throw new SourceFormatException("同一版本的镜像内容不一致");
                baselineHash = expected;
                if (candidate.Digest.Length > 0 && !string.Equals(candidate.Digest, "sha256:" + expected, StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("附件摘要与校验文件不一致");
                // A fallback must carry exactly the same artifact, not another build of the same tag.
                if (release.Digest.Length > 0 && !string.Equals(release.Digest, "sha256:" + expected, StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("镜像附件与原附件摘要不一致");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await Send(candidate.Download, deadline.Token);
                if (response.Content.Headers.ContentLength is > 268435456) throw new SourceFormatException("更新包过大");
                var totalSize = candidate.Size ?? response.Content.Headers.ContentLength;
                using var input = await response.Content.ReadAsStreamAsync(); using var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                using var hash = SHA256.Create(); var buffer = new byte[65536]; long count = 0;
                while (true)
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(30)); var read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token);
                    if (read == 0) break; count += read;
                    if (count > 268435456) throw new SourceFormatException("更新包过大");
                    hash.TransformBlock(buffer, 0, read, null, 0); await output.WriteAsync(buffer, 0, read, deadline.Token);
                    progress?.Report(totalSize is > 0 ? Math.Min(.99, (double)count / totalSize.Value) : -1);
                }
                hash.TransformFinalBlock([], 0, 0);
                var actual = BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
                if (actual != expected || candidate.Size is { } size && size != count) throw new SourceFormatException("更新包校验失败，已保留旧版");
                progress?.Report(1); return candidate;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or SourceFormatException)
            { if (ct.IsCancellationRequested) throw; }
        }
        throw new SourceFormatException("两个下载源均未完成验证，请稍后重试或手动下载");
    }
    private static bool ParseCandidate(UpdateRelease r, string variant) => r.AssetName == AssetName(r.Version, variant) &&
        new[] { "Gitee", "GitHub" }.Contains(r.Origin) && r.Download == (r.Origin == "Gitee" ? GiteePage : GithubPage) + "/download/v" + r.Version + "/" + r.AssetName && r.Checksum == r.Download + ".sha256";
    public static string ReadChecksum(string text, string name)
    {
        var lines = text.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length != 1) throw new SourceFormatException("校验文件格式无效");
        var match = Regex.Match(lines[0], @"^([a-fA-F0-9]{64})\s+\*?(?:\./)?(.+)$");
        if (!match.Success || match.Groups[2].Value != name) throw new SourceFormatException("校验文件与附件不符");
        return match.Groups[1].Value.ToLowerInvariant();
    }
    private async Task<HttpResponseMessage> Send(string url, CancellationToken ct, string? etag = null)
    {
        var uri = new Uri(url);
        for (var hop = 0; hop < 6; hop++)
        {
            if (!TrustedDownload(uri)) throw new SourceFormatException("更新地址不受信任");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            { var next = response.Headers.Location; response.Dispose(); if (next is null) throw new SourceFormatException("下载跳转地址缺失"); uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue; }
            if (response.StatusCode != HttpStatusCode.NotModified && !response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException("更新服务暂不可用"); }
            return response;
        }
        throw new SourceFormatException("下载跳转过多");
    }
    private async Task<string> ReadText(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        _cache.TryGetValue(url, out var cached);
        using var response = await Send(url, deadline.Token, cached.Etag);
        if (response.StatusCode == HttpStatusCode.NotModified) return cached.Body ?? throw new SourceFormatException("更新缓存缺失");
        using var stream = await response.Content.ReadAsStreamAsync(); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var n = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token); if (n == 0) break; if (output.Length + n > 1048576) throw new SourceFormatException("更新信息过大"); output.Write(buffer, 0, n); }
        var body = Encoding.UTF8.GetString(output.ToArray());
        _cache[url] = (response.Headers.ETag?.ToString() ?? "", body); return body;
    }
    public void Dispose() => _http.Dispose();
}
