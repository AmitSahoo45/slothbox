using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data.Actions;
using SlothBox.Data.Files;

namespace SlothBox.Data.Dashboard;

// A file as the dashboard shows it (spec 7.4.2, 7.4.9): its row, category and folder label, its newest done action,
// and whether a filing of it is left to undo. File carries no extracted text: no page shows it.
public sealed record FileView(FileRecord File, string? Category, string? FolderLabel, long LastActionId,
    DateTimeOffset At, bool CanUndo);

// A file's receipt row and its actions, oldest first: the rest of what the file drawer shows.
public sealed record FileExtras(ReceiptData? Receipt, IReadOnlyList<FileAction> History);

// One file's first filing (spec 6.8), with the receipt's amount and currency when the file has a receipt row.
public sealed record FirstFiling(Guid FileId, FileKind Type, FileStatus Status, DateTimeOffset At, decimal? Amount,
    string? Currency);

// The dashboard's reads. Only files with a done action show (spec 6.2).
public sealed class DashboardQueries(IDbContextFactory<SlothBoxDbContext> contexts)
{
    // Newest first by each file's latest done action. `before` is the paging cursor: the last action id of the
    // previous page.
    public async Task<IReadOnlyList<FileView>> ActivityAsync(long? before, int limit, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await Views(db).Where(view => before == null || view.ActionId < before)
            .OrderByDescending(view => view.ActionId).Take(limit).ToListAsync(ct);
        return rows.ConvertAll(ToView);
    }

    public async Task<FileView?> FileAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await Views(db).SingleOrDefaultAsync(view => view.Id == id, ct);
        return row is null ? null : ToView(row);
    }

    // Needs review items in queue order: when each entered review or was last skipped (spec 7.4.3).
    public async Task<IReadOnlyList<FileView>> ReviewAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await Views(db).Where(view => view.Status == FileStatus.Review)
            .OrderBy(view => view.UpdatedAt).ThenBy(view => view.ActionId).ToListAsync(ct);
        return rows.ConvertAll(ToView);
    }

    // Receipts and histories of any number of files in two queries, so a page of files costs the same as one.
    public async Task<IReadOnlyDictionary<Guid, FileExtras>> ExtrasAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var list = ids.ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => list.Contains(r.FileId)).ToListAsync(ct);
        var actions = await db.Actions.AsNoTracking().Where(a => list.Contains(a.FileId)).OrderBy(a => a.Id)
            .ToListAsync(ct);
        var receiptOf = receipts.ToDictionary(r => r.FileId, ReceiptRepository.ToData);
        var historyOf = actions.Select(ActionLog.ToAction).ToLookup(action => action.FileId);
        return list.Distinct().ToDictionary(id => id,
            id => new FileExtras(receiptOf.GetValueOrDefault(id), historyOf[id].ToList()));
    }

    // Moves a review item to the end of the queue. False when the file is not in Needs review.
    public async Task<bool> SkipReviewAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.Files.Where(f => f.Id == id && f.Status == FileStatus.Review)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.UpdatedAt, now), ct) > 0;
    }

    // First filings: done move or copy actions with no `before` snapshot, completed since `since` (all when null).
    public async Task<IReadOnlyList<FirstFiling>> FirstFilingsAsync(DateTimeOffset? since, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var start = since?.ToUniversalTime();
        return await (
                from a in db.Actions
                where a.Status == ActionStatus.Done && a.Before == null &&
                      (a.Kind == ActionKind.Move || a.Kind == ActionKind.Copy) &&
                      (start == null || a.CompletedAt >= start)
                join f in db.Files on a.FileId equals f.Id
                join r in db.Receipts on f.Id equals r.FileId into receipts
                from r in receipts.DefaultIfEmpty()
                select new FirstFiling(f.Id, f.Type, f.Status, a.CompletedAt ?? a.UpdatedAt,
                    r == null ? null : r.Amount, r == null ? null : r.Currency))
            .ToListAsync(ct);
    }

    // The columns the dashboard shows: never the stored text, the vector or the search vector.
    private sealed class Row
    {
        public Guid Id { get; init; }
        public required string Sha256 { get; init; }
        public FileOrigin Origin { get; init; }
        public int? WatchedFolderId { get; init; }
        public required string OriginalName { get; init; }
        public required string OriginalPath { get; init; }
        public required string CurrentPath { get; init; }
        public FileKind Type { get; init; }
        public int? CategoryId { get; init; }
        public required string Title { get; init; }
        public required string Caption { get; init; }
        public DateTimeOffset FileDate { get; init; }
        public long SizeBytes { get; init; }
        public FileStatus Status { get; init; }
        public ReviewReason? ReviewReason { get; init; }
        public Guid? DuplicateOf { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string? Category { get; init; }
        public string? FolderLabel { get; init; }
        public long ActionId { get; init; }
        public DateTimeOffset At { get; init; }
        public int Filings { get; init; }
        public int Undos { get; init; }
    }

    // Each file with its newest done action. A file can be undone while its done moves and copies outnumber its done
    // undos: each undo reversed one of them (spec 6.6).
    private static IQueryable<Row> Views(SlothBoxDbContext db)
    {
        var latest = db.Actions.Where(a => a.Status == ActionStatus.Done).GroupBy(a => a.FileId).Select(g => new
        {
            FileId = g.Key,
            ActionId = g.Max(a => a.Id),
            Filings = g.Count(a => a.Kind == ActionKind.Move || a.Kind == ActionKind.Copy),
            Undos = g.Count(a => a.Kind == ActionKind.UndoMove || a.Kind == ActionKind.DeleteCopy)
        });
        return from l in latest
            join f in db.Files on l.FileId equals f.Id
            join a in db.Actions on l.ActionId equals a.Id
            join c in db.Categories on f.CategoryId equals (int?)c.Id into categories
            from c in categories.DefaultIfEmpty()
            join w in db.WatchedFolders on f.WatchedFolderId equals (int?)w.Id into folders
            from w in folders.DefaultIfEmpty()
            select new Row
            {
                Id = f.Id,
                Sha256 = f.Sha256,
                Origin = f.Origin,
                WatchedFolderId = f.WatchedFolderId,
                OriginalName = f.OriginalName,
                OriginalPath = f.OriginalPath,
                CurrentPath = f.CurrentPath,
                Type = f.Type,
                CategoryId = f.CategoryId,
                Title = f.Title,
                Caption = f.Caption,
                FileDate = f.FileDate,
                SizeBytes = f.SizeBytes,
                Status = f.Status,
                ReviewReason = f.ReviewReason,
                DuplicateOf = f.DuplicateOf,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                Category = c == null ? null : c.Name,
                FolderLabel = w == null ? null : w.Label,
                ActionId = l.ActionId,
                At = a.CompletedAt ?? a.UpdatedAt,
                Filings = l.Filings,
                Undos = l.Undos
            };
    }

    private static FileView ToView(Row row) => new(
        new FileRecord(row.Id, row.Sha256, row.Origin, row.WatchedFolderId, row.OriginalName, row.OriginalPath,
            row.CurrentPath, row.Type, row.CategoryId, row.Title, row.Caption, "", row.FileDate, row.SizeBytes,
            row.Status, row.ReviewReason, row.DuplicateOf, row.CreatedAt, row.UpdatedAt),
        row.Category, row.FolderLabel, row.ActionId, row.At, row.Filings > row.Undos);
}
