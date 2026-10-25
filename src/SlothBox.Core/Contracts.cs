using SlothBox.Core.Folders;

namespace SlothBox.Core;

public interface IFolderRules
{
    FolderCheck Check(string path, IReadOnlyList<WatchedFolder> existing, string outputRoot);
    // First-run setup (spec 7.4.1 step 1): why SlothBox may not sort files into this folder, or null when it may.
    string? CheckOutput(string path, IReadOnlyList<WatchedFolder> watched);
}

public interface IPlatformPaths
{
    IReadOnlyList<string> SystemFolders { get; }
    IReadOnlyList<string> CloudRoots { get; }
    bool IsDriveRoot(string path);
    bool IsNetworkPath(string path);
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
// HttpRequestException with no status code: the pipeline reads it as "Gemma offline" and the job waits (spec 9).
// An error status from Ollama, a timeout after the retry, or output that cannot be used comes back as
// NeedsReview(BadOutput), so one file cannot hold the queue. No stray OperationCanceledException.
public interface IFileAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken ct);
}

// Spec 4.3 step 7: is Ollama answering, and which models are pulled and loaded? Implemented in Ai.
public interface IOllamaStatus
{
    Task<GemmaState> CheckAsync(CancellationToken ct);
}

// Opens a file with its default app, or shows it selected in Explorer (spec 7.4.9). Callers check the path first.
public interface IFileLauncher
{
    void Open(string path);
    void Reveal(string path);
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
    // The sweep's enqueue (spec 4.5): nothing is queued while the path is a finished path with this stamp, checked in
    // the same statement, so a job that ends while the sweep runs is not queued again. True when a job was added.
    Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, FileStamp stamp, CancellationToken ct);
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
    // How many actions UndoSinceAsync would try to reverse (spec 7.4.2 confirm dialog). Files changed since filing
    // are refused when the undo runs, so it can undo fewer.
    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct);
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

// Windows Firewall, implemented in App: whether an inbound rule with this display name exists (spec 7.4.6).
public interface IFirewallRules
{
    bool Exists(string displayName);
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
    // Adds a category with no id, or saves a changed one. The editing rules (A22) are the caller's.
    Task<Category> SaveAsync(Category category, CancellationToken ct);
    // Removes a user category nothing refers to; false, with nothing removed, while anything does.
    Task<bool> DeleteEmptyAsync(int id, CancellationToken ct);
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
    // A change that moves nothing (spec 6.6): `after` is applied to the file and its receipt row under the same
    // per-file lock and checks as AppendPendingAsync. False, with nothing written, while the file has a pending
    // action, is no longer at `path`, or no longer matches `before`.
    Task<bool> UpdateFieldsAsync(Guid fileId, string path, FileSnapshot before, FileSnapshot after,
        CancellationToken ct);
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
    // A new folder and its baseline in one transaction: no sweep can see the folder without its baseline (4.5).
    Task<WatchedFolder> AddAsync(WatchedFolder folder, IReadOnlyList<string> baseline, CancellationToken ct);
    Task RemoveAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetBaselineAsync(int folderId, CancellationToken ct);
    Task<bool> IsInBaselineAsync(int folderId, string path, CancellationToken ct);
    Task SetBaselineAsync(int folderId, IReadOnlyList<string> paths, CancellationToken ct);
    Task ClearBaselineAsync(int folderId, CancellationToken ct);
    // Locate (spec 4.5): the folder moves to a new path with a new baseline; its finished paths follow by file name.
    // Null when the folder no longer exists.
    Task<WatchedFolder?> RelocateAsync(int id, string path, bool isCloud, FolderMode mode,
        IReadOnlyList<string> baseline, CancellationToken ct);
}

// %LOCALAPPDATA%\SlothBox\settings.json (spec 11), created with defaults and a new phone token on first use.
public interface ISettingsStore
{
    SlothBoxSettings Current { get; }
    // Saves a change (setup, the Settings page, a new phone link); Current has it at once. Returns the saved settings.
    SlothBoxSettings Update(Func<SlothBoxSettings, SlothBoxSettings> change);
}

public interface IAppState
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
