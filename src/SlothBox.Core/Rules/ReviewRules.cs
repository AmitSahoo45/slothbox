namespace SlothBox.Core.Rules;

// Analysis is what to file (null when Gemma's output was unreadable); Reason is set when the file needs review.
public sealed record Validation(Analysis? Analysis, ReviewReason? Reason);

// Spec 5.2: Needs review is decided by rules, never by the model's confidence.
public static class ReviewRules
{
    // forcedType is set when the extension fixes the type, as for Office files read by name (A8).
    public static Validation Check(AnalysisResult result, IReadOnlyList<Category> categories,
        string defaultCurrency, FileKind? forcedType = null)
    {
        if (result is AnalysisResult.NeedsReview review)
            return new Validation(review.PartialAnalysis, review.Reason);

        var analysis = ((AnalysisResult.Success)result).Analysis;
        var type = forcedType ?? analysis.Type;
        // Gemma must pick from the user's list for that type; anything else goes to that type's Other.
        var category = categories.FirstOrDefault(c =>
                c.Type == type && string.Equals(c.Name, analysis.Category, StringComparison.OrdinalIgnoreCase))?.Name
            ?? categories.FirstOrDefault(c => c.Type == type && c.IsSystem)?.Name
            ?? "Other";
        var receipt = type == FileKind.Receipt ? analysis.Receipt : null;
        if (receipt is { Amount: not null, Currency: null })
            receipt = receipt with { Currency = defaultCurrency };

        ReviewReason? reason = type switch
        {
            FileKind.Unknown => ReviewReason.UnknownType,
            FileKind.Receipt when receipt?.Date is null => ReviewReason.MissingDate,
            FileKind.Receipt when receipt!.Amount is null => ReviewReason.MissingAmount,
            _ => null
        };
        return new Validation(analysis with { Type = type, Category = category, Receipt = receipt }, reason);
    }
}
