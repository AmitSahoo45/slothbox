using SlothBox.Server.Api;

namespace SlothBox.Server.Worker;

// The worker's own event: it paused, or runs again.
public sealed record WorkerChanged(bool Paused, PauseReason? Reason);

// Paused by the user (spec 5.7) or by the worker itself (spec 9). Thread-safe; the worker waits on it.
public sealed class WorkerState
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _resumed = NewSource();
    private bool _paused;
    private PauseReason? _reason;

    public bool Paused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    public PauseReason? Reason
    {
        get
        {
            lock (_gate)
                return _reason;
        }
    }

    public event Action? Changed;

    // Set once the worker's start-up (spec 4.3 steps 4 to 7) is done; the watchers and the first sweep wait for it.
    public void MarkStarted() => _started.TrySetResult();

    public Task WaitUntilStartedAsync(CancellationToken ct) => _started.Task.WaitAsync(ct);

    private readonly TaskCompletionSource _started = NewSource();

    // A user's pause outranks the worker's own, and a full drive outranks an offline Gemma: a pause is never
    // downgraded, so the worker cannot lift a pause the user set.
    private static int Rank(PauseReason reason) => reason switch
    {
        PauseReason.User => 2,
        PauseReason.DriveFull => 1,
        _ => 0
    };

    public void Pause(PauseReason reason)
    {
        lock (_gate)
        {
            if (_paused && Rank(_reason!.Value) >= Rank(reason))
                return;
            _paused = true;
            _reason = reason;
        }
        Changed?.Invoke();
    }

    // Lifts the pause only while it is still for `reason` (the worker's own re-check). True when running afterwards.
    public bool TryResume(PauseReason reason)
    {
        TaskCompletionSource waiting;
        lock (_gate)
        {
            if (!_paused)
                return true;
            if (_reason != reason)
                return false;
            waiting = Lift();
        }
        waiting.TrySetResult();
        Changed?.Invoke();
        return true;
    }

    public void Resume()
    {
        TaskCompletionSource waiting;
        lock (_gate)
        {
            if (!_paused)
                return;
            waiting = Lift();
        }
        waiting.TrySetResult();
        Changed?.Invoke();
    }

    // Under the lock: clears the pause and hands back the source the waiters hold.
    private TaskCompletionSource Lift()
    {
        _paused = false;
        _reason = null;
        var waiting = _resumed;
        _resumed = NewSource();
        return waiting;
    }

    // True once Resume is called (or when not paused); false when `timeout` passes first (the worker's own re-check).
    public async Task<bool> WaitForResumeAsync(TimeSpan? timeout, CancellationToken ct)
    {
        Task resumed;
        lock (_gate)
        {
            if (!_paused)
                return true;
            resumed = _resumed.Task;
        }
        if (timeout is null)
        {
            await resumed.WaitAsync(ct);
            return true;
        }
        try
        {
            await resumed.WaitAsync(timeout.Value, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
