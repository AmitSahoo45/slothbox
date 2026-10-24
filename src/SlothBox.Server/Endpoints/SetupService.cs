using System.Globalization;
using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Server.Api;
using SlothBox.Server.Watching;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// First-run setup (spec 4.3 step 5, 4.5, 7.4.1): the output folder, the first watched folders and the checks. Setup is
// complete once the output folder is saved; the worker and the watchers wait for it. The App adds the known folders
// and the folder dialog; without them nothing is suggested and paths are typed.
public sealed class SetupService(
    ISettingsStore settings,
    IFolderRules rules,
    IWatchedFolderRepository folders,
    IAppState appState,
    StatusService status,
    WorkerState worker,
    SweepRequests sweeps,
    ILogger<SetupService> log,
    IKnownFolders? knownFolders = null,
    INativeDialogs? dialogs = null)
{
    public const string DefaultOutputRoot = @"D:\SlothBox";
    public const string CompletedKey = "setup_completed_at";

    private const string NoDialog = "The folder dialog isn't available. Type or paste the folder path instead.";

    private const string DatabaseOffline =
        "The database is offline, so SlothBox can't save your folders yet. Start Docker Desktop, then try again.";

    // One finish at a time: a second click waits, then finds setup finished.
    private readonly SemaphoreSlim _finishing = new(1, 1);

    public async Task<SetupDto> GetAsync(CancellationToken ct)
    {
        var outputRoot = settings.Current.OutputRoot;
        var complete = outputRoot.Length > 0;
        return new SetupDto(complete, complete ? outputRoot : DefaultOutputRoot,
            complete ? [] : await SuggestionsAsync(ct), await status.GetAsync(ct), dialogs is not null);
    }

    // Step 1, before anything is saved. Works while the database is down; finishing checks again.
    public async Task<OutputFolderCheckDto> CheckOutputAsync(string? path, CancellationToken ct) =>
        rules.CheckOutput((path ?? "").Trim(), await WatchedAsync(ct) ?? []) is { } reason
            ? new OutputFolderCheckDto(false, reason)
            : new OutputFolderCheckDto(true, null);

    // Everything is checked before anything is written, so a refused setup leaves nothing behind. A folder an earlier,
    // failed try already saved is kept as it is, so finishing again never adds it twice.
    public async Task<Outcome<SetupDto>> CompleteAsync(CompleteSetupRequest request, CancellationToken ct)
    {
        await _finishing.WaitAsync(ct);
        try
        {
            return await FinishAsync(request, ct);
        }
        finally
        {
            _finishing.Release();
        }
    }

    private async Task<Outcome<SetupDto>> FinishAsync(CompleteSetupRequest request, CancellationToken ct)
    {
        if (settings.Current.OutputRoot.Length > 0)
            return new Outcome<SetupDto>.Conflict(
                "Setup is already finished. The output folder can't be changed in this version.");
        // The folders and the setup record are saved in the database.
        if (await WatchedAsync(ct) is not { } watched)
            return new Outcome<SetupDto>.Conflict(DatabaseOffline);
        if (rules.CheckOutput((request.OutputRoot ?? "").Trim(), watched) is { } outputRefused)
            return new Outcome<SetupDto>.Refused(outputRefused);
        var output = Normalize(request.OutputRoot!);

        var adding = new List<(WatchedFolder Folder, IReadOnlyList<string> Baseline)>();
        foreach (var chosen in request.Folders ?? [])
        {
            var known = watched.Concat(adding.Select(add => add.Folder)).ToList();
            if (IsOneOf(chosen.Path, known))
                continue;
            var name = string.IsNullOrWhiteSpace(chosen.Label) ? chosen.Path : chosen.Label.Trim();
            var check = rules.Check((chosen.Path ?? "").Trim(), known, output);
            if (check is FolderCheck.Refused refused)
                return new Outcome<SetupDto>.Refused($"{name}: {refused.Reason}");
            var allowed = (FolderCheck.Allowed)check;
            var path = Normalize(chosen.Path!);
            // Setup always leaves the files already there (spec 4.5): they become the folder's baseline.
            if (FolderSweep.Candidates(path) is not { } files)
                return new Outcome<SetupDto>.Refused($"{name}: SlothBox can't open this folder.");
            var label = string.IsNullOrWhiteSpace(chosen.Label) ? Path.GetFileName(path) : chosen.Label.Trim();
            adding.Add((new WatchedFolder(0, path, label, allowed.IsCloud ? FolderMode.Copy : chosen.Mode,
                allowed.IsCloud, true), [.. files.Select(file => file.FullName)]));
        }
        // A chosen folder may contain the output folder by the watching rules; the output rules refuse that.
        if (rules.CheckOutput(output, [.. adding.Select(add => add.Folder)]) is { } insideChosen)
            return new Outcome<SetupDto>.Refused(insideChosen);
        try
        {
            Directory.CreateDirectory(Path.Combine(output, "Inbox"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Outcome<SetupDto>.Refused("SlothBox can't create this folder.");
        }

        foreach (var (folder, baseline) in adding)
            await folders.AddAsync(folder, baseline, ct);
        await appState.SetAsync(CompletedKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct);
        // Finish anyway: the queue starts paused, as if the user had paused it, before the worker can start.
        if (request.FinishAnyway)
            worker.Pause(PauseReason.User);
        // Last: once the output folder is saved, the worker and the watchers start (spec 4.3 step 5).
        settings.Update(current => current with { OutputRoot = output });
        sweeps.Request();
        log.LogInformation("First-run setup finished with {Count} new watched folder(s).", adding.Count);
        return new Outcome<SetupDto>.Done(await GetAsync(ct));
    }

    // Spec 8.2: the App's folder dialog. Without it, or when it fails, the page asks for a typed path (spec 9).
    public async Task<Outcome<PickFolderResultDto>> PickFolderAsync(CancellationToken ct)
    {
        if (dialogs is null)
            return new Outcome<PickFolderResultDto>.Conflict(NoDialog);
        try
        {
            return new Outcome<PickFolderResultDto>.Done(new PickFolderResultDto(await dialogs.PickFolderAsync(ct)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("The folder dialog failed: {Error}", e.Message);
            return new Outcome<PickFolderResultDto>.Conflict(NoDialog);
        }
    }

    // Spec 4.5 "Defaults": Downloads and Screenshots, when the rules accept them; a cloud one copies only.
    private async Task<IReadOnlyList<InitialFolderDto>> SuggestionsAsync(CancellationToken ct)
    {
        if (knownFolders is null)
            return [];
        var known = new List<WatchedFolder>(await WatchedAsync(ct) ?? []);
        var suggested = new List<InitialFolderDto>();
        foreach (var (label, path) in new[] { ("Downloads", knownFolders.Downloads), ("Screenshots", knownFolders.Screenshots) })
        {
            if (path is null || rules.Check(path, known, "") is not FolderCheck.Allowed allowed)
                continue;
            var full = Normalize(path);
            suggested.Add(new InitialFolderDto(full, label, allowed.Mode));
            known.Add(new WatchedFolder(0, full, label, allowed.Mode, allowed.IsCloud, true));
        }
        return suggested;
    }

    // The watched folders, or null while the database does not answer (the status reports why).
    private async Task<IReadOnlyList<WatchedFolder>?> WatchedAsync(CancellationToken ct)
    {
        try
        {
            return await folders.ListAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    // Whether the path names one of these folders; a path that is not a valid full one names none.
    private static bool IsOneOf(string? path, IEnumerable<WatchedFolder> known)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            return false;
        try
        {
            var full = Normalize(path);
            return known.Any(folder => string.Equals(Normalize(folder.Path), full, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
