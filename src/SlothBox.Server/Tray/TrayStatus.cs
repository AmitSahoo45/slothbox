using SlothBox.Server.Api;

namespace SlothBox.Server.Tray;

// Spec 7.8 and 9: the tray's status line, from the status the dashboard shows. Whatever stops the filing comes first.
public static class TrayStatus
{
    public static string Line(StatusDto status)
    {
        if (!status.Database.Ready)
            return "Database offline";
        if (!status.SetupComplete)
            return "Waiting for setup";
        var waiting = status.Queue.Waiting == 1 ? "1 file waiting" : $"{status.Queue.Waiting} files waiting";
        if (!status.Gemma.Ready || status.PauseReason == PauseReason.GemmaOffline)
            return $"Gemma is offline, {waiting}";
        if (status.PauseReason == PauseReason.DriveFull)
            return $"Paused: the output drive is full, {waiting}";
        return status.Paused ? $"Paused, {waiting}" : $"Running, {waiting}";
    }
}
