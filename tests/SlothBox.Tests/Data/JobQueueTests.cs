using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data;
using SlothBox.Data.Queue;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class JobQueueTests(PostgresFixture postgres)
{
    private const string Bill = @"D:\slothbox-test\watched\bill.pdf";
    private const string Photo = @"D:\slothbox-test\watched\photo.jpg";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IncomingFile Seen(string path, FileOrigin origin = FileOrigin.Folder, int? folderId = null) =>
        new(path, origin, folderId, DateTimeOffset.UtcNow);

    private static async Task<List<StoredJob>> JobsAsync(TestDatabase db)
    {
        await using var context = db.CreateDbContext();
        return await context.Jobs.AsNoTracking().OrderBy(job => job.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task The_same_path_from_the_watcher_and_the_sweep_becomes_one_job()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Seen(Bill), Ct);
        await queue.EnqueueAsync(Seen(@"d:\SLOTHBOX-TEST\Watched\BILL.PDF"), Ct);

        (await JobsAsync(db)).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Queued);
    }

    [Fact]
    public async Task Counts_report_the_waiting_and_processing_jobs()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var queue = new JobQueue(db);
        (await queue.CountsAsync(Ct)).ShouldBe(new QueueCounts(0, 0));
        await queue.EnqueueAsync(Seen(Bill), Ct);
        await queue.EnqueueAsync(Seen(Photo), Ct);
        var claimed = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();

        (await queue.CountsAsync(Ct)).ShouldBe(new QueueCounts(1, 1));

        await queue.CompleteAsync(claimed.Id, new JobOutcome(JobStatus.Done), Ct);
        (await queue.CountsAsync(Ct)).ShouldBe(new QueueCounts(1, 0));
    }

    [Fact]
    public async Task Concurrent_enqueues_of_one_path_make_one_job()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var queue = new JobQueue(db);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => queue.EnqueueAsync(Seen(Bill), Ct)));

        (await JobsAsync(db)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_path_that_is_processing_is_not_queued_again()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var queue = new JobQueue(db);
        await queue.EnqueueAsync(Seen(Bill), Ct);
        (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();

        await queue.EnqueueAsync(Seen(Bill), Ct);

        (await JobsAsync(db)).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Processing);
        (await queue.ClaimNextAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_finished_path_can_be_queued_again()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var queue = new JobQueue(db);
        await queue.EnqueueAsync(Seen(Bill), Ct);
        var first = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();
        await queue.CompleteAsync(first.Id, new JobOutcome(JobStatus.Done), Ct);

        await queue.EnqueueAsync(Seen(Bill), Ct);

        (await JobsAsync(db)).Select(job => job.Status).ShouldBe([JobStatus.Done, JobStatus.Queued]);
    }

    [Fact]
    public async Task Claims_take_the_oldest_queued_job_first_and_count_attempts()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await DataSeed.FolderAsync(db);
        var queue = new JobQueue(db);
        await queue.EnqueueAsync(Seen(Bill, FileOrigin.Folder, folderId), Ct);
        await queue.EnqueueAsync(Seen(Photo, FileOrigin.Phone), Ct);

        var first = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();
        var second = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();

        first.File.Path.ShouldBe(Bill);
        first.File.Origin.ShouldBe(FileOrigin.Folder);
        first.File.WatchedFolderId.ShouldBe(folderId);
        first.Status.ShouldBe(JobStatus.Processing);
        first.Attempts.ShouldBe(1);
        second.File.Path.ShouldBe(Photo);
        second.File.Origin.ShouldBe(FileOrigin.Phone);
        second.File.WatchedFolderId.ShouldBeNull();
        (await queue.ClaimNextAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task After_a_restart_processing_jobs_return_to_the_queue()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var crashed = new JobQueue(db);
        await crashed.EnqueueAsync(Seen(Bill), Ct);
        await crashed.EnqueueAsync(Seen(Photo), Ct);
        (await crashed.ClaimNextAsync(Ct)).ShouldNotBeNull();

        var restarted = new JobQueue(db);
        (await restarted.RequeueInterruptedAsync(Ct)).ShouldBe(1);

        var again = (await restarted.ClaimNextAsync(Ct)).ShouldNotBeNull();
        again.File.Path.ShouldBe(Bill);
        again.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Completing_a_job_records_its_outcome()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var fileId = await DataSeed.FileAsync(db);
        var queue = new JobQueue(db);
        await queue.EnqueueAsync(Seen(Bill), Ct);
        await queue.EnqueueAsync(Seen(Photo), Ct);
        var filed = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();
        var locked = (await queue.ClaimNextAsync(Ct)).ShouldNotBeNull();

        await queue.CompleteAsync(filed.Id, new JobOutcome(JobStatus.Done, fileId), Ct);
        await queue.CompleteAsync(locked.Id, new JobOutcome(JobStatus.Failed, Error: "still locked after 10 min"), Ct);

        var jobs = await JobsAsync(db);
        (jobs[0].Status, jobs[0].FileId, jobs[0].LastError).ShouldBe((JobStatus.Done, (Guid?)fileId, (string?)null));
        (jobs[1].Status, jobs[1].FileId, jobs[1].LastError)
            .ShouldBe((JobStatus.Failed, (Guid?)null, (string?)"still locked after 10 min"));
    }
}
