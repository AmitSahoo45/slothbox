namespace SlothBox.Core.Folders;

// The machine's own facts for the folder rules (spec 4.5): system folders, OneDrive roots, drive roots, network paths.
public sealed class PlatformPaths(Func<string, string?>? environment = null) : IPlatformPaths
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    // %WINDIR%, Program Files, Program Files (x86), %PROGRAMDATA%, %APPDATA% and %LOCALAPPDATA%.
    public IReadOnlyList<string> SystemFolders { get; } =
    [
        .. new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData
            }
            .Select(Environment.GetFolderPath).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    // OneDrive sets these for the signed-in accounts; OneDrive and OneDriveConsumer often name the same folder.
    public IReadOnlyList<string> CloudRoots =>
    [
        .. OneDriveVariables.Select(_environment).OfType<string>().Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    public bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && string.Equals(Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    // A UNC path, or a drive letter mapped to a share. Only the drive type is asked for, so a share is never probed.
    public bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;
        var root = Path.GetPathRoot(path);
        if (!OperatingSystem.IsWindows() || root is not { Length: >= 2 } || root[1] != ':')
            return false;
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
