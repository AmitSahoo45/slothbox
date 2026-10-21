using System.Globalization;

namespace SlothBox.Core.Naming;

// Spec 5.5. Gemma never writes a file name: it returns fields, and the name is built from them here.
public sealed class NameBuilder(PipelineSettings settings) : INameBuilder
{
    private const int MaxVendorLength = 30;

    // The characters Windows refuses in a name, whatever system the code runs on.
    private static readonly HashSet<char> Refused = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    public string BuildTargetFolder(string outputRoot, FileKind kind, string categoryFolder) =>
        Path.Combine(outputRoot, kind switch
        {
            FileKind.Receipt => "Receipts",
            FileKind.Screenshot => "Screenshots",
            FileKind.Document => "Documents",
            FileKind.Other => "Other",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown files go to Needs review.")
        }, categoryFolder);

    public string BuildFileName(Analysis analysis, SourceFileInfo info, string targetFolder)
    {
        if (analysis.Type == FileKind.Other)
            return info.OriginalName;

        var extension = info.Extension.ToLowerInvariant();
        var limit = NameLimit(targetFolder);
        var title = Slug(analysis.Title);
        if (title.Length == 0)
            title = Clean(Path.GetFileNameWithoutExtension(info.OriginalName));
        return analysis.Type switch
        {
            FileKind.Receipt => Compose("", ReceiptName(analysis.Receipt), extension, limit),
            FileKind.Screenshot =>
                Compose($"{info.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_", title, extension,
                    limit),
            _ => Compose("", title, extension, limit)
        };
    }

    public string ResolveClash(string targetFolder, string fileName, Func<string, bool> isTaken)
    {
        // A name whose temp copy is still there is taken too: that temp may be the only copy of another file.
        bool Taken(string name)
        {
            var path = Path.Combine(targetFolder, name);
            return isTaken(path) || isTaken(path + TempFiles.Suffix);
        }

        if (!Taken(fileName))
            return fileName;
        var extension = Path.GetExtension(fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var limit = NameLimit(targetFolder);
        for (var n = 2; ; n++)
        {
            var suffix = $"-{n}";
            var room = Math.Max(0, limit - suffix.Length - extension.Length);
            var candidate = (name.Length > room ? StoredText.Cut(name, room).TrimEnd('.', ' ', '-') : name) +
                suffix + extension;
            if (!Taken(candidate))
                return candidate;
        }
    }

    // At most MaxFileNameLength, and short enough that folder + separator + name stays under MaxPathLength.
    private int NameLimit(string targetFolder) =>
        Math.Min(settings.MaxFileNameLength, settings.MaxPathLength - 2 - targetFolder.Length);

    private static string ReceiptName(ReceiptData? receipt)
    {
        var parts = new List<string>();
        if (receipt?.Date is { } date)
            parts.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var vendor = string.Concat(Clean(receipt?.Vendor ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (vendor.Length > 0)
            parts.Add(StoredText.Cut(vendor, MaxVendorLength));
        if (receipt?.Amount is { } amount)
            parts.Add(Amount(amount) + Clean(receipt.Currency ?? "").ToUpperInvariant());
        return string.Join('_', parts);
    }

    // Whole amounts have no decimals; others have two, rounded the way the ledger stores them.
    private static string Amount(decimal amount)
    {
        var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return rounded.ToString(rounded == decimal.Truncate(rounded) ? "0" : "0.00", CultureInfo.InvariantCulture);
    }

    // Lowercase words joined with hyphens.
    private static string Slug(string title) =>
        string.Join('-', Clean(title).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim('-', '.');

    private static string Clean(string text) =>
        string.Concat(StoredText.Clean(text).Where(c => !Refused.Contains(c) && !char.IsControl(c)));

    // Cuts the title to fit (at a hyphen where possible), drops trailing dots and spaces, and guards reserved names.
    private static string Compose(string prefix, string title, string extension, int limit)
    {
        var room = Math.Max(0, limit - prefix.Length - extension.Length);
        if (title.Length > room)
        {
            var cut = StoredText.Cut(title, room);
            var lastBreak = cut.LastIndexOf('-');
            title = title[cut.Length] != '-' && lastBreak > 0 ? cut[..lastBreak] : cut;
        }
        var name = (prefix + title).TrimEnd('.', ' ', '-');
        if (ReservedNames.Contains(name))
            name += "_";
        return name + extension;
    }
}
