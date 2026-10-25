using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NbaLive.Core;

internal static class UpdateTests
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    static string Scratch() { var p = Path.Combine(Path.GetTempPath(), "NbaLiveUpdateTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is SourceFormatException or IOException or InvalidDataException) { return; } throw new Exception("Expected rejection"); }
    static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    static string Json(string origin = "Gitee", string version = "1.2.2", string variant = "modern", bool preview = false, bool draft = false, bool missing = false, string digest = "")
    {
        var name = UpdateSource.AssetName(version, variant); var root = origin == "Gitee" ? UpdateSource.GiteePage : UpdateSource.GithubPage;
        var assets = new List<object> { new { name, browser_download_url = root + "/download/v" + version + "/" + name, digest } };
        if (!missing) assets.Add(new { name = name + ".sha256", browser_download_url = root + "/download/v" + version + "/" + name + ".sha256" });
        return JsonSerializer.Serialize(new { tag_name = "v" + version, prerelease = preview, draft, body = "更新说明", assets });
    }
    static UpdateManifest Package(string root, string version, string variant = "net48")
    {
        Directory.CreateDirectory(root); var files = new[] { "NbaLive.exe", "NbaLive.Core.dll", "NbaLive.exe.config" };
        if (variant == "modern") files = ["NbaLive.exe", "NbaLive.dll", "NbaLive.Core.dll", "NbaLive.runtimeconfig.json", "NbaLive.deps.json"];
        foreach (var file in files) File.WriteAllText(Path.Combine(root, file), version + file);
        var manifest = new UpdateManifest("NbaLive", version, variant, files.ToDictionary(f => f, f => UpdatePackage.Hash(Path.Combine(root, f))));
        File.WriteAllText(Path.Combine(root, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest)); return manifest;
    }
    static string Zip(string root, string directory, string? extra = null)
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(directory)) archive.CreateEntryFromFile(file, "NbaLive-test/" + Path.GetFileName(file));
        if (extra is not null) { var entry = archive.CreateEntry("NbaLive-test/" + extra); using var writer = new StreamWriter(entry.Open()); writer.Write("bad"); }
        return zip;
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); } }
    sealed class Stub : IUpdateSource
    {
        public int Calls; public TaskCompletionSource<UpdateCheck> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UpdateCheck> Check(UpdateVersion current, string variant, CancellationToken ct) { Calls++; return Completion.Task; }
        public Task<UpdateRelease> Download(UpdateRelease release, string variant, string zip, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Updates compare numeric versions and reject preview or malformed tags", () => Sync(() =>
        {
            Check(UpdateVersion.TryParse("v1.10.0", out var a) && UpdateVersion.TryParse("1.9.9", out var b) && a.CompareTo(b) > 0, "numeric order");
            foreach (var bad in new[] { "1.2", "1.2.3.4", "1.2.3-beta", "01.2.3", "1.2.3+build", "-1.0.0", "999999999999.0.0" }) Check(!UpdateVersion.TryParse(bad, out _), bad);
            var old = JsonSerializer.Deserialize<AppSettings>("{}")!; Check(old.CheckForUpdates && old.SkippedUpdateVersion == "", "old settings default");
            Check((old with { SkippedUpdateVersion = "v1.2.3" }).Validate().SkippedUpdateVersion == "1.2.3", "skip normalized");
        })),
        ("Updates match exact modern and compatibility assets without source archives", () => Sync(() =>
        {
            Check(UpdateSource.Parse(Json(), "Gitee", "modern")?.Version == "1.2.2", "modern");
            Check(UpdateSource.Parse(Json(variant: "net48"), "Gitee", "net48")?.AssetName.Contains("net48") == true, "compat");
            Check(UpdateSource.Parse(Json(variant: "net48"), "Gitee", "modern") is null, "no variant switch");
            Check(UpdateSource.Parse(Json(preview: true), "Gitee", "modern") is null && UpdateSource.Parse(Json(draft: true), "Gitee", "modern") is null && UpdateSource.Parse(Json(missing: true), "Gitee", "modern") is null, "complete stable packages only");
            Check(UpdateSource.Parse(Json().Replace("james7whm/nba-live", "attacker/nba-live"), "Gitee", "modern") is null, "fixed repository");
            Check(UpdateSource.Parse(Json().Replace("\"digest\":\"\"", "\"digest\":\"\",\"size\":\"invalid\""), "Gitee", "modern") is null, "invalid size rejected");
            Check(UpdateSource.Parse(Json().Replace("\"assets\":[", "\"assets\":[null,42,\"invalid\","), "Gitee", "modern")?.Version == "1.2.2", "malformed unrelated assets ignored");
        })),
        ("Updates prefer a newer domestic release and detect a delayed mirror", async () =>
        {
            var hosts = new List<string>(); using var source = new UpdateSource(new Handler(r => { hosts.Add(r.RequestUri!.Host); return Text(Json()); }));
            var current = new UpdateVersion(1, 2, 1); Check((await source.Check(current, "modern", default)).Release?.Origin == "Gitee" && hosts.Count == 1, "domestic priority");
            using var delayed = new UpdateSource(new Handler(r => Text(r.RequestUri!.Host == "gitee.com" ? Json(version: "1.2.1") : Json("GitHub", "1.10.0"))));
            Check((await delayed.Check(current, "modern", default)).Release?.Version == "1.10.0", "mirror lag does not hide newer release");
        }),
        ("Updates fall back on missing packages and keep successful checks when GitHub is offline", async () =>
        {
            using var source = new UpdateSource(new Handler(r => Text(r.RequestUri!.Host == "gitee.com" ? Json(missing: true) : Json("GitHub"))));
            Check((await source.Check(new(1, 2, 1), "modern", default)).Release?.Origin == "GitHub", "missing attachment fallback");
            using var offline = new UpdateSource(new Handler(r => r.RequestUri!.Host == "gitee.com" ? Text(Json(version: "1.2.1")) : new(HttpStatusCode.ServiceUnavailable)));
            var result = await offline.Check(new(1, 2, 1), "modern", default); Check(!result.Failed && result.Release is null, "domestic success remains success");
        }),
        ("Updates reuse ETags and do not turn total network failure into up-to-date", async () =>
        {
            var first = true; using var source = new UpdateSource(new Handler(r =>
            {
                if (!first) { Check(r.Headers.IfNoneMatch.Any(), "conditional request"); return new(HttpStatusCode.NotModified); }
                first = false; var response = Text(Json()); response.Headers.ETag = new("\"version1\""); return response;
            }));
            await source.Check(new(1, 2, 1), "modern", default); Check((await source.Check(new(1, 2, 1), "modern", default)).Release is not null, "304 cached");
            using var failed = new UpdateSource(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            Check((await failed.Check(new(1, 2, 1), "modern", default)).Failed, "failed != latest");
        }),
        ("Updates download only trusted HTTPS targets and verify checksum filenames", () => Sync(() =>
        {
            Check(UpdateSource.TrustedDownload(new("https://foruda.gitee.com/a.zip?token=temporary")), "official CDN");
            foreach (var bad in new[] { "http://gitee.com/a", "https://gitee.com.evil.test/a", "https://gitee.com:444/a", "https://me@gitee.com/a" }) Check(!UpdateSource.TrustedDownload(new(bad)), bad);
            var sha = new string('a', 64); Check(UpdateSource.ReadChecksum(sha + "  ./x.zip", "x.zip") == sha, "common checksum format");
            Reject(() => UpdateSource.ReadChecksum(sha + "  wrong.zip", "x.zip")); Reject(() => UpdateSource.ReadChecksum(sha + "  x.zip\n" + sha + "  x.zip", "x.zip"));
        })),
        ("Updates recover a bad domestic download from the same GitHub version", async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("verified package bytes"); var sha = RuntimeCompat.Sha256Hex(bytes).ToLowerInvariant(); var name = UpdateSource.AssetName("1.2.2", "modern");
            using var source = new UpdateSource(new Handler(r =>
            {
                var uri = r.RequestUri!;
                if (uri.Host == "api.github.com") { Check(uri.AbsolutePath.EndsWith("/tags/v1.2.2"), "same tag"); return Text(Json("GitHub", digest: "sha256:" + sha)); }
                if (uri.AbsolutePath.EndsWith(".sha256")) return Text(sha + "  " + name);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(uri.Host == "gitee.com" ? [1, 2, 3] : bytes) };
            }));
            var output = Path.Combine(Scratch(), "download.zip");
            var actual = await source.Download(UpdateSource.Parse(Json(), "Gitee", "modern")!, "modern", output, null, default);
            Check(actual.Origin == "GitHub" && UpdatePackage.Hash(output) == sha, "fallback verified");
        }),
        ("Updates reject inconsistent mirror rebuilds instead of mixing same-tag files", async () =>
        {
            var name = UpdateSource.AssetName("1.2.2", "modern");
            using var source = new UpdateSource(new Handler(r =>
            {
                if (r.RequestUri!.Host == "api.github.com") return Text(Json("GitHub"));
                if (r.RequestUri.AbsolutePath.EndsWith(".sha256")) return Text(new string(r.RequestUri.Host == "gitee.com" ? 'a' : 'b', 64) + "  " + name);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            }));
            try { await source.Download(UpdateSource.Parse(Json(), "Gitee", "modern")!, "modern", Path.Combine(Scratch(), "bad.zip"), null, default); throw new Exception("accepted mismatch"); } catch (SourceFormatException) { }
        }),
        ("Updates validate package manifests, extraction and product version", () => Sync(() =>
        {
            var root = Scratch(); var files = Path.Combine(root, "files"); Package(files, "1.2.2"); var zip = Zip(root, files);
            Check(UpdatePackage.Extract(zip, Path.Combine(root, "next"), "1.2.2", "net48", _ => "1.2.2").Files.Count == 3, "valid package");
            Reject(() => UpdatePackage.Extract(zip, Path.Combine(root, "wrong-version"), "1.2.2", "net48", _ => "1.2.1"));
            Reject(() => UpdatePackage.Extract(zip, Path.Combine(root, "wrong-variant"), "1.2.2", "modern", _ => "1.2.2"));
            File.WriteAllText(Path.Combine(files, "NbaLive.exe"), "corruption"); Reject(() => UpdatePackage.ReadManifest(files));
        })),
        ("Updates reject zip traversal, duplicate files and unlisted entries", () => Sync(() =>
        {
            var root = Scratch(); var files = Path.Combine(root, "files"); Package(files, "1.2.2");
            foreach (var extra in new[] { "../escape", "NbaLive.exe", "outside.txt", "data:stream", "NUL.txt", "x/../../escape", "../" })
                Reject(() => UpdatePackage.Extract(Zip(root, files, extra), Path.Combine(root, Guid.NewGuid().ToString("N")), "1.2.2", "net48", _ => "1.2.2"));
            Check(!File.Exists(Path.Combine(root, "escape")), "never escaped");
            var lyingZip = Zip(root, files); var bytes = File.ReadAllBytes(lyingZip);
            for (var i = 0; i < bytes.Length - 46; i++) if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { Array.Copy(BitConverter.GetBytes(1), 0, bytes, i + 24, 4); break; }
            File.WriteAllBytes(lyingZip, bytes);
            Reject(() => UpdatePackage.Extract(lyingZip, Path.Combine(root, "false-size"), "1.2.2", "net48", _ => "1.2.2"));
        })),
        ("Updates replace owned files and preserve user additions and config", () => Sync(() =>
        {
            var root = Scratch(); var target = Path.Combine(root, "app"); var stage = Path.Combine(root, "next"); Package(target, "1.2.1"); Package(stage, "1.2.2");
            File.WriteAllText(Path.Combine(target, "personal.txt"), "keep"); File.WriteAllText(Path.Combine(root, "settings.json"), "outside-config");
            var result = UpdateTransaction.Apply(target, stage, Path.Combine(root, "backup"), Path.Combine(root, "journal.json"));
            Check(result.State == "awaiting-health" && UpdatePackage.ReadManifest(target).Version == "1.2.2", "not committed before health");
            Check(File.ReadAllText(Path.Combine(target, "personal.txt")) == "keep" && File.ReadAllText(Path.Combine(root, "settings.json")) == "outside-config", "unowned preserved");
            UpdateTransaction.Rollback(Path.Combine(root, "journal.json")); Check(UpdatePackage.ReadManifest(target).Version == "1.2.1", "health failure rollback");
        })),
        ("Updates roll back partial writes and recover an interrupted journal", () => Sync(() =>
        {
            var root = Scratch(); var target = Path.Combine(root, "app"); var stage = Path.Combine(root, "next"); Package(target, "1.2.1"); Package(stage, "1.2.2"); var journal = Path.Combine(root, "journal.json");
            Reject(() => UpdateTransaction.Apply(target, stage, Path.Combine(root, "backup"), journal, _ => throw new IOException("injected failure")));
            Check(UpdatePackage.ReadManifest(target).Version == "1.2.1", "partial rollback");
            var saved = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journal))! with { State = "applying" }; UpdateTransaction.Save(journal, saved);
            File.WriteAllText(Path.Combine(target, "NbaLive.Core.dll"), "partial power-loss write"); UpdateTransaction.Rollback(journal);
            Check(UpdatePackage.ReadManifest(target).Version == "1.2.1", "interrupted transaction recovered");
        })),
        ("Updates refuse same-version updates, unowned collisions and locked program files", () => Sync(() =>
        {
            var root = Scratch(); var target = Path.Combine(root, "app"); var stage = Path.Combine(root, "next"); Package(target, "1.2.1"); Package(stage, "1.2.1");
            Reject(() => UpdateTransaction.Apply(target, stage, Path.Combine(root, "backup0"), Path.Combine(root, "j0.json")));
            var manifest = Package(stage, "1.2.2"); File.WriteAllText(Path.Combine(target, "new.txt"), "user"); File.WriteAllText(Path.Combine(stage, "new.txt"), "package");
            manifest.Files["new.txt"] = UpdatePackage.Hash(Path.Combine(stage, "new.txt")); File.WriteAllText(Path.Combine(stage, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest));
            Reject(() => UpdateTransaction.Apply(target, stage, Path.Combine(root, "backup1"), Path.Combine(root, "j1.json"))); Check(File.ReadAllText(Path.Combine(target, "new.txt")) == "user", "collision preserved");
            manifest.Files.Remove("new.txt"); File.WriteAllText(Path.Combine(stage, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest));
            using var locked = new FileStream(Path.Combine(target, "NbaLive.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
            Reject(() => UpdateTransaction.Apply(target, stage, Path.Combine(root, "backup2"), Path.Combine(root, "j2.json")));
        })),
        ("Updates prevent duplicate checks and retain cached notices without reinstalling current versions", async () =>
        {
            var stub = new Stub(); var root = Scratch(); using var coordinator = new UpdateCoordinator(stub, new(1, 2, 1), "modern", root);
            var first = coordinator.Check(); await coordinator.Check(); Check(stub.Calls == 1, "one request");
            stub.Completion.SetResult(new(UpdateSource.Parse(Json(), "Gitee", "modern"), "new")); await first;
            Check(coordinator.HasNotice, "notice"); coordinator.SkippedVersion = "1.2.2"; Check(!coordinator.HasNotice && coordinator.State.Release is not null, "skip hides badge not manual view");
            using var cached = new UpdateCoordinator(new Stub(), new(1, 2, 1), "modern", root); Check(cached.HasNotice, "cached notice");
            using var installed = new UpdateCoordinator(new Stub(), new(1, 2, 2), "modern", root); Check(installed.State.Release is null, "no self-update");
        }),
        ("Updates respect cancellation and cannot persist a failed check as an installation", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var source = new UpdateSource(new Handler(_ => throw new Exception("must not send")));
            try { await source.Check(new(1, 2, 1), "modern", cancellation.Token); throw new Exception("ignored cancellation"); } catch (OperationCanceledException) { }
            var stub = new Stub(); using var coordinator = new UpdateCoordinator(stub, new(1, 2, 1), "modern", Scratch());
            stub.Completion.SetResult(new(null, "offline", true)); await coordinator.Check();
            Check(coordinator.State.Release is null && await coordinator.Download(Scratch()) is null && !coordinator.State.Downloading, "no install on failure");
        })
    ];
}
