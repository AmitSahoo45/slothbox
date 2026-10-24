using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Data;

namespace SlothBox.Tests;

// In-memory stand-ins for the Data repositories. Members a test never needs throw, so an unexpected call is visible.
internal sealed class FakeWatchedFolders : IWatchedFolderRepository
{
    public Dictionary<int, List<string>> Baselines { get; } = [];

    public Task<bool> IsInBaselineAsync(int folderId, string path, CancellationToken ct) =>
        Task.FromResult(Baselines.TryGetValue(folderId, out var paths) &&
            paths.Contains(path, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<WatchedFolder>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<WatchedFolder> SaveAsync(WatchedFolder folder, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetBaselineAsync(int folderId, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task ClearBaselineAsync(int folderId, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeIgnoredHashes : IIgnoredHashes
{
    public HashSet<string> Hashes { get; } = [];

    public Task<bool> ContainsAsync(string sha256, CancellationToken ct) => Task.FromResult(Hashes.Contains(sha256));

    public Task AddAsync(IgnoredHash hash, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(string sha256, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<IgnoredHash>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
}

// Ollama as a test sets it: answering with both models pulled, unless told otherwise.
internal sealed class FixedGemma : IOllamaStatus
{
    public static GemmaState Ready(bool visionOnGpu = true) => new(true, null,
        new ModelState("gemma4:e4b", true, true, visionOnGpu), new ModelState("embeddinggemma", true, true, false));

    public GemmaState State { get; set; } = Ready();

    public Task<GemmaState> CheckAsync(CancellationToken ct) => Task.FromResult(State);
}

// API tests that have no test database get this: anything reaching for a database fails at once, instead of
// finding the owner's real one on 127.0.0.1:5433.
internal sealed class NoDatabase : IDbContextFactory<SlothBoxDbContext>
{
    public SlothBoxDbContext CreateDbContext() =>
        throw new InvalidOperationException("This test has no database.");
}

// Records what the dashboard asked to open or show, instead of starting programs.
internal sealed class RecordingLauncher : IFileLauncher
{
    public List<(string Action, string Path)> Calls { get; } = [];

    public void Open(string path) => Calls.Add(("open", path));
    public void Reveal(string path) => Calls.Add(("reveal", path));
}

internal sealed class FixedSettings(SlothBoxSettings settings) : ISettingsStore
{
    private volatile SlothBoxSettings _current = settings;

    public SlothBoxSettings Current
    {
        get => _current;
        set => _current = value;
    }
}

// Answers every analysis with the scripted result and keeps the requests it saw.
internal sealed class ScriptedAnalyzer : IFileAnalyzer
{
    public List<AnalysisRequest> Requests { get; } = [];

    public Func<AnalysisRequest, AnalysisResult> Answer { get; set; } =
        _ => throw new InvalidOperationException("No analysis was scripted for this test.");

    public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Answer(request));
    }
}

internal sealed class FakeEmbedder : IEmbedder
{
    public static readonly float[] Vector = Enumerable.Range(0, 768).Select(i => i / 768f).ToArray();

    public List<(string Title, string Body)> Documents { get; } = [];
    public Exception? Failure { get; set; }

    public Task<float[]> EmbedDocumentAsync(string title, string body, CancellationToken ct)
    {
        if (Failure is not null)
            throw Failure;
        Documents.Add((title, body));
        return Task.FromResult(Vector);
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeFiles : IFileRepository
{
    public List<FileRecord> Records { get; } = [];

    public Task<FileRecord?> FindFiledByHashAsync(string sha256, CancellationToken ct) =>
        Task.FromResult(Records.FirstOrDefault(record => record.Sha256 == sha256));

    public Task<FileRecord?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<FileRecord?> FindUnfinishedAsync(string sha256, string originalPath, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SaveAsync(FileRecord file, CancellationToken ct) => throw new NotSupportedException();
    public Task SaveEmbeddingAsync(Guid id, float[] embedding, CancellationToken ct) =>
        throw new NotSupportedException();
}
