namespace SlothBox.Core.Gate;

// Pipeline step 2 (spec 5.1): skip rules, the stability wait, an exclusive open, then SHA-256.
public sealed class FileGate(
    IWatchedFolderRepository folders,
    IIgnoredHashes ignoredHashes,
    IFileRepository files,
    PipelineSettings settings,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IFileGate
{
    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".tmp", ".download", TempFiles.Suffix
    };

    private static readonly HashSet<string> ShellFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "Thumbs.db"
    };

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<GateResult> CheckAsync(IncomingFile file, CancellationToken ct)
    {
        var path = Path.GetFullPath(file.Path);
        if (IsTemporary(Path.GetFileName(path)))
            return new GateResult.Skip(GateSkipReason.Temporary);

        var info = new FileInfo(path);
        if (!info.Exists)
            return new GateResult.Skip(GateSkipReason.Missing);
        if (info.Attributes.HasFlag(FileAttributes.Hidden))
            return new GateResult.Skip(GateSkipReason.Hidden);
        if (info.Attributes.HasFlag(FileAttributes.System))
            return new GateResult.Skip(GateSkipReason.System);
        if (file.WatchedFolderId is { } folderId && await folders.IsInBaselineAsync(folderId, path, ct))
            return new GateResult.Skip(GateSkipReason.Baseline);

        var window = TimeSpan.FromSeconds(settings.StableSeconds);
        var seen = (info.Length, info.LastWriteTimeUtc);
        // A last write older than the window means size and time have already been unchanged for that long.
        if (DateTime.UtcNow - seen.LastWriteTimeUtc < window)
        {
            await _delay(window, ct);
            info.Refresh();
            if (!info.Exists)
                return new GateResult.Skip(GateSkipReason.Missing);
            if ((info.Length, info.LastWriteTimeUtc) != seen)
                return new GateResult.NotReady();
        }

        string sha256;
        try
        {
            // FileShare.None fails while any other program has the file open, and blocks writes while hashing.
            sha256 = await FileHash.Sha256Async(path, FileShare.None, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new GateResult.Skip(GateSkipReason.Missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GateResult.NotReady();
        }

        if (await ignoredHashes.ContainsAsync(sha256, ct))
            return new GateResult.Skip(GateSkipReason.Ignored);
        var filed = await files.FindFiledByHashAsync(sha256, ct);
        return filed is null ? new GateResult.Ready(sha256) : new GateResult.Duplicate(filed.Id, sha256);
    }

    private static bool IsTemporary(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || ShellFiles.Contains(name) ||
        TemporaryExtensions.Contains(Path.GetExtension(name));
}
