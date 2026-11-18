namespace StudentRecords.Api;
// All API requests and background journal writes participate. The backup worker
// closes admission, drains existing leases, and always reopens in its finally.
public sealed class MaintenanceGate
{
    private readonly object sync = new();
    private int active;
    private bool maintenance;
    private TaskCompletionSource? drained;
    public bool IsActive
    {
        get
        {
            lock (sync)
                return maintenance;
        }
    }

    public IDisposable? TryEnter()
    {
        lock (sync)
        {
            if (maintenance)
                return null;
            active++;
            return new Lease(this);
        }
    }

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (sync)
        {
            if (maintenance)
                throw new InvalidOperationException("MaintenanceAlreadyActive");
            maintenance = true;
            wait = active == 0 ? Task.CompletedTask : (drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        try
        {
            await wait.WaitAsync(cancellationToken);
        }
        catch
        {
            End();
            throw;
        }
    }

    public void End()
    {
        lock (sync)
        {
            maintenance = false;
            drained = null;
        }
    }

    private void Leave()
    {
        lock (sync)
        {
            if (--active == 0)
                drained?.TrySetResult();
        }
    }

    private sealed class Lease(MaintenanceGate owner) : IDisposable
    {
        private MaintenanceGate? gate = owner;
        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Leave();
    }
}
