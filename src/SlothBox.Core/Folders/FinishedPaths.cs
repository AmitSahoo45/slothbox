namespace SlothBox.Core.Folders;

// A file's size and last-write time, read from the folder listing without opening the file. The time keeps whole
// microseconds, the precision Postgres stores, so a stamp read back from the database compares equal.
public readonly record struct FileStamp(long Size, DateTime LastWriteUtc)
{
    public static FileStamp Of(FileInfo info)
    {
        var ticks = info.LastWriteTimeUtc.Ticks;
        return new FileStamp(info.Length, new DateTime(ticks - ticks % 10, DateTimeKind.Utc));
    }

    // Null when there is no file at the path.
    public static FileStamp? Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? Of(info) : null;
    }
}

// Spec 4.5: a path whose last job ended while its file stayed where it was (a copy-only original, an undone file,
// a skipped file). The sweep passes it over while the stamp is unchanged. FolderId is null for the Inbox.
public sealed record FinishedPath(string Path, int? FolderId, FileStamp Stamp);

public interface IFinishedPaths
{
    // Adds the path, or replaces its stamp and folder. A path of a folder that no longer exists is not recorded.
    Task RecordAsync(FinishedPath entry, CancellationToken ct);
    Task<IReadOnlyList<FinishedPath>> ListAsync(int? folderId, CancellationToken ct);
    Task ForgetAsync(IReadOnlyCollection<string> paths, CancellationToken ct);
}
