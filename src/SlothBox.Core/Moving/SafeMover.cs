namespace SlothBox.Core.Moving;

// Spec 6.4 and 6.5. Every change is logged as pending before any file is touched, then marked done or failed.
public sealed class SafeMover(
    IActionLog log,
    IFileRepository files,
    IJobQueue jobs,
    Func<string, string, bool>? sameVolume = null) : ISafeMover
{
    private const string TempSuffix = ".slothbox-tmp";
    private const string ChangedMeanwhile = "This file changed while SlothBox was working on it. Try again.";
    private const string OriginalMissing =
        "The original is missing or changed in its folder, so SlothBox kept its copy.";

    private readonly Func<string, string, bool> _sameVolume = sameVolume ?? SameVolume;

    public async Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct)
    {
        // No log, no move: if the pending row cannot be written, the exception escapes before any file is touched.
        if (await log.AppendPendingAsync(request, ct) is not { } actionId)
            return new MoveResult(false, null, null, ChangedMeanwhile);

        // Once logged, the change runs to the end even if the caller gives up, so no file is left half-changed.
        var error = await ChangeFilesAsync(request, CancellationToken.None);
        await log.CompleteAsync(actionId, error is null ? ActionStatus.Done : ActionStatus.Failed, error,
            CancellationToken.None);
        return new MoveResult(error is null, actionId, error is null ? request.To : null, error);
    }

    public async Task<IReadOnlyList<RecoveredAction>> RecoverPendingAsync(CancellationToken ct)
    {
        var recovered = new List<RecoveredAction>();
        foreach (var action in await log.GetPendingAsync(ct))
            recovered.Add(await RecoverAsync(action, ct));
        return recovered;
    }

    private async Task<string?> ChangeFilesAsync(MoveRequest request, CancellationToken ct)
    {
        try
        {
            return request.Kind == ActionKind.DeleteCopy
                ? await DeleteCopyAsync(request.From, request.To, request.Sha256, ct)
                : await TransferAsync(request, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private async Task<string?> TransferAsync(MoveRequest request, CancellationToken ct)
    {
        if (File.Exists(request.To))
            return "A file already exists at the destination.";
        Directory.CreateDirectory(Path.GetDirectoryName(request.To)!);
        var keepOriginal = request.Kind == ActionKind.Copy;
        if (!keepOriginal && _sameVolume(request.From, request.To))
        {
            File.Move(request.From, request.To);
            return null;
        }

        // Copies, and moves between drives, go through a temp file that must match the recorded hash.
        var temp = request.To + TempSuffix;
        try
        {
            File.Copy(request.From, temp, overwrite: true);
            if (await FileHash.Sha256Async(temp, FileShare.Read, ct) != request.Sha256)
            {
                DeleteFile(temp);
                return "The copy does not match the hash recorded for the original.";
            }
            File.Move(temp, request.To);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (keepOriginal)
            return null;
        try
        {
            DeleteFile(request.From);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The original stays, so the verified copy goes: nothing has moved.
            try
            {
                DeleteFile(request.To);
            }
            catch (Exception leftover) when (leftover is IOException or UnauthorizedAccessException)
            {
                return $"The original could not be removed: {e.Message} SlothBox's copy is still at {request.To}.";
            }
            return $"The original could not be removed: {e.Message}";
        }
    }

    // Removes SlothBox's own copy, only while it still has its hash and the original is still in its folder,
    // unchanged: otherwise the copy may be the only one left.
    private static async Task<string?> DeleteCopyAsync(string copy, string original, string sha256,
        CancellationToken ct)
    {
        if (!File.Exists(copy))
            return null;
        if (!await HasHashAsync(original, sha256, ct))
            return OriginalMissing;
        if (await FileHash.Sha256Async(copy, FileShare.Read, ct) != sha256)
            return "The copy changed after SlothBox filed it.";
        DeleteFile(copy);
        return null;
    }

    private async Task<RecoveredAction> RecoverAsync(FileAction action, CancellationToken ct)
    {
        try
        {
            if (action.Kind == ActionKind.DeleteCopy)
                return await ResolveAsync(action,
                    await DeleteCopyAsync(action.FromPath, action.ToPath, action.Sha256, ct), requeue: false, ct);

            var temp = action.ToPath + TempSuffix;
            if (await HasHashAsync(action.ToPath, action.Sha256, ct))
            {
                TryDelete(temp);
                // The move had finished. An original edited since it was hashed is not SlothBox's to delete.
                if (action.Kind != ActionKind.Copy && await HasHashAsync(action.FromPath, action.Sha256, ct))
                    DeleteFile(action.FromPath);
                return await ResolveAsync(action, null, requeue: false, ct);
            }
            if (File.Exists(action.FromPath))
            {
                // Only SlothBox's partial copy is removed; a file at the destination may belong to the user.
                TryDelete(temp);
                var firstFiling = action.Before is null && action.Kind is ActionKind.Move or ActionKind.Copy;
                return await ResolveAsync(action, "The move was interrupted before it finished.", firstFiling, ct);
            }
            return await ResolveAsync(action, "Both the original and the destination are missing.",
                requeue: false, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file is locked: the action stays pending and is tried again on the next start.
            return new RecoveredAction(action.Id, action.FileId, ActionStatus.Pending, e.Message, false);
        }
    }

    private async Task<RecoveredAction> ResolveAsync(FileAction action, string? error, bool requeue,
        CancellationToken ct)
    {
        var status = error is null ? ActionStatus.Done : ActionStatus.Failed;
        await log.CompleteAsync(action.Id, status, error, ct);
        var requeued = false;
        if (requeue && await files.GetAsync(action.FileId, ct) is { } file)
        {
            await jobs.EnqueueAsync(
                new IncomingFile(action.FromPath, file.Origin, file.WatchedFolderId, DateTimeOffset.UtcNow), ct);
            requeued = true;
        }
        return new RecoveredAction(action.Id, action.FileId, status, error, requeued);
    }

    private static async Task<bool> HasHashAsync(string path, string sha256, CancellationToken ct) =>
        File.Exists(path) && await FileHash.Sha256Async(path, FileShare.Read, ct) == sha256;

    // Only for SlothBox's own copies and for an original whose verified copy is in place. A read-only file moves
    // on one drive, so it moves across drives too.
    private static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                DeleteFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SameVolume(string from, string to) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)),
            StringComparison.OrdinalIgnoreCase);
}
