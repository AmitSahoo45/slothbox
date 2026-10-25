using SlothBox.Core;

namespace SlothBox.Tests.Tray;

// The App's tray as Server sees it: the status lines and the notices it was asked to show. A call can be made to fail,
// as it would while the tray closes under Server.
internal sealed class RecordingTray : ITrayNotifier
{
    private readonly Lock _gate = new();
    private readonly List<(Guid FileId, string FileName)> _notices = [];
    private string? _status;
    private int _failingNotices;
    private int _failingLines;

    public IReadOnlyList<(Guid FileId, string FileName)> Notices
    {
        get
        {
            lock (_gate)
                return [.. _notices];
        }
    }

    public string? LastStatus
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    // How many of the next notices, or status lines, throw.
    public void FailNotices(int calls)
    {
        lock (_gate)
            _failingNotices = calls;
    }

    public void FailLines(int calls)
    {
        lock (_gate)
            _failingLines = calls;
    }

    public void ShowReviewNotice(Guid fileId, string fileName)
    {
        lock (_gate)
        {
            Fail(ref _failingNotices);
            _notices.Add((fileId, fileName));
        }
    }

    public void SetStatus(string statusLine)
    {
        lock (_gate)
        {
            Fail(ref _failingLines);
            _status = statusLine;
        }
    }

    private static void Fail(ref int failing)
    {
        if (failing == 0)
            return;
        failing--;
        throw new InvalidOperationException("The tray is closing.");
    }
}

// The queue counts the status reads, or a database that does not answer.
internal sealed class TrayQueue : IJobQueue
{
    public QueueCounts Counts { get; set; } = new(0, 0);
    public Exception? Failure { get; set; }

    public Task<QueueCounts> CountsAsync(CancellationToken ct) =>
        Failure is { } failure ? Task.FromException<QueueCounts>(failure) : Task.FromResult(Counts);

    public Task EnqueueAsync(IncomingFile file, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, Core.Folders.FileStamp stamp,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<Job?> ClaimNextAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> RequeueInterruptedAsync(CancellationToken ct) => throw new NotSupportedException();
}

// Undo last answers with the scripted result.
internal sealed class ScriptedUndo : IUndoService
{
    public UndoResult Last { get; set; } = new(Guid.Empty, false, null, "There is nothing to undo.");

    public Task<UndoResult> UndoLastAsync(CancellationToken ct) => Task.FromResult(Last);

    public Task<UndoResult> UndoFileAsync(Guid fileId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<UndoResult>> UndoSinceAsync(DateTimeOffset since, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
}
