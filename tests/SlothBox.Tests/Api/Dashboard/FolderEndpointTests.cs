using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Api;
using SlothBox.Server.Watching;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

// The Folders page's API (spec 4.5, 7.4.6, 8.2) over a real database and temp folders.
[Collection(PostgresCollection.Name)]
public sealed class FolderEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewFolder(DashboardHarness h, string name, params string[] files)
    {
        var path = h.Moves.Temp.PathOf(name);
        Directory.CreateDirectory(path);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(path, file), $"content of {file}");
        return path;
    }

    private static async Task<FolderDto> AddAsync(DashboardHarness h, string path, bool tidy = false,
        FolderMode mode = FolderMode.Move)
    {
        using var response = await h.SendAsync(HttpMethod.Post, "/api/folders", new CreateFolderRequest(path, mode, tidy));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<FolderDto>();
    }

    private static async Task<FolderCheckDto> CheckAsync(DashboardHarness h, string path)
    {
        using var response = await h.SendAsync(HttpMethod.Post, "/api/folders/check", new CheckFolderRequest(path));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<FolderCheckDto>();
    }

    private static async Task<List<string>> QueuedAsync(DashboardHarness h)
    {
        await using var db = h.Moves.Db.CreateDbContext();
        return await db.Jobs.Where(job => job.Status == JobStatus.Queued).Select(job => job.Path).ToListAsync(Ct);
    }

    // Spec 13.3 case 17.
    [Fact]
    public async Task A_file_already_in_a_new_folder_is_never_queued_until_tidy_them_now_queues_it_once()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var path = NewFolder(h, "downloads", "old-bill.pdf");

        var folder = await AddAsync(h, path);

        folder.BaselineCount.ShouldBe(1);
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
        (await QueuedAsync(h)).ShouldBeEmpty();

        using var tidy = await h.SendAsync(HttpMethod.Post, $"/api/folders/{folder.Id}/tidy-existing");

        tidy.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await tidy.ReadAsync<TidyResultDto>()).ShouldBe(new TidyResultDto(folder.Id, 1));
        await h.Service<FolderSweep>().RunAsync(Ct);
        (await QueuedAsync(h)).ShouldBe([Path.Combine(path, "old-bill.pdf")]);
        (await h.GetAsync<List<FolderDto>>("/api/folders")).Single(f => f.Id == folder.Id).BaselineCount.ShouldBe(0);
    }

    [Fact]
    public async Task Adding_a_folder_with_tidy_them_now_queues_the_files_already_there()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var path = NewFolder(h, "scans", "a.pdf", "b.pdf");

        var folder = await AddAsync(h, path, tidy: true);

        (folder.Label, folder.Mode, folder.Enabled, folder.BaselineCount).ShouldBe(("scans", FolderMode.Move, true, 0));
        (await QueuedAsync(h)).ShouldBe([Path.Combine(path, "a.pdf"), Path.Combine(path, "b.pdf")], ignoreOrder: true);
    }

    // Spec 13.3 case 14: the same file reported by its watcher and found by the sweep is one job.
    [Fact]
    public async Task A_file_its_watcher_reported_and_the_sweep_found_is_one_job()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var path = NewFolder(h, "downloads");
        var folder = await AddAsync(h, path);
        var file = Path.Combine(path, "new.pdf");
        File.WriteAllText(file, "new");
        await h.Service<IJobQueue>().EnqueueAsync(new IncomingFile(file, FileOrigin.Folder, folder.Id,
            DateTimeOffset.UtcNow), Ct);

        await h.Service<FolderSweep>().RunAsync(Ct);

        (await QueuedAsync(h)).ShouldBe([file]);
    }

    [Fact]
    public async Task Optional_fields_can_be_left_out_of_folder_requests()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var path = JsonSerializer.Serialize(NewFolder(h, "scans"));

        using var added = await h.SendJsonAsync(HttpMethod.Post, "/api/folders",
            $$"""{"path":{{path}},"mode":"copy","tidyExisting":false}""");

        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
        var folder = await added.ReadAsync<FolderDto>();
        using var patched = await h.SendJsonAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}", """{"enabled":false}""");
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patched.ReadAsync<FolderDto>()).ShouldBe(folder with { Enabled = false });
    }

    [Fact]
    public async Task A_folder_the_rules_refuse_is_a_bad_request_with_the_reason()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Post, "/api/folders",
            new CreateFolderRequest(h.Pipeline.Settings.Current.OutputRoot, FolderMode.Move, false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "This is SlothBox's output folder. Watching it would make SlothBox refile its own files.");
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Checking_a_folder_tells_the_reason_or_how_many_files_are_already_there()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var downloads = NewFolder(h, "downloads", "a.pdf", "b.jpg", "c.pdf.crdownload");
        var cloud = NewFolder(h, Path.Combine("Dropbox", "Scans"), "scan.pdf");

        (await CheckAsync(h, downloads)).ShouldBe(new FolderCheckDto(true, null, false, 2));
        (await CheckAsync(h, cloud)).ShouldBe(new FolderCheckDto(true, null, true, 1));
        (await CheckAsync(h, Path.Combine(downloads, "missing"))).ShouldBe(
            new FolderCheckDto(false, "Folder not found.", false, 0));
    }

    [Fact]
    public async Task A_cloud_folder_is_watched_copy_only_and_cannot_be_switched_to_move()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folder = await AddAsync(h, NewFolder(h, Path.Combine("Dropbox", "Camera")));

        (folder.Mode, folder.IsCloud).ShouldBe((FolderMode.Copy, true));

        using var response = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}",
            new UpdateFolderRequest(Mode: FolderMode.Move));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_folder_list_shows_what_was_filed_from_each_folder_and_a_missing_folder()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);

        var watched = (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem();

        (watched.FiledCount, watched.BaselineCount, watched.Missing).ShouldBe((1, 0, false));

        Directory.Delete(watched.Path, recursive: true);

        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem().Missing.ShouldBeTrue();
    }

    [Fact]
    public async Task A_folder_is_renamed_switched_off_and_located_at_its_new_path()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var before = NewFolder(h, "before", "old.pdf");
        var folder = await AddAsync(h, before);
        var after = NewFolder(h, "after");
        File.Move(Path.Combine(before, "old.pdf"), Path.Combine(after, "old.pdf"));
        Directory.Delete(before);

        using var response = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}",
            new UpdateFolderRequest(Enabled: false, Label: "Scans", Path: after));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.ReadAsync<FolderDto>()).ShouldBe(folder with { Path = after, Label = "Scans", Enabled = false });

        using var on = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}", new UpdateFolderRequest(Enabled: true));
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The file that was already there is still known as such at the new path.
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_path_the_rules_refuse_is_a_bad_request()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folder = await AddAsync(h, NewFolder(h, "downloads"));
        Directory.Delete(folder.Path);

        using var response = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}",
            new UpdateFolderRequest(Path: h.Pipeline.Settings.Current.OutputRoot));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem().Path.ShouldBe(folder.Path);
    }

    // D25: files already at the new place are left alone, as when a folder is added with "Leave them".
    [Fact]
    public async Task Locating_a_folder_leaves_alone_the_files_already_at_its_new_path()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var before = NewFolder(h, "before", "old.pdf");
        var folder = await AddAsync(h, before);
        var after = NewFolder(h, "after", "unrelated.pdf");
        File.Move(Path.Combine(before, "old.pdf"), Path.Combine(after, "old.pdf"));
        Directory.Delete(before);

        using var response = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}",
            new UpdateFolderRequest(Path: after));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.ReadAsync<FolderDto>()).BaselineCount.ShouldBe(2);
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
    }

    // Locate is for a folder that was moved or renamed; one that is still there keeps its path.
    [Fact]
    public async Task Locate_is_refused_while_the_folder_is_still_where_it_was()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folder = await AddAsync(h, NewFolder(h, "downloads"));

        using var response = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}",
            new UpdateFolderRequest(Path: NewFolder(h, "elsewhere", "private.pdf")));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem().Path.ShouldBe(folder.Path);
    }

    [Fact]
    public async Task Tidy_them_now_waits_until_the_folder_is_switched_on()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folder = await AddAsync(h, NewFolder(h, "downloads", "old.pdf"));
        using var off = await h.SendAsync(HttpMethod.Patch, $"/api/folders/{folder.Id}", new UpdateFolderRequest(Enabled: false));

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/folders/{folder.Id}/tidy-existing");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await QueuedAsync(h)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PATCH", "/api/folders/42")]
    [InlineData("POST", "/api/folders/42/tidy-existing")]
    [InlineData("DELETE", "/api/folders/42")]
    public async Task An_unknown_folder_is_not_found(string method, string path)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = method == "PATCH"
            ? await h.SendAsync(HttpMethod.Patch, path, new UpdateFolderRequest(Label: "Scans"))
            : await h.SendAsync(new HttpMethod(method), path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stopping_watching_a_folder_keeps_the_files_it_already_filed()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var filed = await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);
        var folder = (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem();

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/folders/{folder.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldBeEmpty();
        File.Exists(filed.CurrentPath).ShouldBeTrue();
    }

    // Spec 4.5: a "Tidy them now" batch can be undone with Undo since.
    [Fact]
    public async Task A_tidy_batch_is_undone_with_undo_since()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var path = NewFolder(h, "downloads", "a.txt", "b.txt");
        var folder = await AddAsync(h, path);
        var since = DateTimeOffset.UtcNow;
        using var tidy = await h.SendAsync(HttpMethod.Post, $"/api/folders/{folder.Id}/tidy-existing");
        h.Pipeline.Analyzer.Answer = _ => new AnalysisResult.Success(Analyses.Notes);
        var queue = h.Service<IJobQueue>();
        while (await queue.ClaimNextAsync(Ct) is { } job)
            (await h.Pipeline.Pipeline().RunAsync(job, Ct)).ShouldBeOfType<PipelineResult.Filed>();
        Directory.GetFiles(path).ShouldBeEmpty();

        using var undo = await h.SendAsync(HttpMethod.Post, "/api/undo/since", new UndoSinceRequest(since));

        undo.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await undo.ReadAsync<UndoBatchResultDto>()).Undone.ShouldBe(2);
        Directory.GetFiles(path).Select(Path.GetFileName).ShouldBe(["a.txt", "b.txt"], ignoreOrder: true);
    }
}
