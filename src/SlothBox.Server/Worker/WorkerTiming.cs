namespace SlothBox.Server.Worker;

// How long the worker waits between looks: for new jobs, for Gemma to come back (spec 9: every 30 s), for the
// database (spec 4.3 step 4), for a locked file (spec 5.1 step 2) and for setup to finish (spec 4.3 step 5).
public sealed record WorkerTiming(TimeSpan IdlePoll, TimeSpan OfflineRetry, TimeSpan DatabaseRetry,
    TimeSpan LockRetry, TimeSpan SetupPoll)
{
    public static readonly WorkerTiming Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
}
