using SlothBox.Core;
using SlothBox.Core.Rules;

namespace SlothBox.Tests.Rules;

public sealed class ReviewRulesTests
{
    private static readonly IReadOnlyList<Category> Categories =
    [
        new(1, FileKind.Receipt, "Food", "Restaurants, food delivery, groceries", "Food", false),
        new(3, FileKind.Receipt, "Bills and recharges", "Electricity, mobile, internet", "Bills and recharges", false),
        new(5, FileKind.Receipt, "Other", "Anything that fits no other category", "Other", true),
        new(6, FileKind.Screenshot, "Reddit comments", "Reddit posts and comment threads", "Reddit comments", false),
        new(10, FileKind.Screenshot, "Other", "Anything that fits no other category", "Other", true),
        new(11, FileKind.Document, "Work", "Work documents and reports", "Work", false),
        new(15, FileKind.Document, "Other", "Anything that fits no other category", "Other", true)
    ];

    private static readonly ReceiptData JioReceipt = new("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi);

    private static Analysis Analysis(FileKind type, string category, ReceiptData? receipt = null) =>
        new(type, category, "Jio recharge", "UPI payment to Jio", "", receipt);

    private static Validation Check(AnalysisResult result, FileKind? forcedType = null) =>
        ReviewRules.Check(result, Categories, "INR", forcedType);

    [Fact]
    public void A_good_receipt_is_filed_as_it_is()
    {
        var receipt = Analysis(FileKind.Receipt, "Bills and recharges", JioReceipt);

        Check(new AnalysisResult.Success(receipt)).ShouldBe(new Validation(receipt, null));
    }

    [Fact]
    public void A_category_not_in_the_users_list_becomes_Other() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Screenshot, "Memes")))
            .ShouldBe(new Validation(Analysis(FileKind.Screenshot, "Other"), null));

    [Fact]
    public void A_category_of_another_type_becomes_Other() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Receipt, "Reddit comments", JioReceipt)))
            .Analysis!.Category.ShouldBe("Other");

    [Fact]
    public void A_category_matches_without_regard_to_case() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Receipt, "food", JioReceipt)))
            .Analysis!.Category.ShouldBe("Food");

    [Fact]
    public void A_receipt_without_a_date_needs_review_with_missing_date()
    {
        var undated = Analysis(FileKind.Receipt, "Bills and recharges", JioReceipt with { Date = null });

        Check(new AnalysisResult.Success(undated)).ShouldBe(new Validation(undated, ReviewReason.MissingDate));
    }

    [Fact]
    public void A_receipt_without_an_amount_needs_review_with_missing_amount() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Receipt, "Food", JioReceipt with { Amount = null })))
            .Reason.ShouldBe(ReviewReason.MissingAmount);

    [Fact]
    public void A_receipt_with_no_receipt_fields_needs_review_with_missing_date() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Receipt, "Food")))
            .Reason.ShouldBe(ReviewReason.MissingDate);

    [Fact]
    public void Output_that_was_unreadable_twice_needs_review_with_bad_output() =>
        Check(new AnalysisResult.NeedsReview(ReviewReason.BadOutput))
            .ShouldBe(new Validation(null, ReviewReason.BadOutput));

    [Fact]
    public void An_unknown_type_needs_review() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Unknown, "Other")))
            .Reason.ShouldBe(ReviewReason.UnknownType);

    [Fact]
    public void A_receipt_amount_without_a_currency_gets_the_default_currency() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Receipt, "Food", JioReceipt with { Currency = null })))
            .Analysis!.Receipt!.Currency.ShouldBe("INR");

    [Fact]
    public void Receipt_fields_are_dropped_from_other_types() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Screenshot, "Reddit comments", JioReceipt)))
            .Analysis!.Receipt.ShouldBeNull();

    [Fact]
    public void An_office_file_read_by_name_is_a_document_whatever_type_gemma_gave() =>
        Check(new AnalysisResult.Success(Analysis(FileKind.Unknown, "Work")), forcedType: FileKind.Document)
            .ShouldBe(new Validation(Analysis(FileKind.Document, "Work"), null));
}
