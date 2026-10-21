namespace SlothBox.Core.Moving;

// Spec 6.4 and 6.5. Every change is logged as pending before any file is touched, then marked done or failed.
public sealed class SafeMover(
    IActionLog log,
    IFileRepository files,
    IJobQueue jobs,
    Func<string, string, bool>? sameVolume = null,
    Func<string, string, CancellationToken, Task>? copy = null) : ISafeMover
{
    private const string ChangedMeanwhile = "This file changed while SlothBox was working on it. Try again.";
    private const string OriginalMissing =
        "The original is missing or changed in its folder, so SlothBox kept its copy.";

    private readonly Func<string, string, bool> _sameVolume = sameVolume ?? SameVolume;
    private readonly Func<string, string, CancellationToken, Task> _copy = copy ?? CopyNewAsync;

    private sealed record Problem(MoveFailure Failure, string Message);

    // Copies into a file that must not exist yet, from a source nobody may write to while it is read.
    public static async Task CopyNewAsync(string from, string to, CancellationToken ct)
    {
        const int bufferSize = 1 << 16;
        await using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var copy = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize,
                         FileOptions.Asynchronous))
            await source.CopyToAsync(copy, ct);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    public async Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct)
    {
        // A copy deletion names SlothBox's copy and the user's original; the same path for both would delete the
        // only copy there is.
        if (request.Kind == ActionKind.DeleteCopy && SamePath(request.From, request.To))
            return Failed(null, MoveFailure.Invalid,
                "The copy and the original are the same file, so nothing was deleted.");
        // No log, no move: if the pending row cannot be written, the exception escapes before any file is touched.
        if (await log.AppendPendingAsync(request, ct) is not { } actionId)
            return Failed(null, MoveFailure.ChangedMeanwhile, ChangedMeanwhile);

        // Once logged, the change runs to the end even if the caller gives up, so no file is left half-changed.
        var problem = await ChangeFilesAsync(request, actionId, CancellationToken.None);
        await log.CompleteAsync(actionId, problem is null ? ActionStatus.Done : ActionStatus.Failed, problem?.Message,
            CancellationToken.None);
        return problem is null
            ? new MoveResult(true, actionId, request.To, null)
            : Failed(actionId, problem.Failure, problem.Message);
    }

    public async Task<IReadOnlyList<RecoveredAction>> RecoverPendingAsync(CancellationToken ct)
    {
        var recovered = new List<RecoveredAction>();
        foreach (var action in await log.GetPendingAsync(ct))
            recovered.Add(await RecoverAsync(action, ct));
        return recovered;
    }

    private static MoveResult Failed(long? actionId, MoveFailure failure, string message) =>
        new(false, actionId, null, message, failure);

    private async Task<Problem?> ChangeFilesAsync(MoveRequest request, long actionId, CancellationToken ct)
    {
        try
        {
            return request.Kind == ActionKind.DeleteCopy
                ? await DeleteCopyAsync(request.From, request.To, request.Sha256, ct)
                : await TransferAsync(request, actionId, ct);
        }
        catch (IOException e)
        {
            return new Problem(IsDriveFull(e) ? MoveFailure.DriveFull : MoveFailure.IoError, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            return new Problem(MoveFailure.IoError, e.Message);
        }
    }

    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL on Windows; the errno ENOSPC elsewhere.
    private static bool IsDriveFull(IOException e) =>
        e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) or 28;

    private async Task<Problem?> TransferAsync(MoveRequest request, long actionId, CancellationToken ct)
    {
        if (File.Exists(request.To))
            return new Problem(MoveFailure.DestinationTaken, "A file already exists at the destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(request.To)!);
        var keepOriginal = request.Kind == ActionKind.Copy;
        if (!keepOriginal && _sameVolume(request.From, request.To))
        {
            File.Move(request.From, request.To);
            return null;
        }

        // Copies, and moves between drives, go through this action's own temp file, created exclusively, which
        // must match the recorded hash. A file already under that name is not this action's and is left alone.
        var temp = TempFiles.For(request.To, actionId);
        if (File.Exists(temp))
            return new Problem(MoveFailure.DestinationTaken, $"A file is already at {temp}.");
        try
        {
            await _copy(request.From, temp, ct);
            if (await FileHash.Sha256Async(temp, FileShare.Read, ct) != request.Sha256)
            {
                DeleteFile(temp);
                return new Problem(MoveFailure.HashMismatch,
                    "The copy does not match the hash recorded for the original.");
            }
            File.Move(temp, request.To);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return keepOriginal ? null : await RemoveOriginalAsync(request, ct);
    }

    // The original goes only while nobody has it open and it still has the recorded hash: the copy holds what that
    // hash describes, so anything written since is the user's newer work. When the original stays, the verified
    // copy is removed: nothing has moved.
    private static async Task<Problem?> RemoveOriginalAsync(MoveRequest request, CancellationToken ct)
    {
        Problem problem;
        try
        {
            if (await FileHash.Sha256Async(request.From, FileShare.None, ct) == request.Sha256)
            {
                DeleteFile(request.From);
                return null;
            }
            problem = new Problem(MoveFailure.OriginalKept, "The original changed while SlothBox was copying it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = new Problem(MoveFailure.OriginalKept, $"The original could not be removed: {e.Message}");
        }
        try
        {
            DeleteFile(request.To);
        }
        catch (Exception leftover) when (leftover is IOException or UnauthorizedAccessException)
        {
            return problem with { Message = $"{problem.Message} SlothBox's copy is still at {request.To}." };
        }
        return problem;
    }

    // Removes SlothBox's own copy, only while it still has its hash and the original is still in its folder,
    // unchanged: otherwise the copy may be the only one left.
    private static async Task<Problem?> DeleteCopyAsync(string copy, string original, string sha256,
        CancellationToken ct)
    {
        if (!File.Exists(copy))
            return null;
        if (!await HasHashAsync(original, sha256, ct))
            return new Problem(MoveFailure.OriginalKept, OriginalMissing);
        if (await FileHash.Sha256Async(copy, FileShare.Read, ct) != sha256)
            return new Problem(MoveFailure.HashMismatch, "The copy changed after SlothBox filed it.");
        DeleteFile(copy);
        return null;
    }

    private async Task<RecoveredAction> RecoverAsync(FileAction action, CancellationToken ct)
    {
        try
        {
            if (action.Kind == ActionKind.DeleteCopy)
                return await ResolveAsync(action,
                    (await DeleteCopyAsync(action.FromPath, action.ToPath, action.Sha256, ct))?.Message,
                    requeue: false, ct);

            // The temp file carries the action id, so recovery touches only this action's own copy.
            var temp = TempFiles.For(action.ToPath, action.Id);
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
            // The original is gone, so a complete temp copy is the only copy left: it finishes the move.
            if (await HasHashAsync(temp, action.Sha256, ct))
            {
                if (File.Exists(action.ToPath))
                    return await ResolveAsync(action,
                        $"The original is missing and another file is at the destination. SlothBox's copy is at {temp}.",
                        requeue: false, ct);
                File.Move(temp, action.ToPath);
                return await ResolveAsync(action, null, requeue: false, ct);
            }
            TryDelete(temp);
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

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
