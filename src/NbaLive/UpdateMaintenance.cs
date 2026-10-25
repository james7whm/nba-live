using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NbaLive;

internal sealed record UpdateJob(string Target, string Version, string Variant, int ParentId, long ParentStart, bool Hidden, bool Demo, bool Preview);
internal static class UpdateMaintenance
{
#if NETFRAMEWORK
    internal const string Variant = "net48";
#elif LITE_PACKAGE
    internal const string Variant = "modern-lite";
#else
    internal const string Variant = "modern";
#endif
    internal static string CurrentVersion => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NbaLive", "Updates");
    private static string Identity(string target) => RuntimeCompat.Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(target).TrimEnd('\\', '/').ToUpperInvariant())).Substring(0, 24);
    private static string LockName(string target) => @"Local\NbaLive.Update." + Identity(target);
    private static string Pointer(string target) => Path.Combine(Root, "pending-" + Identity(target) + ".txt");
    internal static bool IsLocked()
    {
        using var mutex = new Mutex(false, LockName(AppContext.BaseDirectory));
        try { if (!mutex.WaitOne(0)) return true; mutex.ReleaseMutex(); return false; }
        catch (AbandonedMutexException) { mutex.ReleaseMutex(); return false; }
    }
    internal static string Workspace()
    { var path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static string Prepare(string workspace, string stage, bool hidden, bool demo, bool preview)
    {
        var target = Path.GetFullPath(AppContext.BaseDirectory);
        var before = UpdatePackage.ReadManifest(target, CurrentVersion, Variant);
        var after = UpdatePackage.ReadManifest(stage, variant: Variant);
#if LITE_PACKAGE
        var requirements = DesktopRuntimeRequirement.Read(Path.Combine(stage, "NbaLive.runtimeconfig.json"));
        if (!requirements.All(r => DesktopRuntimeRequirement.IsSatisfied(r, DesktopRuntimeRequirement.InstalledVersions(r.Name))))
            throw new SourceFormatException("新版需要更新 .NET 10 桌面运行环境。请使用在线安装包完成升级；当前程序尚未退出。");
#endif
        UpdateVersion.TryParse(before.Version, out var old); UpdateVersion.TryParse(after.Version, out var next);
        if (next.CompareTo(old) <= 0) throw new SourceFormatException("新版本必须高于当前版本");
        if (Path.GetFullPath(stage) != Path.Combine(Path.GetFullPath(workspace), "next")) throw new SourceFormatException("更新暂存路径不符");
        var beforeBytes = before.Files.Keys.Sum(n => new FileInfo(UpdatePackage.SafePath(target, n)).Length);
        var afterBytes = after.Files.Keys.Sum(n => new FileInfo(UpdatePackage.SafePath(stage, n)).Length);
        var workDrive = Path.GetPathRoot(Path.GetFullPath(workspace))!; var targetDrive = Path.GetPathRoot(target)!;
        var workRequired = beforeBytes * 2 + 64L * 1024 * 1024;
        if (string.Equals(workDrive, targetDrive, StringComparison.OrdinalIgnoreCase)) workRequired += afterBytes;
        if (new DriveInfo(workDrive).AvailableFreeSpace < workRequired || new DriveInfo(targetDrive).AvailableFreeSpace < afterBytes + 64L * 1024 * 1024)
            throw new IOException("磁盘空间不足，请腾出空间后重试；旧版尚未退出");
        var probe = Path.Combine(target, ".nbalive-write-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); } finally { if (File.Exists(probe)) File.Delete(probe); }
        var runner = Path.Combine(workspace, "runner");
        foreach (var name in before.Files.Keys.Concat(new[] { UpdatePackage.ManifestName }))
        {
            var destination = UpdatePackage.SafePath(runner, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(UpdatePackage.SafePath(target, name), destination);
        }
        using var self = Process.GetCurrentProcess();
        var job = new UpdateJob(target, after.Version, Variant, self.Id, self.StartTime.ToUniversalTime().Ticks, hidden, demo, preview);
        var jobPath = Path.Combine(workspace, "job.json"); File.WriteAllText(jobPath, JsonSerializer.Serialize(job)); return jobPath;
    }
    private static UpdateJob Read(string path)
    {
        var full = Path.GetFullPath(path); var work = Path.GetDirectoryName(full)!;
        var allowed = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!work.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(work) != Path.GetFullPath(Root) || Path.GetFileName(full) != "job.json" || new FileInfo(full).Length > 16384) throw new SourceFormatException("更新任务路径无效");
        var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(full)) ?? throw new SourceFormatException("更新任务无效");
        if (!Path.IsPathRooted(job.Target) || !UpdateVersion.TryParse(job.Version, out _) || job.Variant != Variant || job.Target.TrimEnd('\\', '/').Length < 4) throw new SourceFormatException("更新任务目标无效");
        UpdatePackage.SafePath(job.Target, UpdatePackage.ManifestName);
        return job;
    }
    internal static async Task StartWorker(string path)
    {
        var job = Read(path); var workspace = Path.GetDirectoryName(path)!;
        var exe = Path.Combine(workspace, "runner", "NbaLive.exe");
        using var worker = Process.Start(new ProcessStartInfo(exe, "--apply-update " + Quote(path)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! }) ?? throw new IOException("无法启动更新程序");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(Path.Combine(workspace, "ready"))) return;
            if (worker.HasExited) throw new IOException("更新程序未就绪：" + ReadResult(workspace));
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(workspace, "cancel"), "startup timeout"); throw new IOException("更新程序启动超时，继续使用旧版");
    }
    private static string ReadResult(string work) { var file = Path.Combine(work, "result.txt"); return File.Exists(file) ? File.ReadAllText(file) : "请稍后重试"; }
    internal static string Quote(string text)
    { if (text.Contains('"') || text.Contains('\n') || text.Contains('\r')) throw new ArgumentException("Invalid launch argument"); return "\"" + text + "\""; }
    private static Process Launch(UpdateJob job, string path, bool health)
    {
        var arguments = (job.Hidden ? "--start-hidden " : "") + (job.Demo ? "--demo " : job.Preview ? "--preview " : "") + (health ? "--update-health " + Quote(path) : "");
        return Process.Start(new ProcessStartInfo(Path.Combine(job.Target, "NbaLive.exe"), arguments) { UseShellExecute = false, WorkingDirectory = job.Target }) ?? throw new IOException("新版启动失败");
    }
    internal static void RunWorker(string path, bool recover)
    {
        string? workspace = null; UpdateJob? job = null;
        try
        {
            job = Read(path); workspace = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').Equals(Path.Combine(workspace, "runner"), StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("更新程序位置不符");
            using var mutex = new Mutex(false, LockName(job.Target));
            try { if (!mutex.WaitOne(0)) throw new IOException("另一个更新任务正在进行"); } catch (AbandonedMutexException) { }
            try
            {
                var journal = Path.Combine(workspace, "journal.json");
                if (recover)
                {
                    if (!File.Exists(journal)) throw new IOException("没有可恢复的记录");
                    // Never restore over a running application, including a manually started instance.
                    if (RunningAt(job.Target)) throw new IOException("请先退出当前目录中的应用再恢复");
                    UpdateTransaction.Rollback(journal); File.Delete(Pointer(job.Target));
                    File.WriteAllText(Path.Combine(workspace, "result.txt"), "已恢复旧版");
                }
                else
                {
                    using var parent = Process.GetProcessById(job.ParentId);
                    if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStart || !string.Equals(parent.MainModule?.FileName, Path.Combine(job.Target, "NbaLive.exe"), StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("原程序身份不符");
                    UpdatePackage.ReadManifest(job.Target, variant: Variant);
                    UpdatePackage.ReadManifest(Path.Combine(workspace, "next"), job.Version, Variant);
                    File.WriteAllText(Path.Combine(workspace, "ready"), "ready");
                    if (!parent.WaitForExit(30000) || File.Exists(Path.Combine(workspace, "cancel"))) throw new IOException("原程序未退出，更新已取消");
                    if (RunningAt(job.Target)) throw new IOException("该目录仍有程序运行，更新已取消");
                    File.WriteAllText(Pointer(job.Target), path);
                    var applied = UpdateTransaction.Apply(job.Target, Path.Combine(workspace, "next"), Path.Combine(workspace, "backup"), journal);
                    using var next = Launch(job, path, true); var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30) && !next.HasExited && !File.Exists(Path.Combine(workspace, "healthy"))) Thread.Sleep(100);
                    if (!File.Exists(Path.Combine(workspace, "healthy")) || next.HasExited)
                    {
                        if (!next.HasExited) { next.Kill(); next.WaitForExit(10000); }
                        UpdateTransaction.Rollback(journal);
                        File.WriteAllText(Path.Combine(workspace, "result.txt"), "新版未完成启动检查，已恢复旧版");
                    }
                    else
                    {
                        UpdateTransaction.Save(journal, applied with { State = "complete" });
                        File.Delete(Pointer(job.Target)); File.WriteAllText(Path.Combine(workspace, "result.txt"), "更新成功 v" + job.Version); return;
                    }
                }
            }
            finally { mutex.ReleaseMutex(); }
            Launch(job, path, false).Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SourceFormatException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (workspace is null) return;
            File.WriteAllText(Path.Combine(workspace, "result.txt"), "更新未完成：" + ex.Message);
            var journal = Path.Combine(workspace, "journal.json");
            if (job is not null && File.Exists(journal) && !RunningAt(job.Target))
            {
                try { UpdateTransaction.Rollback(journal); File.Delete(Pointer(job.Target)); Launch(job, path, false).Dispose(); }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or SourceFormatException or JsonException or System.ComponentModel.Win32Exception)
                { File.AppendAllText(Path.Combine(workspace, "result.txt"), "\n恢复待完成：" + recovery.Message); }
            }
        }
    }
    private static bool RunningAt(string root)
    {
        foreach (var process in Process.GetProcessesByName("NbaLive"))
            using (process) { try { if (string.Equals(process.MainModule?.FileName, Path.Combine(root, "NbaLive.exe"), StringComparison.OrdinalIgnoreCase)) return true; } catch (System.ComponentModel.Win32Exception) { } }
        return false;
    }
    internal static string? PendingRecovery()
    {
        var pointer = Pointer(AppContext.BaseDirectory); if (!File.Exists(pointer)) return null;
        var path = File.ReadAllText(pointer); var job = Read(path);
        if (!string.Equals(job.Target.TrimEnd('\\', '/'), AppContext.BaseDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("恢复目录不符");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "journal.json")));
        if (journal?.State is "complete" or "rolled-back") { File.Delete(pointer); return null; }
        return path;
    }
    internal static void Acknowledge(string path, bool visible)
    {
        var job = Read(path);
        if (!string.Equals(job.Target.TrimEnd('\\', '/'), AppContext.BaseDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || job.Version != CurrentVersion) throw new SourceFormatException("新版启动校验失败");
        UpdatePackage.ReadManifest(job.Target, CurrentVersion, Variant);
        if (visible == job.Hidden) throw new SourceFormatException("更新后窗口显示状态不符");
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "healthy"), JsonSerializer.Serialize(new { ProcessId = self.Id, Version = CurrentVersion, Hidden = !visible }));
    }
    internal static void Recover(string path)
    {
        Read(path); var exe = Path.Combine(Path.GetDirectoryName(path)!, "runner", "NbaLive.exe");
        Process.Start(new ProcessStartInfo(exe, "--recover-update " + Quote(path)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
    }
}
