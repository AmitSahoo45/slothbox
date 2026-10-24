using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Settings;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// GET /status, and the payload of status.changed (spec 8.2, 8.3).
public sealed class StatusService(IOllamaStatus gemma, IJobQueue jobs, WorkerState worker, WorkerStats stats,
    ISettingsStore settings)
{
    // GET /status ("Check again", spec 7.4.10): a pause for Gemma ends as soon as Ollama answers with both models.
    public Task<StatusDto> GetAsync(CancellationToken ct) => BuildAsync(liftGemmaPause: true, ct);

    // The status as it is, for status.changed: telling the pages about a pause must not end it.
    public Task<StatusDto> ReadAsync(CancellationToken ct) => BuildAsync(liftGemmaPause: false, ct);

    private async Task<StatusDto> BuildAsync(bool liftGemmaPause, CancellationToken ct)
    {
        var ollama = await gemma.CheckAsync(ct);
        if (liftGemmaPause && ollama.Ready)
            worker.TryResume(PauseReason.GemmaOffline);

        ServiceStatusDto database;
        QueueChangedEvent queue;
        try
        {
            var counts = await jobs.CountsAsync(ct);
            database = new ServiceStatusDto(true, null);
            queue = new QueueChangedEvent(counts.Waiting, counts.Processing);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            database = new ServiceStatusDto(false,
                DatabaseProblem(Environment.GetEnvironmentVariable(DatabaseConnection.PasswordVariable)));
            queue = new QueueChangedEvent(0, 0);
        }

        // Setup is complete once there is an output folder; first-run setup (E17) records it in app_state.
        return new StatusDto(new ServiceStatusDto(ollama.Reachable, ollama.Message), Model(ollama.Vision),
            Model(ollama.Embedding), database, queue, worker.Paused, settings.Current.OutputRoot.Length > 0,
            stats.AverageSecondsToday, worker.Reason);
    }

    // Spec 11: a missing password variable is named, so the owner knows what to set.
    public static string DatabaseProblem(string? password) => string.IsNullOrEmpty(password)
        ? "Set SLOTHBOX_DB_PASSWORD (see README setup)."
        : "The database is not answering.";

    public static StatusChangedEvent ToEvent(StatusDto status) => new(status.Gemma, status.VisionModel,
        status.EmbeddingModel, status.Database, status.Queue, status.Paused, status.SetupComplete,
        status.AverageSecondsPerFileToday, status.PauseReason);

    private static ModelStatusDto Model(ModelState model) =>
        new(model.Name, model.Present, model.Loaded, model.OnGpu);
}
