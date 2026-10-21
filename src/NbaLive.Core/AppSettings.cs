using System.Text.Json;

namespace NbaLive.Core;

public enum OverlayMode { ScoreOnly, OneEvent, TwoEvents }
public enum OverlayContent { Text, Players }
public sealed record FavoritePlayer(string Id, string Name);

public sealed record AppSettings
{
    public double Width { get; init; } = 360;
    public double FontSize { get; init; } = 12;
    public double BackgroundOpacity { get; init; } = .92;
    public int? X { get; init; }
    public int? Y { get; init; }
    public string HideHotkey { get; init; } = "Ctrl+Alt+H";
    public string LockHotkey { get; init; } = "Ctrl+Alt+L";
    public string? LastGameId { get; init; }
    public OverlayMode DisplayMode { get; init; } = OverlayMode.TwoEvents;
    public bool ImportantEventsOnly { get; init; }
    public string FavoriteTeam { get; init; } = "";
    public bool CapsuleEnabled { get; init; }
    public OverlayContent ContentMode { get; init; } = OverlayContent.Text;
    public FavoritePlayer[] FollowedPlayers { get; init; } = [];
    public AppSettings Validate() => this with
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 280, 480) : 360,
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 11, 16) : 12,
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, .2, 1) : .92,
        HideHotkey = Hotkey.TryParse(HideHotkey, out _) ? HideHotkey : "Ctrl+Alt+H",
        LockHotkey = Hotkey.TryParse(LockHotkey, out _) ? LockHotkey : "Ctrl+Alt+L",
        DisplayMode = Enum.IsDefined(DisplayMode) ? DisplayMode : OverlayMode.TwoEvents,
        ContentMode = Enum.IsDefined(ContentMode) ? ContentMode : OverlayContent.Text,
        FollowedPlayers = PlayerFavorites.Normalize(FollowedPlayers),
        FavoriteTeam = (FavoriteTeam ?? "").Trim() is var team && team.Length <= 40 ? team : ""
    };
}

public sealed class SettingsStore(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NbaLive");
    public string? Error { get; private set; }
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public AppSettings Load()
    {
        try { return File.Exists(FilePath) ? (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()).Validate() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Error = "配置读取失败，已使用默认设置。"; return new(); }
    }
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, true);
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "配置无法写入，当前设置仅在本次运行生效。"; return false; }
    }
}

public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        var tokens = (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;
        uint modifiers = 0;
        foreach (var token in tokens[..^1])
        {
            uint flag = token.ToLowerInvariant() switch { "ctrl" or "control" => 2, "alt" => 1, "shift" => 4, _ => 0 };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var key = tokens[^1].ToUpperInvariant();
        uint code;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) code = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) code = (uint)(111 + f);
        else return false;
        hotkey = new(modifiers, code);
        return true;
    }
}
