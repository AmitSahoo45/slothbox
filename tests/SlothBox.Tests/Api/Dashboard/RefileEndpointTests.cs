using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class RefileEndpointTests(PostgresFixture postgres)
{
    private static readonly ReceiptDto JioOn21 = new("Jio", new DateOnly(2026, 10, 21), 599m, null, PaymentMethod.Upi);

    private static async Task<FileDetailsDto> RefileAsync(DashboardHarness h, Guid id, RefileRequest request)
    {
        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{id}/refile", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<FileDetailsDto>();
    }

    [Fact]
    public async Task File_it_files_a_review_item_by_its_fields_and_undo_puts_it_back_in_Needs_review()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var item = await h.FileAsync("no-date.jpg", Analyses.NoDate);
        using var events = h.Service<ServerEvents>().Subscribe();

        var details = await RefileAsync(h, item.Id, new RefileRequest(FileKind.Receipt, 3, "Jio recharge", JioOn21));

        var target = h.Pipeline.Output("Receipts", "Bills and recharges", "2026-10-21_Jio_599INR.jpg");
        (details.File.Status, details.ReviewReason, details.CurrentPath, details.CategoryId, details.File.Category)
            .ShouldBe((FileStatus.Filed, (ReviewReason?)null, target, (int?)3, "Bills and recharges"));
        // A missing currency takes the default (spec 5.2).
        details.Receipt.ShouldBe(JioOn21 with { Currency = "INR" });
        details.History.Select(action => (action.Kind, action.Status))
            .ShouldBe([(ActionKind.Move, ActionStatus.Done), (ActionKind.Move, ActionStatus.Done)]);
        File.Exists(target).ShouldBeTrue();
        events.Events.TryRead(out var refiled).ShouldBeTrue();
        (refiled!.Name, ((FileRefiledEvent)refiled.Payload).FileId).ShouldBe(("file.refiled", item.Id));
        // A Needs review item is indexed once it is filed (spec 5.1 step 6).
        h.Pipeline.Embedder.Documents.ShouldHaveSingleItem().Title.ShouldBe("Jio recharge");

        using var undo = await h.SendAsync(HttpMethod.Post, $"/api/files/{item.Id}/undo");

        (await undo.ReadAsync<UndoResultDto>()).Success.ShouldBeTrue();
        var back = await h.GetAsync<FileDetailsDto>($"/api/files/{item.Id}");
        (back.File.Status, back.ReviewReason, back.CurrentPath)
            .ShouldBe((FileStatus.Review, (ReviewReason?)ReviewReason.MissingDate, item.CurrentPath));
    }

    [Fact]
    public async Task Changing_a_files_category_moves_it_to_that_categorys_folder()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var chat = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);

        var details = await RefileAsync(h, chat.Id, new RefileRequest(FileKind.Screenshot, 9));

        details.CurrentPath.ShouldBe(h.Pipeline.Output("Screenshots", "Code and errors",
            Path.GetFileName(chat.CurrentPath)));
        (details.File.Category, details.File.Title).ShouldBe(("Code and errors", "dinner plans chat"));
    }

    [Fact]
    public async Task Only_the_type_and_the_category_need_to_be_sent()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var notes = await h.FileTextAsync("notes.txt", "Some notes about .NET 10.", Analyses.Notes);

        using var response = await h.SendJsonAsync(HttpMethod.Post, $"/api/files/{notes.Id}/refile",
            """{"type":"document","categoryId":11}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadAsync<FileDetailsDto>()).CurrentPath
            .ShouldBe(h.Pipeline.Output("Documents", "Work", "dotnet-10-notes.txt"));
    }

    [Fact]
    public async Task A_name_taken_in_the_new_folder_gets_the_next_free_one()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var first = await h.FileAsync("first.jpg", Analyses.NoDate);
        var second = await h.FileAsync("second.jpg", Analyses.NoDate);
        await RefileAsync(h, first.Id, new RefileRequest(FileKind.Receipt, 3, null, JioOn21));

        var details = await RefileAsync(h, second.Id, new RefileRequest(FileKind.Receipt, 3, null, JioOn21));

        Path.GetFileName(details.CurrentPath).ShouldBe("2026-10-21_Jio_599INR-2.jpg");
    }

    [Fact]
    public async Task A_change_that_leaves_the_name_alone_updates_the_fields_in_place()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);

        var details = await RefileAsync(h, receipt.Id, new RefileRequest(FileKind.Receipt, 3, "Jio recharge",
            new ReceiptDto("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Card)));

        (details.CurrentPath, details.Receipt!.PaymentMethod).ShouldBe((receipt.CurrentPath, PaymentMethod.Card));
        details.History.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Saving_without_a_change_is_refused_and_logs_nothing()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var chat = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{chat.Id}/refile",
            new RefileRequest(FileKind.Screenshot, 8));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>()).Title.ShouldBe("Nothing to change.");
        (await h.GetAsync<FileDetailsDto>($"/api/files/{chat.Id}")).History.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_file_with_a_clash_suffix_keeps_its_name_when_only_its_fields_change()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var first = await h.FileAsync("first.jpg", Analyses.NoDate);
        var second = await h.FileAsync("second.jpg", Analyses.NoDate);
        await RefileAsync(h, first.Id, new RefileRequest(FileKind.Receipt, 3, null, JioOn21));
        var suffixed = await RefileAsync(h, second.Id, new RefileRequest(FileKind.Receipt, 3, null, JioOn21));

        var details = await RefileAsync(h, second.Id, new RefileRequest(FileKind.Receipt, 3, null,
            JioOn21 with { Currency = "INR", PaymentMethod = PaymentMethod.Card }));

        (details.CurrentPath, details.Receipt!.PaymentMethod).ShouldBe((suffixed.CurrentPath, PaymentMethod.Card));
        Path.GetFileName(details.CurrentPath).ShouldBe("2026-10-21_Jio_599INR-2.jpg");
        details.History.Count.ShouldBe(suffixed.History.Count);
    }

    [Fact]
    public async Task A_new_title_that_leaves_the_name_alone_is_indexed_again()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        var indexed = h.Pipeline.Embedder.Documents.Count;

        await RefileAsync(h, receipt.Id, new RefileRequest(FileKind.Receipt, 3, "Jio prepaid recharge",
            new ReceiptDto("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi)));

        h.Pipeline.Embedder.Documents.Count.ShouldBe(indexed + 1);
        h.Pipeline.Embedder.Documents[^1].Title.ShouldBe("Jio prepaid recharge");
    }

    [Theory]
    [InlineData("""{"type":"receipt","categoryId":11}""")]
    [InlineData("""{"type":"receipt","categoryId":999}""")]
    [InlineData("""{"type":"unknown","categoryId":3}""")]
    [InlineData("""{"type":"receipt","categoryId":3}""")]
    [InlineData("""{"type":"receipt","categoryId":3,"receipt":{"vendor":"Jio","date":null,"amount":599,"currency":"INR","paymentMethod":"upi"}}""")]
    [InlineData("""{"type":"receipt","categoryId":3,"receipt":{"vendor":"Jio","date":"2026-10-21","amount":599,"currency":"Rupees","paymentMethod":"upi"}}""")]
    [InlineData("""{"type":"receipt","categoryId":3,"receipt":{"vendor":"Jio","date":"2026-10-21","amount":-5,"currency":"INR","paymentMethod":"upi"}}""")]
    public async Task A_refile_that_cannot_be_filed_is_refused_with_its_reason(string body)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var item = await h.FileAsync("no-date.jpg", Analyses.NoDate);

        using var response = await h.SendJsonAsync(HttpMethod.Post, $"/api/files/{item.Id}/refile", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.Exists(item.CurrentPath).ShouldBeTrue();
        (await h.GetAsync<FileDetailsDto>($"/api/files/{item.Id}")).File.Status.ShouldBe(FileStatus.Review);
    }

    [Fact]
    public async Task An_undone_file_cannot_be_refiled()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var chat = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);
        (await h.Moves.Undo().UndoFileAsync(chat.Id, TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{chat.Id}/refile",
            new RefileRequest(FileKind.Screenshot, 9));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Refiling_an_unknown_file_is_not_found()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{Guid.NewGuid()}/refile",
            new RefileRequest(FileKind.Screenshot, 9));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
