using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Server.Watching;

namespace SlothBox.Tests.Watching;

// What the watching side keeps when the worker is done with a job: finished paths (spec 4.5) and tidy progress
// (spec 7.4.6, 8.3).
public sealed class FolderJobsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly InMemoryFinishedPaths _finished = new();
    private readonly ServerEvents _events = new();
    private readonly TidyBatches _tidy;
    private readonly FolderJobs _jobs;

    public FolderJobsTests()
    {
        _tidy = new TidyBatches(_events);
        _jobs = new FolderJobs(_finished, _tidy);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Job JobFor(string path, int? folderId = 1) =>
        new(1, new IncomingFile(path, FileOrigin.Folder, folderId, DateTimeOffset.UtcNow), JobStatus.Processing, 1,
            null, null);

    private static PipelineResult.Filed Filed() => new(TestRecords.File("abc"), "Bills", "Downloads", true);

    // A copy-only original after its filing, or an undone file the gate skipped by its ignored hash.
    [Theory]
    [InlineData("filed")]
    [InlineData("ignored")]
    public async Task A_file_that_stayed_where_it_was_unchanged_becomes_a_finished_path(string outcome)
    {
        var path = _temp.Write("photo.jpg", "photo");
        var seen = FileStamp.Of(path);
        PipelineResult result = outcome == "filed" ? Filed() : new PipelineResult.Skipped("ignored");

        await _jobs.RecordFinishedAsync(JobFor(path), result, seen, Ct);

        _finished.Entries[path].ShouldBe(new FinishedPath(path, 1, seen!.Value));
    }

    // The sweep never queues hidden, system, temporary or baseline files, so their skips need no record; a record
    // would keep a file unhidden later, or tidied while its job ran, from ever being queued. A skip for the folder's
    // sake says nothing about the file.
    [Theory]
    [InlineData("hidden")]
    [InlineData("system")]
    [InlineData("temporary")]
    [InlineData("baseline")]
    [InlineData("folder_disabled")]
    [InlineData("folder_removed")]
    public async Task Only_an_ignored_hash_skip_leaves_a_finished_path(string reason)
    {
        var path = _temp.Write("photo.jpg", "photo");

        await _jobs.RecordFinishedAsync(JobFor(path), new PipelineResult.Skipped(reason), FileStamp.Of(path), Ct);

        _finished.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_changed_during_its_job_does_not_become_a_finished_path()
    {
        var path = _temp.Write("photo.jpg", "photo");
        var seen = FileStamp.Of(path);
        File.AppendAllText(path, " and more");

        await _jobs.RecordFinishedAsync(JobFor(path), Filed(), seen, Ct);

        _finished.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_was_moved_away_leaves_no_finished_path()
    {
        var path = _temp.Write("photo.jpg", "photo");
        var seen = FileStamp.Of(path);
        File.Delete(path);

        await _jobs.RecordFinishedAsync(JobFor(path), Filed(), seen, Ct);

        _finished.Entries.ShouldBeEmpty();
    }

    // The sweep must try these again: a locked file, a failure, Gemma offline.
    [Theory]
    [InlineData("not ready")]
    [InlineData("failed")]
    [InlineData("offline")]
    public async Task A_file_the_sweep_must_try_again_does_not_become_a_finished_path(string outcome)
    {
        var path = _temp.Write("photo.jpg", "photo");
        PipelineResult result = outcome switch
        {
            "not ready" => new PipelineResult.NotReady(),
            "failed" => new PipelineResult.Failed(Guid.NewGuid(), "The move failed.", MoveFailure.IoError),
            _ => new PipelineResult.Offline("Gemma is offline.")
        };

        await _jobs.RecordFinishedAsync(JobFor(path), result, FileStamp.Of(path), Ct);

        _finished.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Tidy_progress_counts_each_tidied_file_once_its_job_is_over()
    {
        using var page = _events.Subscribe();
        var first = _temp.PathOf("a.pdf");
        var second = _temp.PathOf("b.pdf");

        _tidy.Start(1, [first, second]);
        _jobs.JobOver(JobFor(first));
        _jobs.JobOver(JobFor(_temp.PathOf("not-tidied.pdf")));
        _jobs.JobOver(JobFor(second, folderId: 2));
        _jobs.JobOver(JobFor(second));
        _jobs.JobOver(JobFor(second));

        Progress(page).ShouldBe([
            new FolderTidyProgressEvent(1, 0, 2), new FolderTidyProgressEvent(1, 1, 2),
            new FolderTidyProgressEvent(1, 2, 2)
        ]);
    }

    private static List<FolderTidyProgressEvent> Progress(ServerEvents.Subscription page)
    {
        var progress = new List<FolderTidyProgressEvent>();
        while (page.Events.TryRead(out var serverEvent))
        {
            serverEvent.Name.ShouldBe("folder.tidy-progress");
            progress.Add(serverEvent.Payload.ShouldBeOfType<FolderTidyProgressEvent>());
        }
        return progress;
    }

    public void Dispose() => _temp.Dispose();
}
