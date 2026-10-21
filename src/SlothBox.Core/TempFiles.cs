namespace SlothBox.Core;

// SlothBox writes a copy as <name>.slothbox-tmp and renames it once complete (spec 6.4, 7.9).
public static class TempFiles
{
    public const string Suffix = ".slothbox-tmp";
}
