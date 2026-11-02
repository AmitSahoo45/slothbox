using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Core.Pipeline;
using SlothBox.Data.Folders;
using SlothBox.Data.Queue;
using SlothBox.Server.Events;
using SlothBox.Server.Worker;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Worker;

// The worker over the real queue, where jobs.watched_folder_id is a foreign key the fake queue does not have.
[Collection(PostgresCollection.Name)]
public sealed class WorkerQueueTests(PostgresFixture postgres)
{
    private const string Locked = @"D:\slothbox-test\watched\open-in-excel.xlsx";
    private const string Upload = @"D:\slothbox-test\out\Inbox\phone-20261031-101500-a1b2c3.jpg";

    private static readonly WorkerTiming Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The real queue, telling the test when the worker queues a path again.
    private sealed class WatchedQueue(JobQueue queue) : IJobQueue
    {
        public TaskCompletionSource Requeued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EnqueueAsync(IncomingFile file, CancellationToken ct)
        {
            try
            {
                await queue.EnqueueAsync(file, ct);
            }
            finally
            {
                Requeued.TrySetResult();
            }
        }

        public Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, FileStamp stamp, CancellationToken ct) =>
            queue.EnqueueUnlessFinishedAsync(file, stamp, ct);
        public Task<Job?> ClaimNextAsync(CancellationToken ct) => queue.ClaimNextAsync(ct);
        public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) =>
            queue.CompleteAsync(jobId, outcome, ct);
        public Task<int> RequeueInterruptedAsync(CancellationToken ct) => queue.RequeueInterruptedAsync(ct);
        public Task<QueueCounts> CountsAsync(CancellationToken ct) => queue.CountsAsync(ct);
    }

    // Removing a watched folder while one of its files is locked: the retry still carries the folder's id. Its
    // insert used to fail on the foreign key on every loop, read as a database outage, so no job ran again.
    [Fact]
    public async Task A_locked_files_retry_for_a_folder_removed_since_does_not_hold_up_the_queue()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await DataSeed.FolderAsync(db);
        var queue = new JobQueue(db);
        var watched = new WatchedQueue(queue);
        await queue.EnqueueAsync(new IncomingFile(Locked, FileOrigin.Folder, folderId, DateTimeOffset.UtcNow), Ct);
        var sequence = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folderRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ScriptedPipeline(sequence)
        {
            Answer = job =>
            {
                if (job.File.Path != Locked)
                    return new PipelineResult.Skipped("temporary");
                started.TrySetResult();
                folderRemoved.Task.Wait(TimeSpan.FromSeconds(5));
                return new PipelineResult.NotReady();
            }
        };
        using var worker = new PipelineWorker(watched, pipeline, new RecoveryOnlyMover(sequence),
            new FakeDatabase(sequence), new FakeGemma(sequence),
            new FixedSettings(new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" }), new WorkerState(),
            new WorkerStats(), new ServerEvents(), NullLogger<PipelineWorker>.Instance, Fast);
        await worker.StartAsync(Ct);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await new WatchedFolderRepository(db).RemoveAsync(folderId, Ct);
            folderRemoved.SetResult();
            await watched.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            await queue.EnqueueAsync(new IncomingFile(Upload, FileOrigin.Phone, null, DateTimeOffset.UtcNow), Ct);

            await UntilAsync(async () => (await StatusOfAsync(db, Upload)) == JobStatus.Skipped,
                "the next job runs");
            (await StatusOfAsync(db, Locked)).ShouldBe(JobStatus.Skipped);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // The newest job for the path.
    private static async Task<JobStatus?> StatusOfAsync(TestDatabase db, string path)
    {
        await using var context = db.CreateDbContext();
        return await context.Jobs.AsNoTracking().Where(job => job.Path == path).OrderByDescending(job => job.Id)
            .Select(job => (JobStatus?)job.Status).FirstOrDefaultAsync(Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting until {what}.");
            await Task.Delay(10, Ct);
        }
    }
}
