using SlothBox.Core;
using SlothBox.Core.Folders;
using SlothBox.Data.Folders;

namespace SlothBox.Server.Watching;

// The services behind the watched folders (spec 4.5): rules, finished paths, watchers, the sweep, tidy and the
// Folders page. The watch service itself is added by ServerHost, outside tests.
public static class WatchingServices
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IPlatformPaths>(_ => new PlatformPaths());
        services.AddSingleton<IFolderRules, FolderRules>();
        services.AddSingleton<IFinishedPaths, FinishedPathStore>();
        services.AddSingleton<FolderQueries>();
        services.AddSingleton<SweepRequests>();
        services.AddSingleton<FolderWatchers>();
        services.AddSingleton<FolderSweep>();
        services.AddSingleton<TidyBatches>();
        services.AddSingleton<FolderJobs>();
        services.AddSingleton<FolderService>();
    }
}
