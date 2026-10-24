using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Server.Watching;

namespace SlothBox.Tests.Watching;

// Spec 4.5 "Safety sweep" and the finished-paths rule.
public sealed class FolderSweepTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly InMemoryFolders _folders = new();
    private readonly InMemoryFinishedPaths _finished = new();
    private readonly RecordingQueue _queue = new();
    private readonly FixedSettings _settings;

    public FolderSweepTests()
    {
        _settings = new FixedSettings(new SlothBoxSettings { OutputRoot = _temp.PathOf("output") });
        Directory.CreateDirectory(_temp.PathOf("output", "Inbox"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FolderSweep Sweep(Func<string, IReadOnlyList<FileInfo>?>? list = null) =>
        new(_folders, _finished, _queue, _settings, list);

    private WatchedFolder AddFolder(int id, string name, bool enabled = true, FolderMode mode = FolderMode.Move)
    {
        var path = _temp.PathOf(name);
        Directory.CreateDirectory(path);
        var folder = new WatchedFolder(id, path, name, mode, false, enabled);
        _folders.Folders.Add(folder);
        return folder;
    }

    [Fact]
    public async Task A_new_file_is_queued_with_its_folder()
    {
        var folder = AddFolder(1, "downloads");
        var path = _temp.Write(Path.Combine("downloads", "bill.pdf"), "bill");

        (await Sweep().RunAsync(Ct)).ShouldBe(1);

        var job = _queue.Queued.ShouldHaveSingleItem();
        job.Path.ShouldBe(path);
        job.Origin.ShouldBe(FileOrigin.Folder);
        job.WatchedFolderId.ShouldBe(folder.Id);
    }

    // Spec 13.3 case 17, first half.
    [Fact]
    public async Task A_file_in_the_folders_baseline_is_never_queued()
    {
        AddFolder(1, "downloads");
        var path = _temp.Write(Path.Combine("downloads", "old.pdf"), "old");
        _folders.Baselines[1] = [path.ToUpperInvariant()];

        (await Sweep().RunAsync(Ct)).ShouldBe(0);
        (await Sweep().RunAsync(Ct)).ShouldBe(0);

        _queue.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_finished_file_with_the_same_size_and_time_is_passed_over()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var path = _temp.Write(Path.Combine("camera", "photo.jpg"), "photo");
        _finished.Entries[path] = new FinishedPath(path, 1, FileStamp.Of(new FileInfo(path)));

        (await Sweep().RunAsync(Ct)).ShouldBe(0);

        _finished.Entries.ShouldContainKey(path);
    }

    [Fact]
    public async Task A_finished_file_that_changed_since_is_queued_again()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var path = _temp.Write(Path.Combine("camera", "photo.jpg"), "photo");
        _finished.Entries[path] = new FinishedPath(path, 1, FileStamp.Of(new FileInfo(path)));
        File.AppendAllText(path, " edited");

        (await Sweep().RunAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_finished_file_with_a_new_time_but_the_same_size_is_queued_again()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var path = _temp.Write(Path.Combine("camera", "photo.jpg"), "photo");
        _finished.Entries[path] = new FinishedPath(path, 1, FileStamp.Of(new FileInfo(path)));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));

        (await Sweep().RunAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_finished_entry_whose_file_has_gone_is_dropped()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var gone = _temp.PathOf("camera", "gone.jpg");
        var kept = _temp.Write(Path.Combine("camera", "kept.jpg"), "kept");
        _finished.Entries[gone] = new FinishedPath(gone, 1, new FileStamp(4, DateTime.UtcNow));
        _finished.Entries[kept] = new FinishedPath(kept, 1, FileStamp.Of(new FileInfo(kept)));

        (await Sweep().RunAsync(Ct)).ShouldBe(0);

        _finished.Entries.Keys.ShouldBe([kept]);
    }

    // A job that ends while the sweep runs records its path; that entry is not taken for a file that has gone.
    [Fact]
    public async Task A_file_finished_while_the_sweep_runs_is_neither_queued_nor_forgotten()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var path = _temp.PathOf("camera", "IMG_0900.jpg");
        _finished.OnList = () =>
        {
            if (File.Exists(path))
                return;
            File.WriteAllText(path, "photo");
            _finished.Entries[path] = new FinishedPath(path, 1, FileStamp.Of(new FileInfo(path)));
        };

        (await Sweep().RunAsync(Ct)).ShouldBe(0);

        _finished.Entries.ShouldContainKey(path);
    }

    // No access, or a cloud provider error: a failed listing says nothing about the files in the folder.
    [Fact]
    public async Task A_folder_that_cannot_be_listed_is_left_alone()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var kept = _temp.Write(Path.Combine("camera", "kept.jpg"), "kept");
        _finished.Entries[kept] = new FinishedPath(kept, 1, FileStamp.Of(new FileInfo(kept)));
        _temp.Write(Path.Combine("camera", "new.jpg"), "new");

        (await Sweep(list: _ => null).RunAsync(Ct)).ShouldBe(0);

        _finished.Entries.ShouldContainKey(kept);
    }

    [Fact]
    public async Task A_finished_entry_is_kept_while_its_file_is_still_there()
    {
        AddFolder(1, "camera", mode: FolderMode.Copy);
        var kept = _temp.Write(Path.Combine("camera", "kept.jpg"), "kept");
        _finished.Entries[kept] = new FinishedPath(kept, 1, FileStamp.Of(new FileInfo(kept)));

        (await Sweep(list: _ => []).RunAsync(Ct)).ShouldBe(0);

        _finished.Entries.ShouldContainKey(kept);
    }

    // A new folder's baseline is never recorded from a listing that failed.
    [Fact]
    public void The_candidates_of_a_folder_that_cannot_be_listed_are_unknown() =>
        FolderSweep.Candidates(_temp.PathOf("missing")).ShouldBeNull();

    [Fact]
    public async Task Switched_off_and_missing_folders_are_not_swept()
    {
        AddFolder(1, "off", enabled: false);
        _temp.Write(Path.Combine("off", "a.pdf"), "a");
        var missing = AddFolder(2, "missing");
        Directory.Delete(missing.Path);

        (await Sweep().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Files_the_gate_always_skips_are_not_queued()
    {
        AddFolder(1, "downloads");
        _temp.Write(Path.Combine("downloads", "movie.mkv.crdownload"), "partial");
        _temp.Write(Path.Combine("downloads", "~$report.docx"), "owner file");
        _temp.Write(Path.Combine("downloads", "desktop.ini"), "[.ShellClassInfo]");
        var hidden = _temp.Write(Path.Combine("downloads", "notes.txt"), "notes");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        (await Sweep().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Files_in_subfolders_are_not_queued()
    {
        AddFolder(1, "downloads");
        _temp.Write(Path.Combine("downloads", "project", "notes.txt"), "notes");

        (await Sweep().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_Inbox_is_swept_for_phone_uploads()
    {
        var path = _temp.Write(Path.Combine("output", "Inbox", "phone-20261024-101010-a1b2c3.jpg"), "photo");

        (await Sweep().RunAsync(Ct)).ShouldBe(1);

        var job = _queue.Queued.ShouldHaveSingleItem();
        job.Path.ShouldBe(path);
        job.Origin.ShouldBe(FileOrigin.Phone);
        job.WatchedFolderId.ShouldBeNull();
    }

    [Fact]
    public async Task The_Inbox_is_left_alone_while_the_phone_page_is_off()
    {
        _settings.Current = _settings.Current with { Phone = new PhoneSettings(Enabled: false) };
        _temp.Write(Path.Combine("output", "Inbox", "phone-20261024-101010-a1b2c3.jpg"), "photo");

        (await Sweep().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_watchers_follow_the_switched_on_folders_and_the_Inbox()
    {
        var on = AddFolder(1, "downloads");
        AddFolder(2, "off", enabled: false);

        var targets = await Sweep().TargetsAsync(Ct);

        targets.ShouldBe([new WatchTarget(1, on.Path), new WatchTarget(null, _temp.PathOf("output", "Inbox"))]);
    }

    public void Dispose() => _temp.Dispose();
}
