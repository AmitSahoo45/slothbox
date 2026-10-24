using System.Globalization;
using SlothBox.Core;
using SlothBox.Core.Refiling;
using SlothBox.Data.Dashboard;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Server.Worker;

namespace SlothBox.Server.Endpoints;

// The dashboard API of spec 8.2 that returns real data (E11). Routes not built yet stay in StubEndpoints.
public static class DashboardEndpoints
{
    private const int PageSize = 50;
    private const int MaxPageSize = 200;

    // Mapped in the order of spec 8.2, before the stubs, so the contract keeps its order. Problems a route can now
    // answer are added after the ones it already documented.
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Dashboard");
        StubEndpoints.Document<StatusDto>(api.MapGet("/status",
            (StatusService status, CancellationToken ct) => status.GetAsync(ct)), "GetStatus");
        StubEndpoints.Document<StatusDto>(api.MapPost("/pause", (WorkerState worker, StatusService status,
            CancellationToken ct) =>
        {
            worker.Pause(PauseReason.User);
            return status.GetAsync(ct);
        }), "Pause");
        StubEndpoints.Document<StatusDto>(api.MapPost("/resume", async (WorkerState worker, StatusService status,
            CancellationToken ct) =>
        {
            worker.Resume();
            var current = await status.GetAsync(ct);
            if (current.Gemma.Ready && current.VisionModel.Present && current.EmbeddingModel.Present)
                return current;
            // Without Gemma and both models every waiting file would go to Needs review (spec 9): stay paused.
            worker.Pause(PauseReason.GemmaOffline);
            return current with { Paused = true, PauseReason = PauseReason.GemmaOffline };
        }), "Resume");
        StubEndpoints.Document<ActivityPageDto>(api.MapGet("/activity", GetActivityAsync), "GetActivity")
            .ProducesProblem(400);
        StubEndpoints.Document<LazinessStatsDto>(api.MapGet("/stats/laziness", GetLazinessAsync), "GetLazinessStats")
            .ProducesProblem(400);
        StubEndpoints.Document<FileDetailsDto>(api.MapGet("/files/{id:guid}", GetFileAsync), "GetFile")
            .ProducesProblem(404);
        StubEndpoints.Binary(api.MapGet("/files/{id:guid}/thumb", (Guid id, DashboardQueries queries,
                FileAccessGuard guard, FileImages images, CancellationToken ct) =>
            ImageAsync(id, queries, guard, file => images.ThumbnailAsync(file, ct), ct)), "GetThumbnail", "image/jpeg")
            .ProducesProblem(404);
        StubEndpoints.Binary(api.MapGet("/files/{id:guid}/preview", (Guid id, DashboardQueries queries,
                FileAccessGuard guard, FileImages images, CancellationToken ct) =>
            ImageAsync(id, queries, guard, file => images.PreviewAsync(file, ct), ct)), "GetPreview", "image/jpeg")
            .ProducesProblem(404);
        StubEndpoints.Document<MutationResultDto>(api.MapPost("/files/{id:guid}/open", (Guid id,
                DashboardQueries queries, FileAccessGuard guard, IFileLauncher launcher, CancellationToken ct) =>
            LaunchAsync(id, queries, guard, launcher.Open, ct)), "OpenFile").ProducesProblem(404);
        StubEndpoints.Document<MutationResultDto>(api.MapPost("/files/{id:guid}/reveal", (Guid id,
                DashboardQueries queries, FileAccessGuard guard, IFileLauncher launcher, CancellationToken ct) =>
            LaunchAsync(id, queries, guard, launcher.Reveal, ct)), "RevealFile").ProducesProblem(404);
        StubEndpoints.Document<UndoResultDto>(api.MapPost("/files/{id:guid}/undo", async (Guid id, IUndoService undo,
            ServerEvents events, CancellationToken ct) => Undone(await undo.UndoFileAsync(id, ct), events)), "UndoFile");
        StubEndpoints.Document<FileDetailsDto>(api.MapPost("/files/{id:guid}/refile", RefileAsync), "Refile")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        StubEndpoints.Document<UndoResultDto>(api.MapPost("/undo/last", async (IUndoService undo, ServerEvents events,
            CancellationToken ct) => Undone(await undo.UndoLastAsync(ct), events)), "UndoLast");
        StubEndpoints.Document<UndoBatchResultDto>(api.MapPost("/undo/since", async (UndoSinceRequest request,
            IUndoService undo, ServerEvents events, CancellationToken ct) =>
        {
            // A body without a time binds as 0001-01-01, which would undo every action ever.
            if (request.Since == default)
                return Results.Problem(statusCode: 400, title: "Choose the time to undo from.");
            var results = (await undo.UndoSinceAsync(request.Since, ct)).Select(result => Undone(result, events))
                .ToList();
            return Results.Ok(new UndoBatchResultDto(results.Count(result => result.Success), results));
        }), "UndoSince").ProducesProblem(400);
        // The count for the "Undo 12 actions since 10:30?" dialog (spec 7.4.2).
        api.MapGet("/undo/since", async (DateTimeOffset since, IUndoService undo, CancellationToken ct) =>
                new UndoSincePreviewDto(await undo.CountSinceAsync(since, ct)))
            .WithName("PreviewUndoSince").Produces<UndoSincePreviewDto>().ProducesProblem(400);
        StubEndpoints.Document<IReadOnlyList<ReviewItemDto>>(api.MapGet("/review", GetReviewAsync), "GetReview");
        StubEndpoints.Document<MutationResultDto>(api.MapPost("/review/{id:guid}/skip", SkipReviewAsync), "SkipReview")
            .ProducesProblem(404);
    }

    // Newest first, 50 a page (spec 7.4.2). The cursor is the last action id of the previous page.
    private static async Task<IResult> GetActivityAsync(string? cursor, int? limit, DashboardQueries queries,
        CancellationToken ct)
    {
        long? before = null;
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return Results.Problem(statusCode: 400, title: "This page of the activity feed is not available.");
            before = id;
        }
        var size = Math.Clamp(limit ?? PageSize, 1, MaxPageSize);
        var views = await queries.ActivityAsync(before, size + 1, ct);
        var page = views.Take(size).ToList();
        var next = views.Count > size ? page[^1].LastActionId.ToString(CultureInfo.InvariantCulture) : null;
        return Results.Ok(new ActivityPageDto(page.ConvertAll(DashboardViews.Activity), next));
    }

    // Today, this week (Monday to Sunday) or all time; this week by default, as on the Today page (spec 7.4.2).
    private static async Task<IResult> GetLazinessAsync(string? range, LazinessMeter meter, CancellationToken ct)
    {
        var chosen = (range ?? "week").ToLowerInvariant();
        return LazinessMeter.Ranges.Contains(chosen)
            ? Results.Ok(await meter.GetAsync(chosen, ct))
            : Results.Problem(statusCode: 400, title: "Use today, week or all.");
    }

    private static async Task<IResult> GetFileAsync(Guid id, DashboardQueries queries, CancellationToken ct) =>
        await queries.FileAsync(id, ct) is { } view
            ? Results.Ok((await DashboardViews.DetailsAsync([view], queries, ct))[0])
            : UnknownFile();

    // A thumbnail or preview, only of an image inside SlothBox's folders (spec 8.4 rule 4).
    private static async Task<IResult> ImageAsync(Guid id, DashboardQueries queries, FileAccessGuard guard,
        Func<FileRecord, Task<byte[]?>> render, CancellationToken ct)
    {
        if (await queries.FileAsync(id, ct) is not { } view)
            return UnknownFile();
        var file = view.File;
        try
        {
            if (await guard.AllowsAsync(file.CurrentPath, ct) && File.Exists(file.CurrentPath) &&
                await render(file) is { } jpeg)
                return Results.File(jpeg, "image/jpeg");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return Results.Problem(statusCode: 404, title: "There is no image to show for this file.");
    }

    // Open and Show in folder (spec 7.4.9), only for a file still inside SlothBox's folders.
    private static async Task<IResult> LaunchAsync(Guid id, DashboardQueries queries, FileAccessGuard guard,
        Action<string> launch, CancellationToken ct)
    {
        if (await queries.FileAsync(id, ct) is not { } view)
            return UnknownFile();
        var path = view.File.CurrentPath;
        if (!await guard.AllowsAsync(path, ct))
            return Results.Ok(new MutationResultDto(false,
                "SlothBox opens files only in its own folders and the folders it watches."));
        if (!File.Exists(path))
            return Results.Ok(new MutationResultDto(false, "The file is no longer where SlothBox put it."));
        try
        {
            launch(path);
            return Results.Ok(new MutationResultDto(true, null));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            return Results.Ok(new MutationResultDto(false, $"Windows could not open the file: {e.Message}"));
        }
    }

    // The whole queue in three queries, however long it is: the badge on every page reads it (spec 7.4.3).
    private static async Task<IResult> GetReviewAsync(DashboardQueries queries, CancellationToken ct)
    {
        var views = await queries.ReviewAsync(ct);
        var details = await DashboardViews.DetailsAsync(views, queries, ct);
        return Results.Ok(views.Select((view, index) =>
            new ReviewItemDto(details[index], view.File.ReviewReason ?? ReviewReason.BadOutput)).ToList());
    }

    // "Skip for now": the item moves to the end of the queue and stays in Needs review (spec 7.4.3).
    private static async Task<IResult> SkipReviewAsync(Guid id, DashboardQueries queries, CancellationToken ct)
    {
        if (await queries.SkipReviewAsync(id, ct))
            return Results.Ok(new MutationResultDto(true, null));
        return await queries.FileAsync(id, ct) is null
            ? UnknownFile()
            : Results.Ok(new MutationResultDto(false, "This file is not in Needs review."));
    }

    // "File it" and the drawer's Change (spec 7.4.3, 7.4.9): an undoable move to the chosen type and category.
    private static async Task<IResult> RefileAsync(Guid id, RefileRequest request, DashboardQueries queries,
        IFileRepository files, Refiler refiler, ServerEvents events, CancellationToken ct)
    {
        // The refile needs the whole row (its stored text is part of the new vector), not the dashboard's view.
        if (await queries.FileAsync(id, ct) is null || await files.GetAsync(id, ct) is not { } file)
            return UnknownFile();
        var receipt = request.Receipt is { } fields
            ? new ReceiptData(fields.Vendor, fields.Date, fields.Amount, fields.Currency, fields.PaymentMethod)
            : null;
        switch (await refiler.RefileAsync(file,
                    new RefileChange(request.Type, request.CategoryId, request.Title, receipt), ct))
        {
            case RefileResult.Invalid invalid:
                return Results.Problem(statusCode: 400, title: invalid.Message);
            case RefileResult.Refused refused:
                return Results.Problem(statusCode: 409, title: refused.Message);
        }
        var details = (await DashboardViews.DetailsAsync([(await queries.FileAsync(id, ct))!], queries, ct))[0];
        events.Publish(new ServerEvent("file.refiled", DashboardViews.Refiled(details.File)));
        return Results.Ok(details);
    }

    // Each file put back is announced, so open pages refresh it (spec 8.3 file.undone).
    private static UndoResultDto Undone(UndoResult result, ServerEvents events)
    {
        if (result.Succeeded)
            events.Publish(new ServerEvent("file.undone", new FileUndoneEvent(result.FileId)));
        return new UndoResultDto(result.FileId, result.Succeeded, result.RestoredPath, result.Error);
    }

    private static IResult UnknownFile() => Results.Problem(statusCode: 404, title: "SlothBox has no file with this id.");
}
