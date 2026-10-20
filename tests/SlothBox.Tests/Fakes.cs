using SlothBox.Core;

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

internal sealed class FakeFiles : IFileRepository
{
    public List<FileRecord> Records { get; } = [];

    public Task<FileRecord?> FindFiledByHashAsync(string sha256, CancellationToken ct) =>
        Task.FromResult(Records.FirstOrDefault(record => record.Sha256 == sha256));

    public Task<FileRecord?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task SaveAsync(FileRecord file, CancellationToken ct) => throw new NotSupportedException();
}
