using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Events;
using SlothBox.Server.Settings;
using SlothBox.Server.Tray;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Tray;

// Spec 7.8: the tray menu's Pause, Resume and Undo last do what the dashboard's buttons do.
public sealed class TrayCommandsTests
{
    private readonly FixedGemma _gemma = new();
    private readonly WorkerState _worker = new();
    private readonly ServerEvents _events = new();
    private readonly ScriptedUndo _undo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TrayCommands Commands()
    {
        var status = new StatusService(_gemma, new TrayQueue(), _worker, new WorkerStats(),
            new FixedSettings(new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" }),
            new DatabasePassword("synthetic"));
        return new TrayCommands(_worker, new WorkerControl(_worker, status), _undo, _events);
    }

    [Fact]
    public async Task Pause_in_the_tray_is_the_users_pause_and_Resume_lifts_it()
    {
        var commands = Commands();

        await commands.PauseAsync(Ct);

        (commands.Paused, _worker.Reason).ShouldBe((true, (PauseReason?)PauseReason.User));

        await commands.ResumeAsync(Ct);

        (commands.Paused, _worker.Reason).ShouldBe((false, (PauseReason?)null));
    }

    // As on the dashboard: without Gemma and both models every waiting file would go to Needs review (spec 9).
    [Fact]
    public async Task Resume_in_the_tray_keeps_the_worker_paused_while_a_model_is_missing()
    {
        _worker.Pause(PauseReason.User);
        _gemma.State = FixedGemma.Ready() with { Vision = new ModelState("gemma4:e4b", false, false, false) };

        await Commands().ResumeAsync(Ct);

        (_worker.Paused, _worker.Reason).ShouldBe((true, (PauseReason?)PauseReason.GemmaOffline));
    }

    [Fact]
    public async Task Undo_last_in_the_tray_says_where_the_file_is_and_tells_the_open_pages()
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(Path.GetTempPath(), "Downloads");
        _undo.Last = new UndoResult(id, true, Path.Combine(folder, "bill.pdf"), null);
        using var pages = _events.Subscribe();

        var notice = await Commands().UndoLastAsync(Ct);

        notice.ShouldBe(new TrayNotice("Undone", $"bill.pdf is in {folder}."));
        pages.Events.TryRead(out var published).ShouldBeTrue();
        (published!.Name, published.Payload).ShouldBe(("file.undone", (object)new FileUndoneEvent(id)));
    }

    [Fact]
    public async Task Undo_last_with_nothing_to_undo_says_so_and_tells_no_page()
    {
        _undo.Last = new UndoResult(Guid.Empty, false, null, "There is nothing to undo.");
        using var pages = _events.Subscribe();

        var notice = await Commands().UndoLastAsync(Ct);

        notice.ShouldBe(new TrayNotice("Nothing undone", "There is nothing to undo."));
        pages.Events.TryRead(out _).ShouldBeFalse();
    }
}
