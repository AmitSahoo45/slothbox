using System.Runtime.InteropServices;
using SlothBox.Core;

namespace SlothBox.App;

// Spec 4.5 "Defaults": Downloads and Screenshots through Windows known folders, so a folder that was moved (to OneDrive
// or another drive) is found where it is now. Null when Windows has no path for it.
internal sealed class KnownFolders : IKnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");   // FOLDERID_Downloads
    private static readonly Guid ScreenshotsId = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F"); // FOLDERID_Screenshots

    // KF_FLAG_DONT_VERIFY: the folder rules check that the folder is there.
    private const uint DontVerify = 0x4000;

    public string? Downloads => Find(DownloadsId);

    public string? Screenshots => Find(ScreenshotsId);

    private static string? Find(Guid id)
    {
        var result = SHGetKnownFolderPath(id, DontVerify, 0, out var path);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            // Freed whether the call worked or not.
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
