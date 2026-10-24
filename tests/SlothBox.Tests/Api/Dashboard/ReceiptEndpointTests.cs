using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

// The Receipts page's API (spec 7.4.5, 8.2): a month's filed receipts, totals per currency, and the CSV export.
[Collection(PostgresCollection.Name)]
public sealed class ReceiptEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Analysis Receipt(string category, string vendor, DateOnly date, decimal amount, string currency,
        PaymentMethod paidBy = PaymentMethod.Card) =>
        Analyses.JioReceipt with
        {
            Category = category, Title = $"{vendor} receipt",
            Receipt = new ReceiptData(vendor, date, amount, currency, paidBy)
        };

    [Fact]
    public async Task A_month_lists_its_receipts_newest_first_with_totals_per_currency_and_category()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var jio = await h.FileAsync("jio.jpg", Analyses.JioReceipt);
        await h.FileAsync("swiggy.jpg", Receipt("Food", "Swiggy", new DateOnly(2026, 10, 18), 412m, "INR"));
        await h.FileAsync("steam.jpg", Receipt("Shopping", "Steam", new DateOnly(2026, 10, 5), 9.99m, "USD"));
        await h.FileAsync("september.jpg", Receipt("Food", "Zomato", new DateOnly(2026, 9, 30), 250m, "INR"));

        var month = await h.GetAsync<ReceiptsDto>("/api/receipts?month=2026-10");

        month.Month.ShouldBe("2026-10");
        month.Items.Select(row => (row.Vendor, row.Date, row.Amount, row.Currency, row.PaymentMethod, row.Category))
            .ShouldBe([
                ("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi, "Bills and recharges"),
                ("Swiggy", new DateOnly(2026, 10, 18), 412m, "INR", PaymentMethod.Card, "Food"),
                ("Steam", new DateOnly(2026, 10, 5), 9.99m, "USD", PaymentMethod.Card, "Shopping")
            ]);
        month.Items[0].FileId.ShouldBe(jio.Id);
        month.Items[0].CategoryId.ShouldBe(3);
        month.Totals.ShouldBe([new CurrencyTotalDto("INR", 1011m, 2), new CurrencyTotalDto("USD", 9.99m, 1)]);
        month.Categories.ShouldBe([
            new CategoryTotalDto(3, "Bills and recharges", "INR", 599m), new CategoryTotalDto(1, "Food", "INR", 412m),
            new CategoryTotalDto(2, "Shopping", "USD", 9.99m)
        ]);
    }

    [Fact]
    public async Task Receipts_waiting_in_needs_review_and_undone_ones_are_left_out()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("no-date.jpg", Analyses.NoDate);
        var undone = await h.FileAsync("jio.jpg", Analyses.JioReceipt);
        using var undo = await h.SendAsync(HttpMethod.Post, $"/api/files/{undone.Id}/undo");
        undo.StatusCode.ShouldBe(HttpStatusCode.OK);

        var month = await h.GetAsync<ReceiptsDto>("/api/receipts?month=2026-10");

        month.Items.ShouldBeEmpty();
        month.Totals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_month_the_current_one_is_shown()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        (await h.GetAsync<ReceiptsDto>("/api/receipts")).Month.ShouldBe(DateTime.Now.ToString("yyyy-MM"));
    }

    [Theory]
    [InlineData("/api/receipts?month=2026-13")]
    [InlineData("/api/receipts?month=October")]
    [InlineData("/api/receipts/export.csv?month=2026-1")]
    public async Task A_month_that_is_not_one_is_a_bad_request(string path)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // A vendor comes from Gemma, so a cell that a spreadsheet would run as a formula is turned into text.
    [Fact]
    public async Task The_csv_export_has_a_row_per_receipt_of_the_month()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var jio = await h.FileAsync("jio.jpg", Analyses.JioReceipt);
        await h.FileAsync("cash.jpg", Receipt("Food", "=HYPERLINK(\"x\")", new DateOnly(2026, 10, 19), 10m, "INR",
            PaymentMethod.Cash));

        using var response = await h.SendAsync(HttpMethod.Get, "/api/receipts/export.csv?month=2026-10");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("slothbox-receipts-2026-10.csv");
        var lines = (await response.Content.ReadAsStringAsync(Ct)).TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("Date,Vendor,Category,Paid by,Amount,Currency,File");
        lines[1].ShouldBe($"2026-10-20,Jio,Bills and recharges,UPI,599.00,INR,{Path.GetFileName(jio.CurrentPath)}");
        lines[2].ShouldStartWith("2026-10-19,\"'=HYPERLINK(\"\"x\"\")\",Food,Cash,10.00,INR,");
    }
}
