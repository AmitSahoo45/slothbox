using System.Threading.Channels;
using SlothBox.Core;

namespace SlothBox.Server.Watching;

// A folder to watch: a watched folder by its id, or the Inbox (no id), whose files are phone uploads.
public sealed record WatchTarget(int? FolderId, string Path);

// Spec 4.5 and 5.1 step 1: one Windows watcher per enabled folder and the Inbox, top level only. New and renamed
// files come out of Found; an overflow asks for an immediate sweep (spec 9).
public sealed class FolderWatchers(SweepRequests sweeps) : IDisposable
{
    private readonly Lock _gate = new();

    // By folder id; the Inbox is 0 (folder ids start at 1).
    private readonly Dictionary<int, Running> _running = [];
    private readonly Channel<IncomingFile> _found = Channel.CreateUnbounded<IncomingFile>();

    private sealed record Running(WatchTarget Target, FileSystemWatcher Watcher);

    public ChannelReader<IncomingFile> Found => _found.Reader;

    // Starts watchers for the targets that exist and are not watched yet; stops those no longer wanted, those whose
    // path changed and those whose folder has gone (spec 4.5 "Folder problems").
    public void Sync(IReadOnlyList<WatchTarget> targets)
    {
        lock (_gate)
        {
            var wanted = targets.ToDictionary(target => Key(target.FolderId));
            foreach (var (key, running) in _running.ToList())
                if (!wanted.TryGetValue(key, out var target) || !SamePath(target.Path, running.Target.Path) ||
                    !Directory.Exists(running.Target.Path))
                    Stop(key);
            foreach (var (key, target) in wanted)
                if (!_running.ContainsKey(key) && Directory.Exists(target.Path))
                    Start(key, target);
        }
    }

    public bool IsWatching(int? folderId)
    {
        lock (_gate)
            return _running.ContainsKey(Key(folderId));
    }

    public string? WatchedPath(int? folderId)
    {
        lock (_gate)
            return _running.TryGetValue(Key(folderId), out var running) ? running.Target.Path : null;
    }

    public void Dispose()
    {
        lock (_gate)
            foreach (var key in _running.Keys.ToList())
                Stop(key);
        _found.Writer.TryComplete();
    }

    private void Start(int key, WatchTarget target)
    {
        var watcher = new FileSystemWatcher(target.Path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += (_, e) => Report(target, e.FullPath);
        watcher.Renamed += (_, e) => Report(target, e.FullPath);
        watcher.Error += (_, e) => Failed(key, watcher, e.GetException());
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The folder went away or cannot be read; the next Sync tries again.
            watcher.Dispose();
            return;
        }
        _running[key] = new Running(target, watcher);
    }

    private void Report(WatchTarget target, string path) =>
        _found.Writer.TryWrite(new IncomingFile(path, target.FolderId is null ? FileOrigin.Phone : FileOrigin.Folder,
            target.FolderId, DateTimeOffset.UtcNow));

    private void Failed(int key, FileSystemWatcher watcher, Exception error)
    {
        // Anything else means the folder was deleted, renamed or became unreachable: this watcher is done.
        if (error is not InternalBufferOverflowException)
            lock (_gate)
                if (_running.TryGetValue(key, out var running) && running.Watcher == watcher)
                    Stop(key);
        // Either way some events were lost, so the whole folder is looked at again.
        sweeps.Request();
    }

    private void Stop(int key)
    {
        _running[key].Watcher.Dispose();
        _running.Remove(key);
    }

    private static int Key(int? folderId) => folderId ?? 0;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
