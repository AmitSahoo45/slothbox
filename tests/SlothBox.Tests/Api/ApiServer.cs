using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Data;

namespace SlothBox.Tests.Api;

// The server with no database, settings file, Ollama or programs on this machine, plus the services a test adds or
// replaces. Anything reaching for the database fails, as it would while Postgres is down.
internal sealed class ApiServer(Action<IServiceCollection>? services = null) : WebApplicationFactory<Server.Program>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FixedSettings Settings { get; } =
        new(new SlothBoxSettings { Phone = new PhoneSettings(Token: "synthetic-phone-token") });

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing").ConfigureTestServices(collection =>
        {
            collection.AddSingleton<ISettingsStore>(Settings);
            collection.AddSingleton<IOllamaStatus, FixedGemma>();
            collection.AddSingleton<IFileLauncher, RecordingLauncher>();
            collection.AddSingleton<IDbContextFactory<SlothBoxDbContext>, NoDatabase>();
            services?.Invoke(collection);
        });

    public HttpClient Client()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }

    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<T>();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
        return await client.SendAsync(request, Ct);
    }

    public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string json)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(request, Ct);
    }
}
