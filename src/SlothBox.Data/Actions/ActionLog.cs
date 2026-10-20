using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data.Files;

namespace SlothBox.Data.Actions;

public sealed class ActionLog(IDbContextFactory<SlothBoxDbContext> contexts) : IActionLog
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<long?> AppendPendingAsync(MoveRequest request, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One change per file at a time: the row lock makes a concurrent request wait, then see this one.
        var file = (await db.Files.FromSql($"SELECT * FROM files WHERE id = {request.FileId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.FileId == file.Id, ct);
        if (await db.Actions.AnyAsync(a => a.FileId == file.Id && a.Status == ActionStatus.Pending, ct) ||
            request.Before is { } expected && expected != ToSnapshot(file, receipt))
            return null;

        var now = DateTimeOffset.UtcNow;
        var action = new StoredAction
        {
            FileId = request.FileId,
            Kind = request.Kind,
            FromPath = request.From,
            ToPath = request.To,
            Sha256 = request.Sha256,
            Status = ActionStatus.Pending,
            Before = request.Before is null ? null : JsonSerializer.Serialize(request.Before, SnapshotJson),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Actions.Add(action);
        if (request.After is { } after)
            Apply(db, file, receipt, after, now);
        // An undone file's hash is ignored before the file reaches its source folder, so the watcher skips it.
        if (request.After?.Status == FileStatus.Undone)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ignored_hashes (sha256, reason, created_at)
                VALUES ({request.Sha256}, 'undone', {now})
                ON CONFLICT DO NOTHING
                """, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return action.Id;
    }

    public async Task CompleteAsync(long id, ActionStatus status, string? error, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var action = await db.Actions.SingleAsync(a => a.Id == id, ct);
        // Only a pending action is completed; a late second completion changes nothing.
        if (action.Status != ActionStatus.Pending)
            return;

        var now = DateTimeOffset.UtcNow;
        action.Status = status;
        action.Error = error;
        action.UpdatedAt = now;
        action.CompletedAt = now;
        var file = await db.Files.SingleAsync(f => f.Id == action.FileId, ct);
        if (status == ActionStatus.Done)
        {
            file.CurrentPath = action.ToPath;
            file.UpdatedAt = now;
        }
        else if (ToSnapshot(action.Before) is { } before)
        {
            Apply(db, file, await db.Receipts.SingleOrDefaultAsync(r => r.FileId == file.Id, ct), before, now);
        }
        await db.SaveChangesAsync(ct);
        // A failed undo leaves the file in SlothBox: drop the ignore entry unless another undone file still has it.
        if (status == ActionStatus.Failed && action.Kind is ActionKind.UndoMove or ActionKind.DeleteCopy)
            await db.IgnoredHashes
                .Where(h => h.Sha256 == action.Sha256 &&
                    !db.Files.Any(f => f.Sha256 == h.Sha256 && f.Status == FileStatus.Undone))
                .ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FileAction>> GetPendingAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var actions = await db.Actions.AsNoTracking()
            .Where(a => a.Status == ActionStatus.Pending).OrderBy(a => a.Id).ToListAsync(ct);
        return actions.ConvertAll(ToAction);
    }

    public async Task<IReadOnlyList<FileAction>> GetForFileAsync(Guid fileId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var actions = await db.Actions.AsNoTracking()
            .Where(a => a.FileId == fileId).OrderBy(a => a.Id).ToListAsync(ct);
        return actions.ConvertAll(ToAction);
    }

    public async Task<IReadOnlyList<FileAction>> GetDoneSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var from = since.ToUniversalTime();
        var actions = await db.Actions.AsNoTracking()
            .Where(a => a.Status == ActionStatus.Done && a.CreatedAt >= from).OrderBy(a => a.Id).ToListAsync(ct);
        return actions.ConvertAll(ToAction);
    }

    // Writes a snapshot's fields onto the file row and its receipt row; a snapshot without a receipt removes the row.
    private static void Apply(SlothBoxDbContext db, StoredFile file, StoredReceipt? receipt, FileSnapshot state,
        DateTimeOffset now)
    {
        file.Status = state.Status;
        file.Type = state.Type;
        file.CategoryId = state.CategoryId;
        file.Title = state.Title;
        file.ReviewReason = state.ReviewReason;
        file.UpdatedAt = now;

        if (state.Receipt is not { } data)
        {
            if (receipt is not null)
                db.Receipts.Remove(receipt);
            return;
        }
        if (receipt is null)
            db.Receipts.Add(receipt = new StoredReceipt { FileId = file.Id });
        ReceiptRepository.Apply(receipt, data);
    }

    private static FileSnapshot ToSnapshot(StoredFile file, StoredReceipt? receipt) =>
        new(file.Status, file.Type, file.CategoryId, file.Title, file.ReviewReason,
            receipt is null ? null : ReceiptRepository.ToData(receipt));

    private static FileSnapshot? ToSnapshot(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<FileSnapshot>(json, SnapshotJson);

    private static FileAction ToAction(StoredAction a) =>
        new(a.Id, a.FileId, a.Kind, a.FromPath, a.ToPath, a.Sha256, a.Status, a.Error, ToSnapshot(a.Before),
            a.CreatedAt, a.UpdatedAt, a.CompletedAt);
}
