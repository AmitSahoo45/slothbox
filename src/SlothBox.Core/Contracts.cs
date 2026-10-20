namespace SlothBox.Core;

public interface IFolderRules
{
    FolderCheck Check(string path, IReadOnlyList<WatchedFolder> existing, string outputRoot);
}

public interface IPlatformPaths
{
    IReadOnlyList<string> SystemFolders { get; }
    IReadOnlyList<string> CloudRoots { get; }
    bool IsDriveRoot(string path);
    bool IsNetworkPath(string path);
}

public interface IFolderWatcher
{
    IAsyncEnumerable<IncomingFile> WatchAsync(CancellationToken ct);
    Task AddAsync(WatchedFolder folder, CancellationToken ct);
    Task RemoveAsync(int folderId, CancellationToken ct);
    IReadOnlyList<IncomingFile> Sweep();
}

public interface IFileGate
{
    Task<GateResult> CheckAsync(IncomingFile file, CancellationToken ct);
}

public interface IContentReader
{
    Task<FileContent> ReadAsync(string path, CancellationToken ct);
}

public interface IFileAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken ct);
}

public interface INameBuilder
{
    string BuildFileName(Analysis analysis, SourceFileInfo info);
    string BuildTargetFolder(FileKind kind, string categoryFolder);
}

public interface ISafeMover
{
    Task<MoveResult> MoveAsync(Guid fileId, string from, string to, FolderMode mode, CancellationToken ct);
    Task RecoverPendingAsync(CancellationToken ct);
}

public interface IEmbedder
{
    Task<float[]> EmbedDocumentAsync(string title, string body, CancellationToken ct);
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);
}

public interface IJobQueue
{
    Task EnqueueAsync(IncomingFile file, CancellationToken ct);
    Task<Job?> ClaimNextAsync(CancellationToken ct);
    Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct);
}

public interface IUndoService
{
    Task<UndoResult> UndoFileAsync(Guid fileId, CancellationToken ct);
    Task<UndoResult> UndoLastAsync(CancellationToken ct);
    Task<IReadOnlyList<UndoResult>> UndoSinceAsync(DateTimeOffset since, CancellationToken ct);
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);
}

public interface INativeDialogs
{
    Task<string?> PickFolderAsync(CancellationToken ct);
}

public interface IKnownFolders
{
    string? Downloads { get; }
    string? Screenshots { get; }
}

public interface IAutoStart
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public interface ITrayNotifier
{
    void ShowReviewNotice(Guid fileId, string fileName);
    void SetStatus(string statusLine);
}

public interface IFileRepository
{
    Task<FileRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<FileRecord?> FindFiledByHashAsync(string sha256, CancellationToken ct);
    Task SaveAsync(FileRecord file, CancellationToken ct);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct);
    Task<Category> SaveAsync(Category category, CancellationToken ct);
    Task DeleteEmptyAsync(int id, CancellationToken ct);
}

public interface IReceiptRepository
{
    Task<ReceiptData?> GetAsync(Guid fileId, CancellationToken ct);
    Task SaveAsync(Guid fileId, ReceiptData receipt, CancellationToken ct);
    Task DeleteAsync(Guid fileId, CancellationToken ct);
}

public interface IActionLog
{
    Task<long> AppendPendingAsync(FileAction action, CancellationToken ct);
    Task CompleteAsync(long id, ActionStatus status, string? error, CancellationToken ct);
    Task<IReadOnlyList<FileAction>> GetPendingAsync(CancellationToken ct);
    Task<IReadOnlyList<FileAction>> GetForFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<FileAction>> GetDoneSinceAsync(DateTimeOffset since, CancellationToken ct);
}

public interface IIgnoredHashes
{
    Task<bool> ContainsAsync(string sha256, CancellationToken ct);
    Task AddAsync(IgnoredHash hash, CancellationToken ct);
    Task RemoveAsync(string sha256, CancellationToken ct);
    Task<IReadOnlyList<IgnoredHash>> ListAsync(CancellationToken ct);
}

public interface IWatchedFolderRepository
{
    Task<IReadOnlyList<WatchedFolder>> ListAsync(CancellationToken ct);
    Task<WatchedFolder> SaveAsync(WatchedFolder folder, CancellationToken ct);
    Task RemoveAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetBaselineAsync(int folderId, CancellationToken ct);
    Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct);
    Task ClearBaselineAsync(int folderId, CancellationToken ct);
}

public interface IAppState
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
