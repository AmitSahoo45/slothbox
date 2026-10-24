using Microsoft.Extensions.Logging.Abstractions;
using SlothBox.Core;
using SlothBox.Server.Watching;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Watching;

// Spec 4.3 steps 8 and 9: after crash recovery, watchers start and the first sweep runs; watcher reports are queued.
public sealed class WatchServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task Once_the_worker_has_started_the_folders_are_swept_and_watched()
    {
        var downloads = _temp.PathOf("downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "waiting.pdf"), "waiting");
        var folders = new InMemoryFolders();
        folders.Folders.Add(new WatchedFolder(1, downloads, "Downloads", FolderMode.Move, false, true));
        var queue = new RecordingQueue();
        var settings = new FixedSettings(new SlothBoxSettings { OutputRoot = _temp.PathOf("output") });
        var requests = new SweepRequests();
        using var watchers = new FolderWatchers(requests);
        var worker = new WorkerState();
        using var service = new WatchService(watchers, new FolderSweep(folders, new InMemoryFinishedPaths(), queue,
            settings), requests, queue, settings, worker, NullLogger<WatchService>.Instance);

        await service.StartAsync(Ct);
        await Task.Delay(200, Ct);
        queue.Queued.ShouldBeEmpty();

        worker.MarkStarted();
        await UntilAsync(() => queue.Queued.Any(file => file.Path.EndsWith("waiting.pdf")), "the sweep queued it");
        File.WriteAllText(Path.Combine(downloads, "new.pdf"), "new");
        await UntilAsync(() => queue.Queued.Any(file => file.Path.EndsWith("new.pdf")), "the watcher reported it");

        await service.StopAsync(Ct);
    }

    public void Dispose() => _temp.Dispose();
}
