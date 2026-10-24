using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Api;

public sealed class EventsEndpointTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServerEvents Events => _factory.Services.GetRequiredService<ServerEvents>();

    private sealed class Stream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response => response;

        // The next event as (name, data), skipping comments such as ": ready".
        public async Task<(string Name, JsonElement Data)> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string? name = null, data = null;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line["event: ".Length..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    data = line["data: ".Length..];
                else if (line.Length == 0 && name is not null && data is not null)
                    return (name, JsonDocument.Parse(data).RootElement.Clone());
            }
            throw new EndOfStreamException("The event stream ended.");
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }

    private async Task<Stream> ConnectAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080")
        });
        var response = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        // The server writes ": ready" once it listens, so events published after it reach this page.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe(": ready");
        return new Stream(response, reader);
    }

    private static FileFiledEvent Filed(Guid id) => new()
    {
        FileId = id, At = DateTimeOffset.UtcNow, Status = FileStatus.Filed, Type = FileKind.Receipt,
        Category = "Bills and recharges", Title = "Jio recharge", NewName = "2026-10-20_Jio_599INR.jpg",
        Folder = @"D:\SlothBox\Receipts\Bills and recharges", Origin = FileOrigin.Phone, FolderLabel = null,
        ThumbUrl = $"/api/files/{id}/thumb", CanUndo = true
    };

    private Task PublishWhenConnectedAsync(params ServerEvent[] published)
    {
        foreach (var serverEvent in published)
            Events.Publish(serverEvent);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task File_and_queue_events_reach_the_browser_as_they_happen()
    {
        using var stream = await ConnectAsync();
        var id = Guid.NewGuid();

        await PublishWhenConnectedAsync(new ServerEvent("file.filed", Filed(id)),
            new ServerEvent("queue.changed", new QueueChangedEvent(3, 1)));

        var (name, data) = await stream.NextAsync();
        (name, data.GetProperty("fileId").GetGuid(), data.GetProperty("status").GetString(),
                data.GetProperty("origin").GetString())
            .ShouldBe(("file.filed", id, "filed", "phone"));
        var (queue, counts) = await stream.NextAsync();
        (queue, counts.GetProperty("waiting").GetInt32(), counts.GetProperty("processing").GetInt32())
            .ShouldBe(("queue.changed", 3, 1));
    }

    [Fact]
    public async Task A_pause_by_the_worker_reaches_the_browser_as_status_changed_with_its_reason()
    {
        using var stream = await ConnectAsync();
        var worker = _factory.Services.GetRequiredService<WorkerState>();
        worker.Pause(PauseReason.DriveFull);

        await PublishWhenConnectedAsync(new ServerEvent("worker.changed",
            new WorkerChanged(true, PauseReason.DriveFull)));

        var (name, status) = await stream.NextAsync();
        name.ShouldBe("status.changed");
        (status.GetProperty("paused").GetBoolean(), status.GetProperty("pauseReason").GetString())
            .ShouldBe((true, "drive_full"));
        status.GetProperty("gemma").GetProperty("ready").GetBoolean().ShouldBeTrue();
        worker.Resume();
    }

    [Fact]
    public async Task Telling_pages_about_a_pause_for_Gemma_does_not_lift_it()
    {
        using var stream = await ConnectAsync();
        var worker = _factory.Services.GetRequiredService<WorkerState>();
        worker.Pause(PauseReason.GemmaOffline);

        Events.Publish(new ServerEvent("worker.changed", new WorkerChanged(true, PauseReason.GemmaOffline)));

        var (name, status) = await stream.NextAsync();
        (name, status.GetProperty("paused").GetBoolean(), status.GetProperty("pauseReason").GetString())
            .ShouldBe(("status.changed", true, "gemma_offline"));
        worker.Reason.ShouldBe(PauseReason.GemmaOffline);
        worker.Resume();
    }

    [Fact]
    public async Task The_workers_own_event_never_reaches_the_browser_as_it_is()
    {
        using var stream = await ConnectAsync();

        await PublishWhenConnectedAsync(new ServerEvent("worker.changed", new WorkerChanged(false, null)),
            new ServerEvent("queue.changed", new QueueChangedEvent(0, 0)));

        (await stream.NextAsync()).Name.ShouldBe("status.changed");
        (await stream.NextAsync()).Name.ShouldBe("queue.changed");
    }

    public void Dispose() => _factory.Dispose();
}
