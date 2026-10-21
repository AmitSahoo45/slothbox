namespace SlothBox.Core;

// SlothBox's own partial copies (spec 6.4): `<to>.<action id>.slothbox-tmp`, one name per action.
public static class TempFiles
{
    public const string Suffix = ".slothbox-tmp";

    public static string For(string destination, long actionId) => $"{destination}.{actionId}{Suffix}";

    // A destination is taken while a file is there or while any action's temp copy of it is beside it (spec 5.5):
    // that copy may be the only copy of another file, and its move will want the name next.
    public static bool IsTaken(string destination)
    {
        if (File.Exists(destination))
            return true;
        var folder = Path.GetDirectoryName(destination);
        if (folder is null || !Directory.Exists(folder))
            return false;
        var prefix = Path.GetFileName(destination) + ".";
        return Directory.EnumerateFiles(folder, "*" + Suffix)
            .Any(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
