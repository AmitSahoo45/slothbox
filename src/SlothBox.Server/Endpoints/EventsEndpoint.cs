using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using SlothBox.Server.Events;

namespace SlothBox.Server.Endpoints;

// GET /events (spec 8.3): Server-Sent Events for the open dashboard pages.
public static class EventsEndpoint
{
    // A comment now and then keeps the connection open and notices a closed tab.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

    // The spec 8.3 events that pass through as published. The worker's own worker.changed goes out as the full
    // status.changed instead, so a page always learns why the worker paused (pauseReason).
    private static readonly HashSet<string> Forwarded =
    [
        "file.filed", "file.review", "file.duplicate", "file.undone", "file.refiled", "queue.changed",
        "folder.tidy-progress"
    ];

    public static void Map(WebApplication app) =>
        app.MapGet("/events", StreamAsync)
            .WithName("GetEvents").WithTags("Events").Produces<string>(200, "text/event-stream");

    private static async Task StreamAsync(HttpContext context, ServerEvents events, StatusService status,
        IOptions<JsonOptions> json)
    {
        var ct = context.RequestAborted;
        var response = context.Response;
        var options = json.Value.SerializerOptions;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        using var subscription = events.Subscribe();
        await response.WriteAsync(": ready\n\n", ct);
        await response.Body.FlushAsync(ct);
        try
        {
            while (true)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                bool open;
                try
                {
                    open = await subscription.Events.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.WriteAsync(": ping\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    continue;
                }
                if (!open)
                    return;
                while (subscription.Events.TryRead(out var published))
                {
                    if (published.Name == "worker.changed")
                        await WriteAsync(response, "status.changed",
                            StatusService.ToEvent(await status.ReadAsync(ct)), options, ct);
                    else if (Forwarded.Contains(published.Name))
                        await WriteAsync(response, published.Name, published.Payload, options, ct);
                }
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static Task WriteAsync(HttpResponse response, string name, object payload, JsonSerializerOptions options,
        CancellationToken ct) =>
        response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, payload.GetType(), options)}\n\n",
            ct);
}
