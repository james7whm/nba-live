using System.Text.RegularExpressions;

namespace NbaLive.Core;

public static class OverlayProgress
{
    public static string Format(GameInfo? game)
    {
        if (game is null) return "";
        if (game.Status == GameStatus.Finished) return "已结束";
        if (game.Status != GameStatus.Live) return game.Progress;
        var progress = GamePageParser.FormatLiveProgress(game.Progress);
        progress = Regex.Replace(progress, @"^加([1-9]\d*)(?=剩|\s|$)", "加时$1");
        return string.IsNullOrWhiteSpace(progress) ? "进行中 · 时间未提供" : progress;
    }
}
