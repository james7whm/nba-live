namespace NbaLive.Core;

/// <summary>One HTTP request at a time, with queued live reads ahead of secondary data.</summary>
internal sealed class PriorityRequestGate
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _live = [], _other = [];
    private bool _busy;
    private sealed class Waiter
    {
        internal readonly TaskCompletionSource<IDisposable> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation;
    }
    private sealed class Lease(PriorityRequestGate owner) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(); }
    }
    internal Task<IDisposable> Enter(bool live, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_busy) { _busy = true; return Task.FromResult<IDisposable>(new Lease(this)); }
            var waiter = new Waiter();
            waiter.Cancellation = ct.Register(() => waiter.Completion.TrySetCanceled(ct));
            (live ? _live : _other).Enqueue(waiter);
            return waiter.Completion.Task;
        }
    }
    private void Release()
    {
        lock (_sync)
        {
            while (_live.Count > 0 || _other.Count > 0)
            {
                var waiter = (_live.Count > 0 ? _live : _other).Dequeue();
                waiter.Cancellation.Dispose();
                if (waiter.Completion.TrySetResult(new Lease(this))) return;
            }
            _busy = false;
        }
    }
}
