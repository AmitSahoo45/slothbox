using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class UndoEndpointTests(PostgresFixture postgres)
{
    private const string FileChanged = "The file changed or moved after SlothBox filed it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<ServerEvent> Drain(ServerEvents.Subscription subscription)
    {
        var events = new List<ServerEvent>();
        while (subscription.Events.TryRead(out var published))
            events.Add(published);
        return events;
    }

    private static string Query(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O"));

    [Fact]
    public async Task Undo_on_a_file_puts_it_back_and_tells_the_browser()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        using var events = h.Service<ServerEvents>().Subscribe();

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{receipt.Id}/undo");

        (await response.ReadAsync<UndoResultDto>())
            .ShouldBe(new UndoResultDto(receipt.Id, true, receipt.OriginalPath, null));
        File.Exists(receipt.OriginalPath).ShouldBeTrue();
        Drain(events).ShouldBe([new ServerEvent("file.undone", new FileUndoneEvent(receipt.Id))]);
    }

    [Fact]
    public async Task A_refused_undo_says_why_and_changes_nothing()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        File.WriteAllText(receipt.CurrentPath, "annotated by the user");
        using var events = h.Service<ServerEvents>().Subscribe();

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{receipt.Id}/undo");

        (await response.ReadAsync<UndoResultDto>()).ShouldBe(new UndoResultDto(receipt.Id, false, null, FileChanged));
        Drain(events).ShouldBeEmpty();
    }

    [Fact]
    public async Task Undo_last_reverses_the_newest_action()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var older = await h.FileAsync("older.jpg", Analyses.Chat with { Title = "older chat" });
        var newer = await h.FileAsync("newer.jpg", Analyses.Chat with { Title = "newer chat" });

        using var response = await h.SendAsync(HttpMethod.Post, "/api/undo/last");

        (await response.ReadAsync<UndoResultDto>()).ShouldBe(new UndoResultDto(newer.Id, true, newer.OriginalPath, null));
        File.Exists(older.CurrentPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Undo_since_reverses_every_action_since_the_time_newest_first_and_counts_them()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var kept = await h.FileAsync("kept.jpg", Analyses.Chat with { Title = "kept chat" });
        var first = await h.FileAsync("first.jpg", Analyses.Chat with { Title = "first chat" });
        var second = await h.FileAsync("second.jpg", Analyses.Chat with { Title = "second chat" });
        var since = (await h.Moves.ActionsAsync(first.Id))[0].CreatedAt;
        using var events = h.Service<ServerEvents>().Subscribe();

        using var response = await h.SendAsync(HttpMethod.Post, "/api/undo/since", new UndoSinceRequest(since));

        var batch = await response.ReadAsync<UndoBatchResultDto>();
        batch.Undone.ShouldBe(2);
        batch.Results.Select(result => (result.FileId, result.Success))
            .ShouldBe([(second.Id, true), (first.Id, true)]);
        File.Exists(kept.CurrentPath).ShouldBeTrue();
        Drain(events).Select(published => published.Payload)
            .ShouldBe([new FileUndoneEvent(second.Id), new FileUndoneEvent(first.Id)]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"sinse":"2026-10-20T10:30:00Z"}""")]
    [InlineData("""{"since":"0001-01-01T00:00:00+00:00"}""")]
    public async Task Undo_since_without_a_time_is_refused_and_undoes_nothing(string body)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);

        using var response = await h.SendJsonAsync(HttpMethod.Post, "/api/undo/since", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.Exists(receipt.CurrentPath).ShouldBeTrue();
    }

    [Fact]
    public async Task The_preview_counts_the_actions_undo_since_would_reverse()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("kept.jpg", Analyses.Chat with { Title = "kept chat" });
        var review = await h.FileAsync("no-date.jpg", Analyses.NoDate);
        var since = (await h.Moves.ActionsAsync(review.Id))[0].CreatedAt;
        // "File it" on the review item: a second action of the same file, also undone by Undo since.
        var before = await h.Moves.SnapshotAsync(review.Id);
        (await h.Moves.Mover().MoveAsync(new MoveRequest(review.Id, ActionKind.Move, review.CurrentPath,
            h.Pipeline.Output("Receipts", "Bills and recharges", "2026-10-20_Jio_599INR.jpg"), review.Sha256, before,
            before with { Status = FileStatus.Filed, ReviewReason = null, CategoryId = 3 }), Ct)).Succeeded.ShouldBeTrue();
        await h.FileAsync("latest.jpg", Analyses.Chat with { Title = "latest chat" });
        // Filed and already undone: nothing left to undo.
        var undone = await h.FileAsync("undone.jpg", Analyses.Chat with { Title = "undone chat" });
        (await h.Moves.Undo().UndoFileAsync(undone.Id, Ct)).Succeeded.ShouldBeTrue();

        var preview = await h.GetAsync<UndoSincePreviewDto>($"/api/undo/since?since={Query(since)}");

        preview.ShouldBe(new UndoSincePreviewDto(3));
        using var response = await h.SendAsync(HttpMethod.Post, "/api/undo/since", new UndoSinceRequest(since));
        (await response.ReadAsync<UndoBatchResultDto>()).Undone.ShouldBe(3);
        (await h.GetAsync<UndoSincePreviewDto>($"/api/undo/since?since={Query(since)}")).Actions.ShouldBe(0);
    }

    [Fact]
    public async Task A_time_in_the_future_has_nothing_to_undo()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);

        (await h.GetAsync<UndoSincePreviewDto>($"/api/undo/since?since={Query(DateTimeOffset.UtcNow.AddHours(1))}"))
            .ShouldBe(new UndoSincePreviewDto(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?since=yesterday")]
    public async Task The_preview_needs_a_time(string query)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Get, $"/api/undo/since{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
