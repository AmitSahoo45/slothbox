using System.Net;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;
using SlothBox.Tests.Reading;

namespace SlothBox.Tests.Api.Dashboard;

[Collection(PostgresCollection.Name)]
public sealed class FileEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task File_details_carry_the_receipt_fields_and_the_history()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var receipt = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);

        var details = await h.GetAsync<FileDetailsDto>($"/api/files/{receipt.Id}");

        (details.File.FileId, details.File.Status).ShouldBe((receipt.Id, FileStatus.Filed));
        (details.OriginalName, details.OriginalPath, details.CurrentPath, details.Caption, details.CategoryId,
                details.ReviewReason)
            .ShouldBe(("IMG_2041.jpg", receipt.OriginalPath, receipt.CurrentPath, "UPI payment of 599 INR to Jio",
                (int?)3, (ReviewReason?)null));
        details.SizeBytes.ShouldBeGreaterThan(0);
        details.Receipt.ShouldBe(new ReceiptDto("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi));
        var move = details.History.ShouldHaveSingleItem();
        (move.Kind, move.FromPath, move.ToPath, move.Status, move.Error)
            .ShouldBe((ActionKind.Move, receipt.OriginalPath, receipt.CurrentPath, ActionStatus.Done, (string?)null));
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/thumb")]
    [InlineData("GET", "/preview")]
    [InlineData("POST", "/open")]
    [InlineData("POST", "/reveal")]
    public async Task An_unknown_file_is_not_found(string method, string action)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendAsync(new HttpMethod(method), $"/api/files/{Guid.NewGuid()}{action}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_thumbnail_is_a_JPEG_at_most_256_pixels_long_and_is_kept_in_the_cache()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var photo = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt, width: 800, height: 600);

        using var response = await h.SendAsync(HttpMethod.Get, $"/api/files/{photo.Id}/thumb");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        TestFiles.Inspect(await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe((SKEncodedImageFormat.Jpeg, 256, 192));
        File.Exists(h.Pipeline.Output("_cache", "thumbs", $"{photo.Id:N}.jpg")).ShouldBeTrue();
        using var again = await h.SendAsync(HttpMethod.Get, $"/api/files/{photo.Id}/thumb");
        TestFiles.Inspect(await again.Content.ReadAsByteArrayAsync(Ct)).ShouldBe((SKEncodedImageFormat.Jpeg, 256, 192));
    }

    [Fact]
    public async Task A_preview_is_the_image_at_reading_size()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var photo = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt, width: 800, height: 600);

        using var response = await h.SendAsync(HttpMethod.Get, $"/api/files/{photo.Id}/preview");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TestFiles.Inspect(await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe((SKEncodedImageFormat.Jpeg, 800, 600));
    }

    [Theory]
    [InlineData("thumb")]
    [InlineData("preview")]
    public async Task A_document_has_no_image_to_show(string image)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var notes = await h.FileTextAsync("notes.txt", "Some notes about .NET 10.", Analyses.Notes);

        using var response = await h.SendAsync(HttpMethod.Get, $"/api/files/{notes.Id}/{image}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Open_and_show_in_folder_hand_the_files_path_to_Windows()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var photo = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);

        using var open = await h.SendAsync(HttpMethod.Post, $"/api/files/{photo.Id}/open");
        using var reveal = await h.SendAsync(HttpMethod.Post, $"/api/files/{photo.Id}/reveal");

        (await open.ReadAsync<MutationResultDto>()).ShouldBe(new MutationResultDto(true, null));
        (await reveal.ReadAsync<MutationResultDto>()).ShouldBe(new MutationResultDto(true, null));
        h.Launcher.Calls.ShouldBe([("open", photo.CurrentPath), ("reveal", photo.CurrentPath)]);
    }

    [Fact]
    public async Task A_file_that_is_no_longer_there_is_not_opened()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var photo = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        File.Delete(photo.CurrentPath);

        using var response = await h.SendAsync(HttpMethod.Post, $"/api/files/{photo.Id}/open");

        (await response.ReadAsync<MutationResultDto>())
            .ShouldBe(new MutationResultDto(false, "The file is no longer where SlothBox put it."));
        h.Launcher.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_outside_SlothBoxs_folders_is_never_served_or_opened()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var photo = await h.FileAsync("IMG_2041.jpg", Analyses.JioReceipt);
        var outside = h.Moves.Temp.PathOf("elsewhere", "IMG_2041.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.Copy(photo.CurrentPath, outside);
        await using (var db = h.Moves.Db.CreateDbContext())
            await db.Files.Where(f => f.Id == photo.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(f => f.CurrentPath, outside), Ct);

        using var thumb = await h.SendAsync(HttpMethod.Get, $"/api/files/{photo.Id}/thumb");
        using var open = await h.SendAsync(HttpMethod.Post, $"/api/files/{photo.Id}/open");

        thumb.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await open.ReadAsync<MutationResultDto>()).Success.ShouldBeFalse();
        h.Launcher.Calls.ShouldBeEmpty();
    }
}
