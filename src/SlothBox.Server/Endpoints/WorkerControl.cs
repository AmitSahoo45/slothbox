using SlothBox.Server.Api;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// Pause and Resume from the dashboard and the tray (spec 5.7, 7.8), answered with the status after the change.
public sealed class WorkerControl(WorkerState worker, StatusService status)
{
    public Task<StatusDto> PauseAsync(CancellationToken ct)
    {
        worker.Pause(PauseReason.User);
        return status.GetAsync(ct);
    }

    public async Task<StatusDto> ResumeAsync(CancellationToken ct)
    {
        worker.Resume();
        var current = await status.GetAsync(ct);
        if (current.Gemma.Ready && current.VisionModel.Present && current.EmbeddingModel.Present)
            return current;
        // Without Gemma and both models every waiting file would go to Needs review (spec 9): stay paused.
        worker.Pause(PauseReason.GemmaOffline);
        return current with { Paused = true, PauseReason = PauseReason.GemmaOffline };
    }
}
