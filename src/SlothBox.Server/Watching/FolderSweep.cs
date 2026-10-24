using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Core.Gate;

namespace SlothBox.Server.Watching;

// Spec 4.5 "Safety sweep": every enabled folder that exists, and the Inbox, are listed again. A file is queued unless
// it is in the folder's baseline, the gate would skip it by its name or attributes anyway, or its last job ended with
// the file staying put and its size and last-write time are unchanged (finished paths). Nothing is opened, so a
// cloud-only file is not downloaded.
public sealed class FolderSweep(IWatchedFolderRepository folders, IFinishedPaths finished, IJobQueue jobs,
    ISettingsStore settings, Func<string, IReadOnlyList<FileInfo>?>? list = null)
{
    // Lists a folder's top level; null when the listing failed. Tests swap it to make a listing fail.
    private readonly Func<string, IReadOnlyList<FileInfo>?> _list = list ?? (folder => List(folder));

    // How many files were queued.
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var queued = 0;
        foreach (var target in await TargetsAsync(ct))
            queued += await SweepAsync(target, ct);
        return queued;
    }

    // What the watchers should watch: every enabled folder, and the Inbox while the phone page is on.
    public async Task<IReadOnlyList<WatchTarget>> TargetsAsync(CancellationToken ct)
    {
        var targets = (await folders.ListAsync(ct)).Where(folder => folder.Enabled)
            .Select(folder => new WatchTarget(folder.Id, folder.Path)).ToList();
        var current = settings.Current;
        if (current.Phone.Enabled && current.OutputRoot.Length > 0)
            targets.Add(new WatchTarget(null, Path.Combine(current.OutputRoot, "Inbox")));
        return targets;
    }

    // The files at the top of a folder that the gate would not skip by name or attributes; null when the folder is
    // missing or cannot be listed. The baseline of a new or located folder is recorded from the same list.
    public static IReadOnlyList<FileInfo>? Candidates(string folder) =>
        List(folder) is { } files ? [.. files.Where(file => !FileGate.IsAlwaysSkipped(file))] : null;

    private async Task<int> SweepAsync(WatchTarget target, CancellationToken ct)
    {
        // Read before the listing: an entry a job records while the folder is listed is not taken for a gone file.
        var entries = (await finished.ListAsync(target.FolderId, ct))
            .ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        // A missing folder, or a listing that failed (no access, a cloud provider error), says nothing about its files.
        if (_list(target.Path) is not { } files)
            return 0;
        var baseline = target.FolderId is { } folderId
            ? new HashSet<string>(await folders.GetBaselineAsync(folderId, ct), StringComparer.OrdinalIgnoreCase)
            : [];
        var queued = 0;
        foreach (var file in files.Where(file => !FileGate.IsAlwaysSkipped(file)))
        {
            var stamp = FileStamp.Of(file);
            if (baseline.Contains(file.FullName) ||
                entries.TryGetValue(file.FullName, out var entry) && entry.Stamp == stamp)
                continue;
            // The queue checks the finished path again in the same statement: a job may have ended since the read.
            if (await jobs.EnqueueUnlessFinishedAsync(new IncomingFile(file.FullName,
                    target.FolderId is null ? FileOrigin.Phone : FileOrigin.Folder, target.FolderId,
                    DateTimeOffset.UtcNow), stamp, ct))
                queued++;
        }
        // Gone means not listed and not on disk either; reading a file's attributes downloads nothing.
        var listed = new HashSet<string>(files.Select(file => file.FullName), StringComparer.OrdinalIgnoreCase);
        var gone = entries.Keys.Where(path => !listed.Contains(path) && !File.Exists(path)).ToList();
        if (gone.Count > 0)
            await finished.ForgetAsync(gone, ct);
        return queued;
    }

    // Every file at the top of the folder, hidden and system ones included; watchers do not look into subfolders.
    // Null when the folder is missing or cannot be listed: an inaccessible folder must not read as an empty one.
    private static List<FileInfo>? List(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles("*",
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
