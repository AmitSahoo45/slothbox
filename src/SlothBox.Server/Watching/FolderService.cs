using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Data.Folders;
using SlothBox.Server.Api;

namespace SlothBox.Server.Watching;

// The Folders page (spec 4.5, 7.4.6): check, add, switch, rename, locate, tidy and stop watching.
public sealed class FolderService(
    IWatchedFolderRepository folders,
    FolderQueries queries,
    IFinishedPaths finishedPaths,
    IFolderRules rules,
    ISettingsStore settings,
    IJobQueue jobs,
    TidyBatches tidy,
    SweepRequests sweeps)
{
    private const string CloudCopyOnly =
        "This is a cloud folder, so SlothBox only copies from it. Moving a file out would delete it from the cloud.";

    private const string CannotOpen = "SlothBox can't open this folder.";

    public async Task<IReadOnlyList<FolderDto>> ListAsync(CancellationToken ct)
    {
        var counts = await queries.CountsAsync(ct);
        return [.. (await folders.ListAsync(ct)).Select(folder => ToDto(folder, counts))];
    }

    // "N files are already here" counts the files the baseline would record.
    public async Task<FolderCheckDto> CheckAsync(string? path, CancellationToken ct) =>
        Check(path, await folders.ListAsync(ct)) switch
        {
            FolderCheck.Allowed allowed => new FolderCheckDto(true, null, allowed.IsCloud,
                FolderSweep.Candidates(Normalize(path!))?.Count ?? 0),
            FolderCheck.Refused refused => new FolderCheckDto(false, refused.Reason, false, 0),
            _ => throw new InvalidOperationException("Unknown folder check.")
        };

    public async Task<Outcome<FolderDto>> AddAsync(CreateFolderRequest request, CancellationToken ct)
    {
        var check = Check(request.Path, await folders.ListAsync(ct));
        if (check is FolderCheck.Refused refused)
            return new Outcome<FolderDto>.Refused(refused.Reason);
        var allowed = (FolderCheck.Allowed)check;
        var path = Normalize(request.Path);
        // Without its list of files a folder would get no baseline, and its existing files would be swept (D25).
        if (FolderSweep.Candidates(path) is not { } files)
            return new Outcome<FolderDto>.Refused(CannotOpen);
        var label = string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileName(path) : request.Label.Trim();
        var mode = allowed.IsCloud ? FolderMode.Copy : request.Mode;
        // The folder and its baseline go in together, so no sweep sees the folder without its baseline.
        var folder = await folders.AddAsync(new WatchedFolder(0, path, label, mode, allowed.IsCloud, true),
            [.. files.Select(file => file.FullName)], ct);
        if (request.TidyExisting)
            await TidyBaselineAsync(folder, ct);
        sweeps.Request();
        return new Outcome<FolderDto>.Done(await DtoAsync(folder, ct));
    }

    // Everything is checked before anything is written, so a refused change leaves the folder as it was.
    public async Task<Outcome<FolderDto>> UpdateAsync(int id, UpdateFolderRequest request, CancellationToken ct)
    {
        var all = await folders.ListAsync(ct);
        var folder = all.SingleOrDefault(f => f.Id == id);
        if (folder is null)
            return new Outcome<FolderDto>.NotFound();
        FolderCheck.Allowed? located = null;
        if (request.Path is not null)
        {
            // Locate is for a folder that was moved or renamed (spec 4.5); one still at its path keeps it.
            if (Directory.Exists(folder.Path))
                return new Outcome<FolderDto>.Conflict(
                    "The folder is still where it was. Locate only points a missing folder at its new place.");
            var check = Check(request.Path, all.Where(f => f.Id != id));
            if (check is FolderCheck.Refused refused)
                return new Outcome<FolderDto>.Refused(refused.Reason);
            located = (FolderCheck.Allowed)check;
        }
        var isCloud = located?.IsCloud ?? folder.IsCloud;
        var mode = request.Mode ?? (located is { IsCloud: true } ? FolderMode.Copy : folder.Mode);
        if (isCloud && mode == FolderMode.Move)
            return new Outcome<FolderDto>.Refused(CloudCopyOnly);
        if (request.Label is { } label && string.IsNullOrWhiteSpace(label))
            return new Outcome<FolderDto>.Refused("Give the folder a name.");
        if (located is not null)
        {
            var path = Normalize(request.Path!);
            if (FolderSweep.Candidates(path) is not { } files)
                return new Outcome<FolderDto>.Refused(CannotOpen);
            // D25 as when a folder is added: what is already at the new place is left alone, except the files
            // SlothBox finished at the old place, known by name and stamp.
            var finishedThere = (await finishedPaths.ListAsync(id, ct))
                .DistinctBy(entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(entry => Path.GetFileName(entry.Path), entry => entry.Stamp,
                    StringComparer.OrdinalIgnoreCase);
            var baseline = files.Where(file =>
                    !(finishedThere.TryGetValue(file.Name, out var stamp) && stamp == FileStamp.Of(file)))
                .Select(file => file.FullName).ToList();
            folder = await folders.RelocateAsync(id, path, isCloud, mode, baseline, ct);
            if (folder is null)
                return new Outcome<FolderDto>.NotFound();
        }
        var changed = folder with
        {
            Mode = mode, Label = request.Label?.Trim() ?? folder.Label, Enabled = request.Enabled ?? folder.Enabled
        };
        if (changed != folder)
            folder = await folders.SaveAsync(changed, ct);
        sweeps.Request();
        return new Outcome<FolderDto>.Done(await DtoAsync(folder, ct));
    }

    public async Task<Outcome<TidyResultDto>> TidyAsync(int id, CancellationToken ct)
    {
        var folder = (await folders.ListAsync(ct)).SingleOrDefault(f => f.Id == id);
        if (folder is null)
            return new Outcome<TidyResultDto>.NotFound();
        // A switched-off folder's jobs are skipped (spec 5.1), so its baseline would be cleared for nothing.
        if (!folder.Enabled)
            return new Outcome<TidyResultDto>.Conflict("Switch the folder on first.");
        if (!Directory.Exists(folder.Path))
            return new Outcome<TidyResultDto>.Conflict("Folder not found.");
        return new Outcome<TidyResultDto>.Done(new TidyResultDto(id, await TidyBaselineAsync(folder, ct)));
    }

    // Stopping keeps everything already filed where it is; jobs still queued from the folder are skipped (spec 5.1).
    public async Task<Outcome<MutationResultDto>> RemoveAsync(int id, CancellationToken ct)
    {
        if ((await folders.ListAsync(ct)).All(f => f.Id != id))
            return new Outcome<MutationResultDto>.NotFound();
        await folders.RemoveAsync(id, ct);
        sweeps.Request();
        return new Outcome<MutationResultDto>.Done(new MutationResultDto(true,
            "SlothBox stopped watching this folder. Files it already filed stay where they are."));
    }

    // Spec 4.5 "Tidy them now": the baseline files still there are queued, then the baseline is gone; the gate would
    // otherwise skip them as baseline. Undo since reverses the batch.
    private async Task<int> TidyBaselineAsync(WatchedFolder folder, CancellationToken ct)
    {
        var paths = (await folders.GetBaselineAsync(folder.Id, ct)).Where(File.Exists).ToList();
        await folders.ClearBaselineAsync(folder.Id, ct);
        tidy.Start(folder.Id, paths);
        foreach (var path in paths)
            await jobs.EnqueueAsync(new IncomingFile(path, FileOrigin.Folder, folder.Id, DateTimeOffset.UtcNow), ct);
        return paths.Count;
    }

    private FolderCheck Check(string? path, IEnumerable<WatchedFolder> others) =>
        rules.Check((path ?? "").Trim(), [.. others], settings.Current.OutputRoot);

    private async Task<FolderDto> DtoAsync(WatchedFolder folder, CancellationToken ct) =>
        ToDto(folder, await queries.CountsAsync(ct));

    private static FolderDto ToDto(WatchedFolder folder, IReadOnlyDictionary<int, FolderCounts> counts)
    {
        var count = counts.GetValueOrDefault(folder.Id) ?? new FolderCounts(0, 0);
        return new FolderDto(folder.Id, folder.Path, folder.Label, folder.Mode, folder.IsCloud, folder.Enabled,
            count.Filed, count.Baseline, !Directory.Exists(folder.Path));
    }

    // The rules accepted the path, so it is a full one.
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
