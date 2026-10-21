using System.Text;
using SkiaSharp;
using SlothBox.Core;
using SlothBox.Core.Reading;

namespace SlothBox.Tests.Reading;

public sealed class ContentReaderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private Task<FileContent> Read(string path) =>
        new ContentReader(new PipelineSettings()).ReadAsync(path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_large_image_is_resized_to_a_1600_px_long_edge_and_encoded_as_jpeg()
    {
        var path = TestFiles.Image(_temp.PathOf("screenshot.png"), 3200, 1600, SKEncodedImageFormat.Png);

        var image = (await Read(path)).ShouldBeOfType<FileContent.Image>();

        TestFiles.Inspect(image.JpegBytes).ShouldBe((SKEncodedImageFormat.Jpeg, 1600, 800));
    }

    [Fact]
    public async Task A_small_image_keeps_its_size()
    {
        var path = TestFiles.Image(_temp.PathOf("sticker.webp"), 640, 400, SKEncodedImageFormat.Webp);

        var image = (await Read(path)).ShouldBeOfType<FileContent.Image>();

        TestFiles.Inspect(image.JpegBytes).ShouldBe((SKEncodedImageFormat.Jpeg, 640, 400));
    }

    // Stored 400 x 200 with red on the left and blue on the right.
    [Theory]
    [InlineData(1, 400, 200, 100, 100, 300, 100)]
    [InlineData(3, 400, 200, 300, 100, 100, 100)]
    [InlineData(6, 200, 400, 100, 100, 100, 300)]
    [InlineData(8, 200, 400, 100, 300, 100, 100)]
    public async Task A_photo_is_turned_upright_from_its_exif_orientation(int orientation, int width, int height,
        int redX, int redY, int blueX, int blueY)
    {
        var path = TestFiles.JpegWithOrientation(_temp.PathOf("IMG_2041.jpg"), 400, 200, orientation);

        var image = (await Read(path)).ShouldBeOfType<FileContent.Image>();

        using var bitmap = SKBitmap.Decode(image.JpegBytes);
        (bitmap.Width, bitmap.Height).ShouldBe((width, height));
        TestFiles.IsRed(bitmap.GetPixel(redX, redY)).ShouldBeTrue();
        TestFiles.IsBlue(bitmap.GetPixel(blueX, blueY)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_pdf_gives_the_text_of_its_pages()
    {
        var path = TestFiles.Pdf(_temp.PathOf("bill.pdf"), "Electricity bill for October 2026 from the power board",
            "Amount due 1450 INR by 5 November 2026");

        var text = (await Read(path)).ShouldBeOfType<FileContent.Text>().Value;

        text.ShouldContain("Electricity bill for October 2026 from the power board");
        text.ShouldContain("Amount due 1450 INR by 5 November 2026");
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public async Task A_pdf_with_under_50_characters_of_text_in_its_first_three_pages_counts_as_scanned(
        int characters, bool scanned)
    {
        var path = TestFiles.Pdf(_temp.PathOf("scan.pdf"), new string('x', characters), "", "",
            "Page four has plenty of text, but only the first three pages decide.");

        var content = await Read(path);

        if (scanned)
            content.ShouldBe(new FileContent.Unsupported(ReviewReason.ScannedPdf));
        else
            content.ShouldBeOfType<FileContent.Text>();
    }

    [Fact]
    public async Task A_password_protected_pdf_needs_review() =>
        (await Read(TestFiles.EncryptedPdf(_temp.PathOf("statement.pdf"))))
            .ShouldBe(new FileContent.Unsupported(ReviewReason.PasswordProtected));

    [Fact]
    public async Task A_docx_gives_its_paragraphs_one_per_line()
    {
        var path = TestFiles.Docx(_temp.PathOf("notes.docx"), "Release notes for .NET 10", "Second paragraph");

        (await Read(path)).ShouldBe(new FileContent.Text("Release notes for .NET 10\nSecond paragraph"));
    }

    [Fact]
    public async Task A_password_protected_docx_needs_review() =>
        (await Read(TestFiles.EncryptedDocx(_temp.PathOf("salary.docx"))))
            .ShouldBe(new FileContent.Unsupported(ReviewReason.PasswordProtected));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("README.md")]
    [InlineData("expenses.csv")]
    public async Task Plain_text_files_are_read_as_utf8(string name)
    {
        var path = _temp.PathOf(name);
        File.WriteAllText(path, "Paid ₹599 to Jio", new UTF8Encoding(false));

        (await Read(path)).ShouldBe(new FileContent.Text("Paid ₹599 to Jio"));
    }

    [Fact]
    public async Task Stored_text_stops_at_20000_characters()
    {
        var textFile = _temp.PathOf("long.txt");
        File.WriteAllText(textFile, new string('a', 25_000));
        var docx = TestFiles.Docx(_temp.PathOf("long.docx"), new string('b', 25_000));

        (await Read(textFile)).ShouldBeOfType<FileContent.Text>().Value.Length.ShouldBe(20_000);
        (await Read(docx)).ShouldBeOfType<FileContent.Text>().Value.Length.ShouldBe(20_000);
    }

    [Theory]
    [InlineData("IMG_0001.heic")]
    [InlineData("IMG_0002.HEIF")]
    public async Task Heic_photos_need_review(string name)
    {
        var path = _temp.Write(name, "not decoded in v1");

        (await Read(path)).ShouldBe(new FileContent.Unsupported(ReviewReason.Heic));
    }

    [Theory]
    [InlineData("budget.xlsx")]
    [InlineData("deck.pptx")]
    [InlineData("old.doc")]
    [InlineData("old.xls")]
    [InlineData("old.ppt")]
    [InlineData("ChromeSetup.exe")]
    [InlineData("backup.zip")]
    [InlineData("clip.mp4")]
    [InlineData("funny.gif")]
    [InlineData("settings.json")]
    [InlineData("LICENSE")]
    public async Task Other_files_are_read_by_name_only(string name)
    {
        var path = _temp.Write(name, "content SlothBox does not read");

        (await Read(path)).ShouldBe(new FileContent.NameOnly());
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("bill.pdf")]
    [InlineData("notes.docx")]
    public async Task A_file_that_cannot_be_parsed_is_read_by_name_only(string name)
    {
        var path = _temp.Write(name, "this is not really that kind of file");

        (await Read(path)).ShouldBe(new FileContent.NameOnly());
    }

    [Fact]
    public async Task An_image_that_claims_an_enormous_size_is_read_by_name_only() =>
        (await Read(TestFiles.PngClaiming(_temp.PathOf("tiny.png"), 500_000, 500_000)))
            .ShouldBe(new FileContent.NameOnly());

    [Fact]
    public async Task Stored_text_never_ends_in_half_an_emoji()
    {
        var textFile = _temp.PathOf("chat.txt");
        File.WriteAllText(textFile, new string('a', 19_999) + "😀 and more", new UTF8Encoding(false));
        var docx = TestFiles.Docx(_temp.PathOf("chat.docx"), new string('b', 19_999) + "😀 and more");

        (await Read(textFile)).ShouldBe(new FileContent.Text(new string('a', 19_999)));
        (await Read(docx)).ShouldBe(new FileContent.Text(new string('b', 19_999)));
    }

    [Fact]
    public async Task Characters_the_database_cannot_store_are_removed_and_line_breaks_kept()
    {
        var utf16WithoutMark = _temp.PathOf("export.csv");
        File.WriteAllBytes(utf16WithoutMark, Encoding.Unicode.GetBytes("Hello"));
        var controls = _temp.PathOf("log.txt");
        File.WriteAllText(controls, "tab\there\r\nbell\u0007 end", new UTF8Encoding(false));

        (await Read(utf16WithoutMark)).ShouldBe(new FileContent.Text("Hello"));
        (await Read(controls)).ShouldBe(new FileContent.Text("tab\there\r\nbell end"));
    }

    public void Dispose() => _temp.Dispose();
}
