namespace SlothBox.Core.Folders;

// Spec 4.5: which folders may be watched. Platform facts come from IPlatformPaths so the rules are testable.
public sealed class FolderRules(IPlatformPaths platform) : IFolderRules
{
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private const string FullPathNeeded = "Use a full folder path, like D:\\Scans.";
    private const string CannotOpen = "SlothBox can't open this folder.";

    // Folder names that sync to Google Drive (old app, My Drive, shared drives) or Dropbox.
    private static readonly string[] CloudFolderNames = ["Google Drive", "My Drive", "Shared drives", "Dropbox"];

    public FolderCheck Check(string path, IReadOnlyList<WatchedFolder> existing, string outputRoot)
    {
        if (Place(path, FullPathNeeded, "Network folders can't be watched in this version.", out var full) is
            { } refused)
            return Refused(refused);
        if (outputRoot.Length > 0 && IsSameOrInside(full, Normalize(outputRoot)))
            return Refused("This is SlothBox's output folder. Watching it would make SlothBox refile its own files.");
        foreach (var watched in existing.Select(folder => Normalize(folder.Path)))
        {
            if (string.Equals(full, watched, PathComparison))
                return Refused("SlothBox already watches this folder.");
            if (IsSameOrInside(full, watched))
                return Refused("This folder is inside a folder SlothBox already watches.");
            if (IsSameOrInside(watched, full))
                return Refused("This folder contains a folder SlothBox already watches.");
        }

        if (!Directory.Exists(full))
            return Refused("Folder not found.");
        if (!CanOpen(full))
            return Refused(CannotOpen);
        // Moving a file out of a cloud-synced folder would delete it from the cloud.
        return IsCloud(full)
            ? new FolderCheck.Allowed(FolderMode.Copy, true)
            : new FolderCheck.Allowed(FolderMode.Move, false);
    }

    // Spec 7.4.1 step 1: the watched-folder rules in reverse. The output folder is a full local path, not a drive root
    // or a system folder, and neither inside nor around a watched folder, where SlothBox would refile its own files.
    // A cloud folder is fine: files moved into it only upload. A missing folder is created when setup finishes.
    public string? CheckOutput(string path, IReadOnlyList<WatchedFolder> watched)
    {
        // A network output folder would hold the Inbox, and watching is unreliable on network drives.
        if (Place(path, "Use a full folder path, like D:\\SlothBox.",
                "SlothBox can't keep sorted files in a network folder in this version.", out var full) is { } refused)
            return refused;
        foreach (var folder in watched.Select(folder => Normalize(folder.Path)))
        {
            if (IsSameOrInside(full, folder))
                return "SlothBox watches this folder. Pick another place for sorted files.";
            if (IsSameOrInside(folder, full))
                return "This folder contains a folder SlothBox watches, so SlothBox would refile its own files. " +
                    "Pick another place.";
        }
        if (File.Exists(full))
            return "This is a file, not a folder.";
        return Directory.Exists(full) && !CanOpen(full) ? CannotOpen : null;
    }

    // The rules both kinds of folder share, in the order of spec 4.5: device and network paths, relative paths, drive
    // roots, then system folders. Null, with the full path, when the path passes them.
    private string? Place(string path, string fullPathNeeded, string network, out string full)
    {
        full = "";
        // Windows reads / as \, so "/\server\share" is a share and "\/?\" a device path (a no-op elsewhere).
        path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // Device paths (\\?\, \\.\, \??\) and relative paths would slip past the folder comparisons.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal))
            return fullPathNeeded;
        // Judged before anything touches the path: probing a network share can hang.
        if (platform.IsNetworkPath(path))
            return network;
        if (!Path.IsPathFullyQualified(path))
            return fullPathNeeded;
        try
        {
            full = Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "This is not a valid folder path.";
        }

        if (platform.IsDriveRoot(full))
            return "Pick a folder inside the drive.";
        var target = full;
        return platform.SystemFolders.Any(system => IsSameOrInside(target, Normalize(system)))
            ? "This is a Windows system folder. Pick one of your own folders."
            : null;
    }

    private static bool CanOpen(string full)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private bool IsCloud(string full) =>
        platform.CloudRoots.Any(root => IsSameOrInside(full, Normalize(root))) ||
        full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            CloudFolderNames.Any(name => segment.Equals(name, PathComparison)) ||
            segment.StartsWith("Dropbox (", PathComparison));

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            PathComparison);

    private static FolderCheck.Refused Refused(string reason) => new(reason);
}
