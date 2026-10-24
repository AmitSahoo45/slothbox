using SlothBox.Core;
using SlothBox.Core.Folders;

namespace SlothBox.Tests.Watching;

// Watched folders with their baselines, in memory. Members the watching tests never need throw.
internal sealed class InMemoryFolders : IWatchedFolderRepository
{
    public List<WatchedFolder> Folders { get; } = [];
    public Dictionary<int, List<string>> Baselines { get; } = [];

    public Task<IReadOnlyList<WatchedFolder>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WatchedFolder>>([.. Folders]);

    public Task<IReadOnlyList<string>> GetBaselineAsync(int folderId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Baselines.TryGetValue(folderId, out var paths) ? [.. paths] : []);

    public Task<bool> IsInBaselineAsync(int folderId, string path, CancellationToken ct) =>
        Task.FromResult(Baselines.TryGetValue(folderId, out var paths) &&
            paths.Contains(path, StringComparer.OrdinalIgnoreCase));

    public Task<WatchedFolder> SaveAsync(WatchedFolder folder, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task ClearBaselineAsync(int folderId, CancellationToken ct) => throw new NotSupportedException();
    public Task<WatchedFolder> AddAsync(WatchedFolder folder, IReadOnlyList<string> baseline, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<WatchedFolder?> RelocateAsync(int id, string path, bool isCloud, FolderMode mode,
        IReadOnlyList<string> baseline, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class InMemoryFinishedPaths : IFinishedPaths
{
    public Dictionary<string, FinishedPath> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Runs as an entry is recorded, or as the entries are read (before they are handed out).
    public Action<FinishedPath>? OnRecord { get; set; }
    public Action? OnList { get; set; }

    public Task RecordAsync(FinishedPath entry, CancellationToken ct)
    {
        OnRecord?.Invoke(entry);
        Entries[entry.Path] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FinishedPath>> ListAsync(int? folderId, CancellationToken ct)
    {
        OnList?.Invoke();
        return Task.FromResult<IReadOnlyList<FinishedPath>>([.. Entries.Values.Where(e => e.FolderId == folderId)]);
    }

    public Task ForgetAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
            Entries.Remove(path);
        return Task.CompletedTask;
    }
}

// Keeps what was queued; a queue that accepts everything, like the real one does for a new path.
internal sealed class RecordingQueue : IJobQueue
{
    private readonly Lock _gate = new();
    private readonly List<IncomingFile> _queued = [];

    public IReadOnlyList<IncomingFile> Queued
    {
        get
        {
            lock (_gate)
                return [.. _queued];
        }
    }

    public Task EnqueueAsync(IncomingFile file, CancellationToken ct)
    {
        lock (_gate)
            _queued.Add(file);
        return Task.CompletedTask;
    }

    public async Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, FileStamp stamp, CancellationToken ct)
    {
        await EnqueueAsync(file, ct);
        return true;
    }

    public Task<Job?> ClaimNextAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> RequeueInterruptedAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<QueueCounts> CountsAsync(CancellationToken ct) => throw new NotSupportedException();
}
