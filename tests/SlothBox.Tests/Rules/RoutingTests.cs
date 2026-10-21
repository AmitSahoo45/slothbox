using SlothBox.Core;
using SlothBox.Core.Rules;

namespace SlothBox.Tests.Rules;

public sealed class RoutingTests
{
    [Theory]
    [InlineData("IMG_2041.jpg", true, null, null)]
    [InlineData("Screenshot.PNG", true, null, null)]
    [InlineData("bill.pdf", true, null, null)]
    [InlineData("notes.md", true, null, null)]
    [InlineData("budget.xlsx", true, FileKind.Document, null)]
    [InlineData("old.ppt", true, FileKind.Document, null)]
    [InlineData("ChromeSetup.exe", false, FileKind.Other, "Installers")]
    [InlineData("App.msix", false, FileKind.Other, "Installers")]
    [InlineData("backup.7z", false, FileKind.Other, "Archives")]
    [InlineData("logs.tar.gz", false, FileKind.Other, "Archives")]
    [InlineData("song.mp3", false, FileKind.Other, "Media")]
    [InlineData("funny.gif", false, FileKind.Other, "Media")]
    [InlineData("data.json", false, FileKind.Other, "Misc")]
    [InlineData("LICENSE", false, FileKind.Other, "Misc")]
    [InlineData("IMG_0001.heic", false, null, null)]
    public void Each_extension_is_routed_as_in_the_table(string name, bool askGemma, FileKind? kind,
        string? otherCategory) =>
        Routing.For(name).ShouldBe(new FileRoute(askGemma, kind, otherCategory));
}
