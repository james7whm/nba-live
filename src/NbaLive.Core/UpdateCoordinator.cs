using System.Text.Json;

namespace NbaLive.Core;

public sealed record UpdateState(UpdateRelease? Release, string Message, bool Checking = false, bool Downloading = false, double Progress = 0);
public sealed record UpdateCache(DateTimeOffset LastAttempt, UpdateRelease? Release);
public sealed class UpdateCoordinator : IDisposable
{
    private readonly IUpdateSource _source;
    private readonly UpdateVersion _version;
    private readonly string _variant, _cachePath;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _download;
    private bool _disposed;
    private DateTimeOffset _lastAttempt;
    public UpdateState State { get; private set; } = new(null, "尚未检查更新");
    public event Action<UpdateState>? Changed;
    public bool Automatic { get; set; } = true;
    public string SkippedVersion { get; set; } = "";
    public bool HasNotice => State.Release is { } r && r.Version != SkippedVersion;
    public UpdateCoordinator(IUpdateSource source, UpdateVersion version, string variant, string directory)
    {
        _source = source; _version = version; _variant = variant; _cachePath = Path.Combine(directory, "update-cache.json");
        try
        {
            if (File.Exists(_cachePath))
            {
                var cache = JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(_cachePath));
                if (cache is not null)
                {
                    _lastAttempt = cache.LastAttempt > DateTimeOffset.UtcNow ? default : cache.LastAttempt;
                    if (cache.Release is { } r && UpdateVersion.TryParse(r.Version, out var v) && v.CompareTo(version) > 0) State = new(r, $"发现新版本 v{r.Version}（缓存，下载前会校验）");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public async Task Schedule()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _life.Token);
            while (!_life.IsCancellationRequested)
            {
                if (Automatic && DateTimeOffset.UtcNow - _lastAttempt >= TimeSpan.FromHours(24)) await Check();
                await Task.Delay(TimeSpan.FromMinutes(1), _life.Token);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
    }
    public async Task Check()
    {
        if (_disposed || !await _operation.WaitAsync(0)) return;
        try
        {
            Publish(State with { Checking = true, Message = "正在检查更新…" });
            var result = await _source.Check(_version, _variant, _life.Token);
            if (_disposed) return;
            _lastAttempt = DateTimeOffset.UtcNow;
            Publish(new(result.Failed ? State.Release : result.Release, result.Message));
            try { Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!); File.WriteAllText(_cachePath + ".tmp", JsonSerializer.Serialize(new UpdateCache(_lastAttempt, State.Release))); RuntimeCompat.ReplaceFile(_cachePath + ".tmp", _cachePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or SourceFormatException or JsonException)
        { Publish(State with { Checking = false, Message = "暂时无法检查更新，请稍后重试" }); }
        finally { _operation.Release(); }
    }
    public async Task<string?> Download(string workspace)
    {
        if (_disposed || State.Release is not { } release || !await _operation.WaitAsync(0)) return null;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        try
        {
            Publish(State with { Downloading = true, Progress = 0, Message = "正在下载，完成校验后将重启；关闭此窗口可取消" });
            Directory.CreateDirectory(workspace); var zip = Path.Combine(workspace, "package.zip");
            var progress = new Progress<double>(p => { if (!_disposed && State.Downloading) Publish(State with { Progress = p }); });
            var actual = await _source.Download(release, _variant, zip, progress, _download.Token);
            Publish(State with { Message = "下载已校验，正在检查程序文件…", Progress = 1 });
            var stage = Path.Combine(workspace, "next");
            await Task.Run(() => { _download.Token.ThrowIfCancellationRequested(); UpdatePackage.Extract(zip, stage, release.Version, _variant); _download.Token.ThrowIfCancellationRequested(); });
            Publish(State with { Release = actual, Downloading = false, Message = "校验完成，正在准备重启…" }); return stage;
        }
        catch (OperationCanceledException) { Publish(State with { Downloading = false, Message = "已取消更新，继续使用当前版本" }); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SourceFormatException or JsonException or System.Net.Http.HttpRequestException)
        { Publish(State with { Downloading = false, Message = "更新未完成，旧版保持可用：" + ex.Message }); return null; }
        finally { _download?.Dispose(); _download = null; _operation.Release(); }
    }
    public void CancelDownload() => _download?.Cancel();
    public void NotifySkipped() => Publish(State);
    public void SetMessage(string text) => Publish(State with { Message = text });
    private void Publish(UpdateState state) { if (_disposed) return; State = state; Changed?.Invoke(state); }
    public void Dispose() { if (_disposed) return; _disposed = true; _life.Cancel(); _download?.Cancel(); }
}
