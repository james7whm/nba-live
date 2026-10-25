using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NbaLive.Core;

public sealed record UpdateManifest(string Product, string Version, string Variant, Dictionary<string, string> Files);
public static class UpdatePackage
{
    public const string ManifestName = "NbaLive.package.json";
    public static string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.StartsWith('/') || relative.Length > 200) throw new SourceFormatException("更新路径无效");
        foreach (var part in relative.Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) throw new SourceFormatException("更新路径无效");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new SourceFormatException("更新路径越界");
        var current = full;
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new SourceFormatException("更新路径包含链接");
            if (current.TrimEnd(Path.DirectorySeparatorChar).Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        return full;
    }
    public static UpdateManifest ReadManifest(string root, string? version = null, string? variant = null, bool verifyFiles = true)
    {
        var path = SafePath(root, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 2097152) throw new SourceFormatException("此安装包不支持一键升级，请使用完整新版发行包");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path)) ?? throw new SourceFormatException("更新清单缺失");
        if (manifest.Product != "NbaLive" || !UpdateVersion.TryParse(manifest.Version, out _) || manifest.Variant is not ("modern" or "modern-lite" or "net48") ||
            version is not null && manifest.Version != version || variant is not null && manifest.Variant != variant || manifest.Files is null || manifest.Files.Count is < 3 or > 10000)
            throw new SourceFormatException("安装包版本或类型不符");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (!names.Add(item.Key) || item.Key.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || item.Key.Split('/').Any(p => p.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || p.Equals("bookmarks.json", StringComparison.OrdinalIgnoreCase)) || !Regex.IsMatch(item.Value ?? "", "^[a-fA-F0-9]{64}$")) throw new SourceFormatException("更新清单无效");
            var file = SafePath(root, item.Key);
            if (verifyFiles && (!File.Exists(file) || !string.Equals(Hash(file), item.Value, StringComparison.OrdinalIgnoreCase))) throw new SourceFormatException("程序文件校验失败：" + item.Key);
        }
        var required = manifest.Variant != "net48" ? new[] { "NbaLive.exe", "NbaLive.dll", "NbaLive.Core.dll", "NbaLive.runtimeconfig.json", "NbaLive.deps.json" } : new[] { "NbaLive.exe", "NbaLive.Core.dll", "NbaLive.exe.config" };
        if (required.Any(r => !names.Contains(r))) throw new SourceFormatException("安装包缺少运行文件");
        return manifest;
    }
    public static UpdateManifest Extract(string zip, string destination, string version, string variant, Func<string, string?>? versionReader = null)
    {
        if (Directory.Exists(destination)) throw new SourceFormatException("更新暂存目录必须为空");
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is < 3 or > 10000) throw new SourceFormatException("压缩包文件数量异常");
        var roots = archive.Entries.Select(e => e.FullName.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length != 1 || string.IsNullOrEmpty(roots[0])) throw new SourceFormatException("更新包目录结构不符");
        SafePath(destination, roots[0]);
        long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new SourceFormatException("更新包包含链接");
            var name = entry.FullName.Substring(roots[0].Length).TrimStart('/');
            if (name.Length == 0) continue;
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { SafePath(destination, name.TrimEnd('/')); continue; }
            var target = SafePath(destination, name);
            if (!seen.Add(name) || entry.Length < 0 || (total += entry.Length) > 1073741824) throw new SourceFormatException("更新包内容异常");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = File.Create(target);
            var buffer = new byte[65536]; long written = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length); if (read == 0) break;
                written += read; if (written > entry.Length) throw new SourceFormatException("解压内容超过声明大小");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new SourceFormatException("解压内容不完整");
        }
        var manifest = ReadManifest(destination, version, variant);
        if (seen.Count != manifest.Files.Count + 1 || seen.Any(n => n != ManifestName && !manifest.Files.ContainsKey(n))) throw new SourceFormatException("更新包包含清单之外的文件");
        var productVersion = (versionReader ?? (p => FileVersionInfo.GetVersionInfo(p).ProductVersion))(Path.Combine(destination, "NbaLive.exe"));
        if (productVersion?.Split('+')[0] != version) throw new SourceFormatException("程序产品版本与发行版不符");
        return manifest;
    }
}

public sealed record UpdateJournal(string Target, string Stage, string Backup, string State, UpdateManifest Before, UpdateManifest After);
/// <summary>Only manifest-owned files are changed. The journal is durable before the first change.</summary>
public static class UpdateTransaction
{
    public static void Save(string path, UpdateJournal journal)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(journal)); RuntimeCompat.ReplaceFile(path + ".tmp", path);
    }
    public static UpdateJournal Apply(string target, string stage, string backup, string journalPath, Action<string>? afterWrite = null)
    {
        var before = UpdatePackage.ReadManifest(target); var after = UpdatePackage.ReadManifest(stage, variant: before.Variant);
        if (!UpdateVersion.TryParse(before.Version, out var old) || !UpdateVersion.TryParse(after.Version, out var next) || next.CompareTo(old) <= 0) throw new SourceFormatException("更新版本必须高于当前版本");
        if (Directory.Exists(backup)) throw new SourceFormatException("备份目录已存在");
        foreach (var name in after.Files.Keys)
        {
            var path = UpdatePackage.SafePath(target, name);
            if (File.Exists(path) && !before.Files.Keys.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new SourceFormatException("更新会覆盖自行添加的文件：" + name);
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && !Directory.Exists(parent)) { if (File.Exists(parent)) throw new IOException("更新目录被文件占用"); parent = Path.GetDirectoryName(parent); }
        }
        foreach (var name in before.Files.Keys.Concat(new[] { UpdatePackage.ManifestName }))
        {
            var source = UpdatePackage.SafePath(target, name); var copy = UpdatePackage.SafePath(backup, name);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(source, copy);
        }
        var journal = new UpdateJournal(Path.GetFullPath(target), Path.GetFullPath(stage), Path.GetFullPath(backup), "applying", before, after);
        Save(journalPath, journal);
        try
        {
            foreach (var name in after.Files.Keys.Concat(new[] { UpdatePackage.ManifestName }))
            {
                var destination = UpdatePackage.SafePath(target, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temp = destination + ".update-" + Guid.NewGuid().ToString("N");
                try { File.Copy(UpdatePackage.SafePath(stage, name), temp); RuntimeCompat.ReplaceFile(temp, destination); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                afterWrite?.Invoke(name);
            }
            foreach (var name in before.Files.Keys.Where(n => !after.Files.Keys.Contains(n, StringComparer.OrdinalIgnoreCase))) File.Delete(UpdatePackage.SafePath(target, name));
            UpdatePackage.ReadManifest(target, after.Version, after.Variant);
            journal = journal with { State = "awaiting-health" }; Save(journalPath, journal); return journal;
        }
        catch { Rollback(journalPath); throw; }
    }
    public static void Rollback(string journalPath)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journalPath)) ?? throw new SourceFormatException("恢复记录无效");
        if (journal.State is "complete" or "rolled-back") return;
        UpdatePackage.ReadManifest(journal.Backup, journal.Before.Version, journal.Before.Variant);
        foreach (var name in journal.Before.Files.Keys.Concat(new[] { UpdatePackage.ManifestName }))
        {
            var destination = UpdatePackage.SafePath(journal.Target, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(UpdatePackage.SafePath(journal.Backup, name), destination, true);
        }
        foreach (var name in journal.After.Files.Keys.Where(n => !journal.Before.Files.Keys.Contains(n, StringComparer.OrdinalIgnoreCase))) File.Delete(UpdatePackage.SafePath(journal.Target, name));
        Save(journalPath, journal with { State = "rolled-back" });
    }
}
