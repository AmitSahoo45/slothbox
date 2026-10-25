using System.ComponentModel;
using System.Diagnostics;

namespace SlothBox.App;

// Opens a dashboard page in the default browser.
internal static class Browser
{
    public static void Open(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No browser is set up for http links; the dashboard is still at its address.
        }
    }
}
