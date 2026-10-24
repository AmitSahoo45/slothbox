using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SlothBox.Core;
using SlothBox.Core.Folders;
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

    // Locate (spec 4.5): the folder moves with the baseline its new place needs; finished paths follow by name.
    [Fact]
    public async Task Locating_a_folder_moves_it_with_its_new_baseline_and_its_finished_paths()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);
        var finished = new FinishedPathStore(db);
        var folder = await folders.SaveAsync(new WatchedFolder(0, Downloads, "Downloads", FolderMode.Move, false, true), Ct);
        await folders.SetBaselineAsync(folder.Id, [Path.Combine(Downloads, "old-bill.pdf")], Ct);
        var stamp = new FileStamp(10, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
        await finished.RecordAsync(new FinishedPath(Path.Combine(Downloads, "kept.jpg"), folder.Id, stamp), Ct);
        const string moved = @"E:\slothbox-test\Downloads";
        string[] baseline = [Path.Combine(moved, "old-bill.pdf"), Path.Combine(moved, "already-there.pdf")];

        var located = await folders.RelocateAsync(folder.Id, moved, isCloud: true, FolderMode.Copy, baseline, Ct);

        located.ShouldBe(folder with { Path = moved, IsCloud = true, Mode = FolderMode.Copy });
        (await folders.ListAsync(Ct)).ShouldBe([located!]);
        (await folders.GetBaselineAsync(folder.Id, Ct)).ShouldBe(baseline, ignoreOrder: true);
        (await finished.ListAsync(folder.Id, Ct)).ShouldBe([new FinishedPath(Path.Combine(moved, "kept.jpg"), folder.Id, stamp)]);
    }

    [Fact]
    public async Task Locating_a_folder_that_was_removed_finds_nothing()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);

        (await new WatchedFolderRepository(db).RelocateAsync(42, Downloads, false, FolderMode.Move, [], Ct))
            .ShouldBeNull();
    }

    [Fact]
    public async Task A_new_folder_is_added_with_its_baseline()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db);

        var added = await folders.AddAsync(new WatchedFolder(0, Downloads, "Downloads", FolderMode.Move, false, true),
            [Path.Combine(Downloads, "old-bill.pdf")], Ct);

        (await folders.ListAsync(Ct)).ShouldBe([added]);
        (await folders.GetBaselineAsync(added.Id, Ct)).ShouldBe([Path.Combine(Downloads, "old-bill.pdf")]);
    }

    // A switched-on folder without its baseline would have its existing files swept (D25).
    [Fact]
    public async Task A_new_folder_whose_baseline_cannot_be_written_is_not_added()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folders = new WatchedFolderRepository(db.With(new FailingBaselineInsert()));

        await Should.ThrowAsync<DbUpdateException>(() => folders.AddAsync(
            new WatchedFolder(0, Downloads, "Downloads", FolderMode.Move, false, true),
            [Path.Combine(Downloads, "old-bill.pdf")], Ct));

        (await new WatchedFolderRepository(db).ListAsync(Ct)).ShouldBeEmpty();
    }

    // The database fails while the baseline rows are written.
    private sealed class FailingBaselineInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void Fail(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO folder_baseline", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The database went away.");
        }
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
