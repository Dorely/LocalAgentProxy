namespace LocalAgentProxy;

// Round-robin among applications with queued work; FIFO within each application.
public sealed class FairScheduler(int capacity = 32)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<Waiter>> _queues = [];
    private readonly Queue<string> _rotation = new();
    private bool _active;
    private int _waiting;
    public int Waiting { get { lock (_sync) return _waiting; } }
    public bool Active { get { lock (_sync) return _active; } }

    public async Task<IDisposable> AcquireAsync(string client, CancellationToken ct)
    {
        var waiter = new Waiter(ct);
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_waiting >= capacity) throw new ProxyException(429, "queue_full", "The generation queue is full.");
            if (!_queues.TryGetValue(client, out var queue))
            {
                _queues.Add(client, queue = new Queue<Waiter>());
                _rotation.Enqueue(client);
            }
            queue.Enqueue(waiter);
            _waiting++;
            Dispatch();
        }
        using var registration = ct.Register(() => waiter.Completion.TrySetCanceled(ct));
        return await waiter.Completion.Task;
    }
    private void Dispatch()
    {
        while (!_active && _rotation.TryDequeue(out var client))
        {
            var queue = _queues[client];
            var waiter = queue.Dequeue();
            _waiting--;
            if (queue.Count == 0) _queues.Remove(client); else _rotation.Enqueue(client);
            if (waiter.Token.IsCancellationRequested) { waiter.Completion.TrySetCanceled(waiter.Token); continue; }
            _active = true;
            if (!waiter.Completion.TrySetResult(new Lease(this))) _active = false;
        }
    }
    private void Release() { lock (_sync) { _active = false; Dispatch(); } }
    private sealed record Waiter(CancellationToken Token)
    {
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Lease(FairScheduler scheduler) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) scheduler.Release(); }
    }
}
