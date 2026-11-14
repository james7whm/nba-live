using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NbaLive;

internal sealed record ChatMessage(long Id, string Nickname, int Avatar, string Team, string Text, long Created);

// An endpoint must be set explicitly. Only loopback may use unencrypted HTTP.
internal sealed class ChatClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 512 * 1024 };
    private readonly Uri _endpoint;
    private string? _token;
    internal ChatClient(Uri endpoint) { _endpoint = endpoint; }
    internal static ChatClient? FromEnvironment()
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("NBALIVE_CHAT_ENDPOINT"), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) return null;
        return new ChatClient(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"));
    }
    private async Task<JsonDocument> Request(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        if (_token is not null) request.Headers.Authorization = new("Bearer", _token);
        if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"聊天室服务返回 {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    internal async Task SaveProfile(string nickname, int avatar, string team, CancellationToken ct)
    {
        using var doc = await Request(HttpMethod.Post, "v1/profile", new { nickname, avatar, team }, ct);
        if (_token is null) _token = doc.RootElement.GetProperty("token").GetString() ?? throw new IOException("聊天室身份响应异常");
    }
    internal async Task Send(string room, string text, string requestId, CancellationToken ct)
    {
        using var doc = await Request(HttpMethod.Post, "v1/messages", new { room, text, requestId }, ct);
    }
    internal async Task<IReadOnlyList<ChatMessage>> Read(string room, long after, CancellationToken ct)
    {
        using var doc = await Request(HttpMethod.Get, $"v1/messages?room={Uri.EscapeDataString(room)}&after={after}", null, ct);
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(x => new ChatMessage(
            x.GetProperty("id").GetInt64(), x.GetProperty("nickname").GetString() ?? "", x.GetProperty("avatar").GetInt32(),
            x.GetProperty("team").GetString() ?? "", x.GetProperty("text").GetString() ?? "", x.GetProperty("created").GetInt64())).ToArray();
    }
    public void Dispose() => _http.Dispose();
}
