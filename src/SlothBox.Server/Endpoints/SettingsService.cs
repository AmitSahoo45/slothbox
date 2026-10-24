using System.Security;
using SlothBox.Core;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// The Settings page (spec 7.4.8): start at login, the laziness seconds and the Ollama address; the theme stays in the
// browser. autoStart is the App's Run key; without the App, start at login stays off.
public sealed class SettingsService(ISettingsStore settings, IAutoStart? autoStart = null)
{
    public SettingsDto Get()
    {
        var current = settings.Current;
        return new SettingsDto(autoStart?.IsEnabled ?? false, current.Laziness, current.Ollama.Url);
    }

    // Everything is checked before anything is saved, so a refused change leaves the settings as they were.
    public Outcome<SettingsDto> Update(UpdateSettingsRequest request)
    {
        // Any whole number of seconds from 0 up, as the contract's int32 allows (spec 7.4.8).
        if (request.Laziness is not { } laziness ||
            new[] { laziness.ReceiptSeconds, laziness.ScreenshotSeconds, laziness.DocumentSeconds, laziness.OtherSeconds }
                .Any(seconds => seconds < 0))
            return new Outcome<SettingsDto>.Refused("Use whole seconds from 0 up.");
        if (OllamaAddress(request.OllamaUrl) is not { } url)
            return new Outcome<SettingsDto>.Refused("Use an http or https address, like http://127.0.0.1:11436.");
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

    // The Ollama calls add their own paths (api/chat) to it, so a query, a fragment or a user name could not reach
    // Ollama. A bare host is saved without a trailing slash, as the default; one with a path (behind a proxy) keeps a
    // trailing slash, or the calls would replace its last segment.
    private static string? OllamaAddress(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        if (uri.AbsolutePath == "/")
            return uri.GetLeftPart(UriPartial.Authority);
        var path = uri.GetLeftPart(UriPartial.Path);
        return path.EndsWith('/') ? path : path + "/";
    }
}
