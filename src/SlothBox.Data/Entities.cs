using NpgsqlTypes;
using Pgvector;
using SlothBox.Core;

namespace SlothBox.Data;

public sealed class StoredCategory
{
    public int Id { get; set; }
    public FileKind Type { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StoredWatchedFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public FolderMode Mode { get; set; }
    public bool IsCloud { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class FolderBaseline
{
    public int FolderId { get; set; }
    public string Path { get; set; } = "";
}

// Spec 4.5: a file that stayed in its folder after its last job, with the size and last-write time it had then.
public sealed class StoredFinishedPath
{
    public string Path { get; set; } = "";
    public int? FolderId { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
}

public sealed class StoredFile
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = "";
    public FileOrigin Origin { get; set; }
    public int? WatchedFolderId { get; set; }
    public string OriginalName { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public FileKind Type { get; set; }
    public int? CategoryId { get; set; }
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public DateTimeOffset FileDate { get; set; }
    public long SizeBytes { get; set; }
    public FileStatus Status { get; set; }
    public ReviewReason? ReviewReason { get; set; }
    public Guid? DuplicateOf { get; set; }
    public Vector? Embedding { get; set; }
    public NpgsqlTsVector Search { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StoredReceipt
{
    public Guid FileId { get; set; }
    public string? Vendor { get; set; }
    public DateOnly? ReceiptDate { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
}

public sealed class StoredAction
{
    public long Id { get; set; }
    public Guid FileId { get; set; }
    public ActionKind Kind { get; set; }
    public string FromPath { get; set; } = "";
    public string ToPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public ActionStatus Status { get; set; }
    public string? Error { get; set; }
    public string? Before { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class StoredJob
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public FileOrigin Origin { get; set; }
    public int? WatchedFolderId { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid? FileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StoredIgnoredHash
{
    public string Sha256 { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StoredAppState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
