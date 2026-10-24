using SlothBox.Core;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Watching;

// Spec 4.3 steps 8 and 9, and 4.5: once the worker's start-up is done, the watchers follow the enabled folders and the
// Inbox, and the sweep runs at once, every SweepMinutes, and whenever it is asked for (a watcher overflowed or failed,
// or a folder was added, switched or located). Watcher reports go into the queue.
public sealed class WatchService(
    FolderWatchers watchers,
    FolderSweep sweep,
    SweepRequests requests,
    IJobQueue jobs,
    ISettingsStore settings,
    WorkerState worker,
    ILogger<WatchService> log) : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reports = QueueReportsAsync(stoppingToken);
        try
        {
            await worker.WaitUntilStartedAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = TimeSpan.FromMinutes(settings.Current.Pipeline.SweepMinutes);
                try
                {
                    watchers.Sync(await sweep.TargetsAsync(stoppingToken));
                    var queued = await sweep.RunAsync(stoppingToken);
                    if (queued > 0)
                        log.LogInformation("The sweep queued {Count} file(s).", queued);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Most likely the database: files stay where they are and the next sweep finds them (spec 9).
                    log.LogWarning("The folder sweep failed: {Error}", e.Message);
                    wait = Retry;
                }
                await requests.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Spec 4.3 "Shutdown": the watchers stop first.
            watchers.Dispose();
            await reports;
        }
    }

    // A report that cannot be queued (the database is down) is lost, but its file is still there for the sweep.
    private async Task QueueReportsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var file in watchers.Found.ReadAllAsync(ct))
            {
                try
                {
                    await jobs.EnqueueAsync(file, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Could not queue {Path}: {Error}", file.Path, e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
