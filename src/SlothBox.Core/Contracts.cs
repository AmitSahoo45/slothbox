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

// Step 4: Gemma. Implemented in Ai. A connection failure (Ollama or Docker not running) surfaces as
// HttpRequestException: the pipeline reads it as "Gemma offline" and the job waits (spec 9). A timeout after the
// retry, or output that cannot be used, comes back as NeedsReview(BadOutput). No stray OperationCanceledException.
public interface IFileAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken ct);
}

public interface INameBuilder
{
    // <outputRoot>\Receipts|Screenshots|Documents|Other\<categoryFolder>
    string BuildTargetFolder(string outputRoot, FileKind kind, string categoryFolder);
    // The name from spec 5.5; targetFolder is needed to keep the whole path under the length limit.
    string BuildFileName(Analysis analysis, SourceFileInfo info, string targetFolder);
    // fileName, or fileName with -2, -3 and so on before the extension while that name is taken. The pipeline's
    // check is TempFiles.IsTaken, so a name whose temp copy still exists counts as taken (spec 5.5).
    string ResolveClash(string targetFolder, string fileName, Func<string, bool> isTaken);
}

public interface ISafeMover
{
    // Handles every action kind: move, copy, undo_move and delete_copy.
    Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct);
    // Startup: resolves each pending action as in spec 6.5 and reports the outcome for logging.
    Task<IReadOnlyList<RecoveredAction>> RecoverPendingAsync(CancellationToken ct);
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
    // Startup: jobs left processing by a crashed run return to queued (spec 5.7). Returns how many.
    Task<int> RequeueInterruptedAsync(CancellationToken ct);
    // How many jobs wait and how many are being worked on (the `queue.changed` event, spec 8.3).
    Task<QueueCounts> CountsAsync(CancellationToken ct);
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
    // The row of an earlier first filing of this content from this path that never finished (no done action);
    // a retry reuses it instead of adding another (spec 6.2).
    Task<FileRecord?> FindUnfinishedAsync(string sha256, string originalPath, CancellationToken ct);
    Task SaveAsync(FileRecord file, CancellationToken ct);
    Task SaveEmbeddingAsync(Guid id, float[] embedding, CancellationToken ct);
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
    // One transaction: the pending action, plus request.After applied to the file and its receipt row.
    // Null, with nothing written, while the file has a pending action or no longer matches request.Before.
    Task<long?> AppendPendingAsync(MoveRequest request, CancellationToken ct);
    // Done points the file at the action's destination; failed puts back the action's Before state.
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
    Task<bool> IsInBaselineAsync(int folderId, string path, CancellationToken ct);
    Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct);
    Task ClearBaselineAsync(int folderId, CancellationToken ct);
}

// %LOCALAPPDATA%\SlothBox\settings.json (spec 11), created with defaults and a new phone token on first use.
public interface ISettingsStore
{
    SlothBoxSettings Current { get; }
}

public interface IAppState
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
