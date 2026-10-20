using Microsoft.Extensions.Hosting;
using SlothBox.Server;

namespace SlothBox.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
        var app = ServerHost.Build(args.Where(arg => !string.Equals(arg, "--headless",
            StringComparison.OrdinalIgnoreCase)).ToArray());
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            if (headless)
            {
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
                return;
            }

            ApplicationConfiguration.Initialize();
            using var context = new ApplicationContext();
            using var menu = new ContextMenuStrip();
            menu.Items.Add("Exit", null, (_, _) => context.ExitThread());
            using var tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "SlothBox",
                ContextMenuStrip = menu,
                Visible = true
            };
            Application.Run(context);
            tray.Visible = false;
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            app.StopAsync(timeout.Token).GetAwaiter().GetResult();
            app.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
