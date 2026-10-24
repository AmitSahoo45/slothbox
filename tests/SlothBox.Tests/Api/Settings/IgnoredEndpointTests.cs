using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Core.Gate;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Api;
using SlothBox.Server.Watching;
using SlothBox.Tests.Api.Dashboard;
using SlothBox.Tests.Data;
using SlothBox.Tests.Pipeline;

namespace SlothBox.Tests.Api.Settings;

// Settings > Ignored files (spec 4.5, 6.6, 7.4.8): undone files SlothBox will not file again, and Allow again.
[Collection(PostgresCollection.Name)]
public sealed class IgnoredEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UndoResultDto> UndoAsync(DashboardHarness h, Guid fileId)
    {
        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{fileId}/undo");
        var undone = await response.ReadAsync<UndoResultDto>();
        undone.Success.ShouldBeTrue(undone.Reason);
        return undone;
    }

    private static async Task<List<string>> QueuedAsync(DashboardHarness h)
    {
        await using var db = h.Moves.Db.CreateDbContext();
        return await db.Jobs.Where(job => job.Status == JobStatus.Queued).Select(job => job.Path).ToListAsync(Ct);
    }

    // What the worker does when the watcher reports the undone file back in its folder: the gate skips it for its
    // ignored hash and the path is recorded as finished, so the sweep passes over it (spec 4.5).
    private static async Task SkipAsIgnoredAsync(DashboardHarness h, string path, int folderId)
    {
        var job = PipelineHarness.JobFor(path, folderId: folderId);
        var stamp = FileStamp.Of(path);
        var result = await h.Pipeline.Pipeline().RunAsync(job, Ct);
        result.ShouldBe(new PipelineResult.Skipped(GateSkipReason.Ignored));
        await h.Service<FolderJobs>().RecordFinishedAsync(job, result, stamp, Ct);
    }

    // Each is named as Undo put it back: "(restored)" when its old place was taken (spec 6.6).
    [Fact]
    public async Task Undone_files_are_listed_newest_first_with_the_names_they_came_back_with()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var older = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        var newer = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);
        File.WriteAllText(older.OriginalPath, "another photo saved under the same name");
        await UndoAsync(h, older.Id);
        await UndoAsync(h, newer.Id);

        var ignored = await h.GetAsync<List<IgnoredFileDto>>("/api/ignored");

        ignored.Select(item => (item.Sha256, item.FileName, item.Reason)).ShouldBe([
            (newer.Sha256, "Screenshot_1.jpg", "undone"),
            (older.Sha256, "IMG_2041 (restored).jpg", "undone")
        ]);
        ignored[0].At.ShouldBeGreaterThanOrEqualTo(ignored[1].At);
    }

    [Fact]
    public async Task Allow_again_forgets_the_hash_and_its_finished_path_so_the_sweep_queues_the_file_again()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        var folderId = receipt.WatchedFolderId.ShouldNotBeNull();
        var restored = (await UndoAsync(h, receipt.Id)).RestoredPath.ShouldNotBeNull();
        await SkipAsIgnoredAsync(h, restored, folderId);
        // Another file SlothBox finished in the same folder keeps its record.
        var other = h.Pipeline.Watched("notes.txt", "notes");
        await h.Service<IFinishedPaths>().RecordAsync(
            new FinishedPath(other, folderId, FileStamp.Of(other)!.Value), Ct);
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
        var sweeps = h.Service<SweepRequests>();
        await sweeps.WaitAsync(TimeSpan.Zero, Ct);

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/ignored/{receipt.Sha256}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadAsync<MutationResultDto>()).Success.ShouldBeTrue();
        // The sweep runs at once instead of in up to five minutes.
        (await sweeps.WaitAsync(TimeSpan.Zero, Ct)).ShouldBeTrue();
        (await h.GetAsync<List<IgnoredFileDto>>("/api/ignored")).ShouldBeEmpty();
        (await h.Service<IIgnoredHashes>().ContainsAsync(receipt.Sha256, Ct)).ShouldBeFalse();
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(1);
        (await QueuedAsync(h)).ShouldBe([restored]);
        (await h.Service<IFinishedPaths>().ListAsync(folderId, Ct)).Select(entry => entry.Path).ShouldBe([other]);
    }

    [Fact]
    public async Task Allow_again_for_a_copy_only_original_forgets_where_it_stayed()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folderId = await h.Pipeline.AddFolderAsync(FolderMode.Copy);
        var photo = h.Pipeline.Watched("IMG_2041.jpg", "a copy-only photo");
        h.Pipeline.Analyzer.Answer = _ => new AnalysisResult.Success(Analyses.Notes);
        var job = PipelineHarness.JobFor(photo, folderId: folderId);
        var stamp = FileStamp.Of(photo);
        var filed = await h.Pipeline.Pipeline().RunAsync(job, Ct);
        await h.Service<FolderJobs>().RecordFinishedAsync(job, filed, stamp, Ct);
        var copy = filed.ShouldBeOfType<PipelineResult.Filed>().File;
        (await UndoAsync(h, copy.Id)).RestoredPath.ShouldBe(photo);

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/ignored/{copy.Sha256}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(1);
        (await QueuedAsync(h)).ShouldBe([photo]);
    }

    // A different file SlothBox filed from the same place since the undo keeps its record: it is not the undone file.
    [Fact]
    public async Task Allow_again_leaves_the_record_of_a_newer_file_at_the_same_path()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var folderId = await h.Pipeline.AddFolderAsync(FolderMode.Copy);
        var photo = h.Pipeline.Watched("IMG_2041.jpg", "the first photo");
        h.Pipeline.Analyzer.Answer = _ => new AnalysisResult.Success(Analyses.Notes);
        var first = (await FileCopyAsync(h, photo, folderId)).File;
        await UndoAsync(h, first.Id);
        File.WriteAllText(photo, "a different photo saved later under the same name");
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(1));
        h.Pipeline.Analyzer.Answer = _ => new AnalysisResult.Success(Analyses.Notes with { Title = "later notes" });
        await FileCopyAsync(h, photo, folderId);

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/ignored/{first.Sha256}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.Service<IFinishedPaths>().ListAsync(folderId, Ct)).Select(entry => entry.Path).ShouldBe([photo]);
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
    }

    // A copy-only filing, with the original recorded as finished as the worker does (spec 4.5).
    private static async Task<PipelineResult.Filed> FileCopyAsync(DashboardHarness h, string path, int folderId)
    {
        var job = PipelineHarness.JobFor(path, folderId: folderId);
        var stamp = FileStamp.Of(path);
        var result = await h.Pipeline.Pipeline().RunAsync(job, Ct);
        await h.Service<FolderJobs>().RecordFinishedAsync(job, result, stamp, Ct);
        return result.ShouldBeOfType<PipelineResult.Filed>();
    }

    [Fact]
    public async Task Allowing_a_file_that_is_not_ignored_is_refused()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/ignored/{new string('a', 64)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe("This file is not on the ignored list.");
    }
}
