using SlothBox.Core;
using SlothBox.Tests.Moving;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class ActionLogTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_from_a_path_that_is_not_the_files_current_path_is_refused()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var source = h.Watched("IMG_2041.jpg", "receipt photo");
        var file = await h.AddFileAsync(source);
        var elsewhere = h.Output("Receipts", "Food", "IMG_2041.jpg");

        var id = await h.Log.AppendPendingAsync(
            new MoveRequest(file.Id, ActionKind.UndoMove, elsewhere, source, file.Sha256), Ct);

        id.ShouldBeNull();
        (await h.ActionsAsync(file.Id)).ShouldBeEmpty();
    }

    private static readonly FileSnapshot FiledBill = new(FileStatus.Filed, FileKind.Receipt, 3, "Jio recharge", null,
        new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi));

    [Fact]
    public async Task A_field_update_applies_the_new_fields_and_receipt_together_without_an_action()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var bill = h.WriteOutput("receipt photo", "Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg");
        var file = await h.AddFileAsync(bill, FiledBill);
        var after = FiledBill with { Title = "Jio prepaid recharge", Receipt = FiledBill.Receipt! with { PaymentMethod = PaymentMethod.Card } };

        (await h.Log.UpdateFieldsAsync(file.Id, bill, FiledBill, after, Ct)).ShouldBeTrue();

        (await h.SnapshotAsync(file.Id)).ShouldBe(after);
        (await h.ActionsAsync(file.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_field_update_is_refused_while_a_change_is_pending_or_from_a_stale_view()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var bill = h.WriteOutput("receipt photo", "Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg");
        var file = await h.AddFileAsync(bill, FiledBill);
        var after = FiledBill with { Title = "Jio prepaid recharge" };

        (await h.Log.UpdateFieldsAsync(file.Id, bill, FiledBill with { Title = "an older title" }, after, Ct))
            .ShouldBeFalse();
        (await h.Log.UpdateFieldsAsync(file.Id, bill + ".elsewhere", FiledBill, after, Ct)).ShouldBeFalse();
        (await h.Log.AppendPendingAsync(new MoveRequest(file.Id, ActionKind.Move, bill,
            h.Output("Receipts", "Food", "bill.jpg"), file.Sha256), Ct)).ShouldNotBeNull();
        (await h.Log.UpdateFieldsAsync(file.Id, bill, FiledBill, after, Ct)).ShouldBeFalse();

        (await h.SnapshotAsync(file.Id)).ShouldBe(FiledBill);
    }

    [Fact]
    public async Task A_late_second_completion_does_not_overwrite_the_first()
    {
        await using var h = await MoveHarness.CreateAsync(postgres);
        var reviewItem = h.WriteOutput("receipt photo", "Needs review", "IMG_2041.jpg");
        var before = new FileSnapshot(FileStatus.Review, FileKind.Receipt, 5, "Jio recharge", ReviewReason.UnknownType,
            null);
        var after = new FileSnapshot(FileStatus.Filed, FileKind.Receipt, 3, "Jio recharge", null, null);
        var file = await h.AddFileAsync(reviewItem, before);
        var target = h.Output("Receipts", "Bills and recharges", "IMG_2041.jpg");
        var id = (await h.Log.AppendPendingAsync(
            new MoveRequest(file.Id, ActionKind.Move, reviewItem, target, file.Sha256, before, after), Ct)).ShouldNotBeNull();

        await h.Log.CompleteAsync(id, ActionStatus.Done, null, Ct);
        await h.Log.CompleteAsync(id, ActionStatus.Failed, "a late failure", Ct);

        var action = (await h.ActionsAsync(file.Id)).ShouldHaveSingleItem();
        (action.Status, action.Error).ShouldBe((ActionStatus.Done, (string?)null));
        (await h.SnapshotAsync(file.Id)).ShouldBe(after);
        (await h.FileAsync(file.Id)).CurrentPath.ShouldBe(target);
    }
}
