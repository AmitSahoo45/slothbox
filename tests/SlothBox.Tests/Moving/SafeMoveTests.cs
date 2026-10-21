using SlothBox.Core;
using SlothBox.Core.Moving;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Moving;

[Collection(PostgresCollection.Name)]
public sealed class SafeMoveTests(PostgresFixture postgres)
{
    private const string ChangedMeanwhile = "This file changed while SlothBox was working on it. Try again.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Cancels the caller's token right after the pending row is written, like a browser tab closed mid-move.
    private sealed class CancelAfterAppend(IActionLog log, CancellationTokenSource cancel) : IActionLog
    {
        public async Task<long?> AppendPendingAsync(MoveRequest request, CancellationToken ct)
        {
            var id = await log.AppendPendingAsync(request, ct);
            await cancel.CancelAsync();
            return id;
        }

        public Task CompleteAsync(long id, ActionStatus status, string? error, CancellationToken ct) =>
            log.CompleteAsync(id, status, error, ct);
        public Task<IReadOnlyList<FileAction>> GetPendingAsync(CancellationToken ct) => log.GetPendingAsync(ct);
        public Task<IReadOnlyList<FileAction>> GetForFileAsync(Guid fileId, CancellationToken ct) =>
            log.GetForFileAsync(fileId, ct);
        public Task<IReadOnlyList<FileAction>> GetDoneSinceAsync(DateTimeOffset since, CancellationToken ct) =>
            log.GetDoneSinceAsync(since, ct);
    }

    private static readonly FileSnapshot InReview = new(FileStatus.Review, FileKind.Receipt, 5, "Jio recharge",
        ReviewReason.MissingDate, new ReceiptData("Jio", null, 599m, "INR", PaymentMethod.Upi));

    private static readonly FileSnapshot FiledAsBill = new(FileStatus.Filed, FileKind.Receipt, 3, "Jio recharge",
        null, new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi));

    [Fact]
    public async Task A_move_on_one_drive_lands_the_file_and_logs_a_done_action()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt bytes");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Receipts", "Food", "2026-10-20_Cafe_120INR.jpg");

        var result = await h.Mover().MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        (result.Succeeded, result.Destination, result.Error).ShouldBe((true, (string?)target, (string?)null));
        File.Exists(source).ShouldBeFalse();
        File.ReadAllText(target).ShouldBe("receipt bytes");
        var action = (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem();
        action.Id.ShouldBe(result.ActionId!.Value);
        (action.Kind, action.FromPath, action.ToPath, action.Sha256, action.Status, action.Before)
            .ShouldBe((ActionKind.Move, source, target, file.Sha256, ActionStatus.Done, (FileSnapshot?)null));
        action.CompletedAt.ShouldNotBeNull();
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(target);
    }

    [Fact]
    public async Task A_move_across_drives_copies_checks_the_hash_and_removes_the_original()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt bytes");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Receipts", "Food", "2026-10-20_Cafe_120INR.jpg");

        var result = await h.Mover(sameVolume: false)
            .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeTrue();
        File.Exists(source).ShouldBeFalse();
        File.ReadAllText(target).ShouldBe("receipt bytes");
        File.Exists(target + ".slothbox-tmp").ShouldBeFalse();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(target);
    }

    [Fact]
    public async Task A_copy_only_folder_keeps_the_original()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("PXL_0001.jpg", "camera roll photo");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Screenshots", "Other", "2026-10-20_camera-photo.jpg");

        var result = await h.Mover().MoveAsync(new MoveRequest(file.Id, ActionKind.Copy, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeTrue();
        File.ReadAllText(source).ShouldBe("camera roll photo");
        File.ReadAllText(target).ShouldBe("camera roll photo");
        var action = (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem();
        (action.Kind, action.Status).ShouldBe((ActionKind.Copy, ActionStatus.Done));
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(target);
    }

    [Fact]
    public async Task A_copy_that_no_longer_matches_the_recorded_hash_is_discarded_and_the_original_kept()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("bill.pdf", "edited after hashing");
        var file = await h.AddFileAsync(source, sha256: MoveHarness.Sha256Of("as first hashed"));
        var target = h.Output("Documents", "Finance", "electricity-bill.pdf");

        var result = await h.Mover(sameVolume: false)
            .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(source).ShouldBe("edited after hashing");
        File.Exists(target).ShouldBeFalse();
        File.Exists(target + ".slothbox-tmp").ShouldBeFalse();
        var action = (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem();
        action.Status.ShouldBe(ActionStatus.Failed);
        action.Error.ShouldBe(result.Error);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(source);
    }

    [Fact]
    public async Task An_existing_file_at_the_destination_is_never_overwritten()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("bill.pdf", "new bill");
        var file = await h.AddFileAsync(source);
        var target = h.WriteOutput("someone else's file", "Documents", "Finance", "bill.pdf");

        var result = await h.Mover().MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        File.ReadAllText(source).ShouldBe("new bill");
        File.ReadAllText(target).ShouldBe("someone else's file");
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
    }

    [Fact]
    public async Task When_the_action_cannot_be_logged_nothing_moves()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("bill.pdf", "bill");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Documents", "Finance", "bill.pdf");
        await h.Db.DisposeAsync();

        var thrown = await Record.ExceptionAsync(() =>
            h.Mover().MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct));

        thrown.ShouldBeAssignableTo<Npgsql.NpgsqlException>();
        File.ReadAllText(source).ShouldBe("bill");
        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cross_drive_move_whose_original_cannot_be_removed_is_rolled_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another program has open.");
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("open-in-viewer.jpg", "photo");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Screenshots", "Other", "open-in-viewer.jpg");

        MoveResult result;
        await using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await h.Mover(sameVolume: false)
                .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        File.ReadAllText(source).ShouldBe("photo");
        File.Exists(target).ShouldBeFalse();
        File.Exists(target + ".slothbox-tmp").ShouldBeFalse();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
    }

    [Fact]
    public async Task An_original_edited_while_it_was_being_copied_across_drives_is_kept_and_the_copy_rolled_back()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("notes.txt", "first draft");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Documents", "Work", "notes.txt");
        // The editor saves again the moment the copy is complete, before SlothBox removes the original.
        var mover = h.Mover(sameVolume: false, copy: async (from, to, ct) =>
        {
            await SafeMover.CopyNewAsync(from, to, ct);
            File.WriteAllText(from, "second draft");
        });

        var result = await mover.MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        File.ReadAllText(source).ShouldBe("second draft");
        File.Exists(target).ShouldBeFalse();
        Directory.EnumerateFiles(Path.GetDirectoryName(target)!).ShouldBeEmpty();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(source);
    }

    [Fact]
    public async Task A_refile_logs_the_before_state_and_applies_the_new_state()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var reviewItem = h.WriteOutput("receipt photo", "Needs review", "IMG_2041.jpg");
        var file = await h.AddFileAsync(reviewItem, InReview);
        var target = h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg");

        var result = await h.Mover().MoveAsync(
            new MoveRequest(file.Id, ActionKind.Move, reviewItem, target, file.Sha256, InReview, FiledAsBill), Ct);

        result.Succeeded.ShouldBeTrue();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Before.ShouldBe(InReview);
        (await h.SnapshotAsync(file.Id)).ShouldBe(FiledAsBill);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(target);
    }

    [Fact]
    public async Task A_refile_that_fails_puts_the_before_state_back()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var reviewItem = h.WriteOutput("receipt photo", "Needs review", "IMG_2041.jpg");
        var file = await h.AddFileAsync(reviewItem, InReview);
        var target = h.WriteOutput("an older Jio receipt", "Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg");

        var result = await h.Mover().MoveAsync(
            new MoveRequest(file.Id, ActionKind.Move, reviewItem, target, file.Sha256, InReview, FiledAsBill), Ct);

        result.Succeeded.ShouldBeFalse();
        (await h.SnapshotAsync(file.Id)).ShouldBe(InReview);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(reviewItem);
        File.ReadAllText(reviewItem).ShouldBe("receipt photo");
    }

    [Fact]
    public async Task Once_the_action_is_logged_a_cancelled_request_still_finishes_the_move()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("video.mp4", "a large video");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Other", "Media", "video.mp4");
        using var closedTab = new CancellationTokenSource();
        var mover = new SafeMover(new CancelAfterAppend(h.Log, closedTab), h.Files, h.Jobs, (_, _) => false);

        var result = await mover.MoveAsync(
            new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), closedTab.Token);

        result.Succeeded.ShouldBeTrue();
        File.Exists(source).ShouldBeFalse();
        File.ReadAllText(target).ShouldBe("a large video");
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);
    }

    [Fact]
    public async Task A_second_change_to_a_file_is_refused_while_one_is_in_progress()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt photo");
        var file = await h.AddFileAsync(source);
        var inProgress = new MoveRequest(file.Id, ActionKind.Move, source, h.Output("Receipts", "Food", "a.jpg"),
            file.Sha256);
        (await h.Log.AppendPendingAsync(inProgress, Ct)).ShouldNotBeNull();

        var result = await h.Mover().MoveAsync(
            inProgress with { To = h.Output("Receipts", "Food", "b.jpg") }, Ct);

        result.ShouldBe(new MoveResult(false, null, null, ChangedMeanwhile, MoveFailure.ChangedMeanwhile));
        File.ReadAllText(source).ShouldBe("receipt photo");
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    [InlineData(28)]
    public async Task A_full_output_drive_fails_the_move_with_the_drive_full_code_and_keeps_the_original(int hresult)
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("video.mp4", "a large video");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Other", "Media", "video.mp4");
        // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.
        var mover = h.Mover(sameVolume: false,
            copy: (_, _, _) => throw new IOException("There is not enough space on the disk.", hresult));

        var result = await mover.MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        (result.Succeeded, result.Failure).ShouldBe((false, (MoveFailure?)MoveFailure.DriveFull));
        result.Error.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(source).ShouldBe("a large video");
        Directory.EnumerateFiles(Path.GetDirectoryName(target)!).ShouldBeEmpty();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
    }

    [Fact]
    public async Task Any_other_io_error_carries_the_io_error_code()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("video.mp4", "a large video");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Other", "Media", "video.mp4");
        var mover = h.Mover(sameVolume: false,
            copy: (_, _, _) => throw new IOException("The specified network name is no longer available."));

        var result = await mover.MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        (result.Succeeded, result.Failure).ShouldBe((false, (MoveFailure?)MoveFailure.IoError));
        File.ReadAllText(source).ShouldBe("a large video");
    }

    [Fact]
    public async Task A_read_only_original_moves_across_drives_like_it_does_on_one_drive()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("statement.pdf", "bank statement");
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
        var file = await h.AddFileAsync(source);
        var target = h.Output("Documents", "Finance", "bank-statement.pdf");

        var result = await h.Mover(sameVolume: false)
            .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeTrue();
        File.Exists(source).ShouldBeFalse();
        File.ReadAllText(target).ShouldBe("bank statement");
        File.Exists(target + ".slothbox-tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task A_temp_copy_left_by_an_earlier_move_is_left_alone_and_the_move_still_succeeds()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt photo");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Receipts", "Food", "2026-10-20_Cafe_120INR.jpg");
        var earlier = h.WriteOutput("an earlier move's only copy", "Receipts", "Food",
            "2026-10-20_Cafe_120INR.jpg.slothbox-tmp");

        var result = await h.Mover(sameVolume: false)
            .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeTrue();
        File.ReadAllText(earlier).ShouldBe("an earlier move's only copy");
        File.ReadAllText(target).ShouldBe("receipt photo");
        Directory.EnumerateFiles(Path.GetDirectoryName(target)!).Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_temp_name_already_taken_fails_the_move_without_touching_that_file()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt photo");
        var file = await h.AddFileAsync(source);
        var target = h.Output("Receipts", "Food", "2026-10-20_Cafe_120INR.jpg");
        // A fresh database numbers its first action 1, so this is the temp name the move will want.
        var taken = h.WriteOutput("not SlothBox's bytes", "Receipts", "Food",
            Path.GetFileName(TempFiles.For(target, 1)));

        var result = await h.Mover(sameVolume: false)
            .MoveAsync(new MoveRequest(file.Id, ActionKind.Move, source, target, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        File.ReadAllText(taken).ShouldBe("not SlothBox's bytes");
        File.ReadAllText(source).ShouldBe("receipt photo");
        File.Exists(target).ShouldBeFalse();
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
    }

    [Fact]
    public async Task Deleting_a_copy_removes_it_and_points_the_file_back_at_the_original()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var original = h.Watched("PXL_0001.jpg", "camera roll photo");
        var copy = h.WriteOutput("camera roll photo", "Screenshots", "Other", "2026-10-20_camera-photo.jpg");
        var file = await h.AddFileAsync(copy);

        var result = await h.Mover()
            .MoveAsync(new MoveRequest(file.Id, ActionKind.DeleteCopy, copy, original, file.Sha256), Ct);

        result.Succeeded.ShouldBeTrue();
        File.Exists(copy).ShouldBeFalse();
        File.ReadAllText(original).ShouldBe("camera roll photo");
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Kind.ShouldBe(ActionKind.DeleteCopy);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(original);
    }

    [Fact]
    public async Task A_copy_deletion_whose_copy_and_original_are_the_same_path_is_refused_without_a_log_row()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var original = h.Watched("PXL_0001.jpg", "camera roll photo");
        var file = await h.AddFileAsync(original);

        var result = await h.Mover()
            .MoveAsync(new MoveRequest(file.Id, ActionKind.DeleteCopy, original, original, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(original).ShouldBe("camera roll photo");
        (await h.ActionsAsync(file.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_edited_since_filing_is_not_deleted()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var original = h.Watched("PXL_0001.jpg", "camera roll photo");
        var copy = h.WriteOutput("cropped by the user", "Screenshots", "Other", "2026-10-20_camera-photo.jpg");
        var file = await h.AddFileAsync(copy, sha256: MoveHarness.Sha256Of("camera roll photo"));

        var result = await h.Mover()
            .MoveAsync(new MoveRequest(file.Id, ActionKind.DeleteCopy, copy, original, file.Sha256), Ct);

        result.Succeeded.ShouldBeFalse();
        File.ReadAllText(copy).ShouldBe("cropped by the user");
        (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(copy);
    }
}
