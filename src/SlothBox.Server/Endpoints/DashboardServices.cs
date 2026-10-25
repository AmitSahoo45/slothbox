using SlothBox.Core;
using SlothBox.Core.Refiling;
using SlothBox.Core.Undo;
using SlothBox.Data;
using SlothBox.Data.Categories;
using SlothBox.Data.Dashboard;
using SlothBox.Data.Files;
using SlothBox.Data.Receipts;
using SlothBox.Server.Phone;
using SlothBox.Server.Settings;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// The services behind the dashboard API (spec 8.2): status, the dashboard's queries, images, undo, refile, setup and
// settings.
public static class DashboardServices
{
    public static void Add(IServiceCollection services)
    {
        // Follows the Ollama address saved on the Settings page (spec 7.4.8).
        services.AddSingleton<IOllamaStatus, LiveOllamaStatus>();
        services.AddSingleton(_ => DatabasePassword.FromEnvironment());
        services.AddSingleton<WorkerStats>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<DashboardQueries>();
        services.AddSingleton<LazinessMeter>();
        services.AddSingleton<FileAccessGuard>();
        services.AddSingleton<FileImages>();
        services.AddSingleton<IFileLauncher, ShellFileLauncher>();
        services.AddSingleton<IUndoService, UndoService>();
        services.AddSingleton<CategoryQueries>();
        services.AddSingleton<CategoryService>();
        services.AddSingleton<ReceiptQueries>();
        services.AddSingleton(sp => new Refiler(sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<IReceiptRepository>(), sp.GetRequiredService<ICategoryRepository>(),
            sp.GetRequiredService<INameBuilder>(), sp.GetRequiredService<ISafeMover>(),
            sp.GetRequiredService<IActionLog>(), sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<ISettingsStore>()));
        // First-run setup and the Settings page (E17). The App adds the Windows pieces they use when present.
        services.AddSingleton<IAppState, AppStateStore>();
        services.AddSingleton<IgnoredFiles>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<PhoneLink>();
    }
}
