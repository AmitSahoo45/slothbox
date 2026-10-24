using SlothBox.Core;

namespace SlothBox.Server.Api;

public sealed record ServiceStatusDto(bool Ready, string? Message);
public sealed record ModelStatusDto(string Name, bool Present, bool Loaded, bool OnGpu);
// Why the worker is paused: by the user, or by itself (spec 9). Null while it runs.
public enum PauseReason { User, DriveFull, GemmaOffline }
public record StatusDto(ServiceStatusDto Gemma, ModelStatusDto VisionModel, ModelStatusDto EmbeddingModel,
    ServiceStatusDto Database, QueueChangedEvent Queue, bool Paused, bool SetupComplete,
    double? AverageSecondsPerFileToday, PauseReason? PauseReason = null);
public sealed record MutationResultDto(bool Success, string? Message);
public sealed record ReceiptDto(string? Vendor, DateOnly? Date, decimal? Amount, string? Currency,
    PaymentMethod PaymentMethod);
public sealed record CategoryDto(int Id, FileKind Type, string Name, string Description, string Folder,
    bool IsSystem, int FileCount);

public record ActivityItemDto
{
    public required Guid FileId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required FileStatus Status { get; init; }
    public required FileKind Type { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string NewName { get; init; }
    public required string Folder { get; init; }
    public required FileOrigin Origin { get; init; }
    public required string? FolderLabel { get; init; }
    public required string? ThumbUrl { get; init; }
    public required bool CanUndo { get; init; }
}

public sealed record ActivityPageDto(IReadOnlyList<ActivityItemDto> Items, string? NextCursor);
public sealed record DayCountDto(DateOnly Date, int Count);
public sealed record CurrencyTotalDto(string Currency, decimal Amount, int Count);
public sealed record LazinessStatsDto(int FilesFiled, double MinutesSaved, int ReceiptsLogged,
    IReadOnlyList<CurrencyTotalDto> ReceiptTotals, int DuplicatesCaught, IReadOnlyList<DayCountDto> Days,
    LazinessSettings SecondsPerType);
public sealed record ActionDto(long Id, ActionKind Kind, string FromPath, string ToPath, ActionStatus Status,
    DateTimeOffset At, string? Error);
public sealed record FileDetailsDto(ActivityItemDto File, string OriginalName, string OriginalPath,
    string CurrentPath, string Caption, long SizeBytes, int? CategoryId, ReviewReason? ReviewReason,
    ReceiptDto? Receipt, IReadOnlyList<ActionDto> History);
public sealed record UndoResultDto(Guid FileId, bool Success, string? RestoredPath, string? Reason);
public sealed record UndoBatchResultDto(int Undone, IReadOnlyList<UndoResultDto> Results);
public sealed record UndoSincePreviewDto(int Actions);
public sealed record ReviewItemDto(FileDetailsDto File, ReviewReason Reason);
public sealed record SearchHitDto(FileDetailsDto File, double Score, bool MatchedWords, bool MatchedMeaning);
public sealed record SearchResultDto(IReadOnlyList<SearchHitDto> Items);
public sealed record ReceiptRowDto(Guid FileId, string? Vendor, DateOnly? Date, decimal? Amount,
    string? Currency, PaymentMethod PaymentMethod, int? CategoryId, string Category);
public sealed record CategoryTotalDto(int CategoryId, string Category, string Currency, decimal Amount);
public sealed record ReceiptsDto(string Month, IReadOnlyList<ReceiptRowDto> Items,
    IReadOnlyList<CurrencyTotalDto> Totals, IReadOnlyList<CategoryTotalDto> Categories);
public sealed record FolderDto(int Id, string Path, string Label, FolderMode Mode, bool IsCloud, bool Enabled,
    int FiledCount, int BaselineCount, bool Missing);
public sealed record FolderCheckDto(bool Accepted, string? Reason, bool CopyOnly, int ExistingFileCount);
public sealed record TidyResultDto(int FolderId, int Queued);
public sealed record PickFolderResultDto(string? Path);
public sealed record SettingsDto(bool StartAtLogin, LazinessSettings Laziness, string OllamaUrl);
public sealed record SetupDto(bool Complete, string OutputRoot, IReadOnlyList<InitialFolderDto> SuggestedFolders,
    StatusDto Status, bool NativePickerAvailable);
public sealed record PhoneDto(bool Enabled, string? Url, string? QrSvg, bool FirewallRulePresent);
public sealed record IgnoredFileDto(string Sha256, string FileName, DateTimeOffset At, string Reason);
public sealed record RebuildResultDto(int Queued);
public sealed record PhoneUploadResultDto(string FileName, bool Received);

public sealed record RefileRequest(FileKind Type, int CategoryId, string? Title = null, ReceiptDto? Receipt = null);
public sealed record UndoSinceRequest(DateTimeOffset Since);
public sealed record CreateCategoryRequest(FileKind Type, string Name, string Description, string Folder);
public sealed record UpdateCategoryRequest(string Name, string Description, string? Folder);
public sealed record CheckFolderRequest(string Path);
public sealed record CreateFolderRequest(string Path, FolderMode Mode, bool TidyExisting, string? Label = null);
// Path is Locate (spec 4.5): the folder's new place after it was renamed or moved.
public sealed record UpdateFolderRequest(bool? Enabled = null, FolderMode? Mode = null, string? Label = null,
    string? Path = null);
public sealed record UpdateSettingsRequest(bool StartAtLogin, LazinessSettings Laziness, string OllamaUrl);
public sealed record InitialFolderDto(string Path, string Label, FolderMode Mode);
public sealed record CompleteSetupRequest(string OutputRoot, IReadOnlyList<InitialFolderDto> Folders, bool FinishAnyway);
public sealed record PhoneUploadRequest(IFormFile File);

public sealed record FileFiledEvent : ActivityItemDto;
public sealed record FileReviewEvent : ActivityItemDto
{
    public required ReviewReason Reason { get; init; }
}
public sealed record FileDuplicateEvent : ActivityItemDto
{
    public required Guid DuplicateOf { get; init; }
}
public sealed record FileUndoneEvent(Guid FileId);
public sealed record FileRefiledEvent : ActivityItemDto;
public sealed record QueueChangedEvent(int Waiting, int Processing);
public sealed record StatusChangedEvent(ServiceStatusDto Gemma, ModelStatusDto VisionModel,
    ModelStatusDto EmbeddingModel, ServiceStatusDto Database, QueueChangedEvent Queue, bool Paused,
    bool SetupComplete, double? AverageSecondsPerFileToday, PauseReason? PauseReason = null)
    : StatusDto(Gemma, VisionModel, EmbeddingModel, Database, Queue, Paused, SetupComplete, AverageSecondsPerFileToday,
        PauseReason);
public sealed record FolderTidyProgressEvent(int FolderId, int Done, int Total);
