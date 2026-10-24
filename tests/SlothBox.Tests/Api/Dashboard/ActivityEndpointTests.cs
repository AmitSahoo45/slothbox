using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class ActivityEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_feed_lists_files_newest_first_as_activity_items()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        var chat = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);

        var page = await h.GetAsync<ActivityPageDto>("/api/activity");

        page.Items.Select(item => item.FileId).ShouldBe([chat.Id, receipt.Id]);
        page.NextCursor.ShouldBeNull();
        var item = page.Items[1];
        (item with { At = default }).ShouldBe(new ActivityItemDto
        {
            FileId = receipt.Id, At = default, Status = FileStatus.Filed, Type = FileKind.Receipt,
            Category = "Bills and recharges", Title = "Jio recharge", NewName = "2026-10-20_Jio_599INR.jpg",
            Folder = h.Pipeline.Output("Receipts", "Bills and recharges"), Origin = FileOrigin.Folder,
            FolderLabel = "Downloads", ThumbUrl = $"/api/files/{receipt.Id}/thumb", CanUndo = true
        });
        item.At.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task The_feed_pages_with_the_cursor_it_hands_out()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var first = await h.FileAsync("a.jpg", Analyses.Chat with { Title = "first chat" });
        var second = await h.FileAsync("b.jpg", Analyses.Chat with { Title = "second chat" });
        var third = await h.FileAsync("c.jpg", Analyses.Chat with { Title = "third chat" });

        var page = await h.GetAsync<ActivityPageDto>("/api/activity?limit=2");

        page.Items.Select(item => item.FileId).ShouldBe([third.Id, second.Id]);
        var next = await h.GetAsync<ActivityPageDto>(
            $"/api/activity?limit=2&cursor={Uri.EscapeDataString(page.NextCursor.ShouldNotBeNull())}");
        next.Items.Select(item => item.FileId).ShouldBe([first.Id]);
        next.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task An_undone_file_shows_as_undone_where_it_was_put_back_and_cannot_be_undone_again()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        (await h.Moves.Undo().UndoFileAsync(receipt.Id, Ct)).Succeeded.ShouldBeTrue();

        var item = (await h.GetAsync<ActivityPageDto>("/api/activity")).Items.ShouldHaveSingleItem();

        (item.Status, item.NewName, item.Folder, item.CanUndo)
            .ShouldBe((FileStatus.Undone, "IMG_2041.jpg", Path.GetDirectoryName(receipt.OriginalPath)!, false));
    }

    [Fact]
    public async Task A_filing_that_never_finished_is_not_in_the_feed()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.Moves.AddFileAsync(h.Pipeline.Watched("half.jpg", "half a photo"));

        (await h.GetAsync<ActivityPageDto>("/api/activity")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_images_get_a_thumbnail_link()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileTextAsync("notes.txt", "Some notes about .NET 10.", Analyses.Notes);

        (await h.GetAsync<ActivityPageDto>("/api/activity")).Items.ShouldHaveSingleItem().ThumbUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("-5")]
    public async Task A_cursor_SlothBox_did_not_hand_out_is_refused(string cursor)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Get, $"/api/activity?cursor={cursor}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
