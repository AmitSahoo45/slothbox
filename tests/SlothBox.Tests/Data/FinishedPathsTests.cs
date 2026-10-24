using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Data.Folders;

namespace SlothBox.Tests.Data;

// Spec 4.5: the files that stayed in their folder after their last job, with the stamp they had then.
[Collection(PostgresCollection.Name)]
public sealed class FinishedPathsTests(PostgresFixture postgres)
{
    private const string Camera = @"D:\slothbox-test\Camera";
    private static readonly DateTime Noon = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> FolderAsync(WatchedFolderRepository folders) =>
        (await folders.SaveAsync(new WatchedFolder(0, Camera, "Camera", FolderMode.Copy, true, true), Ct)).Id;

    [Fact]
    public async Task A_finished_path_lists_back_by_its_folder_with_its_stamp_to_the_microsecond()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await FolderAsync(new WatchedFolderRepository(db));
        var finished = new FinishedPathStore(db);
        var entry = new FinishedPath(Path.Combine(Camera, "IMG_1.jpg"), folderId, new FileStamp(2048, Noon.AddTicks(1_234_560)));

        await finished.RecordAsync(entry, Ct);

        (await finished.ListAsync(folderId, Ct)).ShouldBe([entry]);
        (await finished.ListAsync(null, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recording_a_path_again_replaces_its_stamp()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await FolderAsync(new WatchedFolderRepository(db));
        var finished = new FinishedPathStore(db);
        var path = Path.Combine(Camera, "IMG_1.jpg");
        await finished.RecordAsync(new FinishedPath(path, folderId, new FileStamp(10, Noon)), Ct);

        await finished.RecordAsync(new FinishedPath(path.ToUpperInvariant(), folderId, new FileStamp(20, Noon.AddHours(1))), Ct);

        (await finished.ListAsync(folderId, Ct)).ShouldHaveSingleItem().Stamp.ShouldBe(new FileStamp(20, Noon.AddHours(1)));
    }

    [Fact]
    public async Task Inbox_paths_have_no_folder_and_are_listed_apart()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await FolderAsync(new WatchedFolderRepository(db));
        var finished = new FinishedPathStore(db);
        var inbox = new FinishedPath(@"D:\SlothBox\Inbox\phone-20261024-101010-a1b2c3.jpg", null, new FileStamp(5, Noon));
        await finished.RecordAsync(inbox, Ct);
        await finished.RecordAsync(new FinishedPath(Path.Combine(Camera, "IMG_1.jpg"), folderId, new FileStamp(5, Noon)), Ct);

        (await finished.ListAsync(null, Ct)).ShouldBe([inbox]);
    }

    [Fact]
    public async Task Forgotten_paths_are_gone_and_the_others_stay()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await FolderAsync(new WatchedFolderRepository(db));
        var finished = new FinishedPathStore(db);
        var gone = new FinishedPath(Path.Combine(Camera, "gone.jpg"), folderId, new FileStamp(5, Noon));
        var kept = new FinishedPath(Path.Combine(Camera, "kept.jpg"), folderId, new FileStamp(5, Noon));
        await finished.RecordAsync(gone, Ct);
        await finished.RecordAsync(kept, Ct);

        await finished.ForgetAsync([gone.Path], Ct);

        (await finished.ListAsync(folderId, Ct)).ShouldBe([kept]);
    }

    [Fact]
    public async Task Removing_a_folder_removes_its_finished_paths()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);
        var folderId = await FolderAsync(folders);
        var finished = new FinishedPathStore(db);
        await finished.RecordAsync(new FinishedPath(Path.Combine(Camera, "IMG_1.jpg"), folderId, new FileStamp(5, Noon)), Ct);

        await folders.RemoveAsync(folderId, Ct);

        (await finished.ListAsync(folderId, Ct)).ShouldBeEmpty();
    }

    // The worker records after the job; the folder may have been removed in between.
    [Fact]
    public async Task A_path_of_a_folder_removed_meanwhile_is_not_recorded()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);
        var folderId = await FolderAsync(folders);
        await folders.RemoveAsync(folderId, Ct);
        var finished = new FinishedPathStore(db);

        await finished.RecordAsync(new FinishedPath(Path.Combine(Camera, "IMG_1.jpg"), folderId, new FileStamp(5, Noon)), Ct);

        (await finished.ListAsync(folderId, Ct)).ShouldBeEmpty();
    }
}
