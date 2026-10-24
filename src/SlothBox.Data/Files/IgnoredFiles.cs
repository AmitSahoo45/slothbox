using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Files;

// One row of Settings > Ignored files (spec 7.4.8): the name is the file's as SlothBox put it back.
public sealed record IgnoredFileView(string Sha256, string FileName, DateTimeOffset At, string Reason);

// Undone files SlothBox will not file again (spec 6.6), and Allow again.
public sealed class IgnoredFiles(IDbContextFactory<SlothBoxDbContext> contexts)
{
    // Newest first. Each hash is named by its newest undone file.
    public async Task<IReadOnlyList<IgnoredFileView>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = await db.IgnoredHashes.AsNoTracking().OrderByDescending(h => h.CreatedAt).ToListAsync(ct);
        var keys = hashes.ConvertAll(h => h.Sha256);
        var files = await db.Files.AsNoTracking().Where(f => keys.Contains(f.Sha256))
            .OrderByDescending(f => f.Status == FileStatus.Undone).ThenByDescending(f => f.UpdatedAt)
            .Select(f => new { f.Sha256, f.CurrentPath, f.OriginalName }).ToListAsync(ct);
        var names = files.DistinctBy(f => f.Sha256).ToDictionary(f => f.Sha256,
            f => Path.GetFileName(f.CurrentPath) is { Length: > 0 } name ? name : f.OriginalName);
        return hashes.ConvertAll(h => new IgnoredFileView(h.Sha256, names.GetValueOrDefault(h.Sha256, ""), h.CreatedAt,
            h.Reason));
    }

    // Allow again: the hash is no longer ignored, and the finished paths where its undone files sit are forgotten in
    // the same transaction. Otherwise the sweep would keep passing over the unchanged file and never queue it again
    // (spec 4.5). A path is forgotten only while it can still be that file: same size, and no file SlothBox took from
    // that path since the undo. False, with nothing changed, when the hash is not ignored.
    public async Task<bool> AllowAsync(string sha256, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.IgnoredHashes.Where(h => h.Sha256 == sha256).ExecuteDeleteAsync(ct) == 0)
            return false;
        // finished_paths.path is citext: the undone file's path matches whatever case the sweep recorded.
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM finished_paths fp
            USING files f
            WHERE f.sha256 = {sha256} AND f.status = 'undone'
              AND fp.path = f.current_path::citext AND fp.size_bytes = f.size_bytes
              AND NOT EXISTS (SELECT 1 FROM files g
                              WHERE g.original_path::citext = fp.path AND g.created_at > f.updated_at)
            """, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
