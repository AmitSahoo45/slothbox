using System.Security.AccessControl;
using System.Security.Principal;
using SlothBox.Core;
using SlothBox.Core.Folders;

namespace SlothBox.Tests.Folders;

public sealed class FolderRulesTests : IDisposable
{
    private const string NotInsideDrive = "Pick a folder inside the drive.";
    private const string SystemFolder = "This is a Windows system folder. Pick one of your own folders.";
    private const string OutputFolder = "This is SlothBox's output folder. Watching it would make SlothBox refile its own files.";
    private const string Network = "Network folders can't be watched in this version.";
    private const string FullPath = "Use a full folder path, like D:\\Scans.";

    private readonly TempFolder _temp = new();
    private readonly FakePlatform _platform;

    public FolderRulesTests() =>
        _platform = new FakePlatform(
            DriveRoot: Path.GetPathRoot(_temp.Root)!,
            SystemFolders: [Folder("Windows"), Folder("ProgramData")],
            CloudRoots: [Folder("Users", "me", "OneDrive")]);

    private sealed record FakePlatform(string DriveRoot, IReadOnlyList<string> SystemFolders,
        IReadOnlyList<string> CloudRoots) : IPlatformPaths
    {
        public bool IsDriveRoot(string path) => string.Equals(Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(DriveRoot), StringComparison.OrdinalIgnoreCase);

        public bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private string Folder(params string[] parts)
    {
        var path = _temp.PathOf(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Output => Folder("SlothBox");

    private FolderCheck Check(string path, params WatchedFolder[] existing) =>
        new FolderRules(_platform).Check(path, existing, Output);

    private static WatchedFolder Watched(int id, string path) =>
        new(id, path, Path.GetFileName(path), FolderMode.Move, false, true);

    [Fact]
    public void An_ordinary_folder_is_allowed_to_move_files() =>
        Check(Folder("Users", "me", "Downloads")).ShouldBe(new FolderCheck.Allowed(FolderMode.Move, false));

    [Fact]
    public void A_drive_root_is_refused() =>
        Check(_platform.DriveRoot).ShouldBe(new FolderCheck.Refused(NotInsideDrive));

    [Fact]
    public void A_system_folder_and_any_folder_inside_one_are_refused()
    {
        Check(Folder("Windows")).ShouldBe(new FolderCheck.Refused(SystemFolder));
        Check(Folder("Windows", "Temp")).ShouldBe(new FolderCheck.Refused(SystemFolder));
        Check(Folder("ProgramData", "Vendor", "Logs")).ShouldBe(new FolderCheck.Refused(SystemFolder));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_a_system_folder_is_allowed() =>
        Check(Folder("Windows Backups")).ShouldBeOfType<FolderCheck.Allowed>();

    [Fact]
    public void The_output_folder_and_any_folder_inside_it_are_refused()
    {
        Check(Output).ShouldBe(new FolderCheck.Refused(OutputFolder));
        Check(Folder("SlothBox", "Receipts", "Food")).ShouldBe(new FolderCheck.Refused(OutputFolder));
    }

    [Fact]
    public void A_folder_inside_containing_or_equal_to_a_watched_folder_is_refused()
    {
        var watched = Watched(1, Folder("Users", "me", "Pictures", "Screenshots"));

        Check(Folder("Users", "me", "Pictures", "Screenshots", "2026"), watched)
            .ShouldBe(new FolderCheck.Refused("This folder is inside a folder SlothBox already watches."));
        Check(Folder("Users", "me", "Pictures"), watched)
            .ShouldBe(new FolderCheck.Refused("This folder contains a folder SlothBox already watches."));
        Check(watched.Path + Path.DirectorySeparatorChar, watched)
            .ShouldBe(new FolderCheck.Refused("SlothBox already watches this folder."));
    }

    [Fact]
    public void A_network_path_is_refused() =>
        Check(@"\\nas\photos").ShouldBe(new FolderCheck.Refused(Network));

    [Theory]
    [InlineData("Users", "me", "OneDrive", "Pictures", "Camera Roll")]
    [InlineData("Google Drive", "Receipts")]
    [InlineData("Dropbox", "Scans")]
    [InlineData("Dropbox (Work)", "Invoices")]
    [InlineData("My Drive", "Camera")]
    [InlineData("Shared drives", "Team", "Scans")]
    public void A_cloud_synced_folder_is_allowed_as_copy_only(params string[] parts) =>
        Check(Folder(parts)).ShouldBe(new FolderCheck.Allowed(FolderMode.Copy, true));

    [Fact]
    public void A_device_path_is_refused_even_when_it_points_into_a_system_folder() =>
        Check(@"\\?\" + Folder("Windows", "Temp")).ShouldBe(new FolderCheck.Refused(FullPath));

    [Theory]
    [InlineData("Scans")]
    [InlineData("D:")]
    public void A_path_that_is_not_a_full_folder_path_is_refused(string path) =>
        Check(path).ShouldBe(new FolderCheck.Refused(FullPath));

    [Fact]
    public void A_missing_folder_is_refused() =>
        Check(_temp.PathOf("Users", "me", "Old stuff")).ShouldBe(new FolderCheck.Refused("Folder not found."));

    [Fact]
    public void A_folder_SlothBox_cannot_open_is_refused()
    {
        var locked = Folder("Users", "someone-else");
        DenyListing(locked, deny: true);
        try
        {
            Check(locked).ShouldBe(new FolderCheck.Refused("SlothBox can't open this folder."));
        }
        finally
        {
            DenyListing(locked, deny: false);
        }
    }

    // Spec 7.4.1 step 1: the output folder, with the watched-folder rules in reverse.
    private string? CheckOutput(string path, params WatchedFolder[] watched) =>
        new FolderRules(_platform).CheckOutput(path, watched);

    [Fact]
    public void An_ordinary_output_folder_is_accepted_whether_or_not_it_exists_yet()
    {
        CheckOutput(Folder("Sorted")).ShouldBeNull();
        CheckOutput(_temp.PathOf("Not yet", "SlothBox")).ShouldBeNull();
    }

    [Fact]
    public void A_cloud_synced_output_folder_is_accepted() =>
        CheckOutput(Folder("Users", "me", "OneDrive", "SlothBox")).ShouldBeNull();

    [Fact]
    public void An_output_folder_at_a_drive_root_in_a_system_folder_or_on_a_network_is_refused()
    {
        CheckOutput(_platform.DriveRoot).ShouldBe(NotInsideDrive);
        CheckOutput(Folder("Windows", "SlothBox")).ShouldBe(SystemFolder);
        CheckOutput(@"\\nas\sorted").ShouldBe("SlothBox can't keep sorted files in a network folder in this version.");
    }

    [Theory]
    [InlineData("SlothBox")]
    [InlineData("D:")]
    public void An_output_path_that_is_not_a_full_folder_path_is_refused(string path) =>
        CheckOutput(path).ShouldBe(OutputFullPath);

    [Fact]
    public void An_output_device_path_is_refused() =>
        CheckOutput(@"\\?\" + Folder("Sorted")).ShouldBe(OutputFullPath);

    [Fact]
    public void An_output_folder_inside_equal_to_or_containing_a_watched_folder_is_refused()
    {
        var watched = Watched(1, Folder("Users", "me", "Downloads"));

        CheckOutput(watched.Path, watched).ShouldBe(InsideWatched);
        CheckOutput(Path.Combine(watched.Path, "Sorted"), watched).ShouldBe(InsideWatched);
        CheckOutput(Folder("Users", "me"), watched).ShouldBe(
            "This folder contains a folder SlothBox watches, so SlothBox would refile its own files. Pick another place.");
        CheckOutput(Folder("Users", "me", "Downloads sorted"), watched).ShouldBeNull();
    }

    [Fact]
    public void An_output_path_that_is_a_file_is_refused()
    {
        var file = _temp.Write("notes.txt", "notes");

        CheckOutput(file).ShouldBe("This is a file, not a folder.");
    }

    private const string OutputFullPath = "Use a full folder path, like D:\\SlothBox.";
    private const string InsideWatched = "SlothBox watches this folder. Pick another place for sorted files.";

    private static void DenyListing(string folder, bool deny)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(folder);
            var security = directory.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
                AccessControlType.Deny);
            if (deny)
                security.AddAccessRule(rule);
            else
                security.RemoveAccessRule(rule);
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(folder, deny ? UnixFileMode.None
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => _temp.Dispose();
}
