using SlothBox.Core;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Events;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Tray;

// A notice the tray shows after one of its commands.
public sealed record TrayNotice(string Title, string Text);

// Spec 7.8: what the tray menu does. The App draws the menu; the work is the dashboard's own, so both agree.
public sealed class TrayCommands(WorkerState worker, WorkerControl control, IUndoService undo, ServerEvents events)
{
    public const string DashboardUrl = "http://127.0.0.1:5080/";
    public const string ReviewUrl = "http://127.0.0.1:5080/review";

    public bool Paused => worker.Paused;

    public Task PauseAsync(CancellationToken ct) => control.PauseAsync(ct);

    public Task ResumeAsync(CancellationToken ct) => control.ResumeAsync(ct);

    // The dashboard's Undo last (spec 6.6), and where the file is now.
    public async Task<TrayNotice> UndoLastAsync(CancellationToken ct)
    {
        var result = DashboardEndpoints.Undone(await undo.UndoLastAsync(ct), events);
        if (!result.Success)
            return new TrayNotice("Nothing undone", result.Reason ?? "SlothBox could not undo the last action.");
        return new TrayNotice("Undone", result.RestoredPath is { } path
            ? $"{Path.GetFileName(path)} is in {Path.GetDirectoryName(path)}."
            : "The last action is undone.");
    }
}
