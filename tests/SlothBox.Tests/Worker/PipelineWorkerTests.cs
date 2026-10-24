using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SlothBox.Core;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Server.Watching;
using SlothBox.Server.Worker;
using SlothBox.Tests.Watching;

namespace SlothBox.Tests.Worker;

public sealed class PipelineWorkerTests
{
    private const string Photo = @"D:\slothbox-test\Downloads\IMG_2041.jpg";
    private const string Second = @"D:\slothbox-test\Downloads\IMG_2042.jpg";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The worker over fakes, with its waits cut to milliseconds, and every event it publishes collected.
    private sealed class WorkerHarness : IAsyncDisposable
    {
        private static readonly WorkerTiming Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10));

        private readonly ServerEvents.Subscription _subscription;
        private readonly Task _collector;
        private PipelineWorker? _worker;

        public WorkerHarness()
        {
            Queue = new FakeQueue(Sequence);
            Pipeline = new ScriptedPipeline(Sequence);
            Database = new FakeDatabase(Sequence);
            Gemma = new FakeGemma(Sequence);
            _subscription = Events.Subscribe();
            _collector = Task.Run(async () =>
            {
                await foreach (var published in _subscription.Events.ReadAllAsync())
                    lock (Published)
                        Published.Add(published);
            });
        }

        public List<string> Sequence { get; } = [];
        public FakeQueue Queue { get; }
        public ScriptedPipeline Pipeline { get; }
        public FakeDatabase Database { get; }
        public FakeGemma Gemma { get; }
        public WorkerStats Stats { get; } = new();
        public WorkerState State { get; } = new();
        public ServerEvents Events { get; } = new();
        public FixedSettings Settings { get; } = new(new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" });
        public List<ServerEvent> Published { get; } = [];
        public InMemoryFinishedPaths Finished { get; } = new();
        public TidyBatches Tidy => _tidy ??= new TidyBatches(Events);
        private TidyBatches? _tidy;

        public async Task StartAsync()
        {
            _worker = new PipelineWorker(Queue, Pipeline, new RecoveryOnlyMover(Sequence), Database, Gemma, Settings,
                State, Stats, Events, NullLogger<PipelineWorker>.Instance, Fast, new FolderJobs(Finished, Tidy));
            await _worker.StartAsync(Ct);
        }

        public List<ServerEvent> EventsNamed(string name)
        {
            lock (Published)
                return Published.Where(e => e.Name == name).ToList();
        }

        public List<string> SequenceSnapshot()
        {
            lock (Sequence)
                return [.. Sequence];
        }

        public FakeJob Job(int index) => Queue.Snapshot()[index];

        public async ValueTask DisposeAsync()
        {
            if (_worker is not null)
            {
                await _worker.StopAsync(CancellationToken.None);
                _worker.Dispose();
            }
            _subscription.Dispose();
            await _collector;
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting until {what}.");
            await Task.Delay(10, Ct);
        }
    }

    private static PipelineResult.Filed Filed(FileStatus status = FileStatus.Filed, ReviewReason? reason = null,
        Guid? duplicateOf = null)
    {
        var now = DateTimeOffset.UtcNow;
        var file = new FileRecord(Guid.NewGuid(), "abc", FileOrigin.Folder, 1, "IMG_2041.jpg", Photo,
            @"D:\slothbox-test\out\Receipts\Bills and recharges\2026-10-20_Jio_599INR.jpg", FileKind.Receipt, 3,
            "Jio recharge", "UPI payment of 599 INR to Jio", "", now, 5, status, reason, duplicateOf, now, now);
        return new PipelineResult.Filed(file, "Bills and recharges", "Downloads", true);
    }

    // Spec 4.3: the watchers and the first sweep start after crash recovery and the requeue.
    [Fact]
    public async Task The_worker_tells_the_watchers_once_its_start_up_is_done()
    {
        await using var h = new WorkerHarness();
        h.Gemma.Ready = false;

        await h.StartAsync();

        await h.State.WaitUntilStartedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        h.SequenceSnapshot().Take(4).ShouldBe(["migrate", "recover", "requeue", "check"]);
    }

    // Spec 4.5: a copy-only original is still in its folder after its filing, so the sweep must pass it over.
    [Fact]
    public async Task A_file_left_in_place_by_its_job_becomes_a_finished_path()
    {
        using var temp = new TempFolder();
        var original = temp.Write("IMG_2043.jpg", "photo");
        await using var h = new WorkerHarness();
        h.Queue.Add(original);
        h.Pipeline.Answer = _ => Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job is done");
        await UntilAsync(() => h.Finished.Entries.ContainsKey(original), "the path is finished");
        h.Finished.Entries[original].FolderId.ShouldBe(1);
    }

    // Recorded while the job still holds its path in the queue: a sweep in between can neither queue it again nor miss
    // the record (spec 4.5).
    [Fact]
    public async Task A_finished_path_is_recorded_before_its_job_is_marked_done()
    {
        using var temp = new TempFolder();
        var original = temp.Write("IMG_2044.jpg", "photo");
        await using var h = new WorkerHarness();
        h.Queue.Add(original);
        h.Pipeline.Answer = _ => Filed();
        JobStatus? statusWhenRecorded = null;
        h.Finished.OnRecord = _ => statusWhenRecorded = h.Job(0).Status;

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done && statusWhenRecorded is not null, "the job is done");
        statusWhenRecorded.ShouldBe(JobStatus.Processing);
    }

    [Fact]
    public async Task A_tidied_file_waiting_for_its_retry_does_not_count_as_tidied_yet()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Tidy.Start(1, [Photo]);
        var answers = new Queue<PipelineResult>([new PipelineResult.NotReady(), Filed()]);
        h.Pipeline.Answer = _ => answers.Dequeue();

        await h.StartAsync();

        await UntilAsync(() => h.EventsNamed("folder.tidy-progress").Count == 2, "the tidy is done");
        h.Pipeline.RunCount.ShouldBe(2);
        h.EventsNamed("folder.tidy-progress").Select(e => e.Payload).ShouldBe(
            [new FolderTidyProgressEvent(1, 0, 1), new FolderTidyProgressEvent(1, 1, 1)]);
    }

    [Fact]
    public async Task On_start_the_worker_migrates_recovers_pending_actions_and_requeues_interrupted_jobs_before_working()
    {
        await using var h = new WorkerHarness();
        var interrupted = h.Queue.Add(Photo, JobStatus.Processing);
        h.Pipeline.Answer = _ => Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the interrupted job is done");
        h.SequenceSnapshot().Take(5).ShouldBe(["migrate", "recover", "requeue", "check", "claim"]);
        h.Pipeline.Runs.ShouldHaveSingleItem().Id.ShouldBe(interrupted.Id);
    }

    [Fact]
    public async Task On_start_without_both_models_the_worker_stays_paused_until_they_are_there()
    {
        await using var h = new WorkerHarness();
        h.Gemma.Ready = false;
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();

        await h.StartAsync();

        await UntilAsync(() => h.State.Paused, "the worker stays paused");
        h.State.Reason.ShouldBe(PauseReason.GemmaOffline);
        await Task.Delay(150, Ct);
        (h.Job(0).Status, h.Pipeline.RunCount).ShouldBe((JobStatus.Queued, 0));

        h.Gemma.Ready = true;

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job runs once both models are there");
        h.State.Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task Each_file_filed_counts_toward_todays_average_time()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        h.Pipeline.Answer = job => job.File.Path == Photo ? Filed() : new PipelineResult.Skipped("temporary");

        await h.StartAsync();

        await UntilAsync(() => h.Job(1).Status == JobStatus.Skipped, "both jobs are done");
        h.Stats.AverageSecondsToday.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task A_filed_result_ends_the_job_as_done_and_publishes_file_filed_and_queue_changed()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        var filed = Filed();
        h.Pipeline.Answer = _ => filed;

        await h.StartAsync();

        await UntilAsync(() => h.EventsNamed("file.filed").Count == 1, "file.filed is published");
        var job = h.Job(0);
        (job.Status, job.FileId).ShouldBe((JobStatus.Done, (Guid?)filed.File.Id));
        var item = h.EventsNamed("file.filed").Single().Payload.ShouldBeOfType<FileFiledEvent>();
        (item.FileId, item.Status, item.Type, item.Category, item.Title, item.NewName, item.Folder, item.Origin,
                item.FolderLabel, item.ThumbUrl, item.CanUndo)
            .ShouldBe((filed.File.Id, FileStatus.Filed, FileKind.Receipt, "Bills and recharges", "Jio recharge",
                "2026-10-20_Jio_599INR.jpg", @"D:\slothbox-test\out\Receipts\Bills and recharges", FileOrigin.Folder,
                "Downloads", $"/api/files/{filed.File.Id}/thumb", true));
        await UntilAsync(() => h.EventsNamed("queue.changed").LastOrDefault()?.Payload is QueueChangedEvent(0, 0),
            "queue.changed reports an empty queue");
    }

    [Fact]
    public async Task A_review_result_publishes_file_review_with_its_reason_and_ends_the_job_as_review()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed(FileStatus.Review, ReviewReason.MissingDate);

        await h.StartAsync();

        await UntilAsync(() => h.EventsNamed("file.review").Count == 1, "file.review is published");
        h.Job(0).Status.ShouldBe(JobStatus.Review);
        h.EventsNamed("file.review").Single().Payload.ShouldBeOfType<FileReviewEvent>().Reason
            .ShouldBe(ReviewReason.MissingDate);
    }

    [Fact]
    public async Task A_duplicate_result_publishes_file_duplicate_with_the_file_it_repeats()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        var original = Guid.NewGuid();
        h.Pipeline.Answer = _ => Filed(FileStatus.Duplicate, duplicateOf: original);

        await h.StartAsync();

        await UntilAsync(() => h.EventsNamed("file.duplicate").Count == 1, "file.duplicate is published");
        h.Job(0).Status.ShouldBe(JobStatus.Done);
        h.EventsNamed("file.duplicate").Single().Payload.ShouldBeOfType<FileDuplicateEvent>().DuplicateOf
            .ShouldBe(original);
    }

    [Fact]
    public async Task A_skipped_result_ends_the_job_as_skipped()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => new PipelineResult.Skipped("temporary");

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Skipped, "the job is skipped");
        h.Job(0).Error.ShouldBe("temporary");
        h.EventsNamed("file.filed").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_move_ends_the_job_as_failed_with_its_error_and_the_worker_carries_on()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        var fileId = Guid.NewGuid();
        h.Pipeline.Answer = job => job.File.Path == Photo
            ? new PipelineResult.Failed(fileId, "A file already exists at the destination.", MoveFailure.DestinationTaken)
            : Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(1).Status == JobStatus.Done, "the second job is done");
        var failed = h.Job(0);
        (failed.Status, failed.FileId, failed.Error).ShouldBe((JobStatus.Failed, (Guid?)fileId,
            (string?)"A file already exists at the destination."));
        h.State.Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task A_full_drive_pauses_the_worker_until_the_user_resumes_it()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        h.Pipeline.Answer = job => job.File.Path == Photo
            ? new PipelineResult.Failed(Guid.NewGuid(), "There is not enough space on the disk.", MoveFailure.DriveFull)
            : Filed();

        await h.StartAsync();

        await UntilAsync(() => h.EventsNamed("worker.changed").LastOrDefault()?.Payload is
            WorkerChanged(true, PauseReason.DriveFull), "the worker announces its pause");
        h.State.Reason.ShouldBe(PauseReason.DriveFull);
        await Task.Delay(150, Ct);
        h.Job(1).Status.ShouldBe(JobStatus.Queued);

        h.State.Resume();

        await UntilAsync(() => h.Job(1).Status == JobStatus.Done, "the second job is done after resuming");
        await UntilAsync(() => h.EventsNamed("worker.changed").LastOrDefault()?.Payload is WorkerChanged(false, null),
            "the worker announces it runs again");
    }

    [Fact]
    public async Task Gemma_offline_returns_the_job_to_the_queue_and_the_worker_resumes_once_Gemma_is_back()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        var attempts = 0;
        h.Pipeline.Answer = _ =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
                return Filed();
            // Ollama goes away mid-job.
            h.Gemma.Ready = false;
            return new PipelineResult.Offline("No connection could be made to 127.0.0.1:11436.");
        };

        await h.StartAsync();

        await UntilAsync(() => h.State.Paused, "the worker pauses itself");
        h.State.Reason.ShouldBe(PauseReason.GemmaOffline);
        await UntilAsync(() => h.SequenceSnapshot().Count(step => step == "check") >= 3, "the worker keeps checking");
        (h.State.Paused, h.Job(0).Status, attempts).ShouldBe((true, JobStatus.Queued, 1));

        h.Gemma.Ready = true;

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job is done once Gemma is back");
        await UntilAsync(() => !h.State.Paused, "the worker resumes by itself");
        await UntilAsync(() => h.EventsNamed("worker.changed").LastOrDefault()?.Payload is WorkerChanged(false, null),
            "the worker announces it runs again");
    }

    [Fact]
    public async Task A_users_pause_during_a_job_that_ends_offline_stays_a_users_pause()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Pipeline.Answer = job =>
        {
            if (job.File.Path != Photo)
                return Filed();
            started.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(5));
            return new PipelineResult.Offline("No connection could be made to 127.0.0.1:11436.");
        };

        await h.StartAsync();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        h.State.Pause(PauseReason.User);
        release.SetResult();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Queued, "the job is back in the queue");
        await Task.Delay(200, Ct);
        (h.State.Paused, h.State.Reason).ShouldBe((true, (PauseReason?)PauseReason.User));
        h.Pipeline.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_users_pause_during_the_offline_wait_is_not_lifted_by_the_workers_re_check()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ =>
        {
            h.Gemma.Ready = false;
            return new PipelineResult.Offline("No connection could be made to 127.0.0.1:11436.");
        };

        await h.StartAsync();

        await UntilAsync(() => h.State.Paused, "the worker pauses itself");
        h.State.Pause(PauseReason.User);
        h.Gemma.Ready = true;

        await Task.Delay(200, Ct);
        (h.State.Paused, h.State.Reason).ShouldBe((true, (PauseReason?)PauseReason.User));
        h.Pipeline.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task After_Gemma_is_back_a_full_drive_still_pauses_the_worker()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        var attempts = 0;
        h.Pipeline.Answer = _ =>
        {
            switch (Interlocked.Increment(ref attempts))
            {
                case 1:
                    h.Gemma.Ready = false;
                    return new PipelineResult.Offline("No connection could be made to 127.0.0.1:11436.");
                case 2:
                    return new PipelineResult.Failed(Guid.NewGuid(), "There is not enough space on the disk.",
                        MoveFailure.DriveFull);
                default:
                    return Filed();
            }
        };

        await h.StartAsync();

        await UntilAsync(() => h.State.Reason == PauseReason.GemmaOffline, "the worker pauses for Gemma");
        h.Gemma.Ready = true;

        await UntilAsync(() => h.State.Reason == PauseReason.DriveFull, "the next file finds the drive full");
        await Task.Delay(200, Ct);
        (h.State.Paused, h.State.Reason).ShouldBe((true, (PauseReason?)PauseReason.DriveFull));
        h.Job(1).Status.ShouldBe(JobStatus.Queued);
    }

    [Fact]
    public async Task A_stray_cancellation_from_the_pipeline_fails_the_job_and_the_worker_carries_on()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        h.Pipeline.Answer = job => job.File.Path == Photo
            ? throw new OperationCanceledException("The Gemma call timed out.")
            : Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(1).Status == JobStatus.Done, "the second job is done");
        (h.Job(0).Status, h.Job(0).Error).ShouldBe((JobStatus.Failed, (string?)"The Gemma call timed out."));
    }

    [Fact]
    public async Task An_error_while_recording_an_outcome_does_not_stop_the_worker()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();
        h.Queue.CompleteFaults.Enqueue(new InvalidOperationException("The connection was closed mid-command."));

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job is done on the second attempt");
        h.Pipeline.RunCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_user_pause_holds_the_queue_and_resume_lets_it_run()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();
        h.State.Pause(PauseReason.User);

        await h.StartAsync();

        await Task.Delay(150, Ct);
        h.Job(0).Status.ShouldBe(JobStatus.Queued);

        h.State.Resume();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job runs after resuming");
    }

    [Fact]
    public async Task A_locked_file_is_tried_again_shortly()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        var attempts = 0;
        h.Pipeline.Answer = _ => Interlocked.Increment(ref attempts) == 1 ? new PipelineResult.NotReady() : Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Queue.Snapshot().Count == 2 && h.Job(1).Status == JobStatus.Done,
            "the file is queued again and filed");
        h.Job(0).Status.ShouldBe(JobStatus.Skipped);
        h.Job(1).File.Path.ShouldBe(Photo);
    }

    [Fact]
    public async Task A_file_still_locked_after_the_retry_window_is_left_for_the_next_sweep()
    {
        await using var h = new WorkerHarness();
        h.Settings.Current = h.Settings.Current with { Pipeline = new PipelineSettings(LockRetryMinutes: 0) };
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => new PipelineResult.NotReady();

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Skipped, "the job is skipped");
        await Task.Delay(150, Ct);
        h.Queue.Snapshot().Count.ShouldBe(1);
        h.Job(0).Error.ShouldNotBeNull().ShouldContain("sweep");
    }

    [Fact]
    public async Task An_unexpected_error_fails_the_job_and_the_worker_carries_on()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Queue.Add(Second);
        h.Pipeline.Answer = job => job.File.Path == Photo
            ? throw new InvalidOperationException("No category row for Receipt files.")
            : Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(1).Status == JobStatus.Done, "the second job is done");
        (h.Job(0).Status, h.Job(0).Error).ShouldBe((JobStatus.Failed, (string?)"No category row for Receipt files."));
    }

    [Fact]
    public async Task While_setup_is_not_complete_the_worker_waits()
    {
        await using var h = new WorkerHarness();
        h.Settings.Current = new SlothBoxSettings();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();

        await h.StartAsync();

        await Task.Delay(150, Ct);
        h.Job(0).Status.ShouldBe(JobStatus.Queued);
        h.SequenceSnapshot().ShouldNotContain("recover");

        h.Settings.Current = new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" };

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job runs once setup is complete");
    }

    [Fact]
    public async Task A_database_outage_holds_the_worker_and_work_continues_when_it_is_back()
    {
        await using var h = new WorkerHarness();
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();
        h.Queue.ClaimFaults.Enqueue(new NpgsqlException("The database is offline."));

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job is done once the database is back");
        h.SequenceSnapshot().Count(step => step == "requeue").ShouldBe(2);
    }

    [Fact]
    public async Task The_database_is_retried_until_it_answers()
    {
        await using var h = new WorkerHarness();
        h.Database.Failures.Enqueue(new NpgsqlException("Connection refused."));
        h.Database.Failures.Enqueue(new NpgsqlException("Connection refused."));
        h.Queue.Add(Photo);
        h.Pipeline.Answer = _ => Filed();

        await h.StartAsync();

        await UntilAsync(() => h.Job(0).Status == JobStatus.Done, "the job is done once the database answers");
        h.SequenceSnapshot().Count(step => step == "migrate").ShouldBe(3);
    }
}
