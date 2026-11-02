using Microsoft.EntityFrameworkCore;
using Npgsql;
using SlothBox.Core;
using SlothBox.Core.Folders;

namespace SlothBox.Data.Queue;

public sealed class JobQueue(IDbContextFactory<SlothBoxDbContext> contexts) : IJobQueue
{
    public async Task EnqueueAsync(IncomingFile file, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // ix_jobs_path_active allows one queued or processing job per path; a second enqueue does nothing.
        await InsertAsync(db.Database.ExecuteSqlAsync($"""
            INSERT INTO jobs (path, origin, watched_folder_id, status, attempts, created_at, updated_at)
            VALUES ({file.Path}, {DbValue.Of(file.Origin)}, {file.WatchedFolderId}, 'queued', 0, {now}, {now})
            ON CONFLICT (path) WHERE status IN ('queued','processing') DO NOTHING
            """, ct));
    }

    // One statement: no job while finished_paths has the path with this stamp, or while it has an active job.
    public async Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, FileStamp stamp, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var lastWrite = new DateTimeOffset(stamp.LastWriteUtc, TimeSpan.Zero);
        var added = await InsertAsync(db.Database.ExecuteSqlAsync($"""
            INSERT INTO jobs (path, origin, watched_folder_id, status, attempts, created_at, updated_at)
            SELECT {file.Path}, {DbValue.Of(file.Origin)}, {file.WatchedFolderId}::integer, 'queued', 0, {now}, {now}
            WHERE NOT EXISTS (SELECT 1 FROM finished_paths WHERE path = {file.Path}::citext
                AND size_bytes = {stamp.Size} AND last_write_at = {lastWrite})
            ON CONFLICT (path) WHERE status IN ('queued','processing') DO NOTHING
            """, ct));
        return added > 0;
    }

    // A worker retry or a sweep can still carry the id of a folder removed since: the insert fails the foreign key
    // on watched_folder_id, and there is nothing to queue for that folder.
    private static async Task<int> InsertAsync(Task<int> insert)
    {
        try
        {
            return await insert;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return 0;
        }
    }

    public async Task<Job?> ClaimNextAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.Jobs.FromSql($"""
            UPDATE jobs SET status = 'processing', attempts = attempts + 1, updated_at = {now}
            WHERE id = (SELECT id FROM jobs WHERE status = 'queued' ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        return claimed.Count == 0 ? null : ToJob(claimed[0]);
    }

    public async Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Jobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(set => set
            .SetProperty(job => job.Status, outcome.Status)
            .SetProperty(job => job.FileId, outcome.FileId)
            .SetProperty(job => job.LastError, outcome.Error)
            .SetProperty(job => job.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<int> RequeueInterruptedAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Jobs.Where(job => job.Status == JobStatus.Processing).ExecuteUpdateAsync(set => set
            .SetProperty(job => job.Status, JobStatus.Queued)
            .SetProperty(job => job.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<QueueCounts> CountsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var waiting = await db.Jobs.CountAsync(job => job.Status == JobStatus.Queued, ct);
        var processing = await db.Jobs.CountAsync(job => job.Status == JobStatus.Processing, ct);
        return new QueueCounts(waiting, processing);
    }

    private static Job ToJob(StoredJob job) =>
        new(job.Id, new IncomingFile(job.Path, job.Origin, job.WatchedFolderId, job.CreatedAt), job.Status,
            job.Attempts, job.LastError, job.FileId);
}
