using SlothBox.Core;
using SlothBox.Core.Gate;
using SlothBox.Core.Undo;
using SlothBox.Tests.Data;
using SlothBox.Tests.Moving;

namespace SlothBox.Tests.Undo;

[Collection(PostgresCollection.Name)]
public sealed class UndoTests(PostgresFixture postgres)
{
    private const string FileChanged = "The file changed or moved after SlothBox filed it.";
    private const string NothingToUndo = "There is nothing to undo.";
    private const string OriginalMissing =
        "The original is missing or changed in its folder, so SlothBox kept its copy.";
    private const string FileOpen = "The file is open in another program. Close it and try again.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FileSnapshot InReview = new(FileStatus.Review, FileKind.Receipt, 5, "Jio recharge",
        ReviewReason.MissingDate, new ReceiptData("Jio", null, 599m, "INR", PaymentMethod.Upi));

    private static readonly FileSnapshot FiledAsBill = new(FileStatus.Filed, FileKind.Receipt, 3, "Jio recharge",
        null, new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi));

    private sealed record Filing(FileRecord File, string Source, string Target);

    // An action list read before another undo finished, as a second browser tab would hold it: the first read
    // answers from that stale list, later reads see the database.
    private sealed class StaleLog(IActionLog log, IReadOnlyList<FileAction> stale) : IActionLog
    {
        private IReadOnlyList<FileAction>? _stale = stale;

        public Task<IReadOnlyList<FileAction>> GetForFileAsync(Guid fileId, CancellationToken ct) =>
            Interlocked.Exchange(ref _stale, null) is { } list ? Task.FromResult(list) : log.GetForFileAsync(fileId, ct);
        public Task<IReadOnlyList<FileAction>> GetDoneSinceAsync(DateTimeOffset since, CancellationToken ct) =>
            Interlocked.Exchange(ref _stale, null) is { } list
                ? Task.FromResult(list)
                : log.GetDoneSinceAsync(since, ct);
        public Task<long?> AppendPendingAsync(MoveRequest request, CancellationToken ct) =>
            log.AppendPendingAsync(request, ct);
        public Task CompleteAsync(long id, ActionStatus status, string? error, CancellationToken ct) =>
            log.CompleteAsync(id, status, error, ct);
        public Task<IReadOnlyList<FileAction>> GetPendingAsync(CancellationToken ct) => log.GetPendingAsync(ct);
    }

    // A first filing: the file is recorded where it arrived, then moved (or copied) into the output folder.
    private static async Task<Filing> FileItAsync(MoveHarness h, string name, string content,
        ActionKind kind = ActionKind.Move, FileSnapshot? state = null, string[]? target = null)
    {
        var source = h.Watched(name, content);
        var file = await h.AddFileAsync(source, state);
        var to = h.Output(target ?? ["Receipts", "Food", Path.GetFileName(name)]);
        (await h.Mover().MoveAsync(new MoveRequest(file.Id, kind, source, to, file.Sha256), Ct))
            .Succeeded.ShouldBeTrue();
        return new Filing(file, source, to);
    }

    private static async Task RefileAsync(MoveHarness h, Filing filing, string target) =>
        (await h.Mover().MoveAsync(new MoveRequest(filing.File.Id, ActionKind.Move, filing.Target, target,
            filing.File.Sha256, InReview, FiledAsBill), Ct)).Succeeded.ShouldBeTrue();

    [Fact]
    public async Task Undoing_a_first_filing_puts_the_file_back_and_ignores_its_hash()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, true, filing.Source, null));
        File.ReadAllText(filing.Source).ShouldBe("receipt photo");
        File.Exists(filing.Target).ShouldBeFalse();
        var file = await h.FileAsync(filing.File.Id);
        (file.Status, file.CurrentPath).ShouldBe((FileStatus.Undone, filing.Source));
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeTrue();
        (await h.ActionsAsync(filing.File.Id)).Select(a => (a.Kind, a.Status))
            .ShouldBe([(ActionKind.Move, ActionStatus.Done), (ActionKind.UndoMove, ActionStatus.Done)]);
    }

    [Fact]
    public async Task After_an_undo_the_same_file_arriving_again_is_skipped()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        await h.Undo().UndoFileAsync(filing.File.Id, Ct);
        var secondCopy = h.Watched("IMG_2041 (1).jpg", "receipt photo");
        var gate = new FileGate(new FakeWatchedFolders(), h.Ignored, h.Files, new PipelineSettings(),
            (_, _) => Task.CompletedTask);

        foreach (var path in new[] { filing.Source, secondCopy })
            (await gate.CheckAsync(new IncomingFile(path, FileOrigin.Folder, null, DateTimeOffset.UtcNow), Ct))
                .ShouldBe(new GateResult.Skip(GateSkipReason.Ignored));
    }

    [Fact]
    public async Task Undo_is_refused_when_the_file_was_edited_after_filing()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        File.WriteAllText(filing.Target, "receipt photo, annotated by the user");

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, false, null, FileChanged));
        File.ReadAllText(filing.Target).ShouldBe("receipt photo, annotated by the user");
        File.Exists(filing.Source).ShouldBeFalse();
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Filed);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeFalse();
        (await h.ActionsAsync(filing.File.Id)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Undo_is_refused_when_the_file_was_moved_away()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        File.Move(filing.Target, filing.Target + ".moved-by-user");

        (await h.Undo().UndoFileAsync(filing.File.Id, Ct))
            .ShouldBe(new UndoResult(filing.File.Id, false, null, FileChanged));
    }

    [Fact]
    public async Task Undoing_a_copy_only_filing_deletes_only_SlothBoxs_copy()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "PXL_0001.jpg", "camera roll photo", ActionKind.Copy);

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, true, filing.Source, null));
        File.ReadAllText(filing.Source).ShouldBe("camera roll photo");
        File.Exists(filing.Target).ShouldBeFalse();
        (await h.ActionsAsync(filing.File.Id)).Select(a => a.Kind).ShouldBe([ActionKind.Copy, ActionKind.DeleteCopy]);
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Undone);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_copy_is_kept_when_its_original_has_gone_from_the_copy_only_folder()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "PXL_0001.jpg", "camera roll photo", ActionKind.Copy);
        File.Delete(filing.Source);

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, false, null, OriginalMissing));
        File.ReadAllText(filing.Target).ShouldBe("camera roll photo");
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Filed);
    }

    [Fact]
    public async Task A_copy_is_kept_when_its_original_has_changed_in_the_copy_only_folder()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "PXL_0001.jpg", "camera roll photo", ActionKind.Copy);
        File.WriteAllText(filing.Source, "cropped in the gallery app");

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, false, null, OriginalMissing));
        File.ReadAllText(filing.Target).ShouldBe("camera roll photo");
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Filed);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_stale_undo_of_a_copy_already_undone_is_refused_and_keeps_the_original()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "PXL_0001.jpg", "camera roll photo", ActionKind.Copy);
        var stale = await h.ActionsAsync(filing.File.Id);
        (await h.Undo().UndoFileAsync(filing.File.Id, Ct)).Succeeded.ShouldBeTrue();
        var staleUndo = new UndoService(new StaleLog(h.Log, stale), h.Files, h.Receipts, h.Mover());

        var result = await staleUndo.UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, false, null, FileChanged));
        File.ReadAllText(filing.Source).ShouldBe("camera roll photo");
        (await h.ActionsAsync(filing.File.Id)).Count.ShouldBe(2);
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Undone);
    }

    [Fact]
    public async Task A_second_undo_started_from_a_stale_view_cannot_undo_the_file_again()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        var seenByTheSecondClick = await h.SnapshotAsync(filing.File.Id);
        var secondClick = new MoveRequest(filing.File.Id, ActionKind.UndoMove, filing.Target, filing.Source,
            filing.File.Sha256, seenByTheSecondClick, seenByTheSecondClick with { Status = FileStatus.Undone });
        (await h.Undo().UndoFileAsync(filing.File.Id, Ct)).Succeeded.ShouldBeTrue();

        var second = await h.Mover().MoveAsync(secondClick, Ct);

        second.Succeeded.ShouldBeFalse();
        File.ReadAllText(filing.Source).ShouldBe("receipt photo");
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Undone);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_open_in_another_program_is_refused_and_undo_since_carries_on_with_the_rest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux file locks are advisory and do not block reads.");
        await using var h = await MoveHarness.CreateAsync(postgres);
        var older = await FileItAsync(h, "older.pdf", "older", target: ["Documents", "Work", "older.pdf"]);
        var open = await FileItAsync(h, "notes.docx", "open in Word", target: ["Documents", "Work", "notes.docx"]);
        var since = (await h.ActionsAsync(older.File.Id))[0].CreatedAt;

        IReadOnlyList<UndoResult> results;
        await using (new FileStream(open.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            results = await h.Undo().UndoSinceAsync(since, Ct);

        results.ShouldBe([
            new UndoResult(open.File.Id, false, null, FileOpen),
            new UndoResult(older.File.Id, true, older.Source, null)
        ]);
        File.ReadAllText(open.Target).ShouldBe("open in Word");
    }

    [Fact]
    public async Task Undo_after_File_it_returns_to_Needs_review_and_undo_again_returns_to_the_source_folder()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo", state: InReview,
            target: ["Needs review", "IMG_2041.jpg"]);
        await RefileAsync(h, filing, h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg"));

        var first = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        first.ShouldBe(new UndoResult(filing.File.Id, true, filing.Target, null));
        File.ReadAllText(filing.Target).ShouldBe("receipt photo");
        (await h.SnapshotAsync(filing.File.Id)).ShouldBe(InReview);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeFalse();

        var second = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        second.ShouldBe(new UndoResult(filing.File.Id, true, filing.Source, null));
        File.ReadAllText(filing.Source).ShouldBe("receipt photo");
        (await h.SnapshotAsync(filing.File.Id)).ShouldBe(new FileSnapshot(FileStatus.Undone, FileKind.Receipt, 5,
            "Jio recharge", null, new ReceiptData("Jio", null, 599m, "INR", PaymentMethod.Upi)));
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_first_filing_can_still_be_undone_after_its_refile_was_undone_to_a_restored_name()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo", state: InReview,
            target: ["Needs review", "IMG_2041.jpg"]);
        await RefileAsync(h, filing, h.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg"));
        File.WriteAllText(filing.Target, "another file that took the name meanwhile");
        var restored = Path.Combine(h.Output("Needs review"), "IMG_2041 (restored).jpg");
        (await h.Undo().UndoFileAsync(filing.File.Id, Ct)).ShouldBe(new UndoResult(filing.File.Id, true, restored, null));

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.ShouldBe(new UndoResult(filing.File.Id, true, filing.Source, null));
        File.ReadAllText(filing.Source).ShouldBe("receipt photo");
        File.Exists(restored).ShouldBeFalse();
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Undone);
    }

    [Fact]
    public async Task Undo_last_reverses_the_newest_action_overall_then_the_one_before_it()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var older = await FileItAsync(h, "older.pdf", "older", target: ["Documents", "Work", "older.pdf"]);
        var newer = await FileItAsync(h, "newer.pdf", "newer", target: ["Documents", "Work", "newer.pdf"]);

        (await h.Undo().UndoLastAsync(Ct)).ShouldBe(new UndoResult(newer.File.Id, true, newer.Source, null));
        File.ReadAllText(older.Target).ShouldBe("older");

        (await h.Undo().UndoLastAsync(Ct)).ShouldBe(new UndoResult(older.File.Id, true, older.Source, null));
        (await h.Undo().UndoLastAsync(Ct)).ShouldBe(new UndoResult(Guid.Empty, false, null, NothingToUndo));
    }

    [Fact]
    public async Task Undo_since_reverses_every_action_since_the_chosen_time_newest_first()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var kept = await FileItAsync(h, "kept.pdf", "kept", target: ["Documents", "Work", "kept.pdf"]);
        var refiled = await FileItAsync(h, "refiled.jpg", "refiled", state: InReview,
            target: ["Needs review", "refiled.jpg"]);
        await RefileAsync(h, refiled, h.Output("Receipts", "Bills and recharges", "refiled.jpg"));
        var latest = await FileItAsync(h, "latest.png", "latest", target: ["Screenshots", "Chats", "latest.png"]);
        var since = (await h.ActionsAsync(refiled.File.Id))[0].CreatedAt;

        var results = await h.Undo().UndoSinceAsync(since, Ct);

        results.ShouldBe([
            new UndoResult(latest.File.Id, true, latest.Source, null),
            new UndoResult(refiled.File.Id, true, refiled.Target, null),
            new UndoResult(refiled.File.Id, true, refiled.Source, null)
        ]);
        File.ReadAllText(latest.Source).ShouldBe("latest");
        File.ReadAllText(refiled.Source).ShouldBe("refiled");
        File.ReadAllText(kept.Target).ShouldBe("kept");
        (await h.FileAsync(kept.File.Id)).Status.ShouldBe(FileStatus.Filed);
    }

    [Fact]
    public async Task Undo_since_leaves_a_files_older_actions_alone_once_a_newer_one_is_refused()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var refiled = await FileItAsync(h, "refiled.jpg", "refiled", state: InReview,
            target: ["Needs review", "refiled.jpg"]);
        var billPath = h.Output("Receipts", "Bills and recharges", "refiled.jpg");
        await RefileAsync(h, refiled, billPath);
        File.WriteAllText(billPath, "refiled, then edited by the user");
        var since = (await h.ActionsAsync(refiled.File.Id))[0].CreatedAt;

        var results = await h.Undo().UndoSinceAsync(since, Ct);

        results.ShouldBe([new UndoResult(refiled.File.Id, false, null, FileChanged)]);
        File.ReadAllText(billPath).ShouldBe("refiled, then edited by the user");
        (await h.ActionsAsync(refiled.File.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task When_the_original_path_is_taken_the_file_is_restored_beside_it()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        File.WriteAllText(filing.Source, "a newer photo with the same name");

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        var restored = Path.Combine(Path.GetDirectoryName(filing.Source)!, "IMG_2041 (restored).jpg");
        result.ShouldBe(new UndoResult(filing.File.Id, true, restored, null));
        File.ReadAllText(restored).ShouldBe("receipt photo");
        File.ReadAllText(filing.Source).ShouldBe("a newer photo with the same name");
        (await h.FileAsync(filing.File.Id)).CurrentPath.ShouldBe(restored);
    }

    [Fact]
    public async Task An_undo_that_cannot_move_the_file_back_leaves_it_filed_and_its_hash_allowed()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, Path.Combine("phone", "IMG_2041.jpg"), "receipt photo");
        var sourceFolder = Path.GetDirectoryName(filing.Source)!;
        Directory.Delete(sourceFolder);
        File.WriteAllText(sourceFolder, "a file that took the folder's name");

        var result = await h.Undo().UndoFileAsync(filing.File.Id, Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(filing.Target).ShouldBe("receipt photo");
        (await h.FileAsync(filing.File.Id)).Status.ShouldBe(FileStatus.Filed);
        (await h.Ignored.ContainsAsync(filing.File.Sha256, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task With_nothing_left_to_undo_undo_is_refused()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var filing = await FileItAsync(h, "IMG_2041.jpg", "receipt photo");
        (await h.Undo().UndoFileAsync(filing.File.Id, Ct)).Succeeded.ShouldBeTrue();

        (await h.Undo().UndoFileAsync(filing.File.Id, Ct))
            .ShouldBe(new UndoResult(filing.File.Id, false, null, NothingToUndo));
    }
}
