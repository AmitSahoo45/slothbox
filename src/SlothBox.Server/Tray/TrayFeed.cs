using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Events;

namespace SlothBox.Server.Tray;

// Spec 7.8: keeps the tray's status line current and shows a notice for each file that goes to Needs review
// (spec 5.1 step 6). The App's tray is the notifier; without one (--headless, or Server on its own) there is nothing
// to show.
public sealed class TrayFeed(ServerEvents events, StatusService status, ILogger<TrayFeed> log,
    ITrayNotifier? tray = null, TimeSpan? refresh = null) : BackgroundService
{
    // A database or Gemma that stops answering publishes no event, so the line is also checked now and then.
    private readonly TimeSpan _refresh = refresh ?? TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (tray is null)
            return;
        using var subscription = events.Subscribe();
        while (!stoppingToken.IsCancellationRequested)
        {
            await ShowStatusAsync(tray, stoppingToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(_refresh);
            try
            {
                if (!await subscription.Events.WaitToReadAsync(wait.Token))
                    return;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                continue;
            }
            // Events that arrived together get one new status line.
            while (subscription.Events.TryRead(out var published))
                if (published is { Name: "file.review", Payload: FileReviewEvent review })
                    Show(() => tray.ShowReviewNotice(review.FileId, review.NewName));
        }
    }

    private async Task ShowStatusAsync(ITrayNotifier notifier, CancellationToken ct)
    {
        string line;
        try
        {
            line = TrayStatus.Line(await status.ReadAsync(ct));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("The tray's status could not be read: {Error}", e.Message);
            return;
        }
        Show(() => notifier.SetStatus(line));
    }

    // Nothing here may stop SlothBox: a background service that fails stops the host.
    private void Show(Action call)
    {
        try
        {
            call();
        }
        catch (Exception e)
        {
            log.LogWarning("The tray could not be updated: {Error}", e.Message);
        }
    }
}
