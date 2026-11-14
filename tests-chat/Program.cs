using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NbaLive;

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var temp = Path.Combine(Path.GetTempPath(), "nba-chat-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var server = Path.GetFullPath(Path.Combine("chat-server", "server.py"));
var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true };
foreach (var arg in new[] { server, "--database", Path.Combine(temp, "chat.sqlite3"), "--port", port.ToString() }) start.ArgumentList.Add(arg);
using var process = Process.Start(start) ?? throw new Exception("Could not start local chat service");
try
{
    var address = new Uri($"http://127.0.0.1:{port}/");
    using var one = new ChatClient(address); using var two = new ChatClient(address);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    for (;;)
    {
        try { await one.Read("123", 0, deadline.Token); break; }
        catch (HttpRequestException) when (!deadline.IsCancellationRequested) { await Task.Delay(100, deadline.Token); }
    }
    await one.SaveProfile("路人", 0, "", deadline.Token);
    await two.SaveProfile("球迷", 1, "knicks", deadline.Token);
    var requestId = Guid.NewGuid().ToString("N");
    await one.Send("123", "你好", requestId, deadline.Token);
    await one.Send("123", "你好", requestId, deadline.Token); // A timed-out response may safely be retried.
    await two.Send("123", "好球 🏀😀[动:😀]", Guid.NewGuid().ToString("N"), deadline.Token);
    var messages = await one.Read("123", 0, deadline.Token);
    if (messages.Count != 2 || messages[0].Text != "你好" || messages[1].Text != "好球 🏀😀[动:😀]" || messages[0].Team != "" || (await two.Read("124", 0, deadline.Token)).Count != 0)
        throw new Exception("Chat messages or room isolation were incorrect");
    Console.WriteLine("PASS Two .NET clients exchange messages through local service; other room remains empty");
}
finally
{
    if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
    foreach (var file in new[] { "chat.sqlite3", "chat.sqlite3-wal", "chat.sqlite3-shm" }) File.Delete(Path.Combine(temp, file));
    Directory.Delete(temp);
}
