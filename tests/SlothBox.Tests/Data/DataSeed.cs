using SlothBox.Core;
using SlothBox.Data;

namespace SlothBox.Tests.Data;

// Synthetic rows for tests that need a foreign key target.
internal static class DataSeed
{
    public static async Task<int> FolderAsync(TestDatabase db, FolderMode mode = FolderMode.Move)
    {
        await using var context = db.CreateDbContext();
        var folder = new StoredWatchedFolder
        {
            Path = $@"D:\slothbox-test\watched-{Guid.NewGuid():N}",
            Label = "watched",
            Mode = mode,
            Enabled = true,
            AddedAt = DateTimeOffset.UtcNow
        };
        context.WatchedFolders.Add(folder);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return folder.Id;
    }

    public static async Task<Guid> FileAsync(TestDatabase db)
    {
        await using var context = db.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            Sha256 = "synthetic",
            Origin = FileOrigin.Folder,
            OriginalName = "bill.pdf",
            OriginalPath = @"D:\slothbox-test\watched\bill.pdf",
            CurrentPath = @"D:\slothbox-test\watched\bill.pdf",
            Type = FileKind.Document,
            Status = FileStatus.Filed,
            FileDate = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Files.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return file.Id;
    }
}
