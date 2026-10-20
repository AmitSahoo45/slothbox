using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Core.Moving;
using SlothBox.Core.Undo;
using SlothBox.Data;
using SlothBox.Data.Actions;
using SlothBox.Data.Files;
using SlothBox.Data.Queue;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Moving;

// A fresh database, a temp folder tree ("watched" and "output") and the real repositories behind the Core mover.
internal sealed class MoveHarness : IAsyncDisposable
{
    private MoveHarness(TestDatabase db)
    {
        Db = db;
        Files = new FileRepository(db);
        Log = new ActionLog(db);
        Jobs = new JobQueue(db);
        Receipts = new ReceiptRepository(db);
        Ignored = new IgnoredHashes(db);
    }

    public static async Task<MoveHarness> CreateAsync(PostgresFixture postgres) =>
        new(await postgres.CreateDatabaseAsync(Ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestDatabase Db { get; }
    public TempFolder Temp { get; } = new();
    public FileRepository Files { get; }
    public ActionLog Log { get; }
    public JobQueue Jobs { get; }
    public ReceiptRepository Receipts { get; }
    public IgnoredHashes Ignored { get; }

    public SafeMover Mover(bool? sameVolume = null) =>
        new(Log, Files, Jobs, sameVolume is { } same ? (_, _) => same : null);

    public UndoService Undo() => new(Log, Files, Receipts, Mover());

    public static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public string Watched(string name, string content) => Temp.Write(Path.Combine("watched", name), content);

    public string Output(params string[] parts) => Temp.PathOf(["output", .. parts]);

    public string WriteOutput(string content, params string[] parts)
    {
        var path = Output(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public Task<int> AddFolderAsync() => DataSeed.FolderAsync(Db);

    // A file row for the file at `path`, in `state` (filed receipt by default), with its receipt row if any.
    public async Task<FileRecord> AddFileAsync(string path, FileSnapshot? state = null, int? folderId = null,
        string? sha256 = null)
    {
        state ??= new FileSnapshot(FileStatus.Filed, FileKind.Receipt, 1, "Cafe bill", null, null);
        var now = DateTimeOffset.UtcNow;
        var record = new FileRecord(Guid.NewGuid(), sha256 ?? Sha256Of(File.ReadAllText(path)), FileOrigin.Folder,
            folderId, Path.GetFileName(path), path, path, state.Type, state.CategoryId, state.Title, "", "", now, 5,
            state.Status, state.ReviewReason, null, now, now);
        await Files.SaveAsync(record, Ct);
        if (state.Receipt is { } receipt)
        {
            await using var context = Db.CreateDbContext();
            context.Receipts.Add(new StoredReceipt
            {
                FileId = record.Id,
                Vendor = receipt.Vendor,
                ReceiptDate = receipt.Date,
                Amount = receipt.Amount,
                Currency = receipt.Currency,
                PaymentMethod = receipt.PaymentMethod
            });
            await context.SaveChangesAsync(Ct);
        }
        return record;
    }

    public Task<IReadOnlyList<FileAction>> ActionsAsync(Guid fileId) => Log.GetForFileAsync(fileId, Ct);

    public async Task<FileRecord> FileAsync(Guid fileId) => (await Files.GetAsync(fileId, Ct)).ShouldNotBeNull();

    // The file's stored state, read straight from the tables.
    public async Task<FileSnapshot> SnapshotAsync(Guid fileId)
    {
        await using var context = Db.CreateDbContext();
        var file = await context.Files.AsNoTracking().SingleAsync(f => f.Id == fileId, Ct);
        var receipt = await context.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.FileId == fileId, Ct);
        return new FileSnapshot(file.Status, file.Type, file.CategoryId, file.Title, file.ReviewReason,
            receipt is null
                ? null
                : new ReceiptData(receipt.Vendor, receipt.ReceiptDate, receipt.Amount, receipt.Currency,
                    receipt.PaymentMethod));
    }

    public async ValueTask DisposeAsync()
    {
        Temp.Dispose();
        await Db.DisposeAsync();
    }
}
