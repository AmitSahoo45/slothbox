namespace SlothBox.Core.Folders;

// Spec 4.5: which folders may be watched. Platform facts come from IPlatformPaths so the rules are testable.
public sealed class FolderRules(IPlatformPaths platform) : IFolderRules
{
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private const string FullPathNeeded = "Use a full folder path, like D:\\Scans.";

    // Folder names that sync to Google Drive (old app, My Drive, shared drives) or Dropbox.
    private static readonly string[] CloudFolderNames = ["Google Drive", "My Drive", "Shared drives", "Dropbox"];

    public FolderCheck Check(string path, IReadOnlyList<WatchedFolder> existing, string outputRoot)
    {
        // Device paths (\\?\, \\.\) and relative paths would slip past the folder comparisons below.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return Refused(FullPathNeeded);
        // Judged before anything touches the path: probing a network share can hang.
        if (platform.IsNetworkPath(path))
            return Refused("Network folders can't be watched in this version.");
        if (!Path.IsPathFullyQualified(path))
            return Refused(FullPathNeeded);
        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refused("This is not a valid folder path.");
        }

        if (platform.IsDriveRoot(full))
            return Refused("Pick a folder inside the drive.");
        if (platform.SystemFolders.Any(system => IsSameOrInside(full, Normalize(system))))
            return Refused("This is a Windows system folder. Pick one of your own folders.");
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
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            entries.MoveNext();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return Refused("SlothBox can't open this folder.");
        }
        // Moving a file out of a cloud-synced folder would delete it from the cloud.
        return IsCloud(full)
            ? new FolderCheck.Allowed(FolderMode.Copy, true)
            : new FolderCheck.Allowed(FolderMode.Move, false);
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
