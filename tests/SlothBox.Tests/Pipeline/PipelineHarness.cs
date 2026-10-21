using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Core.Gate;
using SlothBox.Core.Moving;
using SlothBox.Core.Naming;
using SlothBox.Core.Pipeline;
using SlothBox.Core.Reading;
using SlothBox.Data.Categories;
using SlothBox.Data.Folders;
using SlothBox.Tests.Data;
using SlothBox.Tests.Moving;

namespace SlothBox.Tests.Pipeline;

// The real gate, reader, names, mover and repositories over a fresh database and temp folders, with a scripted
// analyzer and a fake embedder: the pipeline runs end to end without a GPU.
internal sealed class PipelineHarness : IAsyncDisposable
{
    private PipelineHarness(MoveHarness moves)
    {
        Moves = moves;
        Categories = new CategoryRepository(moves.Db);
        Folders = new WatchedFolderRepository(moves.Db);
        Settings = new FixedSettings(new SlothBoxSettings { OutputRoot = moves.Temp.PathOf("output") });
        Directory.CreateDirectory(moves.Temp.PathOf("watched"));
        Directory.CreateDirectory(moves.Temp.PathOf("output", "Inbox"));
    }

    public static async Task<PipelineHarness> CreateAsync(PostgresFixture postgres) =>
        new(await MoveHarness.CreateAsync(postgres));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MoveHarness Moves { get; }
    public CategoryRepository Categories { get; }
    public WatchedFolderRepository Folders { get; }
    public FixedSettings Settings { get; }
    public ScriptedAnalyzer Analyzer { get; } = new();
    public FakeEmbedder Embedder { get; } = new();

    public FilePipeline Pipeline(SafeMover? mover = null) =>
        new(new FileGate(Folders, Moves.Ignored, Moves.Files, Settings.Current.Pipeline, (_, _) => Task.CompletedTask),
            new ContentReader(Settings.Current.Pipeline), Analyzer, new NameBuilder(Settings.Current.Pipeline),
            mover ?? Moves.Mover(), Embedder, Moves.Files, Moves.Receipts, Categories, Folders, Settings);

    public static Job JobFor(string path, FileOrigin origin = FileOrigin.Folder, int? folderId = null) =>
        new(1, new IncomingFile(path, origin, folderId, DateTimeOffset.UtcNow), JobStatus.Processing, 1, null, null);

    public string WatchedPath(string name) => Moves.Temp.PathOf("watched", name);

    public string Watched(string name, string content) => Moves.Watched(name, content);

    public string InboxPath(string name) => Moves.Temp.PathOf("output", "Inbox", name);

    public string Output(params string[] parts) => Moves.Output(parts);

    public string WriteOutput(string content, params string[] parts) => Moves.WriteOutput(content, parts);

    public async Task<int> AddFolderAsync(FolderMode mode = FolderMode.Move) =>
        (await Folders.SaveAsync(new WatchedFolder(0, Moves.Temp.PathOf("watched"), "Downloads", mode,
            mode == FolderMode.Copy, true), Ct)).Id;

    public async Task<float[]?> EmbeddingOfAsync(Guid fileId)
    {
        await using var context = Moves.Db.CreateDbContext();
        return (await context.Files.AsNoTracking().SingleAsync(f => f.Id == fileId, Ct)).Embedding?.ToArray();
    }

    public async Task<int> CountFilesAsync()
    {
        await using var context = Moves.Db.CreateDbContext();
        return await context.Files.CountAsync(Ct);
    }

    public ValueTask DisposeAsync() => Moves.DisposeAsync();
}
