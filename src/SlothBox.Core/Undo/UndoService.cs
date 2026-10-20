namespace SlothBox.Core.Undo;

// Spec 6.6. Undo reverses one action at a time, newest first, with the safe-move steps.
public sealed class UndoService(
    IActionLog log,
    IFileRepository files,
    IReceiptRepository receipts,
    ISafeMover mover) : IUndoService
{
    private const string FileChanged = "The file changed or moved after SlothBox filed it.";
    private const string FileOpen = "The file is open in another program. Close it and try again.";
    private const string NothingToUndo = "There is nothing to undo.";

    public async Task<UndoResult> UndoFileAsync(Guid fileId, CancellationToken ct) =>
        Live(await log.GetForFileAsync(fileId, ct)).FirstOrDefault() is { } action
            ? await UndoAsync(action, ct)
            : Refused(fileId, NothingToUndo);

    public async Task<UndoResult> UndoLastAsync(CancellationToken ct) =>
        Live(await log.GetDoneSinceAsync(DateTimeOffset.UnixEpoch, ct)).FirstOrDefault() is { } action
            ? await UndoAsync(action, ct)
            : Refused(Guid.Empty, NothingToUndo);

    public async Task<IReadOnlyList<UndoResult>> UndoSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var results = new List<UndoResult>();
        var stuck = new HashSet<Guid>();
        foreach (var action in Live(await log.GetDoneSinceAsync(since, ct)))
        {
            // Once one of a file's actions cannot be undone, its older actions stay as they are.
            if (stuck.Contains(action.FileId))
                continue;
            var result = await UndoAsync(action, ct);
            if (!result.Succeeded)
                stuck.Add(action.FileId);
            results.Add(result);
        }
        return results;
    }

    // Done moves and copies not yet reversed, newest first. Each undo action reversed the newest
    // still-standing move or copy of its file that came before it.
    private static IEnumerable<FileAction> Live(IReadOnlyList<FileAction> actions)
    {
        var undos = new Dictionary<Guid, int>();
        foreach (var action in actions.Where(a => a.Status == ActionStatus.Done).OrderByDescending(a => a.Id))
        {
            var pending = undos.GetValueOrDefault(action.FileId);
            if (action.Kind is ActionKind.UndoMove or ActionKind.DeleteCopy)
                undos[action.FileId] = pending + 1;
            else if (pending > 0)
                undos[action.FileId] = pending - 1;
            else
                yield return action;
        }
    }

    private async Task<UndoResult> UndoAsync(FileAction action, CancellationToken ct)
    {
        var file = await files.GetAsync(action.FileId, ct);
        if (file is null)
            return Refused(action.FileId, FileChanged);
        try
        {
            if (!File.Exists(file.CurrentPath) ||
                await FileHash.Sha256Async(file.CurrentPath, FileShare.Read, ct) != action.Sha256)
                return Refused(action.FileId, FileChanged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(action.FileId, FileOpen);
        }

        var before = new FileSnapshot(file.Status, file.Type, file.CategoryId, file.Title, file.ReviewReason,
            await receipts.GetAsync(file.Id, ct));
        // Undoing a first filing takes the file out of SlothBox; the action log then ignores its hash.
        var after = action.Before ?? before with { Status = FileStatus.Undone, ReviewReason = null };
        // A copy-only folder still holds the original, so only SlothBox's copy is deleted (the mover checks the
        // original is still there, unchanged).
        var request = action.Kind == ActionKind.Copy
            ? new MoveRequest(file.Id, ActionKind.DeleteCopy, file.CurrentPath, action.FromPath, action.Sha256,
                before, after)
            : new MoveRequest(file.Id, ActionKind.UndoMove, file.CurrentPath, FreePath(action.FromPath),
                action.Sha256, before, after);

        var moved = await mover.MoveAsync(request, ct);
        return new UndoResult(file.Id, moved.Succeeded, moved.Succeeded ? request.To : null, moved.Error);
    }

    // "name (restored).ext" when the original path is taken, then "name (restored 2).ext" and so on.
    private static string FreePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder,
                n == 1 ? $"{name} (restored){extension}" : $"{name} (restored {n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static UndoResult Refused(Guid fileId, string reason) => new(fileId, false, null, reason);
}
