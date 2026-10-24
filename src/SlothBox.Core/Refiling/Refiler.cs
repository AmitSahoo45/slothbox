using SlothBox.Core.Pipeline;

namespace SlothBox.Core.Refiling;

// The type, category and fields a file is refiled with. Title and receipt are optional: a missing title keeps the
// file's own; a receipt is required only for the receipt type.
public sealed record RefileChange(FileKind Type, int CategoryId, string? Title, ReceiptData? Receipt);

public abstract record RefileResult
{
    public sealed record Done(FileRecord File) : RefileResult;

    // The change itself cannot be filed (the API answers 400).
    public sealed record Invalid(string Message) : RefileResult;

    // The file cannot be refiled now (the API answers 409).
    public sealed record Refused(string Message) : RefileResult;
}

// Spec 6.6 "Fix from the UI" and 7.4.3 "File it": changing a file's type or category is a normal move action, so it
// can be undone; resolving a Needs review item is the same refile.
public sealed class Refiler(
    IFileRepository files,
    IReceiptRepository receipts,
    ICategoryRepository categories,
    INameBuilder names,
    ISafeMover mover,
    IActionLog log,
    IEmbedder embedder,
    ISettingsStore settings,
    Func<string, bool>? isTaken = null)
{
    // The ledger's amount is numeric(12,2): at most 10 digits before the point.
    private const decimal AmountLimit = 10_000_000_000m;

    private readonly Func<string, bool> _isTaken = isTaken ?? TempFiles.IsTaken;

    public async Task<RefileResult> RefileAsync(FileRecord file, RefileChange change, CancellationToken ct)
    {
        if (file.Status == FileStatus.Duplicate)
            return new RefileResult.Refused("This file is a duplicate of one SlothBox already filed.");
        if (file.Status == FileStatus.Undone)
            return new RefileResult.Refused("This file was undone and is back in its folder.");
        if (change.Type == FileKind.Unknown)
            return new RefileResult.Invalid("Choose a type for this file.");
        var category = (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == change.CategoryId);
        if (category is null || category.Type != change.Type)
            return new RefileResult.Invalid("Choose a category of that type.");
        ReceiptData? receipt = null;
        if (change.Type == FileKind.Receipt)
        {
            if (Receipt(change.Receipt) is not { } checkedReceipt)
                return new RefileResult.Invalid(ReceiptProblem(change.Receipt));
            receipt = checkedReceipt;
        }
        var title = StoredText.Clean(change.Title ?? file.Title).Trim();
        var before = new FileSnapshot(file.Status, file.Type, file.CategoryId, file.Title, file.ReviewReason,
            await receipts.GetAsync(file.Id, ct));
        var after = new FileSnapshot(FileStatus.Filed, change.Type, category.Id, title, null, receipt);
        // Saving what is already there changes nothing; answering as if it had would offer an Undo that reverses
        // the file's previous filing instead.
        if (after == before)
            return new RefileResult.Invalid("Nothing to change.");

        var outputRoot = settings.Current.OutputRoot;
        var targetFolder = names.BuildTargetFolder(outputRoot, change.Type, category.Folder);
        var fileName = names.BuildFileName(new Analysis(change.Type, category.Name, title, file.Caption,
                file.ExtractedText, receipt),
            new SourceFileInfo(file.CurrentPath, file.OriginalName, Path.GetExtension(file.CurrentPath),
                file.FileDate.ToLocalTime(), file.SizeBytes, outputRoot), targetFolder);
        // The file does not clash with itself, so a "-2" file whose name stays keeps its suffix.
        var target = Path.Combine(targetFolder, names.ResolveClash(targetFolder, fileName,
            path => !SamePath(path, file.CurrentPath) && _isTaken(path)));
        if (SamePath(target, file.CurrentPath))
            return await UpdateInPlaceAsync(file, category, before, after, ct);

        var moved = await mover.MoveAsync(
            new MoveRequest(file.Id, ActionKind.Move, file.CurrentPath, target, file.Sha256, before, after), ct);
        if (!moved.Succeeded)
            return new RefileResult.Refused(moved.Error ?? "The file could not be moved.");
        var refiled = await files.GetAsync(file.Id, ct) ?? file;
        await FileIndex.EmbedAsync(refiled, category.Name, embedder, files, settings.Current.Pipeline, ct);
        return new RefileResult.Done(refiled);
    }

    // The name and folder stay, so only fields change: nothing moves and no action is logged, under the same
    // per-file lock and checks as a move (spec 6.4).
    private async Task<RefileResult> UpdateInPlaceAsync(FileRecord file, Category category, FileSnapshot before,
        FileSnapshot after, CancellationToken ct)
    {
        if (!await log.UpdateFieldsAsync(file.Id, file.CurrentPath, before, after, ct))
            return new RefileResult.Refused("This file changed while SlothBox was working on it. Try again.");
        var updated = await files.GetAsync(file.Id, ct) ?? file;
        // The vector reads the title and category (spec 5.6).
        if (after.Title != before.Title || after.CategoryId != before.CategoryId)
            await FileIndex.EmbedAsync(updated, category.Name, embedder, files, settings.Current.Pipeline, ct);
        return new RefileResult.Done(updated);
    }

    // A receipt needs a date and an amount the ledger can hold; a missing currency takes the default (spec 5.2).
    private ReceiptData? Receipt(ReceiptData? receipt)
    {
        if (receipt is not { Date: not null, Amount: { } amount } || amount <= 0 || amount >= AmountLimit)
            return null;
        var currency = (receipt.Currency ?? settings.Current.Pipeline.DefaultCurrency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            return null;
        var vendor = string.IsNullOrWhiteSpace(receipt.Vendor) ? null : StoredText.Clean(receipt.Vendor).Trim();
        return receipt with
        {
            Vendor = vendor, Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero), Currency = currency
        };
    }

    private static string ReceiptProblem(ReceiptData? receipt) => receipt switch
    {
        null or { Date: null } or { Amount: null } => "A receipt needs a date and an amount.",
        { Amount: <= 0 or >= AmountLimit } => "Enter an amount above 0 and below 10,000,000,000.",
        _ => "Use a 3-letter currency code, like INR."
    };

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
