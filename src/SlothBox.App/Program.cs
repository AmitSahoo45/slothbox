using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlothBox.Core;
using SlothBox.Server;
using SlothBox.Server.Tray;

namespace SlothBox.App;

internal static class Program
{
    private const string Headless = "--headless";

    [STAThread]
    private static void Main(string[] args)
    {
        // Spec 4.3: one SlothBox per user session. A second launch opens the dashboard and exits.
        using var instance = new Mutex(false, @"Local\SlothBox", out var first);
        if (!first)
        {
            Browser.Open(TrayCommands.DashboardUrl);
            return;
        }
        var headless = args.Contains(Headless, StringComparer.OrdinalIgnoreCase);
        var serverArgs = args.Where(arg => !string.Equals(arg, Headless, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (headless)
            RunHeadless(serverArgs);
        else
            RunWithTray(serverArgs);
    }

    // No tray icon and no folder dialog (paths are typed); everything else runs (spec 4.3).
    private static void RunHeadless(string[] args)
    {
        var app = ServerHost.Build(args, AddWindowsPieces);
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            app.WaitForShutdownAsync().GetAwaiter().GetResult();
        }
        finally
        {
            Stop(app);
        }
    }

    private static void RunWithTray(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var tray = new TrayIcon();
        var app = ServerHost.Build(args, services =>
        {
            AddWindowsPieces(services);
            services.AddSingleton<INativeDialogs>(new FolderDialog(tray));
            services.AddSingleton<ITrayNotifier>(tray);
        });
        try
        {
            try
            {
                app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Most often another program holds port 5080 or 5081. With no window SlothBox would vanish unseen.
                MessageBox.Show($"SlothBox could not start: {e.Message}", "SlothBox", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            // A host that stops by itself takes the tray icon with it.
            app.Lifetime.ApplicationStopping.Register(tray.Exit);
            tray.Run(app.Services.GetRequiredService<TrayCommands>(), app.Services.GetRequiredService<IAutoStart>());
        }
        finally
        {
            Stop(app);
        }
    }

    // The Windows pieces that need no window, so --headless has them too (spec 4.7).
    private static void AddWindowsPieces(IServiceCollection services)
    {
        services.AddSingleton<IKnownFolders, KnownFolders>();
        services.AddSingleton<IAutoStart, RunKey>();
        services.AddSingleton<IFirewallRules, FirewallRules>();
    }

    // Spec 4.3 "Shutdown": the current job gets up to 30 s to finish.
    private static void Stop(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        app.StopAsync(timeout.Token).GetAwaiter().GetResult();
        app.DisposeAsync().GetAwaiter().GetResult();
    }
}
