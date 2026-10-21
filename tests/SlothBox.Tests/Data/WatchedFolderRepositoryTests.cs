using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data.Folders;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class WatchedFolderRepositoryTests(PostgresFixture postgres)
{
    private const string Downloads = @"D:\slothbox-test\Downloads";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_folder_gets_an_id_lists_back_and_a_second_save_updates_it()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);

        var saved = await folders.SaveAsync(new WatchedFolder(0, Downloads, "Downloads", FolderMode.Move, false, true), Ct);

        saved.Id.ShouldBeGreaterThan(0);
        (await folders.ListAsync(Ct)).ShouldBe([saved]);

        var paused = await folders.SaveAsync(saved with { Label = "My downloads", Enabled = false }, Ct);

        paused.Id.ShouldBe(saved.Id);
        (await folders.ListAsync(Ct)).ShouldBe([paused]);
    }

    [Fact]
    public async Task A_baseline_is_set_checked_without_regard_to_case_listed_and_cleared()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);
        var folder = await folders.SaveAsync(new WatchedFolder(0, Downloads, "Downloads", FolderMode.Move, false, true), Ct);
        string[] existing = [Path.Combine(Downloads, "old-bill.pdf"), Path.Combine(Downloads, "setup.exe")];

        await folders.SetBaselineAsync(folder.Id, existing, Ct);

        (await folders.IsInBaselineAsync(folder.Id, Path.Combine(Downloads, "OLD-BILL.PDF"), Ct)).ShouldBeTrue();
        (await folders.IsInBaselineAsync(folder.Id, Path.Combine(Downloads, "new-bill.pdf"), Ct)).ShouldBeFalse();
        (await folders.GetBaselineAsync(folder.Id, Ct)).ShouldBe(existing, ignoreOrder: true);

        await folders.ClearBaselineAsync(folder.Id, Ct);

        (await folders.GetBaselineAsync(folder.Id, Ct)).ShouldBeEmpty();
        (await folders.IsInBaselineAsync(folder.Id, existing[0], Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Removing_a_folder_removes_it_and_its_baseline()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);
        var folder = await folders.SaveAsync(new WatchedFolder(0, Downloads, "Downloads", FolderMode.Copy, true, true), Ct);
        await folders.SetBaselineAsync(folder.Id, [Path.Combine(Downloads, "old-bill.pdf")], Ct);

        await folders.RemoveAsync(folder.Id, Ct);

        (await folders.ListAsync(Ct)).ShouldBeEmpty();
        await using var context = db.CreateDbContext();
        (await context.FolderBaselines.CountAsync(Ct)).ShouldBe(0);
    }
}
