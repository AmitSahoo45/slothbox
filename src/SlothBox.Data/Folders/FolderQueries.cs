using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Folders;

// The Folders page's numbers (spec 7.4.6): files filed from each folder and the files that were already there.
public sealed record FolderCounts(int Filed, int Baseline);

public sealed class FolderQueries(IDbContextFactory<SlothBoxDbContext> contexts)
{
    // Filed counts files now filed or in Needs review whose first filing finished (spec 6.2 "When a file row counts").
    public async Task<IReadOnlyDictionary<int, FolderCounts>> CountsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var filed = await db.Files
            .Where(f => f.WatchedFolderId != null && (f.Status == FileStatus.Filed || f.Status == FileStatus.Review) &&
                db.Actions.Any(a => a.FileId == f.Id && a.Status == ActionStatus.Done))
            .GroupBy(f => f.WatchedFolderId!.Value)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.FolderId, g => g.Count, ct);
        var baseline = await db.FolderBaselines.GroupBy(b => b.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.FolderId, g => g.Count, ct);
        return filed.Keys.Union(baseline.Keys).ToDictionary(id => id,
            id => new FolderCounts(filed.GetValueOrDefault(id), baseline.GetValueOrDefault(id)));
    }
}
