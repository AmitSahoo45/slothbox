using SlothBox.Server.Endpoints;

namespace SlothBox.Tests.Api.Dashboard;

public sealed class ShellFileLauncherTests
{
    // Explorer from the Windows folder, never another explorer.exe that the search path would find first.
    [Fact]
    public void Show_in_folder_starts_Explorer_by_its_full_path_with_the_file_selected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Explorer exists only on Windows.");

        var start = ShellFileLauncher.RevealStart(@"D:\slothbox-test\out\Receipts\bill.pdf");

        start.FileName.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"));
        File.Exists(start.FileName).ShouldBeTrue();
        start.Arguments.ShouldBe("/select,\"D:\\slothbox-test\\out\\Receipts\\bill.pdf\"");
        start.UseShellExecute.ShouldBeFalse();
    }
}
