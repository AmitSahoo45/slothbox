namespace SlothBox.Server.Watching;

// Asks the watch service for a sweep now: a watcher overflowed, or a folder was added, switched on or located.
public sealed class SweepRequests
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sweep is already asked for.
        }
    }

    // True when a sweep was asked for before the wait ran out.
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
