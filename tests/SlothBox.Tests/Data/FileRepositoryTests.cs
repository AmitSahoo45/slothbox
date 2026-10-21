using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data;
using SlothBox.Data.Actions;
using SlothBox.Data.Files;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class FileRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_file_reads_back_and_a_second_save_updates_it()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var folderId = await DataSeed.FolderAsync(db);
        var files = new FileRepository(db);
        var at = new DateTimeOffset(2026, 10, 20, 10, 41, 12, TimeSpan.FromHours(5.5));
        var review = new FileRecord(Guid.NewGuid(), "a1b2", FileOrigin.Folder, folderId, "IMG_2041.jpg",
            @"D:\slothbox-test\watched\IMG_2041.jpg", @"D:\slothbox-test\out\Needs review\IMG_2041.jpg",
            FileKind.Receipt, 5, "Jio recharge", "UPI payment of 599 INR to Jio", "Payment successful", at, 2048,
            FileStatus.Review, ReviewReason.MissingDate, null, at, at);

        await files.SaveAsync(review, Ct);
        (await files.GetAsync(review.Id, Ct)).ShouldBe(review);

        var filed = review with
        {
            CategoryId = 3,
            CurrentPath = @"D:\slothbox-test\out\Receipts\Bills and recharges\2026-10-20_Jio_599INR.jpg",
            Status = FileStatus.Filed,
            ReviewReason = null,
            UpdatedAt = at.AddMinutes(2)
        };
        await files.SaveAsync(filed, Ct);
        (await files.GetAsync(review.Id, Ct)).ShouldBe(filed);
    }

    [Fact]
    public async Task An_unfinished_first_filing_is_found_by_content_and_path_until_its_action_is_done()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var files = new FileRepository(db);
        var log = new ActionLog(db);
        const string source = @"D:\slothbox-test\watched\receipt.jpg";
        var record = TestRecords.File("abc") with { OriginalPath = source, CurrentPath = source };
        await files.SaveAsync(record, Ct);

        (await files.FindUnfinishedAsync("abc", source, Ct))!.Id.ShouldBe(record.Id);
        (await files.FindUnfinishedAsync("abc", @"D:\slothbox-test\elsewhere\receipt.jpg", Ct)).ShouldBeNull();
        (await files.FindUnfinishedAsync("other", source, Ct)).ShouldBeNull();

        var failed = (await log.AppendPendingAsync(new MoveRequest(record.Id, ActionKind.Move, source,
            @"D:\slothbox-test\out\Receipts\Food\receipt.jpg", "abc"), Ct)).ShouldNotBeNull();
        await log.CompleteAsync(failed, ActionStatus.Failed, "drive full", Ct);
        (await files.FindUnfinishedAsync("abc", source, Ct))!.Id.ShouldBe(record.Id);

        var done = (await log.AppendPendingAsync(new MoveRequest(record.Id, ActionKind.Move, source,
            @"D:\slothbox-test\out\Receipts\Food\receipt.jpg", "abc"), Ct)).ShouldNotBeNull();
        await log.CompleteAsync(done, ActionStatus.Done, null, Ct);
        (await files.FindUnfinishedAsync("abc", source, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_embedding_is_saved_on_the_file_row()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var files = new FileRepository(db);
        var record = TestRecords.File("abc");
        await files.SaveAsync(record, Ct);
        var vector = Enumerable.Range(0, 768).Select(i => i / 768f).ToArray();

        await files.SaveEmbeddingAsync(record.Id, vector, Ct);

        await using var context = db.CreateDbContext();
        var stored = await context.Files.AsNoTracking().SingleAsync(f => f.Id == record.Id, Ct);
        stored.Embedding.ShouldNotBeNull().ToArray().ShouldBe(vector);
    }

    [Fact]
    public async Task An_unknown_id_reads_as_null()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);

        (await new FileRepository(db).GetAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Only_files_whose_filing_finished_and_that_are_still_in_the_output_count_for_duplicates()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var files = new FileRepository(db);
        var filed = await AddAsync(db, "filed", FileStatus.Filed, filingDone: true);
        var review = await AddAsync(db, "review", FileStatus.Review, filingDone: true);
        await AddAsync(db, "unfinished", FileStatus.Filed, filingDone: false);
        await AddAsync(db, "undone", FileStatus.Undone, filingDone: true);
        await AddAsync(db, "duplicate", FileStatus.Duplicate, filingDone: true);

        (await files.FindFiledByHashAsync("filed", Ct))!.Id.ShouldBe(filed);
        (await files.FindFiledByHashAsync("review", Ct))!.Id.ShouldBe(review);
        (await files.FindFiledByHashAsync("unfinished", Ct)).ShouldBeNull();
        (await files.FindFiledByHashAsync("undone", Ct)).ShouldBeNull();
        (await files.FindFiledByHashAsync("duplicate", Ct)).ShouldBeNull();
    }

    private static async Task<Guid> AddAsync(TestDatabase db, string sha256, FileStatus status, bool filingDone)
    {
        await using var context = db.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            Sha256 = sha256,
            Origin = FileOrigin.Folder,
            OriginalName = $"{sha256}.pdf",
            OriginalPath = $@"D:\slothbox-test\watched\{sha256}.pdf",
            CurrentPath = $@"D:\slothbox-test\out\{sha256}.pdf",
            Type = FileKind.Document,
            Status = status,
            ReviewReason = status == FileStatus.Review ? ReviewReason.UnknownType : null,
            FileDate = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Files.Add(file);
        context.Actions.Add(new StoredAction
        {
            FileId = file.Id,
            Kind = ActionKind.Move,
            FromPath = file.OriginalPath,
            ToPath = file.CurrentPath,
            Sha256 = sha256,
            Status = filingDone ? ActionStatus.Done : ActionStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(Ct);
        return file.Id;
    }
}
