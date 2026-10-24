using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Categories;

public sealed class CategoryQueries(IDbContextFactory<SlothBoxDbContext> contexts)
{
    // Files now filed in each category whose first filing finished (spec 6.2 "When a file row counts"); a Needs
    // review item has no category yet.
    public async Task<IReadOnlyDictionary<int, int>> FileCountsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Files
            .Where(f => f.CategoryId != null && (f.Status == FileStatus.Filed || f.Status == FileStatus.Review) &&
                db.Actions.Any(a => a.FileId == f.Id && a.Status == ActionStatus.Done))
            .GroupBy(f => f.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CategoryId, g => g.Count, ct);
    }
}
