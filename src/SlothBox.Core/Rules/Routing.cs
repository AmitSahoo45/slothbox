namespace SlothBox.Core.Rules;

// Kind is fixed by the extension when set; OtherCategory names the Other\ folder for files Gemma never reads.
public sealed record FileRoute(bool AskGemma, FileKind? Kind, string? OtherCategory);

// The "Gemma call" and "Type" columns of spec 5.3.
public static class Routing
{
    public static FileRoute For(string path) => FileGroups.Of(path) switch
    {
        FileGroup.Image or FileGroup.ReadableDocument => new FileRoute(true, null, null),
        FileGroup.UnparsedOffice => new FileRoute(true, FileKind.Document, null),
        FileGroup.Installer => new FileRoute(false, FileKind.Other, "Installers"),
        FileGroup.Archive => new FileRoute(false, FileKind.Other, "Archives"),
        FileGroup.Media => new FileRoute(false, FileKind.Other, "Media"),
        FileGroup.Heic => new FileRoute(false, null, null),
        _ => new FileRoute(false, FileKind.Other, "Misc")
    };
}
