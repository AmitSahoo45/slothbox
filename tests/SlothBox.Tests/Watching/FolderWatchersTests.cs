using SlothBox.Core;
using SlothBox.Server.Watching;

namespace SlothBox.Tests.Watching;

// Spec 4.5 and 5.1 step 1: the Windows watchers report new and renamed files at the top of each watched folder.
public sealed class FolderWatchersTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _temp = new();
    private readonly SweepRequests _sweeps = new();
    private readonly FolderWatchers _watchers;

    public FolderWatchersTests() => _watchers = new FolderWatchers(_sweeps);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder(string name)
    {
        var path = _temp.PathOf(name);
        Directory.CreateDirectory(path);
        return path;
    }

    // The next reported file with this name; earlier reports for other names are passed over.
    private async Task<IncomingFile> ReportedAsync(string name)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Wait);
        while (true)
        {
            var found = await _watchers.Found.ReadAsync(timeout.Token);
            if (Path.GetFileName(found.Path) == name)
                return found;
        }
    }

    [Fact]
    public async Task A_new_file_is_reported_with_its_folder()
    {
        var downloads = Folder("downloads");
        _watchers.Sync([new WatchTarget(7, downloads)]);

        File.WriteAllText(Path.Combine(downloads, "bill.pdf"), "bill");

        var found = await ReportedAsync("bill.pdf");
        found.Path.ShouldBe(Path.Combine(downloads, "bill.pdf"));
        found.Origin.ShouldBe(FileOrigin.Folder);
        found.WatchedFolderId.ShouldBe(7);
    }

    [Fact]
    public async Task A_finished_download_is_reported_under_its_final_name()
    {
        var downloads = Folder("downloads");
        _watchers.Sync([new WatchTarget(7, downloads)]);
        var partial = Path.Combine(downloads, "report.pdf.crdownload");
        File.WriteAllText(partial, "report");

        File.Move(partial, Path.Combine(downloads, "report.pdf"));

        (await ReportedAsync("report.pdf")).Path.ShouldBe(Path.Combine(downloads, "report.pdf"));
    }

    [Fact]
    public async Task The_Inbox_reports_phone_uploads()
    {
        var inbox = Folder("Inbox");
        _watchers.Sync([new WatchTarget(null, inbox)]);

        File.WriteAllText(Path.Combine(inbox, "phone-20261024-101010-a1b2c3.jpg"), "photo");

        var found = await ReportedAsync("phone-20261024-101010-a1b2c3.jpg");
        found.Origin.ShouldBe(FileOrigin.Phone);
        found.WatchedFolderId.ShouldBeNull();
    }

    [Fact]
    public async Task Files_in_subfolders_are_not_reported()
    {
        var downloads = Folder("downloads");
        Directory.CreateDirectory(Path.Combine(downloads, "project"));
        _watchers.Sync([new WatchTarget(7, downloads)]);

        File.WriteAllText(Path.Combine(downloads, "project", "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(downloads, "top.txt"), "top");

        var first = await ReportedAsync("top.txt");
        first.Path.ShouldBe(Path.Combine(downloads, "top.txt"));
        _watchers.Found.TryRead(out var other).ShouldBeFalse(other?.Path);
    }

    [Fact]
    public async Task A_folder_that_is_no_longer_a_target_is_no_longer_watched()
    {
        var downloads = Folder("downloads");
        _watchers.Sync([new WatchTarget(7, downloads)]);

        _watchers.Sync([]);
        File.WriteAllText(Path.Combine(downloads, "late.pdf"), "late");
        await Task.Delay(500, Ct);

        _watchers.IsWatching(7).ShouldBeFalse();
        _watchers.Found.TryRead(out _).ShouldBeFalse();
    }

    // Spec 4.5 "Folder problems": its watcher stops; once the folder is back, it is watched again.
    [Fact]
    public async Task A_folder_that_disappears_is_watched_again_once_it_is_back()
    {
        var downloads = Folder("downloads");
        var targets = new[] { new WatchTarget(7, downloads) };
        _watchers.Sync(targets);
        Directory.Delete(downloads);

        _watchers.Sync(targets);
        _watchers.IsWatching(7).ShouldBeFalse();

        Directory.CreateDirectory(downloads);
        _watchers.Sync(targets);
        _watchers.IsWatching(7).ShouldBeTrue();
        File.WriteAllText(Path.Combine(downloads, "back.pdf"), "back");
        (await ReportedAsync("back.pdf")).WatchedFolderId.ShouldBe(7);
    }

    [Fact]
    public void A_folder_whose_path_changed_is_watched_at_its_new_path()
    {
        var before = Folder("before");
        var after = Folder("after");
        _watchers.Sync([new WatchTarget(7, before)]);

        _watchers.Sync([new WatchTarget(7, after)]);

        _watchers.IsWatching(7).ShouldBeTrue();
        _watchers.WatchedPath(7).ShouldBe(after);
    }

    public void Dispose()
    {
        _watchers.Dispose();
        _temp.Dispose();
    }
}
