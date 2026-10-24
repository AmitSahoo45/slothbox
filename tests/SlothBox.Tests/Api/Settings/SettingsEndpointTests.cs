using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Settings;

namespace SlothBox.Tests.Api.Settings;

// The Settings page (spec 7.4.8, 8.2): start at login, the laziness seconds and the Ollama address.
public sealed class SettingsEndpointTests
{
    private const string SecondsRange = "Use 0 to 3600 seconds per file.";
    private const string AddressOnly = "Use the Ollama address only, like http://127.0.0.1:11436.";

    private static readonly LazinessSettings Estimates = new(40, 20, 10, 0);

    private static async Task<HttpResponseMessage> PutAsync(ApiServer server, UpdateSettingsRequest request) =>
        await server.SendAsync(HttpMethod.Put, "/api/settings", request);

    [Fact]
    public async Task Settings_start_with_the_defaults()
    {
        await using var server = new ApiServer();

        (await server.GetAsync<SettingsDto>("/api/settings"))
            .ShouldBe(new SettingsDto(false, new LazinessSettings(), "http://127.0.0.1:11436"));
    }

    [Fact]
    public async Task Saved_settings_apply_at_once_and_start_at_login_follows_the_run_key()
    {
        var autoStart = new FakeAutoStart();
        await using var server = new ApiServer(services => services.AddSingleton<IAutoStart>(autoStart));

        using var response = await PutAsync(server, new UpdateSettingsRequest(true, Estimates, " http://127.0.0.1:11500/ "));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = new SettingsDto(true, Estimates, "http://127.0.0.1:11500");
        (await response.ReadAsync<SettingsDto>()).ShouldBe(expected);
        autoStart.IsEnabled.ShouldBeTrue();
        (server.Settings.Current.Laziness, server.Settings.Current.Ollama.Url)
            .ShouldBe((Estimates, "http://127.0.0.1:11500"));
        (await server.GetAsync<SettingsDto>("/api/settings")).ShouldBe(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public async Task Seconds_outside_0_to_3600_are_refused_and_nothing_is_saved(int seconds)
    {
        await using var server = new ApiServer();

        using var response = await PutAsync(server, new UpdateSettingsRequest(false,
            Estimates with { DocumentSeconds = seconds }, "http://127.0.0.1:11436"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(SecondsRange);
        server.Settings.Current.Laziness.ShouldBe(new LazinessSettings());
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:11436")]
    [InlineData("ftp://127.0.0.1:11436")]
    [InlineData("http://127.0.0.1:11436/ollama")]
    [InlineData("http://127.0.0.1:11436?model=x")]
    [InlineData("http://user:secret@127.0.0.1:11436")]
    public async Task An_ollama_address_that_is_not_a_plain_web_address_is_refused(string url)
    {
        await using var server = new ApiServer();

        using var response = await PutAsync(server, new UpdateSettingsRequest(false, Estimates, url));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(AddressOnly);
        server.Settings.Current.ShouldBe(new SlothBoxSettings
            { Phone = new PhoneSettings(Token: "synthetic-phone-token") });
    }

    [Fact]
    public async Task A_body_without_the_seconds_or_the_address_is_refused()
    {
        await using var server = new ApiServer();

        using var noSeconds = await server.SendJsonAsync(HttpMethod.Put, "/api/settings",
            """{"startAtLogin":false,"ollamaUrl":"http://127.0.0.1:11436"}""");
        using var noAddress = await server.SendJsonAsync(HttpMethod.Put, "/api/settings",
            """{"startAtLogin":false,"laziness":{"receiptSeconds":30,"screenshotSeconds":15,"documentSeconds":15,"otherSeconds":5}}""");

        (noSeconds.StatusCode, noAddress.StatusCode).ShouldBe((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest));
        (await noSeconds.ReadAsync<ProblemDetails>()).Title.ShouldBe(SecondsRange);
        (await noAddress.ReadAsync<ProblemDetails>()).Title.ShouldBe(AddressOnly);
    }

    [Fact]
    public async Task Without_the_windows_app_start_at_login_stays_off()
    {
        await using var server = new ApiServer();

        using var on = await PutAsync(server, new UpdateSettingsRequest(true, Estimates, "http://127.0.0.1:11436"));
        on.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await on.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "Start at login is set by the SlothBox app on Windows, which is not running.");
        server.Settings.Current.Laziness.ShouldBe(new LazinessSettings());

        using var off = await PutAsync(server, new UpdateSettingsRequest(false, Estimates, "http://127.0.0.1:11436"));
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.Settings.Current.Laziness.ShouldBe(Estimates);
    }

    [Fact]
    public async Task A_run_key_that_cannot_be_changed_is_reported_and_nothing_is_saved()
    {
        var autoStart = new FakeAutoStart { Failure = new UnauthorizedAccessException("Access to the key is denied.") };
        await using var server = new ApiServer(services => services.AddSingleton<IAutoStart>(autoStart));

        using var response = await PutAsync(server, new UpdateSettingsRequest(true, Estimates, "http://127.0.0.1:11436"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "Windows did not let SlothBox change start at login: Access to the key is denied.");
        server.Settings.Current.Laziness.ShouldBe(new LazinessSettings());
    }

    // Check again uses the saved address (spec 7.4.8): Ollama is asked at the new address at once, no restart.
    [Fact]
    public async Task The_status_check_asks_ollama_at_the_saved_address()
    {
        var (first, second) = (ClosedPort(), ClosedPort());
        await using var server = new ApiServer(services => services.AddSingleton<IOllamaStatus, LiveOllamaStatus>());

        foreach (var port in new[] { first, second })
        {
            using var saved = await PutAsync(server,
                new UpdateSettingsRequest(false, Estimates, $"http://127.0.0.1:{port}"));
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await server.GetAsync<StatusDto>("/api/status")).Gemma
                .ShouldBe(new ServiceStatusDto(false, $"Ollama is not answering at http://127.0.0.1:{port}."));
        }
    }

    // A loopback port nothing listens on, so the call fails at once without leaving the machine.
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class OllamaFollowerTests
{
    [Fact]
    public void A_client_is_built_once_and_again_only_when_the_ollama_settings_change()
    {
        var settings = new FixedSettings(new SlothBoxSettings());
        var built = new List<string>();
        var follower = new OllamaFollower<string>(settings, current =>
        {
            built.Add(current.Ollama.Url);
            return $"client for {current.Ollama.Url}";
        });

        follower.Current.ShouldBe("client for http://127.0.0.1:11436");
        follower.Current.ShouldBe("client for http://127.0.0.1:11436");
        settings.Update(current => current with { Laziness = new LazinessSettings(1, 1, 1, 1) });
        follower.Current.ShouldBe("client for http://127.0.0.1:11436");
        settings.Update(current => current with { Ollama = current.Ollama with { Url = "http://127.0.0.1:11500" } });
        follower.Current.ShouldBe("client for http://127.0.0.1:11500");

        built.ShouldBe(["http://127.0.0.1:11436", "http://127.0.0.1:11500"]);
    }
}
