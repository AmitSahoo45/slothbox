using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Files;

public sealed class ReceiptRepository(IDbContextFactory<SlothBoxDbContext> contexts) : IReceiptRepository
{
    public async Task<ReceiptData?> GetAsync(Guid fileId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.FileId == fileId, ct);
        return row is null ? null : ToData(row);
    }

    public async Task SaveAsync(Guid fileId, ReceiptData receipt, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Receipts.SingleOrDefaultAsync(r => r.FileId == fileId, ct);
        if (row is null)
            db.Receipts.Add(row = new StoredReceipt { FileId = fileId });
        Apply(row, receipt);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Receipts.Where(r => r.FileId == fileId).ExecuteDeleteAsync(ct);
    }

    internal static void Apply(StoredReceipt row, ReceiptData receipt)
    {
        row.Vendor = receipt.Vendor;
        row.ReceiptDate = receipt.Date;
        row.Amount = receipt.Amount;
        row.Currency = receipt.Currency;
        row.PaymentMethod = receipt.PaymentMethod;
    }

    internal static ReceiptData ToData(StoredReceipt row) =>
        new(row.Vendor, row.ReceiptDate, row.Amount, row.Currency, row.PaymentMethod);
}
