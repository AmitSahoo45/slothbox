namespace SlothBox.Core.Gate;

public static class GateSkipReason
{
    // Download, Office and shell leftovers from spec 5.3, plus SlothBox's own copy-in-progress files.
    public const string Temporary = "temporary";
    public const string Hidden = "hidden";
    public const string System = "system";
    public const string Baseline = "baseline";
    public const string Ignored = "ignored";
    public const string Missing = "missing";

    // The pipeline's own reasons: the job's watched folder is gone, or switched off, since the job was queued.
    public const string FolderRemoved = "folder_removed";
    public const string FolderDisabled = "folder_disabled";
}
