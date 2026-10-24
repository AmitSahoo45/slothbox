using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class ReviewEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Needs_review_lists_its_items_in_queue_order_with_reasons_and_details()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var noDate = await h.FileAsync("no-date.jpg", Analyses.NoDate);
        var unknown = await h.FileAsync("mystery.jpg", Analyses.Unknown);
        await h.FileAsync("filed.jpg", Analyses.Chat);

        var items = await h.GetAsync<List<ReviewItemDto>>("/api/review");

        items.Select(item => (item.File.File.FileId, item.Reason))
            .ShouldBe([(noDate.Id, ReviewReason.MissingDate), (unknown.Id, ReviewReason.UnknownType)]);
        var first = items[0].File;
        (first.File.Status, first.ReviewReason, first.CurrentPath)
            .ShouldBe((FileStatus.Review, (ReviewReason?)ReviewReason.MissingDate, h.Pipeline.Output("Needs review", "no-date.jpg")));
        first.Receipt.ShouldBe(new ReceiptDto("Jio", null, 599m, "INR", PaymentMethod.Upi));
    }

    [Fact]
    public async Task Needs_review_reads_any_number_of_items_with_a_few_light_queries()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        for (var n = 1; n <= 4; n++)
            await h.FileAsync($"no-date-{n}.jpg", Analyses.NoDate);
        h.Commands.Clear();

        var items = await h.GetAsync<List<ReviewItemDto>>("/api/review");

        items.Count.ShouldBe(4);
        items.ShouldAllBe(item => item.File.Receipt != null && item.File.History.Count == 1);
        h.Commands.Commands.Count.ShouldBeLessThanOrEqualTo(3);
        // The page never needs a file's stored text or its vector.
        h.Commands.Commands.ShouldAllBe(sql => !sql.Contains("extracted_text") && !sql.Contains("embedding"));
    }

    [Fact]
    public async Task Skip_for_now_moves_an_item_to_the_end_of_the_queue()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var first = await h.FileAsync("no-date.jpg", Analyses.NoDate);
        var second = await h.FileAsync("mystery.jpg", Analyses.Unknown);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/review/{first.Id}/skip");

        (await response.ReadAsync<MutationResultDto>()).ShouldBe(new MutationResultDto(true, null));
        (await h.GetAsync<List<ReviewItemDto>>("/api/review")).Select(item => item.File.File.FileId)
            .ShouldBe([second.Id, first.Id]);
    }

    [Fact]
    public async Task Only_a_file_in_Needs_review_can_be_skipped()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var filed = await h.FileAsync("filed.jpg", Analyses.Chat);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/review/{filed.Id}/skip");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadAsync<MutationResultDto>())
            .ShouldBe(new MutationResultDto(false, "This file is not in Needs review."));
    }

    [Fact]
    public async Task Skipping_an_unknown_file_is_not_found()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/review/{Guid.NewGuid()}/skip");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
