using SkiaSharp;
using SlothBox.Core;
using SlothBox.Core.Gate;
using SlothBox.Core.Pipeline;
using SlothBox.Tests.Data;
using SlothBox.Tests.Reading;

namespace SlothBox.Tests.Pipeline;

[Collection(PostgresCollection.Name)]
public sealed class FilePipelineTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Analysis JioReceipt = new(FileKind.Receipt, "Bills and recharges", "Jio recharge",
        "UPI payment of 599 INR to Jio", "Payment successful Jio 599.00",
        new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi));

    private static string Photo(string path) => TestFiles.Image(path, 64, 48, SKEncodedImageFormat.Jpeg);

    [Fact]
    public async Task A_receipt_photo_is_filed_renamed_recorded_and_indexed()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: folderId), Ct);

        var filed = result.ShouldBeOfType<PipelineResult.Filed>();
        var target = h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg");
        (filed.File.CurrentPath, filed.Category, filed.FolderLabel, filed.Embedded)
            .ShouldBe((target, "Bills and recharges", "Downloads", true));
        File.Exists(target).ShouldBeTrue();
        File.Exists(photo).ShouldBeFalse();
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Status, file.Type, file.CategoryId, file.Title, file.Caption, file.ExtractedText)
            .ShouldBe((FileStatus.Filed, FileKind.Receipt, 3, "Jio recharge", "UPI payment of 599 INR to Jio",
                "Payment successful Jio 599.00"));
        (file.OriginalName, file.OriginalPath, file.CurrentPath, file.WatchedFolderId, file.Origin)
            .ShouldBe(("IMG_2041.jpg", photo, target, folderId, FileOrigin.Folder));
        (await h.Moves.Receipts.GetAsync(file.Id, Ct)).ShouldBe(JioReceipt.Receipt);
        var action = (await h.Moves.ActionsAsync(file.Id)).ShouldHaveSingleItem();
        (action.Kind, action.Status, action.FromPath, action.ToPath)
            .ShouldBe((ActionKind.Move, ActionStatus.Done, photo, target));
        var request = h.Analyzer.Requests.ShouldHaveSingleItem();
        (request.OriginalName, request.FolderLabel, request.Categories.Count).ShouldBe(("IMG_2041.jpg", "Downloads", 19));
        request.Content.ShouldBeOfType<FileContent.Image>();
        h.Embedder.Documents.ShouldHaveSingleItem()
            .ShouldBe(("Jio recharge", "Bills and recharges. UPI payment of 599 INR to Jio Payment successful Jio 599.00"));
        (await h.EmbeddingOfAsync(file.Id)).ShouldBe(FakeEmbedder.Vector);
    }

    [Fact]
    public async Task A_phone_upload_is_filed_from_the_inbox_with_phone_upload_as_the_folder_hint()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = Photo(h.InboxPath("phone-20261020-104112-a1b2c3.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, FileOrigin.Phone), Ct);

        var filed = result.ShouldBeOfType<PipelineResult.Filed>();
        (filed.FolderLabel, filed.File.Origin, filed.File.WatchedFolderId).ShouldBe((null, FileOrigin.Phone, null));
        h.Analyzer.Requests.ShouldHaveSingleItem().FolderLabel.ShouldBe("phone upload");
        File.Exists(photo).ShouldBeFalse();
        File.Exists(h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg")).ShouldBeTrue();
    }

    public static IEnumerable<object[]> ReviewCases =>
    [
        ["garbled.jpg", ReviewReason.BadOutput],
        ["mystery.jpg", ReviewReason.UnknownType],
        ["no-date.jpg", ReviewReason.MissingDate],
        ["no-amount.jpg", ReviewReason.MissingAmount],
        ["photo.heic", ReviewReason.Heic],
        ["scan.pdf", ReviewReason.ScannedPdf],
        ["secret.pdf", ReviewReason.PasswordProtected]
    ];

    [Theory]
    [MemberData(nameof(ReviewCases))]
    public async Task Each_review_reason_sends_the_file_to_Needs_review_under_its_own_name(string name,
        ReviewReason reason)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var path = h.WatchedPath(name);
        switch (reason)
        {
            case ReviewReason.Heic:
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                break;
            case ReviewReason.ScannedPdf:
                TestFiles.Pdf(path, "scan");
                break;
            case ReviewReason.PasswordProtected:
                TestFiles.EncryptedPdf(path);
                break;
            default:
                Photo(path);
                break;
        }
        h.Analyzer.Answer = _ => reason switch
        {
            ReviewReason.BadOutput => new AnalysisResult.NeedsReview(ReviewReason.BadOutput),
            ReviewReason.UnknownType => new AnalysisResult.Success(JioReceipt with { Type = FileKind.Unknown }),
            ReviewReason.MissingDate => new AnalysisResult.Success(
                JioReceipt with { Receipt = JioReceipt.Receipt! with { Date = null } }),
            ReviewReason.MissingAmount => new AnalysisResult.Success(
                JioReceipt with { Receipt = JioReceipt.Receipt! with { Amount = null } }),
            _ => throw new InvalidOperationException("Gemma is not asked about a file that could not be read.")
        };

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(path, folderId: folderId), Ct);

        var filed = result.ShouldBeOfType<PipelineResult.Filed>();
        var target = h.Output("Needs review", name);
        (filed.File.CurrentPath, filed.File.Status, filed.File.ReviewReason, filed.Embedded)
            .ShouldBe((target, FileStatus.Review, reason, false));
        File.Exists(target).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        (await h.Moves.FileAsync(filed.File.Id)).ReviewReason.ShouldBe(reason);
        (await h.EmbeddingOfAsync(filed.File.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_receipt_missing_its_date_keeps_the_fields_Gemma_did_read_for_the_review_screen()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var photo = Photo(h.WatchedPath("no-date.jpg"));
        var partial = JioReceipt with { Receipt = JioReceipt.Receipt! with { Date = null } };
        h.Analyzer.Answer = _ => new AnalysisResult.Success(partial);

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.Title, file.CategoryId).ShouldBe((FileKind.Receipt, "Jio recharge", (int?)null));
        (await h.Moves.Receipts.GetAsync(file.Id, Ct)).ShouldBe(partial.Receipt);
    }

    [Fact]
    public async Task The_same_content_arriving_again_goes_to_Duplicates_pointing_at_the_filed_file()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var first = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        var original = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(first, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();
        var again = h.WatchedPath("IMG_2041 (1).jpg");
        File.Copy(original.File.CurrentPath, again);

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(again, folderId: folderId), Ct);

        var duplicate = result.ShouldBeOfType<PipelineResult.Filed>();
        var target = h.Output("Duplicates", "IMG_2041 (1).jpg");
        (duplicate.File.Status, duplicate.File.DuplicateOf, duplicate.File.CurrentPath, duplicate.Category)
            .ShouldBe((FileStatus.Duplicate, original.File.Id, target, "Bills and recharges"));
        File.Exists(target).ShouldBeTrue();
        File.Exists(again).ShouldBeFalse();
        h.Analyzer.Requests.Count.ShouldBe(1);
        (await h.EmbeddingOfAsync(duplicate.File.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_copy_only_folder_keeps_the_original_and_logs_a_copy()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync(FolderMode.Copy);
        var photo = Photo(h.WatchedPath("PXL_0001.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        File.Exists(photo).ShouldBeTrue();
        File.Exists(filed.File.CurrentPath).ShouldBeTrue();
        (await h.Moves.ActionsAsync(filed.File.Id)).ShouldHaveSingleItem().Kind.ShouldBe(ActionKind.Copy);
    }

    [Theory]
    [InlineData("statement.pdf.crdownload", GateSkipReason.Temporary)]
    [InlineData("vanished.jpg", GateSkipReason.Missing)]
    public async Task A_file_the_gate_skips_is_reported_as_skipped(string name, string reason)
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var path = reason == GateSkipReason.Missing ? h.WatchedPath(name) : h.Watched(name, "half a download");

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(path, folderId: await h.AddFolderAsync()), Ct);

        result.ShouldBe(new PipelineResult.Skipped(reason));
        h.Analyzer.Requests.ShouldBeEmpty();
        (await h.CountFilesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_job_from_a_folder_that_was_removed_is_skipped_and_its_file_left_alone()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync(FolderMode.Copy);
        var photo = Photo(h.WatchedPath("PXL_0001.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        await h.Folders.RemoveAsync(folderId, Ct);

        // The queue's foreign key clears the folder id when the folder row goes.
        foreach (var job in new[] { PipelineHarness.JobFor(photo, folderId: folderId), PipelineHarness.JobFor(photo) })
            (await h.Pipeline().RunAsync(job, Ct)).ShouldBe(new PipelineResult.Skipped(GateSkipReason.FolderRemoved));

        File.Exists(photo).ShouldBeTrue();
        (await h.CountFilesAsync()).ShouldBe(0);
        h.Analyzer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_from_a_folder_that_was_switched_off_is_skipped()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync(FolderMode.Copy);
        var folder = (await h.Folders.ListAsync(Ct)).Single();
        await h.Folders.SaveAsync(folder with { Enabled = false }, Ct);
        var photo = Photo(h.WatchedPath("PXL_0001.jpg"));

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: folderId), Ct);

        result.ShouldBe(new PipelineResult.Skipped(GateSkipReason.FolderDisabled));
        File.Exists(photo).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_another_program_holds_open_is_not_ready()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux file locks are advisory and do not block reads.");
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));

        PipelineResult result;
        await using (new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: await h.AddFolderAsync()), Ct);

        result.ShouldBe(new PipelineResult.NotReady());
        h.Analyzer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_installer_is_filed_under_Other_Installers_by_name_without_asking_Gemma()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var setup = h.Watched("ChromeSetup.exe", "MZ not really an installer");

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(setup, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        var target = h.Output("Other", "Installers", "ChromeSetup.exe");
        (filed.File.CurrentPath, filed.Category, filed.Embedded).ShouldBe((target, "Installers", true));
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.Title, file.Status).ShouldBe((FileKind.Other, 16, "ChromeSetup", FileStatus.Filed));
        File.Exists(target).ShouldBeTrue();
        h.Analyzer.Requests.ShouldBeEmpty();
        h.Embedder.Documents.ShouldHaveSingleItem().ShouldBe(("ChromeSetup", "Installers."));
    }

    [Fact]
    public async Task A_text_document_is_filed_under_its_category_with_its_text_stored_and_cut_for_Gemma()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var text = new string('a', 7000) + " end";
        var notes = h.Watched("release notes.txt", text);
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Document, "learning",
            "dotnet 10 release notes", "Notes on the release.", "", null));

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(notes, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        filed.File.CurrentPath.ShouldBe(h.Output("Documents", "Learning", "dotnet-10-release-notes.txt"));
        var file = await h.Moves.FileAsync(filed.File.Id);
        (file.Type, file.CategoryId, file.ExtractedText).ShouldBe((FileKind.Document, 13, text));
        h.Analyzer.Requests.ShouldHaveSingleItem().Content.ShouldBeOfType<FileContent.Text>().Value.Length.ShouldBe(6000);
        h.Embedder.Documents.ShouldHaveSingleItem().Body
            .ShouldBe("Learning. Notes on the release. " + new string('a', 2000));
    }

    [Fact]
    public async Task A_category_not_in_the_list_files_under_that_types_Other()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var shot = Photo(h.WatchedPath("Screenshot 2026-10-20 104112.png"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(new Analysis(FileKind.Screenshot, "Memes",
            "cat on a keyboard", "A cat sits on a keyboard.", "", null));

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(shot, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        (filed.Category, (await h.Moves.FileAsync(filed.File.Id)).CategoryId).ShouldBe(("Other", 10));
        Path.GetDirectoryName(filed.File.CurrentPath).ShouldBe(h.Output("Screenshots", "Other"));
    }

    [Fact]
    public async Task A_second_file_wanting_a_taken_name_gets_the_next_free_one()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        var first = Photo(h.WatchedPath("IMG_2041.jpg"));
        (await h.Pipeline().RunAsync(PipelineHarness.JobFor(first, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();
        var second = TestFiles.Image(h.WatchedPath("IMG_2042.jpg"), 32, 32, SKEncodedImageFormat.Jpeg);

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(second, folderId: folderId), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        filed.File.CurrentPath.ShouldBe(h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR-2.jpg"));
    }

    [Fact]
    public async Task A_retry_after_a_failed_filing_reuses_the_files_row()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        var job = PipelineHarness.JobFor(photo, folderId: folderId);
        var broken = h.Moves.Mover(sameVolume: false,
            copy: (_, _, _) => throw new IOException("The specified network name is no longer available."));

        var failed = (await h.Pipeline(broken).RunAsync(job, Ct)).ShouldBeOfType<PipelineResult.Failed>();

        failed.Failure.ShouldBe(MoveFailure.IoError);
        File.Exists(photo).ShouldBeTrue();
        (await h.Moves.FileAsync(failed.FileId)).CurrentPath.ShouldBe(photo);

        var filed = (await h.Pipeline().RunAsync(job, Ct)).ShouldBeOfType<PipelineResult.Filed>();

        filed.File.Id.ShouldBe(failed.FileId);
        (await h.CountFilesAsync()).ShouldBe(1);
        (await h.Moves.ActionsAsync(filed.File.Id)).Select(a => a.Status)
            .ShouldBe([ActionStatus.Failed, ActionStatus.Done]);
    }

    [Fact]
    public async Task When_Gemma_cannot_be_reached_nothing_is_written()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => throw new HttpRequestException("No connection could be made to 127.0.0.1:11436.");

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: await h.AddFolderAsync()), Ct);

        result.ShouldBeOfType<PipelineResult.Offline>().Error.ShouldContain("11436");
        File.Exists(photo).ShouldBeTrue();
        (await h.CountFilesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_error_status_from_Gemma_sends_the_file_to_Needs_review_instead_of_holding_the_queue()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var folderId = await h.AddFolderAsync();
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => throw new HttpRequestException(
            "Response status code does not indicate success: 500 (Internal Server Error).", null,
            System.Net.HttpStatusCode.InternalServerError);

        var result = await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: folderId), Ct);

        var filed = result.ShouldBeOfType<PipelineResult.Filed>();
        (filed.File.Status, filed.File.ReviewReason, filed.File.CurrentPath)
            .ShouldBe((FileStatus.Review, (ReviewReason?)ReviewReason.BadOutput, h.Output("Needs review", "IMG_2041.jpg")));
    }

    [Fact]
    public async Task A_failed_embedding_leaves_the_file_filed_without_a_vector()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        h.Embedder.Failure = new TimeoutException("EmbeddingGemma did not answer within 30 s, twice.");

        var filed = (await h.Pipeline().RunAsync(PipelineHarness.JobFor(photo, folderId: await h.AddFolderAsync()), Ct))
            .ShouldBeOfType<PipelineResult.Filed>();

        (filed.Embedded, filed.File.Status).ShouldBe((false, FileStatus.Filed));
        File.Exists(filed.File.CurrentPath).ShouldBeTrue();
        (await h.EmbeddingOfAsync(filed.File.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_full_output_drive_fails_the_job_with_the_drive_full_code()
    {
        await using var h = await PipelineHarness.CreateAsync(postgres);
        var photo = Photo(h.WatchedPath("IMG_2041.jpg"));
        h.Analyzer.Answer = _ => new AnalysisResult.Success(JioReceipt);
        var full = h.Moves.Mover(sameVolume: false,
            copy: (_, _, _) => throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));

        var failed = (await h.Pipeline(full).RunAsync(PipelineHarness.JobFor(photo, folderId: await h.AddFolderAsync()), Ct))
            .ShouldBeOfType<PipelineResult.Failed>();

        failed.Failure.ShouldBe(MoveFailure.DriveFull);
        File.Exists(photo).ShouldBeTrue();
    }
}
