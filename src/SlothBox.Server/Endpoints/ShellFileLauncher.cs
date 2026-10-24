using System.Diagnostics;
using SlothBox.Core;

namespace SlothBox.Server.Endpoints;

// Spec 7.4.9: Open uses the file's default app; Show in folder opens Explorer with the file selected. Windows only:
// the browser cannot open local files itself, so SlothBox does it when the user asks.
public sealed class ShellFileLauncher : IFileLauncher
{
    public void Open(string path)
    {
        RequireWindows();
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Reveal(string path)
    {
        RequireWindows();
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Opening files works on Windows only.");
    }
}
