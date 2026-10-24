using System.Diagnostics;
using Npgsql;
using SlothBox.Core;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Events;

namespace SlothBox.Server.Worker;

// The one worker (spec 5.7): claims jobs, runs the pipeline, records the outcome, publishes the UI's events and
// pauses itself when the drive is full or Gemma is offline (spec 9).
public sealed class PipelineWorker(
    IJobQueue jobs,
    IFilePipeline pipeline,
    ISafeMover mover,
    IDatabaseStartup database,
    IOllamaStatus gemma,
    ISettingsStore settings,
    WorkerState state,
    WorkerStats stats,
    ServerEvents events,
    ILogger<PipelineWorker> log,
    WorkerTiming? timing = null) : BackgroundService
{
    private readonly WorkerTiming _timing = timing ?? WorkerTiming.Default;

    // Locked files by path: when the lock was first seen, and the retries waiting for their turn.
    private readonly Dictionary<string, DateTimeOffset> _lockedSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTimeOffset Due, IncomingFile File)> _retries = [];

    // Set after a database outage: the job it interrupted is still marked processing.
    private bool _requeueNeeded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.Changed += PublishWorkerChanged;
        try
        {
            var started = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!started)
                    {
                        await StartUpAsync(stoppingToken);
                        started = true;
                    }
                    await StepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (NpgsqlException e)
                {
                    // Postgres is down: nothing moves until it is back (spec 9).
                    log.LogWarning("The database is not answering: {Error}", e.Message);
                    _requeueNeeded = started;
                    await Task.Delay(_timing.DatabaseRetry, stoppingToken);
                }
                catch (Exception e)
                {
                    // Nothing may stop the worker for good: log it, give whatever broke a moment, and carry on.
                    log.LogError(e, "The worker hit an unexpected error.");
                    _requeueNeeded = started;
                    await Task.Delay(_timing.DatabaseRetry, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.Changed -= PublishWorkerChanged;
        }
    }

    // Spec 4.3: the database first (retrying while it is down), then setup, then crash recovery and the jobs a
    // crash left behind, then Ollama and both models; only then the queue.
    private async Task StartUpAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await database.MigrateAsync(ct);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("The database is not ready: {Error}", e.Message);
                await Task.Delay(_timing.DatabaseRetry, ct);
            }
        }
        while (settings.Current.OutputRoot.Length == 0)
            await Task.Delay(_timing.SetupPoll, ct);
        foreach (var action in await mover.RecoverPendingAsync(ct))
            if (action.Error is not null)
                log.LogWarning("Action {ActionId} could not be recovered: {Error}", action.ActionId, action.Error);
        var requeued = await jobs.RequeueInterruptedAsync(ct);
        if (requeued > 0)
            log.LogInformation("{Count} interrupted job(s) returned to the queue.", requeued);
        // Step 7: without Ollama and both models the queue stays paused until they are there.
        if (!(await gemma.CheckAsync(ct)).Ready)
            state.Pause(PauseReason.GemmaOffline);
    }

    private async Task StepAsync(CancellationToken ct)
    {
        if (_requeueNeeded)
        {
            await jobs.RequeueInterruptedAsync(ct);
            _requeueNeeded = false;
        }
        if (state.Paused)
        {
            // A pause for Gemma being offline is re-checked by the worker itself (spec 9: every 30 s); the other
            // reasons wait for Resume.
            if (state.Reason != PauseReason.GemmaOffline)
            {
                await state.WaitForResumeAsync(null, ct);
                return;
            }
            // Still paused for Gemma after the wait: it ends once Ollama answers with both models pulled.
            if (await state.WaitForResumeAsync(_timing.OfflineRetry, ct) || state.Reason != PauseReason.GemmaOffline)
                return;
            if ((await gemma.CheckAsync(ct)).Ready)
                state.TryResume(PauseReason.GemmaOffline);
            return;
        }
        await EnqueueDueRetriesAsync(ct);
        if (await ProcessOneAsync(ct) is null)
            await Task.Delay(_timing.IdlePoll, ct);
    }

    // Claims the next job and runs it; null when the queue is empty.
    private async Task<PipelineResult?> ProcessOneAsync(CancellationToken ct)
    {
        var job = await jobs.ClaimNextAsync(ct);
        if (job is null)
            return null;
        PipelineResult result;
        var watch = Stopwatch.StartNew();
        try
        {
            result = await pipeline.RunAsync(job, ct);
            if (result is PipelineResult.Filed)
                stats.Record(watch.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not NpgsqlException)
        {
            // Anything else, a stray cancellation included, is this job's failure, not the worker's.
            log.LogError(e, "Job {JobId} for {Path} failed.", job.Id, job.File.Path);
            result = new PipelineResult.Failed(Guid.Empty, e.Message, null);
        }
        await FinishAsync(job, result, ct);
        return result;
    }

    private async Task FinishAsync(Job job, PipelineResult result, CancellationToken ct)
    {
        switch (result)
        {
            case PipelineResult.Filed filed:
                _lockedSince.Remove(job.File.Path);
                await jobs.CompleteAsync(job.Id, new JobOutcome(
                    filed.File.Status == FileStatus.Review ? JobStatus.Review : JobStatus.Done, filed.File.Id), ct);
                events.Publish(new ServerEvent(filed.File.Status switch
                {
                    FileStatus.Review => "file.review",
                    FileStatus.Duplicate => "file.duplicate",
                    _ => "file.filed"
                }, Activity(filed)));
                break;
            case PipelineResult.Skipped skipped:
                await jobs.CompleteAsync(job.Id, new JobOutcome(JobStatus.Skipped, Error: skipped.Reason), ct);
                break;
            case PipelineResult.NotReady:
                await DeferAsync(job, ct);
                break;
            case PipelineResult.Failed failed:
                _lockedSince.Remove(job.File.Path);
                await jobs.CompleteAsync(job.Id, new JobOutcome(JobStatus.Failed,
                    failed.FileId == Guid.Empty ? null : failed.FileId, failed.Error), ct);
                if (failed.Failure == MoveFailure.DriveFull)
                    state.Pause(PauseReason.DriveFull);
                break;
            case PipelineResult.Offline offline:
                // Nothing was written: the job goes back to the queue and waits for Gemma.
                await jobs.CompleteAsync(job.Id, new JobOutcome(JobStatus.Queued, Error: offline.Error), ct);
                state.Pause(PauseReason.GemmaOffline);
                break;
        }
        var counts = await jobs.CountsAsync(ct);
        events.Publish(new ServerEvent("queue.changed", new QueueChangedEvent(counts.Waiting, counts.Processing)));
    }

    // A locked or still-changing file is tried again after LockRetry, for up to LockRetryMinutes (spec 5.1 step 2);
    // after that it is left for the next sweep.
    private async Task DeferAsync(Job job, CancellationToken ct)
    {
        var path = job.File.Path;
        var now = DateTimeOffset.UtcNow;
        if (!_lockedSince.TryGetValue(path, out var since))
            _lockedSince[path] = since = now;
        var window = TimeSpan.FromMinutes(settings.Current.Pipeline.LockRetryMinutes);
        if (now - since >= window)
        {
            _lockedSince.Remove(path);
            await jobs.CompleteAsync(job.Id, new JobOutcome(JobStatus.Skipped,
                Error: $"Still locked after {window.TotalMinutes:0} minutes; left for the next sweep."), ct);
            return;
        }
        await jobs.CompleteAsync(job.Id, new JobOutcome(JobStatus.Skipped, Error: "Not ready yet; trying again shortly."),
            ct);
        _retries.Add((now + _timing.LockRetry, job.File));
    }

    private async Task EnqueueDueRetriesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var retry in _retries.Where(r => r.Due <= now).ToList())
        {
            await jobs.EnqueueAsync(retry.File with { DetectedAt = now }, ct);
            _retries.Remove(retry);
        }
    }

    private void PublishWorkerChanged() =>
        events.Publish(new ServerEvent("worker.changed", new WorkerChanged(state.Paused, state.Reason)));

    // The spec 8.3 activity item for a filed, reviewed or duplicate file.
    private static ActivityItemDto Activity(PipelineResult.Filed filed)
    {
        var file = filed.File;
        var thumb = DashboardViews.ThumbUrl(file.Id, file.CurrentPath);
        return file.Status switch
        {
            FileStatus.Review => new FileReviewEvent
            {
                FileId = file.Id, At = file.UpdatedAt, Status = file.Status, Type = file.Type,
                Category = filed.Category, Title = file.Title, NewName = Path.GetFileName(file.CurrentPath),
                Folder = Path.GetDirectoryName(file.CurrentPath) ?? "", Origin = file.Origin,
                FolderLabel = filed.FolderLabel, ThumbUrl = thumb, CanUndo = true,
                Reason = file.ReviewReason ?? ReviewReason.BadOutput
            },
            FileStatus.Duplicate => new FileDuplicateEvent
            {
                FileId = file.Id, At = file.UpdatedAt, Status = file.Status, Type = file.Type,
                Category = filed.Category, Title = file.Title, NewName = Path.GetFileName(file.CurrentPath),
                Folder = Path.GetDirectoryName(file.CurrentPath) ?? "", Origin = file.Origin,
                FolderLabel = filed.FolderLabel, ThumbUrl = thumb, CanUndo = true,
                DuplicateOf = file.DuplicateOf ?? Guid.Empty
            },
            _ => new FileFiledEvent
            {
                FileId = file.Id, At = file.UpdatedAt, Status = file.Status, Type = file.Type,
                Category = filed.Category, Title = file.Title, NewName = Path.GetFileName(file.CurrentPath),
                Folder = Path.GetDirectoryName(file.CurrentPath) ?? "", Origin = file.Origin,
                FolderLabel = filed.FolderLabel, ThumbUrl = thumb, CanUndo = true
            }
        };
    }
}
