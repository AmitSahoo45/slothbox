using SlothBox.Core.Folders;

namespace SlothBox.Tests.Folders;

// The machine's facts behind the folder rules (spec 4.5).
public sealed class PlatformPathsTests
{
    [Fact]
    public void The_Windows_program_and_app_data_folders_are_system_folders()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows folders exist only on Windows.");

        var folders = new PlatformPaths().SystemFolders;

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData
                 })
            folders.ShouldContain(Environment.GetFolderPath(special));
        folders.ShouldNotContain(string.Empty);
    }

    [Fact]
    public void The_OneDrive_roots_come_from_its_environment_variables()
    {
        var variables = new Dictionary<string, string>
        {
            ["OneDrive"] = @"C:\Users\someone\OneDrive",
            ["OneDriveCommercial"] = @"C:\Users\someone\OneDrive - Contoso"
        };

        var roots = new PlatformPaths(name => variables.GetValueOrDefault(name)).CloudRoots;

        roots.ShouldBe([@"C:\Users\someone\OneDrive", @"C:\Users\someone\OneDrive - Contoso"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\Scans", false)]
    [InlineData(@"D:\Scans\2026", false)]
    public void Drive_roots_are_told_apart_from_folders(string path, bool root)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters exist only on Windows.");

        new PlatformPaths().IsDriveRoot(path).ShouldBe(root);
    }

    [Theory]
    [InlineData(@"\\nas\share\scans", true)]
    [InlineData(@"\\nas\share", true)]
    [InlineData(@"D:\Scans", false)]
    public void Shares_are_network_paths(string path, bool network)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths exist only on Windows.");

        new PlatformPaths().IsNetworkPath(path).ShouldBe(network);
    }
}
