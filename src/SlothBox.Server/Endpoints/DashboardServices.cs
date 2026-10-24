using SlothBox.Ai.Status;
using SlothBox.Core;
using SlothBox.Core.Refiling;
using SlothBox.Core.Undo;
using SlothBox.Data.Dashboard;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// The services behind the dashboard API (spec 8.2): status, the dashboard's queries, images, undo and refile.
public static class DashboardServices
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IOllamaStatus>(sp =>
        {
            var ollama = sp.GetRequiredService<ISettingsStore>().Current.Ollama;
            return new OllamaStatus(ServerHost.OllamaClient(ollama), ollama);
        });
        services.AddSingleton<WorkerStats>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<DashboardQueries>();
        services.AddSingleton<LazinessMeter>();
        services.AddSingleton<FileAccessGuard>();
        services.AddSingleton<FileImages>();
        services.AddSingleton<IFileLauncher, ShellFileLauncher>();
        services.AddSingleton<IUndoService, UndoService>();
        services.AddSingleton(sp => new Refiler(sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<IReceiptRepository>(), sp.GetRequiredService<ICategoryRepository>(),
            sp.GetRequiredService<INameBuilder>(), sp.GetRequiredService<ISafeMover>(),
            sp.GetRequiredService<IActionLog>(), sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<ISettingsStore>()));
    }
}
