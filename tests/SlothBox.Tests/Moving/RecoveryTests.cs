using SlothBox.Core;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Moving;

// Each test stops a move right after its pending row was written, arranges how far the files got, then recovers.
[Collection(PostgresCollection.Name)]
public sealed class RecoveryTests(PostgresFixture postgres)
{
    private const string Content = "receipt bytes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Crash(FileRecord File, string Source, string Target, int FolderId, long ActionId)
    {
        public string Temp => TempFiles.For(Target, ActionId);
    }

    private static async Task<Crash> CrashedAsync(MoveHarness h, ActionKind kind = ActionKind.Move,
        FileSnapshot? before = null, FileSnapshot? after = null)
    {
        var folderId = await h.AddFolderAsync();
        var source = h.Watched("IMG_2041.jpg", Content);
        var file = await h.AddFileAsync(source, folderId: folderId);
        var target = h.Output("Receipts", "Food", "2026-10-20_Cafe_120INR.jpg");
        var actionId = (await h.Log.AppendPendingAsync(
            new MoveRequest(file.Id, kind, source, target, file.Sha256, before, after), Ct)).ShouldNotBeNull();
        return new Crash(file, source, target, folderId, actionId);
    }

    private static void Finish(MoveHarness h, Crash crash) =>
        h.WriteOutput(Content, "Receipts", "Food", Path.GetFileName(crash.Target));

    [Fact]
    public async Task A_verified_destination_with_the_original_still_present_deletes_the_original_and_completes()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        Finish(h, crash);

        var recovered = (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        (recovered.Status, recovered.Requeued).ShouldBe((ActionStatus.Done, false));
        File.Exists(crash.Source).ShouldBeFalse();
        File.ReadAllText(crash.Target).ShouldBe(Content);
        (await h.ActionsAsync(crash.File.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);
        (await h.FileAsync(crash.File.Id)).CurrentPath.ShouldBe(crash.Target);
    }

    [Fact]
    public async Task A_verified_copy_keeps_the_original_of_a_copy_only_folder()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h, ActionKind.Copy);
        Finish(h, crash);

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);

        File.ReadAllText(crash.Source).ShouldBe(Content);
        (await h.FileAsync(crash.File.Id)).CurrentPath.ShouldBe(crash.Target);
    }

    [Fact]
    public async Task An_original_edited_since_it_was_hashed_is_kept_even_when_the_destination_is_verified()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        Finish(h, crash);
        File.WriteAllText(crash.Source, "edited after the crash");

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);

        File.ReadAllText(crash.Source).ShouldBe("edited after the crash");
        File.ReadAllText(crash.Target).ShouldBe(Content);
    }

    [Fact]
    public async Task A_verified_destination_with_the_original_gone_completes()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        Finish(h, crash);
        File.Delete(crash.Source);

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);

        File.ReadAllText(crash.Target).ShouldBe(Content);
        (await h.FileAsync(crash.File.Id)).CurrentPath.ShouldBe(crash.Target);
    }

    [Fact]
    public async Task A_partial_temp_file_is_removed_the_action_fails_and_the_job_is_queued_again()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var temp = h.WriteOutput("receipt by", "Receipts", "Food", Path.GetFileName(crash.Temp));

        var recovered = (await h.Mover(sameVolume: false).RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        (recovered.Status, recovered.Requeued).ShouldBe((ActionStatus.Failed, true));
        File.Exists(temp).ShouldBeFalse();
        File.ReadAllText(crash.Source).ShouldBe(Content);
        (await h.ActionsAsync(crash.File.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);
        (await h.FileAsync(crash.File.Id)).CurrentPath.ShouldBe(crash.Source);
        var job = (await h.Jobs.ClaimNextAsync(Ct)).ShouldNotBeNull();
        (job.File.Path, job.File.Origin, job.File.WatchedFolderId)
            .ShouldBe((crash.Source, FileOrigin.Folder, (int?)crash.FolderId));
    }

    [Fact]
    public async Task A_move_that_never_started_fails_and_is_queued_again()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Requeued.ShouldBeTrue();

        File.ReadAllText(crash.Source).ShouldBe(Content);
        (await h.Jobs.ClaimNextAsync(Ct)).ShouldNotBeNull().File.Path.ShouldBe(crash.Source);
    }

    [Fact]
    public async Task A_different_file_at_the_destination_is_left_alone()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        h.WriteOutput("a file the user saved here", "Receipts", "Food", Path.GetFileName(crash.Target));

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);

        File.ReadAllText(crash.Target).ShouldBe("a file the user saved here");
        File.ReadAllText(crash.Source).ShouldBe(Content);
    }

    [Fact]
    public async Task With_both_the_original_and_the_destination_missing_the_action_fails_with_an_error()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        File.Delete(crash.Source);

        var recovered = (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        (recovered.Status, recovered.Requeued).ShouldBe((ActionStatus.Failed, false));
        recovered.Error.ShouldNotBeNullOrWhiteSpace();
        (await h.ActionsAsync(crash.File.Id)).ShouldHaveSingleItem().Error.ShouldBe(recovered.Error);
        (await h.Jobs.ClaimNextAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_interrupted_refile_fails_back_to_its_before_state_without_a_new_job()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var before = new FileSnapshot(FileStatus.Filed, FileKind.Receipt, 1, "Cafe bill", null, null);
        var after = new FileSnapshot(FileStatus.Filed, FileKind.Receipt, 4, "Cafe bill", null, null);
        var crash = await CrashedAsync(h, before: before, after: after);

        var recovered = (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        (recovered.Status, recovered.Requeued).ShouldBe((ActionStatus.Failed, false));
        (await h.SnapshotAsync(crash.File.Id)).ShouldBe(before);
        (await h.Jobs.ClaimNextAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task With_the_original_gone_a_complete_temp_copy_finishes_the_move()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var temp = h.WriteOutput(Content, "Receipts", "Food", Path.GetFileName(crash.Temp));
        File.Delete(crash.Source);

        (await h.Mover(sameVolume: false).RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);

        File.Exists(temp).ShouldBeFalse();
        File.ReadAllText(crash.Target).ShouldBe(Content);
        (await h.FileAsync(crash.File.Id)).CurrentPath.ShouldBe(crash.Target);
    }

    [Fact]
    public async Task With_the_original_gone_a_partial_temp_copy_is_removed()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var temp = h.WriteOutput("receipt by", "Receipts", "Food", Path.GetFileName(crash.Temp));
        File.Delete(crash.Source);

        var recovered = (await h.Mover(sameVolume: false).RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        (recovered.Status, recovered.Requeued).ShouldBe((ActionStatus.Failed, false));
        File.Exists(temp).ShouldBeFalse();
    }

    [Fact]
    public async Task A_complete_temp_copy_blocked_by_another_file_is_kept_and_named_in_the_error()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var temp = h.WriteOutput(Content, "Receipts", "Food", Path.GetFileName(crash.Temp));
        h.WriteOutput("a file the user saved here", "Receipts", "Food", Path.GetFileName(crash.Target));
        File.Delete(crash.Source);

        var recovered = (await h.Mover(sameVolume: false).RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        recovered.Status.ShouldBe(ActionStatus.Failed);
        recovered.Error.ShouldNotBeNull().ShouldContain(temp);
        File.ReadAllText(temp).ShouldBe(Content);
        File.ReadAllText(crash.Target).ShouldBe("a file the user saved here");
    }

    [Fact]
    public async Task A_move_on_one_drive_leaves_alone_a_temp_file_it_could_not_have_written()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var foreign = h.WriteOutput("another file's only copy", "Receipts", "Food",
            Path.GetFileName(crash.Target) + ".slothbox-tmp");

        (await h.Mover(sameVolume: true).RecoverPendingAsync(Ct)).ShouldHaveSingleItem()
            .Status.ShouldBe(ActionStatus.Failed);

        File.ReadAllText(foreign).ShouldBe("another file's only copy");
    }

    [Fact]
    public async Task A_cross_drive_recovery_leaves_alone_the_temp_files_of_other_actions()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        var foreign = h.WriteOutput("another action's only copy", "Receipts", "Food",
            Path.GetFileName(TempFiles.For(crash.Target, crash.ActionId + 1)));
        var unnumbered = h.WriteOutput("a copy with no action number", "Receipts", "Food",
            Path.GetFileName(crash.Target) + ".slothbox-tmp");

        (await h.Mover(sameVolume: false).RecoverPendingAsync(Ct)).ShouldHaveSingleItem()
            .Status.ShouldBe(ActionStatus.Failed);

        File.ReadAllText(foreign).ShouldBe("another action's only copy");
        File.ReadAllText(unnumbered).ShouldBe("a copy with no action number");
    }

    [Fact]
    public async Task A_locked_original_leaves_the_action_pending_for_the_next_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux file locks are advisory and do not block reads.");
        await using var h = await MoveHarness.CreateAsync(postgres);
        var crash = await CrashedAsync(h);
        Finish(h, crash);

        RecoveredAction recovered;
        await using (new FileStream(crash.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            recovered = (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem();

        recovered.Status.ShouldBe(ActionStatus.Pending);
        (await h.ActionsAsync(crash.File.Id)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Pending);
        File.ReadAllText(crash.Source).ShouldBe(Content);
    }

    [Fact]
    public async Task An_interrupted_copy_deletion_keeps_the_copy_when_the_original_has_gone()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var original = h.Watched("PXL_0001.jpg", Content);
        var copy = h.WriteOutput(Content, "Screenshots", "Other", "2026-10-20_camera-photo.jpg");
        var filed = new FileSnapshot(FileStatus.Filed, FileKind.Screenshot, 10, "Camera photo", null, null);
        var file = await h.AddFileAsync(copy, filed);
        await h.Log.AppendPendingAsync(new MoveRequest(file.Id, ActionKind.DeleteCopy, copy, original, file.Sha256,
            filed, filed with { Status = FileStatus.Undone }), Ct);
        File.Delete(original);

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Failed);

        File.ReadAllText(copy).ShouldBe(Content);
        (await h.SnapshotAsync(file.Id)).ShouldBe(filed);
        (await h.Ignored.ContainsAsync(file.Sha256, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_interrupted_copy_deletion_finishes()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var original = h.Watched("PXL_0001.jpg", Content);
        var copy = h.WriteOutput(Content, "Screenshots", "Other", "2026-10-20_camera-photo.jpg");
        var file = await h.AddFileAsync(copy);
        await h.Log.AppendPendingAsync(new MoveRequest(file.Id, ActionKind.DeleteCopy, copy, original, file.Sha256), Ct);

        (await h.Mover().RecoverPendingAsync(Ct)).ShouldHaveSingleItem().Status.ShouldBe(ActionStatus.Done);

        File.Exists(copy).ShouldBeFalse();
        File.ReadAllText(original).ShouldBe(Content);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(original);
    }
}
