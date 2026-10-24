using SkiaSharp;
using SlothBox.Core;
using SlothBox.Core.Gate;
using SlothBox.Core.Pipeline;
using SlothBox.Tests.Data;
using SlothBox.Tests.Reading;

namespace SlothBox.Tests.Pipeline;

// Spec 5.3 end to end: each row of the table through the real gate, reader, names, mover and database.
[Collection(PostgresCollection.Name)]
public sealed class PipelineRoutingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Analysis JioReceipt = new(FileKind.Receipt, "Bills and recharges", "Jio recharge",
        "UPI payment of 599 INR to Jio", "", new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR",
            PaymentMethod.Upi));

    private static async Task<PipelineResult.Filed> FiledAsync(PipelineHarness h, string path) =>
        (await h.Pipeline().RunAsync(PipelineHarness.JobFor(path, folderId: await h.AddFolderAsync()), Ct))
        .ShouldBeOfType<PipelineResult.Filed>();

    [Fact]
    public async Task A_screenshot_is_named_by_the_local_date_its_file_was_created()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var shot = TestFiles.Image(h.WatchedPath("Screenshot (14).png"), 64, 48, SKEncodedImageFormat.Png);
        // Just after local midnight, so a UTC date would be the day before east of Greenwich.
        var created = new DateTime(2026, 10, 18, 0, 30, 0, DateTimeKind.Local);
        File.SetCreationTime(shot, created);
        Assert.SkipUnless(File.GetCreationTime(shot) == created, "This file system cannot set a creation time.");
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Screenshot, "reddit comments",
            "Reddit thread on dotnet 10", "A Reddit thread about .NET 10.", "r/dotnet .NET 10 is out", null));

        var filed = await FiledAsync(h, shot);

        var target = h.Output("Screenshots", "Reddit comments", "2026-10-18_reddit-thread-on-dotnet-10.png");
        (filed.File.CurrentPath, filed.Category, filed.Embedded).ShouldBe((target, "Reddit comments", true));
        File.Exists(target).ShouldBeTrue();
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.Status, file.FileDate)
            .ShouldBe((FileKind.Screenshot, 6, FileStatus.Filed, new DateTimeOffset(created)));
        // The text Gemma read off the screen is what search finds.
        file.ExtractedText.ShouldBe("r/dotnet .NET 10 is out");
        h.Embedder.Documents.ShouldHaveSingleItem().ShouldBe(("Reddit thread on dotnet 10",
            "Reddit comments. A Reddit thread about .NET 10. r/dotnet .NET 10 is out"));
    }

    [Theory]
    [InlineData("IMG_2041.jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData("IMG_2041.jpeg", SKEncodedImageFormat.Jpeg)]
    [InlineData("Scan.PNG", SKEncodedImageFormat.Png)]
    [InlineData("letter.webp", SKEncodedImageFormat.Webp)]
    public async Task An_image_goes_to_Gemma_as_a_jpeg_and_is_filed_as_the_type_Gemma_gives(string name,
        SKEncodedImageFormat format)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = TestFiles.Image(h.WatchedPath(name), 64, 48, format);
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Document, "personal",
            "letter from the bank", "A letter from the bank.", "Dear customer", null));

        var filed = await FiledAsync(h, photo);

        // An original keeps its own extension, in lower case (spec 5.5).
        filed.File.CurrentPath.ShouldBe(h.Output("Documents", "Personal",
            "letter-from-the-bank" + Path.GetExtension(name).ToLowerInvariant()));
        var image = h.Analyzer.Requests.ShouldHaveSingleItem().Content.ShouldBeOfType<FileContent.Image>();
        TestFiles.Inspect(image.JpegBytes).ShouldBe((SKEncodedImageFormat.Jpeg, 64, 48));
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.ExtractedText).ShouldBe((FileKind.Document, 14, "Dear customer"));
    }

    [Theory]
    [InlineData("notes.pdf")]
    [InlineData("notes.docx")]
    [InlineData("notes.txt")]
    [InlineData("notes.md")]
    [InlineData("notes.csv")]
    public async Task A_readable_document_goes_to_Gemma_as_text_and_is_named_by_its_title(string name)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        const string text = "Release notes for .NET 10: faster JSON, smaller containers and C# 14.";
        var path = h.WatchedPath(name);
        switch (Path.GetExtension(name))
        {
            case ".pdf":
                TestFiles.Pdf(path, text);
                break;
            case ".docx":
                TestFiles.Docx(path, text);
                break;
            default:
                File.WriteAllText(path, text);
                break;
        }
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Document, "learning",
            "dotnet 10 release notes", "Notes on the .NET 10 release.", "", null));

        var filed = await FiledAsync(h, path);

        filed.File.CurrentPath.ShouldBe(h.Output("Documents", "Learning",
            "dotnet-10-release-notes" + Path.GetExtension(name)));
        var read = h.Analyzer.Requests.ShouldHaveSingleItem().Content.ShouldBeOfType<FileContent.Text>().Value;
        read.ShouldContain(text);
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.ExtractedText).ShouldBe((FileKind.Document, 13, read));
    }

    [Fact]
    public async Task A_pdf_Gemma_reads_as_a_receipt_is_filed_and_logged_as_one()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var bill = TestFiles.Pdf(h.WatchedPath("bill.pdf"),
            "Jio recharge of 599 INR paid by UPI on 20 October 2026. Thank you for choosing Jio.");
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);

        var filed = await FiledAsync(h, bill);

        filed.File.CurrentPath.ShouldBe(h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.pdf"));
        (await h.Moves.Receipts.GetAsync(filed.File.Id, Ct)).ShouldBe(JioReceipt.Receipt);
        (await h.Moves.FileAsync(filed.File.Id)).ExtractedText.ShouldContain("Jio recharge of 599 INR");
    }

    [Theory]
    [InlineData("Q3 plan.xlsx")]
    [InlineData("Q3 plan.pptx")]
    [InlineData("Q3 plan.doc")]
    [InlineData("Q3 plan.xls")]
    [InlineData("Q3 PLAN.PPT")]
    public async Task An_office_file_goes_to_Gemma_by_name_and_is_filed_as_a_document(string name)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var path = h.Watched(name, "PK not really an Office file");
        // The name alone did not tell Gemma what this is; the extension still makes it a document (A8).
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Unknown, "work", "q3 plan",
            "The plan for the third quarter.", "", null));

        var filed = await FiledAsync(h, path);

        var target = h.Output("Documents", "Work", "q3-plan" + Path.GetExtension(name).ToLowerInvariant());
        (filed.File.CurrentPath, filed.File.Status, filed.Category, filed.Embedded)
            .ShouldBe((target, FileStatus.Filed, "Work", true));
        File.Exists(target).ShouldBeTrue();
        var request = h.Analyzer.Requests.ShouldHaveSingleItem();
        (request.OriginalName, request.Content).ShouldBe((name, new FileContent.NameOnly()));
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.ExtractedText).ShouldBe((FileKind.Document, 11, ""));
    }

    [Fact]
    public async Task Receipt_fields_Gemma_gives_an_office_file_are_dropped()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var invoice = h.Watched("invoice.xlsx", "PK not really an Office file");
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt with { Title = "jio invoice" });

        var filed = await FiledAsync(h, invoice);

        // A receipt category is not a document category, so it files under the documents' Other.
        filed.File.CurrentPath.ShouldBe(h.Output("Documents", "Other", "jio-invoice.xlsx"));
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId).ShouldBe((FileKind.Document, 15));
        (await h.Moves.Receipts.GetAsync(file.Id, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("ChromeSetup.exe", "Installers", 16)]
    [InlineData("Tool.msi", "Installers", 16)]
    [InlineData("App.msix", "Installers", 16)]
    [InlineData("Game.appx", "Installers", 16)]
    [InlineData("App.appinstaller", "Installers", 16)]
    [InlineData("SETUP.EXE", "Installers", 16)]
    [InlineData("photos.zip", "Archives", 17)]
    [InlineData("backup.7z", "Archives", 17)]
    [InlineData("old.rar", "Archives", 17)]
    [InlineData("src.tar", "Archives", 17)]
    [InlineData("logs.tar.gz", "Archives", 17)]
    [InlineData("ubuntu.iso", "Archives", 17)]
    [InlineData("clip.mp4", "Media", 18)]
    [InlineData("film.mkv", "Media", 18)]
    [InlineData("video.mov", "Media", 18)]
    [InlineData("song.mp3", "Media", 18)]
    [InlineData("voice.wav", "Media", 18)]
    [InlineData("memo.m4a", "Media", 18)]
    [InlineData("funny.gif", "Media", 18)]
    [InlineData("data.json", "Misc", 19)]
    [InlineData("index.html", "Misc", 19)]
    [InlineData("LICENSE", "Misc", 19)]
    public async Task Installers_archives_media_and_the_rest_go_under_Other_by_extension_without_Gemma(string name,
        string folder, int categoryId)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var path = h.Watched(name, $"the bytes of {name}");

        var filed = await FiledAsync(h, path);

        // The original name stays as it is, case and all (spec 5.5).
        var target = h.Output("Other", folder, name);
        (filed.File.CurrentPath, filed.File.Status, filed.Category, filed.Embedded)
            .ShouldBe((target, FileStatus.Filed, folder, true));
        File.Exists(target).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        var title = Path.GetFileNameWithoutExtension(name);
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.Title, file.ExtractedText)
            .ShouldBe((FileKind.Other, (int?)categoryId, title, ""));
        h.Analyzer.Requests.ShouldBeEmpty();
        // Gemma never reads it, so search finds it by its name and category (spec 5.1 step 6).
        h.Embedder.Documents.ShouldHaveSingleItem().ShouldBe((title, $"{folder}."));
    }

    [Theory]
    [InlineData("IMG_0002.HEIF", ReviewReason.Heic)]
    [InlineData("salary.docx", ReviewReason.PasswordProtected)]
    public async Task A_heif_photo_and_a_protected_word_file_wait_in_Needs_review_without_Gemma(string name,
        ReviewReason reason)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var path = h.WatchedPath(name);
        if (reason == ReviewReason.Heic)
            File.WriteAllBytes(path, [1, 2, 3, 4]);
        else
            TestFiles.EncryptedDocx(path);

        var filed = await FiledAsync(h, path);

        (filed.File.CurrentPath, filed.File.Status, filed.File.ReviewReason, filed.Embedded)
            .ShouldBe((h.Output("Needs review", name), FileStatus.Review, (ReviewReason?)reason, false));
        h.Analyzer.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("statement.pdf.part", GateSkipReason.Temporary)]
    [InlineData("video.mp4.partial", GateSkipReason.Temporary)]
    [InlineData("setup.exe.tmp", GateSkipReason.Temporary)]
    [InlineData("photo.jpg.download", GateSkipReason.Temporary)]
    [InlineData("IMG_2041.jpg.7.slothbox-tmp", GateSkipReason.Temporary)]
    [InlineData("~$Q3 plan.xlsx", GateSkipReason.Temporary)]
    [InlineData("desktop.ini", GateSkipReason.Temporary)]
    [InlineData("Thumbs.db", GateSkipReason.Temporary)]
    [InlineData(".notes.txt", GateSkipReason.Hidden)]
    public async Task Files_the_table_always_skips_stay_where_they_are(string name, string reason)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var path = h.Watched(name, "left alone");
        // A dot name is hidden on Linux; Windows needs the attribute.
        if (reason == GateSkipReason.Hidden)
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(path, folderId: await h.AddFolderAsync()), Ct);

        result.ShouldBe(new PipelineResult.Skipped(reason));
        File.Exists(path).ShouldBeTrue();
        h.Analyzer.Requests.ShouldBeEmpty();
        (await h.CountFilesAsync()).ShouldBe(0);
    }
}
