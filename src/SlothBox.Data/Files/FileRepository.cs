using Microsoft.EntityFrameworkCore;
using Pgvector;
using SlothBox.Core;

namespace SlothBox.Data.Files;

public sealed class FileRepository(IDbContextFactory<SlothBoxDbContext> contexts) : IFileRepository
{
    public async Task<FileRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
        return file is null ? null : ToRecord(file);
    }

    // A file counts once its first filing is done and it is still in the output folder (filed or in Needs review),
    // so a filing that never finished cannot turn the next copy into a duplicate.
    public async Task<FileRecord?> FindFiledByHashAsync(string sha256, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking()
            .Where(f => f.Sha256 == sha256 && (f.Status == FileStatus.Filed || f.Status == FileStatus.Review) &&
                db.Actions.Any(a => a.FileId == f.Id && a.Status == ActionStatus.Done))
            .OrderBy(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return file is null ? null : ToRecord(file);
    }

    public async Task<FileRecord?> FindUnfinishedAsync(string sha256, string originalPath, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking()
            .Where(f => f.Sha256 == sha256 && f.OriginalPath == originalPath &&
                !db.Actions.Any(a => a.FileId == f.Id && a.Status == ActionStatus.Done))
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return file is null ? null : ToRecord(file);
    }

    public async Task SaveEmbeddingAsync(Guid id, float[] embedding, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var vector = new Vector(embedding);
        var now = DateTimeOffset.UtcNow;
        await db.Files.Where(f => f.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(f => f.Embedding, vector)
            .SetProperty(f => f.UpdatedAt, now), ct);
    }

    public async Task SaveAsync(FileRecord record, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == record.Id, ct);
        if (file is null)
            db.Files.Add(file = new StoredFile { Id = record.Id });
        file.Sha256 = record.Sha256;
        file.Origin = record.Origin;
        file.WatchedFolderId = record.WatchedFolderId;
        file.OriginalName = record.OriginalName;
        file.OriginalPath = record.OriginalPath;
        file.CurrentPath = record.CurrentPath;
        file.Type = record.Type;
        file.CategoryId = record.CategoryId;
        file.Title = record.Title;
        file.Caption = record.Caption;
        file.ExtractedText = record.ExtractedText;
        file.FileDate = record.FileDate.ToUniversalTime();
        file.SizeBytes = record.SizeBytes;
        file.Status = record.Status;
        file.ReviewReason = record.ReviewReason;
        file.DuplicateOf = record.DuplicateOf;
        file.CreatedAt = record.CreatedAt.ToUniversalTime();
        file.UpdatedAt = record.UpdatedAt.ToUniversalTime();
        await db.SaveChangesAsync(ct);
    }

    internal static FileRecord ToRecord(StoredFile f) =>
        new(f.Id, f.Sha256, f.Origin, f.WatchedFolderId, f.OriginalName, f.OriginalPath, f.CurrentPath, f.Type,
            f.CategoryId, f.Title, f.Caption, f.ExtractedText, f.FileDate, f.SizeBytes, f.Status, f.ReviewReason,
            f.DuplicateOf, f.CreatedAt, f.UpdatedAt);
}
