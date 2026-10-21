namespace SlothBox.Core.Pipeline;

// Runs steps 2 to 6 of spec 5.1 for one job. The worker in Server claims jobs and calls it.
public interface IFilePipeline
{
    Task<PipelineResult> RunAsync(Job job, CancellationToken ct);
}

// What became of a job. The worker turns it into the job's status, its own pause state and the UI's events.
public abstract record PipelineResult
{
    // The file is in the output folder (filed, in Needs review or in Duplicates, by File.Status) and recorded.
    public sealed record Filed(FileRecord File, string Category, string? FolderLabel, bool Embedded) : PipelineResult;

    // Nothing to do: a temp, hidden, system, baseline or ignored file, or one that vanished (GateSkipReason).
    public sealed record Skipped(string Reason) : PipelineResult;

    // The file is still being written or is open elsewhere: try again later.
    public sealed record NotReady : PipelineResult;

    // The move failed; the file row stays for a retry. DriveFull pauses the worker (spec 9).
    public sealed record Failed(Guid FileId, string Error, MoveFailure? Failure) : PipelineResult;

    // Gemma could not be reached: nothing was written; the job goes back to the queue and the worker pauses.
    public sealed record Offline(string Error) : PipelineResult;
}
