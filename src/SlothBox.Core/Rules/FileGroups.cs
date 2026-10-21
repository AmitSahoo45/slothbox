namespace SlothBox.Core.Rules;

// The rows of spec 5.3 by extension. Always-skipped files are the gate's concern.
public enum FileGroup { Image, ReadableDocument, UnparsedOffice, Installer, Archive, Media, Heic, Misc }

public static class FileGroups
{
    private static readonly Dictionary<string, FileGroup> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = FileGroup.Image, [".jpeg"] = FileGroup.Image, [".png"] = FileGroup.Image,
        [".webp"] = FileGroup.Image,
        [".pdf"] = FileGroup.ReadableDocument, [".docx"] = FileGroup.ReadableDocument,
        [".txt"] = FileGroup.ReadableDocument, [".md"] = FileGroup.ReadableDocument,
        [".csv"] = FileGroup.ReadableDocument,
        [".xlsx"] = FileGroup.UnparsedOffice, [".pptx"] = FileGroup.UnparsedOffice,
        [".doc"] = FileGroup.UnparsedOffice, [".xls"] = FileGroup.UnparsedOffice,
        [".ppt"] = FileGroup.UnparsedOffice,
        [".exe"] = FileGroup.Installer, [".msi"] = FileGroup.Installer, [".msix"] = FileGroup.Installer,
        [".appx"] = FileGroup.Installer, [".appinstaller"] = FileGroup.Installer,
        [".zip"] = FileGroup.Archive, [".7z"] = FileGroup.Archive, [".rar"] = FileGroup.Archive,
        [".tar"] = FileGroup.Archive, [".gz"] = FileGroup.Archive, [".iso"] = FileGroup.Archive,
        [".mp4"] = FileGroup.Media, [".mkv"] = FileGroup.Media, [".mov"] = FileGroup.Media,
        [".mp3"] = FileGroup.Media, [".wav"] = FileGroup.Media, [".m4a"] = FileGroup.Media,
        [".gif"] = FileGroup.Media,
        [".heic"] = FileGroup.Heic, [".heif"] = FileGroup.Heic
    };

    public static FileGroup Of(string path) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(path), FileGroup.Misc);
}
