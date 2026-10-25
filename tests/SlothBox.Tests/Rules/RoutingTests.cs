using SlothBox.Core;
using SlothBox.Core.Rules;

namespace SlothBox.Tests.Rules;

public sealed class RoutingTests
{
    [Theory]
    [InlineData("IMG_2041.jpg", true, null, null)]
    [InlineData("IMG_2041.jpeg", true, null, null)]
    [InlineData("Screenshot.PNG", true, null, null)]
    [InlineData("sticker.webp", true, null, null)]
    [InlineData("bill.pdf", true, null, null)]
    [InlineData("letter.docx", true, null, null)]
    [InlineData("todo.txt", true, null, null)]
    [InlineData("notes.md", true, null, null)]
    [InlineData("expenses.csv", true, null, null)]
    [InlineData("budget.xlsx", true, FileKind.Document, null)]
    [InlineData("talk.pptx", true, FileKind.Document, null)]
    [InlineData("letter.doc", true, FileKind.Document, null)]
    [InlineData("budget.xls", true, FileKind.Document, null)]
    [InlineData("old.ppt", true, FileKind.Document, null)]
    [InlineData("ChromeSetup.exe", false, FileKind.Other, "Installers")]
    [InlineData("Tool.msi", false, FileKind.Other, "Installers")]
    [InlineData("App.msix", false, FileKind.Other, "Installers")]
    [InlineData("Game.appx", false, FileKind.Other, "Installers")]
    [InlineData("App.appinstaller", false, FileKind.Other, "Installers")]
    [InlineData("photos.zip", false, FileKind.Other, "Archives")]
    [InlineData("backup.7z", false, FileKind.Other, "Archives")]
    [InlineData("old.rar", false, FileKind.Other, "Archives")]
    [InlineData("src.tar", false, FileKind.Other, "Archives")]
    [InlineData("logs.tar.gz", false, FileKind.Other, "Archives")]
    [InlineData("ubuntu.iso", false, FileKind.Other, "Archives")]
    [InlineData("clip.mp4", false, FileKind.Other, "Media")]
    [InlineData("film.mkv", false, FileKind.Other, "Media")]
    [InlineData("video.mov", false, FileKind.Other, "Media")]
    [InlineData("song.mp3", false, FileKind.Other, "Media")]
    [InlineData("voice.wav", false, FileKind.Other, "Media")]
    [InlineData("memo.m4a", false, FileKind.Other, "Media")]
    [InlineData("funny.gif", false, FileKind.Other, "Media")]
    [InlineData("data.json", false, FileKind.Other, "Misc")]
    [InlineData("LICENSE", false, FileKind.Other, "Misc")]
    [InlineData("IMG_0001.heic", false, null, null)]
    [InlineData("IMG_0002.HEIF", false, null, null)]
    public void Each_extension_is_routed_as_in_the_table(string name, bool askGemma, FileKind? kind,
        string? otherCategory) =>
        Routing.For(name).ShouldBe(new FileRoute(askGemma, kind, otherCategory));
}
