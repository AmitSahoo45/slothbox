using Microsoft.Extensions.Logging.Abstractions;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Events;
using SlothBox.Server.Settings;
using SlothBox.Server.Tray;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Tray;

// Spec 7.8: the tray's status line, and its notice for each file that goes to Needs review (spec 5.1 step 6).
public sealed class TrayFeedTests
{
    private readonly FixedGemma _gemma = new();
    private readonly TrayQueue _queue = new();
    private readonly WorkerState _worker = new();
    private readonly ServerEvents _events = new();
    private readonly RecordingTray _tray = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TrayFeed Feed(ITrayNotifier? tray, TimeSpan? refresh = null) =>
        new(_events, new StatusService(_gemma, _queue, _worker, new WorkerStats(),
                new FixedSettings(new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" }),
                new DatabasePassword("synthetic")),
            NullLogger<TrayFeed>.Instance, tray, refresh);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting until {what}.");
            await Task.Delay(20, Ct);
        }
    }

    private static StatusDto Status(bool database, bool setup, bool gemma, bool paused, PauseReason? reason,
        int waiting) =>
        new(new ServiceStatusDto(gemma, null), new ModelStatusDto("gemma4:e4b", true, true, true),
            new ModelStatusDto("embeddinggemma", true, true, false), new ServiceStatusDto(database, null),
            new QueueChangedEvent(waiting, 0), paused, setup, null, reason);

    private static FileReviewEvent Review(Guid id, string name) => new()
    {
        FileId = id, At = DateTimeOffset.UtcNow, Status = FileStatus.Review, Type = FileKind.Document,
        Category = "", Title = name, NewName = name, Folder = @"D:\slothbox-test\out\Needs review",
        Origin = FileOrigin.Folder, FolderLabel = "Downloads", ThumbUrl = null, CanUndo = true,
        Reason = ReviewReason.ScannedPdf
    };

    private static FileFiledEvent Filed(Guid id, string name) => new()
    {
        FileId = id, At = DateTimeOffset.UtcNow, Status = FileStatus.Filed, Type = FileKind.Document,
        Category = "Bills", Title = name, NewName = name, Folder = @"D:\slothbox-test\out\Documents\Bills",
        Origin = FileOrigin.Folder, FolderLabel = "Downloads", ThumbUrl = null, CanUndo = true
    };

    [Theory]
    [InlineData(true, true, true, false, null, 0, "Running, 0 files waiting")]
    [InlineData(true, true, true, false, null, 1, "Running, 1 file waiting")]
    [InlineData(true, true, true, true, PauseReason.User, 3, "Paused, 3 files waiting")]
    [InlineData(true, true, true, true, PauseReason.DriveFull, 2, "Paused: the output drive is full, 2 files waiting")]
    [InlineData(true, true, true, true, PauseReason.GemmaOffline, 5, "Gemma is offline, 5 files waiting")]
    [InlineData(true, true, false, false, null, 0, "Gemma is offline, 0 files waiting")]
    [InlineData(true, true, false, true, PauseReason.User, 4, "Gemma is offline, 4 files waiting")]
    [InlineData(true, false, true, false, null, 0, "Waiting for setup")]
    [InlineData(false, true, true, false, null, 0, "Database offline")]
    [InlineData(false, false, false, true, PauseReason.User, 0, "Database offline")]
    public void The_status_line_says_what_SlothBox_is_doing(bool database, bool setup, bool gemma, bool paused,
        PauseReason? reason, int waiting, string line) =>
        TrayStatus.Line(Status(database, setup, gemma, paused, reason, waiting)).ShouldBe(line);

    [Fact]
    public async Task The_tray_gets_the_status_line_as_soon_as_SlothBox_starts()
    {
        _queue.Counts = new QueueCounts(2, 1);
        using var feed = Feed(_tray);

        await feed.StartAsync(Ct);

        await UntilAsync(() => _tray.LastStatus == "Running, 2 files waiting", "the first status line arrived");
        await feed.StopAsync(Ct);
    }

    [Fact]
    public async Task Only_a_file_that_goes_to_Needs_review_shows_a_notice()
    {
        using var feed = Feed(_tray);
        await feed.StartAsync(Ct);
        await UntilAsync(() => _tray.LastStatus is not null, "the feed listened");
        var review = Guid.NewGuid();

        _events.Publish(new ServerEvent("file.filed", Filed(Guid.NewGuid(), "bill.pdf")));
        _events.Publish(new ServerEvent("file.review", Review(review, "scan.pdf")));

        await UntilAsync(() => _tray.Notices.Count > 0, "a notice was shown");
        _tray.Notices.ShouldBe([(review, "scan.pdf")]);
        await feed.StopAsync(Ct);
    }

    [Fact]
    public async Task The_status_line_follows_a_pause()
    {
        using var feed = Feed(_tray);
        await feed.StartAsync(Ct);
        await UntilAsync(() => _tray.LastStatus == "Running, 0 files waiting", "the first status line arrived");

        _worker.Pause(PauseReason.User);
        _events.Publish(new ServerEvent("worker.changed", new WorkerChanged(true, PauseReason.User)));

        await UntilAsync(() => _tray.LastStatus == "Paused, 0 files waiting", "the line said paused");
        await feed.StopAsync(Ct);
    }

    // A database that stops answering publishes no event, so the line is checked again now and then.
    [Fact]
    public async Task The_status_line_is_checked_again_without_an_event()
    {
        using var feed = Feed(_tray, TimeSpan.FromMilliseconds(50));
        await feed.StartAsync(Ct);
        await UntilAsync(() => _tray.LastStatus == "Running, 0 files waiting", "the first status line arrived");

        _queue.Failure = new InvalidOperationException("Connection refused.");

        await UntilAsync(() => _tray.LastStatus == "Database offline", "the line said the database is offline");
        await feed.StopAsync(Ct);
    }

    // A failure in the feed would stop SlothBox: a background service that fails stops the host.
    [Fact]
    public async Task A_tray_call_that_fails_does_not_stop_the_feed()
    {
        _tray.FailLines(1);
        using var feed = Feed(_tray, TimeSpan.FromMilliseconds(50));
        await feed.StartAsync(Ct);
        await UntilAsync(() => _tray.LastStatus is not null, "a status line arrived after the failed one");
        var shown = Guid.NewGuid();

        _tray.FailNotices(1);
        _events.Publish(new ServerEvent("file.review", Review(Guid.NewGuid(), "first.pdf")));
        _events.Publish(new ServerEvent("file.review", Review(shown, "second.pdf")));

        await UntilAsync(() => _tray.Notices.Count > 0, "a notice was shown");
        _tray.Notices.ShouldBe([(shown, "second.pdf")]);
        feed.ExecuteTask!.IsCompleted.ShouldBeFalse();
        await feed.StopAsync(Ct);
    }

    [Fact]
    public async Task Without_a_tray_there_is_nothing_to_show()
    {
        using var feed = Feed(null);

        await feed.StartAsync(Ct);

        await UntilAsync(() => feed.ExecuteTask!.IsCompleted, "the feed stopped");
        feed.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
