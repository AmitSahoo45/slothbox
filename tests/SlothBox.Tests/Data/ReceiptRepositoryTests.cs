using SlothBox.Core;
using SlothBox.Data.Files;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class ReceiptRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_receipt_row_is_saved_corrected_read_back_and_deleted()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var fileId = await DataSeed.FileAsync(db);
        var receipts = new ReceiptRepository(db);
        var receipt = new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599.50m, "INR", PaymentMethod.Upi);

        (await receipts.GetAsync(fileId, Ct)).ShouldBeNull();
        await receipts.SaveAsync(fileId, receipt, Ct);
        (await receipts.GetAsync(fileId, Ct)).ShouldBe(receipt);

        var corrected = receipt with { Amount = 600m, PaymentMethod = PaymentMethod.Card };
        await receipts.SaveAsync(fileId, corrected, Ct);
        (await receipts.GetAsync(fileId, Ct)).ShouldBe(corrected);

        await receipts.DeleteAsync(fileId, Ct);
        (await receipts.GetAsync(fileId, Ct)).ShouldBeNull();
    }
}
