using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class ResourceProbe
{
    static readonly Assembly Assembly = typeof(NbaLive.App).Assembly;
    static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    static async Task Pump() { await Task.Delay(500); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    static void Sample(string phase)
    {
        // Measurement only: production code never forces collection or trims the process working set.
        var managed = GC.GetTotalMemory(true); using var process = Process.GetCurrentProcess(); process.Refresh();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Phase = phase, ManagedMiB = Math.Round(managed / 1048576d, 2), WorkingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 2), PrivateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 2), Handles = process.HandleCount }));
    }
    internal static async Task Run()
    {
        var settings = new AppSettings { TextDelivery = TextDelivery.Latest };
        var source = (IGameSource)Activator.CreateInstance(Assembly.GetType("NbaLive.DemoSource")!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [false, false], null)!;
        var snapshot = await source.GetSnapshot("0", CancellationToken.None);
        var entries = Enumerable.Range(1, 1500).Select(i => snapshot.Entries[0] with { SourceId = "resource-" + i, Text = "资源测试：第 " + i + " 条演示文字消息", Order = i }).ToArray();
        var state = new SessionState(snapshot with { Entries = entries }, entries, "", false, false) { SelectedGame = snapshot.Game, HistoryRevision = 1 };
        var overlay = (Window)Activator.CreateInstance(Assembly.GetType("NbaLive.OverlayWindow")!, BindingFlags.NonPublic | BindingFlags.Instance, null, [settings], null)!;
        try
        {
            overlay.Show(); Call(overlay, "Render", state, true); await Pump(); Sample("text-1500");
            Call(overlay, "ToggleInlineHistory"); await Pump(); Sample("history-open");
            Call(overlay, "ToggleInlineHistory"); await Pump(); Sample("history-closed");
            var getLogo = typeof(NbaLive.TeamLogo).GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!;
            long decodedBytes = 0;
            foreach (var theme in new[] { AppTheme.Warm, AppTheme.Dark })
            {
                Assembly.GetType("NbaLive.ThemeManager")!.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [theme]);
                foreach (var team in TeamPalette.All)
                {
                    var image = (BitmapSource)getLogo.Invoke(null, [team.Name])!;
                    decodedBytes += (long)image.PixelWidth * image.PixelHeight * 4;
                }
            }
            await Pump(); Sample("all-logos-both-themes");
            Console.WriteLine("DecodedLogoBytes=" + decodedBytes);
        }
        finally { overlay.Close(); }
    }
}
