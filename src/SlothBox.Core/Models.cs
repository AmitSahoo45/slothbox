namespace SlothBox.Core;

public enum FileKind { Receipt, Screenshot, Document, Other, Unknown }
public enum FileOrigin { Folder, Phone }
public enum FolderMode { Move, Copy }
public enum FileStatus { Filed, Review, Duplicate, Undone }
public enum JobStatus { Queued, Processing, Done, Review, Failed, Skipped }
public enum ActionKind { Move, Copy, UndoMove, DeleteCopy }
public enum ActionStatus { Pending, Done, Failed }
public enum PaymentMethod { Upi, Card, Cash, Netbanking, Unknown }
public enum ReviewReason { BadOutput, UnknownType, MissingDate, MissingAmount, Heic, ScannedPdf, PasswordProtected }

public sealed record IncomingFile(string Path, FileOrigin Origin, int? WatchedFolderId, DateTimeOffset DetectedAt);
public sealed record WatchedFolder(int Id, string Path, string Label, FolderMode Mode, bool IsCloud, bool Enabled);
public sealed record Category(int Id, FileKind Type, string Name, string Description, string Folder, bool IsSystem);
public sealed record ReceiptData(string? Vendor, DateOnly? Date, decimal? Amount, string? Currency,
    PaymentMethod PaymentMethod = PaymentMethod.Unknown);
public sealed record Analysis(FileKind Type, string Category, string Title, string Caption, string Text,
    ReceiptData? Receipt);
public sealed record AnalysisRequest(string OriginalName, string FolderLabel, FileContent Content,
    IReadOnlyList<Category> Categories);
public sealed record SourceFileInfo(string Path, string OriginalName, string Extension, DateTimeOffset CreatedAt,
    long SizeBytes, string OutputRoot);
public sealed record Job(long Id, IncomingFile File, JobStatus Status, int Attempts, string? LastError, Guid? FileId);
public sealed record QueueCounts(int Waiting, int Processing);
public sealed record JobOutcome(JobStatus Status, Guid? FileId = null, string? Error = null);
public sealed record FileRecord(Guid Id, string Sha256, FileOrigin Origin, int? WatchedFolderId, string OriginalName,
    string OriginalPath, string CurrentPath, FileKind Type, int? CategoryId, string Title, string Caption,
    string ExtractedText, DateTimeOffset FileDate, long SizeBytes, FileStatus Status, ReviewReason? ReviewReason,
    Guid? DuplicateOf, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FileSnapshot(FileStatus Status, FileKind Type, int? CategoryId, string Title,
    ReviewReason? ReviewReason, ReceiptData? Receipt);
public sealed record FileAction(long Id, Guid FileId, ActionKind Kind, string FromPath, string ToPath, string Sha256,
    ActionStatus Status, string? Error, FileSnapshot? Before, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);
public sealed record IgnoredHash(string Sha256, string Reason, DateTimeOffset CreatedAt);

// What Ollama reports for one model: pulled, loaded, and loaded wholly on the GPU.
public sealed record ModelState(string Name, bool Present, bool Loaded, bool OnGpu);

// Spec 4.3 step 7: SlothBox files only while Ollama answers and both models are pulled.
public sealed record GemmaState(bool Reachable, string? Message, ModelState Vision, ModelState Embedding)
{
    public bool Ready => Reachable && Vision.Present && Embedding.Present;
}
public sealed record SearchQuery(string Query, FileKind? Type = null, int? CategoryId = null,
    DateOnly? From = null, DateOnly? To = null);
public sealed record SearchHit(FileRecord File, double Score, bool MatchedWords, bool MatchedMeaning);

public abstract record FolderCheck
{
    public sealed record Allowed(FolderMode Mode, bool IsCloud) : FolderCheck;
    public sealed record Refused(string Reason) : FolderCheck;
}

public abstract record GateResult
{
    public sealed record Ready(string Sha256) : GateResult;
    public sealed record NotReady : GateResult;
    public sealed record Skip(string Reason) : GateResult;
    public sealed record Duplicate(Guid ExistingFileId, string Sha256) : GateResult;
}

public abstract record FileContent
{
    public sealed record Image(byte[] JpegBytes) : FileContent;
    public sealed record Text(string Value) : FileContent;
    public sealed record NameOnly : FileContent;
    public sealed record Unsupported(ReviewReason Reason) : FileContent;
}

public abstract record AnalysisResult
{
    public sealed record Success(Analysis Analysis) : AnalysisResult;
    public sealed record NeedsReview(ReviewReason Reason, Analysis? PartialAnalysis = null) : AnalysisResult;
}

// One logged file change (spec 6.4). Before is the file's state just before it (null for a first filing);
// After, when set, is the state the file takes with it.
public sealed record MoveRequest(Guid FileId, ActionKind Kind, string From, string To, string Sha256,
    FileSnapshot? Before = null, FileSnapshot? After = null);
// Why a change did not happen. The worker pauses itself on DriveFull (spec 9).
public enum MoveFailure { ChangedMeanwhile, Invalid, DestinationTaken, HashMismatch, OriginalKept, DriveFull, IoError }
public sealed record MoveResult(bool Succeeded, long? ActionId, string? Destination, string? Error,
    MoveFailure? Failure = null);
public sealed record RecoveredAction(long ActionId, Guid FileId, ActionStatus Status, string? Error, bool Requeued);
public sealed record UndoResult(Guid FileId, bool Succeeded, string? RestoredPath, string? Error);
