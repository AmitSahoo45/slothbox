using SlothBox.Core;
using SlothBox.Core.Pipeline;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Worker;

internal sealed class FakeJob
{
    public long Id { get; init; }
    public required IncomingFile File { get; init; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public Guid? FileId { get; set; }
}

// The queue's rules in memory: one active job per path, the oldest queued job claimed first.
internal sealed class FakeQueue(List<string> sequence) : IJobQueue
{
    private long _nextId;

    public List<FakeJob> Jobs { get; } = [];
    public Queue<Exception> ClaimFaults { get; } = new();
    public Queue<Exception> CompleteFaults { get; } = new();

    public FakeJob Add(string path, JobStatus status = JobStatus.Queued, FileOrigin origin = FileOrigin.Folder)
    {
        lock (sequence)
        {
            var job = new FakeJob
            {
                Id = ++_nextId, File = new IncomingFile(path, origin, 1, DateTimeOffset.UtcNow), Status = status
            };
            Jobs.Add(job);
            return job;
        }
    }

    public List<FakeJob> Snapshot()
    {
        lock (sequence)
            return Jobs.Select(j => new FakeJob
            {
                Id = j.Id, File = j.File, Status = j.Status, Attempts = j.Attempts, Error = j.Error, FileId = j.FileId
            }).ToList();
    }

    public Task EnqueueAsync(IncomingFile file, CancellationToken ct)
    {
        lock (sequence)
        {
            sequence.Add("enqueue");
            if (!Jobs.Any(j => j.File.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) &&
                    j.Status is JobStatus.Queued or JobStatus.Processing))
                Jobs.Add(new FakeJob { Id = ++_nextId, File = file, Status = JobStatus.Queued });
        }
        return Task.CompletedTask;
    }

    public Task<Job?> ClaimNextAsync(CancellationToken ct)
    {
        lock (sequence)
        {
            if (ClaimFaults.TryDequeue(out var fault))
                throw fault;
            var next = Jobs.FirstOrDefault(j => j.Status == JobStatus.Queued);
            if (next is null)
                return Task.FromResult<Job?>(null);
            sequence.Add("claim");
            next.Status = JobStatus.Processing;
            next.Attempts++;
            return Task.FromResult<Job?>(new Job(next.Id, next.File, next.Status, next.Attempts, next.Error, next.FileId));
        }
    }

    public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct)
    {
        lock (sequence)
        {
            if (CompleteFaults.TryDequeue(out var fault))
                throw fault;
            var job = Jobs.Single(j => j.Id == jobId);
            job.Status = outcome.Status;
            job.FileId = outcome.FileId;
            job.Error = outcome.Error;
        }
        return Task.CompletedTask;
    }

    public Task<int> RequeueInterruptedAsync(CancellationToken ct)
    {
        lock (sequence)
        {
            sequence.Add("requeue");
            var interrupted = Jobs.Where(j => j.Status == JobStatus.Processing).ToList();
            foreach (var job in interrupted)
                job.Status = JobStatus.Queued;
            return Task.FromResult(interrupted.Count);
        }
    }

    public Task<QueueCounts> CountsAsync(CancellationToken ct)
    {
        lock (sequence)
            return Task.FromResult(new QueueCounts(Jobs.Count(j => j.Status == JobStatus.Queued),
                Jobs.Count(j => j.Status == JobStatus.Processing)));
    }
}

internal sealed class ScriptedPipeline(List<string> sequence) : IFilePipeline
{
    public List<Job> Runs { get; } = [];

    public Func<Job, PipelineResult> Answer { get; set; } =
        _ => throw new InvalidOperationException("No pipeline result was scripted for this test.");

    public int RunCount
    {
        get
        {
            lock (sequence)
                return Runs.Count;
        }
    }

    public Task<PipelineResult> RunAsync(Job job, CancellationToken ct)
    {
        lock (sequence)
        {
            sequence.Add("run");
            Runs.Add(job);
        }
        return Task.FromResult(Answer(job));
    }
}

internal sealed class RecoveryOnlyMover(List<string> sequence) : ISafeMover
{
    public Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<RecoveredAction>> RecoverPendingAsync(CancellationToken ct)
    {
        lock (sequence)
            sequence.Add("recover");
        return Task.FromResult<IReadOnlyList<RecoveredAction>>([]);
    }
}

// Ollama with both models pulled, until a test takes them away.
internal sealed class FakeGemma(List<string> sequence) : IOllamaStatus
{
    private volatile bool _ready = true;

    public bool Ready
    {
        get => _ready;
        set => _ready = value;
    }

    public Task<GemmaState> CheckAsync(CancellationToken ct)
    {
        lock (sequence)
            sequence.Add("check");
        var ready = Ready;
        return Task.FromResult(new GemmaState(true, null, new ModelState("gemma4:e4b", ready, false, false),
            new ModelState("embeddinggemma", ready, false, false)));
    }
}

internal sealed class FakeDatabase(List<string> sequence) : IDatabaseStartup
{
    public Queue<Exception> Failures { get; } = new();

    public Task MigrateAsync(CancellationToken ct)
    {
        lock (sequence)
        {
            sequence.Add("migrate");
            if (Failures.TryDequeue(out var failure))
                throw failure;
        }
        return Task.CompletedTask;
    }
}
