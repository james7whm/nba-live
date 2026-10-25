using System.Text.Json;
using Microsoft.Win32;

namespace NbaLive.Core;

public sealed record DesktopRuntimeRequirement(string Name, Version Minimum)
{
    public static IReadOnlyList<DesktopRuntimeRequirement> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object || !options.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
            throw new SourceFormatException("轻量更新包缺少运行环境要求");
        var result = new List<DesktopRuntimeRequirement>();
        foreach (var framework in frameworks.EnumerateArray())
        {
            if (framework.ValueKind != JsonValueKind.Object || !framework.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() is not ("Microsoft.NETCore.App" or "Microsoft.WindowsDesktop.App") || !framework.TryGetProperty("version", out var value) ||
                value.ValueKind != JsonValueKind.String || !Version.TryParse(value.GetString(), out var version) || version.Build < 0)
                throw new SourceFormatException("轻量更新包的运行环境要求无效");
            result.Add(new(name.GetString()!, version));
        }
        if (result.Count != 2 || result.Select(r => r.Name).Distinct().Count() != 2) throw new SourceFormatException("轻量更新包缺少桌面运行环境要求");
        return result;
    }
    public static bool IsSatisfied(DesktopRuntimeRequirement required, IEnumerable<string> installed) => installed.Any(s =>
        Version.TryParse(s, out var version) && version.Major == required.Minimum.Major && version.Minor == required.Minimum.Minor && version >= required.Minimum);
    public static IReadOnlyList<string> InstalledVersions(string framework)
    {
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows()) return [];
#endif
        if (framework is not ("Microsoft.NETCore.App" or "Microsoft.WindowsDesktop.App")) return [];
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\" + framework);
        return key?.GetValueNames() ?? [];
    }
}
