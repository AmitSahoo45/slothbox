using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Folders;

public sealed class WatchedFolderRepository(IDbContextFactory<SlothBoxDbContext> contexts) : IWatchedFolderRepository
{
    public async Task<IReadOnlyList<WatchedFolder>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.WatchedFolders.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct);
        return rows.ConvertAll(ToFolder);
    }

    // A folder with no id is added; one with an id is updated.
    public async Task<WatchedFolder> SaveAsync(WatchedFolder folder, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = folder.Id == 0 ? null : await db.WatchedFolders.SingleOrDefaultAsync(f => f.Id == folder.Id, ct);
        if (row is null)
            db.WatchedFolders.Add(row = new StoredWatchedFolder { AddedAt = DateTimeOffset.UtcNow });
        row.Path = folder.Path;
        row.Label = folder.Label;
        row.Mode = folder.Mode;
        row.IsCloud = folder.IsCloud;
        row.Enabled = folder.Enabled;
        await db.SaveChangesAsync(ct);
        return ToFolder(row);
    }

    // The baseline rows go with the folder (fk_folder_baseline_folder cascades).
    public async Task RemoveAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.WatchedFolders.Where(f => f.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetBaselineAsync(int folderId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FolderBaselines.Where(b => b.FolderId == folderId).Select(b => b.Path).ToListAsync(ct);
    }

    // folder_baseline.path is citext, so the comparison ignores case in the database.
    public async Task<bool> IsInBaselineAsync(int folderId, string path, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FolderBaselines.AnyAsync(b => b.FolderId == folderId && b.Path == path, ct);
    }

    public async Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.FolderBaselines.Where(b => b.FolderId == folderId).ExecuteDeleteAsync(ct);
        db.FolderBaselines.AddRange(paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FolderBaseline { FolderId = folderId, Path = path }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ClearBaselineAsync(int folderId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.FolderBaselines.Where(b => b.FolderId == folderId).ExecuteDeleteAsync(ct);
    }

    public async Task<WatchedFolder> AddAsync(WatchedFolder folder, IReadOnlyList<string> baseline,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = new StoredWatchedFolder
        {
            Path = folder.Path, Label = folder.Label, Mode = folder.Mode, IsCloud = folder.IsCloud,
            Enabled = folder.Enabled, AddedAt = DateTimeOffset.UtcNow
        };
        db.WatchedFolders.Add(row);
        await db.SaveChangesAsync(ct);
        db.FolderBaselines.AddRange(baseline.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FolderBaseline { FolderId = row.Id, Path = path }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToFolder(row);
    }

    // The caller lists the new baseline from the new path. Finished paths are top-level files, so each keeps its file
    // name under the new path.
    public async Task<WatchedFolder?> RelocateAsync(int id, string path, bool isCloud, FolderMode mode,
        IReadOnlyList<string> newBaseline, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.WatchedFolders.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (row is null)
            return null;
        var baseline = await db.FolderBaselines.Where(b => b.FolderId == id).ToListAsync(ct);
        var finished = await db.FinishedPaths.Where(f => f.FolderId == id).ToListAsync(ct);
        // The paths are keys: the old rows go first, then the moved ones are added.
        db.FolderBaselines.RemoveRange(baseline);
        db.FinishedPaths.RemoveRange(finished);
        await db.SaveChangesAsync(ct);
        row.Path = path;
        row.IsCloud = isCloud;
        row.Mode = mode;
        db.FolderBaselines.AddRange(newBaseline.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => new FolderBaseline { FolderId = id, Path = file }));
        db.FinishedPaths.AddRange(finished.DistinctBy(f => System.IO.Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(f => new StoredFinishedPath
            {
                Path = System.IO.Path.Combine(path, System.IO.Path.GetFileName(f.Path)), FolderId = id,
                SizeBytes = f.SizeBytes, LastWriteAt = f.LastWriteAt, FinishedAt = f.FinishedAt
            }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToFolder(row);
    }

    private static WatchedFolder ToFolder(StoredWatchedFolder f) =>
        new(f.Id, f.Path, f.Label, f.Mode, f.IsCloud, f.Enabled);
}
