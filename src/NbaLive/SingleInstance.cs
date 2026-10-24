using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace NbaLive;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName;
    private readonly bool _ownsMutex;
    internal bool IsFirst => _ownsMutex;
    internal event Action<bool>? ShowRequested;

    internal SingleInstance(bool demo)
    {
        var mode = demo ? ".Demo" : "";
        _mutex = new Mutex(true, @"Local\NbaLive.Desktop" + mode, out _ownsMutex);
        _pipeName = $"NbaLive-{WindowsIdentity.GetCurrent().User?.Value}-{Process.GetCurrentProcess().SessionId}{mode}";
    }
    internal void Listen() => _ = ListenLoop();
    private async Task ListenLoop()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
#if NETFRAMEWORK
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,PipeAccessRights.FullControl,System.Security.AccessControl.AccessControlType.Allow));
                using var server = new NamedPipeServerStream(_pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,0,security);
#else
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
                await server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                var packet = new byte[1];
                var count = await server.ReadAsync(packet, 0, packet.Length, _lifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token).ConfigureAwait(false);
                if (count == 1 && packet[0] is 1 or 2) ShowRequested?.Invoke(packet[0] == 2);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (TimeoutException) { }
            catch (IOException)
            {
                try { await Task.Delay(200, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
    internal async Task<bool> Signal(bool settings)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous
#if !NETFRAMEWORK
                | PipeOptions.CurrentUserOnly
#endif
                );
            await client.ConnectAsync(1500);
            await client.WriteAsync(new byte[] { settings ? (byte)2 : (byte)1 },0,1);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return false; }
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
        // Keep the cancellation source alive until the pending pipe operation observes cancellation.
    }
}
