using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Receipts;

// One receipt of the ledger (spec 7.4.5) with the file it belongs to.
public sealed record ReceiptRow(Guid FileId, string? Vendor, DateOnly Date, decimal Amount, string Currency,
    PaymentMethod PaymentMethod, int? CategoryId, string Category, string FileName);

public sealed class ReceiptQueries(IDbContextFactory<SlothBoxDbContext> contexts)
{
    // Receipts filed with a date in the month, newest first. Needs review items, duplicates and undone files are left
    // out, and so is a first filing that never finished (spec 6.2).
    public async Task<IReadOnlyList<ReceiptRow>> MonthAsync(DateOnly month, CancellationToken ct)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var next = first.AddMonths(1);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await (
                from receipt in db.Receipts
                join file in db.Files on receipt.FileId equals file.Id
                join category in db.Categories on file.CategoryId equals category.Id into categories
                from category in categories.DefaultIfEmpty()
                where file.Status == FileStatus.Filed && receipt.ReceiptDate >= first && receipt.ReceiptDate < next &&
                    receipt.Amount != null && db.Actions.Any(a => a.FileId == file.Id && a.Status == ActionStatus.Done)
                select new
                {
                    file.Id, receipt.Vendor, receipt.ReceiptDate, receipt.Amount, receipt.Currency,
                    receipt.PaymentMethod, file.CategoryId, Category = category == null ? null : category.Name,
                    file.CurrentPath
                })
            .AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(r => r.ReceiptDate).ThenBy(r => r.Vendor, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ReceiptRow(r.Id, r.Vendor, r.ReceiptDate!.Value, r.Amount!.Value, (r.Currency ?? "").Trim(),
                r.PaymentMethod, r.CategoryId, r.Category ?? "Other", Path.GetFileName(r.CurrentPath)))
            .ToList();
    }
}
