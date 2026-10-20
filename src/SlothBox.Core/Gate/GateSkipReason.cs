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
}
