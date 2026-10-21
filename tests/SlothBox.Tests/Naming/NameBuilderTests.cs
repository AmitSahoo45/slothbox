using System.Globalization;
using SlothBox.Core;
using SlothBox.Core.Naming;

namespace SlothBox.Tests.Naming;

public sealed class NameBuilderTests
{
    private const string Bills = @"D:\SlothBox\Receipts\Bills and recharges";
    private const string Learning = @"D:\SlothBox\Documents\Learning";

    private static readonly NameBuilder Names = new(new PipelineSettings());

    private static SourceFileInfo Source(string originalName, DateTimeOffset? createdAt = null) =>
        new($@"D:\slothbox-test\watched\{originalName}", originalName, Path.GetExtension(originalName),
            createdAt ?? new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(5.5)), 2048, @"D:\SlothBox");

    private static Analysis Receipt(string? vendor, decimal amount) =>
        new(FileKind.Receipt, "Bills and recharges", "Jio recharge", "", "",
            new ReceiptData(vendor, new DateOnly(2026, 10, 20), amount, "INR", PaymentMethod.Upi));

    private static Analysis Document(string title) => new(FileKind.Document, "Learning", title, "", "", null);

    [Fact]
    public void A_receipt_is_named_date_vendor_amount_and_currency() =>
        Names.BuildFileName(Receipt("Jio", 599m), Source("IMG_2041.jpg"), Bills).ShouldBe("2026-10-20_Jio_599INR.jpg");

    [Theory]
    [InlineData("599.5", "2026-10-20_Jio_599.50INR.jpg")]
    [InlineData("599.00", "2026-10-20_Jio_599INR.jpg")]
    [InlineData("1234.565", "2026-10-20_Jio_1234.57INR.jpg")]
    public void Amounts_are_whole_numbers_or_have_two_decimals(string amount, string expected) =>
        Names.BuildFileName(Receipt("Jio", decimal.Parse(amount, CultureInfo.InvariantCulture)),
            Source("IMG_2041.jpg"), Bills).ShouldBe(expected);

    [Theory]
    [InlineData("Reliance Digital", "2026-10-20_RelianceDigital_599INR.jpg")]
    [InlineData("The Very Long Name Of A Supermarket Chain", "2026-10-20_TheVeryLongNameOfASupermarketC_599INR.jpg")]
    [InlineData(null, "2026-10-20_599INR.jpg")]
    public void Vendor_words_are_joined_in_their_own_case_up_to_30_characters(string? vendor, string expected) =>
        Names.BuildFileName(Receipt(vendor, 599m), Source("IMG_2041.jpg"), Bills).ShouldBe(expected);

    [Fact]
    public void A_screenshot_is_named_by_its_local_creation_date_and_title()
    {
        var screenshot = new Analysis(FileKind.Screenshot, "Reddit comments", "Reddit thread on dotnet 10", "", "", null);
        var createdJustAfterMidnight = new DateTimeOffset(2026, 10, 21, 0, 30, 0, TimeSpan.FromHours(5.5));

        Names.BuildFileName(screenshot, Source("Screenshot 2026-10-21 003000.png", createdJustAfterMidnight),
                @"D:\SlothBox\Screenshots\Reddit comments")
            .ShouldBe("2026-10-21_reddit-thread-on-dotnet-10.png");
    }

    [Fact]
    public void A_document_is_named_by_its_title() =>
        Names.BuildFileName(Document("dotnet 10 release notes"), Source("download (3).pdf"), Learning)
            .ShouldBe("dotnet-10-release-notes.pdf");

    [Fact]
    public void A_document_without_a_title_keeps_its_original_name() =>
        Names.BuildFileName(Document(""), Source("Scan 0001.PDF"), Learning).ShouldBe("Scan 0001.pdf");

    [Fact]
    public void Other_files_keep_their_original_name()
    {
        var installer = new Analysis(FileKind.Other, "Installers", "", "", "", null);

        Names.BuildFileName(installer, Source("ChromeSetup.exe"), @"D:\SlothBox\Other\Installers")
            .ShouldBe("ChromeSetup.exe");
    }

    [Fact]
    public void The_extension_is_lower_case_and_an_original_jpeg_keeps_jpeg() =>
        Names.BuildFileName(Receipt("Jio", 599m), Source("IMG_2041.JPEG"), Bills).ShouldBe("2026-10-20_Jio_599INR.jpeg");

    [Theory]
    [InlineData("Invoice: what is due?", "invoice-what-is-due.pdf")]
    [InlineData("a<b>c|d\"e/f\\g*h", "abcdefgh.pdf")]
    [InlineData("\u0001bell\u0007", "bell.pdf")]
    [InlineData("CON", "con_.pdf")]
    [InlineData("lpt9", "lpt9_.pdf")]
    [InlineData("notes . .", "notes.pdf")]
    public void Titles_become_valid_windows_names(string title, string expected) =>
        Names.BuildFileName(Document(title), Source("x.pdf"), Learning).ShouldBe(expected);

    [Fact]
    public void A_300_character_title_gives_a_name_of_at_most_80_characters()
    {
        var name = Names.BuildFileName(Document(string.Join(' ', Enumerable.Repeat("word", 60))), Source("x.pdf"),
            Learning);

        name.Length.ShouldBeLessThanOrEqualTo(80);
        name.ShouldStartWith("word-word-");
        name.ShouldEndWith("word.pdf");
    }

    [Fact]
    public void The_title_is_cut_so_the_whole_path_stays_under_240_characters()
    {
        var deepFolder = @"D:\SlothBox\Documents\" + new string('f', 190);

        var name = Names.BuildFileName(Document("dotnet 10 release notes for the whole team"), Source("x.pdf"),
            deepFolder);

        (deepFolder.Length + 1 + name.Length).ShouldBeLessThan(240);
        name.ShouldStartWith("dotnet-");
        name.ShouldEndWith(".pdf");
    }

    [Fact]
    public void A_name_that_is_taken_gets_minus_2_then_minus_3()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Bills, "2026-10-20_Jio_599INR.jpg")
        };

        Names.ResolveClash(Bills, "2026-10-20_Jio_599INR.jpg", taken.Contains).ShouldBe("2026-10-20_Jio_599INR-2.jpg");
        taken.Add(Path.Combine(Bills, "2026-10-20_Jio_599INR-2.jpg"));
        Names.ResolveClash(Bills, "2026-10-20_Jio_599INR.jpg", taken.Contains).ShouldBe("2026-10-20_Jio_599INR-3.jpg");
    }

    [Fact]
    public void A_name_whose_temp_copy_is_still_there_counts_as_taken()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Bills, "2026-10-20_Jio_599INR.jpg.slothbox-tmp")
        };

        Names.ResolveClash(Bills, "2026-10-20_Jio_599INR.jpg", taken.Contains).ShouldBe("2026-10-20_Jio_599INR-2.jpg");
    }

    [Fact]
    public void A_vendor_cut_never_splits_an_emoji() =>
        Names.BuildFileName(Receipt(new string('A', 29) + "😀", 599m), Source("IMG_2041.jpg"), Bills)
            .ShouldBe($"2026-10-20_{new string('A', 29)}_599INR.jpg");

    [Fact]
    public void A_free_name_is_kept() =>
        Names.ResolveClash(Bills, "2026-10-20_Jio_599INR.jpg", _ => false).ShouldBe("2026-10-20_Jio_599INR.jpg");

    [Theory]
    [InlineData(FileKind.Receipt, "Food", "Receipts")]
    [InlineData(FileKind.Screenshot, "Chats", "Screenshots")]
    [InlineData(FileKind.Document, "Work", "Documents")]
    [InlineData(FileKind.Other, "Installers", "Other")]
    public void Target_folders_follow_the_output_layout(FileKind kind, string categoryFolder, string typeFolder) =>
        Names.BuildTargetFolder(@"D:\SlothBox", kind, categoryFolder)
            .ShouldBe(Path.Combine(@"D:\SlothBox", typeFolder, categoryFolder));
}
