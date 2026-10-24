using Microsoft.EntityFrameworkCore;
using Npgsql;
using SlothBox.Core.Folders;

namespace SlothBox.Data.Folders;

// Spec 4.5: finished_paths. The path is citext, so a path recorded again in another case replaces its entry.
public sealed class FinishedPathStore(IDbContextFactory<SlothBoxDbContext> contexts) : IFinishedPaths
{
    public async Task RecordAsync(FinishedPath entry, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (entry.FolderId is { } folderId && !await db.WatchedFolders.AnyAsync(f => f.Id == folderId, ct))
            return;
        var lastWrite = new DateTimeOffset(entry.Stamp.LastWriteUtc, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO finished_paths (path, folder_id, size_bytes, last_write_at, finished_at)
                VALUES ({entry.Path}, {entry.FolderId}, {entry.Stamp.Size}, {lastWrite}, {now})
                ON CONFLICT (path) DO UPDATE SET folder_id = excluded.folder_id, size_bytes = excluded.size_bytes,
                    last_write_at = excluded.last_write_at, finished_at = excluded.finished_at
                """, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // The folder was removed between the check and the insert: there is nothing to remember it for.
        }
    }

    public async Task<IReadOnlyList<FinishedPath>> ListAsync(int? folderId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.FinishedPaths.AsNoTracking().Where(f => f.FolderId == folderId).ToListAsync(ct);
        return rows.ConvertAll(row =>
            new FinishedPath(row.Path, row.FolderId, new FileStamp(row.SizeBytes, row.LastWriteAt.UtcDateTime)));
    }

    public async Task ForgetAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var list = paths.ToList();
        await db.FinishedPaths.Where(f => list.Contains(f.Path)).ExecuteDeleteAsync(ct);
    }
}
