using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Files;

public sealed class IgnoredHashes(IDbContextFactory<SlothBoxDbContext> contexts) : IIgnoredHashes
{
    public async Task<bool> ContainsAsync(string sha256, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.IgnoredHashes.AnyAsync(h => h.Sha256 == sha256, ct);
    }

    // Ignoring a hash twice keeps the first entry and its time.
    public async Task AddAsync(IgnoredHash hash, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ignored_hashes (sha256, reason, created_at)
            VALUES ({hash.Sha256}, {hash.Reason}, {hash.CreatedAt.ToUniversalTime()})
            ON CONFLICT DO NOTHING
            """, ct);
    }

    public async Task RemoveAsync(string sha256, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.IgnoredHashes.Where(h => h.Sha256 == sha256).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<IgnoredHash>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = await db.IgnoredHashes.AsNoTracking().OrderByDescending(h => h.CreatedAt).ToListAsync(ct);
        return hashes.ConvertAll(h => new IgnoredHash(h.Sha256, h.Reason, h.CreatedAt));
    }
}
