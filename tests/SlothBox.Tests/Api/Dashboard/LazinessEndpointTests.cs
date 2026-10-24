using System.Net;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class LazinessEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Monday => DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));

    [Fact]
    public async Task The_meter_counts_each_first_filing_by_type_with_receipts_and_duplicates()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var jio = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        await h.FileAsync("IMG_2042.jpg", Analyses.JioReceipt with
        {
            Receipt = Analyses.Jio with { Vendor = "Cafe", Amount = 120.50m }
        });
        var chat = await h.FileAsync("Screenshot_1.jpg", Analyses.Chat);
        await h.FileTextAsync("notes.txt", "Some notes about .NET 10.", Analyses.Notes);
        await h.FileAsync("no-date.jpg", Analyses.NoDate);
        var undone = await h.FileAsync("undone.jpg", Analyses.Chat with { Title = "undone chat" });
        (await h.Moves.Undo().UndoFileAsync(undone.Id, Ct)).Succeeded.ShouldBeTrue();
        var copy = h.Pipeline.WatchedPath("IMG_2041 (1).jpg");
        File.Copy(jio.CurrentPath, copy);
        await h.FilePathAsync(copy, Analyses.JioReceipt);
        // A refile is not a first filing, so it does not count again.
        var before = await h.Moves.SnapshotAsync(chat.Id);
        (await h.Moves.Mover().MoveAsync(new MoveRequest(chat.Id, ActionKind.Move, chat.CurrentPath,
            h.Pipeline.Output("Screenshots", "Other", Path.GetFileName(chat.CurrentPath)), chat.Sha256, before,
            before with { CategoryId = 10 }), Ct)).Succeeded.ShouldBeTrue();

        var stats = await h.GetAsync<LazinessStatsDto>("/api/stats/laziness?range=all");

        // Two receipts, a screenshot, a document and the Needs review receipt; the undone file and the duplicate
        // do not count as filed.
        (stats.FilesFiled, stats.MinutesSaved, stats.ReceiptsLogged, stats.DuplicatesCaught)
            .ShouldBe((5, (30 + 30 + 15 + 15 + 30) / 60.0, 2, 1));
        stats.ReceiptTotals.ShouldBe([new CurrencyTotalDto("INR", 719.50m, 2)]);
        stats.SecondsPerType.ShouldBe(new LazinessSettings());
        stats.Days.Select(day => day.Date).ShouldBe(Enumerable.Range(0, 7)
            .Select(offset => DateOnly.FromDateTime(Monday.AddDays(offset))));
        stats.Days.Single(day => day.Date == DateOnly.FromDateTime(DateTime.Today)).Count.ShouldBe(5);
        stats.Days.Sum(day => day.Count).ShouldBe(5);
    }

    // Settings accepts any whole number of seconds (spec 7.4.8); the minutes saved must not overflow.
    [Fact]
    public async Task The_largest_estimates_add_up_without_overflowing()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        h.Pipeline.Settings.Current = h.Pipeline.Settings.Current with
        {
            Laziness = new LazinessSettings(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)
        };
        await h.FileAsync("first.jpg", Analyses.Chat with { Title = "first chat" });
        await h.FileAsync("second.jpg", Analyses.Chat with { Title = "second chat" });

        (await h.GetAsync<LazinessStatsDto>("/api/stats/laziness?range=all")).MinutesSaved
            .ShouldBe(2L * int.MaxValue / 60.0);
    }

    [Fact]
    public async Task Today_counts_from_local_midnight_and_this_week_from_Monday()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("today.jpg", Analyses.Chat with { Title = "today chat" });
        var yesterday = await h.FileAsync("yesterday.jpg", Analyses.Chat with { Title = "yesterday chat" });
        var lastWeek = await h.FileAsync("last-week.jpg", Analyses.Chat with { Title = "last week chat" });
        await h.FiledAtAsync(yesterday.Id, DateTime.Today.AddMinutes(-30));
        await h.FiledAtAsync(lastWeek.Id, Monday.AddDays(-2));

        var today = await h.GetAsync<LazinessStatsDto>("/api/stats/laziness?range=today");
        var week = await h.GetAsync<LazinessStatsDto>("/api/stats/laziness?range=week");
        var all = await h.GetAsync<LazinessStatsDto>("/api/stats/laziness?range=all");

        today.FilesFiled.ShouldBe(1);
        // Yesterday is in this week unless today is Monday.
        week.FilesFiled.ShouldBe(DateTime.Today == Monday ? 1 : 2);
        all.FilesFiled.ShouldBe(3);
        all.Days.Sum(day => day.Count).ShouldBe(week.FilesFiled);
    }

    [Fact]
    public async Task With_nothing_filed_the_meter_reads_zero()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        var stats = await h.GetAsync<LazinessStatsDto>("/api/stats/laziness");

        (stats.FilesFiled, stats.MinutesSaved, stats.ReceiptsLogged, stats.DuplicatesCaught).ShouldBe((0, 0.0, 0, 0));
        stats.ReceiptTotals.ShouldBeEmpty();
        stats.Days.Count.ShouldBe(7);
        stats.Days.ShouldAllBe(day => day.Count == 0);
    }

    [Fact]
    public async Task A_range_the_meter_does_not_know_is_refused()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(HttpMethod.Get, "/api/stats/laziness?range=month");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
