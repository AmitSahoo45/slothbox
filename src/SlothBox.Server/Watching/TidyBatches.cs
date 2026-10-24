using SlothBox.Server.Api;
using SlothBox.Server.Events;

namespace SlothBox.Server.Watching;

// "Tidy them now" (spec 4.5, 7.4.6): the files a folder's tidy queued, and how many of their jobs are over, sent to
// the page as `folder.tidy-progress` (spec 8.3).
public sealed class TidyBatches(ServerEvents events)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Batch> _batches = [];

    private sealed class Batch
    {
        public HashSet<string> Waiting { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Total { get; set; }
        public int Done { get; set; }
    }

    public void Start(int folderId, IReadOnlyCollection<string> paths)
    {
        FolderTidyProgressEvent progress;
        lock (_gate)
        {
            if (!_batches.TryGetValue(folderId, out var batch))
                _batches[folderId] = batch = new Batch();
            foreach (var path in paths)
                if (batch.Waiting.Add(path))
                    batch.Total++;
            if (batch.Waiting.Count == 0)
            {
                _batches.Remove(folderId);
                return;
            }
            progress = new FolderTidyProgressEvent(folderId, batch.Done, batch.Total);
        }
        events.Publish(new ServerEvent("folder.tidy-progress", progress));
    }

    // A job is over for this path; counts it if it belongs to its folder's tidy batch.
    public void Over(int? folderId, string path)
    {
        if (folderId is not { } id)
            return;
        FolderTidyProgressEvent progress;
        lock (_gate)
        {
            if (!_batches.TryGetValue(id, out var batch) || !batch.Waiting.Remove(path))
                return;
            batch.Done++;
            progress = new FolderTidyProgressEvent(id, batch.Done, batch.Total);
            if (batch.Waiting.Count == 0)
                _batches.Remove(id);
        }
        events.Publish(new ServerEvent("folder.tidy-progress", progress));
    }
}
