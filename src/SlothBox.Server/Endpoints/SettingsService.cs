using System.Security;
using SlothBox.Core;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// The Settings page (spec 7.4.8): start at login, the laziness seconds and the Ollama address; the theme stays in the
// browser. autoStart is the App's Run key; without the App, start at login stays off.
public sealed class SettingsService(ISettingsStore settings, IAutoStart? autoStart = null)
{
    // An hour per file is already generous; the meter sums the seconds as whole numbers (spec 6.8).
    private const int MaxSeconds = 3600;

    public SettingsDto Get()
    {
        var current = settings.Current;
        return new SettingsDto(autoStart?.IsEnabled ?? false, current.Laziness, current.Ollama.Url);
    }

    // Everything is checked before anything is saved, so a refused change leaves the settings as they were.
    public Outcome<SettingsDto> Update(UpdateSettingsRequest request)
    {
        if (request.Laziness is not { } laziness ||
            new[] { laziness.ReceiptSeconds, laziness.ScreenshotSeconds, laziness.DocumentSeconds, laziness.OtherSeconds }
                .Any(seconds => seconds is < 0 or > MaxSeconds))
            return new Outcome<SettingsDto>.Refused($"Use 0 to {MaxSeconds} seconds per file.");
        if (OllamaAddress(request.OllamaUrl) is not { } url)
            return new Outcome<SettingsDto>.Refused("Use the Ollama address only, like http://127.0.0.1:11436.");
        if (request.StartAtLogin != (autoStart?.IsEnabled ?? false))
        {
            if (autoStart is null)
                return new Outcome<SettingsDto>.Conflict(
                    "Start at login is set by the SlothBox app on Windows, which is not running.");
            try
            {
                autoStart.SetEnabled(request.StartAtLogin);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
            {
                return new Outcome<SettingsDto>.Conflict($"Windows did not let SlothBox change start at login: {e.Message}");
            }
        }
        // The Ollama calls follow the saved address from their next call (LiveOllama).
        settings.Update(current => current with { Laziness = laziness, Ollama = current.Ollama with { Url = url } });
        return new Outcome<SettingsDto>.Done(Get());
    }

    // Scheme, host and port only: the Ollama calls add their own paths. Saved without a trailing slash, as the default.
    private static string? OllamaAddress(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
